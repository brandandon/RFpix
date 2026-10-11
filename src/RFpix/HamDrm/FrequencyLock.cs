// FrequencyLock.cs - works out how far off frequency the HamDRM signal is.
//
// A HamDRM signal is made of many carriers (single tones), evenly spaced:
// 41.7 Hz apart in mode A, 46.9 Hz in B, 75 Hz in E. When the receiving radio
// is tuned a little off, every carrier moves by the same amount. The decoder
// needs to know that amount to within a fraction of a hertz, or the carriers
// blur into each other. We find it in two parts:
//
// 1. The fine part (less than one carrier spacing) comes free from the mode
//    detector. A guard and its copy are the same sound one useful-length apart,
//    so the only difference between them is how far each carrier has turned in
//    that time. That angle gives the frequency, but only "modulo" one spacing:
//    +10 Hz and +10 Hz plus one whole spacing look identical.
//
// 2. The coarse part (how many whole spacings) comes from three steady pilot
//    tones every HamDRM signal carries at 725, 1475 and 1850 Hz. Data carriers
//    jump to a new phase every symbol; pilots carry on smoothly. So for each
//    guess at the tuning error we look where the pilots should be and measure
//    how steadily their phase moves from one symbol to the next. The right
//    guess finds three perfectly steady tones; wrong guesses find jumpy data.
//
// QSSTV does the same (getfoffsint.cpp, by PA0MBO), rewritten.

using System.Numerics;
using RFpix.Dsp;

namespace RFpix.HamDrm;

/// <param name="OffsetHz">How far the signal is from where it should be, in Hz. Positive means
/// it arrived higher than sent (e.g. +25 Hz: the pilot sent at 725 Hz is heard at 750 Hz).</param>
/// <param name="PilotQuality">How steady the three pilot tones were, 0 to 1. A clean signal gives 1.</param>
public sealed record FrequencyLockResult(double OffsetHz, double PilotQuality)
{
    /// <summary>Below this the pilots couldn't be picked out, so the offset isn't trustworthy.</summary>
    public const double MinimumQuality = 0.5;

    public bool IsLocked => PilotQuality >= MinimumQuality;
}

public static class FrequencyLock
{
    /// <summary>The largest tuning error searched for, either way. Radios are normally
    /// within a few hertz; 200 Hz allows for old or drifting rigs.</summary>
    public const double MaxOffsetHz = 200;

    /// <summary>The three steady pilot tones, in Hz, as sent (all three modes use the same ones).</summary>
    public static readonly double[] PilotFrequencies = { 725, 1475, 1850 };

    /// <summary>HamDRM carriers sit at this frequency plus whole carrier spacings (QSSTV's 350 Hz offset).</summary>
    public const double CarrierGridStart = 350;

    private const double Rate = Downconverter.OutputRate;

    /// <summary>Measures the tuning error.</summary>
    /// <param name="window">12 kHz complex samples from the <see cref="Downconverter"/>.</param>
    /// <param name="detection">The mode detector's decision on that same window.</param>
    public static FrequencyLockResult? Measure(ReadOnlySpan<Complex> window, ModeDetection detection)
    {
        if (detection.Mode is not { } mode) return null;
        int tu = HamDrmModes.UsefulLength(mode);
        int tg = HamDrmModes.GuardLength(mode);
        int ts = HamDrmModes.SymbolLength(mode);
        double spacing = Rate / tu;

        // Part 1: the fine offset from the guard's angle. A wave at frequency f turns
        // by f x (useful length) whole circles between a guard and its copy.
        double measured = -detection.GuardPhase * Rate / (2 * Math.PI * tu);
        // A perfectly tuned signal also gives an angle, because the carriers don't sit
        // exactly on whole spacings from the downconverter's 1575 Hz centre. Remove it.
        double perfect = CarrierGridStart - Downconverter.CentreFrequency;
        double fine = Wrap(measured - perfect, spacing);

        // Where each symbol's useful part starts, within the window.
        var starts = new List<int>();
        for (int s = detection.SymbolStart + tg; s + tu <= window.Length; s += ts) starts.Add(s);
        if (starts.Count < 3) return null;

        // Part 2: try every whole number of spacings within reach.
        int reach = (int)Math.Ceiling(MaxOffsetHz / spacing);
        double bestQuality = -1, bestOffset = 0;
        for (int n = -reach; n <= reach; n++)
        {
            double offset = fine + n * spacing;
            if (Math.Abs(offset) > MaxOffsetHz + spacing / 2) continue;

            double quality = 0;
            foreach (double pilot in PilotFrequencies)
            {
                // Where this pilot would be after the downconverter, if the guess is right.
                double heardAt = pilot + offset - Downconverter.CentreFrequency;
                quality += Steadiness(window, starts, tu, heardAt);
            }
            quality /= PilotFrequencies.Length;

            if (quality > bestQuality) { bestQuality = quality; bestOffset = offset; }
        }
        return new FrequencyLockResult(bestOffset, Math.Max(0, bestQuality));
    }

    /// <summary>How steadily a tone at <paramref name="hz"/> moves from symbol to symbol, 0 to 1.</summary>
    private static double Steadiness(ReadOnlySpan<Complex> window, List<int> starts, int tu, double hz)
    {
        // Measure the tone in each symbol (one "bin" of a Fourier transform)...
        var step = Complex.FromPolarCoordinates(1, -2 * Math.PI * hz / Rate);
        Complex previous = Complex.Zero, steadiness = Complex.Zero;
        for (int i = 0; i < starts.Count; i++)
        {
            Complex sum = Complex.Zero, turn = Complex.One;
            for (int n = 0; n < tu; n++)
            {
                sum += window[starts[i] + n] * turn;
                turn *= step;
            }
            // ...and how much it turned since the last symbol. Only the direction of
            // that change counts (each is scaled to length 1), so a strong data carrier
            // can't outvote a steady pilot.
            if (i > 0)
            {
                Complex change = sum * Complex.Conjugate(previous);
                if (change.Magnitude > 1e-12) steadiness += change / change.Magnitude;
            }
            previous = sum;
        }
        // A steady tone turns by the same amount every time, so the changes all point
        // the same way and add up to their count. Random data changes cancel out.
        return steadiness.Magnitude / (starts.Count - 1);
    }

    /// <summary>Brings a frequency into the range -spacing/2 to +spacing/2.</summary>
    private static double Wrap(double hz, double spacing) =>
        hz - spacing * Math.Round(hz / spacing);
}
