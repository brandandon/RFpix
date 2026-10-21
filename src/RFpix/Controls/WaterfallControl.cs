// WaterfallControl.cs - draws the scrolling waterfall.
//
// The picture is kept as a grid of coloured pixels: one column per frequency
// step, one row per line. Each new line is painted at the top and everything
// else moves down one row, so signals "fall" down the screen over time.
// A frequency scale (in Hz) is drawn along the bottom, and marks at HamDRM's three
// key frequencies: 725, 1475 and 1850 Hz, where every HamDRM signal (any mode) has a
// steady tone, its "frequency pilots". Those show as three bright lines in the signal;
// when they sit on the marks, the signal is where RFpix expects it.

using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace RFpix.Controls;

public sealed class WaterfallControl : Control
{
    private const int HistoryLines = 400;        // how many lines are kept on screen
    private const double ScaleHeight = 20;       // room for the Hz labels at the bottom

    private static readonly int[] Palette = BuildPalette();

    private WriteableBitmap? _bitmap;
    private int[] _pixels = Array.Empty<int>();
    private int _columns;
    private double _noiseFloor = double.NaN;

    /// <summary>HamDRM's frequency pilots, the same in every mode (350 Hz + carrier x spacing).</summary>
    public static readonly double[] KeyFrequencies = { 725, 1475, 1850 };

    private static readonly IPen KeyPen = new Pen(new SolidColorBrush(Color.FromArgb(150, 255, 211, 77)), 1);

    /// <summary>The frequency at the right-hand edge, in Hz (set from the incoming lines).</summary>
    public double DisplayMaxFrequency { get; private set; } = 3000;

    /// <summary>Add one line (dB per frequency step) to the top of the waterfall.</summary>
    public void AddLine(float[] db, double hzPerStep)
    {
        if (db.Length == 0) return;

        // First line, or the width changed: start a fresh, black picture.
        if (_bitmap is null || db.Length != _columns)
        {
            _columns = db.Length;
            _pixels = new int[_columns * HistoryLines];
            Array.Fill(_pixels, Palette[0]);
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(new PixelSize(_columns, HistoryLines), new Vector(96, 96),
                                          PixelFormat.Bgra8888, AlphaFormat.Premul);
        }
        DisplayMaxFrequency = _columns * hzPerStep;

        // Automatic brightness: follow the band noise level slowly, so the
        // background stays dark blue whatever the radio's audio level is.
        double average = 0;
        foreach (var v in db) average += v;
        average /= db.Length;
        _noiseFloor = double.IsNaN(_noiseFloor) ? average : _noiseFloor * 0.97 + average * 0.03;

        // Move every row down one, then paint the new line in row 0.
        Array.Copy(_pixels, 0, _pixels, _columns, _columns * (HistoryLines - 1));
        for (int x = 0; x < _columns; x++)
        {
            // Map "noise floor - 5 dB" .. "noise floor + 40 dB" onto the colour palette.
            double t = (db[x] - _noiseFloor + 5) / 45;
            _pixels[x] = Palette[(int)Math.Clamp(t * 255, 0, 255)];
        }

        // Copy our pixel grid into the bitmap that gets drawn on screen.
        using (var frame = _bitmap.Lock())
        {
            for (int y = 0; y < HistoryLines; y++)
            {
                Marshal.Copy(_pixels, y * _columns, frame.Address + y * frame.RowBytes, _columns);
            }
        }

        InvalidateVisual();   // ask Avalonia to redraw us
    }

    public override void Render(DrawingContext context)
    {
        var picture = new Rect(0, 0, Bounds.Width, Math.Max(0, Bounds.Height - ScaleHeight));
        context.FillRectangle(Brushes.Black, picture);

        if (_bitmap is not null)
        {
            context.DrawImage(_bitmap, new Rect(0, 0, _columns, HistoryLines), picture);
        }

        // The key frequency marks, top to bottom of the picture.
        foreach (double hz in KeyFrequencies)
        {
            double x = Math.Round(hz / DisplayMaxFrequency * Bounds.Width) + 0.5;   // on a whole pixel: crisp
            context.DrawLine(KeyPen, new Point(x, picture.Top), new Point(x, picture.Bottom));
        }

        // Frequency scale: a tick and a label every 500 Hz.
        var pen = new Pen(Brushes.Gray, 1);
        for (int hz = 0; hz <= DisplayMaxFrequency; hz += 500)
        {
            double x = hz / DisplayMaxFrequency * Bounds.Width;
            context.DrawLine(pen, new Point(x, picture.Bottom), new Point(x, picture.Bottom + 4));

            var label = new FormattedText(hz.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                                          FlowDirection.LeftToRight, Typeface.Default, 11, Brushes.Gray);
            double labelX = Math.Clamp(x - label.Width / 2, 0, Math.Max(0, Bounds.Width - label.Width));
            context.DrawText(label, new Point(labelX, picture.Bottom + 4));
        }
    }

    // The colours, from quiet to loud: black, dark blue, cyan, yellow, red, white.
    private static int[] BuildPalette()
    {
        (double pos, int r, int g, int b)[] stops =
        {
            (0.00,   0,   0,   0),
            (0.25,   0,   0, 140),
            (0.50,   0, 180, 255),
            (0.70, 255, 230,   0),
            (0.90, 255,  40,   0),
            (1.00, 255, 255, 255),
        };

        var palette = new int[256];
        for (int i = 0; i < 256; i++)
        {
            double t = i / 255.0;
            int s = 0;
            while (s < stops.Length - 2 && t > stops[s + 1].pos) s++;
            var (p0, r0, g0, b0) = stops[s];
            var (p1, r1, g1, b1) = stops[s + 1];
            double f = (t - p0) / (p1 - p0);
            int r = (int)(r0 + (r1 - r0) * f);
            int g = (int)(g0 + (g1 - g0) * f);
            int b = (int)(b0 + (b1 - b0) * f);
            palette[i] = unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;   // opaque, in BGRA order
        }
        return palette;
    }
}
