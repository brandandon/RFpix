// RadioTests.cs - checks radio control through Hamlib, using Hamlib's "Dummy" radio
// (model 1): a pretend radio inside Hamlib that remembers what it's told, so the
// commands can be checked without a real radio on the end of a cable.

using RFpix.Radio;
using Xunit;

namespace RFpix.Tests;

public class RadioTests
{
    private static RadioSettings Dummy(PttMethod ptt = PttMethod.Cat, bool dataMode = false) =>
        new() { Choice = "other", OtherModel = RadioConnection.DummyModel, BaudRate = 9600, Ptt = ptt, DataModeForTransmit = dataMode };

    private const ulong Usb = 1ul << 2, UsbData = 1ul << 11, Fm = 1ul << 5, FmData = 1ul << 12;

    [Fact]
    public void With_no_radio_control_keying_is_always_vox()
    {
        // Older settings files can hold "no radio" with CAT keying (the page showed VOX but didn't save it).
        Assert.Equal(PttMethod.Vox, new RadioSettings { Choice = "none", Ptt = PttMethod.Cat }.Keying());
        Assert.Equal(PttMethod.Rts, new RadioSettings { Choice = "ic9700", Ptt = PttMethod.Rts }.Keying());
        Assert.Equal(PttMethod.Vox, new RadioSettings { Choice = "ic9700", Ptt = PttMethod.Vox }.Keying());
    }

    [Fact]
    public void Reads_and_sets_frequency_and_mode()
    {
        using var radio = RadioConnection.Open(Dummy());
        radio.SetFrequency(14_230_000);
        radio.SetMode(Usb);

        Assert.Equal(14_230_000, radio.ReadFrequency());
        Assert.Equal(Usb, radio.ReadMode());
        Assert.Equal("USB", RadioConnection.ModeName(radio.ReadMode()));
    }

    [Fact]
    public void Data_mode_is_only_for_the_IC_9700()
    {
        // Hamlib's pretend radio isn't an IC-9700: the box is ignored and the mode left alone.
        using var radio = RadioConnection.Open(Dummy(dataMode: true));
        Assert.False(radio.DataModeForTransmit);
        radio.SetMode(Usb);
        radio.Key(TimeSpan.FromSeconds(10));
        Assert.Equal(Usb, radio.ReadMode());
        radio.Unkey();
        Assert.Equal("", radio.LastDataModeReport);
    }

    [Fact]
    public void Without_data_mode_the_mode_is_left_alone()
    {
        using var radio = RadioConnection.Open(Dummy(dataMode: false));
        radio.SetMode(Fm);
        radio.Key(TimeSpan.FromSeconds(10));
        Assert.Equal(Fm, radio.ReadMode());
        radio.Unkey();
        Assert.Equal(Fm, radio.ReadMode());
    }

    [Fact]
    public void The_time_limit_unkeys_the_radio_by_itself()
    {
        using var radio = RadioConnection.Open(Dummy(dataMode: true));
        radio.SetMode(Fm);
        bool told = false;
        radio.KeyedTooLong += () => told = true;

        radio.Key(TimeSpan.FromMilliseconds(300));
        Assert.True(radio.ReadPtt());
        // Released after 0.3 s, plus a quarter second to switch back from data mode.
        for (int waited = 0; radio.IsKeyed && waited < 5000; waited += 50) Thread.Sleep(50);
        Thread.Sleep(400);

        Assert.False(radio.IsKeyed);
        Assert.False(radio.ReadPtt());
        Assert.Equal(Fm, radio.ReadMode());
        Assert.True(told);
    }

    [Fact]
    public void With_VOX_RFpix_sends_no_key_command()
    {
        // Hamlib is told there's no PTT at all, so a key command would fail: none is sent.
        using var radio = RadioConnection.Open(Dummy(ptt: PttMethod.Vox));
        radio.SetMode(Usb);

        radio.Key(TimeSpan.FromSeconds(10));
        Assert.True(radio.IsKeyed);
        radio.Unkey();
        Assert.False(radio.IsKeyed);
    }

    [Fact]
    public void Closing_the_connection_unkeys_and_restores_the_mode()
    {
        var settings = Dummy(dataMode: true);
        var radio = RadioConnection.Open(settings);
        radio.SetMode(Usb);
        radio.Key(TimeSpan.FromSeconds(10));
        radio.Dispose();

        Assert.False(radio.IsKeyed);
        Assert.Throws<RadioException>(() => radio.ReadFrequency());   // closed
    }

