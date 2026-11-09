// RadioConnection.cs - talks to the radio through Hamlib (or through Ham Radio Deluxe, when HRD
// has the radio: HamRadioDeluxe.cs): frequency, mode, data mode, PTT.
//
// Every call to the radio goes over a serial cable (or a network program) and can
// take up to a few seconds if the radio doesn't answer, so the screen calls these
// from a background task. A lock lets only one command reach the radio at a time.
//
// Safety: keying is always given a time limit. If nothing un-keys the radio in time
// (a fault, a crash in the sending code), a watcher thread un-keys it. If un-keying fails, the
// watcher keeps trying every 2 seconds until it works. Closing the connection un-keys it too,
// and switches data mode back off if it was switched on for transmitting (and if switching back
// failed earlier, closing tries again).
//
// The class is in three files: this one (connecting, frequency and mode, closing),
// RadioConnection.Keying.cs (keying, un-keying and the time limit) and
// RadioConnection.DataMode.cs (switching data mode for transmitting).
//
// Data mode: before keying, RFpix switches the radio to its data mode and back after un-keying,
// leaving it alone if it was already in one. The IC-9700's DATA setting is turned on with Icom's
// own command (IcomCiv.cs), keeping its mode and filter, and checked; the FT-991A is switched to
// Hamlib's data modes (FM → DATA-FM, USB → DATA-USB, LSB → DATA-LSB); through HRD, its own
// data-mode button (or list) is used, for any radio HRD offers one for.

using RFpix.Diagnostics;

namespace RFpix.Radio;

/// <summary>Something went wrong talking to the radio; the message is for the operator.</summary>
public sealed class RadioException(string message) : Exception(message);

public sealed partial class RadioConnection : IDisposable
{
    private readonly object _lock = new();
    private readonly RadioSettings _settings;
    private IntPtr _rig;
    private HrdClient? _hrd;   // set when Ham Radio Deluxe has the radio (no Hamlib then)
    private KeyGuard? _guard;   // the time limit's watcher, while keyed
    private string? _modeBeforeTransmit;   // set while data mode was switched on for transmitting, e.g. "FM"
                                           // (until it's been switched back)

    /// <summary>Switch an IC-9700 to data mode while transmitting. Starts as saved in the settings;
    /// the Radio tab changes it at once when its box is ticked or cleared.</summary>
    public bool DataModeForTransmit { get; set; }

    /// <summary>Raised when data mode is switched for transmitting, with what happened in a few
    /// words (e.g. "IC-9700 in FM-D", "IC-9700 back to FM"), for the status line.</summary>
    public event Action<string>? DataModeSwitched;

    /// <summary>What data mode did on the last transmission, e.g. "FM → FM-D → FM"; empty if nothing.</summary>
    public string LastDataModeReport { get; private set; } = "";

    /// <summary>True while RFpix is transmitting (keyed, or with VOX, between Key and Unkey).</summary>
    public bool IsKeyed { get; private set; }

    /// <summary>Raised (on the watcher thread) if the time limit un-keyed the radio.</summary>
    public event Action? KeyedTooLong;

    private RadioConnection(RadioSettings settings, IntPtr rig, HrdClient? hrd = null)
    {
        _settings = settings;
        _rig = rig;
        _hrd = hrd;
        DataModeForTransmit = settings.DataModeForTransmit && RadioChoices.CanSwitchDataMode(settings.Choice);
    }

    /// <summary>Through Ham Radio Deluxe: the radio HRD is controlling ("IC-9700"); null otherwise.</summary>
    public string? HrdRadio => _hrd?.RadioName;

