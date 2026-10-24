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
    private AudioClient? _client;
    private EventWaitHandle? _ready;
    private Thread? _thread;
    private GapDetector? _gaps;
    private float[] _buffer = Array.Empty<float>();
    private volatile bool _running;

    public bool IsRunning => _running;
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
        try
        {
            var device = _enumerator.GetDevice(deviceId);
            var client = device.AudioClient;
            var format = client.MixFormat;   // Windows' own format for this device: no conversion inside Windows

            // Event-driven: Windows signals us whenever a chunk is ready (in shared mode the
            // period must be 0, meaning Windows' own, 10 ms).
            client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.EventCallback,
                              BufferMilliseconds * 10_000L, 0, format, Guid.Empty);
            var ready = new EventWaitHandle(false, EventResetMode.AutoReset);
            client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle());

            FormatDescription = $"{format.SampleRate} Hz, {format.BitsPerSample}-bit, {format.Channels} channel(s)";
            _client = client;
            _ready = ready;
            _gaps = new GapDetector(format.SampleRate);
            _running = true;
            client.Start();
            _thread = new Thread(() => Run(client, format, ready))
            {
                IsBackground = true,
                Priority = ThreadPriority.Highest,
                Name = "RFpix sound input",
            };
            _thread.Start();
        }
        catch (Exception ex)
        {
            Stop();
            throw new AudioException(AudioErrors.Explain(ex), ex);
        }
    }

    public void Stop()
    {
        _running = false;
        _ready?.Set();                                 // wake the loop so it sees it should stop
        if (_thread is { } thread && thread != Thread.CurrentThread) thread.Join(1000);
        _thread = null;
        try { _client?.Stop(); } catch { /* already stopped */ }
        _client?.Dispose();
        _client = null;
        _ready?.Dispose();
        _ready = null;
        FormatDescription = "";
    }

    private void Run(AudioClient client, WaveFormat format, EventWaitHandle ready)
    {
        try
        {
            var capture = client.AudioCaptureClient;
            int bytesPerSample = format.BitsPerSample / 8;
            int frameSize = bytesPerSample * format.Channels;
            // Windows' shared-mode format is nearly always 32-bit floating point.
            bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
                        || (format.Encoding == WaveFormatEncoding.Extensible && format.BitsPerSample == 32);
            var bytes = Array.Empty<byte>();

            while (_running)
            {
                ready.WaitOne(BufferMilliseconds);
                while (_running && capture.GetNextPacketSize() > 0)
                {
                    IntPtr data = capture.GetBuffer(out int frames, out var flags, out long position, out _);

                    // Lost sound: put the same length of silence in its place.
                    bool positionValid = (flags & AudioClientBufferFlags.TimestampError) == 0;
                    long missing = _gaps!.Missing(position, frames, positionValid);
                    if (missing > 0) Silence(missing, format.SampleRate);

                    if (bytes.Length < frames * frameSize) bytes = new byte[frames * frameSize];
                    if ((flags & AudioClientBufferFlags.Silent) != 0) Array.Clear(bytes, 0, frames * frameSize);
                    else Marshal.Copy(data, bytes, 0, frames * frameSize);
                    capture.ReleaseBuffer(frames);

                    Convert(bytes, frames, frameSize, bytesPerSample, isFloat);
                    SamplesAvailable?.Invoke(this, new AudioSamplesEventArgs(_buffer, frames, format.SampleRate));
                }
            }
        }
        catch (Exception ex)
        {
            // Usually the radio was switched off or unplugged.
            if (_running)
            {
                _running = false;
                Failed?.Invoke(this, AudioErrors.Explain(ex));
            }
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
    private void Convert(byte[] bytes, int frames, int frameSize, int bytesPerSample, bool isFloat)
    {
        if (_buffer.Length < frames) _buffer = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            int o = i * frameSize;   // first channel (left) only: radios send the same audio on both
            _buffer[i] = bytesPerSample switch
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
