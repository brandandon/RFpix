// Pictures.cs - opening any picture, and making and reading JPEG and AVIF files.
//
// This is done by Magick.NET (ImageMagick for .NET, Apache licence), which reads nearly
// every picture format, including phone photos (HEIC) and AVIF, and writes AVIF. It
// carries ImageMagick's own code inside it, so nothing else needs installing.
// JPEG 2000 stays with CoreJ2K (Jpeg2000.cs), which makes files of an exact size.
//
// Opening a picture for sending:
//   - it's turned the right way up (phones store "rotate me" in the file instead of
//     turning the pixels), see-through parts become white, and any hidden details the
//     camera stored (location, camera model) are left behind: only the pixels are sent;
//   - a huge picture is first brought down to 4096 on its longer side, which is still
//     far more than HamDRM will carry.
//
// Reading a received picture: it comes off the air from anyone, so its size is read from
// its header first and anything unreasonably large is refused, as for JPEG 2000.

using ImageMagick;

namespace RFpix.Imaging;

public static class Pictures
{
    /// <summary>The largest picture RFpix opens for sending: 100 million pixels (a big camera's photo).</summary>
    public const long MaxOpenPixels = 100_000_000;

    /// <summary>Opens a picture file for sending: the right way up, solid, at most
    /// <see cref="Jpeg2000.MaxSide"/> on its longer side.</summary>
    public static DecodedPicture Load(string path)
    {
        try
        {
            using var info = new MagickImage();
            info.Ping(path);
            if ((long)info.Width * info.Height > MaxOpenPixels)
                throw new PictureException($"This picture is {info.Width} × {info.Height}, too large to open.");

            using var image = new MagickImage(path);
            image.AutoOrient();
            if (Math.Max(image.Width, image.Height) > Jpeg2000.MaxSide)
            {
                var (w, h) = PictureScaler.FitLongSide((int)image.Width, (int)image.Height, Jpeg2000.MaxSide);
                image.Resize((uint)w, (uint)h);
            }
            return ToPicture(image);
        }
        catch (MagickException ex)
        {
            throw new PictureException("RFpix can't open this file as a picture.", ex);
        }
    }

    /// <summary>Makes a JPEG of the picture at a quality from 1 to 100.</summary>
    public static byte[] EncodeJpeg(DecodedPicture picture, int quality)
    {
        using var image = FromPicture(picture);
        image.Quality = (uint)Math.Clamp(quality, 1, 100);
        return image.ToByteArray(MagickFormat.Jpeg);
    }

    /// <summary>Makes an AVIF of the picture at a quality from 1 to 100.</summary>
    public static byte[] EncodeAvif(DecodedPicture picture, int quality)
    {
        using var image = FromPicture(picture);
        image.Quality = (uint)Math.Clamp(quality, 1, 100);
        // How hard the encoder works, 0 (slowest, smallest) to 9: 6 takes a fraction of a
        // second for a HamDRM-sized picture, and the files are within a few percent of the slowest.
        image.Settings.SetDefine(MagickFormat.Heic, "speed", "6");
        return image.ToByteArray(MagickFormat.Avif);
    }

    /// <summary>Makes a picture file in the format at a quality from 1 to 100.</summary>
    public static byte[] Encode(DecodedPicture picture, PictureFormat format, int quality) => format switch
    {
        PictureFormat.Jpeg2000 => Jpeg2000.Encode(picture, quality),
        PictureFormat.Jpeg => EncodeJpeg(picture, quality),
        _ => EncodeAvif(picture, quality),
    };

    /// <summary>Reads a picture file (JPEG 2000, JPEG, AVIF, PNG, ...): one received, or one
    /// just made for sending. Throws <see cref="PictureException"/> with a plain message.</summary>
    public static DecodedPicture Decode(byte[] data)
    {
        if (Jpeg2000.IsJpeg2000(data)) return Jpeg2000.Decode(data);
        try
        {
            using var info = new MagickImage();
            info.Ping(data);
            if (info.Width > Jpeg2000.MaxSide || info.Height > Jpeg2000.MaxSide || (long)info.Width * info.Height > Jpeg2000.MaxPixels)
                throw new PictureException($"This picture is {info.Width} × {info.Height}, larger than RFpix shows " +
                                           $"(up to {Jpeg2000.MaxSide} on a side).");
            using var image = new MagickImage(data);
            image.AutoOrient();
            return ToPicture(image);
        }
        catch (MagickException ex)
        {
            throw new PictureException("This picture is damaged or of a kind RFpix can't show.", ex);
        }
    }

    /// <summary>The image's pixels as RFpix keeps them: blue, green, red, alpha (always solid).</summary>
    private static DecodedPicture ToPicture(MagickImage image)
    {
        image.ColorSpace = ColorSpace.sRGB;
        if (image.HasAlpha)
        {
            image.BackgroundColor = MagickColors.White;
            image.Alpha(AlphaOption.Remove);
        }
        using var pixels = image.GetPixelsUnsafe();
        var bgra = pixels.ToByteArray(PixelMapping.BGRA)
            ?? throw new PictureException("RFpix couldn't read this picture's pixels.");
        // Make sure every pixel is solid, whatever the file said.
        for (int i = 3; i < bgra.Length; i += 4) bgra[i] = 255;
        return new DecodedPicture((int)image.Width, (int)image.Height, bgra);
    }

    /// <summary>A Magick image of the picture's colours (no transparency, nothing but pixels).</summary>
    private static MagickImage FromPicture(DecodedPicture picture)
    {
        var bgr = new byte[picture.Width * picture.Height * 3];
        for (int i = 0, j = 0; i < bgr.Length; i += 3, j += 4)
        {
            bgr[i] = picture.Bgra[j];
            bgr[i + 1] = picture.Bgra[j + 1];
            bgr[i + 2] = picture.Bgra[j + 2];
        }
        var settings = new PixelReadSettings((uint)picture.Width, (uint)picture.Height, StorageType.Char, PixelMapping.BGR);
        var image = new MagickImage();
        image.ReadPixels(bgr, settings);
        return image;
    }
}
