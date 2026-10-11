// Downconverter.cs - turns the radio's audio into the form the HamDRM decoder works on.
//
// The sound card gives us 48 000 numbers a second, and the HamDRM signal sits
// somewhere between about 350 and 2800 Hz. The decoder doesn't need all that.
// Like QSSTV, it works at 12 000 samples a second on a "complex" signal, which
// is a pair of numbers per sample (called I and Q) instead of one. Complex
// samples carry both the size and the direction of each wave's swing, so
// frequencies above and below the centre can be told apart. That's what makes
// the later maths simple and immune to small tuning errors.
//
// Three steps, for every incoming sample:
//   1. Mix: multiply by a 1575 Hz wave going "backwards", which slides the
//      HamDRM band down so its middle sits at 0 Hz (it now spans about -1250 to +1250).
//   2. Filter: a low-pass filter removes everything further than about 1.5 kHz
//      from the middle (other signals, hum, the copy the mixing creates).
//   3. Keep only every 4th sample (48 000 -> 12 000). Safe, because after the
//      filter nothing is left that needs the higher rate.

using System.Numerics;

namespace RFpix.Dsp;

public sealed class Downconverter
{
    /// <summary>Samples per second coming out.</summary>
    public const int OutputRate = 12000;

    /// <summary>The audio frequency that ends up at 0 Hz: the middle of a HamDRM signal.</summary>
    public const double CentreFrequency = 1575;

    // The filter passes up to about 1300 Hz either side of the middle (the signal
    // needs 1250) and blocks everything beyond about 1900 Hz. That edge must be
    // sharp: mixing a real sound also makes a mirror copy of it on the other side
    // of 0 Hz, and for the lowest part of the signal that mirror starts at -1925 Hz.
    private const double FilterCutoff = 1600;      // Hz from the middle, halfway down the edge
    private const int TapsAt48kHz = 441;           // more taps = a sharper edge, but more work

    private readonly int _filterLength;
    private readonly int _step;                    // input samples per output sample (4 at 48 kHz)
    private readonly double[] _taps;
    private readonly Complex[] _history;
    private int _historyPos;                       // where the next mixed sample goes
    private int _sinceOutput;
    private readonly Complex _rotate;              // turns the mixing wave a little each sample
    private Complex _phase = Complex.One;
    private int _renormalise;

    public int InputRate { get; }

    /// <summary>Is this sound card rate one we can convert exactly? (12, 24, 48, 96 kHz...)</summary>
    public static bool Supports(int inputRate) => inputRate >= OutputRate && inputRate % OutputRate == 0;

    public Downconverter(int inputRate)
    {
        if (!Supports(inputRate))
            throw new ArgumentException($"HamDRM decoding needs a sound rate that's a multiple of 12000 Hz, not {inputRate} Hz.");

        InputRate = inputRate;
        _step = inputRate / OutputRate;
        // Keep the edge equally sharp at any rate: more samples per second need more taps.
        _filterLength = (int)((long)TapsAt48kHz * inputRate / 48000) | 1;   // always an odd number
        _history = new Complex[_filterLength];
        _rotate = Complex.FromPolarCoordinates(1, -2 * Math.PI * CentreFrequency / inputRate);

        // Design the low-pass filter: a "windowed sinc", the textbook recipe.
        // The sinc shape passes everything below the cutoff; the Blackman window
        // tapers its ends so the filter doesn't ring.
        _taps = new double[_filterLength];
        double fc = FilterCutoff / inputRate;
        int mid = _filterLength / 2;
        double sum = 0;
        for (int i = 0; i < _filterLength; i++)
        {
            int n = i - mid;
            double sinc = n == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * n) / (Math.PI * n);
            double window = 0.42 - 0.5 * Math.Cos(2 * Math.PI * i / (_filterLength - 1))
                                 + 0.08 * Math.Cos(4 * Math.PI * i / (_filterLength - 1));
            _taps[i] = sinc * window;
            sum += _taps[i];
        }
        for (int i = 0; i < _filterLength; i++) _taps[i] /= sum;   // pass the middle at full strength
    }

    /// <summary>Converts a chunk of audio. Converted samples are added to <paramref name="output"/>.</summary>
    public void Process(ReadOnlySpan<float> input, List<Complex> output)
    {
        foreach (float s in input)
        {
            // 1. Mix.
            _history[_historyPos] = s * _phase;
            _historyPos = (_historyPos + 1) % _filterLength;
            _phase *= _rotate;
            if (++_renormalise == 4096)
            {
                // Rounding errors slowly change the wave's size; put it back to exactly 1.
                _phase /= _phase.Magnitude;
                _renormalise = 0;
            }

            // 2 and 3. Filter, but only work out the samples we keep.
            if (++_sinceOutput < _step) continue;
            _sinceOutput = 0;

            Complex acc = Complex.Zero;
            int h = _historyPos;   // oldest sample
            for (int i = 0; i < _filterLength; i++)
            {
                acc += _history[h] * _taps[i];
                if (++h == _filterLength) h = 0;
            }
            output.Add(acc * 2);   // x2 because mixing splits the signal's strength into two halves
        }
    }
}
