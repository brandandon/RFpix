// HamRadioDeluxe.cs - shares the radio with Ham Radio Deluxe (HRD), the way WSJT-X does.
//
// When HRD's Rig Control has the radio, no other program can open its COM port. HRD lets
// other programs in through a network link instead (port 7809 on the same computer). RFpix
// sends it text commands and reads its answers:
//   get radios / get radio      which radio HRD is controlling ("1:IC-9700")
//   get frequency               the frequency in Hz
//   get dropdown-text {Mode}    the mode, as HRD's Mode list shows it ("Mode: USB")
//   set button-select TX 1      press HRD's TX button (0 to release it)
//   set button-select Data 1    an Icom's DATA setting on (0 off); other radios have other names
// Each command after the first few starts with the radio's number in brackets ("[1] get
// frequency"), so it goes to the right radio when HRD controls more than one.
//
// The link (HRD 5 and later): every message is a 16-byte header (the message's length, two fixed
// "magic" numbers and a zero, each 4 bytes, lowest byte first) and then the text in UTF-16, ending
// with a zero character. HRD's answers come back the same way. (From WSJT-X's HRDMessage.cpp.)

using System.Net.Sockets;
using System.Text;

namespace RFpix.Radio;

/// <summary>The messages on HRD's network link.</summary>
public static class HrdMessage
{
    public const int HeaderSize = 16;
    public const uint Magic1 = 0x1234ABCD, Magic2 = 0xABCD1234;
    public const int MaxSize = 1024 * 1024;

    /// <summary>A message carrying the text.</summary>
    public static byte[] Make(string text)
    {
        var payload = Encoding.Unicode.GetBytes(text + "\0");
        var message = new byte[HeaderSize + payload.Length];
        BitConverter.TryWriteBytes(message.AsSpan(0), (uint)message.Length);
        BitConverter.TryWriteBytes(message.AsSpan(4), Magic1);
        BitConverter.TryWriteBytes(message.AsSpan(8), Magic2);
        payload.CopyTo(message, HeaderSize);
        return message;
    }

    /// <summary>How long the whole message is, from its header (null until the header has arrived).
    /// Throws if it isn't an HRD message.</summary>
    public static int? Length(ReadOnlySpan<byte> received)
    {
        if (received.Length < HeaderSize) return null;
        uint size = BitConverter.ToUInt32(received);
        if (BitConverter.ToUInt32(received[4..]) != Magic1 || BitConverter.ToUInt32(received[8..]) != Magic2)
            throw new RadioException("Ham Radio Deluxe sent something RFpix doesn't understand. Is the address right (port 7809)?");
        if (size < HeaderSize + 2 || size > MaxSize || size % 2 != 0)
            throw new RadioException("Ham Radio Deluxe sent a damaged answer.");
        return (int)size;
    }

    /// <summary>The text of a whole message, up to its first zero character (HRD puts a zero after the
    /// value, sometimes followed by more).</summary>
    public static string Text(ReadOnlySpan<byte> message)
    {
        string text = Encoding.Unicode.GetString(message[HeaderSize..]);
        int nul = text.IndexOf('\0');
        return nul < 0 ? text : text[..nul];
    }
}

/// <summary>A connection to Ham Radio Deluxe's Rig Control. Not thread-safe: RadioConnection
/// lets only one command through at a time.</summary>
public sealed class HrdClient : IDisposable
{
    public const int DefaultPort = 7809;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private int _radioId;
    private string[] _buttons = Array.Empty<string>();
    private readonly Dictionary<string, string[]> _dropdowns = new(StringComparer.Ordinal);

    /// <summary>The radio HRD is controlling, as HRD names it ("IC-9700").</summary>
    public string RadioName { get; private set; } = "";

