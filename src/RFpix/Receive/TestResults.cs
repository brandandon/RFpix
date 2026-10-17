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
    public bool Perfect => Report.File is { } f && f.Data.AsSpan().SequenceEqual(Test.Picture());

    /// <summary>Were the test's own settings used? (Null if the settings weren't read.)</summary>
    public bool? SettingsMatch =>
        Report.Settings is not { } fac ? null
        : Report.Mode == Test.Settings.Mode && fac.Bandwidth == Test.Settings.Bandwidth && fac.Qam == Test.Settings.Qam &&
          fac.Interleave == Test.Settings.Interleave && fac.Protection == Test.Settings.Protection;

    /// <summary>"perfect", "41 of 51 pieces", ...</summary>
    public string Outcome =>
        Perfect ? "perfect"
        : Report.File is not null ? "arrived, but differs from the original"
        : Report.PiecesExpected > 0 ? $"{Report.PiecesReceived} of {Report.PiecesExpected} pieces"
        : "nothing came through";

    /// <summary>The settings it was actually sent with, e.g. "Mode B · 2.5 kHz · 16-QAM · long · high".</summary>
    public string SettingsReceived =>
        Report.Settings is not { } fac ? "not read"
        : $"Mode {Report.Mode?.ToString() ?? "?"}  ·  {(fac.Bandwidth == HamDrmBandwidth.Wide ? "2.5" : "2.2")} kHz  ·  {fac.Qam}-QAM  ·  " +
          $"{fac.Interleave.ToString().ToLowerInvariant()}  ·  {fac.Protection.ToString().ToLowerInvariant()}";

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
    private static readonly Regex TestName = new(@"^rfpix-test-(\d+)\.jpg$", RegexOptions.IgnoreCase);

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
            (result.Perfect ? r.PiecesExpected : r.PiecesReceived).ToString(inv), r.PiecesExpected.ToString(inv),
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
