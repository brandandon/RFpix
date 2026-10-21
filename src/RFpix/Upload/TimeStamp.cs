// TimeStamp.cs - writes the date and time on a copy of a picture, as MMSSTV does
// ("OCT-20-2026 1942" in white on a black strip in the lower left corner).
//
// The letters are the waterfall text font (Send/WaterfallFont.cs), made bigger on bigger
// pictures so the stamp stays about the same size compared with the picture.

using RFpix.Imaging;
using RFpix.Send;

namespace RFpix.Upload;

public static class TimeStamp
{
    /// <summary>The stamp's text, e.g. "OCT-20-2026 1942".</summary>
    public static string Text(DateTime when) =>
        $"{when.ToString("MMM", System.Globalization.CultureInfo.InvariantCulture).ToUpperInvariant()}-{when:dd-yyyy HHmm}";

    // The font's letters sit in rows 1 to 12 (capitals and digits); only those are drawn.
    private const int FirstRow = 1, Rows = 12;

    /// <summary>A copy of the picture with the text in the lower left corner.</summary>
    public static DecodedPicture Stamp(DecodedPicture picture, string text)
    {
        var bgra = (byte[])picture.Bgra.Clone();
        int w = picture.Width, h = picture.Height;
        // 1 pixel per font pixel up to 400 tall, then bigger: about 3-4% of the height.
        int scale = Math.Max(1, (int)Math.Round(h / 400.0));
        int textWidth = text.Sum(c => Glyph(c).Width) * scale;
        int pad = 2 * scale;
        int boxW = Math.Min(w, textWidth + 2 * pad), boxH = Math.Min(h, Rows * scale + 2 * pad);
        int top = h - boxH;

        // The black strip.
        for (int y = top; y < h; y++)
            for (int x = 0; x < boxW; x++)
            {
                int i = 4 * (y * w + x);
                bgra[i] = bgra[i + 1] = bgra[i + 2] = 0;
            }
        // The letters, in white.
        int x0 = pad;
        foreach (char c in text)
        {
            var g = Glyph(c);
            for (int row = 0; row < Rows; row++)
            {
                uint bits = g.Rows[FirstRow + row];
                for (int col = 0; col < g.Width; col++)
                {
                    if ((bits >> col & 1) == 0) continue;
                    for (int sy = 0; sy < scale; sy++)
                        for (int sx = 0; sx < scale; sx++)
                        {
                            int x = x0 + col * scale + sx, y = top + pad + row * scale + sy;
                            if (x >= w || y >= h) continue;
                            int i = 4 * (y * w + x);
                            bgra[i] = bgra[i + 1] = bgra[i + 2] = 255;
                        }
                }
            }
            x0 += g.Width * scale;
        }
        return new DecodedPicture(w, h, bgra);
    }

    private static WaterfallGlyph Glyph(char c) =>
        WaterfallFont.Glyphs.TryGetValue(c, out var g) ? g : WaterfallFont.Glyphs[' '];
}
