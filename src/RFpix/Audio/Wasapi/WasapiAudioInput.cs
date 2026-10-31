// WasapiAudioInput.cs - the Windows backend for sound coming in from the radio.
//
// It talks to WASAPI, Windows' own sound system, through NAudio's thin wrappers.
// Shared mode is used for receiving: Windows can still play other sounds, and
// nothing gets mixed INTO a recording, so it's safe.
//
// RFpix runs its own capture loop (rather than NAudio's WasapiCapture) for one
// reason: Windows numbers every sample it captures, and only the raw loop can see
// those numbers. When they jump, sound was lost, and GapDetector puts silence of
// the same length in its place so the decoder's timing stays right.
//
// The loop runs on its own high-priority thread and only converts and hands over;
// the decoding happens elsewhere (AudioPump), so this thread is never late.
//
// Each start has its own Windows sound client, wake-up signal, gap counter and buffer, used
// only by its own loop thread, and that thread closes them when it finishes. (Closing them from
// Stop could free them while the loop was still using them; and a quick Stop and Start could
// leave the old loop running with the new start's.) Stop waits up to a second for the loop.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace RFpix.Audio.Wasapi;

[SupportedOSPlatform("windows")]
public sealed class WasapiAudioInput : IAudioInput
{
    private const int BufferMilliseconds = 200;   // Windows holds this much for us if we're late

    private readonly MMDeviceEnumerator _enumerator = new();

    /// <summary>One start: everything its loop thread uses, and closes when it finishes.</summary>
    private sealed class Session(AudioClient client, EventWaitHandle ready, GapDetector gaps)
    {
        public readonly AudioClient Client = client;
        public readonly EventWaitHandle Ready = ready;
        public readonly GapDetector Gaps = gaps;
        public volatile bool Running = true;
    }

    private Session? _session;
    private Thread? _thread;
    private GapDetector? _gaps;   // the latest start's, for Gaps (still counted after Stop)

    public bool IsRunning => _session is { Running: true };
    public string FormatDescription { get; private set; } = "";
    public int Gaps => _gaps?.Gaps ?? 0;

    public event EventHandler<AudioSamplesEventArgs>? SamplesAvailable;
    public event EventHandler<string>? Failed;