    private HrdClient(TcpClient tcp)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
        _stream.ReadTimeout = _stream.WriteTimeout = (int)Timeout.TotalMilliseconds;
    }

    /// <summary>Connects to HRD at "host:port" (port 7809 if none), and finds its radio.</summary>
    public static HrdClient Connect(string address)
    {
        var (host, port) = ParseAddress(address);
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            if (!tcp.ConnectAsync(host, port).Wait(Timeout))
                throw new RadioException($"Ham Radio Deluxe didn't answer at {host}:{port}. Is HRD Rig Control running and connected to the radio?");
        }
        catch (AggregateException ex) when (ex.InnerException is SocketException)
        {
            tcp.Dispose();
            throw new RadioException($"Couldn't reach Ham Radio Deluxe at {host}:{port}. Start HRD Rig Control and connect it to the radio first.");
        }
        catch (RadioException)
        {
            tcp.Dispose();
            throw;
        }
        var hrd = new HrdClient(tcp);
        try
        {
            hrd.Start();
            return hrd;
        }
        catch
        {
            hrd.Dispose();
            throw;
        }
    }

    public static (string Host, int Port) ParseAddress(string address)
    {
        string a = (address ?? "").Trim();
        if (a.Length == 0) return ("localhost", DefaultPort);
        int colon = a.LastIndexOf(':');
        if (colon > 0 && int.TryParse(a[(colon + 1)..], out int port) && port is > 0 and < 65536) return (a[..colon], port);
        return (a, DefaultPort);
    }

    private void Start()
    {
        Send("get context", context: false);
        // "1:IC-9700,2:FT-991A": the radios HRD knows; "get radio" names the one in use.
        var radios = Send("get radios", context: false).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        RadioName = Send("get radio", context: false).Trim();
        if (radios.Length == 0 || RadioName.Length == 0)
            throw new RadioException("Ham Radio Deluxe isn't connected to a radio. Connect HRD Rig Control to the radio first.");
        foreach (var radio in radios)
        {
            var parts = radio.Split(':', 2);
            if (parts.Length == 2 && parts[1].Trim() == RadioName && int.TryParse(parts[0], out int id)) _radioId = id;
        }
        _buttons = Send("get buttons").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                      .Select(b => b.Replace(' ', '~')).ToArray();
        foreach (var name in Send("get dropdowns").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            _dropdowns[name] = Send($"get dropdown-list {{{name}}}").Split(',').Select(s => s.Trim()).ToArray();
    }

    // ---- sending ----

    /// <summary>Sends a command and returns HRD's answer.</summary>
    public string Send(string command, bool context = true)
    {
        string text = context && _radioId > 0 ? $"[{_radioId}] {command}" : command;
        try
        {
            _stream.Write(HrdMessage.Make(text));
            var header = ReadExactly(HrdMessage.HeaderSize);
            int length = HrdMessage.Length(header)!.Value;
            var message = new byte[length];
            header.CopyTo(message, 0);
            ReadExactly(length - HrdMessage.HeaderSize).CopyTo(message, HrdMessage.HeaderSize);
            return HrdMessage.Text(message).Trim();
        }
        catch (IOException ex)
        {
            throw new RadioException($"Lost contact with Ham Radio Deluxe ({ex.Message}). Is HRD still running?");
        }
    }

    private byte[] ReadExactly(int count)
    {
        var buffer = new byte[count];
        int got = 0;
        while (got < count)
        {
            int n = _stream.Read(buffer, got, count - got);
            if (n == 0) throw new IOException("HRD closed the connection");
            got += n;
        }
        return buffer;
    }

    private void SendSimple(string command, string what)
    {
        string answer = Send(command);
        if (!answer.Equals("OK", StringComparison.OrdinalIgnoreCase))
            throw new RadioException($"Ham Radio Deluxe didn't {what} (it answered \"{answer}\").");
    }

    private string? FindButton(params string[] names) =>
        names.Select(n => _buttons.FirstOrDefault(b => b.Equals(n, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(b => b is not null);

    private string? FindDropdown(params string[] names) =>
        names.FirstOrDefault(n => _dropdowns.ContainsKey(n));

    private bool ButtonOn(string button) => Send($"get button-select {button}") == "1";

    private void PressButton(string button, bool on, string what) => SendSimple($"set button-select {button} {(on ? 1 : 0)}", what);

    /// <summary>The selection a dropdown shows ("Mode: USB" → "USB"), or null.</summary>
    private string? DropdownText(string dropdown)
    {
        string answer = Send($"get dropdown-text {{{dropdown}}}");
        int colon = answer.IndexOf(':');
        return colon < 0 ? null : answer[(colon + 1)..].Trim();
    }

    private void SetDropdown(string dropdown, string selection, string what)
    {
        int index = Array.IndexOf(_dropdowns[dropdown], selection);
        SendSimple($"set dropdown {dropdown.Replace(' ', '~')} {selection.Replace(' ', '~')} {index}", what);
    }

    // ---- frequency and mode ----

    public double ReadFrequency()
    {
        string answer = Send("get frequency");
        return ulong.TryParse(answer, out ulong hz) ? hz
            : throw new RadioException($"Ham Radio Deluxe didn't give the frequency (it answered \"{answer}\").");
    }

    public void SetFrequency(double hz) => SendSimple($"set frequency-hz {(ulong)Math.Round(hz)}", "change the frequency");

    private string? ModeDropdown => FindDropdown("Mode", "Main Mode", "Mode A");

    /// <summary>The mode as HRD shows it ("USB", "FM"...), or "" if HRD has no mode list for the radio.</summary>
    public string ReadModeText() => ModeDropdown is { } dd ? DropdownText(dd) ?? "" : "";

    // ---- transmitting ----

    private string PttButton => FindButton("TX", "TX~Data", "TX~Alt", "MOX")
        ?? throw new RadioException($"Ham Radio Deluxe has no TX button for the {RadioName}, so RFpix can't key it through HRD. Use VOX.");

    public void SetPtt(bool on) => PressButton(PttButton, on, on ? "key the transmitter" : "un-key the transmitter");

    public bool ReadPtt() => ButtonOn(PttButton);

    // ---- data mode (as WSJT-X does it: Icoms have a Data button; others DATA-ON/OFF buttons, or a Data list) ----

    private string? DataToggle => RadioName.StartsWith("IC", StringComparison.OrdinalIgnoreCase) ? FindButton("Data") : null;
    private string? DataOnButton => FindButton("DATA-ON(mid)");
    private string? DataOffButton => FindButton("DATA-OFF");
    private string? DataDropdown => FindDropdown("Data");

    private string? DataDropdownOn => DataDropdown is { } dd
        ? _dropdowns[dd].LastOrDefault(s => s is "On" or "Data1" or "D1" or "D1-FIL1") : null;

    /// <summary>Whether HRD offers a way to switch this radio's data mode.</summary>
    public bool CanSwitchDataMode =>
        DataToggle is not null || (DataOnButton is not null && DataOffButton is not null) || DataDropdownOn is not null;

    public bool DataModeOn()
    {
        if (DataToggle is { } toggle) return ButtonOn(toggle);
        if (DataOnButton is { } on && DataOffButton is not null) return ButtonOn(on);
        if (DataDropdown is { } dd) return DropdownText(dd) is { } text && text != "Off";
        return false;
    }

    public void SetDataMode(bool on)
    {
        const string What = "switch data mode";
        if (DataToggle is { } toggle) PressButton(toggle, on, What);
        else if (DataOnButton is { } onButton && DataOffButton is { } offButton) PressButton(on ? onButton : offButton, true, What);
        else if (DataDropdown is { } dd && DataDropdownOn is { } onText) SetDropdown(dd, on ? onText : "Off", What);
        else throw new RadioException($"Ham Radio Deluxe has no data mode switch for the {RadioName}.");
    }

    public void Dispose()
    {
        _stream.Dispose();
        _tcp.Dispose();
    }
}
