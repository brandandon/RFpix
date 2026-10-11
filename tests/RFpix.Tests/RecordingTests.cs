// RecordingTests.cs - checks the Record button's pieces: the WAV writer and the recorder
// that keeps the last 30 seconds, so a transmission is caught from its start.

using RFpix.Audio;
using RFpix.HamDrm;
using RFpix.Receive;
using Xunit;

namespace RFpix.Tests;

public sealed class RecordingTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rfpix-rec-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime When = new(2026, 10, 11, 15, 30, 0);

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
    }

    [Fact]
    public void WAV_files_written_read_back_the_same()
    {
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, "tone.wav");
        var tone = Enumerable.Range(0, 48000).Select(i => (float)(0.5 * Math.Sin(2 * Math.PI * 1000 * i / 48000))).ToArray();
        using (var writer = new WavWriter(path, 48000))
        {
            writer.Write(tone.AsSpan(0, 1000));     // in pieces, as audio arrives
            writer.Write(tone.AsSpan(1000));
            writer.Write(new[] { 2f, -2f });          // too loud: clipped
        }

        var wav = WavFile.Read(path);
        Assert.Equal((48000, 1, 16), (wav.SampleRate, wav.Channels, wav.BitsPerSample));
        Assert.Equal(48002, wav.Samples.Length);
        for (int i = 0; i < tone.Length; i++) Assert.InRange(wav.Samples[i] - tone[i], -1 / 32767f, 1 / 32767f);
        Assert.InRange(wav.Samples[^2], 0.9999f, 1f);
        Assert.InRange(wav.Samples[^1], -1f, -0.9999f);
        Assert.Equal(44 + 48002 * 2, new FileInfo(path).Length);
    }

    [Fact]
    public void A_recording_starts_30_seconds_before_Record_was_pressed()
    {
        var recorder = new AudioRecorder();
        int rate = 8000;
        Feed(recorder, rate, from: 0, seconds: 40);             // listening for 40 s
        string path = recorder.Start(_folder, When);            // Record pressed
        Feed(recorder, rate, from: 40, seconds: 5);              // 5 s more
        var (length, _) = recorder.Progress;
        string saved = recorder.Stop()!;

        Assert.Equal(path, saved);
        Assert.InRange(length.TotalSeconds, 34.999, 35.001);
        var wav = WavFile.Read(saved);
        Assert.Equal(35 * rate, wav.Samples.Length);
        // The file begins with the sound from 10 s in, and carries on without a gap.
        for (int i = 0; i < wav.Samples.Length; i += 997)
            Assert.InRange(wav.Samples[i] - Marker(10 * rate + i), -2 / 32767f, 2 / 32767f);   // 16-bit rounding
    }

    [Fact]
    public void Pressed_early_it_holds_what_was_heard_so_far()
    {
        var recorder = new AudioRecorder();
        Assert.Throws<InvalidOperationException>(() => recorder.Start(_folder, When));   // nothing heard yet

        Feed(recorder, 8000, from: 0, seconds: 3);
        recorder.Start(_folder, When);
        var wav = WavFile.Read(recorder.Stop()!);
        Assert.Equal(3 * 8000, wav.Samples.Length);
        Assert.InRange(wav.Samples[0] - Marker(0), -2 / 32767f, 2 / 32767f);
    }

    [Fact]
    public void Files_are_named_after_the_callsigns_heard_and_never_overwritten()
    {
        var recorder = new AudioRecorder();
        Feed(recorder, 8000, from: 0, seconds: 1);

        recorder.Start(_folder, When);
        Assert.Equal("2026-10-11 1530 recording.wav", Path.GetFileName(recorder.Stop()));

        recorder.Start(_folder, When);
        Assert.Equal("2026-10-11 1530 KD9DD N0CALL-P.wav",
                     Path.GetFileName(recorder.Stop(new[] { "KD9DD", "n0call/p", "KD9DD", " " })));

        recorder.Start(_folder, When);
        Assert.Equal("2026-10-11 1530 KD9DD.wav", Path.GetFileName(recorder.Stop(new[] { "KD9DD" })));

        recorder.Start(_folder, When);
        Assert.Equal("2026-10-11 1530 KD9DD (2).wav", Path.GetFileName(recorder.Stop(new[] { "KD9DD" })));

        recorder.Start(_folder, When);
        Assert.Equal("2026-10-11 1530 recording (2).wav", Path.GetFileName(recorder.Stop()));
        Assert.Null(recorder.Stop());   // nothing recording
    }

    [Fact]
    public void A_change_of_sample_rate_ends_the_recording_and_keeps_it()
    {
        var recorder = new AudioRecorder();
        string? stoppedPath = null, why = null;
        recorder.StoppedByItself += (p, w) => (stoppedPath, why) = (p, w);

        Feed(recorder, 8000, from: 0, seconds: 2);
        string path = recorder.Start(_folder, When);
        Feed(recorder, 8000, from: 2, seconds: 1);
        recorder.AddSamples(new float[480], 480, 48000);   // the sound card was changed

        Assert.False(recorder.IsRecording);
        Assert.Equal(path, stoppedPath);
        Assert.Contains("sample rate", why);
        Assert.Equal(3 * 8000, WavFile.Read(path).Samples.Length);
    }

    [Fact]
    public void A_recording_made_mid_transmission_still_decodes_from_the_start()
    {
        // Record pressed 20 s into recording 04 (the signal began at 1 s): the 30 seconds
        // before reach back past the start, so the whole picture is in the file.
        var original = WavFile.Read(Recordings.Path("04_modeB_2p5khz_qam16_long_high_rs0.wav"));
        var recorder = new AudioRecorder();
        int pressAt = 20 * 48000;
        for (int pos = 0; pos + 480 <= original.Samples.Length; pos += 480)
        {
            if (pos == pressAt) recorder.Start(_folder, When);
            recorder.AddSamples(original.Samples[pos..(pos + 480)], 480, 48000);
        }
        string saved = recorder.Stop(new[] { "RFPIX" })!;
        Assert.Equal("2026-10-11 1530 RFPIX.wav", Path.GetFileName(saved));

        var receiver = new HamDrmReceiver();
        ReceivedFile? received = null;
        receiver.FileReceived += f => received ??= f;
        var audio = WavFile.Read(saved).Samples;
        for (int pos = 0; pos + 480 <= audio.Length; pos += 480)
            receiver.AddSamples(audio[pos..(pos + 480)], 480, 48000);
        receiver.EndOfAudio();

        Assert.Equal(File.ReadAllBytes(Path.Combine(Recordings.Folder, "images", "img04.jpg")), received?.Data);
    }

    // ---- helpers ----

    // A different, known value for every sample, so the file's contents can be checked.
    private static float Marker(long sample) => (sample % 2000 - 1000) / 1000f * 0.9f;

    private static void Feed(AudioRecorder recorder, int rate, int from, int seconds)
    {
        var chunk = new float[rate / 100];
        for (long s = (long)from * rate; s < (long)(from + seconds) * rate; s += chunk.Length)
        {
            for (int i = 0; i < chunk.Length; i++) chunk[i] = Marker(s + i);
            recorder.AddSamples(chunk, chunk.Length, rate);
        }
    }
}
