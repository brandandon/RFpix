// TestResultTests.cs - checks that a test transmission is recognised when received, that
// its result says how it came through, and that it's written to the test log.

using RFpix.HamDrm;
using RFpix.Receive;
using RFpix.Send;
using Xunit;

namespace RFpix.Tests;

public class TestResultTests
{
    [Fact]
    public void A_test_received_perfectly_is_reported_and_logged()
    {
        var test = TestTransmissions.All.Single(t => t.Number == 2);
        var tx = new HamDrmTransmitter(test.FileName, test.Picture(), test.Settings, "KD9DD");
        var report = Report(tx);

        var result = TestResults.Recognise(report, soundGaps: 1);
        Assert.NotNull(result);
        Assert.True(result!.Perfect);
        Assert.Equal(true, result.SettingsMatch);
        Assert.StartsWith("Test 2 from KD9DD: perfect  ·  quality ", result.Text);
        Assert.Contains("1 gap in the sound", result.Text);
        Assert.Equal(report.PiecesExpected, report.PiecesReceived);
        Assert.True(report.QualityDb > 25);   // a clean signal (the figure tops out around 30)

        string log = Path.Combine(Path.GetTempPath(), $"rfpix-test-log-{Guid.NewGuid():N}.csv");
        try
        {
            TestResults.Append(result, log);
            TestResults.Append(result, log);
            var lines = File.ReadAllLines(log);
            Assert.Equal(3, lines.Length);   // column titles once, then one line per test
            Assert.StartsWith("Date,Time,Test,", lines[0].TrimStart('﻿'));
            Assert.Contains(",2,", lines[1]);
            Assert.Contains(",KD9DD,perfect,", lines[1]);
        }
        finally
        {
            File.Delete(log);
        }
    }

    [Fact]
    public void A_test_cut_short_reports_how_many_pieces_arrived()
    {
        var test = TestTransmissions.All.Single(t => t.Number == 2);
        var tx = new HamDrmTransmitter(test.FileName, test.Picture(), test.Settings, "KD9DD");
        var report = Report(tx, keepSeconds: 20);   // the signal stops 20 s in

        var result = TestResults.Recognise(report)!;
        Assert.False(result.Perfect);
        Assert.Null(report.File);
        Assert.InRange(report.PiecesReceived, 1, report.PiecesExpected - 1);
        Assert.Equal($"{report.PiecesReceived} of {report.PiecesExpected} pieces", result.Outcome);
    }

    [Fact]
    public void A_test_picture_sent_with_other_settings_says_so()
    {
        var test = TestTransmissions.All.Single(t => t.Number == 4);   // 64-QAM ...
        var other = test.Settings with { Qam = 16 };                    // ... sent as 16-QAM
        var result = TestResults.Recognise(Report(new HamDrmTransmitter(test.FileName, test.Picture(), other, "W1AW")))!;

        Assert.True(result.Perfect);
        Assert.Equal(false, result.SettingsMatch);
        Assert.Contains("sent with other settings: Mode B  ·  2.5 kHz  ·  16-QAM", result.Text);
    }

    /// <summary>Plays a test from W9BED with stretches of 4 seconds lost at these times (in seconds),
    /// and returns its result, the transmission's parts joined back together by the test log.</summary>
    private static TestResult Faded(int number, params double[] at)
    {
        var test = TestTransmissions.All.Single(t => t.Number == number);
        var audio = Pad(test.Transmitter("W9BED").Audio());
        foreach (double start in at) Array.Clear(audio, (int)(start * 48000), 4 * 48000);
        string path = Path.Combine(Path.GetTempPath(), $"rfpix-faded-{Guid.NewGuid():N}.csv");
        var log = new TestLog(path);
        TestResult? last = null;
        foreach (var r in Reports(audio)) if (TestResults.Recognise(r) is { } t) last = log.Add(t);
        File.Delete(path);
        Assert.NotNull(last);
        return last!;
    }

    [Fact]
    public void A_Reed_Solomon_test_with_pieces_missing_is_rebuilt_and_says_so()
    {
        // A weak path: two stretches of 4 seconds lost (each dropout splits the transmission; the log
        // joins the parts back together).
        var result = Faded(10, 15, 35);   // RS2
        Assert.True(result.Perfect);
        Assert.StartsWith("perfect: Reed-Solomon rebuilt ", result.Outcome);
        Assert.Equal(true, result.SettingsMatch);
        Assert.EndsWith("RS2", result.SettingsReceived);
    }

    [Fact]
    public void A_fountain_test_with_pieces_missing_is_rebuilt_and_says_so()
    {
        var result = Faded(12, 15, 35);   // Fountain 50%, the same fades as the RS2 test above
        Assert.True(result.Perfect);
        Assert.StartsWith("perfect: the fountain code rebuilt ", result.Outcome);
        Assert.Equal(true, result.SettingsMatch);
        Assert.EndsWith("Fountain 50%", result.SettingsReceived);
    }

    [Fact]
    public void A_fountain_test_cut_short_says_how_many_pieces_would_have_done()
    {
        var test = TestTransmissions.All.Single(t => t.Number == 12);
        var report = Report(test.Transmitter("KD9DD"), keepSeconds: 20);
        var result = TestResults.Recognise(report)!;
        Assert.False(result.Perfect);
        int k = result.FountainPieces!.Value;
        Assert.Equal($"{result.OwnPieces} of {report.PiecesExpected} pieces (any {k} would have done)", result.Outcome);
        Assert.Equal(true, result.SettingsMatch);
    }

