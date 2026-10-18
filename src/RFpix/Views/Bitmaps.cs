// Bitmaps.cs - turns RFpix's own picture pixels into something the screen can show.

using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using RFpix.Imaging;

namespace RFpix.Views;

internal static class Bitmaps
{
    /// <summary>Copies decoded pixels into a picture Avalonia can show.</summary>
    public static WriteableBitmap From(DecodedPicture picture)
    {
        var bitmap = new WriteableBitmap(new Avalonia.PixelSize(picture.Width, picture.Height), new Avalonia.Vector(96, 96),
                                         PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using var buffer = bitmap.Lock();
        int rowBytes = picture.Width * 4;
        for (int y = 0; y < picture.Height; y++)
            Marshal.Copy(picture.Bgra, y * rowBytes, buffer.Address + y * buffer.RowBytes, rowBytes);
        return bitmap;
    }
}
