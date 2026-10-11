// ModeDetectorTests.cs - checks that RFpix finds HamDRM signals and names their mode,
// and that it doesn't "find" one in silence or noise.

using System.Numerics;
using RFpix.Audio;
using RFpix.Dsp;
using RFpix.HamDrm;
using Xunit;

namespace RFpix.Tests;

public class ModeDetectorTests
{
    // ---- the clean recordings ----

    [Theory]
    [MemberData(nameof(Recordings.All), MemberType = typeof(Recordings))]
    public void Names_the_right_mode_for_every_recording(string file)
    {
        // The mode is in the file name, e.g. "04_modeB_..." is mode B.
        var expected = Enum.Parse<HamDrmMode>(file.Substring(file.IndexOf("mode") + 4, 1));
        var wav = WavFile.Read(Recordings.Path(file));

        var decisions = Run(wav.Samples, wav.SampleRate);

        // While only the leading silence has been heard: no signal.
        Assert.All(DecisionsBetween(decisions, 0, 0.9), d => Assert.Null(d.Mode));
        // Once the window is full of signal, every decision names the right mode.
        double end = wav.Duration.TotalSeconds;
        var during = DecisionsBetween(decisions, 2.0, end - 1.2);
        Assert.NotEmpty(during);
        Assert.All(during, d => Assert.Equal(expected, d.Mode));
        Assert.All(during, d => Assert.InRange(d.Strength, 0.8, 1.0));
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
        var expected = Enum.Parse<HamDrmMode>(file.Substring(file.IndexOf("mode") + 4, 1));

        // Add noise, measured over the signal's own 2.5 kHz bandwidth, as an on-air SNR would be.
        double signalPower = wav.Samples.Skip(48000).Take(wav.Samples.Length - 96000).Average(s => (double)s * s);
        double noisePowerInBand = signalPower / Math.Pow(10, snrDb / 10);
        double noiseLevel = Math.Sqrt(noisePowerInBand * 24000 / 2500);   // white noise spreads over 0-24 kHz
        var noise = Noise(wav.Samples.Length, noiseLevel, seed: 2);
        var noisy = wav.Samples.Select((s, i) => s + noise[i]).ToArray();

        var during = DecisionsBetween(Run(noisy, 48000), 2.0, wav.Duration.TotalSeconds - 1.2);
        double right = during.Count(d => d.Mode == expected) / (double)during.Count;
        Assert.True(right >= 0.95, $"right only {right:P0} of the time");
        Assert.DoesNotContain(during, d => d.Mode is not null && d.Mode != expected);
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

        Assert.All(DecisionsBetween(decisions, 2.0, end - 0.2), d => Assert.Equal(HamDrmMode.B, d.Mode));
        // Within 1.5 s of the signal stopping, and from then on: no signal.
        Assert.All(DecisionsBetween(decisions, end + 1.5, end + 11), d => Assert.Null(d.Mode));
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

        var during = DecisionsBetween(Run(shifted, 48000), 2.0, wav.Duration.TotalSeconds - 1.2);
        Assert.All(during, d => Assert.Equal(HamDrmMode.B, d.Mode));
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

    // ---- helpers ----

    /// <summary>Runs audio through the receiver and records every decision with its time in seconds.</summary>
    private static List<(double Time, ModeDetection Detection)> Run(float[] audio, int rate)
    {
        var receiver = new HamDrmReceiver();
        var decisions = new List<(double, ModeDetection)>();
        int fed = 0;
        receiver.Decided += d => decisions.Add((fed / (double)rate, d));

        int chunk = rate / 100;   // 10 ms at a time, like the sound card
        for (int pos = 0; pos < audio.Length; pos += chunk)
        {
            int count = Math.Min(chunk, audio.Length - pos);
            fed = pos + count;
            receiver.AddSamples(audio[pos..(pos + count)], count, rate);
        }
        return decisions;
    }

    private static List<ModeDetection> DecisionsBetween(List<(double Time, ModeDetection Detection)> all, double from, double to) =>
        all.Where(d => d.Time >= from && d.Time <= to).Select(d => d.Detection).ToList();

    private static float[] Noise(int length, double level, int seed)
    {
        var random = new Random(seed);
        var noise = new float[length];
        for (int i = 0; i < length; i++)
        {
            // Gaussian ("normal") noise, like real band noise, from two uniform random numbers.
            double u1 = 1 - random.NextDouble(), u2 = random.NextDouble();
            noise[i] = (float)(level * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2));
        }
        return noise;
    }

    /// <summary>Moves every frequency in the audio up (or down) by the same amount.</summary>
    private static float[] ShiftFrequency(float[] audio, double hz, int rate)
    {
        // Turn the audio into its "analytic" form with an FFT (keep only positive
        // frequencies), rotate it, and take the real part back.
        int n = 1;
        while (n < audio.Length) n *= 2;
        var re = new double[n];
        var im = new double[n];
        for (int i = 0; i < audio.Length; i++) re[i] = audio[i];
        Fft.Transform(re, im);
        for (int k = 1; k < n / 2; k++) { re[k] *= 2; im[k] *= 2; }
        for (int k = n / 2 + 1; k < n; k++) { re[k] = 0; im[k] = 0; }
        // Inverse FFT, done as a forward FFT of the complex conjugate.
        for (int k = 0; k < n; k++) im[k] = -im[k];
        Fft.Transform(re, im);

        var output = new float[audio.Length];
        for (int i = 0; i < audio.Length; i++)
        {
            double a = re[i] / n, b = -im[i] / n;   // the analytic signal at sample i
            double angle = 2 * Math.PI * hz * i / rate;
            output[i] = (float)(a * Math.Cos(angle) - b * Math.Sin(angle));
        }
        return output;
    }
}