    [Fact]
    public void A_dropout_in_the_middle_makes_one_log_line_not_two()
    {
        var test = TestTransmissions.All.Single(t => t.Number == 4);   // the one split in W9BED's log
        var audio = Pad(test.Transmitter("KD9DD").Audio());
        Array.Clear(audio, 15 * 48000, 4 * 48000);                     // 4 s of nothing, the sender carrying on
        var reports = Reports(audio);
        Assert.Equal(2, reports.Count);                                  // the receiver saw two...
        Assert.False(reports[0].ContinuesPrevious);
        Assert.True(reports[1].ContinuesPrevious);                       // ...but knows the second continues the first

        string path = Path.Combine(Path.GetTempPath(), $"rfpix-split-{Guid.NewGuid():N}.csv");
        try
        {
            var log = new TestLog(path);
            log.Add(TestResults.Recognise(reports[0])!);
            Assert.False(File.Exists(path));                             // the first part waits for its continuation
            var joined = log.Add(TestResults.Recognise(reports[1])!);
            Assert.Equal(reports[0].PacketsGood + reports[1].PacketsGood, joined.Report.PacketsGood);
            Assert.True(joined.Perfect);                                 // the two parts together brought every piece
            var lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);                               // titles + one line
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Each_sending_of_a_test_is_judged_on_its_own()
    {
        var test = TestTransmissions.All.Single(t => t.Number == 2);
        var signal = test.Transmitter("KD9DD").Audio();
        // Sent once cut short at 20 s, then again in full 3.1 s later (off the first one's beat).
        var cut = signal[..(20 * 48000)];
        var audio = Pad(cut.Concat(new float[(int)(3.1 * 48000)]).Concat(signal).ToArray());
        var reports = Reports(audio);
        Assert.Equal(2, reports.Count);
        Assert.False(reports[1].ContinuesPrevious);

        var first = TestResults.Recognise(reports[0])!;
        var second = TestResults.Recognise(reports[1])!;
        Assert.False(first.Perfect);
        Assert.True(second.Perfect);
        Assert.Equal(second.Report.PiecesExpected, second.OwnPieces);

        // And a second sending that itself lost pieces is reported by what it brought, not by
        // what the two sendings brought together.
        var lossy = signal.ToArray();
        Array.Clear(lossy, 20 * 48000, 8 * 48000);
        var both = Pad(cut.Concat(new float[(int)(3.1 * 48000)]).Concat(lossy).ToArray());
        var again = TestResults.Recognise(Reports(both).Last())!;
        Assert.InRange(again.OwnPieces, 1, again.Report.PiecesExpected - 1);
        Assert.False(again.Perfect);
    }

    private static float[] Pad(float[] signal)
    {
        var audio = new float[signal.Length + 2 * 48000];
        Array.Copy(signal, 0, audio, 48000, signal.Length);
        return audio;
    }

    private static List<TransmissionReport> Reports(float[] audio)
    {
        var receiver = new HamDrmReceiver();
        var reports = new List<TransmissionReport>();
        receiver.TransmissionReported += reports.Add;
        for (int pos = 0; pos + 480 <= audio.Length; pos += 480) receiver.AddSamples(audio[pos..(pos + 480)], 480, 48000);
        receiver.EndOfAudio();
        return reports;
    }

    [Fact]
    public void Other_files_are_not_tests()
    {
        var report = new TransmissionReport(DateTime.Now, "holiday.jpg", null, 0, 0, null, null, null, Array.Empty<double>(), 0, 0);
        Assert.Null(TestResults.Recognise(report));
        Assert.Null(TestResults.Recognise(report with { Name = "rfpix-test-99.jpg" }));
        Assert.Null(TestResults.Recognise(report with { Name = null }));
    }

    [Fact]
    public void A_burst_that_brought_no_data_leaves_nothing_in_the_next_report()
    {
        // The first 1.6 s of a long-interleaved transmission (its settings read, its quality measured,
        // but no data out of the deinterleaver yet), 3 s of quiet, then a whole other transmission.
        var burstTest = TestTransmissions.All.First(t => t.Settings.Interleave == HamDrmInterleave.Long);
        var burst = new HamDrmTransmitter(burstTest.FileName, burstTest.Picture(), burstTest.Settings, "W1AW").Audio()[..(int)(1.6 * 48000)];
        var test = TestTransmissions.All.Single(t => t.Number == 2);
        var whole = new HamDrmTransmitter(test.FileName, test.Picture(), test.Settings, "KD9DD").Audio();

        var alone = Reports(Pad(whole));
        var afterBurst = Reports(Pad(burst.Concat(new float[3 * 48000]).Concat(whole).ToArray()));
        var report = Assert.Single(afterBurst);   // the burst itself isn't reported
        Assert.Equal(alone.Single().FrameQualities.Count, report.FrameQualities.Count);   // none of the burst's
        Assert.False(report.ContinuesPrevious);
    }

    /// <summary>Plays a transmission (or its first seconds) through a receiver and returns its report.</summary>
    private static TransmissionReport Report(HamDrmTransmitter tx, double? keepSeconds = null)
    {
        var signal = tx.Audio();
        if (keepSeconds is double s) signal = signal[..(int)(s * 48000)];
        var audio = new float[signal.Length + 2 * 48000];
        Array.Copy(signal, 0, audio, 48000, signal.Length);
        var receiver = new HamDrmReceiver();
        TransmissionReport? report = null;
        receiver.TransmissionReported += r => report ??= r;
        for (int pos = 0; pos + 480 <= audio.Length; pos += 480) receiver.AddSamples(audio[pos..(pos + 480)], 480, 48000);
        receiver.EndOfAudio();
        Assert.NotNull(report);
        return report!;
    }
}
