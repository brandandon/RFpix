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
//
// RFpix 0.7.1 (Nov 2026): tests 14 to 17 send RFpix's own FM signal (FmSignal/) at speeds FM 2 to FM 5,
// with fountain 20%, for checking it on the air through FM radios. Only RFpix 0.7.1 or newer receives them.

using System.Reflection;
using RFpix.FmSignal;
using RFpix.HamDrm;
using RFpix.HamDrm.Coding;

namespace RFpix.Send;

/// <summary>One fixed test transmission.</summary>
/// <param name="Number">As shown on the picture.</param>
/// <param name="Purpose">What it tests, in a few words.</param>
/// <param name="ReedSolomon">0 for none, or RS1 to RS4.</param>
/// <param name="RepairPercent">RFpix's repair code (the fountain code): the extra, in percent; 0 for none.</param>
/// <param name="FmSpeed">1 to 6 to send RFpix's FM signal at that speed (Settings unused); 0 for HamDRM.</param>
public sealed record TestTransmission(int Number, TransmitSettings Settings, string Purpose, int ReedSolomon = 0, int RepairPercent = 0,
                                      int FmSpeed = 0)
{
    /// <summary>RFpix's FM signal at this test's speed, or null for a HamDRM test.</summary>
    public FmSignalMode? FmMode => FmSpeed > 0 ? FmSignalMode.ById(FmSpeed - 1) : null;

    /// <summary>Its number in the FM signal: the same every time, so a station that missed pieces can add
    /// the next sending's.</summary>
    public ushort FmFileNumber => (ushort)(0xF000 + Number);

    /// <summary>The sound of this test going out from <paramref name="callsign"/>.</summary>
    public float[] Audio(string callsign)
    {
        if (FmMode is not { } mode) return Transmitter(callsign).Audio();
        var (data, k) = FmSignalTransmitter.Wrap(Picture(), "jpg", mode, RepairPercent);
        return FmSignalTransmitter.Audio(data, k, mode, FmFileNumber, new FmFileInfo(callsign, FileName));
    }

    /// <summary>How long it is on the air.</summary>
    public TimeSpan Duration => FmMode is { } mode
        ? TimeSpan.FromSeconds(FmSignalTransmitter.Seconds(FmSignalTransmitter.Wrap(Picture(), "jpg", mode, RepairPercent).Data.Length / mode.PieceBytes, mode))
        : Transmitter("TEST").Duration;
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
        if (FmMode is { } fm)
        {
            var (fmData, k) = FmSignalTransmitter.Wrap(Picture(), "jpg", fm, RepairPercent);
            return log.AddFm(FileName, fmData, k, fm, FmFileNumber);
        }
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
    public string SettingsText => FmMode is { } fm ? $"{fm.Description}  ·  {RepairText}  ·  RFpix only" :
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
        new TestTransmission(14, FmSettings, "RFpix FM 2: tough, through FM radios", RepairPercent: 20, FmSpeed: 2),
        new TestTransmission(15, FmSettings, "RFpix FM 3: as tough as test 11, twice as fast", RepairPercent: 20, FmSpeed: 3),
        new TestTransmission(16, FmSettings, "RFpix FM 4: fast, needs a fair signal", RepairPercent: 20, FmSpeed: 4),
        new TestTransmission(17, FmSettings, "RFpix FM 5: fast, strong signal, level a little hot", RepairPercent: 20, FmSpeed: 5),
        // Number 21 (Dan, Nov 27, 2026): 18 to 20 are RF-Hybrid's (RFpix-Fast), so the numbers match everywhere.
        new TestTransmission(21, FmSettings, "RFpix FM 6: the fastest, very clean signal only (Quality near 30 dB)", RepairPercent: 20, FmSpeed: 6),
    };

    // (For the FM tests, unused: the FM signal has its own settings.)
    private static TransmitSettings FmSettings => S(HamDrmMode.B, HamDrmBandwidth.Wide, 16, HamDrmInterleave.Long, HamDrmProtection.High);
}
