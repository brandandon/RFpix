// Jpeg2000.cs - turns a received JPEG 2000 picture into pixels the screen can show,
// and makes JPEG 2000 pictures for sending.
//
// EasyPal sends JPEG 2000 by default, because at the small sizes HamDRM uses it looks
// better than JPEG for the same number of bytes. Avalonia can't read it by itself, so
// the decoding is done by CoreJ2K (a JPEG 2000 library written in C#, BSD licence).
//
// A picture comes off the air from anyone, so before decoding we read its size from
// the file's own header and refuse anything unreasonably large: a few bytes can claim
// to be a picture of a billion pixels, which would use up the computer's memory.

using CoreJ2K;
using CoreJ2K.Configuration;
using System.Globalization;
using CoreJ2K.Util;
using CoreJ2K.j2k.util;

namespace RFpix.Imaging;

/// <summary>A decoded picture: 4 bytes per pixel (blue, green, red, alpha), row by row, top first.</summary>
public sealed record DecodedPicture(int Width, int Height, byte[] Bgra);

/// <summary>A picture that couldn't be decoded; the message is meant for the operator.</summary>
public sealed class PictureException(string message, Exception? inner = null) : Exception(message, inner);

public static class Jpeg2000
{
    static Jpeg2000()
    {
        // CoreJ2K writes warnings about damaged files to the console; RFpix tells the
        // operator itself, so they're dropped.
//
// Sending: CoreJ2K's encoder is told how many bits per pixel to use, so it makes a file
// of almost exactly the size asked for. QSSTV and EasyPal read its files (checked with
// OpenJPEG, the library QSSTV uses).
        FacilityManager.DefaultMsgLogger = new QuietLogger();
    }

    private sealed class QuietLogger : IMsgLogger
    {
        public void printmsg(int sev, string msg) { }
        public void println(string str, int flind, int ind) { }
        public void flush() { }
    }

    /// <summary>The largest picture RFpix will decode: 4096 on a side and 9 million pixels
    /// (a 4K screen is 3840 × 2160). HamDRM pictures are far smaller.</summary>
    public const int MaxSide = 4096;
    public const long MaxPixels = 9_000_000;

    // A JP2 file starts with this 12-byte "signature box"; a bare codestream starts with
    // the SOC and SIZ markers (FF 4F FF 51).
    private static readonly byte[] Jp2Signature = { 0, 0, 0, 0x0C, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A };
    private static readonly byte[] CodestreamStart = { 0xFF, 0x4F, 0xFF, 0x51 };

    /// <summary>True if the data is a JPEG 2000 picture (going by its first bytes, not its name).</summary>
    public static bool IsJpeg2000(ReadOnlySpan<byte> data) =>
        data.StartsWith(Jp2Signature) || data.StartsWith(CodestreamStart);

    /// <summary>The picture's size and number of colour components, from its SIZ header,
    /// or null if there's no readable header.</summary>
    public static (int Width, int Height, int Components)? ReadSize(ReadOnlySpan<byte> data)
    {
        // The SIZ header is the first thing in the codestream, both in a bare codestream
        // and inside a JP2 file's "jp2c" box. Its layout: FF 51, length (2 bytes),
        // capabilities (2), image width and height (4 each), image offset x and y (4 each),
        // tile size and tile offset (16), number of components (2).
        int at = data.IndexOf(CodestreamStart);
        if (at < 0 || at + 4 + 38 > data.Length) return null;
        var siz = data[(at + 2)..];
        long width = ReadUInt32(siz, 6) - ReadUInt32(siz, 14);
        long height = ReadUInt32(siz, 10) - ReadUInt32(siz, 18);
        int components = (siz[38] << 8) | siz[39];
        if (width <= 0 || height <= 0 || width > int.MaxValue || height > int.MaxValue) return null;
        return ((int)width, (int)height, components);
    }