    /// <summary>Opens the connection described by the settings. Throws <see cref="RadioException"/>.</summary>
    public static RadioConnection Open(RadioSettings settings)
    {
        var choice = RadioChoices.Find(settings.Choice);
        AppLog.Info("Radio", "Connecting. " + LogSummary.Radio(settings, PortName(settings), null));
        if (choice.Id == RadioChoices.HrdId)
        {
            var hrd = new RadioConnection(settings, IntPtr.Zero, HrdClient.Connect(settings.Port ?? ""));
            AppLog.Info("Radio", $"Connected to Ham Radio Deluxe, which is controlling the {hrd.HrdRadio}.");
            return hrd;
        }
        int model = choice.HamlibModel == -1 ? settings.OtherModel : choice.HamlibModel;
        if (model <= 0) throw new RadioException("No radio is chosen, so there's nothing to connect to.");
        bool pretend = model == DummyModel;   // Hamlib's pretend radio: no port needed
        if (choice.Connection != "none" && !pretend && string.IsNullOrWhiteSpace(settings.Port))
            throw new RadioException(choice.Connection == "network"
                ? "Type the address of the program sharing the radio, e.g. localhost:4532."
                : "Pick the radio's COM port.");

        IntPtr rig;
        try
        {
            // Hamlib's own messages go to the log while connecting (and before any error later).
            HamlibLog.Attach();
            HamlibLog.WriteFor(ConnectDetail);
            rig = HamlibNative.rig_init((uint)model);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw new RadioException("Hamlib, the radio-control library, couldn't be loaded. " +
                (OperatingSystem.IsWindows() ? "libhamlib-4.dll should be next to RFpix.exe: try rebuilding RFpix."
                 : OperatingSystem.IsMacOS() ? "It comes inside RFpix.app: try installing RFpix again."
                 : File.Exists(Path.Combine(AppContext.BaseDirectory, "RFpix.dll")) && AppContext.BaseDirectory.Contains("/opt/rfpix")
                     ? "It comes with RFpix: try installing RFpix again."
                     : "Install your system's hamlib package (libhamlib4), or use the RFpix AppImage or .deb, which carry their own.") + $" ({ex.Message})");
        }
        if (rig == IntPtr.Zero) throw new RadioException($"Hamlib doesn't know radio model {model}. Check the model number.");

        try
        {
            if (!pretend) Set(rig, "rig_pathname", settings.Port!.Trim());
            if (choice.Connection == "serial")
            {
                Set(rig, "serial_speed", settings.BaudRate.ToString());
                if (!string.IsNullOrWhiteSpace(settings.CivAddress))
                    Set(rig, "civaddr", IcomCiv.Address(settings.CivAddress).ToString());   // Hamlib wants it as a plain number
                Set(rig, "ptt_type", settings.Ptt switch
                {
                    PttMethod.Rts => "RTS",
                    PttMethod.Dtr => "DTR",
                    PttMethod.Vox => "None",
                    _ => "RIG",
                });
            }
            Check(HamlibNative.rig_open(rig), "Couldn't connect to the radio");
        }
        catch
        {
            HamlibNative.rig_cleanup(rig);
            throw;
        }
        // The first readings after connecting (often where a wrong port or baud rate shows) are written too.
        HamlibLog.WriteFor(ConnectDetail);
        AppLog.Info("Radio", "Connected (the port opened and Hamlib accepted the settings).");
        return new RadioConnection(settings, rig);
    }

    /// <summary>How long Hamlib's own messages go to the log, from the start of connecting and
    /// again once connected.</summary>
    private static readonly TimeSpan ConnectDetail = TimeSpan.FromSeconds(15);

    /// <summary>A serial port's full name ("Silicon Labs ... Enhanced COM Port"), for the log.</summary>
    private static string? PortName(RadioSettings settings)
    {
        if (RadioChoices.Find(settings.Choice).Connection != "serial" || string.IsNullOrWhiteSpace(settings.Port)) return null;
        try { return SerialPorts.List().FirstOrDefault(p => p.Name == settings.Port.Trim())?.Description ?? "not found on this computer now"; }
        catch (Exception) { return null; }
    }

    /// <summary>Hamlib's "Dummy" radio (model 1): a pretend radio inside Hamlib that remembers
    /// what it's told. Handy for trying RFpix, and for the tests, without a real radio.</summary>
    public const int DummyModel = 1;

    /// <summary>The frequency the radio is on, in Hz.</summary>
    public double ReadFrequency()
    {
        lock (_lock)
        {
            if (_hrd is { } hrd) return hrd.ReadFrequency();
            Check(HamlibNative.rig_get_freq(Rig, HamlibNative.VfoCurrent, out double hz), "Couldn't read the frequency");
            return hz;
        }
    }

    public void SetFrequency(double hz)
    {
        lock (_lock)
        {
            if (_hrd is { } hrd) { hrd.SetFrequency(hz); return; }
            Check(HamlibNative.rig_set_freq(Rig, HamlibNative.VfoCurrent, hz), "Couldn't set the frequency");
        }
    }

