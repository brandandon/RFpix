// SstvEncoder.cs - turns a picture into analog SSTV sound.
//
// The sound is one tone whose frequency follows the picture (see SstvModes.cs). The tone
// never jumps in phase as its frequency changes (each sample continues the wave from the
// last), so it stays clean on the air. Every part of every line starts at its exact time,
// worked out from the start of the transmission, so the timing never drifts, however long
// the picture: a receiver's slant comes only from its own sound card.
//
// Colour: Martin and Scottie send green, blue and red as they are. Robot and PD send
// brightness (Y) and two colour differences (R-Y and B-Y, "Cr" and "Cb"), in the full
// 0-255 range JPEG uses, which is what MMSSTV and QSSTV expect.

using RFpix.Imaging;

namespace RFpix.Analog;

public static class SstvEncoder
{
    public const int SampleRate = 48000;

    /// <summary>The tone's strength: about the same average loudness as a HamDRM transmission
    /// (-15 dBFS), so the same transmit level suits both.</summary>
    public const double Amplitude = 0.25;

    /// <summary>The picture's sound with its VIS code first: 48 kHz, -1 to 1. The picture must
    /// already be the mode's size (see <see cref="Fit"/>).</summary>
    public static float[] Audio(DecodedPicture picture, SstvModeInfo mode, bool vis = true)
    {
        if (picture.Width != mode.Width || picture.Height != mode.Height)
            throw new ArgumentException($"{mode.Name} pictures are {mode.Width} × {mode.Height}; fit the picture first.");
        var tones = new ToneWriter();
        if (vis) WriteVis(tones, mode.Vis);
        var channels = new Channels(picture);
        switch (mode.Color)
        {
            case SstvColor.Gbr: WriteGbr(tones, mode, channels); break;
            case SstvColor.Robot: WriteRobot(tones, mode, channels); break;
            default: WritePD(tones, mode, channels); break;
        }
        return tones.ToArray();
    }

    /// <summary>How long the whole transmission takes (VIS code and picture).</summary>
    public static TimeSpan Duration(SstvModeInfo mode, bool vis = true) =>
        mode.PictureTime + (vis ? SstvModes.VisTime : TimeSpan.Zero);

    /// <summary>The picture made the mode's size: enlarged or reduced to fill it, the middle kept
    /// (the edges that don't fit are cut off, as most SSTV programs do).</summary>
    public static DecodedPicture Fit(DecodedPicture picture, SstvModeInfo mode)
    {
        if (picture.Width == mode.Width && picture.Height == mode.Height) return picture;
        double scale = Math.Max((double)mode.Width / picture.Width, (double)mode.Height / picture.Height);
        int w = Math.Max(mode.Width, (int)Math.Ceiling(picture.Width * scale));
        int h = Math.Max(mode.Height, (int)Math.Ceiling(picture.Height * scale));
        var resized = Resample(picture, w, h);
        int left = (w - mode.Width) / 2, top = (h - mode.Height) / 2;
        var bgra = new byte[4 * mode.Width * mode.Height];
        for (int y = 0; y < mode.Height; y++)
            Array.Copy(resized.Bgra, 4 * ((y + top) * w + left), bgra, 4 * y * mode.Width, 4 * mode.Width);
        return new DecodedPicture(mode.Width, mode.Height, bgra);
    }

    // Area-average when making smaller (PictureScaler), and simple interpolation when making bigger.
    private static DecodedPicture Resample(DecodedPicture p, int w, int h)
    {
        if (w <= p.Width && h <= p.Height) return PictureScaler.Resize(p, w, h);
        var bgra = new byte[4 * w * h];
        for (int y = 0; y < h; y++)
        {
            double sy = Math.Clamp((y + 0.5) * p.Height / h - 0.5, 0, p.Height - 1);
            int y0 = (int)sy, y1 = Math.Min(y0 + 1, p.Height - 1);
            double fy = sy - y0;
            for (int x = 0; x < w; x++)
            {
                double sx = Math.Clamp((x + 0.5) * p.Width / w - 0.5, 0, p.Width - 1);
                int x0 = (int)sx, x1 = Math.Min(x0 + 1, p.Width - 1);
                double fx = sx - x0;
                for (int c = 0; c < 4; c++)
                {
                    double a = p.Bgra[4 * (y0 * p.Width + x0) + c], b = p.Bgra[4 * (y0 * p.Width + x1) + c];
                    double d = p.Bgra[4 * (y1 * p.Width + x0) + c], e = p.Bgra[4 * (y1 * p.Width + x1) + c];
                    double v = (a * (1 - fx) + b * fx) * (1 - fy) + (d * (1 - fx) + e * fx) * fy;
                    bgra[4 * (y * w + x) + c] = (byte)Math.Round(v);
                }
            }
        }
        return new DecodedPicture(w, h, bgra);
    }

