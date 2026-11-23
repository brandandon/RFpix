// SstvReceiver.cs - receives analog SSTV pictures from the radio's sound.
//
// The steps:
//   1. Frequency: the sound is moved down so 1750 Hz (the middle of SSTV's tones, 1100-2400 Hz)
//      sits at 0 Hz, and filtered to keep only those tones (a narrow, steep filter: noise outside
//      them is what turns a weak picture to speckle). The tone's frequency is read from how fast
//      its phase turns. Kept as a "track", 24 000 a second, with the signal's strength, so a
//      pixel's shade can be read with each moment counted by how strong the signal was then.
//   2. VIS: the start code (1900 Hz leader, 1200 Hz break, leader, then 10 bits of 30 ms)
//      says which mode follows. It's found by checking the track millisecond by millisecond,
//      each part read as the strongest tone in it (noise pulls a plain average towards the
//      middle of the band, which in a fade spoiled the code at about 12 dB).
//      A picture whose VIS code was missed (listening started partway through, or the code was
//      lost in noise) is found from its sync pulses instead, as MMSSTV does: each mode family has
//      its own line time and sync length, so five syncs in a row the right distance apart, and of
//      the right length, say which mode it is. That picture starts at the top of the frame from the
//      line where it was found (where it really was in the picture can't be known).
//   3. The picture: from the end of the VIS code the mode's timing says where every line
//      and every pixel is. Each line's 1200 Hz sync pulse is looked for near where it should
//      be; a straight line through the syncs found gives the true line length, so a sender
//      (or receiver) sound card that runs a little fast or slow doesn't slant the picture.
//      (Analog auto slant, in the Receive tab, can turn that off, as in MMSSTV.) Lines are shown as they
//      arrive, and the whole picture is drawn once more at the end with the best timing.
//      In a fade the tone sinks into the noise (measured on the sync pulses), and a pixel read
//      then is mostly noise: it leans on its neighbours (above, below and either side) instead, by
//      how far its tone stood above the noise, so a fade shows as a softer stretch rather than
//      random colour. Only measured values are mixed, never drawn ones, so nothing is copied on
//      down the picture (on a weak signal that made streaks).
//   4. Colour: back from green/blue/red, or from brightness and colour differences.
//
// After a picture MMSSTV and QSSTV usually send the sender's callsign as an FSK ID; it's read
// from the same frequency track (FskIdDecoder) and reported by IdReceived.
//
// It stops when the picture is complete, when the syncs have been missing for a while (the
// signal has gone), or when a new VIS code starts another picture.

using System.Numerics;
using RFpix.Imaging;

namespace RFpix.Analog;

/// <summary>A picture received (or as much of it as arrived).</summary>
/// <param name="Lines">How many lines arrived (all of them if complete).</param>
/// <param name="JoinedLate">Found from its sync pulses, its VIS code missed: it starts partway through.</param>
public sealed record SstvPicture(SstvModeInfo Mode, DecodedPicture Picture, int Lines, bool Complete, DateTime Ended,
                                 bool JoinedLate = false);

public sealed class SstvReceiver
{
    public const int InputRate = 48000;
    public const int TrackRate = 24000;   // frequency track samples per second

    // ---- step 1: the frequency track ----

    // The sound is moved down by 1750 Hz and filtered, keeping SSTV's tones (600 Hz either way of
    // 1750, so 1150-2350 Hz, falling away over the next 200 Hz or so: 255 taps). Only every second
    // filtered sample is worked out (the track is 24 000 a second). Measured on the HF bench
    // (Nov 2026), this narrower filter gives a much cleaner picture at 8-16 dB than the earlier
    // 600-3200 Hz one, for slightly softer fine detail on a strong signal.
    internal static double CentreHz = 1750, CutoffHz = 600;
    internal static int Taps = 255;
    private static double[] _lowpass = MakeLowpass(Taps, CutoffHz / InputRate);
    private readonly double[] _re = new double[2 * Taps], _im = new double[2 * Taps];   // each sample twice, so a whole window sits in one piece
    private int _at;
    private static int MixPeriod;
    private static double[] MixCos = [], MixSin = [];
    static SstvReceiver() => SetFilter(CentreHz, CutoffHz, Taps);
    private int _mixAt;
    private bool _odd;
    private double _prevRe, _prevIm;
    private double _level = 1e-3;   // running signal strength, to tell sound from silence

    // The track, one entry for every 24 000th of a second:
    //  - the frequency in Hz (0 where there's no signal), for the VIS code, the syncs and the FSK ID;
    //  - the smoothed sound itself (Z, moved down by 1900 Hz), for:
    //    - the turn from one sample to the next (Z times the last Z turned back). Its angle is the
    //      frequency and its size is the signal's strength squared, so adding the turns over a pixel and
    //      taking the angle of the sum reads the pixel's frequency with each moment counted by how strong
    //      the signal was: in a fade, or a burst of noise, the weak moments hardly count;
    //    - how much of a given tone a stretch holds (Tone), for reading the VIS code in noise.
    private readonly List<float> _track = new();
    private readonly List<float> _zRe = new(), _zIm = new();
    private readonly FskIdDecoder _fskId = new(TrackRate);

    public SstvReceiver() => _fskId.Received += call => IdReceived?.Invoke(call);
    private long _trackStart;                      // track samples dropped from the front

    /// <summary>Sound to receive: 48 kHz, -1 to 1.</summary>
    public void AddSamples(ReadOnlySpan<float> samples)
    {
        var h = _lowpass;
        int taps = h.Length;
        foreach (float x in samples)
        {
            double re = x * MixCos[_mixAt], im = x * MixSin[_mixAt];
            if (++_mixAt == MixPeriod) _mixAt = 0;
            _re[_at] = _re[_at + taps] = re;
            _im[_at] = _im[_at + taps] = im;
            if (++_at == taps) _at = 0;
            _odd = !_odd;
            if (_odd) continue;   // every second sample only

            double zr = 0, zi = 0;
            for (int k = 0, i = _at; k < taps; k++, i++) { zr += _re[i] * h[k]; zi += _im[i] * h[k]; }
            double magnitude = Math.Sqrt(zr * zr + zi * zi);
            _level = _level * 0.999 + magnitude * 0.001;
            // The turn since the last track sample.
            double qr = zr * _prevRe + zi * _prevIm, qi = zi * _prevRe - zr * _prevIm;
            _prevRe = zr; _prevIm = zi;
            double hz = CentreHz + Math.Atan2(qi, qr) * TrackRate / (2 * Math.PI);
            // Below a tenth of the usual level it's silence or noise between transmissions.
            float trackHz = magnitude > 0.1 * _level && magnitude > 1e-4 ? (float)hz : 0;
            _track.Add(trackHz);
            _zRe.Add((float)zr);
            _zIm.Add((float)zi);
            _fskId.Add(trackHz);
            OnTrack();
        }
    }

