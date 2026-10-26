// OverlayRenderer.cs - draws a template on a picture, with SkiaSharp (the drawing library
// Avalonia itself uses, so text looks as it does on screen). Smooth edges, any installed font.
//
// Each item's rectangle is worked out from the picture's size; text is centred in its
// rectangle from top to bottom, and placed left, centre or right across it. Too long a
// text is made smaller to fit (unless that's turned off). Outlines go round the letters
// before they're filled in, so the letter shapes stay the same; a shadow is drawn first,
// a little below and to the right.

using System.Runtime.InteropServices;
using RFpix.Imaging;
using SkiaSharp;

namespace RFpix.Templates;

public static class OverlayRenderer
{
    /// <summary>A copy of the picture with the template drawn on it.</summary>
    public static DecodedPicture Draw(DecodedPicture picture, OverlayTemplate template, TemplateFields fields)
    {
        int w = picture.Width, h = picture.Height;
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        Marshal.Copy(picture.Bgra, 0, bitmap.GetPixels(), picture.Bgra.Length);   // opaque, so premultiplied is the same
        using (var canvas = new SKCanvas(bitmap))
        {
            foreach (var item in template.Items) DrawItem(canvas, item, w, h, fields);
            canvas.Flush();
        }
        var bgra = new byte[picture.Bgra.Length];
        Marshal.Copy(bitmap.GetPixels(), bgra, 0, bgra.Length);
        for (int i = 3; i < bgra.Length; i += 4) bgra[i] = 255;
        return new DecodedPicture(w, h, bgra);
    }

    /// <summary>An item's rectangle on a picture of this size.</summary>
    public static SKRect Rect(OverlayItem item, int w, int h) =>
        new((float)(item.X * w), (float)(item.Y * h), (float)((item.X + item.Width) * w), (float)((item.Y + item.Height) * h));

    private static SKColor ColorOf(string? text, SKColor fallback) =>
        text is not null && SKColor.TryParse(text, out var c) ? c : fallback;

    private static void DrawItem(SKCanvas canvas, OverlayItem item, int w, int h, TemplateFields fields)
    {
        var rect = Rect(item, w, h);
        switch (item.Kind)
        {
            case OverlayKind.Box: DrawBox(canvas, item, rect, h); break;
            case OverlayKind.Border: DrawBorder(canvas, item, w, h); break;
            case OverlayKind.Image: DrawImage(canvas, item, rect); break;
            case OverlayKind.Received: DrawReceived(canvas, item, rect, h, fields); break;
            default: DrawText(canvas, item, rect, h, fields.Fill(item.Text)); break;
        }
    }

    private static void DrawBox(SKCanvas canvas, OverlayItem item, SKRect rect, int h)
    {
        byte alpha = (byte)Math.Round(Math.Clamp(item.Opacity, 0, 1) * 255);
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        var from = ColorOf(item.Fill, SKColors.Black).WithAlpha(alpha);
        if (item.FillTo is not null)
        {
            var to = ColorOf(item.FillTo, SKColors.Black).WithAlpha(alpha);
            paint.Shader = SKShader.CreateLinearGradient(new SKPoint(rect.Left, rect.MidY), new SKPoint(rect.Right, rect.MidY),
                                                         new[] { from, to }, SKShaderTileMode.Clamp);
        }
        else paint.Color = from;
        float r = (float)(Math.Max(0, item.Corner) * h);
        if (r > 0) canvas.DrawRoundRect(rect, r, r, paint);
        else canvas.DrawRect(rect, paint);
    }