    // ---- the parts of a transmission ----

    private static void WriteVis(ToneWriter t, int vis)
    {
        t.Tone(SstvModes.LeaderHz, SstvModes.LeaderMs);
        t.Tone(SstvModes.SyncHz, SstvModes.BreakMs);
        t.Tone(SstvModes.LeaderHz, SstvModes.LeaderMs);
        t.Tone(SstvModes.SyncHz, SstvModes.BitMs);   // start bit
        int ones = 0;
        for (int i = 0; i < 7; i++)
        {
            int bit = (vis >> i) & 1;
            ones += bit;
            t.Tone(bit == 1 ? SstvModes.VisOneHz : SstvModes.VisZeroHz, SstvModes.BitMs);
        }
        t.Tone(ones % 2 == 1 ? SstvModes.VisOneHz : SstvModes.VisZeroHz, SstvModes.BitMs);   // even parity
        t.Tone(SstvModes.SyncHz, SstvModes.BitMs);   // stop bit
    }

    private static void WriteGbr(ToneWriter t, SstvModeInfo m, Channels c)
    {
        bool scottie = m.Mode is SstvMode.Scottie1 or SstvMode.Scottie2 or SstvMode.ScottieDX;
        if (scottie) t.Tone(SstvModes.SyncHz, m.SyncMs);   // Scottie: one sync before the first line
        for (int y = 0; y < m.Height; y++)
        {
            if (scottie)
            {
                t.Tone(SstvModes.BlackHz, m.GapMs);
                t.Scan(c.G[y], m.ScanMs);
                t.Tone(SstvModes.BlackHz, m.GapMs);
                t.Scan(c.B[y], m.ScanMs);
                t.Tone(SstvModes.SyncHz, m.SyncMs);
                t.Tone(SstvModes.BlackHz, m.GapMs);
                t.Scan(c.R[y], m.ScanMs);
            }
            else
            {
                t.Tone(SstvModes.SyncHz, m.SyncMs);
                t.Tone(SstvModes.BlackHz, m.PorchMs);
                t.Scan(c.G[y], m.ScanMs);
                t.Tone(SstvModes.BlackHz, m.GapMs);
                t.Scan(c.B[y], m.ScanMs);
                t.Tone(SstvModes.BlackHz, m.GapMs);
                t.Scan(c.R[y], m.ScanMs);
                t.Tone(SstvModes.BlackHz, m.GapMs);
            }
        }
    }

    private static void WriteRobot(ToneWriter t, SstvModeInfo m, Channels c)
    {
        double colourMs = m.ScanMs / 2;
        for (int y = 0; y < m.Height; y++)
        {
            t.Tone(SstvModes.SyncHz, m.SyncMs);
            t.Tone(SstvModes.BlackHz, m.PorchMs);
            t.Scan(c.Y[y], m.ScanMs);
            if (m.Mode == SstvMode.Robot36)
            {
                // Robot 36: R-Y on even lines, B-Y on odd ones, each shared by the pair of lines.
                bool even = y % 2 == 0;
                int other = even ? Math.Min(y + 1, m.Height - 1) : y - 1;
                t.Tone(even ? SstvModes.BlackHz : SstvModes.WhiteHz, m.GapMs);
                t.Tone(SstvModes.LeaderHz, 1.5);
                t.Scan(Average(even ? c.Cr[y] : c.Cb[y], even ? c.Cr[other] : c.Cb[other]), colourMs);
            }
            else
            {
                // Robot 72: both colour differences on every line.
                t.Tone(SstvModes.BlackHz, m.GapMs);
                t.Tone(SstvModes.LeaderHz, 1.5);
                t.Scan(c.Cr[y], colourMs);
                t.Tone(SstvModes.WhiteHz, m.GapMs);
                t.Tone(SstvModes.BlackHz, 1.5);
                t.Scan(c.Cb[y], colourMs);
            }
        }
    }