    private static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);

    /// <summary>For trying other filters on the bench.</summary>
    internal static void SetFilter(double centreHz, double cutoffHz, int taps)
    {
        CentreHz = centreHz; CutoffHz = cutoffHz; Taps = taps;
        _lowpass = MakeLowpass(taps, cutoffHz / InputRate);
        MixPeriod = InputRate / Gcd(InputRate, (int)centreHz);
        MixCos = Enumerable.Range(0, MixPeriod).Select(i => Math.Cos(2 * Math.PI * centreHz * i / InputRate)).ToArray();
        MixSin = Enumerable.Range(0, MixPeriod).Select(i => -Math.Sin(2 * Math.PI * centreHz * i / InputRate)).ToArray();
    }

    private float Track(long t) => t - _trackStart is var i && i >= 0 && i < _track.Count ? _track[(int)i] : 0;
    private long TrackEnd => _trackStart + _track.Count;

    /// <summary>The turn from track sample i - 1 to i (i counted from the front of the track kept).</summary>
    private (double Re, double Im) Turn(int i)
    {
        if (i < 1 || i >= _zRe.Count) return (0, 0);
        double zr = _zRe[i], zi = _zIm[i], pr = _zRe[i - 1], pi = _zIm[i - 1];
        return (zr * pr + zi * pi, zi * pr - zr * pi);
    }

    /// <summary>How much of a steady <paramref name="hz"/> tone track samples [from, to) hold: the size of
    /// the sound's match with that tone (squared), per sample.</summary>
    private double Tone(double hz, double from, double to)
    {
        long a = Math.Max((long)Math.Ceiling(from), _trackStart), b = Math.Min((long)Math.Ceiling(to), TrackEnd);
        if (b <= a) return 0;
        double step = -2 * Math.PI * (hz - CentreHz) / TrackRate, re = 0, im = 0;
        double c = 1, s = 0, dc = Math.Cos(step), ds = Math.Sin(step);   // turned a little more each sample
        for (long t = a; t < b; t++)
        {
            int i = (int)(t - _trackStart);
            re += _zRe[i] * c - _zIm[i] * s;
            im += _zRe[i] * s + _zIm[i] * c;
            (c, s) = (c * dc - s * ds, c * ds + s * dc);
        }
        return (re * re + im * im) / ((b - a) * (double)(b - a));
    }

    private void DropTrack(int count)
    {
        count = Math.Clamp(count, 0, _track.Count);
        _track.RemoveRange(0, count);
        _zRe.RemoveRange(0, count);
        _zIm.RemoveRange(0, count);
        _trackStart += count;
    }

    /// <summary>The frequency over track samples [from, to), each moment counted by the signal's strength
    /// (0 if there's nothing there), and the power there (signal and noise, per sample).</summary>
    private (double Hz, double Power) Weighted(double from, double to)
    {
        double re = 0, im = 0, power = 0;
        int n = 0;
        long a = Math.Max((long)Math.Ceiling(from), _trackStart + 1), b = Math.Min((long)Math.Ceiling(to), TrackEnd);
        for (long t = a; t < b; t++)
        {
            int i = (int)(t - _trackStart);
            var (r, m) = Turn(i);
            re += r; im += m;
            power += _zRe[i] * _zRe[i] + _zIm[i] * _zIm[i];
            n++;
        }
        if (n == 0 || re == 0 && im == 0) return (0, 0);
        return (CentreHz + Math.Atan2(im, re) * TrackRate / (2 * Math.PI), power / n);
    }

    /// <summary>Running totals of the power (signal and noise) from track sample <paramref name="from"/>:
    /// element i is the total of samples [from, from + i).</summary>
    private double[] PowerSums(long from, long to)
    {
        var sums = new double[Math.Max(1, to - from + 1)];
        for (long t = from; t < to; t++)
        {
            long i = t - _trackStart;
            double p = i >= 0 && i < _zRe.Count ? _zRe[(int)i] * _zRe[(int)i] + _zIm[(int)i] * _zIm[(int)i] : 0;
            sums[t - from + 1] = sums[t - from] + p;
        }
        return sums;
    }

    /// <summary>The noise's power (per sample) in track samples [from, to), which hold one steady tone
    /// between <paramref name="lo"/> and <paramref name="hi"/> Hz (a sync pulse, or the VIS code's leader):
    /// all the power less the tone's, in pieces of up to 20 ms (so a fade or a station tuned off doesn't
    /// spoil it). A short piece's tone takes in some noise too (about the share of the noise that one
    /// piece's worth of the filter's 1200 Hz passes), so that share is given back.</summary>
    private double NoiseIn(double from, double to, double lo, double hi)
    {
        int chunk = (int)(0.020 * TrackRate);
        double total = 0, weight = 0;
        for (double c0 = from; c0 + 0.002 * TrackRate <= to; c0 += chunk)
        {
            double c1 = Math.Min(to, c0 + chunk);
            double best = 0;
            for (double hz = lo; hz <= hi; hz += 5) best = Math.Max(best, Tone(hz, c0, c1));
            double power = 0;
            int n = 0;
            long a = Math.Max((long)Math.Ceiling(c0), _trackStart), b = Math.Min((long)Math.Ceiling(c1), TrackEnd);
            for (long t = a; t < b; t++) { int i = (int)(t - _trackStart); power += _zRe[i] * _zRe[i] + _zIm[i] * _zIm[i]; n++; }
            if (n == 0) continue;
            double pieces = Math.Max(1.5, 2 * CutoffHz * n / TrackRate);   // independent noise samples in it
            total += Math.Max(0, power / n - best) * pieces / (pieces - 1) * n;
            weight += n;
        }
        return weight > 0 ? total / weight : 0;
    }

    /// <summary>The noise measured on the last VIS code's leader (0 if none), for the picture it starts.</summary>
    private double _visNoise;

    // ---- events ----

    /// <summary>A picture has started (its VIS code was read).</summary>
    public event Action<SstvModeInfo>? Started;

    /// <summary>Lines have arrived: the picture so far, and how many lines it has.</summary>
    public event Action<DecodedPicture, int>? Progress;

    /// <summary>A picture has ended (complete, or cut short).</summary>
    public event Action<SstvPicture>? Finished;

    /// <summary>The picture's signal has gone (no syncs for 20 lines): it's kept, waiting, in case the
    /// signal comes back before the picture's planned end. Gives the lines drawn so far.</summary>
    public event Action<int>? Faded;

    /// <summary>The signal came back on the waiting picture's beat: it carries on (from this line).</summary>
    public event Action<int>? Resumed;

    /// <summary>A sender's callsign, from the FSK ID MMSSTV and QSSTV send after a picture.</summary>
    public event Action<string>? IdReceived;

    /// <summary>The mode being received (or waiting for its signal to come back), or null between pictures.</summary>
    public SstvModeInfo? Receiving => (_image ?? _faded)?.Mode;

    /// <summary>The picture being received was found without its VIS code (it joined partway through).</summary>
    public bool ReceivingLate => (_image ?? _faded)?.JoinedLate == true;

    /// <summary>The picture's signal has gone and RFpix is waiting to see if it comes back.</summary>
    public bool Waiting => _faded is not null;

    // ---- step 2: VIS ----

    private long _nextVisCheck;

    /// <summary>True to start no new picture (a picture already arriving carries on): set while a digital
    /// transmission is arriving and for a few seconds after, when its sound (or the waterfall text after
    /// it) can briefly look like an analog picture (seen on the air, Nov 20, 2026: "Robot 72" at the end
    /// of an RFpix FM picture, and "Martin 1, joined partway" during a HamDRM one).</summary>
    public bool HoldNewPictures { get => Volatile.Read(ref _hold); set => Volatile.Write(ref _hold, value); }
    private bool _hold;

    /// <summary>Auto slant (Settings, on unless turned off): each picture's line length is measured from
    /// its syncs, so a sound card that runs a little fast or slow (either station's) doesn't slant the
    /// picture. Off: every line is the mode's standard length, as MMSSTV does with its own Auto slant
    /// off; the syncs then only set where the picture starts across. Read when each picture starts.</summary>
    public bool AutoSlant { get => Volatile.Read(ref _autoSlant); set => Volatile.Write(ref _autoSlant, value); }
    private bool _autoSlant = true;

    private void OnTrack()
    {
        long end = TrackEnd;
        if (_pendingVis is var (pendingMode, firstEnd, vis))
        {
            // A VIS code found 25 ms ago: now its true end can be pinned down, and the picture begun.
            if (end >= firstEnd + RefineSpan)
            {
                _pendingVis = null;
                long pictureStart = Refine(firstEnd, vis);
                // The noise, on the second leader (300 ms of 1900 Hz, clear of its edges).
                long leader2 = pictureStart - (long)(0.600 * TrackRate);
                _visNoise = NoiseIn(leader2 + 0.02 * TrackRate, leader2 + 0.28 * TrackRate, 1800, 2000);
                _lastVisAt = pictureStart;
                if (!HoldNewPictures) Begin(pendingMode, pictureStart);
            }
        }
        else if (end >= _nextVisCheck)
        {
            _nextVisCheck = end + TrackRate / 1000;   // once a millisecond
            if (FindVis(end) is (var mode, long first, int code)) _pendingVis = (mode, first, code);
        }
        if (_image is not null) _image.Advance(end);
        else
        {
            // A picture whose signal faded: past its planned end there's nothing left to come back.
            if (_faded is not null && end > _fadedEnd) End();
            if (FindBySyncs(end - 1 - SmoothHalf) is (var lateMode, long lateStart))
            {
                // On the waiting picture's beat: the same transmission, after a fade. Carry on with it.
                if (_faded is { } faded && faded.Mode == lateMode && faded.LineAt(lateStart) is int line) Resume(line);
                else if (!HoldNewPictures) Begin(lateMode, lateStart, joinedLate: true);
            }
            // (While a picture waits, all its sound is kept: it's drawn again from the start at the end.)
            else if (_faded is null && _track.Count > TrackRate * 12)
            {
                // Between pictures only the last few seconds are needed (for the VIS code, and the
                // syncs of a picture whose VIS code was missed: six lines of the slowest mode).
                DropTrack(_track.Count - TrackRate * 8);
            }
        }
    }

    // ---- step 2b: a picture without its VIS code, found from its syncs ----

    private readonly List<(long At, double Ms)> _pulses = new();   // 1200 Hz pulses heard between pictures
    private long _pulseStart = -1, _pulseLast;
    private long _lastEnd = long.MinValue;                          // where the last picture ended

    /// <summary>For finding syncs: the frequency around track sample t, over half a millisecond either side,
    /// each moment counted by its strength (the frequency of one sample alone flickers in noise).</summary>
    internal static int SmoothHalf = 12;
    internal static double EdgeKnob { set => Image.EdgeMs = value; }
    internal static double FadeWeightKnob { get => Image.FadeWeight; set => Image.FadeWeight = value; }
    // How much a neighbour's value is allowed to differ from the pixel's own by the picture itself (in
    // units of the noise on a clearly heard pixel): above or below, and either side (the frequency track
    // already blurs along a line, so pixels either side say less).
    internal static double DetailV = 0.25, DetailH = 0.5;
    // A pixel whose own and nearest neighbours' trust adds up to less than this (as much as one pixel whose
    // tone is half the power) looks further up and down, up to this many scans.
    internal static double Enough = 1;
    internal static int FadeReach = 12;
    internal static double TrustFloor = 0.05;
    private double Smoothed(long t) => SmoothHalf == 0 ? Track(t) : Weighted(t - SmoothHalf, t + SmoothHalf + 1).Hz;

    /// <summary>The smoothed frequency (<see cref="Smoothed"/>) of <paramref name="count"/> track samples from
    /// <paramref name="from"/>, worked out in one pass.</summary>
    private float[] SmoothedRun(long from, int count)
    {
        var hz = new float[count];
        if (SmoothHalf == 0) { for (int i = 0; i < count; i++) hz[i] = Track(from + i); return hz; }
        double re = 0, im = 0;
        long lo = from - SmoothHalf, hi = from - SmoothHalf;   // the window [lo, hi)
        (double, double) Q(long s) => Turn((int)(s - _trackStart));
        for (int k = 0; k < count; k++)
        {
            long want = from + k + SmoothHalf + 1;
            while (hi < want) { var (r, m) = Q(hi++); re += r; im += m; }
            while (lo < from + k - SmoothHalf) { var (r, m) = Q(lo++); re -= r; im -= m; }
            hz[k] = re == 0 && im == 0 ? 0 : (float)(CentreHz + Math.Atan2(im, re) * TrackRate / (2 * Math.PI));
        }
        return hz;
    }

    /// <summary>How many line times in a row must match.</summary>
    private const int SyncsInARow = 4;

    /// <summary>Follows 1200 Hz pulses on the track (one sample at a time); when one ends, looks for a
    /// mode whose line time and sync length fit it and the pulses before it.</summary>
    private (SstvModeInfo, long)? FindBySyncs(long t)
    {
        double f = Smoothed(t);
        bool sync = f > 1050 && f < 1350;
        if (sync)
        {
            if (_pulseStart < 0) _pulseStart = t;
            _pulseLast = t;
            return null;
        }
        if (_pulseStart < 0 || t - _pulseLast < TrackRate / 2000) return null;   // allow a gap of half a millisecond
        double ms = (_pulseLast - _pulseStart + 1) * 1000.0 / TrackRate;
        long at = _pulseStart;
        _pulseStart = -1;
        if (ms < 3 || ms > 25 || at <= _lastEnd) return null;   // not a sync (a VIS bit is 30 ms)
        _pulses.Add((at, ms));
        _pulses.RemoveAll(p => at - p.At > 8L * TrackRate);
        return MatchSyncs();
    }

    private (SstvModeInfo, long)? MatchSyncs()
    {
        var (last, lastMs) = _pulses[^1];
        // The shortest line time that fits (a chain of Robot 36 lines with every other sync lost
        // would also look like Robot 72).
        foreach (var mode in SstvModes.All.OrderBy(m => m.LineMs))
        {
            if (!SyncLength(lastMs, mode)) continue;
            double line = mode.LineMs * TrackRate / 1000;
            long first = last;
            bool all = true;
            for (int k = 1; k <= SyncsInARow && all; k++)
            {
                // A sound card is never more than 0.2% off; and a millisecond for finding the edge.
                double expected = last - k * line, tolerance = 0.002 * k * line + TrackRate / 1000.0;
                var match = _pulses.Where(p => Math.Abs(p.At - expected) <= tolerance && SyncLength(p.Ms, mode))
                                   .Select(p => (long?)p.At).FirstOrDefault();
                if (match is long m) first = m; else all = false;
            }
            if (!all) continue;
            _pulses.Clear();
            // The picture starts at the line of the first of those syncs. (For Scottie the sync is in
            // the middle of the line, and Image adds one sync length for the extra one VIS starts with.)
            bool scottie = mode.IsScottie;
            double offset = scottie ? (2 * (mode.GapMs + mode.ScanMs) + mode.SyncMs) * TrackRate / 1000 : 0;
            return (mode, first - (long)Math.Round(offset));
        }
        return null;
    }

    private static bool SyncLength(double ms, SstvModeInfo mode) => ms >= 0.6 * mode.SyncMs && ms <= 1.4 * mode.SyncMs + 1;

    // The frequency over part of the VIS code, each moment counted by the signal's strength (so a short
    // fade or crackle in it doesn't spoil it).
    private double Mean(long from, double ms) => Weighted(from, from + ms * TrackRate / 1000).Hz;

    /// <summary>The strongest tone between <paramref name="lo"/> and <paramref name="hi"/> Hz in the
    /// <paramref name="ms"/> from track sample <paramref name="from"/>: each 20 ms measured on its own and
    /// added (a fade or a station tuned a little off doesn't spoil a long stretch), tones 5 Hz apart.</summary>
    private double Peak(long from, double ms, double lo, double hi)
    {
        int chunk = (int)(0.020 * TrackRate), chunks = Math.Max(1, (int)Math.Round(ms * TrackRate / 1000 / chunk));
        double best = -1, bestHz = 0;
        for (double hz = lo; hz <= hi; hz += 5)
        {
            double sum = 0;
            for (int c = 0; c < chunks; c++) sum += Tone(hz, from + c * chunk, from + (c + 1) * chunk);
            if (sum > best) { best = sum; bestHz = hz; }
        }
        return bestHz;
    }

    private static bool Near(double hz, double target, double within = 60) => Math.Abs(hz - target) <= within;

    private long? _lastVisAt;


    /// <summary>A VIS code ending at <paramref name="end"/> (the end of its stop bit), or null.</summary>
    private (SstvModeInfo, long, int)? FindVis(long end)
    {
        double ms = TrackRate / 1000.0;
        long stop = end - (long)(30 * ms);                  // the stop bit's start
        long bits = stop - (long)(8 * 30 * ms);             // the 8 bits (7 data + parity)
        long startBit = bits - (long)(30 * ms);
        long leader2 = startBit - (long)(300 * ms);
        if (leader2 - (long)(310 * ms) < _trackStart || end - _lastVisAt < 2 * TrackRate) return null;   // (null: none yet)

        // A quick look first (the frequency counted by strength): the start and stop bits must be well
        // below the picture's tones. In noise, and in a fade, that reading is pulled towards 1900 Hz (the
        // middle of the band, where noise averages out), so it's only a first sieve.
        if (Mean(stop + (long)(5 * ms), 20) > 1550 || Mean(startBit + (long)(5 * ms), 20) > 1550) return null;

        // Then each part's tone, read as the strongest tone in it (which noise doesn't pull aside).
        // Middles of each part, avoiding the edges.
        if (!Near(Peak(stop + (long)(5 * ms), 20, 1000, 1450), SstvModes.SyncHz, 80)) return null;
        if (!Near(Peak(startBit + (long)(5 * ms), 20, 1000, 1450), SstvModes.SyncHz, 80)) return null;
        int vis = 0, ones = 0;
        for (int i = 0; i < 8; i++)
        {
            double f = Peak(bits + (long)((30 * i + 5) * ms), 20, 1000, 1450);
            // A 1 (1100 Hz) or a 0 (1300 Hz): whichever it's nearer, if it's anywhere near either.
            if (!Near(f, SstvModes.SyncHz, 180)) return null;
            bool one = f < SstvModes.SyncHz;
            if (i < 7) vis |= (one ? 1 : 0) << i;
            if (one) ones++;
        }
        if (ones % 2 != 0) return null;   // even parity: the 1s, parity included, are even
        if (SstvModes.FromVis(vis) is not { } mode) return null;
        if (!Near(Peak(leader2 + (long)(20 * ms), 250, 1700, 2100), SstvModes.LeaderHz, 60)) return null;
        // The break and first leader (some senders shorten them, so only check part).
        if (!Near(Peak(leader2 - (long)(120 * ms), 100, 1700, 2100), SstvModes.LeaderHz, 60)) return null;

        // The first moment all that holds can be up to 15 ms or so early (a window that takes in part of
        // the next tone still has the right strongest tone), and the picture's timing starts here: so
        // once 25 ms more have arrived the end is moved to where the parts' frequencies best fit the
        // code just read (Refine), and only then is the picture begun.
        _lastVisAt = end;
        return (mode, end, vis);
    }

    private (SstvModeInfo Mode, long FirstEnd, int Vis)? _pendingVis;
    private static readonly int RefineSpan = (int)(0.025 * TrackRate);

    /// <summary>The VIS code's true end, from up to 25 ms after <paramref name="first"/> (where it was
    /// first found): where the end of the leader, the start bit, the bits read and the stop bit best fit
    /// their frequencies (each read over its whole 30 ms but the edges).</summary>
    private long Refine(long first, int vis)
    {
        double ms = TrackRate / 1000.0;
        var expected = new double[11];
        expected[0] = SstvModes.LeaderHz;
        expected[1] = SstvModes.SyncHz;
        for (int i = 0; i < 8; i++)
        {
            bool one = i < 7 ? (vis >> i & 1) == 1 : System.Numerics.BitOperations.PopCount((uint)vis) % 2 == 1;   // the parity bit
            expected[2 + i] = one ? SstvModes.VisOneHz : SstvModes.VisZeroHz;
        }
        expected[10] = SstvModes.SyncHz;
        long best = first;
        double bestCost = double.MaxValue;
        for (long end = first; end <= first + RefineSpan && end <= TrackEnd; end += 2)
        {
            double cost = 0;
            for (int k = 0; k < 11; k++)
            {
                // Part k (0: the last 30 ms of the leader) starts (10 - k) * 30 ms before the stop bit's start.
                long partStart = end - (long)((11 - k) * 30 * ms);
                cost += Math.Abs(Weighted(partStart + 3 * ms, partStart + 27 * ms).Hz - expected[k]);
            }
            if (cost < bestCost) { bestCost = cost; best = end; }
        }
        return best;
    }

    // ---- step 3: the picture ----

    private Image? _image;
    private Image? _faded;   // a picture whose signal has gone, kept in case it comes back
    private double _fadedEnd;  // where it would have ended (worked out once: its timing can't change while it waits)

    /// <summary>The picture's signal has gone: keep it, waiting, rather than end it. Pulses of the same
    /// mode on its beat before its planned end are the same transmission (Resume); anything else ends it.</summary>
    private void Fade()
    {
        if (_image is not { } image) return;
        _image = null;
        _faded = image;
        _fadedEnd = image.PlannedEnd;
        _pulses.Clear();
        _pulseStart = -1;
        Faded?.Invoke(image.Drawn);
    }

    private void Resume(int line)
    {
        if (_faded is not { } image) return;
        _faded = null;
        _image = image;
        _pulses.Clear();
        _pulseStart = -1;
        image.Resume(line);
        Resumed?.Invoke(line);
    }

    private void Begin(SstvModeInfo mode, long start, bool joinedLate = false)
    {
        if (_image is not null || _faded is not null) End();   // a new picture cuts the last one short
        _pulses.Clear();
        _pulseStart = -1;
        // Drop the track before the picture.
        DropTrack((int)Math.Max(0, start - TrackRate / 10 - _trackStart));
        _image = new Image(this, mode, start, joinedLate);
        Started?.Invoke(mode);
    }

    /// <summary>Ends the picture being received (the signal has stopped, or listening stops).</summary>
    public void End()
    {
        if ((_image ?? _faded) is not { } image) return;
        _image = _faded = null;
        _lastEnd = TrackEnd;
        _pulses.Clear();
        _pulseStart = -1;
        var picture = image.Finish();
        if (picture is not null) Finished?.Invoke(picture);
    }

    /// <summary>One picture being received.</summary>
    private sealed class Image
    {
        private readonly SstvReceiver _rx;
        public readonly SstvModeInfo Mode;
        public readonly bool JoinedLate;
        private readonly long _start;            // track sample where the first line's time begins
        private readonly double _lineSamples;    // nominal line length, in track samples
        private readonly double _syncOffset;     // where in a line its sync starts (ms)
        private readonly List<(int Line, double At)> _syncs = new();
        private int _lines;                      // sound lines drawn so far
        private int _missedSyncs;
        private readonly byte[] _bgra;
        private readonly bool _autoSlant;        // as set when the picture started

        // The noise, measured on the sync pulses (the middle of the last few lines' figures): in a fade, a
        // pixel whose tone is hardly above the noise is drawn mostly from its neighbours, rather than as a
        // random shade (see Smooth). Each scan's last drawn values are kept for a pixel with no tone at all.
        internal static double FadeWeight = 10;
        private readonly List<double> _noises = new();
        private double _noise;
        private readonly double[]?[] _prior = new double[]?[4];

        public Image(SstvReceiver rx, SstvModeInfo mode, long start, bool joinedLate = false)
        {
            _rx = rx;
            _autoSlant = rx.AutoSlant;
            // The noise as measured on the VIS code's leader, counted as five syncs' worth.
            if (!joinedLate && rx._visNoise > 0) { for (int i = 0; i < 5; i++) _noises.Add(rx._visNoise); _noise = rx._visNoise; }
            Mode = mode;
            JoinedLate = joinedLate;
            bool scottie = mode.IsScottie;
            // Scottie starts with one extra sync; its lines' own syncs come between blue and red.
            _start = start + (scottie ? (long)Math.Round(mode.SyncMs * TrackRate / 1000) : 0);
            _syncOffset = scottie ? 2 * (mode.GapMs + mode.ScanMs) : 0;
            _lineSamples = mode.LineMs * TrackRate / 1000;
            _bgra = new byte[4 * mode.Width * mode.Height];
            for (int i = 3; i < _bgra.Length; i += 4) _bgra[i] = 255;
        }

        /// <summary>The line's start, by the timing found so far.</summary>
        private double LineStart(int line)
        {
            var (a, b) = Timing();
            return a + b * line;
        }

        // A straight line through the syncs found: where line 0 starts, and the line length.
        private (double Start, double Length) Timing()
        {
            if (_syncs.Count < 4) return (_start, _lineSamples);
            if (!_autoSlant)
            {
                // The standard line length; where line 0 starts is the middle of where the syncs put it
                // (the middle one, so a few noise bursts taken for syncs don't move it).
                var starts = _syncs.Select(s => s.At - _lineSamples * s.Line).OrderBy(v => v).ToList();
                return (starts[starts.Count / 2], _lineSamples);
            }
            // Fit, then fit again without the syncs far from the line (noise mistaken for syncs).
            var points = _syncs.ToList();
            (double, double) fit = (_start, _lineSamples);
            for (int pass = 0; pass < 2; pass++)
            {
                if (points.Count < 4) break;
                double n = points.Count, sx = 0, sy = 0, sxx = 0, sxy = 0;
                foreach (var (l, at) in points) { sx += l; sy += at; sxx += (double)l * l; sxy += l * at; }
                double d = n * sxx - sx * sx;
                if (d <= 0) break;
                double b = (n * sxy - sx * sy) / d, a = (sy - b * sx) / n;
                // A sound card is never more than 1% off.
                if (Math.Abs(b / _lineSamples - 1) > 0.01) break;
                fit = (a, b);
                double tolerance = 0.002 * TrackRate;   // 2 ms
                points = points.Where(p => Math.Abs(p.At - (a + b * p.Line)) < tolerance).ToList();
            }
            return fit;
        }

        /// <summary>Draws the lines whose sound has all arrived.</summary>
        public void Advance(long trackEnd)
        {
            while (_lines < Mode.SoundLines)
            {
                double start = LineStart(_lines);
                if (start + _lineSamples * 1.02 + 0.03 * TrackRate > trackEnd) return;   // not all here yet
                FindSync(_lines);
                DrawLine(_lines, LineStart(_lines));
                _lines++;
                if (_lines <= _resumeLine) _missedSyncs = 0;   // the gap before a fade's end isn't counted again
                if (_missedSyncs >= 20)   // no syncs for 20 lines: the signal has gone (perhaps only for a while)
                {
                    _rx.Fade();
                    return;
                }
                if (_lines % 4 == 0 || _lines == Mode.SoundLines) _rx.Progress?.Invoke(Snapshot(), Drawn);
            }
            _rx.End();   // complete
        }

        public int Drawn => Mode.Color == SstvColor.PD ? 2 * _lines : _lines;

        private int _resumeLine = -1;   // after a fade: the line where the signal came back

        /// <summary>Where the picture would have ended (track sample), by its timing.</summary>
        public double PlannedEnd => LineStart(Mode.SoundLines);

        /// <summary>A picture found from its syncs, starting at <paramref name="start"/> (as
        /// <see cref="MatchSyncs"/> gives it): is it this picture's own line, after a fade? Its line
        /// number if it falls on this picture's beat (within 5 ms), after the last line with a sync and
        /// before the end; otherwise null.</summary>
        public int? LineAt(long start)
        {
            if (_syncs.Count == 0) return null;
            double at = start + (Mode.IsScottie ? Math.Round(Mode.SyncMs * TrackRate / 1000) : 0);
            var (a, b) = Timing();
            int line = (int)Math.Round((at - a) / b);
            if (line <= _syncs[^1].Line || line >= Mode.SoundLines) return null;
            return Math.Abs(at - (a + b * line)) <= 0.005 * TrackRate ? line : null;
        }

        /// <summary>Carries on after a fade: the signal came back at <paramref name="line"/>.</summary>
        public void Resume(int line)
        {
            _resumeLine = line;
            _missedSyncs = 0;
        }

        // Looks for the line's sync pulse within 8 ms of where it should be.
        private void FindSync(int line)
        {
            double expected = LineStart(line) + _syncOffset * TrackRate / 1000;
            int width = (int)Math.Round(Mode.SyncMs * TrackRate / 1000);
            int range = (int)(0.008 * TrackRate);
            double best = -1;
            int bestAt = 0;
            // A sync is a stretch near 1200 Hz between brighter tones: score = samples inside below 1350 Hz.
            long first = (long)Math.Round(expected) - range - 2;
            var hz = _rx.SmoothedRun(first, 2 * range + width + 5);
            float At(long s) => hz[s - first];
            for (int d = -range; d <= range; d++)
            {
                long at = (long)Math.Round(expected) + d;
                int inside = 0;
                for (int k = 0; k < width; k++)
                {
                    float f = At(at + k);
                    if (f > 1000 && f < 1350) inside++;
                }
                float before = At(at - 2), after = At(at + width + 2);
                double score = inside + (before > 1400 ? 2 : 0) + (after > 1400 ? 2 : 0);
                if (score > best) { best = score; bestAt = d; }
            }
            if (best >= 0.7 * width)
            {
                // The noise on this sync (its middle, clear of its edges), if it's long enough to tell.
                double syncAt = Math.Round(expected) + bestAt;
                if (width - Ms(2) >= Ms(2.5))
                {
                    _noises.Add(_rx.NoiseIn(syncAt + Ms(1), syncAt + width - Ms(1), 1050, 1350));
                    if (_noises.Count > 15) _noises.RemoveAt(0);
                    _noise = _noises.OrderBy(v => v).ElementAt(_noises.Count / 2);
                }
                _syncs.Add((line, (long)Math.Round(expected) + bestAt - _syncOffset * TrackRate / 1000));
                _missedSyncs = 0;
            }
            else _missedSyncs++;
        }

        // The value (0-255) of a scan's pixels, averaged over each pixel's time. The frequency
        // track blurs sharp changes over a millisecond or more (its filter), so a scan's first and last
        // pixels would take in some of the tone before or after it: a 1200 Hz sync just after
        // Robot's colour scan reads as "no colour", which shows as a green stripe down the right
        // edge. So pixels are read only from the part of the scan clear of that blur; the edge
        // pixels take the nearest clear value.
        internal static double EdgeMs = 1.5;

        // Each scan's own values and how sure of each one we are (0 to 1), per slot in the order they came. The
        // sureness is how much of the power around the pixel (a millisecond either side: fading changes over
        // tenths of a second) is the tone rather than the noise.
        private readonly List<(float[] Raw, float[] Sure)>[] _scans = { new(), new(), new(), new() };
        private readonly int[] _scanAt = new int[4];   // redrawing (Finish): the next scan of each slot
        private bool _redrawing;

        private double[] Scan(double at, double ms, int width, int slot)
        {
            if (_redrawing) return Smooth(slot, _scanAt[slot]++, below: true);
            var raw = new float[width];
            var sure = new float[width];
            double samples = ms * TrackRate / 1000, step = samples / width;
            double edge = Math.Min(Ms(EdgeMs), samples / 4);
            double lo = at + edge, hi = at + samples - edge;
            long p0 = (long)Math.Floor(at - Ms(1)), p1 = (long)Math.Ceiling(at + samples + Ms(1));
            var sums = _rx.PowerSums(p0, p1);
            int half = (int)Ms(1);
            for (int x = 0; x < width; x++)
            {
                double from = Math.Max(at + x * step, lo), to = Math.Min(at + (x + 1) * step, hi);
                if (Math.Ceiling(from) >= to)
                {
                    // Narrower than a track sample, or in the blurred edge: the nearest clear sample.
                    double centre = Math.Round(Math.Clamp(at + (x + 0.5) * step, lo, hi));
                    from = centre; to = centre + 1;
                }
                var (hz, _) = _rx.Weighted(from, to);
                if (hz <= 0) { raw[x] = 0; sure[x] = 0; continue; }
                raw[x] = (float)Math.Clamp((hz - SstvModes.BlackHz) / (SstvModes.WhiteHz - SstvModes.BlackHz) * 255, 0, 255);
                double k = 1;
                if (_noise > 0 && FadeWeight > 0)
                {
                    long c = (long)Math.Round(at + (x + 0.5) * step) - p0;
                    long a = Math.Clamp(c - half, 0, sums.Length - 1), b = Math.Clamp(c + half, 0, sums.Length - 1);
                    double power = b > a ? (sums[b] - sums[a]) / (b - a) : 0;
                    // How much of the power is the tone: all of it on a clean signal, little in a deep fade.
                    double tone = Math.Max(0, power - _noise);
                    k = tone / (tone + FadeWeight * _noise);
                }
                sure[x] = (float)k;
            }
            _scans[slot].Add((raw, sure));
            return Smooth(slot, _scans[slot].Count - 1, below: false);
        }

        // A pixel's value from its own and its neighbours' (the scans above and below it, and the pixels
        // either side), each counted by how much it can be trusted: a pixel heard clearly is its own value;
        // a weak one leans on its neighbours, the more the weaker it is. Only the measured values are used,
        // never the drawn ones, so nothing is carried on down the picture (copying down line after line
        // makes streaks on a weak signal). Only in a deep fade, where the lines next to it were lost too,
        // does it look further up and down, as far as it must to find lines that were heard (and then
        // blends between them: lost lines show as a soft stretch, not random colour).
        private double[] Smooth(int slot, int index, bool below)
        {
            var list = _scans[slot];
            var (raw, sure) = list[index];
            int width = raw.Length;
            var values = new double[width];
            var prior = _prior[slot];
            // Trust, from how sure: none at all for a tone hardly above the noise (noise alone sometimes looks
            // like a little tone, and many such pixels in a deep fade would add up to a lot of noise).
            static double Trust(double k) => k >= 0.999 ? 1000 : k <= TrustFloor ? 0 : (k - TrustFloor) / (1 - k);
            for (int x = 0; x < width; x++)
            {
                double pc = Trust(sure[x]);
                double total = pc * raw[x], weight = pc;
                if (pc < 1000)
                {
                    void Add(float[] r, float[] s, int xx, double detail)
                    {
                        if (xx < 0 || xx >= width || s[xx] <= TrustFloor) return;
                        double p = Trust(s[xx]);
                        double w = 1 / (1 / p + detail);
                        total += w * r[xx]; weight += w;
                    }
                    void AddLine(int i, double detail)
                    {
                        if (i < 0 || i >= list.Count || i > index && !below) return;
                        var (r, s) = list[i];
                        if (r.Length != width) return;
                        Add(r, s, x, detail); Add(r, s, x - 1, detail + DetailH); Add(r, s, x + 1, detail + DetailH);
                    }
                    Add(raw, sure, x - 1, DetailH);
                    Add(raw, sure, x + 1, DetailH);
                    AddLine(index - 1, DetailV);
                    AddLine(index + 1, DetailV);
                    for (int d = 2; d <= FadeReach && weight < Enough; d++)
                    {
                        AddLine(index - d, DetailV * d * d);
                        AddLine(index + d, DetailV * d * d);
                    }
                }
                values[x] = weight > 1e-9 ? total / weight : prior?[x] ?? 0;
            }
            _prior[slot] = values;
            return values;
        }

        private double Ms(double ms) => ms * TrackRate / 1000;

        // Robot 36's colour: each line carries one colour difference; kept for its pair.
        private double[]? _cr, _cb;

        private void DrawLine(int line, double start)
        {
            var m = Mode;
            int w = m.Width;
            switch (m.Color)
            {
                case SstvColor.Gbr when m.Mode is SstvMode.Martin1 or SstvMode.Martin2:
                {
                    double g = start + Ms(m.SyncMs + m.PorchMs);
                    double b = g + Ms(m.ScanMs + m.GapMs), r = b + Ms(m.ScanMs + m.GapMs);
                    PutRgb(line, Scan(r, m.ScanMs, w, 0), Scan(g, m.ScanMs, w, 1), Scan(b, m.ScanMs, w, 2));
                    break;
                }
                case SstvColor.Gbr:
                {
                    double g = start + Ms(m.GapMs);
                    double b = g + Ms(m.ScanMs + m.GapMs), r = b + Ms(m.ScanMs + m.SyncMs + m.GapMs);
                    PutRgb(line, Scan(r, m.ScanMs, w, 0), Scan(g, m.ScanMs, w, 1), Scan(b, m.ScanMs, w, 2));
                    break;
                }
                case SstvColor.Robot when m.Mode == SstvMode.Robot36:
                {
                    double y0 = start + Ms(m.SyncMs + m.PorchMs);
                    double sep = y0 + Ms(m.ScanMs);
                    double c = sep + Ms(m.GapMs + 1.5);
                    var y = Scan(y0, m.ScanMs, w, 0);
                    // The separator says which: black before R-Y, white before B-Y.
                    double sepHz = MeanHz(sep + Ms(0.5), Ms(m.GapMs - 1));
                    bool isCr = sepHz > 0 ? sepHz < 1900 : line % 2 == 0;
                    var colour = Scan(c, m.ScanMs / 2, w, isCr ? 1 : 2);
                    if (isCr) _cr = colour; else _cb = colour;
                    PutYcc(line, y, _cr ?? Gray(w), _cb ?? Gray(w));
                    // The line above shared this colour difference: draw it again with both.
                    if (line > 0 && !isCr && _cr is not null) Redraw(line - 1);
                    if (isCr) _lastY = y;
                    break;
                }
                case SstvColor.Robot:
                {
                    double y0 = start + Ms(m.SyncMs + m.PorchMs);
                    double cr = y0 + Ms(m.ScanMs + m.GapMs + 1.5);
                    double cb = cr + Ms(m.ScanMs / 2 + m.GapMs + 1.5);
                    PutYcc(line, Scan(y0, m.ScanMs, w, 0), Scan(cr, m.ScanMs / 2, w, 1), Scan(cb, m.ScanMs / 2, w, 2));
                    break;
                }
                default:
                {
                    double y0 = start + Ms(m.SyncMs + m.PorchMs);
                    var ya = Scan(y0, m.ScanMs, w, 0);
                    var cr = Scan(y0 + Ms(m.ScanMs), m.ScanMs, w, 1);
                    var cb = Scan(y0 + Ms(2 * m.ScanMs), m.ScanMs, w, 2);
                    var yb = Scan(y0 + Ms(3 * m.ScanMs), m.ScanMs, w, 0);
                    PutYcc(2 * line, ya, cr, cb);
                    if (2 * line + 1 < m.Height) PutYcc(2 * line + 1, yb, cr, cb);
                    break;
                }
            }
        }

        private double[]? _lastY;
        private void Redraw(int line) { if (_lastY is not null && _cr is not null && _cb is not null) PutYcc(line, _lastY, _cr, _cb); }

        private double MeanHz(double from, double samples) => _rx.Weighted(from, from + samples).Hz;

        private static double[] Gray(int w) => Enumerable.Repeat(128.0, w).ToArray();

        private void PutRgb(int line, double[] r, double[] g, double[] b)
        {
            if (line >= Mode.Height) return;
            for (int x = 0; x < Mode.Width; x++)
            {
                int i = 4 * (line * Mode.Width + x);
                _bgra[i] = (byte)Math.Round(b[x]);
                _bgra[i + 1] = (byte)Math.Round(g[x]);
                _bgra[i + 2] = (byte)Math.Round(r[x]);
            }
        }

        private void PutYcc(int line, double[] y, double[] cr, double[] cb)
        {
            if (line >= Mode.Height) return;
            for (int x = 0; x < Mode.Width; x++)
            {
                double Y = y[x], R = cr[x] - 128, B = cb[x] - 128;
                int i = 4 * (line * Mode.Width + x);
                _bgra[i] = Clamp(Y + 1.772 * B);
                _bgra[i + 1] = Clamp(Y - 0.344136 * B - 0.714136 * R);
                _bgra[i + 2] = Clamp(Y + 1.402 * R);
            }
        }

        private static byte Clamp(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);

        private DecodedPicture Snapshot() => new(Mode.Width, Mode.Height, (byte[])_bgra.Clone());

        /// <summary>The picture with the final timing (drawn again from the start), or null if
        /// hardly any of it arrived.</summary>
        public SstvPicture? Finish()
        {
            int lines = _lines;
            if (lines < 8) return null;
            _cr = _cb = _lastY = null;
            Array.Clear(_prior);
            foreach (var list in _scans) list.Clear();
            for (int line = 0; line < lines; line++) DrawLine(line, LineStart(line));
            // Again, now that each scan's neighbours below are known too (while arriving, a line is drawn
            // from the ones above it only).
            _cr = _cb = _lastY = null;
            Array.Clear(_prior);
            Array.Clear(_scanAt);
            _redrawing = true;
            for (int line = 0; line < lines; line++) DrawLine(line, LineStart(line));
            _redrawing = false;
            bool complete = lines == Mode.SoundLines;
            return new SstvPicture(Mode, Snapshot(), Drawn, complete, DateTime.Now, JoinedLate);
        }
    }

    // A windowed-sinc low-pass filter: cutoff as a fraction of the sample rate.
    private static double[] MakeLowpass(int taps, double cutoff)
    {
        var h = new double[taps];
        double sum = 0;
        int mid = taps / 2;
        for (int i = 0; i < taps; i++)
        {
            int n = i - mid;
            double sinc = n == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * n) / (Math.PI * n);
            double window = 0.42 - 0.5 * Math.Cos(2 * Math.PI * i / (taps - 1)) + 0.08 * Math.Cos(4 * Math.PI * i / (taps - 1));
            h[i] = sinc * window;
            sum += h[i];
        }
        for (int i = 0; i < taps; i++) h[i] /= sum;
        return h;
    }
}