    public IReadOnlyList<AudioDeviceInfo> GetDevices()
    {
        var devices = new List<AudioDeviceInfo>();
        foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            devices.Add(new AudioDeviceInfo(device.ID, device.FriendlyName));
        }
        return devices;
    }

    public void Start(string deviceId)
    {
        Stop();
        AudioClient? client = null;
        EventWaitHandle? ready = null;
        try
        {
            var device = _enumerator.GetDevice(deviceId);
            client = device.AudioClient;
            var format = client.MixFormat;   // Windows' own format for this device: no conversion inside Windows

            // Event-driven: Windows signals us whenever a chunk is ready (in shared mode the
            // period must be 0, meaning Windows' own, 10 ms).
            client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.EventCallback,
                              BufferMilliseconds * 10_000L, 0, format, Guid.Empty);
            ready = new EventWaitHandle(false, EventResetMode.AutoReset);
            client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle());

            var session = new Session(client, ready, new GapDetector(format.SampleRate));
            client.Start();
            FormatDescription = $"{format.SampleRate} Hz, {format.BitsPerSample}-bit, {format.Channels} channel(s)";
            _gaps = session.Gaps;
            _session = session;
            _thread = new Thread(() => Run(session, format))
            {
                IsBackground = true,
                Priority = ThreadPriority.Highest,
                Name = "RFpix sound input",
            };
            _thread.Start();
        }
        catch (Exception ex)
        {
            // No loop is using them yet: close them here.
            _session = null;
            try { client?.Stop(); } catch { /* not started */ }
            client?.Dispose();
            ready?.Dispose();
            FormatDescription = "";
            throw new AudioException(AudioErrors.Explain(ex), ex);
        }
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _session, null) is { } session)
        {
            session.Running = false;
            try { session.Ready.Set(); }   // wake the loop so it sees it should stop
            catch (ObjectDisposedException) { /* the loop has finished already */ }
        }
        // The loop finishes what it's doing, then closes the sound client itself.
        if (_thread is { } thread && thread != Thread.CurrentThread) thread.Join(1000);
        _thread = null;
        FormatDescription = "";
    }

    private void Run(Session session, WaveFormat format)
    {
        var client = session.Client;
        var ready = session.Ready;
        try
        {
            var capture = client.AudioCaptureClient;
            int bytesPerSample = format.BitsPerSample / 8;
            int frameSize = bytesPerSample * format.Channels;
            // Windows' shared-mode format is nearly always 32-bit floating point.
            bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
                        || (format.Encoding == WaveFormatEncoding.Extensible && format.BitsPerSample == 32);
            var bytes = Array.Empty<byte>();
            var buffer = Array.Empty<float>();

            while (session.Running)
            {
                ready.WaitOne(BufferMilliseconds);
                while (session.Running && capture.GetNextPacketSize() > 0)
                {
                    IntPtr data = capture.GetBuffer(out int frames, out var flags, out long position, out _);

                    // Lost sound: put the same length of silence in its place.
                    bool positionValid = (flags & AudioClientBufferFlags.TimestampError) == 0;
                    long missing = session.Gaps.Missing(position, frames, positionValid);
                    if (missing > 0) Silence(missing, format.SampleRate);

                    if (bytes.Length < frames * frameSize) bytes = new byte[frames * frameSize];
                    if ((flags & AudioClientBufferFlags.Silent) != 0) Array.Clear(bytes, 0, frames * frameSize);
                    else Marshal.Copy(data, bytes, 0, frames * frameSize);
                    capture.ReleaseBuffer(frames);

                    Convert(bytes, frames, frameSize, bytesPerSample, isFloat, ref buffer);
                    SamplesAvailable?.Invoke(this, new AudioSamplesEventArgs(buffer, frames, format.SampleRate));
                }
            }
        }
        catch (Exception ex)
        {
            // Usually the radio was switched off or unplugged.
            if (session.Running)
            {
                session.Running = false;
                Failed?.Invoke(this, AudioErrors.Explain(ex));
            }
        }
        finally
        {
            // Only this loop uses them, so only it closes them.
            try { client.Stop(); } catch { /* already stopped, or the device is gone */ }
            try { client.Dispose(); } catch { /* the device is gone */ }
            ready.Dispose();
        }
    }

    private void Silence(long samples, int sampleRate)
    {
        int chunk = sampleRate / 100;
        var zeros = new float[chunk];
        for (long done = 0; done < samples; done += chunk)
        {
            int n = (int)Math.Min(chunk, samples - done);
            SamplesAvailable?.Invoke(this, new AudioSamplesEventArgs(zeros, n, sampleRate));
        }
    }

    // Windows hands us raw bytes. Convert them to one channel of numbers between
    // -1 and +1, whatever format the device happens to use.
    private static void Convert(byte[] bytes, int frames, int frameSize, int bytesPerSample, bool isFloat, ref float[] buffer)
    {
        if (buffer.Length < frames) buffer = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            int o = i * frameSize;   // first channel (left) only: radios send the same audio on both
            buffer[i] = bytesPerSample switch
            {
                4 when isFloat => BitConverter.ToSingle(bytes, o),
                4 => BitConverter.ToInt32(bytes, o) / 2147483648f,
                3 => Read24Bit(bytes, o) / 8388608f,
                2 => BitConverter.ToInt16(bytes, o) / 32768f,
                _ => 0f
            };
        }
    }

    private static int Read24Bit(byte[] b, int o)
    {
        int value = b[o] | (b[o + 1] << 8) | (b[o + 2] << 16);
        if ((value & 0x800000) != 0) value |= unchecked((int)0xFF000000);   // keep the minus sign
        return value;
    }

    // ---- the device's own recording level (Windows' Sound settings > Recording > Levels) ----
    // Used from the screen's thread only (the Receive tab's slider).

    private string? _levelDeviceId;
    private MMDevice? _levelDevice;

    private AudioEndpointVolume LevelControl(string deviceId)
    {
        if (_levelDevice is null || _levelDeviceId != deviceId)
        {
            ForgetLevelDevice();
            _levelDevice = _enumerator.GetDevice(deviceId);
            _levelDeviceId = deviceId;
        }
        return _levelDevice.AudioEndpointVolume;
    }

    private void ForgetLevelDevice()
    {
        _levelDevice?.Dispose();
        _levelDevice = null;
        _levelDeviceId = null;
    }

    public double? GetRecordingLevel(string deviceId)
    {
        try { return LevelControl(deviceId).MasterVolumeLevelScalar; }
        catch (Exception) { ForgetLevelDevice(); return null; }   // unplugged, or no level control
    }

    public bool SetRecordingLevel(string deviceId, double level)
    {
        try
        {
            LevelControl(deviceId).MasterVolumeLevelScalar = (float)Math.Clamp(level, 0, 1);
            return true;
        }
        catch (Exception) { ForgetLevelDevice(); return false; }
    }

    public void Dispose()
    {
        Stop();
        ForgetLevelDevice();
        _enumerator.Dispose();
    }
}