    private static void WritePD(ToneWriter t, SstvModeInfo m, Channels c)
    {
        for (int y = 0; y + 1 < m.Height; y += 2)
        {
            t.Tone(SstvModes.SyncHz, m.SyncMs);
            t.Tone(SstvModes.BlackHz, m.PorchMs);
            t.Scan(c.Y[y], m.ScanMs);
            t.Scan(Average(c.Cr[y], c.Cr[y + 1]), m.ScanMs);
            t.Scan(Average(c.Cb[y], c.Cb[y + 1]), m.ScanMs);
            t.Scan(c.Y[y + 1], m.ScanMs);
        }
    }

    private static double[] Average(double[] a, double[] b)
    {
        var r = new double[a.Length];
        for (int i = 0; i < a.Length; i++) r[i] = (a[i] + b[i]) / 2;
        return r;
    }

    /// <summary>The picture's lines as red, green, blue, and brightness and colour differences (0-255).</summary>
    private sealed class Channels
    {
        public readonly double[][] R, G, B, Y, Cr, Cb;

        public Channels(DecodedPicture p)
        {
            R = new double[p.Height][]; G = new double[p.Height][]; B = new double[p.Height][];
            Y = new double[p.Height][]; Cr = new double[p.Height][]; Cb = new double[p.Height][];
            for (int y = 0; y < p.Height; y++)
            {
                R[y] = new double[p.Width]; G[y] = new double[p.Width]; B[y] = new double[p.Width];
                Y[y] = new double[p.Width]; Cr[y] = new double[p.Width]; Cb[y] = new double[p.Width];
                for (int x = 0; x < p.Width; x++)
                {
                    int i = 4 * (y * p.Width + x);
                    double b = p.Bgra[i], g = p.Bgra[i + 1], r = p.Bgra[i + 2];
                    R[y][x] = r; G[y][x] = g; B[y][x] = b;
                    Y[y][x] = 0.299 * r + 0.587 * g + 0.114 * b;
                    Cr[y][x] = 128 + 0.5 * r - 0.418688 * g - 0.081312 * b;
                    Cb[y][x] = 128 - 0.168736 * r - 0.331264 * g + 0.5 * b;
                }
            }
        }
    }

    /// <summary>Writes tones and scans one after another, each starting at its exact time.</summary>
    private sealed class ToneWriter
    {
        private readonly List<float> _samples = new();
        private double _endMs;      // where the next part starts, from the beginning
        private double _phase;

        public void Tone(double hz, double ms) => Write(ms, _ => hz);

        public void Scan(double[] values, double ms)
        {
            int n = values.Length;
            Write(ms, f => SstvModes.Frequency(values[Math.Min(n - 1, (int)(f * n))]));
        }

        // The part's samples: those whose time falls in [start, end).
        private void Write(double ms, Func<double, double> hzAt)
        {
            double start = _endMs, end = _endMs + ms;
            long first = (long)Math.Ceiling(start * SampleRate / 1000), last = (long)Math.Ceiling(end * SampleRate / 1000);
            for (long s = first; s < last; s++)
            {
                double fraction = (s * 1000.0 / SampleRate - start) / ms;
                _phase += 2 * Math.PI * hzAt(Math.Clamp(fraction, 0, 0.999999)) / SampleRate;
                if (_phase > 2 * Math.PI) _phase -= 2 * Math.PI;
                _samples.Add((float)(Amplitude * Math.Sin(_phase)));
            }
            _endMs = end;
        }

        public float[] ToArray() => _samples.ToArray();
    }
}
