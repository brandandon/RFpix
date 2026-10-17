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
        var test = TestTransmissions.All.Single(t => t.Number == 3);
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

    [Fact]
    public void Other_files_are_not_tests()
    {
        var report = new TransmissionReport(DateTime.Now, "holiday.jpg", null, 0, 0, null, null, null, null, 0, 0);
        Assert.Null(TestResults.Recognise(report));
        Assert.Null(TestResults.Recognise(report with { Name = "rfpix-test-99.jpg" }));
        Assert.Null(TestResults.Recognise(report with { Name = null }));
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
