// SoundCardClock.cs - measuring a sound card's clock, and correcting for it.
//
// A sound card counts time with its own crystal. "48000 samples a second" is really 48000
// and a little: a typical USB sound card is off by somewhere between a few and a few
// hundred parts per million (ppm). HamDRM is sensitive to this: a transmission 100 ppm
// fast runs 12 ms ahead after its 2 minutes, and its tones sit 0.01% high. RFpix's own
// receiver follows such drift, but other stations' EasyPal may not.
//
// Measuring: count the samples the sound card delivers (or takes) against the computer's
// clock, which Windows keeps right by checking an internet time server. Over a couple of
// minutes, the best straight line through (time, samples) gives the true sample rate;
// the line's wobble says how sure the result is.
//
// Correcting: re-time the sound by that much. Coming in, a card that runs 20 ppm fast
// delivers 20 ppm too many samples a second, so every 1 000 000 become 999 980. Going out,
// one that runs fast takes 20 ppm too many, so the sound is stretched to match. Re-timing
// works out each new sample between the old ones with a smooth curve through four of them
// (a "cubic" interpolation), cleanly enough for 64-QAM.

namespace RFpix.Audio;

/// <summary>Collects (time, samples) readings and works out the true sample rate.</summary>
public sealed class ClockMeasurement
{
    private readonly object _lock = new();
    private int _n;
    private double _sumT, _sumS, _sumTT, _sumTS, _sumSS;
    private double? _t0;
    private long _s0;

    public ClockMeasurement(int nominalRate) => NominalRate = nominalRate;

    public int NominalRate { get; }

    /// <summary>Adds a reading: at this moment (seconds, on the computer's clock), this many samples so far.</summary>
    public void Add(double seconds, long samples)
    {
        lock (_lock)
        {
            // Measured from the first reading, so the sums stay small and exact.
            _t0 ??= seconds;
            if (_n == 0) _s0 = samples;
            double t = seconds - _t0.Value, s = samples - _s0;
            _n++;
            _sumT += t; _sumS += s; _sumTT += t * t; _sumTS += t * s; _sumSS += s * s;
            Seconds = t;
        }
    }

    /// <summary>Readings so far, and how long they span.</summary>
    public int Count { get { lock (_lock) return _n; } }
    public double Seconds { get; private set; }

    /// <summary>The true sample rate (samples per second), or null with too few readings.</summary>
    public double? Rate
    {
        get
        {
            lock (_lock)
            {
                double d = _n * _sumTT - _sumT * _sumT;
                return _n < 10 || d <= 0 ? null : (_n * _sumTS - _sumT * _sumS) / d;
            }
        }
    }

    /// <summary>How far off the nominal rate it is, in parts per million (+ = fast).</summary>
    public double? Ppm => Rate is double r ? (r / NominalRate - 1) * 1e6 : null;

    /// <summary>How sure the answer is, in ppm (one standard error of the line's slope).</summary>
    public double? UncertaintyPpm
    {
        get
        {
            lock (_lock)
            {
                if (_n < 10) return null;
                double d = _n * _sumTT - _sumT * _sumT;
                if (d <= 0) return null;
                double slope = (_n * _sumTS - _sumT * _sumS) / d;
                double intercept = (_sumS - slope * _sumT) / _n;
                // The leftover wobble around the line, from the sums.
                double residual = _sumSS - 2 * slope * _sumTS - 2 * intercept * _sumS + slope * slope * _sumTT
                                  + 2 * slope * intercept * _sumT + _n * intercept * intercept;
                double variance = Math.Max(0, residual) / (_n - 2);
                double slopeError = Math.Sqrt(variance * _n / d);
                return slopeError / NominalRate * 1e6;
            }
        }
    }
}

/// <summary>Re-times a stream of sound by a tiny amount: <paramref name="ratio"/> samples out
/// for every sample in (0.99998 for a card 20 ppm fast, coming in).</summary>
public sealed class ClockCorrector
{
    private readonly double _step;                  // input samples per output sample
    private readonly float[] _history = new float[3];
    private double _position = 3;                   // of the next output, counting the history as 0-2
    private float[] _work = Array.Empty<float>();

    public ClockCorrector(double ratio)
    {
        if (ratio is < 0.99 or > 1.01) throw new ArgumentOutOfRangeException(nameof(ratio), "Clock corrections are tiny: within 1%.");
        Ratio = ratio;
        _step = 1 / ratio;
    }

    public double Ratio { get; }

    /// <summary>The ratio for sound coming IN from a card <paramref name="ppm"/> fast (or slow, if negative).</summary>
    public static double InputRatio(double ppm) => 1 / (1 + ppm * 1e-6);

    /// <summary>The ratio for sound going OUT to a card <paramref name="ppm"/> fast (or slow).</summary>
    public static double OutputRatio(double ppm) => 1 + ppm * 1e-6;

    /// <summary>Re-times the next chunk; the new samples are added to <paramref name="output"/>.</summary>
    public void Process(ReadOnlySpan<float> input, List<float> output)
    {
        int length = input.Length + 3;
        if (_work.Length < length) _work = new float[length];
        _history.CopyTo(_work, 0);
        input.CopyTo(_work.AsSpan(3));

        // Each new sample from the four old ones around it (a Catmull-Rom curve).
        while ((int)_position + 2 <= length - 1)
        {
            int i = (int)_position;
            float t = (float)(_position - i);
            float a = _work[i - 1], b = _work[i], c = _work[i + 1], d = _work[i + 2];
            output.Add(b + 0.5f * t * (c - a + t * (2 * a - 5 * b + 4 * c - d + t * (3 * (b - c) + d - a))));
            _position += _step;
        }
        // Keep the last three samples for next time, and count from them.
        _work.AsSpan(length - 3, 3).CopyTo(_history);
        _position -= length - 3;
    }

    /// <summary>Re-times a whole sound at once.</summary>
    public static float[] Resample(float[] sound, double ratio)
    {
        var corrector = new ClockCorrector(ratio);
        var output = new List<float>((int)(sound.Length * ratio) + 8);
        corrector.Process(sound, output);
        corrector.Process(new float[3], output);   // let the last samples through
        int wanted = (int)Math.Round(sound.Length * ratio);
        if (output.Count > wanted) output.RemoveRange(wanted, output.Count - wanted);
        return output.ToArray();
    }
}
