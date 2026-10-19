// PictureFitter.cs - the image optimizer: fits a picture to the transmit time.
//
// How long a picture takes on the air depends only on its file size and the send settings,
// so the first step is the byte budget: the largest file that goes out within the target
// time (2 minutes at the net's settings is about 30 kB). Then two things make a picture
// fit, and they trade against each other:
//   - Size: fewer pixels, a smaller file before any compression.
//   - Quality: harder compression keeps the pixels but softens fine detail.
//
// Auto picks the mix. Each format looks its best at about a certain number of bits per
// pixel (JPEG 2000 around 0.8, JPEG 1.3, AVIF 0.45: below that, detail smears), so Auto
// sizes the picture to use the budget at about that rate, then finds the highest quality
// that fits. If the picture still comes out mushy (the quality has to drop below a floor),
// it makes the picture smaller and tries again.
// Auto may run up to 3 seconds past the target (a little more picture, still well inside a
// repeater's time-out). Manual takes the size and quality the operator picks (the sliders,
// after Auto has fitted the picture), and just reports the time.
//
// The time is shown green when it fits (for Auto, within its 3 extra seconds), yellow when
// it runs up to 7 seconds over the target, and red beyond that.
//
// The fitter works with any encoder (the program uses Pictures.Encode), so the tests can
// try it with JPEG 2000 alone or with a stand-in.

using System.Text;
using RFpix.HamDrm;
using RFpix.Imaging;

namespace RFpix.Send;

/// <summary>A picture ready to send, and how long it takes.</summary>
/// <param name="Picture">The picture as fitted (smaller than the original, maybe).</param>
/// <param name="File">The picture file: what the receiver ends up with.</param>
/// <param name="FileName">The name it's received as, e.g. "tower-sunset.jp2".</param>
/// <param name="SendData">The bytes sent: the file, wrapped in Reed-Solomon if the profile uses it.</param>
/// <param name="SendName">The name it goes out under: the file's, or .rs1-.rs4 when wrapped.</param>
public sealed record FittedPicture(
    DecodedPicture Picture, PictureFormat Format, int Quality, byte[] File, string FileName,
    byte[] SendData, string SendName, SendProfile Profile, TimeSpan Duration)
{
    public TimeSpan Target => TimeSpan.FromSeconds(Profile.TargetSeconds);

    /// <summary>True if Auto chose the size and quality (false: the operator did).</summary>
    public bool Automatic { get; init; }

    /// <summary>How the time compares with the target: green, yellow or red on the screen.</summary>
    public TimeFit Fit =>
        Duration <= Target + (Automatic ? PictureFitter.AutoAllowance : TimeSpan.Zero) ? TimeFit.Fits
        : Duration <= Target + PictureFitter.SlightlyOver ? TimeFit.SlightlyOver
        : TimeFit.Over;

    /// <summary>True if it goes out within the target time (for Auto, within its allowance).</summary>
    public bool FitsTarget => Fit == TimeFit.Fits;

    /// <summary>A transmitter for it, from <paramref name="callsign"/>.</summary>
    public HamDrmTransmitter Transmitter(string callsign) => new(SendName, SendData, Profile.Settings, callsign);
}

/// <summary>How a picture's time compares with the target.</summary>
public enum TimeFit
{
    /// <summary>Within the target (for Auto, within its 3-second allowance): green.</summary>
    Fits,
    /// <summary>Up to 7 seconds over the target: yellow.</summary>
    SlightlyOver,
    /// <summary>More than 7 seconds over: red.</summary>
    Over,
}

public sealed class PictureFitter
{
    /// <summary>How far past the target Auto may go.</summary>
    public static readonly TimeSpan AutoAllowance = TimeSpan.FromSeconds(3);

    /// <summary>Up to this far over the target is "slightly over" (yellow), beyond it red.</summary>
    public static readonly TimeSpan SlightlyOver = TimeSpan.FromSeconds(7);

