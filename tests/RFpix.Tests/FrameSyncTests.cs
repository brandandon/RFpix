// FrameSyncTests.cs - checks that RFpix finds where frames start, and measures the
// difference between the sending and receiving sound cards' clocks.
//
// Every recording's signal starts 1.0 s in, and frames follow every 0.4 s, so frame
// starts belong at 1.0, 1.4, 1.8 s and so on. A clock difference stretches that:
// at +100 ppm they're at 1.0001, 1.40014, ... seconds by the receiver's count.
//
// Limits come from measurements (Oct 10, 2026), with some headroom: frame starts within
// 0.25 ms; clock within 10 ppm on clean signals and 23 ppm at 6 dB SNR.

using RFpix.Audio;
using RFpix.HamDrm;
using Xunit;
using static RFpix.Tests.TestAudio;

namespace RFpix.Tests;

public class FrameSyncTests
{
    [Theory]
    [MemberData(nameof(Recordings.All), MemberType = typeof(Recordings))]
    public void Finds_every_frame_start_in_every_recording(string file)
    {
        var wav = WavFile.Read(Recordings.Path(file));
        var during = Run(wav.Samples, 48000).Between(2.5, wav.Duration.TotalSeconds - 1.2);

        Assert.NotEmpty(during);
        Assert.All(during, d => Assert.NotNull(d.FrameStartTime));
        Assert.All(during, d => Assert.InRange(MissMs(d.FrameStartTime!.Value, 0), -0.5, 0.5));
    }

    [Theory]
    [InlineData("01_modeA_2p5khz_qam64_long_high_rs0.wav", 100)]
    [InlineData("04_modeB_2p5khz_qam16_long_high_rs0.wav", -250)]
    [InlineData("05_modeB_2p2khz_qam64_short_low_rs0.wav", -100)]
    [InlineData("07_modeE_2p5khz_qam4_long_high_rs0.wav", 50)]
    [InlineData("08_modeE_2p2khz_qam16_short_low_rs0.wav", 0)]
    public void Measures_the_clock_difference(string file, double ppm)
    {
        var wav = WavFile.Read(Recordings.Path(file));
        var audio = ppm == 0 ? wav.Samples : ChangeClock(wav.Samples, ppm);
        double end = audio.Length / 48000.0;
        var decisions = Run(audio, 48000);

        // Frame starts still land where the stretched timing puts them.
        var during = decisions.Between(2.5, end - 1.2);
        Assert.All(during, d => Assert.InRange(MissMs(d.FrameStartTime!.Value, ppm), -0.5, 0.5));

        // No clock reading until there are 4 s of frames to measure over...
        Assert.All(decisions.Between(0, 4.5), d => Assert.Null(d.ClockPpm));
        // ...then it reads the difference within 12 ppm.
        // (Frames are confirmed from about 2 s in, so 4 s of them are ready by about 6.2 s.)
        var readings = decisions.Between(6.5, end - 1.2).Select(d => d.ClockPpm).ToList();
        Assert.NotEmpty(readings);
        Assert.All(readings, r => Assert.InRange(r!.Value, ppm - 12, ppm + 12));
    }

    [Theory]
    [InlineData("04_modeB_2p5khz_qam16_long_high_rs0.wav", 100)]
    [InlineData("07_modeE_2p5khz_qam4_long_high_rs0.wav", 0)]
    [InlineData("05_modeB_2p2khz_qam64_short_low_rs0.wav", -100)]
    public void Stays_accurate_at_6_dB_SNR(string file, double ppm)
    {
        var wav = WavFile.Read(Recordings.Path(file));
        var audio = AddNoise(ppm == 0 ? wav.Samples : ChangeClock(wav.Samples, ppm), 6, seed: 7);
        double end = audio.Length / 48000.0;
        var during = Run(audio, 48000).Between(2.5, end - 1.2);

        var found = during.Where(d => d.FrameStartTime is not null).ToList();
        Assert.True(found.Count >= 0.9 * during.Count, $"frame found only {found.Count} of {during.Count} times");
        Assert.All(found, d => Assert.InRange(MissMs(d.FrameStartTime!.Value, ppm), -0.5, 0.5));

        var readings = during.Where(d => d.Time >= 6.0 && d.ClockPpm is not null).ToList();
        Assert.NotEmpty(readings);
        Assert.All(readings, d => Assert.InRange(d.ClockPpm!.Value, ppm - 30, ppm + 30));
    }

    [Theory]
    [InlineData("07_modeE_2p5khz_qam4_long_high_rs0.wav")]
    [InlineData("04_modeB_2p5khz_qam16_long_high_rs0.wav")]
    public void Rarely_trusts_a_wrong_frame_start_even_at_3_dB_SNR(string file)
    {
        // At 3 dB the frame finder sometimes picks the wrong symbol; the voting
        // (3 of the last 5 readings must agree) should block nearly all of those.
        var wav = WavFile.Read(Recordings.Path(file));
        var audio = AddNoise(wav.Samples, 3, seed: 8);
        var during = Run(audio, 48000).Between(2.5, wav.Duration.TotalSeconds - 1.2);

        var found = during.Where(d => d.FrameStartTime is not null).ToList();
        int wrong = found.Count(d => Math.Abs(MissMs(d.FrameStartTime!.Value, 0)) > 2);
        Assert.True(found.Count >= 0.7 * during.Count, $"frame found only {found.Count} of {during.Count} times");
        Assert.True(wrong <= 0.01 * found.Count, $"{wrong} wrong frame starts out of {found.Count}");
    }

    [Fact]
    public void Status_line_shows_the_clock_difference()
    {
        var wav = WavFile.Read(Recordings.Path("04_modeB_2p5khz_qam16_long_high_rs0.wav"));
        var audio = ChangeClock(wav.Samples, 100);

        var receiver = new HamDrmReceiver();
        int fifteenSeconds = 48000 * 15;
        for (int pos = 0; pos < fifteenSeconds; pos += 480)
            receiver.AddSamples(audio[pos..(pos + 480)], 480, 48000);

        Assert.InRange(receiver.ClockPpm!.Value, 90, 110);
        Assert.Contains($"clock {receiver.ClockPpm.Value:+0} ppm", receiver.StatusText);
    }

    [Fact]
    public void No_signal_means_no_frame_and_no_clock()
    {
        var decisions = Run(Noise(48000 * 8, 0.1, seed: 9), 48000);
        Assert.All(decisions, d => Assert.Null(d.FrameStartTime));
        Assert.All(decisions, d => Assert.Null(d.ClockPpm));
    }

    /// <summary>How far (in ms) a frame start is from the nearest place a frame should start,
    /// given the recording's 1.0 s lead-in and a receiving clock <paramref name="ppm"/> fast.</summary>
    private static double MissMs(double frameStart, double ppm)
    {
        double stretch = 1 + ppm * 1e-6;
        double fromFirst = frameStart - 1.0 * stretch;
        return Math.IEEERemainder(fromFirst, SampleClock.FramePeriod * stretch) * 1000;
    }
}
