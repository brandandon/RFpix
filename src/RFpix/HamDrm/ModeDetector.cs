// ModeDetector.cs - finds a HamDRM signal and works out which mode it uses.
//
// The trick: each symbol's guard is a copy of the end of that same symbol.
// So if we take the incoming sound and compare it with itself a fixed time
// later, it matches over the guard, but only when that time equals the mode's
// useful length, and only at the moments where guards are.
//
// For each mode we:
//   1. Multiply every sample by the sample one useful-length later (in a way
//      that measures "how alike are these two?").
//   2. Add those up over a guard's length. Over a real guard they all agree
//      and the total grows; over noise or data they cancel out.
//   3. Stack the results symbol after symbol, so twenty or thirty guards add
//      up at the same spot and stand out clearly.
//   4. Divide by the signal's strength, giving a score from 0 (nothing alike)
//      to 1 (a perfect match).
// The mode with the highest score wins, if its score is high enough.
// Where the peak sits also tells us where each symbol starts, needed later.
//
// This is the same method QSSTV uses (its getmode.cpp, by PA0MBO), rewritten.

using System.Numerics;

namespace RFpix.HamDrm;

/// <summary>What the detector currently thinks it hears.</summary>
/// <param name="Mode">The HamDRM mode, or null if there's no HamDRM signal.</param>
/// <param name="Strength">How clearly the mode was recognised, 0 to 1.</param>
/// <param name="SymbolStart">Where a symbol (its guard) starts within the detector's window, in 12 kHz samples.</param>
/// <param name="GuardPhase">The angle (in radians) of the guard match. A guard and its copy
/// differ only by how far each wave has turned in between, so this angle reveals the
/// signal's frequency to within one carrier spacing. Used by <see cref="FrequencyLock"/>.</param>
public sealed record ModeDetection(HamDrmMode? Mode, double Strength, int SymbolStart, double GuardPhase = 0)
{
    public static readonly ModeDetection None = new(null, 0, 0);
}

public sealed class ModeDetector
{
    /// <summary>How much sound each decision looks at: 0.8 s, about 30 to 40 symbols.</summary>
    public const int WindowLength = 9600;

    /// <summary>How often a fresh decision is made: every 0.2 s.</summary>
    public const int DecisionInterval = 2400;

    // A mode is announced when its score reaches FoundScore and clearly beats
    // the others; it's dropped when it falls below LostScore. The gap between
    // the two stops the display flickering when a signal is weak.
    // Measured: a clean signal scores 0.99-1.00; with noise as strong as the signal,
    // about 0.5; noise alone up to about 0.18. So a signal is found down to an SNR
    // of about -3 dB, before it's strong enough to decode, which is what we want.
    public const double FoundScore = 0.35;
    public const double LostScore = 0.25;

    // Incoming samples go round and round this "ring", each new one replacing
    // the oldest. At each decision they're copied out, oldest first, into _window.
    private readonly Complex[] _ring = new Complex[WindowLength];
    private int _ringPos;
    private readonly Complex[] _window = new Complex[WindowLength];
    private int _filled;
    private int _sinceDecision;

    // Below this average strength (about -90 dB) the input counts as silence.
    private const double SilencePower = 1e-9;

    /// <summary>The latest decision.</summary>
    public ModeDetection Current { get; private set; } = ModeDetection.None;

    /// <summary>The 0.8 s of sound the latest decision was made on, oldest sample first.
    /// Only valid until the next decision.</summary>
    public ReadOnlySpan<Complex> Window => _window;

    /// <summary>How many samples have been fed in since the last reset. At a decision,
    /// the window covers the last <see cref="WindowLength"/> of them.</summary>
    public long SamplesSeen { get; private set; }

    /// <summary>Each mode's score at the latest decision (for tests and troubleshooting).</summary>
    public IReadOnlyDictionary<HamDrmMode, double> Scores => _scores;
    private readonly Dictionary<HamDrmMode, double> _scores = new();

    /// <summary>Raised whenever a new decision is made.</summary>
    public event Action<ModeDetection>? Decided;

    /// <summary>Feed in 12 kHz complex samples from the <see cref="Dsp.Downconverter"/>.</summary>
    public void AddSamples(IReadOnlyList<Complex> samples)
    {
        foreach (var s in samples)
        {
            _ring[_ringPos] = s;
            SamplesSeen++;
            if (++_ringPos == WindowLength) _ringPos = 0;
            if (_filled < WindowLength) _filled++;

            if (++_sinceDecision >= DecisionInterval && _filled == WindowLength)
            {
                _sinceDecision = 0;
                Decide();
            }
        }
    }

