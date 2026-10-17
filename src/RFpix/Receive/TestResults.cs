// TestResults.cs - recognises RFpix's test transmissions when they're received, says how
// each came through, and keeps a log of them.
//
// A test picture is known by its name (rfpix-test-1.jpg to rfpix-test-8.jpg), and RFpix
// carries the original of every one, so it can tell exactly how a test arrived:
//   - perfect: the picture is identical to the original;
//   - or how many of its pieces arrived, if it didn't come through;
//   - the signal quality over the transmission, and what the test's QAM needs;
//   - whether it was sent with the test's own settings (someone sending the file from
//     QSSTV with other settings would show up here).
//
// Every test received is added to "Test results.csv" in Documents\RFpix, one line each.
// It opens in Excel, so a distant tester can simply send that file back.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RFpix.HamDrm;
using RFpix.Send;

namespace RFpix.Receive;

/// <summary>How one test transmission came through.</summary>
/// <param name="SoundGaps">Gaps Windows left in the sound during it (each filled with silence).</param>
public sealed record TestResult(TestTransmission Test, TransmissionReport Report, int SoundGaps)
{
    /// <summary>The picture arrived, identical to the original.</summary>
    public bool Arrived => Report.File is { } f && f.Data.AsSpan().SequenceEqual(Test.Picture());

    /// <summary>The pieces this transmission brought by itself.</summary>
    public int OwnPieces => Report.PiecesSeen.Count;

    /// <summary>The picture arrived from this transmission alone (with Reed-Solomon's repairs, if
    /// any), not with the help of pieces an earlier sending of the same test brought.</summary>
    public bool Perfect => Arrived && OwnPieces + Report.File!.RepairedPieces >= Report.PiecesExpected;

    /// <summary>Were the test's own settings used? (Null if the settings weren't read.)</summary>
    public bool? SettingsMatch =>
        Report.Settings is not { } fac ? null
        : Report.Mode == Test.Settings.Mode && fac.Bandwidth == Test.Settings.Bandwidth && fac.Qam == Test.Settings.Qam &&
          fac.Interleave == Test.Settings.Interleave && fac.Protection == Test.Settings.Protection &&
          ReedSolomonReceived == Test.ReedSolomon;

    /// <summary>The Reed-Solomon level it came with (0 for none): from the file, or from the name
    /// it was sent under (.rs1-.rs4) if it didn't come through.</summary>
    public int ReedSolomonReceived =>
        Report.File?.ReedSolomonLevel ?? (Report.Name is { } name ? RsFile.LevelOf(name) : null) ?? 0;

    /// <summary>"perfect", "41 of 51 pieces", ...</summary>
    public string Outcome =>
        Perfect && Report.File!.RepairedPieces > 0 ? $"perfect: Reed-Solomon rebuilt {Report.File.RepairedPieces} missing pieces"
        : Perfect ? "perfect"
        : Arrived ? $"{OwnPieces} of {Report.PiecesExpected} pieces (the picture was completed with pieces from an earlier send)"
        : Report.File is not null ? "arrived, but differs from the original"
        : Report.PiecesExpected > 0 ? $"{OwnPieces} of {Report.PiecesExpected} pieces"
        : "nothing came through";

    /// <summary>The settings it was actually sent with, e.g. "Mode B · 2.5 kHz · 16-QAM · long · high".</summary>
    public string SettingsReceived =>
        Report.Settings is not { } fac ? "not read"
        : $"Mode {Report.Mode?.ToString() ?? "?"}  ·  {(fac.Bandwidth == HamDrmBandwidth.Wide ? "2.5" : "2.2")} kHz  ·  {fac.Qam}-QAM  ·  " +
          $"{fac.Interleave.ToString().ToLowerInvariant()}  ·  {fac.Protection.ToString().ToLowerInvariant()}" +
          (ReedSolomonReceived > 0 ? $"  ·  RS{ReedSolomonReceived}" : "");

    /// <summary>What the test's QAM needs, in dB.</summary>
    public double QualityNeeded => HamDrmReceiver.QualityNeeded(Test.Settings.Qam, Test.Settings.Protection);

    /// <summary>One line for the screen.</summary>
    public string Text
    {
        get
        {
            var text = new StringBuilder($"Test {Test.Number}");
            if (Report.Callsign is { Length: > 0 } call) text.Append($" from {call}");
            text.Append($": {Outcome}");
            if (Report.QualityDb is double q) text.Append($"  ·  quality {q:0} dB (needs about {QualityNeeded:0})");
            if (SoundGaps > 0) text.Append($"  ·  {SoundGaps} gap{(SoundGaps == 1 ? "" : "s")} in the sound");
            if (SettingsMatch == false) text.Append($"  ·  sent with other settings: {SettingsReceived}");
            return text.ToString();
        }
    }
}

public static class TestResults
{
    // The picture's own name, or (a test with Reed-Solomon that didn't come through) the name it was sent under.
    private static readonly Regex TestName = new(@"^rfpix-test-(\d+)\.(jpg|rs[1-4])$", RegexOptions.IgnoreCase);

