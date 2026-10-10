// WasapiAudioInput.cs - the Windows backend for the audio layer.
//
// It uses NAudio to talk to WASAPI, Windows' own sound system. Shared mode is
// used for receiving: Windows can still play other sounds, and nothing gets
// mixed INTO a recording, so it's safe. (Transmit will use exclusive mode later,
// so nothing else can leak onto the air.)

using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace RFpix.Audio.Wasapi;

[SupportedOSPlatform("windows")]
public sealed class WasapiAudioInput : IAudioInput
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private WasapiCapture? _capture;
    private float[] _buffer = Array.Empty<float>();
    private volatile bool _running;

    public bool IsRunning => _running;
    public string FormatDescription { get; private set; } = "";

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

            // Ask Windows for audio in 50 ms chunks, in shared mode.
            var capture = new WasapiCapture(device, true, 50)
            {
                ShareMode = AudioClientShareMode.Shared
            };
            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;

            var format = capture.WaveFormat;
            FormatDescription = $"{format.SampleRate} Hz, {format.BitsPerSample}-bit, {format.Channels} channel(s)";

            _capture = capture;
            _running = true;
            capture.StartRecording();
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
        var capture = _capture;
        _capture = null;
        if (capture is null) return;

        capture.DataAvailable -= OnDataAvailable;
        capture.RecordingStopped -= OnRecordingStopped;
        try { capture.StopRecording(); } catch { /* already stopped */ }
        capture.Dispose();
        FormatDescription = "";
    }

    // Windows hands us raw bytes. Convert them to one channel of numbers between
    // -1 and +1, whatever format the device happens to use.
    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var capture = _capture;
        if (capture is null) return;

        var format = capture.WaveFormat;
        int bytesPerSample = format.BitsPerSample / 8;
        int frameSize = bytesPerSample * format.Channels;
        if (frameSize == 0) return;

        int frames = e.BytesRecorded / frameSize;
        if (_buffer.Length < frames) _buffer = new float[frames];

        // Windows' shared-mode format is nearly always 32-bit floating point.
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
                    || (format.Encoding == WaveFormatEncoding.Extensible && format.BitsPerSample == 32);

        byte[] bytes = e.Buffer;
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

        SamplesAvailable?.Invoke(this, new AudioSamplesEventArgs(_buffer, frames, format.SampleRate));
    }

    private static int Read24Bit(byte[] b, int o)
    {
        int value = b[o] | (b[o + 1] << 8) | (b[o + 2] << 16);
        if ((value & 0x800000) != 0) value |= unchecked((int)0xFF000000);   // keep the minus sign
        return value;
    }

    // Called when recording stops. If it stopped because of a problem
    // (radio unplugged, for example), tell the rest of the app why.
    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        _running = false;
        if (e.Exception is not null)
        {
            Failed?.Invoke(this, AudioErrors.Explain(e.Exception));
        }
    }

    public void Dispose()
    {
        Stop();
        _enumerator.Dispose();
    }
}
