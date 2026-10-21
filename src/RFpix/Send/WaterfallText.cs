// WaterfallText.cs - sound that writes words on the waterfall.
//
// A waterfall shows frequency across and time down. So text can be "drawn" on it: for each
// row of the letters' pixels, play a short sound containing a tone for every lit pixel,
// each pixel at its own frequency. Row after row, the letters scroll onto everyone's
// waterfall, readable left to right like any text. (EasyPal and QSSTV do the same, and
// this follows QSSTV's numbers, so the text looks the same on their waterfalls.)
//
// The numbers:
//   - The text sits between 300 and 2600 Hz, one pixel per 23.4 Hz step (48000 / 2048):
//     98 pixels across. A short text, like a callsign, is drawn up to 3 times bigger.
//   - Each row lasts 128 ms (2048 samples, played 3 times). Rows are sent bottom row
//     first: a waterfall adds new lines at the top, so the first row sent ends up lowest.
//   - A long text is split into lines (at spaces), sent one under another.
//   - The tones of a row start at different points in their cycle (a "chirp"), so they
//     don't all peak together: the sound stays smooth instead of spiky.
//   - Each row fades into the next over 5 ms (and the text fades in and out at its ends),
//     so the changes don't click and smear faint lines across the waterfall.

namespace RFpix.Send;

public static class WaterfallText
{
    public const int SampleRate = 48000;

    /// <summary>Samples in one pixel row's sound, and the frequency step between pixels (23.4 Hz).</summary>
    public const int RowSamples = 2048;
    public const double PixelHz = (double)SampleRate / RowSamples;

    /// <summary>How many times each row's sound is played: 3 x 2048 samples = 128 ms per row.</summary>
    public const int RowRepeats = 3;

    /// <summary>The lowest and highest frequency used (as QSSTV).</summary>
    public const double LowHz = 300, HighHz = 2600;

    /// <summary>Pixels across: 98.</summary>
    public static int Columns => (int)((HighHz - LowHz) / PixelHz);

    /// <summary>The most a short text is enlarged.</summary>
    public const int MaxScale = 3;

    /// <summary>How long one row takes to fade into the next: 5 ms.</summary>
    private const int FadeSamples = 240;

    /// <summary>The longest text that can be typed in Settings.</summary>
    public const int MaxLength = 40;

    /// <summary>The text as pixels: rows (top first) of on/off columns, at most <see cref="Columns"/>
    /// wide, centred. Empty for blank text.</summary>
    public static bool[][] Pixels(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return Array.Empty<bool[]>();
        if (text.Length > MaxLength) text = text[..MaxLength];

        // Split into lines that fit across, at spaces where possible.
        var lines = new List<string>();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string tryLine = lines.Count > 0 ? lines[^1] + " " + word : word;
            if (lines.Count > 0 && Width(tryLine) <= Columns) lines[^1] = tryLine;
            else lines.Add(word);
        }
        // A single word too wide for a line is cut into pieces that fit.
        lines = lines.SelectMany(SplitToFit).ToList();

        // Enlarge a short single line (a callsign, say) as much as fits, up to 3 times.
        int scale = lines.Count == 1 ? Math.Clamp(Columns / Math.Max(1, Width(lines[0])), 1, MaxScale) : 1;