    /// <summary>The test a transmission carried, or null if it wasn't one of RFpix's tests.</summary>
    public static TestResult? Recognise(TransmissionReport report, int soundGaps = 0)
    {
        if (report.Name is null || TestName.Match(report.Name) is not { Success: true } m) return null;
        int number = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var test = TestTransmissions.All.FirstOrDefault(t => t.Number == number);
        return test is null ? null : new TestResult(test, report, soundGaps);
    }

    /// <summary>Documents\RFpix\Test results.csv.</summary>
    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RFpix", "Test results.csv");

    private static readonly string[] Columns =
    {
        "Date", "Time", "Test", "Test settings", "Sent with", "From", "Result",
        "Pieces received", "Pieces in file", "Quality (dB)", "Quality needed (dB)",
        "Packets good", "Packets bad", "Sound gaps",
    };

    /// <summary>Adds a line to the log (creating it, with column titles, the first time).</summary>
    public static void Append(TestResult result, string? path = null)
    {
        path ??= LogPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var r = result.Report;
        var inv = CultureInfo.InvariantCulture;
        var fields = new[]
        {
            r.Ended.ToString("yyyy-MM-dd", inv), r.Ended.ToString("HH:mm:ss", inv),
            result.Test.Number.ToString(inv), result.Test.SettingsText, result.SettingsReceived,
            r.Callsign ?? "", result.Outcome,
            result.OwnPieces.ToString(inv), r.PiecesExpected.ToString(inv),
            r.QualityDb is double q ? q.ToString("0.0", inv) : "", result.QualityNeeded.ToString("0", inv),
            r.PacketsGood.ToString(inv), r.PacketsBad.ToString(inv), result.SoundGaps.ToString(inv),
        };
        var lines = new StringBuilder();
        if (!File.Exists(path)) lines.AppendLine(string.Join(",", Columns.Select(Quote)));
        lines.AppendLine(string.Join(",", fields.Select(Quote)));
        File.AppendAllText(path, lines.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    // Spreadsheet text: a field with a comma, quote or line break goes in quotes (quotes doubled).
    private static string Quote(string field) =>
        field.IndexOfAny(new[] { ',', '"', '\n' }) >= 0 ? "\"" + field.Replace("\"", "\"\"") + "\"" : field;
}

/// <summary>The test log, with one line per test transmission even when the signal dropped out
/// for a few seconds in the middle of one. The receiver then reports two transmissions, the
/// second marked as continuing the first; they're joined into one line here. An unfinished
/// test is held back a little in case its continuation follows.</summary>
public sealed class TestLog(string? path = null)
{
    private TestResult? _held;
    private readonly object _lock = new();   // results arrive on the audio thread; Flush runs on the screen's

    /// <summary>How long an unfinished test waits for a continuation before it's written.</summary>
    public static readonly TimeSpan HoldFor = HamDrmReceiver.MaxDropout + TimeSpan.FromSeconds(10);

    /// <summary>Adds a test result. Returns it as the screen should show it (joined with the
    /// earlier part, if it continues one).</summary>
    public TestResult Add(TestResult result)
    {
        lock (_lock) return AddLocked(result);
    }

    private TestResult AddLocked(TestResult result)
    {
        if (_held is { } held)
        {
            _held = null;
            if (result.Report.ContinuesPrevious && held.Test.Number == result.Test.Number) result = Join(held, result);
            else TestResults.Append(held, path);
        }
        if (result.Report.File is null) _held = result;   // may still continue
        else TestResults.Append(result, path);
        return result;
    }

    /// <summary>Writes an unfinished test once nothing more can continue it.</summary>
    public void Flush(DateTime now)
    {
        lock (_lock)
            if (_held is { } held && now - held.Report.Ended > HoldFor) WriteHeld();
    }

    /// <summary>Writes anything held back (RFpix closing).</summary>
    public void WriteHeld()
    {
        lock (_lock)
        {
            var held = _held;
            _held = null;
            if (held is not null) TestResults.Append(held, path);
        }
    }

    private static TestResult Join(TestResult first, TestResult rest)
    {
        var a = first.Report;
        var b = rest.Report;
        return rest with
        {
            SoundGaps = first.SoundGaps + rest.SoundGaps,
            Report = b with
            {
                Callsign = b.Callsign ?? a.Callsign,
                Mode = b.Mode ?? a.Mode,
                Settings = b.Settings ?? a.Settings,
                FrameQualities = a.FrameQualities.Concat(b.FrameQualities).ToArray(),
                PacketsGood = a.PacketsGood + b.PacketsGood,
                PacketsBad = a.PacketsBad + b.PacketsBad,
                PiecesSeen = a.PiecesSeen.Union(b.PiecesSeen).Order().ToArray(),
                ContinuesPrevious = a.ContinuesPrevious,
            },
        };
    }
}
