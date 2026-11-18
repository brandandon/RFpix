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
//
// RFpix 0.7.0 (Nov 2026): tests 3, 7, 8 and 9 are retired, and tests 11, 12 and 13 send test 10's
// settings with RFpix's own repair code (the fountain code) at 20%, 50% and 75% in place of RS2, to
// compare the two on the air. They go out as "rfpix-test-11.fc39" and so on; only RFpix can read
// them. Numbers are never reused, so a station with an older RFpix never mistakes one test for another.

using System.Reflection;
using RFpix.HamDrm;
using RFpix.HamDrm.Coding;

namespace RFpix.Send;

/// <summary>One fixed test transmission.</summary>
/// <param name="Number">As shown on the picture.</param>
/// <param name="Purpose">What it tests, in a few words.</param>
/// <param name="ReedSolomon">0 for none, or RS1 to RS4.</param>
/// <param name="RepairPercent">RFpix's repair code (the fountain code): the extra, in percent; 0 for none.</param>
public sealed record TestTransmission(int Number, TransmitSettings Settings, string Purpose, int ReedSolomon = 0, int RepairPercent = 0)
{
    /// <summary>The picture's name: what the receiver saves it as.</summary>
    public string FileName => $"rfpix-test-{Number}.jpg";

    /// <summary>The name it goes out under: the picture's, .rs1-.rs4 when wrapped in Reed-Solomon, or
    /// .fc and the number of the picture's pieces with the fountain code.</summary>
    public string SendName => Wrapped().Name;

    /// <summary>The bytes sent: the picture, wrapped in Reed-Solomon or the fountain code if the test uses it.</summary>
    public byte[] SendData() => Wrapped().Data;

    private (string Name, byte[] Data) Wrapped() =>
        RepairPercent > 0 ? FountainFile.WrapFor(Picture(), "jpg", $"rfpix-test-{Number}", Settings, RepairPercent)
        : ReedSolomon > 0 ? ($"rfpix-test-{Number}.rs{ReedSolomon}", RsFile.Wrap(Picture(), "jpg", ReedSolomon))
        : (FileName, Picture());

    /// <summary>A transmitter for this test, from <paramref name="callsign"/>.</summary>
    public HamDrmTransmitter Transmitter(string callsign)
    {
        var (name, data) = Wrapped();
        return new(name, data, Settings, callsign);
    }

    /// <summary>Notes this test in the sent log as it goes out, so a station that misses some of its
    /// pieces can ask for them (a BSR) and be answered, as for a picture sent from the Send tab.</summary>
    public SentFile RecordSent(SentLog log)
    {
        var (name, data) = Wrapped();
        return log.Add(name, FileName, data, Settings);
    }

    /// <summary>The repair data, for the list and the log: "RS2", "Fountain 50%", or "" for none.</summary>
    public string RepairText => RepairPercent > 0 ? $"Fountain {RepairPercent}%" : ReedSolomon > 0 ? $"RS{ReedSolomon}" : "";

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
        (RepairText.Length > 0 ? $"  ·  {RepairText}" : "");
}

public static class TestTransmissions
{
    private static TransmitSettings S(HamDrmMode mode, HamDrmBandwidth bw, int qam, HamDrmInterleave il, HamDrmProtection p) =>
        new(mode, bw, qam, il, p);

    /// <summary>All of them: the toughest signal to the most demanding, then RS2 and the fountain code
    /// for weak paths.</summary>
    public static IReadOnlyList<TestTransmission> All { get; } = new[]
    {
        new TestTransmission(1, S(HamDrmMode.B, HamDrmBandwidth.Wide, 4, HamDrmInterleave.Long, HamDrmProtection.High),
            "the toughest: try this first"),
        new TestTransmission(2, S(HamDrmMode.B, HamDrmBandwidth.Wide, 16, HamDrmInterleave.Long, HamDrmProtection.High),
            "the usual net setting"),
        new TestTransmission(4, S(HamDrmMode.B, HamDrmBandwidth.Wide, 64, HamDrmInterleave.Long, HamDrmProtection.High),
            "fastest, needs a very clean signal"),
        new TestTransmission(5, S(HamDrmMode.A, HamDrmBandwidth.Wide, 16, HamDrmInterleave.Long, HamDrmProtection.High),
            "mode A: for steady paths"),
        new TestTransmission(6, S(HamDrmMode.E, HamDrmBandwidth.Wide, 16, HamDrmInterleave.Long, HamDrmProtection.High),
            "mode E: for fast-changing paths"),
        new TestTransmission(10, S(HamDrmMode.B, HamDrmBandwidth.Wide, 16, HamDrmInterleave.Long, HamDrmProtection.High),
            "test 2 with Reed-Solomon RS2: rebuilds up to a quarter missing", ReedSolomon: 2),
        new TestTransmission(11, S(HamDrmMode.B, HamDrmBandwidth.Wide, 16, HamDrmInterleave.Long, HamDrmProtection.High),
            "test 2 with Fountain 20%: the quickest, RFpix only", RepairPercent: 20),
        new TestTransmission(12, S(HamDrmMode.B, HamDrmBandwidth.Wide, 16, HamDrmInterleave.Long, HamDrmProtection.High),
            "test 2 with Fountain 50%: everyday, compare with test 10 (RS2)", RepairPercent: 50),
        new TestTransmission(13, S(HamDrmMode.B, HamDrmBandwidth.Wide, 16, HamDrmInterleave.Long, HamDrmProtection.High),
            "test 2 with Fountain 75%: for bad conditions", RepairPercent: 75),
    };
}
