// SstvReceiver.cs - receives analog SSTV pictures from the radio's sound.
//
// The steps:
//   1. Frequency: the sound is moved down so 1900 Hz (the middle of SSTV's band) sits at
//      0 Hz, smoothed to keep only SSTV's band, and the tone's frequency is read from how
//      fast its phase turns, sample by sample. Kept as a "frequency track", 24 000 a second.
//   2. VIS: the start code (1900 Hz leader, 1200 Hz break, leader, then 10 bits of 30 ms)
//      says which mode follows. It's found by checking the track millisecond by millisecond.
//   3. The picture: from the end of the VIS code the mode's timing says where every line
//      and every pixel is. Each line's 1200 Hz sync pulse is looked for near where it should
//      be; a straight line through the syncs found gives the true line length, so a sender
//      (or receiver) sound card that runs a little fast or slow doesn't slant the picture.
//      Lines are shown as they arrive, and the whole picture is drawn once more at the end
//      with the best timing.
//   4. Colour: back from green/blue/red, or from brightness and colour differences.
//
// It stops when the picture is complete, when the syncs have been missing for a while (the
// signal has gone), or when a new VIS code starts another picture.

using System.Numerics;
using RFpix.Imaging;

namespace RFpix.Analog;

/// <summary>A picture received (or as much of it as arrived).</summary>
/// <param name="Lines">How many lines arrived (all of them if complete).</param>
public sealed record SstvPicture(SstvModeInfo Mode, DecodedPicture Picture, int Lines, bool Complete, DateTime Ended);

public sealed class SstvReceiver
{
    public const int InputRate = 48000;
    public const int TrackRate = 24000;   // frequency track samples per second
    private const double CentreHz = 1900;

    // ---- step 1: the frequency track ----

    private static readonly double[] Lowpass = MakeLowpass(63, 1300.0 / InputRate);
    private readonly Complex[] _history = new Complex[Lowpass.Length];
    private int _historyAt;
    private long _inputCount;
    private Complex _previous;
    private double _pairSum;
    private int _pairCount;
    private double _level = 1e-3;   // running signal strength, to tell sound from silence

    private readonly List<float> _track = new();   // Hz, or 0 where there's no signal
    private long _trackStart;                      // track samples dropped from the front

    /// <summary>Sound to receive: 48 kHz, -1 to 1.</summary>
    public void AddSamples(ReadOnlySpan<float> samples)
    {
        foreach (float x in samples)
        {
            double angle = -2 * Math.PI * CentreHz * (_inputCount++ % InputRate) / InputRate;
            _history[_historyAt] = new Complex(x * Math.Cos(angle), x * Math.Sin(angle));
            _historyAt = (_historyAt + 1) % _history.Length;
            Complex z = Complex.Zero;
            for (int k = 0, i = _historyAt; k < Lowpass.Length; k++, i = (i + 1) % _history.Length)
                z += _history[i] * Lowpass[k];

            double magnitude = z.Magnitude;
            _level = _level * 0.9995 + magnitude * 0.0005;
            double dphi = Math.Atan2((z * Complex.Conjugate(_previous)).Imaginary, (z * Complex.Conjugate(_previous)).Real);
            _previous = z;
            double hz = CentreHz + dphi * InputRate / (2 * Math.PI);
            // Below a tenth of the usual level it's silence or noise between transmissions.
            _pairSum += magnitude > 0.1 * _level && magnitude > 1e-4 ? hz : 0;
            if (++_pairCount == 2)
            {
                _track.Add((float)(_pairSum / 2));
                _pairSum = 0;
                _pairCount = 0;
                OnTrack();
            }
        }
    }

    private float Track(long t) => t - _trackStart is var i && i >= 0 && i < _track.Count ? _track[(int)i] : 0;
    private long TrackEnd => _trackStart + _track.Count;

    // ---- events ----

    /// <summary>A picture has started (its VIS code was read).</summary>
    public event Action<SstvModeInfo>? Started;

    /// <summary>Lines have arrived: the picture so far, and how many lines it has.</summary>
    public event Action<DecodedPicture, int>? Progress;

    /// <summary>A picture has ended (complete, or cut short).</summary>
    public event Action<SstvPicture>? Finished;

    /// <summary>The mode being received, or null between pictures.</summary>
    public SstvModeInfo? Receiving => _image?.Mode;

    // ---- step 2: VIS ----

    private long _nextVisCheck;

    private void OnTrack()
    {
        long end = TrackEnd;
        if (end >= _nextVisCheck)
        {
            _nextVisCheck = end + TrackRate / 1000;   // once a millisecond
            if (FindVis(end) is (var mode, long pictureStart)) Begin(mode, pictureStart);
        }
        if (_image is not null) _image.Advance(end);
        else if (_track.Count > TrackRate * 4)
        {
            // Between pictures only the last few seconds are needed (for the VIS code).
            int drop = _track.Count - TrackRate * 2;
            _track.RemoveRange(0, drop);
            _trackStart += drop;
        }
    }