    /// <summary>The smallest and largest longer side Auto picks, in pixels.</summary>
    public const int MinLongSide = 160, MaxAutoLongSide = 1280;

    /// <summary>The highest quality Auto uses: above it, files grow with no visible gain.</summary>
    public const int MaxAutoQuality = 95;

    private readonly Func<DecodedPicture, PictureFormat, int, byte[]> _encode;

    /// <param name="encode">Makes a picture file: picture, format, quality 1-100.</param>
    public PictureFitter(Func<DecodedPicture, PictureFormat, int, byte[]> encode) => _encode = encode;

    /// <summary>Bits per pixel each format looks good at, for Auto's first guess at the size.</summary>
    public static double GoodBitsPerPixel(PictureFormat format) => format switch
    {
        PictureFormat.Jpeg2000 => 0.8,
        PictureFormat.Jpeg => 1.3,
        _ => 0.45,
    };

    /// <summary>The quality below which Auto makes the picture smaller instead.</summary>
    public static int QualityFloor(PictureFormat format) => format switch
    {
        PictureFormat.Jpeg2000 => 25,   // about 0.14 bits per pixel
        PictureFormat.Jpeg => 35,
        _ => 25,
    };

    // ---- names and times ----

    /// <summary>A name to send under: the picture's own name, kept to letters, digits and
    /// dashes (it's shown on other stations' screens and saved there), at most 40 long.</summary>
    public static string BaseName(string path)
    {
        var name = new StringBuilder();
        foreach (char c in Path.GetFileNameWithoutExtension(path))
        {
            bool ok = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_';
            if (ok) name.Append(c);
            else if (name.Length > 0 && name[^1] != '-') name.Append('-');
            if (name.Length == 40) break;
        }
        string result = name.ToString().Trim('-');
        return result.Length > 0 ? result : "picture";
    }

    /// <summary>How many bytes go out for a file of <paramref name="fileBytes"/>: the same, or
    /// whole 255-byte Reed-Solomon blocks for RS1-RS4.</summary>
    public static int SendLength(int fileBytes, int reedSolomon) =>
        reedSolomon == 0 ? fileBytes : (fileBytes + 7 + RsFile.DataBytes(reedSolomon) - 1) / RsFile.DataBytes(reedSolomon) * 255;

    /// <summary>How long <paramref name="sendLength"/> bytes take on the air with these settings.</summary>
    public static TimeSpan DurationFor(string sendName, int sendLength, TransmitSettings settings) =>
        new HamDrmTransmitter(sendName, new byte[sendLength], settings, "X").Duration;

    /// <summary>The largest picture file (in bytes) that goes out within the profile's target time,
    /// plus <paramref name="extra"/>.</summary>
    public static int MaxFileBytes(string sendName, SendProfile profile, TimeSpan extra = default)
    {
        var target = TimeSpan.FromSeconds(profile.TargetSeconds) + extra;
        // Double until it's too long, then find the most bytes that fit, halving the range each time.
        int lo = 0, hi = 1024;
        while (hi < 1 << 24 && DurationFor(sendName, hi, profile.Settings) <= target) { lo = hi; hi *= 2; }
        while (lo < hi)
        {
            int mid = lo + (hi - lo + 1) / 2;
            if (DurationFor(sendName, mid, profile.Settings) <= target) lo = mid;
            else hi = mid - 1;
        }
        if (profile.ReedSolomon == 0) return lo;
        // Reed-Solomon sends whole blocks, and each carries fewer bytes of the file.
        int blocks = lo / 255;
        return Math.Max(0, blocks * RsFile.DataBytes(profile.ReedSolomon) - 7);
    }

