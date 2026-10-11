// ModeDetectorTests.cs - checks that RFpix finds HamDRM signals and names their mode,
// and that it doesn't "find" one in silence or noise.
// The helpers (Run, Between, Noise, AddNoise, ShiftFrequency) are in TestAudio.cs.

using System.Numerics;
using RFpix.Audio;
using RFpix.Dsp;
using RFpix.HamDrm;
using Xunit;
using static RFpix.Tests.TestAudio;

namespace RFpix.Tests;

public class ModeDetectorTests
{
    // ---- the clean recordings ----

    [Theory]
    [MemberData(nameof(Recordings.All), MemberType = typeof(Recordings))]
    public void Names_the_right_mode_for_every_recording(string file)
    {
        var expected = ModeFromFileName(file);
        var wav = WavFile.Read(Recordings.Path(file));

        var decisions = Run(wav.Samples, wav.SampleRate);

        // While only the leading silence has been heard: no signal.
        Assert.All(decisions.Between(0, 0.9), d => Assert.Null(d.Detection.Mode));
        // Once the window is full of signal, every decision names the right mode.
        double end = wav.Duration.TotalSeconds;
        var during = decisions.Between(2.0, end - 1.2);
        Assert.NotEmpty(during);
        Assert.All(during, d => Assert.Equal(expected, d.Detection.Mode));
        Assert.All(during, d => Assert.InRange(d.Detection.Strength, 0.8, 1.0));
    }

    // ---- no signal ----

    [Fact]
    public void Silence_is_no_signal()
    {
        var decisions = Run(new float[48000 * 3], 48000);
        Assert.NotEmpty(decisions);
        Assert.All(decisions, d => Assert.Null(d.Detection.Mode));
    }

    [Theory]
    [InlineData(0.001)]   // very quiet hiss
    [InlineData(0.1)]     // ordinary band noise
    [InlineData(0.5)]     // loud noise
    public void Noise_is_no_signal(double level)
    {
        var decisions = Run(Noise(48000 * 10, level, seed: 1), 48000);
        Assert.All(decisions, d => Assert.Null(d.Detection.Mode));
    }

    [Fact]
    public void A_plain_tone_is_no_signal()
    {
        // A steady whistle repeats itself, but not with HamDRM's timing.
        var tone = new float[48000 * 5];
        for (int i = 0; i < tone.Length; i++) tone[i] = 0.5f * MathF.Sin(2 * MathF.PI * 1500 * i / 48000);
        Assert.All(Run(tone, 48000), d => Assert.Null(d.Detection.Mode));
    }

    // ---- real-world conditions ----

    [Theory]
    [InlineData("04_modeB_2p5khz_qam16_long_high_rs0.wav", 3)]   // noise 3 dB weaker than the signal
    [InlineData("07_modeE_2p5khz_qam4_long_high_rs0.wav", 0)]    // noise as strong as the signal
    public void Still_finds_a_weak_signal_in_noise(string file, double snrDb)
    {
        var wav = WavFile.Read(Recordings.Path(file));
        var expected = ModeFromFileName(file);
        var noisy = AddNoise(wav.Samples, snrDb, seed: 2);

        var during = Run(noisy, 48000).Between(2.0, wav.Duration.TotalSeconds - 1.2);
        double right = during.Count(d => d.Detection.Mode == expected) / (double)during.Count;
        Assert.True(right >= 0.95, $"right only {right:P0} of the time");
        Assert.DoesNotContain(during, d => d.Detection.Mode is not null && d.Detection.Mode != expected);
    }

    [Fact]
    public void Goes_back_to_no_signal_when_the_transmission_ends_and_only_noise_is_left()
    {
        // Recording 04, then 10 seconds of band noise as loud as the signal was.
        var wav = WavFile.Read(Recordings.Path("04_modeB_2p5khz_qam16_long_high_rs0.wav"));
        double signalLevel = Math.Sqrt(wav.Samples.Skip(48000).Take(48000 * 10).Average(s => (double)s * s));
        var audio = wav.Samples.Concat(Noise(48000 * 10, signalLevel, seed: 3)).ToArray();

        var decisions = Run(audio, 48000);
        double end = wav.Duration.TotalSeconds - 1.0;   // where the signal stopped

        Assert.All(decisions.Between(2.0, end - 0.2), d => Assert.Equal(HamDrmMode.B, d.Detection.Mode));
        // Within 1.5 s of the signal stopping, and from then on: no signal.
        Assert.All(decisions.Between(end + 1.5, end + 11), d => Assert.Null(d.Detection.Mode));
    }

    [Theory]
    [InlineData(-50)]
    [InlineData(25)]
    [InlineData(100)]
    public void Tuning_error_does_not_matter(double offsetHz)
    {
        // As if the radio were tuned a little off: every frequency in the signal moves.
        var wav = WavFile.Read(Recordings.Path("04_modeB_2p5khz_qam16_long_high_rs0.wav"));
        var shifted = ShiftFrequency(wav.Samples, offsetHz, wav.SampleRate);

        var during = Run(shifted, 48000).Between(2.0, wav.Duration.TotalSeconds - 1.2);
        Assert.All(during, d => Assert.Equal(HamDrmMode.B, d.Detection.Mode));
    }

    [Fact]
    public void Says_why_it_cannot_decode_an_unusual_sample_rate()
    {
        var receiver = new HamDrmReceiver();
        receiver.AddSamples(new float[4410], 4410, 44100);
        Assert.Contains("48000", receiver.StatusText);

        receiver.AddSamples(new float[4800], 4800, 48000);   // back to normal: problem gone
        Assert.Equal("No HamDRM signal", receiver.StatusText);
    }

    // ---- the downconverter on its own ----

    [Fact]
    public void Downconverter_keeps_the_HamDRM_band_and_removes_the_rest()
    {
        double Strength(double toneHz)
        {
            var tone = new float[48000];
            for (int i = 0; i < tone.Length; i++) tone[i] = MathF.Sin(2 * MathF.PI * (float)toneHz * i / 48000);
            var output = new List<Complex>();
            new Downconverter(48000).Process(tone, output);
            return output.Skip(1000).Average(c => c.Magnitude);   // skip the filter's start-up
        }

        // Inside the HamDRM band (350-2800 Hz): passed at full strength.
        foreach (double hz in new[] { 400.0, 1575, 2750 })
            Assert.InRange(Strength(hz), 0.97, 1.03);
        // Well outside it: removed (below 1% = -40 dB).
        foreach (double hz in new[] { 4000.0, 6000, 10000 })
            Assert.True(Strength(hz) < 0.01, $"{hz} Hz got through");
    }
}