    private double Mean(long from, double ms)
    {
        int n = (int)(ms * TrackRate / 1000);
        double sum = 0;
        int counted = 0;
        for (long t = from; t < from + n; t++)
        {
            float f = Track(t);
            if (f <= 0) return 0;   // silence in it
            sum += f;
            counted++;
        }
        return counted > 0 ? sum / counted : 0;
    }

    private static bool Near(double hz, double target, double within = 60) => Math.Abs(hz - target) <= within;

    private long? _lastVisAt;

    /// <summary>A VIS code ending at <paramref name="end"/> (the end of its stop bit), or null.</summary>
    private (SstvModeInfo, long)? FindVis(long end)
    {
        double ms = TrackRate / 1000.0;
        long stop = end - (long)(30 * ms);                  // the stop bit's start
        long bits = stop - (long)(8 * 30 * ms);             // the 8 bits (7 data + parity)
        long startBit = bits - (long)(30 * ms);
        long leader2 = startBit - (long)(300 * ms);
        if (leader2 - (long)(310 * ms) < _trackStart || end - _lastVisAt < 2 * TrackRate) return null;   // (null: none yet)

        // Middles of each part, avoiding the edges.
        if (!Near(Mean(stop + (long)(5 * ms), 20), SstvModes.SyncHz, 80)) return null;
        if (!Near(Mean(startBit + (long)(5 * ms), 20), SstvModes.SyncHz, 80)) return null;
        if (!Near(Mean(leader2 + (long)(20 * ms), 250), SstvModes.LeaderHz, 60)) return null;
        // The break and first leader (some senders shorten them, so only check part).
        if (!Near(Mean(leader2 - (long)(120 * ms), 100), SstvModes.LeaderHz, 60)) return null;

        int vis = 0, ones = 0;
        for (int i = 0; i < 8; i++)
        {
            double f = Mean(bits + (long)((30 * i + 5) * ms), 20);
            bool one = Near(f, SstvModes.VisOneHz, 70), zero = Near(f, SstvModes.VisZeroHz, 70);
            if (one == zero) return null;
            if (i < 7) vis |= (one ? 1 : 0) << i;
            if (one) ones++;
        }
        if (ones % 2 != 0) return null;   // even parity: the 1s, parity included, are even
        if (SstvModes.FromVis(vis) is not { } mode) return null;
        _lastVisAt = end;
        return (mode, end);
    }

    // ---- step 3: the picture ----

    private Image? _image;

    private void Begin(SstvModeInfo mode, long start)
    {
        if (_image is not null) End();   // a new picture cuts the last one short
        // Drop the track before the picture.
        int drop = (int)Math.Max(0, start - TrackRate / 10 - _trackStart);
        drop = Math.Min(drop, _track.Count);
        _track.RemoveRange(0, drop);
        _trackStart += drop;
        _image = new Image(this, mode, start);
        Started?.Invoke(mode);
    }

    /// <summary>Ends the picture being received (the signal has stopped, or listening stops).</summary>
    public void End()
    {
        if (_image is not { } image) return;
        _image = null;
        var picture = image.Finish();
        if (picture is not null) Finished?.Invoke(picture);
    }

    /// <summary>One picture being received.</summary>
    private sealed class Image
    {
        private readonly SstvReceiver _rx;
        public readonly SstvModeInfo Mode;
        private readonly long _start;            // track sample where the first line's time begins
        private readonly double _lineSamples;    // nominal line length, in track samples
        private readonly double _syncOffset;     // where in a line its sync starts (ms)
        private readonly List<(int Line, double At)> _syncs = new();
        private int _lines;                      // sound lines drawn so far
        private int _missedSyncs;
        private readonly byte[] _bgra;

        public Image(SstvReceiver rx, SstvModeInfo mode, long start)
        {
            _rx = rx;
            Mode = mode;
            bool scottie = mode.Mode is SstvMode.Scottie1 or SstvMode.Scottie2 or SstvMode.ScottieDX;
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
                if (_missedSyncs >= 20)   // no syncs for 20 lines: the signal has gone
                {
                    _rx.End();
                    return;
                }
                if (_lines % 4 == 0 || _lines == Mode.SoundLines) _rx.Progress?.Invoke(Snapshot(), Drawn);
            }
            _rx.End();   // complete
        }

        private int Drawn => Mode.Color == SstvColor.PD ? 2 * _lines : _lines;

