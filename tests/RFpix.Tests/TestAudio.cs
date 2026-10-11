// TestAudio.cs - shared helpers for decoder tests: make noise, move a signal off
// frequency, and play audio through the HamDRM receiver recording what it decides.

using RFpix.Dsp;
using RFpix.HamDrm;

namespace RFpix.Tests;

/// <summary>One receiver decision, with when it happened (seconds into the audio).</summary>
public sealed record Decision(double Time, ModeDetection Detection, FrequencyLockResult? Lock);

public static class TestAudio
{
    /// <summary>Plays audio through a new receiver, 10 ms at a time like the sound card,
    /// and records every decision (about 5 a second).</summary>
    public static List<Decision> Run(float[] audio, int rate)
    {
        var receiver = new HamDrmReceiver();
        var decisions = new List<Decision>();
        int fed = 0;
        // The receiver measures the frequency before announcing a decision, so Lock is current here.
        receiver.Decided += d => decisions.Add(new Decision(fed / (double)rate, d, receiver.Lock));

        int chunk = rate / 100;
        for (int pos = 0; pos < audio.Length; pos += chunk)
        {
            int count = Math.Min(chunk, audio.Length - pos);
            fed = pos + count;
            receiver.AddSamples(audio[pos..(pos + count)], count, rate);
        }
        return decisions;
    }

    /// <summary>The decisions made between two times (in seconds).</summary>
    public static List<Decision> Between(this List<Decision> all, double from, double to) =>
        all.Where(d => d.Time >= from && d.Time <= to).ToList();

    /// <summary>Gaussian ("normal") noise, like real band noise.</summary>
    public static float[] Noise(int length, double level, int seed)
    {
        var random = new Random(seed);
        var noise = new float[length];
        for (int i = 0; i < length; i++)
        {
            double u1 = 1 - random.NextDouble(), u2 = random.NextDouble();
            noise[i] = (float)(level * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2));
        }
        return noise;
    }

    /// <summary>Adds noise to a 48 kHz recording so the signal-to-noise ratio, measured over
    /// the signal's 2.5 kHz bandwidth as an on-air SNR would be, is <paramref name="snrDb"/>.</summary>
    public static float[] AddNoise(float[] audio, double snrDb, int seed)
    {
        double signalPower = audio.Skip(48000).Take(audio.Length - 96000).Average(s => (double)s * s);
        double noisePowerInBand = signalPower / Math.Pow(10, snrDb / 10);
        double level = Math.Sqrt(noisePowerInBand * 24000 / 2500);   // white noise spreads over 0-24 kHz
        var noise = Noise(audio.Length, level, seed);
        return audio.Select((s, i) => s + noise[i]).ToArray();
    }

    /// <summary>Moves every frequency in the audio up (or down) by the same amount,
    /// as a receiver tuned slightly off would.</summary>
    public static float[] ShiftFrequency(float[] audio, double hz, int rate)
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

    /// <summary>The mode written in a recording's file name, e.g. "04_modeB_..." is mode B.</summary>
    public static HamDrmMode ModeFromFileName(string file) =>
        Enum.Parse<HamDrmMode>(file.Substring(file.IndexOf("mode") + 4, 1));
}
