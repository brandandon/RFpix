// FrequencyLockTests.cs - checks that RFpix measures how far off frequency a HamDRM signal is.
//
// Limits come from measurements (Oct 10, 2026): a clean signal reads within 0.1 Hz,
// at 6 dB SNR within 0.9 Hz, at 3 dB within 1.3 Hz, and it never picked the wrong
// whole carrier spacing. The tests allow some headroom above those figures.

using RFpix.Audio;
using RFpix.HamDrm;
using Xunit;
using static RFpix.Tests.TestAudio;

namespace RFpix.Tests;

public class FrequencyLockTests
{
    [Theory]
    [MemberData(nameof(Recordings.All), MemberType = typeof(Recordings))]
    public void A_correctly_tuned_recording_reads_0_Hz(string file)
    {
        var wav = WavFile.Read(Recordings.Path(file));
        var during = Run(wav.Samples, 48000).Between(2.0, wav.Duration.TotalSeconds - 1.2);

        Assert.NotEmpty(during);
        Assert.All(during, d =>
        {
            Assert.NotNull(d.Lock);
            Assert.True(d.Lock!.IsLocked);
            Assert.InRange(d.Lock.PilotQuality, 0.98, 1.0);
            Assert.InRange(d.Lock.OffsetHz, -0.25, 0.25);
        });
    }

    [Theory]
    [InlineData("01_modeA_2p5khz_qam64_long_high_rs0.wav", -150)]
    [InlineData("01_modeA_2p5khz_qam64_long_high_rs0.wav", 100)]
    [InlineData("03_modeA_2p2khz_qam4_long_high_rs0.wav", 30)]
    [InlineData("04_modeB_2p5khz_qam16_long_high_rs0.wav", -50)]
    [InlineData("04_modeB_2p5khz_qam16_long_high_rs0.wav", 25)]
    [InlineData("05_modeB_2p2khz_qam64_short_low_rs0.wav", 12.5)]
    [InlineData("07_modeE_2p5khz_qam4_long_high_rs0.wav", -150)]
    [InlineData("07_modeE_2p5khz_qam4_long_high_rs0.wav", 100)]
    [InlineData("08_modeE_2p2khz_qam16_short_low_rs0.wav", -37.5)]   // exactly half a mode E spacing
    public void Measures_a_tuning_error(string file, double offsetHz)
    {
        var wav = WavFile.Read(Recordings.Path(file));
        var shifted = ShiftFrequency(wav.Samples, offsetHz, 48000);
        var during = Run(shifted, 48000).Between(2.0, wav.Duration.TotalSeconds - 1.2);

        Assert.NotEmpty(during);
        Assert.All(during, d => Assert.InRange(d.Lock!.OffsetHz, offsetHz - 0.25, offsetHz + 0.25));
    }

    [Theory]
    [InlineData("04_modeB_2p5khz_qam16_long_high_rs0.wav", 25, 6, 1.0)]
    [InlineData("01_modeA_2p5khz_qam64_long_high_rs0.wav", 0, 3, 1.5)]
    [InlineData("07_modeE_2p5khz_qam4_long_high_rs0.wav", -150, 3, 1.5)]
    public void Stays_accurate_in_noise(string file, double offsetHz, double snrDb, double allowedErrorHz)
    {
        var wav = WavFile.Read(Recordings.Path(file));
        var audio = AddNoise(ShiftFrequency(wav.Samples, offsetHz, 48000), snrDb, seed: 4);
        var during = Run(audio, 48000).Between(2.0, wav.Duration.TotalSeconds - 1.2);

        var locked = during.Where(d => d.Lock is { IsLocked: true }).ToList();
        Assert.True(locked.Count >= 0.95 * during.Count, $"locked only {locked.Count} of {during.Count} times");
        Assert.All(locked, d => Assert.InRange(d.Lock!.OffsetHz, offsetHz - allowedErrorHz, offsetHz + allowedErrorHz));
    }

    [Fact]
    public void No_signal_means_no_frequency_reading()
    {
        var decisions = Run(Noise(48000 * 5, 0.1, seed: 5), 48000);
        Assert.NotEmpty(decisions);
        Assert.All(decisions, d => Assert.Null(d.Lock));
    }

    [Fact]
    public void Status_line_shows_the_offset()
    {
        var wav = WavFile.Read(Recordings.Path("04_modeB_2p5khz_qam16_long_high_rs0.wav"));
        var shifted = ShiftFrequency(wav.Samples, 25, 48000);

        var receiver = new HamDrmReceiver();
        int tenSeconds = 48000 * 10;
        for (int pos = 0; pos < tenSeconds; pos += 480)
            receiver.AddSamples(shifted[pos..(pos + 480)], 480, 48000);

        Assert.StartsWith("HamDRM mode B  ·  offset +25.0 Hz  ·  ", receiver.StatusText);
    }
}
