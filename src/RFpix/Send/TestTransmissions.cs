// TestTransmissions.cs - the fixed test transmissions on the Send page.
//
// Each one sends its own picture with fixed settings, and the picture says what
// those settings are. So anyone receiving it, near or far, with RFpix, QSSTV or
// EasyPal, knows exactly what was sent and can report how it came through:
// "test 2 arrived perfectly, test 4 didn't".
//
// The pictures are built into RFpix (Assets/TestPictures, drawn by
// tools/test-pictures/make_test_pictures.py). The file names start with
// "rfpix-test-" so a receiver can recognise them.

using System.Reflection;
using RFpix.HamDrm;

namespace RFpix.Send;

/// <summary>One fixed test transmission.</summary>
/// <param name="Number">1 to 8, as shown on the picture.</param>
/// <param name="Purpose">What it tests, in a few words.</param>
public sealed record TestTransmission(int Number, TransmitSettings Settings, string Purpose)
{
    /// <summary>The name it's sent (and saved) as.</summary>
    public string FileName => $"rfpix-test-{Number}.jpg";

    /// <summary>The picture's bytes (a JPEG built into RFpix).</summary>
    public byte[] Picture()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("RFpix.TestPictures." + FileName)
            ?? throw new InvalidOperationException($"The test picture {FileName} is missing from RFpix.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>Short settings for the list, e.g. "Mode B · 2.5 kHz · 16-QAM · long · high".</summary>
    public string SettingsText =>
        $"Mode {Settings.Mode}  ·  {(Settings.Bandwidth == HamDrmBandwidth.Wide ? "2.5" : "2.2")} kHz  ·  {Settings.Qam}-QAM  ·  " +
        $"{Settings.Interleave.ToString().ToLowerInvariant()}  ·  {Settings.Protection.ToString().ToLowerInvariant()}";
}

public static class TestTransmissions
{
    private static TransmitSettings S(HamDrmMode mode, HamDrmBandwidth bw, int qam, HamDrmInterleave il, HamDrmProtection p) =>
        new(mode, bw, qam, il, p);

    /// <summary>All eight, from the toughest signal to the most demanding.</summary>
    public static IReadOnlyList<TestTransmission> All { get; } = new[]
    {
        new TestTransmission(1, S(HamDrmMode.B, HamDrmBandwidth.Wide, 4, HamDrmInterleave.Long, HamDrmProtection.High),
            "the toughest: try this first"),
        new TestTransmission(2, S(HamDrmMode.B, HamDrmBandwidth.Wide, 16, HamDrmInterleave.Long, HamDrmProtection.High),
            "the usual net setting"),
        new TestTransmission(3, S(HamDrmMode.B, HamDrmBandwidth.Wide, 16, HamDrmInterleave.Long, HamDrmProtection.Low),
            "less error correction, a little faster"),
        new TestTransmission(4, S(HamDrmMode.B, HamDrmBandwidth.Wide, 64, HamDrmInterleave.Long, HamDrmProtection.High),
            "fastest, needs a very clean signal"),
        new TestTransmission(5, S(HamDrmMode.A, HamDrmBandwidth.Wide, 16, HamDrmInterleave.Long, HamDrmProtection.High),
            "mode A: for steady paths"),
        new TestTransmission(6, S(HamDrmMode.E, HamDrmBandwidth.Wide, 16, HamDrmInterleave.Long, HamDrmProtection.High),
            "mode E: for fast-changing paths"),
        new TestTransmission(7, S(HamDrmMode.B, HamDrmBandwidth.Narrow, 16, HamDrmInterleave.Long, HamDrmProtection.High),
            "the narrow 2.2 kHz width"),
        new TestTransmission(8, S(HamDrmMode.B, HamDrmBandwidth.Wide, 16, HamDrmInterleave.Short, HamDrmProtection.High),
            "short interleave: less protection against fading"),
    };
}