    /// <summary>The picture file, wrapped and named for sending.</summary>
    public static FittedPicture Package(DecodedPicture picture, PictureFormat format, int quality, byte[] file,
                                        string baseName, SendProfile profile)
    {
        string fileName = $"{baseName}.{format.Extension()}";
        int rs = profile.ReedSolomon;
        string sendName = rs > 0 ? $"{baseName}.rs{rs}" : fileName;
        byte[] sendData = rs > 0 ? RsFile.Wrap(file, format.Extension(), rs) : file;
        var duration = new HamDrmTransmitter(sendName, sendData, profile.Settings, "X").Duration;
        return new FittedPicture(picture, format, quality, file, fileName, sendData, sendName, profile, duration);
    }

    // ---- Manual and Auto ----

    /// <summary>The picture at the size and quality the operator picked.</summary>
    /// <param name="longSide">Its longer side in pixels (never more than it is).</param>
    public FittedPicture Manual(DecodedPicture original, string baseName, PictureFormat format, SendProfile profile,
                                int longSide, int quality)
    {
        var picture = Scaled(original, longSide);
        quality = Math.Clamp(quality, 1, 100);
        return Package(picture, format, quality, _encode(picture, format, quality), baseName, profile);
    }

    /// <summary>The best-looking picture that goes out within the profile's target time.
    /// If even the smallest, roughest version runs over, that's what comes back (and
    /// <see cref="FittedPicture.FitsTarget"/> says so).</summary>
    public FittedPicture Auto(DecodedPicture original, string baseName, PictureFormat format, SendProfile profile,
                              CancellationToken cancel = default)
    {
        string sendName = profile.ReedSolomon > 0 ? $"{baseName}.rs{profile.ReedSolomon}" : $"{baseName}.{format.Extension()}";
        int budget = MaxFileBytes(sendName, profile, AutoAllowance);
        int originalLong = Math.Max(original.Width, original.Height);

        // First guess: the size that uses the budget at the format's good rate.
        double pixels = budget * 8 / GoodBitsPerPixel(format);
        double scale = Math.Sqrt(pixels / ((double)original.Width * original.Height));
        int longSide = Math.Clamp((int)Math.Round(originalLong * scale), MinLongSide, MaxAutoLongSide);
        longSide = Math.Min(longSide, originalLong);

        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            var picture = Scaled(original, longSide);
            var (quality, file) = BestQuality(picture, format, budget, cancel);
            bool smallest = longSide <= Math.Min(MinLongSide, originalLong);
            if (file is not null && (quality >= QualityFloor(format) || smallest))
                return Package(picture, format, quality, file, baseName, profile) with { Automatic = true };
            if (smallest)   // nothing fits: send the roughest, smallest version, over time
                return Package(picture, format, 1, _encode(picture, format, 1), baseName, profile) with { Automatic = true };
            longSide = Math.Max(Math.Min(MinLongSide, originalLong), (int)(longSide * 0.8));
        }
    }

    /// <summary>The highest quality (1 to <see cref="MaxAutoQuality"/>) whose file is within
    /// the budget, and that file; or (0, null) if even quality 1 is too big.</summary>
    private (int Quality, byte[]? File) BestQuality(DecodedPicture picture, PictureFormat format, int budget,
                                                    CancellationToken cancel)
    {
        var lowest = _encode(picture, format, 1);
        if (lowest.Length > budget) return (0, null);
        int good = 1, bad = MaxAutoQuality + 1;   // good fits; bad doesn't (or is past the top)
        byte[] best = lowest;
        while (bad - good > 1)
        {
            cancel.ThrowIfCancellationRequested();
            int q = (good + bad) / 2;
            var file = _encode(picture, format, q);
            if (file.Length <= budget) { good = q; best = file; }
            else bad = q;
        }
        return (good, best);
    }

    private static DecodedPicture Scaled(DecodedPicture original, int longSide)
    {
        var (w, h) = PictureScaler.FitLongSide(original.Width, original.Height, Math.Max(1, longSide));
        return PictureScaler.Resize(original, w, h);
    }
}
