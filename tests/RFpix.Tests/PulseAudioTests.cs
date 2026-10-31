// PulseAudioTests.cs - the Linux sound backend, against a real sound server.
//
// These run only on Linux with RFPIX_PULSE_TEST=1 set, after starting PulseAudio with a test
// output ("rfpix_test_out") and a test input that hears it ("rfpix_test_in"), as GitHub's Linux
// build does:
//   pulseaudio -D --exit-idle-time=-1 -n -L module-native-protocol-unix \
//     -L "module-null-sink sink_name=rfpix_test_out" \
//     -L "module-remap-source source_name=rfpix_test_in master=rfpix_test_out.monitor"
// Elsewhere they pass without doing anything.

using RFpix.Audio;
using RFpix.Audio.Pulse;
using Xunit;

namespace RFpix.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("linux")]   // (each test does nothing elsewhere)
public class PulseAudioTests
{
    private const string Out = "rfpix_test_out", In = "rfpix_test_in";

    private static bool Ready => OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("RFPIX_PULSE_TEST") == "1";

    [Fact]
    public void The_sound_devices_are_listed_without_the_monitors()
    {
        if (!Ready) return;
        using var input = new PulseAudioInput();
        using var output = new PulseAudioOutput();
        var inputs = input.GetDevices();
        Assert.Contains(inputs, d => d.Id == In);
        Assert.DoesNotContain(inputs, d => d.Id.EndsWith(".monitor"));
        Assert.Contains(output.GetDevices(), d => d.Id == Out);
    }

    [Fact]
    public void The_recording_level_can_be_read_and_set()
    {
        if (!Ready) return;
        using var input = new PulseAudioInput();
        Assert.True(input.SetRecordingLevel(In, 0.5));
        Assert.InRange(input.GetRecordingLevel(In) ?? -1, 0.49, 0.51);
        Assert.True(input.SetRecordingLevel(In, 1.0));
        Assert.InRange(input.GetRecordingLevel(In) ?? -1, 0.99, 1.01);
        Assert.Null(input.GetRecordingLevel("no_such_device"));
        Assert.False(input.SetRecordingLevel("no_such_device", 0.5));
    }

    [Fact]
    public async Task Sound_played_out_is_heard_coming_in()
    {
        if (!Ready) return;
        using var input = new PulseAudioInput();
        using var output = new PulseAudioOutput();
        input.SetRecordingLevel(In, 1.0);
        double sumSquares = 0;
        long count = 0;
        int rate = 0;
        input.SamplesAvailable += (_, e) =>
        {
            rate = e.SampleRate;
            for (int i = 0; i < e.Count; i++) { sumSquares += (double)e.Samples[i] * e.Samples[i]; count++; }
        };
        input.Start(In);
        Assert.Contains("48000 Hz", input.FormatDescription);

        var tone = new float[48000 * 2];   // 2 seconds of 1 kHz at half the full level
        for (int i = 0; i < tone.Length; i++) tone[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 1000 * i / 48000.0));
        double reported = 0;
        var started = DateTime.UtcNow;
        await output.PlayAsync(Out, tone, 48000, null, new Progress<double>(p => reported = p), CancellationToken.None);
        double took = (DateTime.UtcNow - started).TotalSeconds;
        await Task.Delay(300);
        input.Stop();

        Assert.Equal(48000, rate);
        Assert.InRange(took, 1.7, 5.0);                      // about as long as the sound lasts (plus the device's delay)
        Assert.True(count > 48000, $"only {count} samples arrived");
        double rms = Math.Sqrt(sumSquares / count);
        Assert.True(rms > 0.1, $"the sound coming in was too quiet (rms {rms:0.000})");   // 0.35 when it all arrives
    }

    // Code review, Oct 2026: Stop closed the stream while the reading thread might still be using it.
    [Fact]
    public void Listening_can_be_stopped_and_started_quickly_over_and_over()
    {
        if (!Ready) return;
        using var input = new PulseAudioInput();
        int chunks = 0;
        input.SamplesAvailable += (_, _) => Interlocked.Increment(ref chunks);
        for (int i = 0; i < 20; i++)
        {
            input.Start(In);
            Thread.Sleep(i % 3 * 10);   // 0, 10 or 20 ms: sometimes in the middle of a read
            input.Stop();
            Assert.False(input.IsRunning);
        }

        input.Start(In);
        Thread.Sleep(500);
        Assert.True(input.IsRunning);
        Assert.True(Volatile.Read(ref chunks) > 0, "no sound arrived");
        input.Stop();
        int atStop = Volatile.Read(ref chunks);
        Thread.Sleep(300);
        Assert.InRange(Volatile.Read(ref chunks) - atStop, 0, 1);   // nothing after Stop (but a read already under way)
    }

    [Fact]
    public async Task Stopping_a_sending_stops_it_at_once()
    {
        if (!Ready) return;
        using var output = new PulseAudioOutput();
        var long_ = new float[48000 * 30];
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var started = DateTime.UtcNow;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => output.PlayAsync(Out, long_, 48000, () => 0.5, null, cancel.Token));
        Assert.InRange((DateTime.UtcNow - started).TotalSeconds, 0.5, 2.5);
    }

    [Fact]
    public async Task The_output_clock_can_be_measured()
    {
        if (!Ready) return;
        using var output = new PulseAudioOutput();
        var readings = new List<(double At, long Samples)>();
        await output.MeasureClockAsync(Out, TimeSpan.FromSeconds(3), (at, samples, rate) => readings.Add((at, samples)), CancellationToken.None);
        Assert.True(readings.Count > 50, $"only {readings.Count} readings");
        var (a, b) = (readings[readings.Count / 4], readings[^1]);
        double samplesPerSecond = (b.Samples - a.Samples) / (b.At - a.At);
        Assert.InRange(samplesPerSecond, 46000, 50000);      // about 48000 a second
    }
}