    // A frame round the whole picture (the item's rectangle isn't used).
    private static void DrawBorder(SKCanvas canvas, OverlayItem item, int w, int h)
    {
        float t = (float)Math.Max(1, item.Thickness * h);
        byte alpha = (byte)Math.Round(Math.Clamp(item.Opacity, 0, 1) * 255);
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = t,
                                        Color = ColorOf(item.Fill, SKColors.White).WithAlpha(alpha) };
        float r = (float)(Math.Max(0, item.Corner) * h);
        var rect = new SKRect(t / 2, t / 2, w - t / 2, h - t / 2);
        if (r > 0) canvas.DrawRoundRect(rect, r, r, paint);
        else canvas.DrawRect(rect, paint);
    }

    private static void DrawImage(SKCanvas canvas, OverlayItem item, SKRect rect)
    {
        if (item.ImageData is null) return;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(item.ImageData); } catch (FormatException) { return; }
        using var image = SKBitmap.Decode(bytes);
        if (image is null) return;
        var dest = rect;
        if (item.KeepShape)
        {
            float scale = Math.Min(rect.Width / image.Width, rect.Height / image.Height);
            float dw = image.Width * scale, dh = image.Height * scale;
            dest = new SKRect(rect.MidX - dw / 2, rect.MidY - dh / 2, rect.MidX + dw / 2, rect.MidY + dh / 2);
        }
        using var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High,
                                        Color = SKColors.White.WithAlpha((byte)Math.Round(Math.Clamp(item.Opacity, 0, 1) * 255)) };
        canvas.DrawBitmap(image, dest, paint);
    }

    // The received picture chosen on the Send tab, in its box (whole, keeping its shape, unless
    // that's turned off); with no picture, a grey stand-in in the picker and editor, else nothing.
    private static void DrawReceived(SKCanvas canvas, OverlayItem item, SKRect rect, int h, TemplateFields fields)
    {
        var dest = rect;
        byte alpha = (byte)Math.Round(Math.Clamp(item.Opacity, 0, 1) * 255);
        if (fields.Received is { } picture && picture.Width > 0 && picture.Height > 0)
        {
            var info = new SKImageInfo(picture.Width, picture.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using var image = new SKBitmap(info);
            Marshal.Copy(picture.Bgra, 0, image.GetPixels(), Math.Min(picture.Bgra.Length, info.BytesSize));
            if (item.KeepShape) dest = Fit(rect, picture.Width, picture.Height);
            using var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha(alpha) };
            canvas.DrawBitmap(image, dest, paint);
        }
        else if (fields.Preview)
        {
            if (item.KeepShape) dest = Fit(rect, 4, 3);
            using var fill = new SKPaint { IsAntialias = true, Color = new SKColor(0x70, 0x78, 0x80, 210) };
            canvas.DrawRect(dest, fill);
            using var paint = new SKPaint { IsAntialias = true, Color = SKColors.White, Typeface = SKTypeface.Default,
                                            TextSize = Math.Max(6, Math.Min(dest.Height * 0.16f, dest.Width * 0.1f)) };
            foreach (var (line, row) in new[] { ("Received", -0.6f), ("picture", 0.6f) })
            {
                float width = paint.MeasureText(line);
                canvas.DrawText(line, dest.MidX - width / 2, dest.MidY + row * paint.TextSize + paint.TextSize * 0.35f, paint);
            }
        }
        else return;

        if (item.Frame)
        {
            float t = (float)Math.Max(1, item.Thickness * h);
            using var frame = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = t,
                                            Color = ColorOf(item.Fill, SKColors.White).WithAlpha(alpha) };
            canvas.DrawRect(new SKRect(dest.Left - t / 2, dest.Top - t / 2, dest.Right + t / 2, dest.Bottom + t / 2), frame);
        }
    }

    /// <summary>The largest rectangle of this shape that fits in <paramref name="rect"/>, centred.</summary>
    private static SKRect Fit(SKRect rect, int width, int height)
    {
        float scale = Math.Min(rect.Width / width, rect.Height / height);
        float dw = width * scale, dh = height * scale;
        return new SKRect(rect.MidX - dw / 2, rect.MidY - dh / 2, rect.MidX + dw / 2, rect.MidY + dh / 2);
    }

    private static void DrawText(SKCanvas canvas, OverlayItem item, SKRect rect, int h, string text)
    {
        if (text.Length == 0) return;
        var lines = text.Split('\n');
        using var typeface = SKTypeface.FromFamilyName(item.Font,
            item.Bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal, SKFontStyleWidth.Normal,
            item.Italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright) ?? SKTypeface.Default;
        using var paint = new SKPaint { IsAntialias = true, Typeface = typeface, TextSize = (float)Math.Max(1, item.Size * h) };

        // Too wide for the rectangle: smaller letters.
        float widest = lines.Max(l => paint.MeasureText(l));
        if (item.ShrinkToFit && widest > rect.Width && widest > 0)
        {
            paint.TextSize *= rect.Width / widest;
            widest = rect.Width;
        }
        var metrics = paint.FontMetrics;
        float lineHeight = paint.FontSpacing;
        float textHeight = -metrics.Ascent + metrics.Descent + lineHeight * (lines.Length - 1);
        float baseline = rect.MidY - textHeight / 2 - metrics.Ascent;
        float outline = item.OutlineColor is null ? 0 : (float)(Math.Max(0, item.Outline) * paint.TextSize);
        float shadow = paint.TextSize * 0.06f;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            float width = paint.MeasureText(line);
            float x = item.Align switch
            {
                OverlayAlign.Center => rect.MidX - width / 2,
                OverlayAlign.Right => rect.Right - width,
                _ => rect.Left,
            };
            float y = baseline + i * lineHeight;
            if (item.Shadow)
            {
                paint.Style = SKPaintStyle.Fill;
                paint.Color = new SKColor(0, 0, 0, 150);
                canvas.DrawText(line, x + shadow, y + shadow, paint);
            }
            if (outline > 0)
            {
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = 2 * outline;   // half of it is under the letters
                paint.StrokeJoin = SKStrokeJoin.Round;
                paint.Color = ColorOf(item.OutlineColor, SKColors.Black);
                canvas.DrawText(line, x, y, paint);
            }
            paint.Style = SKPaintStyle.Fill;
            paint.Color = ColorOf(item.Color, SKColors.White);
            canvas.DrawText(line, x, y, paint);
        }
    }
}
