// HfChannel.cs - for the test bench: makes a signal sound as if it had come over an HF path.
//
// It uses the Watterson model, the one the ITU uses to test HF modems (ITU-R F.1487, the old
// CCIR 520): the signal arrives by two paths (two hops off the ionosphere), the second a little
// later than the first, and each path fades up and down by itself. How fast they fade is the
// "frequency spread": 0.1 Hz is a slow, gentle fade; 10 Hz is the quick "flutter" heard on polar
// and auroral paths. Each path's strength wanders like real fading does (the maths name is
// "Rayleigh fading with a Gaussian Doppler spectrum"); on average the two paths together are as
// strong as the signal sent, so a signal-to-noise ratio means the same with or without fading.
//
// Noise is added afterwards, with TestAudio.AddNoise.

using RFpix.Dsp;

namespace RFpix.Tests.Bench;

/// <summary>One kind of HF path: how much later the second path arrives, and how fast both fade.</summary>
/// <param name="Name">For the bench's tables.</param>
/// <param name="DelayMs">How much later the second path arrives, in milliseconds.</param>
/// <param name="SpreadHz">How fast the paths fade (the ITU's "frequency spread"); 0 for a steady
/// signal with only noise.</param>
public sealed record HfCondition(string Name, double DelayMs, double SpreadHz)
{
    /// <summary>A steady signal: noise only, no fading.</summary>
    public static HfCondition NoiseOnly { get; } = new("Noise only", 0, 0);

    /// <summary>The ITU's "good" mid-latitude conditions: 0.5 ms, 0.1 Hz.</summary>
    public static HfCondition Good { get; } = new("Good (0.5 ms, 0.1 Hz)", 0.5, 0.1);

    /// <summary>The ITU's "moderate" mid-latitude conditions: 1 ms, 0.5 Hz.</summary>
    public static HfCondition Moderate { get; } = new("Moderate (1 ms, 0.5 Hz)", 1, 0.5);

    /// <summary>The ITU's "poor" (disturbed) mid-latitude conditions: 2 ms, 1 Hz.</summary>
    public static HfCondition Poor { get; } = new("Poor (2 ms, 1 Hz)", 2, 1);

    /// <summary>The ITU's "flutter" (high-latitude) conditions: 0.5 ms, 10 Hz.</summary>
    public static HfCondition Flutter { get; } = new("Flutter (0.5 ms, 10 Hz)", 0.5, 10);

    /// <summary>All five, gentlest first.</summary>
    public static IReadOnlyList<HfCondition> All { get; } = new[] { NoiseOnly, Good, Moderate, Poor, Flutter };

    /// <summary>Fading with no echo: one path (two fading paths at the same delay add up to one), fading
    /// at 0.5 Hz like the moderate path. Separates what fading does from what echoes do. Not in All, so
    /// the HamDRM bench's tables and seeds stay as they were.</summary>
    public static HfCondition Flat { get; } = new("Flat fading (no echo, 0.5 Hz)", 0, 0.5);
}

public static class HfChannel
{
    /// <summary>The audio as heard over <paramref name="condition"/>. The same seed always gives the
    /// same fading, so different settings can be compared over exactly the same path.</summary>
    public static float[] Apply(float[] audio, HfCondition condition, int seed, int rate = 48000)
    {
        if (condition.SpreadHz <= 0) return (float[])audio.Clone();

        var (re, im) = Analytic(audio);
        int delay = (int)Math.Round(condition.DelayMs * rate / 1000);
        var random = new Random(seed);
        var first = new FadingPath(condition.SpreadHz, random);
        var second = new FadingPath(condition.SpreadHz, random);

        // Each path carries half the power, so together they average the signal's own strength.
        double half = Math.Sqrt(0.5);
        // The fading is slow next to the audio, so it's worked out every millisecond and drawn
        // straight in between (10 Hz flutter is still 100 points a fade).
        int step = rate / 1000;
        var output = new float[audio.Length];
        for (int start = 0; start < audio.Length; start += step)
        {
            var (a1, b1) = first.Gain(start / (double)rate);
            var (a2, b2) = second.Gain(start / (double)rate);
            var (c1, d1) = first.Gain((start + step) / (double)rate);
            var (c2, d2) = second.Gain((start + step) / (double)rate);
            int end = Math.Min(start + step, audio.Length);
            for (int i = start; i < end; i++)
            {
                double f = (i - start) / (double)step;
                double g1re = a1 + (c1 - a1) * f, g1im = b1 + (d1 - b1) * f;
                double g2re = a2 + (c2 - a2) * f, g2im = b2 + (d2 - b2) * f;
                // The real part of (path gain x analytic signal), for each path.
                double y = g1re * re[i] - g1im * im[i];
                int j = i - delay;
                if (j >= 0) y += g2re * re[j] - g2im * im[j];
                output[i] = (float)(half * y);
            }
        }
        return output;
    }

