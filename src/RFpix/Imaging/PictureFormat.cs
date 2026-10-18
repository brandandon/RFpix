// PictureFormat.cs - the picture formats RFpix sends, and telling them apart.
//
// JPEG 2000 is the default: EasyPal sends it, QSSTV and EasyPal both show it, and at the
// small sizes HamDRM carries it looks better than JPEG. JPEG is shown by everything.
// AVIF is "RFpix only, smaller": at the same size it looks better still (in tests on
// real photos, an 8 kB AVIF matched a 30 kB JPEG 2000), so a picture goes out in a
// fraction of the time, but QSSTV and EasyPal can't show it.
//
// A received file's format is judged by its first bytes, not its name: names come off
// the air and may be wrong.

namespace RFpix.Imaging;

public enum PictureFormat
{
    Jpeg2000,
    Jpeg,
    Avif,
}

public static class PictureFormats
{
    /// <summary>The file extension a picture of this format is sent with, without the dot.</summary>
    public static string Extension(this PictureFormat format) => format switch
    {
        PictureFormat.Jpeg2000 => "jp2",
        PictureFormat.Jpeg => "jpg",
        _ => "avif",
    };

    /// <summary>The name shown on the screen.</summary>
    public static string DisplayName(this PictureFormat format) => format switch
    {
        PictureFormat.Jpeg2000 => "JPEG 2000",
        PictureFormat.Jpeg => "JPEG",
        _ => "AVIF",
    };

    /// <summary>True if only RFpix can show it (QSSTV and EasyPal can't).</summary>
    public static bool RFpixOnly(this PictureFormat format) => format == PictureFormat.Avif;

    /// <summary>The format of a picture, going by its first bytes, or null if it's none of RFpix's.</summary>
    public static PictureFormat? Detect(ReadOnlySpan<byte> data)
    {
        if (Jpeg2000.IsJpeg2000(data)) return PictureFormat.Jpeg2000;
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return PictureFormat.Jpeg;
        if (IsAvif(data)) return PictureFormat.Avif;
        return null;
    }

    /// <summary>True if the data is an AVIF picture. It starts with an "ftyp" box naming
    /// its brands: "avif" (a picture) or "avis" (a picture sequence), as the main brand or
    /// one of the compatible ones.</summary>
    public static bool IsAvif(ReadOnlySpan<byte> data)
    {
        if (data.Length < 16 || !data[4..8].SequenceEqual("ftyp"u8)) return false;
        long size = ((long)data[0] << 24) | ((long)data[1] << 16) | ((long)data[2] << 8) | data[3];
        int end = (int)Math.Min(Math.Max(size, 16), data.Length);
        // The main brand at 8, a version at 12, then compatible brands, 4 bytes each.
        for (int at = 8; at + 4 <= end; at += at == 8 ? 8 : 4)
        {
            var brand = data[at..(at + 4)];
            if (brand.SequenceEqual("avif"u8) || brand.SequenceEqual("avis"u8)) return true;
        }
        return false;
    }
}