    /// <summary>Decodes a JPEG 2000 picture. Throws <see cref="PictureException"/> with a
    /// plain message if it's too large, damaged or of an unusual kind.</summary>
    public static DecodedPicture Decode(byte[] data)
    {
        var size = ReadSize(data) ?? throw new PictureException("This JPEG 2000 picture is damaged and can't be shown.");
        if (size.Width > MaxSide || size.Height > MaxSide || (long)size.Width * size.Height > MaxPixels)
            throw new PictureException($"This JPEG 2000 picture is {size.Width} × {size.Height}, larger than RFpix shows " +
                                       $"(up to {MaxSide} on a side).");
        if (size.Components is < 1 or > 4)
            throw new PictureException($"This JPEG 2000 picture has {size.Components} colour components; RFpix shows 1 to 4.");

        InterleavedImage image;
        try
        {
            image = J2kImage.FromBytes(data);
        }
        catch
        {
            // CoreJ2K trips over some files' colour description (QSSTV's, with 4 components,
            // for one), so try again reading just the colours as stored. They're plain red,
            // green and blue (or grey) in pictures from QSSTV and EasyPal.
            try
            {
                image = J2kImage.FromBytes(data, new J2KDecoderConfiguration().WithColorSpace(false));
            }
            catch (Exception ex)
            {
                throw new PictureException("This JPEG 2000 picture is damaged and can't be shown.", ex);
            }
        }

        using (image)
        {
            int w = image.Width, h = image.Height, n = image.NumberOfComponents;
            if (w <= 0 || h <= 0 || (long)w * h > MaxPixels || n is < 1 or > 4)
                throw new PictureException("This JPEG 2000 picture is damaged and can't be shown.");

            // Components: 1 or 2 = grey, 3 or 4 = red, green, blue. A 2nd or 4th component is
            // transparency (QSSTV sends one, fully solid); see-through parts have no meaning for a
            // picture off the air, so pictures are always shown solid.
            // Each component may have more or fewer than 8 bits; it's scaled to 0-255.
            bool colour = n >= 3;
            var planes = new byte[colour ? 3 : 1][];
            for (int c = 0; c < planes.Length; c++) planes[c] = To8Bits(image.GetComponent(c), image.GetBitDepth(c));

            var bgra = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++)
            {
                byte r = planes[0][i], g = colour ? planes[1][i] : r, b = colour ? planes[2][i] : r;
                bgra[i * 4] = b;
                bgra[i * 4 + 1] = g;
                bgra[i * 4 + 2] = r;
                bgra[i * 4 + 3] = 255;
            }
            return new DecodedPicture(w, h, bgra);
        }
    }

    // ---- sending ----

    /// <summary>The bits per pixel for a quality from 1 to 100 (the slider on the Send tab):
    /// from 0.05 at 1 to 3 at 100, each step about 4% more. JPEG 2000 has no quality
    /// setting of its own, only a size, so this makes one.</summary>
    public static double BitsPerPixel(double quality) => 0.05 * Math.Pow(60, (Math.Clamp(quality, 1, 100) - 1) / 99);

    /// <summary>Makes a JPEG 2000 file (JP2) of the picture at a quality from 1 to 100.</summary>
    public static byte[] Encode(DecodedPicture picture, int quality) =>
        EncodeToSize(picture, (int)(BitsPerPixel(quality) * picture.Width * picture.Height / 8));

    /// <summary>The smallest file asked of the encoder: its headers alone take a few hundred bytes.</summary>
    private const int MinBytes = 400;

    /// <summary>Makes a JPEG 2000 file (JP2) of the picture, no larger than <paramref name="maxBytes"/>
    /// (and usually within 1% of it). The smallest it makes is about 400 bytes.</summary>
    public static byte[] EncodeToSize(DecodedPicture picture, int maxBytes)
    {
        int w = picture.Width, h = picture.Height;
        // Red, green and blue, each centred on zero as the encoder expects (0-255 becomes -128 to 127).
        var planes = new int[3][];
        for (int c = 0; c < 3; c++) planes[c] = new int[w * h];
        for (int i = 0; i < w * h; i++)
        {
            planes[0][i] = picture.Bgra[i * 4 + 2] - 128;
            planes[1][i] = picture.Bgra[i * 4 + 1] - 128;
            planes[2][i] = picture.Bgra[i * 4] - 128;
        }

        // The size asked for covers the picture data; the JP2 wrapper around it adds about
        // 90 bytes. If the result is still over, ask again for proportionally less. A file
        // can't be smaller than its headers (a few hundred bytes), so that's the least asked for.
        int aim = Math.Max(MinBytes, maxBytes - 100);
        byte[] data = Array.Empty<byte>();
        for (int attempt = 0; attempt < 6; attempt++)
        {
            var settings = new ParameterList(J2kImage.GetDefaultEncoderParameterList());
            settings["rate"] = (aim * 8.0 / (w * h)).ToString("R", CultureInfo.InvariantCulture);
            settings["verbose"] = "off";
            try
            {
                data = J2kImage.ToBytes(new InterleavedImageSource(w, h, 3, 8, new bool[3], planes), settings);
            }
            catch (Exception) when (attempt < 5)
            {
                aim = aim * 3 / 2;   // too few bytes even for the headers: allow more
                continue;
            }
            if (data.Length <= maxBytes || aim <= MinBytes) break;
            aim = Math.Max(MinBytes, (int)(aim * (double)maxBytes / data.Length) - 16);
        }
        return data;
    }

    private static byte[] To8Bits(int[] samples, int bits)
    {
        bits = Math.Clamp(bits, 1, 30);
        int max = (1 << bits) - 1;
        var result = new byte[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            int v = Math.Clamp(samples[i], 0, max);
            result[i] = bits == 8 ? (byte)v : (byte)((v * 255L + max / 2) / max);
        }
        return result;
    }

    private static long ReadUInt32(ReadOnlySpan<byte> d, int at) =>
        ((long)d[at] << 24) | ((long)d[at + 1] << 16) | ((long)d[at + 2] << 8) | d[at + 3];
}
