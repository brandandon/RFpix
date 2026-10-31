// HrdTests.cs - checks sharing the radio through Ham Radio Deluxe, against a pretend HRD
// (a small server on this computer that answers the way HRD's Rig Control does for an
// IC-9700: a TX button, a Data button and a Mode list).

using System.Net;
using System.Net.Sockets;
using RFpix.Radio;
using Xunit;

namespace RFpix.Tests;

public class HrdTests
{
    /// <summary>A pretend Ham Radio Deluxe: answers one connection's commands, and remembers what it was told.</summary>
    private sealed class FakeHrd : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Thread _thread;
        public readonly List<string> Commands = new();
        public string Mode = "USB";
        public bool Tx, Data;
        public ulong Frequency = 145_500_000;
        public bool DataButton = true;
        public int RefuseUnkey, RefuseDataOff;   // how many times to answer "Failed" to these
        public bool RefuseKey;

        public FakeHrd()
        {
            _listener.Start();
            _thread = new Thread(Serve) { IsBackground = true };
            _thread.Start();
        }

        public string Address => $"127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

        private void Serve()
        {
            try
            {
                using var client = _listener.AcceptTcpClient();
                var stream = client.GetStream();
                while (true)
                {
                    var header = Read(stream, HrdMessage.HeaderSize);
                    if (header is null) return;
                    int length = HrdMessage.Length(header)!.Value;
                    var rest = Read(stream, length - HrdMessage.HeaderSize);
                    if (rest is null) return;
                    var message = header.Concat(rest).ToArray();
                    string command = HrdMessage.Text(message);
                    lock (Commands) Commands.Add(command);
                    stream.Write(HrdMessage.Make(Answer(command) + "\0"));   // HRD ends a value with a zero, then the message's own
                }
            }
            catch (Exception) { }
        }

        private static byte[]? Read(NetworkStream stream, int count)
        {
            var buffer = new byte[count];
            int got = 0;
            while (got < count)
            {
                int n = stream.Read(buffer, got, count - got);
                if (n == 0) return null;
                got += n;
            }
            return buffer;
        }

        private string Answer(string command)
        {
            string c = command.StartsWith("[1] ") ? command[4..] : command;
            switch (c)
            {
                case "get context": return "1";
                case "get radios": return "1:IC-9700";
                case "get radio": return "IC-9700";
                case "get buttons": return DataButton ? "TX,Data,VFO A,Tuner" : "TX,VFO A";
                case "get dropdowns": return "Mode,AGC";
                case "get dropdown-list {Mode}": return "LSB,USB,AM,CW,RTTY,FM,DV";
                case "get dropdown-list {AGC}": return "Fast,Mid,Slow";
                case "get frequency": return Frequency.ToString();
                case "get dropdown-text {Mode}": return $"Mode: {Mode}";
                case "get button-select TX": return Tx ? "1" : "0";
                case "get button-select Data": return Data ? "1" : "0";
                case "set button-select TX 1":
                    if (RefuseKey) return "Failed";
                    Tx = true; return "OK";
                case "set button-select TX 0":
                    if (RefuseUnkey > 0) { RefuseUnkey--; return "Failed"; }
                    Tx = false; return "OK";
                case "set button-select Data 1": Data = true; return "OK";
                case "set button-select Data 0":
                    if (RefuseDataOff > 0) { RefuseDataOff--; return "Failed"; }
                    Data = false; return "OK";
            }
            if (c.StartsWith("set frequency-hz ")) { Frequency = ulong.Parse(c[17..]); return "OK"; }
            return "Unknown command";
        }