        var rows = new List<bool[]>();
        foreach (var line in lines)
        {
            var linePixels = Trimmed(Draw(line));
            if (rows.Count > 0) rows.Add(new bool[Columns]);   // a blank row between lines
            foreach (var row in linePixels)
            {
                var wide = new bool[Columns];
                int width = row.Length * scale, left = (Columns - width) / 2;
                for (int x = 0; x < width; x++) wide[left + x] = row[x / scale];
                for (int s = 0; s < scale; s++) rows.Add(wide);
            }
        }
        return rows.ToArray();
    }

    /// <summary>The sound for the text: 48 kHz, -1 to 1. Empty for blank text.</summary>
    public static float[] Audio(string text) =>
        RowsAudio(Pixels(text).Select(r => r.Select(on => on ? 1.0 : 0.0).ToArray()).ToArray());

    /// <summary>The sound for any picture on the waterfall: rows (top first) of <see cref="Columns"/>
    /// strengths, 0 (silent) to 1 (full). Text is all 0s and 1s; WaterfallPicture uses shades between.</summary>
    public static float[] RowsAudio(double[][] rows)
    {
        int columns = Columns, first = (int)Math.Round(LowHz / PixelHz);
        // Each pixel's tone: its strength, and its starting point in the cycle (the chirp).
        // At full strength a row of all pixels is about -18 dBFS, as QSSTV.
        double amplitude = 6000.0 / 32768 / Math.Sqrt(columns);
        var phase = new double[columns];
        for (int i = 0; i < columns; i++) phase[i] = -Math.PI / columns * i * i;

        var audio = new float[rows.Length * RowSamples * RowRepeats];
        var row = new double[RowSamples];
        var previous = new double[RowSamples];   // the row before (silence before the first)
        for (int r = 0; r < rows.Length; r++)
        {
            var pixels = rows[rows.Length - 1 - r];   // bottom row first
            Array.Clear(row);
            for (int c = 0; c < columns && c < pixels.Length; c++)
            {
                double level = Math.Clamp(pixels[c], 0, 1);
                if (level <= 0) continue;
                int bin = first + c;   // a whole number of cycles in 2048 samples, so the repeats join smoothly
                for (int n = 0; n < RowSamples; n++)
                    row[n] += level * amplitude * Math.Cos(2 * Math.PI * bin * n / RowSamples + phase[c]);
            }
            int start = r * RowSamples * RowRepeats;
            for (int k = 0; k < RowRepeats * RowSamples; k++)
            {
                double v = row[k % RowSamples];
                if (k < FadeSamples)   // fade over from the row before
                {
                    double w = 0.5 - 0.5 * Math.Cos(Math.PI * k / FadeSamples);
                    v = w * v + (1 - w) * previous[k % RowSamples];
                }
                audio[start + k] = (float)v;
            }
            (previous, row) = (row, previous);
        }
        // Fade out at the end.
        for (int k = 0; k < FadeSamples && k < audio.Length; k++)
            audio[audio.Length - 1 - k] *= (float)(0.5 - 0.5 * Math.Cos(Math.PI * k / FadeSamples));
        return audio;
    }

    /// <summary>The silence between the waterfall text and the picture: 0.2 s.</summary>
    public const double GapSeconds = 0.2;

    /// <summary>A picture's sound with waterfall text before and after it (either may be blank),
    /// each separated from the picture by a short silence.</summary>
    public static float[] Around(float[] signal, string? before, string? after)
    {
        var first = Audio(before ?? "");
        var last = Audio(after ?? "");
        int gap = (int)(GapSeconds * SampleRate);
        int gapBefore = first.Length > 0 ? gap : 0, gapAfter = last.Length > 0 ? gap : 0;
        var sound = new float[first.Length + gapBefore + signal.Length + gapAfter + last.Length];
        first.CopyTo(sound, 0);
        signal.CopyTo(sound, first.Length + gapBefore);
        last.CopyTo(sound, first.Length + gapBefore + signal.Length + gapAfter);
        return sound;
    }

    /// <summary>How much time waterfall text before and after adds to a transmission.</summary>
    public static TimeSpan Extra(string? before, string? after)
    {
        double seconds = 0;
        foreach (var text in new[] { before, after })
        {
            var d = Duration(text ?? "");
            if (d > TimeSpan.Zero) seconds += d.TotalSeconds + GapSeconds;
        }
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>How long the text takes to send.</summary>
    public static TimeSpan Duration(string text) =>
        TimeSpan.FromSeconds((double)Pixels(text).Length * RowSamples * RowRepeats / SampleRate);

    // ---- drawing with the font ----

    private static WaterfallGlyph Glyph(char c) =>
        WaterfallFont.Glyphs.TryGetValue(c, out var g) ? g : WaterfallFont.Glyphs['?'];

    private static int Width(string line) => line.Sum(c => Glyph(c).Width);

    private static IEnumerable<string> SplitToFit(string line)
    {
        while (Width(line) > Columns)
        {
            int n = 1;
            while (n < line.Length && Width(line[..(n + 1)]) <= Columns) n++;
            yield return line[..n];
            line = line[n..];
        }
        if (line.Length > 0) yield return line;
    }

    /// <summary>A line of text as pixel rows, full font height.</summary>
    private static bool[][] Draw(string line)
    {
        int width = Width(line);
        var rows = new bool[WaterfallFont.Height][];
        for (int y = 0; y < rows.Length; y++) rows[y] = new bool[width];
        int x0 = 0;
        foreach (char c in line)
        {
            var g = Glyph(c);
            for (int y = 0; y < WaterfallFont.Height; y++)
                for (int x = 0; x < g.Width; x++)
                    if ((g.Rows[y] >> x & 1) != 0) rows[y][x0 + x] = true;
            x0 += g.Width;
        }
        return rows;
    }

    /// <summary>Without the empty rows above and below the letters, and empty columns at the sides.</summary>
    private static bool[][] Trimmed(bool[][] rows)
    {
        var used = rows.Select((r, i) => (r, i)).Where(t => t.r.Any(p => p)).Select(t => t.i).ToList();
        if (used.Count == 0) return Array.Empty<bool[]>();
        int left = rows.Min(r => Array.IndexOf(r, true) is var i && i >= 0 ? i : int.MaxValue);
        int right = rows.Max(r => Array.LastIndexOf(r, true));
        return rows[used[0]..(used[^1] + 1)].Select(r => r[left..(right + 1)]).ToArray();
    }
}
