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
//
// Tests 9 and 10 add Reed-Solomon (RS): repair data spread across the whole picture, so
// missing pieces can be rebuilt. They're for weak paths, where a test arrives with most
// but not all of its pieces (Dan and W9BED's 2 m simplex tests, Oct 17, 2026, got 80-89%).
// The file goes out wrapped as .rs2 or .rs3, as QSSTV sends it, and the receiver unwraps it.

using System.Reflection;
using RFpix.HamDrm;

namespace RFpix.Send;

/// <summary>One fixed test transmission.</summary>
/// <param name="Number">1 to 10, as shown on the picture.</param>
/// <param name="Purpose">What it tests, in a few words.</param>
/// <param name="ReedSolomon">0 for none, or RS1 to RS4.</param>
public sealed record TestTransmission(int Number, TransmitSettings Settings, string Purpose, int ReedSolomon = 0)
{
    /// <summary>The picture's name: what the receiver saves it as.</summary>
    public string FileName => $"rfpix-test-{Number}.jpg";

    /// <summary>The name it goes out under: the picture's, or .rs1-.rs4 when wrapped in Reed-Solomon.</summary>
    public string SendName => ReedSolomon > 0 ? $"rfpix-test-{Number}.rs{ReedSolomon}" : FileName;

    /// <summary>The bytes sent: the picture, wrapped in Reed-Solomon if the test uses it.</summary>
    public byte[] SendData() => ReedSolomon > 0 ? RsFile.Wrap(Picture(), "jpg", ReedSolomon) : Picture();

    /// <summary>A transmitter for this test, from <paramref name="callsign"/>.</summary>
    public HamDrmTransmitter Transmitter(string callsign) => new(SendName, SendData(), Settings, callsign);

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
        $"{Settings.Interleave.ToString().ToLowerInvariant()}  ·  {Settings.Protection.ToString().ToLowerInvariant()}" +
        (ReedSolomon > 0 ? $"  ·  RS{ReedSolomon}" : "");
}

public static class TestTransmissions
{
    private static TransmitSettings S(HamDrmMode mode, HamDrmBandwidth bw, int qam, HamDrmInterleave il, HamDrmProtection p) =>
        new(mode, bw, qam, il, p);

    /// <summary>All ten: the toughest signal to the most demanding, then two with Reed-Solomon for weak paths.</summary>
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
        new TestTransmission(9, S(HamDrmMode.B, HamDrmBandwidth.Wide, 4, HamDrmInterleave.Long, HamDrmProtection.High),
            "test 1 with Reed-Solomon RS3, for weak paths: rebuilds up to a third missing", ReedSolomon: 3),
        new TestTransmission(10, S(HamDrmMode.B, HamDrmBandwidth.Wide, 16, HamDrmInterleave.Long, HamDrmProtection.High),
            "test 2 with Reed-Solomon RS2: rebuilds up to a quarter missing", ReedSolomon: 2),
    };
}
