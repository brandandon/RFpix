// SubTones.cs - finds a steady low tone in the received sound, such as a repeater's sub-audible tone
// (CTCSS, 67 to 254 Hz) or mains hum, and removes it with a narrow notch, before HamDRM decoding.
//
// Why: FM radios often pass the repeater's tone out with the received sound (their data/USB audio isn't
// filtered below 300 Hz). Dan's recording through a repeater with a 123 Hz tone (Nov 17, 2026) had it
// about 10 dB below the picture, and only 49 of 70 pieces came through; tested, a 123 Hz tone 5 dB below
// a clean signal stopped decoding completely.
//
// Why a notch, not a high-pass filter: a high-pass filter steep enough to remove the tones also cut into
// the signal's lowest carriers when it was tuned low (on SSB, a signal 150 Hz low starts near 250 Hz)
// and cost signal quality even when there was no tone. The notch only switches on when a tone is
// there, and it's narrow (about 20 Hz wide), so with no tone the sound is untouched.
//
// How: once a second, the last second of sound is looked at with an FFT. A peak between 40 and 300 Hz
// that stands well clear of its surroundings and is no more than 25 dB weaker than all the sound gets a
// notch (at most two: a tone and hum). A notch that hasn't been needed for 5 seconds is removed.

namespace RFpix.Dsp;

public sealed class SubTones
{
    private const double Lowest = 40, Highest = 300;   // where tones are looked for (Hz)
    private const double NotchWidth = 20;               // Hz wide at half depth
    private const double MinFractionDb = -25;           // a tone weaker than this against all the sound is left
    private const double MinProminenceDb = 25;          // and it must stand this far above its surroundings
    private const int MaxNotches = 2;
    private const int ForgetAfter = 5;                  // seconds without the tone before its notch goes

    private readonly int _rate;
    private readonly float[] _block;
    private int _filled;
    private readonly List<Notch> _notches = new();

    /// <summary>The tones being removed now, in Hz (for the log and tests).</summary>
    public IReadOnlyList<double> Removing => _notches.Select(n => n.Hz).ToList();

    public SubTones(int sampleRate)
    {
        _rate = sampleRate;
        _block = new float[sampleRate];   // one second
    }

    /// <summary>One sample in, the same sample out with any tones found removed.</summary>
    public double Process(float sample)
    {
        _block[_filled++] = sample;
        if (_filled == _block.Length) { Look(); _filled = 0; }
        double x = sample;
        foreach (var notch in _notches) x = notch.Filter(x);
        return x;
    }

    /// <summary>Looks at the last second for tones, and updates the notches.</summary>
    private void Look()
    {
        int n = 1;
        while (n < _block.Length) n *= 2;
        var re = new double[n];
        var im = new double[n];
        double total = 0;
        for (int i = 0; i < _block.Length; i++)
        {
            // A Hann window, so a tone's energy stays in its own few bins.
            double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (_block.Length - 1));
            re[i] = _block[i] * w;
            total += (double)_block[i] * _block[i];
        }
        Fft.Transform(re, im);
        double binHz = (double)_rate / n;
        var power = new double[n / 2];
        double windowed = 0;
        for (int k = 0; k < n / 2; k++) { power[k] = re[k] * re[k] + im[k] * im[k]; windowed += power[k]; }
        if (total <= 0 || windowed <= 0) { Age(found: Array.Empty<double>()); return; }

        // Peaks in 40-300 Hz, strongest first, that are tones: well above their surroundings and strong
        // enough against all the sound to matter.
        int lo = (int)(Lowest / binHz), hi = (int)(Highest / binHz);
        var found = new List<double>();
        var candidates = Enumerable.Range(lo + 1, hi - lo - 1)
            .Where(k => power[k] >= power[k - 1] && power[k] > power[k + 1])
            .OrderByDescending(k => power[k]);
        foreach (int k in candidates)
        {
            if (found.Count == MaxNotches) break;
            // The tone's power (its bin and the two either side) against the whole sound.
            double tone = power[k - 1] + power[k] + power[k + 1];
            if (10 * Math.Log10(tone / windowed) < MinFractionDb) break;   // the rest are weaker still
            // Its surroundings: the typical (middle) bin from 30 to 60 Hz either side.
            int width = (int)(30 / binHz), far = (int)(60 / binHz);
            var around = Enumerable.Range(k - far, 2 * far + 1)
                .Where(j => Math.Abs(j - k) >= width && j > 0 && j < power.Length)
                .Select(j => power[j]).Order().ToArray();
            double typical = around.Length > 0 ? around[around.Length / 2] : 0;
            if (typical > 0 && 10 * Math.Log10(power[k] / typical) < MinProminenceDb) continue;
            // Its exact frequency, from the shape of the peak (between bins).
            double a = Math.Log(power[k - 1] + 1e-30), b = Math.Log(power[k] + 1e-30), c = Math.Log(power[k + 1] + 1e-30);
            double shift = 0.5 * (a - c) / (a - 2 * b + c);
            found.Add((k + (double.IsFinite(shift) ? Math.Clamp(shift, -0.5, 0.5) : 0)) * binHz);
        }
        Age(found);
    }

    /// <summary>Keeps the notches for tones still there (moving each to its tone's frequency if it drifted
    /// a little), adds new ones, and forgets those not needed for a while.</summary>
    private void Age(IReadOnlyList<double> found)
    {
        foreach (var notch in _notches) notch.Unseen++;
        foreach (double hz in found)
        {
            var near = _notches.FirstOrDefault(nt => Math.Abs(nt.Hz - hz) < NotchWidth / 4);
            if (near is not null) { near.Unseen = 0; continue; }
            if (_notches.Count < MaxNotches) _notches.Add(new Notch(hz, _rate));
        }
        _notches.RemoveAll(nt => nt.Unseen > ForgetAfter);
    }

    /// <summary>A narrow notch: a second-order IIR filter (the RBJ "cookbook" notch).</summary>
    private sealed class Notch
    {
        private readonly double _b0, _b1, _b2, _a1, _a2;
        private double _x1, _x2, _y1, _y2;
        public double Hz { get; }
        public int Unseen;

        public Notch(double hz, int rate)
        {
            Hz = hz;
            double w = 2 * Math.PI * hz / rate, alpha = Math.Sin(w) / (2 * (hz / NotchWidth)), a0 = 1 + alpha;
            _b0 = 1 / a0; _b1 = -2 * Math.Cos(w) / a0; _b2 = 1 / a0;
            _a1 = -2 * Math.Cos(w) / a0; _a2 = (1 - alpha) / a0;
        }

        public double Filter(double x)
        {
            double y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1; _x1 = x; _y2 = _y1; _y1 = y;
            return y;
        }
    }
}
