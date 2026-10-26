// PulseAudioOutput.cs - the Linux backend for sound going out to the radio.
//
// A playback stream to the chosen device: RFpix's one channel is copied to both, as 32-bit
// floating point at the sound's own rate (the sound server converts). It's written 50 ms at a
// time on a thread of its own (writing waits while the server's 200 ms buffer is full, which
// paces it); when all of it is written, RFpix waits for the server to finish playing it.
// Stopping (Cancel) stops writing and throws away the little that's still waiting.

using System.Runtime.Versioning;
using static RFpix.Audio.Pulse.PulseNative;

namespace RFpix.Audio.Pulse;

[SupportedOSPlatform("linux")]
public sealed class PulseAudioOutput : IAudioOutput
{
    private const int Channels = 2;

    public IReadOnlyList<AudioDeviceInfo> GetDevices()
    {
        try { return ListDevices(input: false); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new AudioException("Linux's sound library (libpulse) isn't installed. Install the libpulse0 package, then click Refresh.", ex);
        }
    }

    private static IntPtr Open(string deviceId, int rate)
    {
        var spec = new SampleSpec { Format = SampleFloat32Le, Rate = (uint)rate, Channels = Channels };
        uint bytes(double seconds) => (uint)(Math.Round(seconds * rate) * Channels * 4);
        var attr = new BufferAttr { MaxLength = uint.MaxValue, TLength = bytes(0.2), PreBuf = uint.MaxValue, MinReq = bytes(0.05),
                                    FragSize = uint.MaxValue };
        IntPtr stream;
        int error;
        try { stream = pa_simple_new(null, "RFpix", StreamPlayback, deviceId, "Send", ref spec, IntPtr.Zero, ref attr, out error); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new AudioException("Linux's sound library (libpulse) isn't installed. Install the libpulse0 package.", ex);
        }
        if (stream == IntPtr.Zero)
            throw new AudioException($"Couldn't play to that sound output ({ErrorText(error)}). Check the radio is switched on and plugged in, then click Refresh in Settings.");
        return stream;
    }

    public Task PlayAsync(string deviceId, float[] samples, int sampleRate, Func<double>? level, IProgress<double>? progress,
                          CancellationToken cancel) =>
        Task.Factory.StartNew(() =>
        {
            IntPtr stream = Open(deviceId, sampleRate);
            try
            {
                var ramp = level is null ? null : new LevelRamp(level);
                int chunk = sampleRate / 20;   // 50 ms
                var mono = new float[chunk];
                var stereo = new float[chunk * Channels];
                for (int at = 0; at < samples.Length && !cancel.IsCancellationRequested; at += chunk)
                {
                    int n = Math.Min(chunk, samples.Length - at);
                    Array.Copy(samples, at, mono, 0, n);
                    ramp?.Apply(mono, 0, n);
                    for (int i = 0; i < n; i++) stereo[2 * i] = stereo[2 * i + 1] = mono[i];
                    if (n < chunk) Array.Clear(stereo, 2 * n, stereo.Length - 2 * n);
                    // Write blocks while the server's buffer (200 ms) is full: that's the pacing.
                    var piece = n == chunk ? stereo : stereo[..(2 * n)];
                    if (pa_simple_write(stream, piece, (UIntPtr)(piece.Length * 4), out int error) < 0)
                        throw new AudioException($"The sound output stopped ({ErrorText(error)}). The radio may have been switched off or unplugged.");
                    progress?.Report((double)(at + n) / samples.Length);
                }
                // Wait for the last of it (at most the 200 ms buffer, plus the device's own delay) to be played.
                if (cancel.IsCancellationRequested) pa_simple_flush(stream, out _);
                else pa_simple_drain(stream, out _);
            }
            finally { pa_simple_free(stream); }
            cancel.ThrowIfCancellationRequested();
            progress?.Report(1);
        }, cancel, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    // Calibration: silence is written, and the samples played so far (written, less what's still
    // waiting to be played) are reported with the time every 10 ms or so.
    public Task MeasureClockAsync(string deviceId, TimeSpan duration, Action<double, long, int> reading, CancellationToken cancel) =>
        Task.Factory.StartNew(() =>
        {
            const int rate = 48000, chunk = rate / 100;   // 10 ms
            IntPtr stream = Open(deviceId, rate);
            try
            {
                var silence = new float[chunk * Channels];
                long written = 0;
                var end = DateTime.UtcNow + duration;
                while (DateTime.UtcNow < end && !cancel.IsCancellationRequested)
                {
                    if (pa_simple_write(stream, silence, (UIntPtr)(silence.Length * 4), out int error) < 0)
                        throw new AudioException($"The sound output stopped ({ErrorText(error)}).");
                    written += chunk;
                    ulong waiting = pa_simple_get_latency(stream, out _);
                    if (waiting == ulong.MaxValue) continue;
                    long played = written - (long)Math.Round(waiting * (double)rate / 1e6);
                    if (played > 0) reading(DateTime.UtcNow.Ticks / 1e7, played, rate);
                }
                pa_simple_flush(stream, out _);
            }
            finally { pa_simple_free(stream); }
            cancel.ThrowIfCancellationRequested();
        }, cancel, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    public void Dispose() { }
}
