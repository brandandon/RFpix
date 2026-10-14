// AudioPumpTests.cs - checks that audio handed over from the sound card's thread arrives
// complete and in order, even when processing is slow, and that jobs keep their place.

using RFpix.Audio;
using Xunit;

namespace RFpix.Tests;

public class AudioPumpTests
{
    [Fact]
    public void Slow_processing_never_loses_or_reorders_audio()
    {
        var received = new List<float>();
        using var pump = new AudioPump((samples, count, rate, _) =>
        {
            for (int i = 0; i < count; i++) received.Add(samples[i]);
            if (received.Count % 4800 == 0) Thread.Sleep(30);   // a burst of slow work now and then
        });

        // The "sound card" hands over 10 ms chunks and reuses its buffer straight away.
        var buffer = new float[480];
        int next = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int chunk = 0; chunk < 400; chunk++)
        {
            for (int i = 0; i < 480; i++) buffer[i] = next++;
            pump.Add(buffer, 480, 48000);
            Array.Clear(buffer);
        }
        double handOverMs = clock.Elapsed.TotalMilliseconds;
        pump.Flush();

        Assert.Equal(0, pump.Dropped);
        Assert.Equal(Enumerable.Range(0, 400 * 480).Select(i => (float)i), received);
        Assert.True(handOverMs < 1000, $"handing over took {handOverMs:0} ms");   // never waited for the slow work
    }

    [Fact]
    public void A_job_runs_after_the_audio_added_before_it()
    {
        var events = new List<string>();
        using var pump = new AudioPump((_, count, _, source) => { Thread.Sleep(5); events.Add($"audio {source}"); });
        pump.Add(new float[480], 480, 48000, "a");
        pump.Add(new float[480], 480, 48000, "b");
        pump.Post(() => events.Add("end of audio"));
        pump.Add(new float[480], 480, 48000, "c");
        pump.Flush();

        Assert.Equal(new[] { "audio a", "audio b", "end of audio", "audio c" }, events);
    }

    [Fact]
    public void When_processing_falls_far_behind_the_extra_audio_is_counted_not_queued_forever()
    {
        using var gate = new ManualResetEventSlim();
        using var pump = new AudioPump((_, _, _, _) => gate.Wait(), capacity: 10);
        for (int i = 0; i < 25; i++) pump.Add(new float[480], 480, 48000);
        Assert.InRange(pump.Dropped, 14, 15);   // 10 waiting (+1 being processed)
        gate.Set();
        pump.Flush();
    }

    [Fact]
    public void A_failure_is_reported_and_processing_carries_on()
    {
        Exception? failure = null;
        int processed = 0;
        using var pump = new AudioPump((_, count, _, source) =>
        {
            if ((string?)source == "bad") throw new InvalidOperationException("broken chunk");
            processed++;
        });
        pump.Failed += ex => failure = ex;
        pump.Add(new float[480], 480, 48000, "bad");
        pump.Add(new float[480], 480, 48000, "good");
        pump.Flush();

        Assert.Equal("broken chunk", failure?.Message);
        Assert.Equal(1, processed);
    }
}
