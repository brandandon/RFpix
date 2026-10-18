// PictureScaler.cs - makes a picture smaller, for sending.
//
// Fewer pixels make a smaller file before any compression, so the optimizer starts here.
// Each new pixel is the average of the old pixels it covers (an "area average"): when a
// 1600-wide photo becomes 640 wide, each new pixel covers 2.5 old ones across, and
// partly-covered ones count for the part they cover. That keeps detail smooth, without
// the jagged edges of just picking every 2.5th pixel. It's done across, then down.
//
// RFpix never makes a picture bigger than it is: that adds bytes, not detail.

namespace RFpix.Imaging;

public static class PictureScaler
{
    /// <summary>The size a picture becomes when its longer side is <paramref name="longSide"/>,
    /// keeping its shape. Never larger than it is, and at least 1 × 1.</summary>
    public static (int Width, int Height) FitLongSide(int width, int height, int longSide)
    {
        int longest = Math.Max(width, height);
        if (longSide >= longest) return (width, height);
        double scale = (double)longSide / longest;
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    /// <summary>A copy of the picture at the new size (only ever smaller; a larger size
    /// gives back the picture unchanged).</summary>
    public static DecodedPicture Resize(DecodedPicture picture, int width, int height)
    {
        width = Math.Clamp(width, 1, picture.Width);
        height = Math.Clamp(height, 1, picture.Height);
        if (width == picture.Width && height == picture.Height) return picture;

        // Across: each row of the old picture becomes a row of the new width.
        var across = new float[picture.Height * width * 4];
        var columns = Spans(picture.Width, width);
        for (int y = 0; y < picture.Height; y++)
        {
            int rowIn = y * picture.Width * 4, rowOut = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                var (first, weights) = columns[x];
                float b = 0, g = 0, r = 0;
                for (int i = 0; i < weights.Length; i++)
                {
                    int at = rowIn + (first + i) * 4;
                    float w = weights[i];
                    b += w * picture.Bgra[at];
                    g += w * picture.Bgra[at + 1];
                    r += w * picture.Bgra[at + 2];
                }
                int o = rowOut + x * 4;
                across[o] = b; across[o + 1] = g; across[o + 2] = r;
            }
        }

        // Down: each column of that becomes a column of the new height.
        var result = new byte[width * height * 4];
        var rows = Spans(picture.Height, height);
        for (int y = 0; y < height; y++)
        {
            var (first, weights) = rows[y];
            for (int x = 0; x < width; x++)
            {
                float b = 0, g = 0, r = 0;
                for (int i = 0; i < weights.Length; i++)
                {
                    int at = ((first + i) * width + x) * 4;
                    float w = weights[i];
                    b += w * across[at];
                    g += w * across[at + 1];
                    r += w * across[at + 2];
                }
                int o = (y * width + x) * 4;
                result[o] = ToByte(b);
                result[o + 1] = ToByte(g);
                result[o + 2] = ToByte(r);
                result[o + 3] = 255;
            }
        }
        return new DecodedPicture(width, height, result);
    }

    /// <summary>For each new pixel along a line of <paramref name="from"/> old ones: the first
    /// old pixel it covers, and how much each old pixel counts (adding up to 1).</summary>
    private static (int First, float[] Weights)[] Spans(int from, int to)
    {
        double step = (double)from / to;   // old pixels per new pixel, 1 or more
        var spans = new (int, float[])[to];
        for (int i = 0; i < to; i++)
        {
            double start = i * step, end = Math.Min(from, (i + 1) * step);
            int first = (int)Math.Floor(start), last = Math.Min(from - 1, (int)Math.Ceiling(end) - 1);
            var weights = new float[last - first + 1];
            for (int k = first; k <= last; k++)
            {
                double covered = Math.Min(end, k + 1) - Math.Max(start, k);
                weights[k - first] = (float)(covered / (end - start));
            }
            spans[i] = (first, weights);
        }
        return spans;
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)(v + 0.5f), 0, 255);
}