    /// <summary>Forget everything heard so far (e.g. when switching to another sound source).</summary>
    public void Reset()
    {
        _filled = 0;
        _ringPos = 0;
        SamplesSeen = 0;
        _sinceDecision = 0;
        Current = ModeDetection.None;
        _scores.Clear();
    }

    private void Decide()
    {
        // Unroll the ring into the window, oldest sample first.
        Array.Copy(_ring, _ringPos, _window, 0, WindowLength - _ringPos);
        Array.Copy(_ring, 0, _window, WindowLength - _ringPos, _ringPos);

        double averagePower = 0;
        foreach (var s in _window) averagePower += Power(s);
        averagePower /= WindowLength;
        if (averagePower < SilencePower)
        {
            foreach (var mode in HamDrmModes.All) _scores[mode] = 0;
            Current = ModeDetection.None;
            Decided?.Invoke(Current);
            return;
        }

        HamDrmMode best = HamDrmMode.A;
        double bestScore = -1, secondScore = 0;
        int bestStart = 0;
        double bestPhase = 0;

        foreach (var mode in HamDrmModes.All)
        {
            var (score, start, phase) = Score(mode);
            _scores[mode] = score;
            if (score > bestScore)
            {
                secondScore = Math.Max(secondScore, bestScore);
                best = mode; bestScore = score; bestStart = start; bestPhase = phase;
            }
            else secondScore = Math.Max(secondScore, score);
        }

        bool alreadyLocked = Current.Mode == best;
        bool found = bestScore >= FoundScore && bestScore >= 2 * secondScore;
        bool stillThere = alreadyLocked && bestScore >= LostScore;

        Current = found || stillThere
            ? new ModeDetection(best, bestScore, bestStart, bestPhase)
            : ModeDetection.None;
        Decided?.Invoke(Current);
    }

    /// <summary>How well the sound matches one mode's timing, 0 to 1, where its symbols
    /// start, and the angle of the guard match there.</summary>
    private (double Score, int Start, double Phase) Score(HamDrmMode mode)
    {
        int tu = HamDrmModes.UsefulLength(mode);
        int tg = HamDrmModes.GuardLength(mode);
        int ts = HamDrmModes.SymbolLength(mode);
        int last = WindowLength - tu - tg;   // last position where a whole guard comparison fits

        // Steps 1 and 2: running totals over a guard's length.
        // likeness = sample x (the sample one useful-length later, "conjugated")
        // energy   = how strong the two samples are, to compare against.
        var stackedLikeness = new Complex[ts];
        var stackedEnergy = new double[ts];

        Complex likeness = Complex.Zero;
        double energy = 0;
        for (int k = 0; k < tg; k++)
        {
            likeness += _window[k] * Complex.Conjugate(_window[k + tu]);
            energy += Power(_window[k]) + Power(_window[k + tu]);
        }

        for (int t = 0; t <= last; t++)
        {
            // Step 3: add this position's totals onto the stack for its place within a symbol.
            stackedLikeness[t % ts] += likeness;
            stackedEnergy[t % ts] += energy / 2;

            if (t == last) break;
            // Slide the guard-length total along by one sample.
            likeness += _window[t + tg] * Complex.Conjugate(_window[t + tg + tu])
                      - _window[t] * Complex.Conjugate(_window[t + tu]);
            energy += Power(_window[t + tg]) + Power(_window[t + tg + tu])
                    - Power(_window[t]) - Power(_window[t + tu]);
        }

        // Step 4: the place within a symbol where the guards line up best.
        double bestScore = 0;
        int bestStart = 0;
        for (int p = 0; p < ts; p++)
        {
            if (stackedEnergy[p] <= 1e-12) continue;   // silence: nothing to compare
            double score = stackedLikeness[p].Magnitude / stackedEnergy[p];
            if (score > bestScore) { bestScore = score; bestStart = p; }
        }
        return (Math.Min(bestScore, 1), bestStart, stackedLikeness[bestStart].Phase);
    }

    private static double Power(Complex c) => c.Real * c.Real + c.Imaginary * c.Imaginary;
}
