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
    public void Keying_switches_to_data_mode_and_back()
    {
        using var radio = RadioConnection.Open(Dummy(dataMode: true));
        radio.SetMode(Usb);

        radio.Key(TimeSpan.FromSeconds(10));
        Assert.True(radio.IsKeyed);
        Assert.True(radio.ReadPtt());
        Assert.Equal(UsbData, radio.ReadMode());   // USB-D while transmitting

        radio.Unkey();
        Assert.False(radio.IsKeyed);
        Assert.False(radio.ReadPtt());
        Assert.Equal(Usb, radio.ReadMode());       // back to USB for listening and talking
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
        Assert.Equal(Fm, radio.ReadMode());   // FM-D switched back to FM
        Assert.True(told);
    }

    [Fact]
    public void With_VOX_RFpix_sends_no_key_command_but_still_switches_data_mode()
    {
        // Hamlib is told there's no PTT at all, so a key command would fail: none is sent.
        using var radio = RadioConnection.Open(Dummy(ptt: PttMethod.Vox, dataMode: true));
        radio.SetMode(Usb);

        radio.Key(TimeSpan.FromSeconds(10));
        Assert.True(radio.IsKeyed);
        Assert.Equal(UsbData, radio.ReadMode());   // ready for RFpix's audio to trip VOX

        radio.Unkey();
        Assert.Equal(Usb, radio.ReadMode());
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

    [Theory]
    [InlineData(Usb, UsbData)]
    [InlineData(1ul << 3, 1ul << 10)]   // LSB → LSB-D
    [InlineData(Fm, FmData)]
    [InlineData(UsbData, UsbData)]      // already data: unchanged
    [InlineData(1ul << 0, 1ul << 0)]    // AM: no data version, unchanged
    public void Data_versions_of_modes(ulong mode, ulong expected)
    {
        Assert.Equal(expected, RadioConnection.DataModeFor(mode));
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
}