    /// <summary>The radio's mode as Hamlib numbers it (see <see cref="ModeName"/>).</summary>
    public ulong ReadMode()
    {
        lock (_lock)
        {
            if (_hrd is { } hrd) return ModeFromName(hrd.ReadModeText(), hrd.CanSwitchDataMode && hrd.DataModeOn());
            Check(HamlibNative.rig_get_mode(Rig, HamlibNative.VfoCurrent, out ulong mode, out _), "Couldn't read the mode");
            return mode;
        }
    }

    public void SetMode(ulong mode)
    {
        if (_hrd is not null) throw new RadioException("Change the mode in Ham Radio Deluxe.");
        lock (_lock)
            Check(HamlibNative.rig_set_mode(Rig, HamlibNative.VfoCurrent, mode, HamlibNative.PassbandNoChange), "Couldn't change the mode");
    }

    /// <summary>14230000 → "14.230.000 MHz", the way radios show it.</summary>
    public static string FormatFrequency(double hz)
    {
        long h = (long)Math.Round(hz);
        return $"{h / 1_000_000}.{h / 1000 % 1000:000}.{h % 1000:000} MHz";
    }

    /// <summary>A mode's name as radios show it.</summary>
    public static string ModeName(ulong mode) => mode switch
    {
        1ul << 0 => "AM",
        1ul << 1 => "CW",
        HamlibNative.ModeUsb => "USB",
        HamlibNative.ModeLsb => "LSB",
        1ul << 4 => "RTTY",
        HamlibNative.ModeFm => "FM",
        HamlibNative.ModePktLsb => "LSB-D",
        HamlibNative.ModePktUsb => "USB-D",
        HamlibNative.ModePktFm => "FM-D",
        _ => "other",
    };

    public void Dispose()
    {
        lock (_lock)
        {
            // Un-keyed, and the mode put back, if that hasn't happened yet (a few more tries).
            if (_hrd is { } hrd)
            {
                UnkeyForClosing();
                StopWatching();
                hrd.Dispose();
                _hrd = null;
                AppLog.Info("Radio", "Disconnected from Ham Radio Deluxe.");
                return;
            }
            if (_rig == IntPtr.Zero) return;
            UnkeyForClosing();
            StopWatching();
            HamlibNative.rig_close(_rig);
            HamlibNative.rig_cleanup(_rig);
            _rig = IntPtr.Zero;
            AppLog.Info("Radio", "Disconnected from the radio.");
        }
    }

    // ---- helpers ----

    private IntPtr Rig => _rig != IntPtr.Zero ? _rig : throw new RadioException("Not connected to the radio.");

    private static void Set(IntPtr rig, string name, string value)
    {
        var token = HamlibNative.rig_token_lookup(rig, name);
        if (token.Value == 0) return;   // this radio has no such setting (e.g. a network connection's baud rate)
        Check(HamlibNative.rig_set_conf(rig, token, value), $"Hamlib didn't accept {name} = {value}");
    }

    private static void Check(int result, string what)
    {
        if (result == 0) return;
        HamlibLog.WriteRecent();   // the log shows what Hamlib sent and got back just before
        throw new RadioException($"{what}: {Explain(-Math.Abs(result))}");
    }

    /// <summary>Hamlib's error numbers, in plain words with what to check.</summary>
    public static string Explain(int code) => -code switch
    {
        2 => "the settings were refused. Check the COM port and baud rate.",
        5 => "the radio didn't answer. Check it's switched on, the COM port is right, the baud rate matches the radio's menu" +
             " and, for an Icom, the CI-V address.",
        6 => OperatingSystem.IsLinux()
            ? "the serial port couldn't be opened. Check it's the right port, that no other program (WSJT-X, flrig, QSSTV) is using it, " +
              "and that you're in the dialout group (in a terminal: sudo usermod -aG dialout $USER, then log out and back in)."
            : "the COM port couldn't be opened. Check it's the right port and that no other program (WSJT-X, flrig, QSSTV) is using it.",
        8 => "the radio's answer didn't make sense. Check the radio model and baud rate.",
        9 => "the radio refused the command. It may not allow it in its current mode.",
        11 => "this radio can't do that through Hamlib.",
        20 => "the radio is switched off.",
        22 => "the COM port is in use by another program. Close WSJT-X, flrig or QSSTV, or share the radio through rigctld.",
        _ => $"Hamlib error {-code}.",
    };
}