        public void Dispose() => _listener.Stop();
    }

    private static RadioSettings Hrd(FakeHrd hrd, PttMethod ptt = PttMethod.Cat, bool dataMode = false) =>
        new() { Choice = RadioChoices.HrdId, Port = hrd.Address, Ptt = ptt, DataModeForTransmit = dataMode };

    private const ulong Usb = 1ul << 2, UsbData = 1ul << 11, Fm = 1ul << 5, FmData = 1ul << 12;

    [Fact]
    public void Messages_are_framed_as_HRD_expects()
    {
        var m = HrdMessage.Make("get radio");
        Assert.Equal(16 + 2 * ("get radio".Length + 1), m.Length);
        Assert.Equal((uint)m.Length, BitConverter.ToUInt32(m, 0));
        Assert.Equal(HrdMessage.Magic1, BitConverter.ToUInt32(m, 4));
        Assert.Equal(HrdMessage.Magic2, BitConverter.ToUInt32(m, 8));
        Assert.Equal(0u, BitConverter.ToUInt32(m, 12));
        Assert.Equal(m.Length, HrdMessage.Length(m));
        Assert.Equal("get radio", HrdMessage.Text(m));
        Assert.Null(HrdMessage.Length(m.AsSpan(0, 10)));                  // the header isn't all here yet
        var bad = (byte[])m.Clone();
        bad[4] ^= 0xFF;
        Assert.Throws<RadioException>(() => HrdMessage.Length(bad));     // not from HRD
    }

    [Fact]
    public void Addresses_default_to_HRDs_port()
    {
        Assert.Equal(("localhost", 7809), HrdClient.ParseAddress(""));
        Assert.Equal(("localhost", 7809), HrdClient.ParseAddress("localhost"));
        Assert.Equal(("192.168.1.20", 7810), HrdClient.ParseAddress(" 192.168.1.20:7810 "));
    }

    [Fact]
    public void The_frequency_and_mode_are_read_through_HRD()
    {
        using var hrd = new FakeHrd();
        using var radio = RadioConnection.Open(Hrd(hrd));
        Assert.Equal("IC-9700", radio.HrdRadio);
        Assert.Equal(145_500_000, radio.ReadFrequency());
        Assert.Equal(Usb, radio.ReadMode());
        hrd.Mode = "FM";
        hrd.Data = true;
        Assert.Equal(FmData, radio.ReadMode());
        Assert.Equal("FM-D", RadioConnection.ModeName(radio.ReadMode()));
        radio.SetFrequency(145_525_000);
        Assert.Equal(145_525_000ul, hrd.Frequency);
        // Every command after the first few names the radio.
        lock (hrd.Commands) Assert.Contains("[1] get frequency", hrd.Commands);
    }

    [Fact]
    public void Keying_presses_HRDs_TX_button_with_data_mode_on_and_back_off()
    {
        using var hrd = new FakeHrd { Mode = "FM" };
        using var radio = RadioConnection.Open(Hrd(hrd, dataMode: true));
        Assert.True(radio.DataModeForTransmit);
        radio.Key(TimeSpan.FromSeconds(5));
        Assert.True(hrd.Tx);
        Assert.True(hrd.Data);
        radio.Unkey();
        Assert.False(hrd.Tx);
        Assert.False(hrd.Data);
        Assert.Equal("FM → FM-D → FM", radio.LastDataModeReport);
        lock (hrd.Commands)
        {
            int dataOn = hrd.Commands.IndexOf("[1] set button-select Data 1"), txOn = hrd.Commands.IndexOf("[1] set button-select TX 1");
            Assert.True(dataOn >= 0 && dataOn < txOn);   // data mode first, then keying
        }
    }

    [Fact]
    public void A_radio_already_in_data_mode_is_left_alone()
    {
        using var hrd = new FakeHrd { Data = true };
        using var radio = RadioConnection.Open(Hrd(hrd, dataMode: true));
        radio.Key(TimeSpan.FromSeconds(5));
        radio.Unkey();
        Assert.True(hrd.Data);
        Assert.Contains("already", radio.LastDataModeReport);
    }

    [Fact]
    public void With_VOX_HRD_is_not_asked_to_key_and_the_time_limit_still_works()
    {
        using var hrd = new FakeHrd();
        using (var radio = RadioConnection.Open(Hrd(hrd, PttMethod.Vox)))
        {
            radio.Key(TimeSpan.FromSeconds(5));
            Assert.False(hrd.Tx);
            radio.Unkey();
        }
        using var hrd2 = new FakeHrd();
        using var keyed = RadioConnection.Open(Hrd(hrd2));
        keyed.Key(TimeSpan.FromMilliseconds(200));
        Assert.True(hrd2.Tx);
        Thread.Sleep(1000);
        Assert.False(hrd2.Tx);            // released by the time limit
        Assert.False(keyed.IsKeyed);
    }

    // Code review, Oct 2026: if un-keying failed, nothing tried again, and the radio could be left
    // transmitting (and in data mode).
    [Fact]
    public void If_un_keying_fails_it_is_tried_again_until_it_works_and_data_mode_is_put_back()
    {
        using var hrd = new FakeHrd { Mode = "FM", RefuseUnkey = 4 };   // refused twice now, twice more on the first retry
        using var radio = RadioConnection.Open(Hrd(hrd, dataMode: true));
        radio.Key(TimeSpan.FromSeconds(60));
        Assert.True(hrd.Tx && hrd.Data);

        var ex = Assert.Throws<RadioException>(radio.Unkey);
        Assert.Contains("Un-key the radio by hand", ex.Message);
        Assert.True(radio.IsKeyed);   // still transmitting, as far as RFpix knows

        // Long before the 60-second limit, RFpix tries again (every 2 seconds) until it's un-keyed.
        var until = DateTime.UtcNow + RadioConnection.UnkeyRetryEvery * 4;
        while (hrd.Tx && DateTime.UtcNow < until) Thread.Sleep(50);
        Assert.False(hrd.Tx);
        Assert.False(radio.IsKeyed);
        Assert.False(hrd.Data);   // and the radio's mode was put back
        Assert.Equal("FM → FM-D → FM", radio.LastDataModeReport);
    }

    [Fact]
    public void If_switching_data_mode_back_fails_closing_tries_again()
    {
        using var hrd = new FakeHrd { Mode = "FM", RefuseDataOff = 1 };
        var radio = RadioConnection.Open(Hrd(hrd, dataMode: true));
        radio.Key(TimeSpan.FromSeconds(60));
        Assert.Throws<RadioException>(radio.Unkey);   // un-keyed, but data mode wouldn't switch off
        Assert.False(hrd.Tx);
        Assert.False(radio.IsKeyed);
        Assert.True(hrd.Data);

        radio.Dispose();   // closing RFpix (or disconnecting)
        Assert.False(hrd.Data);
    }

    [Fact]
    public void If_keying_fails_data_mode_is_switched_back()
    {
        using var hrd = new FakeHrd { Mode = "FM", DataButton = true };
        using var radio = RadioConnection.Open(Hrd(hrd, dataMode: true));
        hrd.RefuseKey = true;
        Assert.Throws<RadioException>(() => radio.Key(TimeSpan.FromSeconds(60)));
        Assert.False(radio.IsKeyed);
        Assert.False(hrd.Data);   // not left in data mode
    }

    [Fact]
    public void A_radio_HRD_has_no_data_button_for_says_so_and_still_keys()
    {
        using var hrd = new FakeHrd { DataButton = false };
        using var radio = RadioConnection.Open(Hrd(hrd, dataMode: true));
        radio.Key(TimeSpan.FromSeconds(5));
        Assert.True(hrd.Tx);
        radio.Unkey();
        Assert.Contains("no data mode switch", radio.LastDataModeReport);
    }

    [Fact]
    public void Without_HRD_running_the_problem_is_explained()
    {
        // A port nothing is listening on.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var e = Assert.Throws<RadioException>(() => RadioConnection.Open(new RadioSettings { Choice = RadioChoices.HrdId, Port = $"127.0.0.1:{port}" }));
        Assert.Contains("Ham Radio Deluxe", e.Message);
    }

    [Theory]
    [InlineData("USB", false, Usb)]
    [InlineData("USB", true, UsbData)]
    [InlineData("USB-D", false, UsbData)]
    [InlineData("FM-N", false, Fm)]
    [InlineData("PKT-FM", false, FmData)]
    [InlineData("LSB", false, 1ul << 3)]
    [InlineData("CW-R", false, 1ul << 1)]
    [InlineData("DV", false, 0ul)]
    public void HRDs_mode_names_are_understood(string name, bool data, ulong mode) =>
        Assert.Equal(mode, RadioConnection.ModeFromName(name, data));
}