    // ---- the IC-9700's data mode, by Icom's own commands (CI-V) ----

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes);

    [Fact]
    public void CIV_messages_are_framed_as_Icom_specifies()
    {
        Assert.Equal("FEFEA2E004FD", Hex(IcomCiv.ReadMode(0xA2)));
        Assert.Equal("FEFEA2E01A06FD", Hex(IcomCiv.ReadDataMode(0xA2)));
        Assert.Equal("FEFEA2E01A060102FD", Hex(IcomCiv.SetDataMode(0xA2, on: true, filter: 2)));   // data on, keep filter 2
        Assert.Equal("FEFEA2E01A060000FD", Hex(IcomCiv.SetDataMode(0xA2, on: false, filter: 2)));  // data off
        Assert.Equal("FEFEA2E01A060101FD", Hex(IcomCiv.SetDataMode(0xA2, on: true, filter: 0)));   // a filter is needed: 1
    }

    [Fact]
    public void CIV_answers_are_told_from_echoes_and_other_messages()
    {
        byte[] B(string hex) => Convert.FromHexString(hex);
        Assert.Equal(new byte[] { 0x04, 0x05, 0x01 }, IcomCiv.Answer(B("FEFEE0A2040501FD"), 0xA2));   // FM, filter 1
        Assert.Equal(new byte[] { 0xFB }, IcomCiv.Answer(B("FEFEE0A2FBFD"), 0xA2));                    // OK
        Assert.Null(IcomCiv.Answer(B("FEFEA2E004FD"), 0xA2));            // the radio's echo of our own message
        Assert.Null(IcomCiv.Answer(B("FEFE00A2000000FD"), 0xA2));        // a broadcast to everyone (transceive)
        Assert.Null(IcomCiv.Answer(B("FEFEE0A4FBFD"), 0xA2));            // from another radio
        Assert.Equal(new byte[] { 0xFB }, IcomCiv.Answer(B("FEFEFEE0A2FBFD"), 0xA2));                  // an extra FE in front
        Assert.Null(IcomCiv.Answer(B("FEFEE0A2"), 0xA2));                // cut short
    }

    /// <summary>A pretend IC-9700 on the CI-V line: it keeps a mode, filter and DATA setting,
    /// answers questions, and can be told to echo, ignore or refuse commands.</summary>
    private sealed class FakeIc9700
    {
        public byte Mode = 0x05, Filter = 1;   // FM, filter 1
        public bool Data;
        public bool RefuseData;                // answers "not OK" to data on
        public bool IgnoreData;                // says OK but doesn't switch
        public readonly List<string> Sent = new();

        public byte[] Exchange(byte[] frame, string what)
        {
            Sent.Add(Convert.ToHexString(frame));
            var body = frame[4..^1];
            byte[] answer = body switch
            {
                [0x04] => new byte[] { 0x04, Mode, Filter },
                [0x1A, 0x06] => new byte[] { 0x1A, 0x06, (byte)(Data ? 1 : 0), (byte)(Data ? Filter : 0) },
                [0x1A, 0x06, var on, var filter] when RefuseData && on == 1 => new[] { IcomCiv.NotOk },
                [0x1A, 0x06, var on, var filter] => Set(on == 1, filter),
                _ => new[] { IcomCiv.NotOk },
            };
            // Wrap it as the radio would send it, and read it back as RFpix does.
            var reply = new byte[] { 0xFE, 0xFE, IcomCiv.Computer, 0xA2 }.Concat(answer).Append(IcomCiv.End).ToArray();
            return IcomCiv.Answer(reply, 0xA2)!;
        }

        private byte[] Set(bool on, byte filter)
        {
            if (!IgnoreData) { Data = on; if (on) Filter = filter; }
            return new[] { IcomCiv.Ok };
        }
    }

    [Fact]
    public void The_IC_9700_goes_to_FM_D_with_its_own_filter_and_back_to_FM()
    {
        var radio = new FakeIc9700 { Mode = 0x05, Filter = 2 };
        var data = new IcomDataMode(0xA2, radio.Exchange, TimeSpan.Zero);
        var (mode, report) = data.On();
        Assert.Equal("FM", mode);
        Assert.Equal("FM → FM-D", report);
        Assert.True(radio.Data);
        Assert.Equal(2, radio.Filter);   // the filter it was on
        Assert.Contains("FEFEA2E01A060102FD", radio.Sent);
        data.Off(mode!);
        Assert.False(radio.Data);
    }

    [Fact]
    public void USB_goes_to_USB_D_and_a_radio_already_in_data_mode_is_left_alone()
    {
        var radio = new FakeIc9700 { Mode = 0x01, Filter = 1 };
        Assert.Equal("USB", new IcomDataMode(0xA2, radio.Exchange, TimeSpan.Zero).On().Mode);

        var already = new FakeIc9700 { Mode = 0x01, Data = true };
        var (mode, report) = new IcomDataMode(0xA2, already.Exchange, TimeSpan.Zero).On();
        Assert.Null(mode);   // nothing to switch back afterwards
        Assert.Equal("already in data mode", report);

        var cw = new FakeIc9700 { Mode = 0x03 };   // CW has no data mode
        Assert.Null(new IcomDataMode(0xA2, cw.Exchange, TimeSpan.Zero).On().Mode);
        Assert.False(cw.Data);
    }

    [Fact]
    public void A_radio_that_refuses_or_doesnt_switch_is_reported_before_keying()
    {
        var refusing = new FakeIc9700 { RefuseData = true };
        var e1 = Assert.Throws<RadioException>(() => new IcomDataMode(0xA2, refusing.Exchange, TimeSpan.Zero).On());
        Assert.Contains("refused", e1.Message);

        var ignoring = new FakeIc9700 { IgnoreData = true };
        var e2 = Assert.Throws<RadioException>(() => new IcomDataMode(0xA2, ignoring.Exchange, TimeSpan.Zero).On());
        Assert.Contains("stayed in FM", e2.Message);
    }

    [Fact]
    public void CIV_addresses_and_modes_read_plainly()
    {
        Assert.Equal(0xA2, IcomCiv.Address(null));
        Assert.Equal(0xA2, IcomCiv.Address("a2"));
        Assert.Equal(0x94, IcomCiv.Address("0x94"));
        Assert.Throws<RadioException>(() => IcomCiv.Address("ZZ"));
        Assert.Equal("FM", IcomCiv.ModeName(0x05));
        Assert.Equal("USB", IcomCiv.ModeName(0x01));
        Assert.True(IcomCiv.HasDataMode(0x05));
        Assert.False(IcomCiv.HasDataMode(0x03));   // CW
    }

    [Fact]
    public void Problems_are_explained_in_plain_words()
    {
        var none = Assert.Throws<RadioException>(() => RadioConnection.Open(new RadioSettings { Choice = "none" }));
        Assert.Contains("No radio is chosen", none.Message);

        var noPort = Assert.Throws<RadioException>(() => RadioConnection.Open(new RadioSettings { Choice = "ft991a" }));
        Assert.Contains("COM port", noPort.Message);

        var badModel = Assert.Throws<RadioException>(() => RadioConnection.Open(new RadioSettings { Choice = "other", OtherModel = 999999, Port = "COM1" }));
        Assert.Contains("999999", badModel.Message);

        var badCiv = Assert.Throws<RadioException>(() => RadioConnection.Open(
            new RadioSettings { Choice = "ic9700", Port = "COM1", BaudRate = 19200, CivAddress = "ZZ" }));
        Assert.Contains("CI-V", badCiv.Message);

        // A port that isn't there: the message says what to check.
        string missingPort = OperatingSystem.IsWindows() ? "COM250" : "/dev/ttyUSB-not-here";
        var noRadio = Assert.Throws<RadioException>(() => RadioConnection.Open(
            new RadioSettings { Choice = "ft991a", Port = missingPort, BaudRate = 38400 }));
        Assert.StartsWith("Couldn't connect to the radio:", noRadio.Message);
        Assert.Contains("COM port", noRadio.Message);
    }

    [Theory]
    [InlineData(14_230_000, "14.230.000 MHz")]
    [InlineData(144_500_000, "144.500.000 MHz")]
    [InlineData(7_171_250, "7.171.250 MHz")]
    public void Frequencies_are_shown_as_on_the_radio(double hz, string shown)
    {
        Assert.Equal(shown, RadioConnection.FormatFrequency(hz));
    }

    [Fact]
    public void The_radio_list_has_the_station_radios_set_up_as_they_worked_in_QSSTV()
    {
        var ic9700 = RadioChoices.Find("ic9700");
        Assert.Equal((3081, "A2", PttMethod.Rts, true), (ic9700.HamlibModel, ic9700.CivAddress, ic9700.Ptt, ic9700.DataMode));
        var ft991a = RadioChoices.Find("ft991a");
        Assert.Equal((1035, PttMethod.Cat, false), (ft991a.HamlibModel, ft991a.Ptt, ft991a.DataMode));
        Assert.Equal("none", RadioChoices.Find("something old").Id);   // unknown → no radio control
    }

    [Fact]
    public void Hamlib_lists_every_radio_it_knows_with_make_model_and_number()
    {
        var all = HamlibModels.All();
        Assert.True(all.Count > 200, $"only {all.Count} radios listed");
        Assert.Equal("Yaesu", HamlibModels.Find(1035)?.Make);
        Assert.StartsWith("FT-991", HamlibModels.Find(1035)?.Model);         // Hamlib's name covers the FT-991A
        Assert.EndsWith("(1035)", HamlibModels.Find(1035)?.ToString());      // what the list shows
        Assert.Equal("Icom", HamlibModels.Find(3081)?.Make);
        Assert.Contains("9700", HamlibModels.Find(3081)?.Model);
        Assert.Equal(RadioConnection.DummyModel, HamlibModels.Find(RadioConnection.DummyModel)?.Number);
        Assert.Equal(all.Count, all.Select(m => m.Number).Distinct().Count());   // each radio once
    }

    [Fact]
    public void The_model_number_is_read_from_a_list_entry_or_a_typed_number()
    {
        Assert.Equal(1035, HamlibModels.NumberFrom("Yaesu FT-991 (1035)"));
        Assert.Equal(3081, HamlibModels.NumberFrom(" 3081 "));
        Assert.Null(HamlibModels.NumberFrom("FT-991"));   // still typing: no number yet
        Assert.Null(HamlibModels.NumberFrom(""));
        Assert.Null(HamlibModels.NumberFrom("0"));
    }
}
