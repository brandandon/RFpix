// WaterfallPicture.cs - a small grey picture drawn on the waterfall ("WF PIC").
//
// The same idea as waterfall text (WaterfallText.cs): each row of the picture is played as
// a short sound with a tone for every pixel, so the picture scrolls onto every waterfall
// listening. Text has only on and off pixels; a picture has shades of grey, sent as tones of
// different strength. A waterfall shows strength in decibels, so the shades are spread over
// 30 dB: white is full strength, mid-grey about 15 dB quieter, and black is silent.
//
// The picture is made 98 pixels wide at most (the waterfall text's grid: 300 to 2600 Hz in
// 23.4 Hz steps), and 40, 62 or 90 rows tall (about 5, 8 or 12 seconds at 128 ms a row),
// keeping its shape. A waterfall pixel is roughly as tall as it is wide on most screens.
// Logos and simple drawings come out best; a photo at this size is very rough.

using RFpix.Imaging;

namespace RFpix.Send;

public static class WaterfallPicture
{
    /// <summary>The heights offered, in rows: small, medium and large.</summary>
    public static readonly int[] Heights = { 40, 62, 90 };

    /// <summary>The span of shades on the waterfall, from the faintest grey to white.</summary>
    public const double RangeDb = 30;

    /// <summary>Darker than this is sent as silence (black).</summary>
    public const double Black = 0.08;

    /// <summary>The picture as tone strengths: rows (top first) of <see cref="WaterfallText.Columns"/>
    /// values from 0 (silent) to 1 (full), centred across.</summary>
    /// <param name="rows">The most rows (the picture is made to fit, keeping its shape).</param>
    /// <param name="invert">Swap light and dark (a dark logo on white often looks better reversed).</param>
    public static double[][] Levels(DecodedPicture picture, int rows, bool invert)
    {
        int columns = WaterfallText.Columns;
        // Fit inside columns x rows, keeping the shape.
        double scale = Math.Min((double)columns / picture.Width, (double)rows / picture.Height);
        int w = Math.Clamp((int)Math.Round(picture.Width * scale), 1, columns);
        int h = Math.Clamp((int)Math.Round(picture.Height * scale), 1, rows);
        var small = picture.Width == w && picture.Height == h ? picture : PictureScaler.Resize(picture, w, h);

        int left = (columns - w) / 2;
        var levels = new double[h][];
        for (int y = 0; y < h; y++)
        {
            levels[y] = new double[columns];
            for (int x = 0; x < w; x++)
            {
                int i = 4 * (y * w + x);   // BGRA
                double brightness = (0.114 * small.Bgra[i] + 0.587 * small.Bgra[i + 1] + 0.299 * small.Bgra[i + 2]) / 255;
                if (invert) brightness = 1 - brightness;
                levels[y][left + x] = Level(brightness);
            }
        }
        return levels;
    }

    /// <summary>A shade (0 black to 1 white) as a tone's strength: spread over 30 dB, black silent.</summary>
    public static double Level(double brightness) =>
        brightness < Black ? 0 : Math.Pow(10, -(1 - brightness) * RangeDb / 20);

    /// <summary>The shade a strength shows as on a waterfall (for the preview).</summary>
    public static double Shade(double level) =>
        level <= 0 ? 0 : Math.Clamp(1 + 20 * Math.Log10(level) / RangeDb, 0, 1);

    /// <summary>The sound: 48 kHz, -1 to 1, bottom row first (as waterfall text).</summary>
    public static float[] Audio(double[][] levels) => WaterfallText.RowsAudio(levels);

    /// <summary>How long a picture of this many rows takes to send.</summary>
    public static TimeSpan Duration(int rows) =>
        TimeSpan.FromSeconds((double)rows * WaterfallText.RowSamples * WaterfallText.RowRepeats / WaterfallText.SampleRate);

    /// <summary>The preview: the picture as it shows on a waterfall (white on dark blue), one pixel per tone.</summary>
    public static DecodedPicture Preview(double[][] levels)
    {
        int columns = WaterfallText.Columns, h = levels.Length;
        var bgra = new byte[4 * columns * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < columns; x++)
            {
                double s = Shade(levels[y][x]);
                int i = 4 * (y * columns + x);
                bgra[i] = (byte)(0x40 + s * (255 - 0x40));   // from dark blue (#0B1A40) to white
                bgra[i + 1] = (byte)(0x1A + s * (255 - 0x1A));
                bgra[i + 2] = (byte)(0x0B + s * (255 - 0x0B));
                bgra[i + 3] = 255;
            }
        return new DecodedPicture(columns, h, bgra);
    }
}