        // Looks for the line's sync pulse within 8 ms of where it should be.
        private void FindSync(int line)
        {
            double expected = LineStart(line) + _syncOffset * TrackRate / 1000;
            int width = (int)Math.Round(Mode.SyncMs * TrackRate / 1000);
            int range = (int)(0.008 * TrackRate);
            double best = -1;
            int bestAt = 0;
            // A sync is a stretch near 1200 Hz between brighter tones: score = samples inside below 1350 Hz.
            for (int d = -range; d <= range; d++)
            {
                long at = (long)Math.Round(expected) + d;
                int inside = 0;
                for (int k = 0; k < width; k++)
                {
                    float f = _rx.Track(at + k);
                    if (f > 1000 && f < 1350) inside++;
                }
                float before = _rx.Track(at - 2), after = _rx.Track(at + width + 2);
                double score = inside + (before > 1400 ? 2 : 0) + (after > 1400 ? 2 : 0);
                if (score > best) { best = score; bestAt = d; }
            }
            if (best >= 0.7 * width)
            {
                _syncs.Add((line, (long)Math.Round(expected) + bestAt - _syncOffset * TrackRate / 1000));
                _missedSyncs = 0;
            }
            else _missedSyncs++;
        }

        // The value (0-255) of a scan's pixels, averaged over each pixel's time.
        private double[] Scan(double at, double ms, int width)
        {
            var values = new double[width];
            double samples = ms * TrackRate / 1000, step = samples / width;
            for (int x = 0; x < width; x++)
            {
                double from = at + x * step, to = from + step;
                double sum = 0;
                int n = 0;
                for (long t = (long)Math.Ceiling(from); t < to; t++)
                {
                    float f = _rx.Track(t);
                    if (f > 0) { sum += f; n++; }
                }
                if (n == 0) { float f = _rx.Track((long)Math.Round((from + to) / 2)); if (f > 0) { sum = f; n = 1; } }
                values[x] = n == 0 ? 0 : Math.Clamp((sum / n - SstvModes.BlackHz) / (SstvModes.WhiteHz - SstvModes.BlackHz) * 255, 0, 255);
            }
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
                    PutRgb(line, Scan(r, m.ScanMs, w), Scan(g, m.ScanMs, w), Scan(b, m.ScanMs, w));
                    break;
                }
                case SstvColor.Gbr:
                {
                    double g = start + Ms(m.GapMs);
                    double b = g + Ms(m.ScanMs + m.GapMs), r = b + Ms(m.ScanMs + m.SyncMs + m.GapMs);
                    PutRgb(line, Scan(r, m.ScanMs, w), Scan(g, m.ScanMs, w), Scan(b, m.ScanMs, w));
                    break;
                }
                case SstvColor.Robot when m.Mode == SstvMode.Robot36:
                {
                    double y0 = start + Ms(m.SyncMs + m.PorchMs);
                    double sep = y0 + Ms(m.ScanMs);
                    double c = sep + Ms(m.GapMs + 1.5);
                    var y = Scan(y0, m.ScanMs, w);
                    var colour = Scan(c, m.ScanMs / 2, w);
                    // The separator says which: black before R-Y, white before B-Y.
                    double sepHz = MeanHz(sep + Ms(0.5), Ms(m.GapMs - 1));
                    bool isCr = sepHz > 0 ? sepHz < 1900 : line % 2 == 0;
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
                    PutYcc(line, Scan(y0, m.ScanMs, w), Scan(cr, m.ScanMs / 2, w), Scan(cb, m.ScanMs / 2, w));
                    break;
                }
                default:
                {
                    double y0 = start + Ms(m.SyncMs + m.PorchMs);
                    var ya = Scan(y0, m.ScanMs, w);
                    var cr = Scan(y0 + Ms(m.ScanMs), m.ScanMs, w);
                    var cb = Scan(y0 + Ms(2 * m.ScanMs), m.ScanMs, w);
                    var yb = Scan(y0 + Ms(3 * m.ScanMs), m.ScanMs, w);
                    PutYcc(2 * line, ya, cr, cb);
                    if (2 * line + 1 < m.Height) PutYcc(2 * line + 1, yb, cr, cb);
                    break;
                }
            }
        }

        private double[]? _lastY;
        private void Redraw(int line) { if (_lastY is not null && _cr is not null && _cb is not null) PutYcc(line, _lastY, _cr, _cb); }

        private double MeanHz(double from, double samples)
        {
            double sum = 0;
            int n = 0;
            for (long t = (long)from; t < from + samples; t++) { float f = _rx.Track(t); if (f > 0) { sum += f; n++; } }
            return n > 0 ? sum / n : 0;
        }

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
            for (int line = 0; line < lines; line++) DrawLine(line, LineStart(line));
            bool complete = lines == Mode.SoundLines;
            return new SstvPicture(Mode, Snapshot(), Drawn, complete, DateTime.Now);
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