    /// <summary>For the bench's "perfect knowledge" checks: what the path did to tone frequency
    /// <paramref name="hz"/> over the samples from <paramref name="start"/> for <paramref name="length"/>
    /// (its average gain there, the same fading as Apply with the same seed). The receiver's FFT sees this
    /// on that tone, whatever else is sent; the rest (tones leaking into each other in fast fading) it sees
    /// as noise.</summary>
    public static Func<int, int, double, System.Numerics.Complex> AverageGain(HfCondition condition, int seed, int samples, int rate = 48000)
    {
        if (condition.SpreadHz <= 0) return (_, _, _) => 1;
        int delay = (int)Math.Round(condition.DelayMs * rate / 1000);
        var random = new Random(seed);
        var first = new FadingPath(condition.SpreadHz, random);
        var second = new FadingPath(condition.SpreadHz, random);
        int step = rate / 1000, points = samples / step + 2;
        var g1 = new System.Numerics.Complex[points];
        var g2 = new System.Numerics.Complex[points];
        for (int i = 0; i < points; i++)
        {
            var (a, b) = first.Gain(i * step / (double)rate);
            var (c, d) = second.Gain(i * step / (double)rate);
            g1[i] = new(a, b); g2[i] = new(c, d);
        }
        System.Numerics.Complex Mean(System.Numerics.Complex[] g, int from, int count)
        {
            System.Numerics.Complex sum = 0;
            for (int n = from; n < from + count; n++)
            {
                int i = Math.Clamp(n / step, 0, points - 2);
                double f = (n - i * step) / (double)step;
                sum += g[i] + (g[i + 1] - g[i]) * f;
            }
            return sum / count;
        }
        double half = Math.Sqrt(0.5);
        return (start, length, hz) => half * (Mean(g1, start, length) +
            Mean(g2, start, length) * System.Numerics.Complex.FromPolarCoordinates(1, -2 * Math.PI * hz * delay / rate));
    }

    /// <summary>The "analytic" form of the audio (the audio itself and the same shifted a quarter
    /// turn), so a path's gain can turn its phase as well as change its strength.</summary>
    private static (double[] Re, double[] Im) Analytic(float[] audio)
    {
        int n = 1;
        while (n < audio.Length) n *= 2;
        var re = new double[n];
        var im = new double[n];
        for (int i = 0; i < audio.Length; i++) re[i] = audio[i];
        Fft.Transform(re, im);
        // Keep the positive frequencies only (doubled), as TestAudio.ShiftFrequency does.
        for (int k = 1; k < n / 2; k++) { re[k] *= 2; im[k] *= 2; }
        for (int k = n / 2 + 1; k < n; k++) { re[k] = 0; im[k] = 0; }
        // Back again: the inverse FFT, done as a forward FFT of the complex conjugate.
        for (int k = 0; k < n; k++) im[k] = -im[k];
        Fft.Transform(re, im);
        for (int k = 0; k < n; k++) { re[k] /= n; im[k] = -im[k] / n; }
        return (re, im);
    }

    /// <summary>One fading path: its gain is the sum of many slowly turning pieces whose speeds are
    /// spread like a bell curve, which gives the wandering strength of real fading. (Its average
    /// power is 1.)</summary>
    private sealed class FadingPath
    {
        private const int Pieces = 64;
        private readonly double[] _speed = new double[Pieces];   // radians a second
        private readonly double[] _phase = new double[Pieces];

        public FadingPath(double spreadHz, Random random)
        {
            // The ITU's spread is twice the bell curve's "standard deviation".
            double sigma = spreadHz / 2;
            for (int p = 0; p < Pieces; p++)
            {
                double u1 = 1 - random.NextDouble(), u2 = random.NextDouble();
                double hz = sigma * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
                _speed[p] = 2 * Math.PI * hz;
                _phase[p] = 2 * Math.PI * random.NextDouble();
            }
        }

        public (double Re, double Im) Gain(double seconds)
        {
            double re = 0, im = 0;
            for (int p = 0; p < Pieces; p++)
            {
                double angle = _speed[p] * seconds + _phase[p];
                re += Math.Cos(angle);
                im += Math.Sin(angle);
            }
            double scale = 1 / Math.Sqrt(Pieces);
            return (re * scale, im * scale);
        }
    }
}
