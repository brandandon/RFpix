// AutoRecordTests.cs - checks "Record each picture" (Receive tab): when an automatic recording starts and stops, the
// few seconds it takes from before the picture, its name, and clearing out old ones (Settings > Misc).

using RFpix.Audio;
using RFpix.Receive;
using Xunit;

namespace RFpix.Tests;

public sealed class AutoRecordTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rfpix-auto-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Now = new(2026, 11, 28, 19, 30, 0);

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
    }

    [Fact]
    public void It_starts_when_a_picture_arrives_and_stops_5_seconds_after_it_ends()
    {
        var policy = new AutoRecordPolicy();
        var t = Now;
        Assert.Equal(AutoRecordStep.Nothing, policy.Update(busy: false, t, autoRecording: false));
        Assert.Equal(AutoRecordStep.Start, policy.Update(busy: true, t, autoRecording: false));
        Assert.Equal(AutoRecordStep.Nothing, policy.Update(busy: true, t.AddSeconds(60), autoRecording: true));
        // The picture ends: it carries on 5 seconds...
        Assert.Equal(AutoRecordStep.Nothing, policy.Update(busy: false, t.AddSeconds(61), autoRecording: true));
        Assert.Equal(AutoRecordStep.Nothing, policy.Update(busy: false, t.AddSeconds(65.9), autoRecording: true));
        Assert.Equal(AutoRecordStep.Stop, policy.Update(busy: false, t.AddSeconds(66), autoRecording: true));
        Assert.Equal(AutoRecordStep.Nothing, policy.Update(busy: false, t.AddSeconds(67), autoRecording: false));
    }

    [Fact]
    public void A_fade_that_comes_back_within_5_seconds_stays_in_the_same_recording()
    {
        var policy = new AutoRecordPolicy();
        var t = Now;
        policy.Update(busy: true, t, autoRecording: false);
        Assert.Equal(AutoRecordStep.Nothing, policy.Update(busy: false, t.AddSeconds(10), autoRecording: true));
        Assert.Equal(AutoRecordStep.Nothing, policy.Update(busy: true, t.AddSeconds(14), autoRecording: true));   // back
        // The 5 seconds count again from when it next ends.
        Assert.Equal(AutoRecordStep.Nothing, policy.Update(busy: false, t.AddSeconds(30), autoRecording: true));
        Assert.Equal(AutoRecordStep.Nothing, policy.Update(busy: false, t.AddSeconds(34), autoRecording: true));
        Assert.Equal(AutoRecordStep.Stop, policy.Update(busy: false, t.AddSeconds(35), autoRecording: true));
    }

    [Fact]
    public void Nothing_starts_during_a_Record_button_recording_or_after_Stop_recording_until_the_picture_ends()
    {
        var policy = new AutoRecordPolicy();
        var t = Now;
        // The Record button's recording already has the sound.
        Assert.Equal(AutoRecordStep.Nothing, policy.Update(busy: true, t, autoRecording: false, otherRecording: true));

        // An automatic recording stopped by hand ("Stop recording"): not started again for the same picture...
        policy.Hold();
        Assert.Equal(AutoRecordStep.Nothing, policy.Update(busy: true, t.AddSeconds(1), autoRecording: false));
        Assert.Equal(AutoRecordStep.Nothing, policy.Update(busy: true, t.AddSeconds(30), autoRecording: false));
        // ...but the next picture is.
        Assert.Equal(AutoRecordStep.Nothing, policy.Update(busy: false, t.AddSeconds(31), autoRecording: false));
        Assert.Equal(AutoRecordStep.Start, policy.Update(busy: true, t.AddSeconds(40), autoRecording: false));
    }

    [Fact]
    public void An_automatic_recording_takes_10_seconds_from_before_and_is_named_for_the_picture()
    {
        var recorder = new AudioRecorder();
        const int rate = 8000;
        Feed(recorder, rate, from: 0, seconds: 40);
        string path = recorder.Start(_folder, Now, AutoRecordPolicy.Lead);
        Feed(recorder, rate, from: 40, seconds: 3);
        string saved = recorder.Stop(new[] { "N0CALL", "n1call/p" }, "SSTV Martin 2")!;

        Assert.NotEqual(path, saved);
        Assert.Equal("2026-11-28 1930 SSTV Martin 2 N0CALL N1CALL-P.wav", Path.GetFileName(saved));
        var wav = WavFile.Read(saved);
        Assert.Equal(13 * rate, wav.Samples.Length);
        for (int i = 0; i < wav.Samples.Length; i += 997)   // from 30 s in, without a gap
            Assert.InRange(wav.Samples[i] - Marker(30 * rate + i), -2 / 32767f, 2 / 32767f);

        // Heard for less than 10 seconds: all of it. And a label alone names it too.
        var early = new AudioRecorder();
        Feed(early, rate, from: 0, seconds: 4);
        early.Start(_folder, Now, AutoRecordPolicy.Lead);
        string named = early.Stop(null, "RFpix FM")!;
        Assert.Equal("2026-11-28 1930 RFpix FM.wav", Path.GetFileName(named));
        Assert.Equal(4 * rate, WavFile.Read(named).Samples.Length);
    }

    [Fact]
    public void Recordings_older_than_the_days_set_are_deleted()
    {
        Directory.CreateDirectory(_folder);
        string old = Make("old.wav", 1000, Now.AddDays(-3.1));
        string kept = Make("kept.wav", 1000, Now.AddDays(-2.9));
        string notWav = Make("notes.txt", 1000, Now.AddDays(-30));

        var deleted = AutoRecordCleanup.Clean(_folder, days: 3, maxMb: 1000, Now);

        Assert.Equal(new[] { "old.wav" }, deleted);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(kept));
        Assert.True(File.Exists(notWav));   // only recordings
    }

    [Fact]
    public void Beyond_the_space_set_the_oldest_go_first_but_never_the_one_just_saved()
    {
        Directory.CreateDirectory(_folder);
        string a = Make("a.wav", 4_000_000, Now.AddHours(-4));
        string b = Make("b.wav", 4_000_000, Now.AddHours(-3));
        string c = Make("c.wav", 4_000_000, Now.AddHours(-2));
        string d = Make("d.wav", 4_000_000, Now.AddHours(-1));

        // 16 MB, at most 10 MB: the two oldest go.
        Assert.Equal(new[] { "a.wav", "b.wav" }, AutoRecordCleanup.Clean(_folder, days: 7, maxMb: 10, Now));
        Assert.True(File.Exists(c) && File.Exists(d));

        // Even when the one just saved is bigger than the space set on its own, it is kept.
        Assert.Equal(new[] { "c.wav" }, AutoRecordCleanup.Clean(_folder, days: 7, maxMb: 1, Now, keep: d));
        Assert.True(File.Exists(d));
    }

    [Fact]
    public void Days_are_1_to_7_and_a_missing_folder_is_fine()
    {
        Assert.Empty(AutoRecordCleanup.Clean(Path.Combine(_folder, "none"), 7, 1000, Now));
        Directory.CreateDirectory(_folder);
        Make("week.wav", 10, Now.AddDays(-6.5));
        Make("older.wav", 10, Now.AddDays(-7.5));
        Make("today.wav", 10, Now.AddHours(-12));
        Assert.Equal(new[] { "older.wav" }, AutoRecordCleanup.Clean(_folder, days: 30, maxMb: 1000, Now));   // at most 7
        Assert.Equal(new[] { "week.wav" }, AutoRecordCleanup.Clean(_folder, days: 0, maxMb: 1000, Now));     // at least 1
    }

    [Theory]
    [InlineData("1000", 1000)]
    [InlineData(" 2,500 ", 2500)]
    [InlineData("10", 10)]
    [InlineData("9", null)]
    [InlineData("lots", null)]
    [InlineData("", null)]
    [InlineData("1.5", null)]
    public void The_space_is_a_whole_number_of_MB(string text, int? expected) =>
        Assert.Equal(expected, AutoRecordCleanup.ParseMb(text));

    private string Make(string name, int bytes, DateTime written)
    {
        string path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTime(path, written);
        return path;
    }

    private static float Marker(int i) => (float)(0.5 * Math.Sin(i * 0.001) + 0.3 * Math.Sin(i * 0.0371));

    private static void Feed(AudioRecorder recorder, int rate, int from, int seconds)
    {
        var chunk = new float[rate / 10];
        for (int s = from * rate; s < (from + seconds) * rate; s += chunk.Length)
        {
            for (int i = 0; i < chunk.Length; i++) chunk[i] = Marker(s + i);
            recorder.AddSamples(chunk, chunk.Length, rate);
        }
    }
}
