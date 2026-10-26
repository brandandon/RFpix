// PulseAudioInput.cs - the Linux backend for sound coming in from the radio.
//
// A recording stream from the chosen device, two channels of 32-bit floating point at 48 kHz
// (the sound server converts from the device's own format). A thread of RFpix's own reads it
// 20 ms at a time and passes on the left channel, as on Windows and the Mac.

using System.Runtime.Versioning;
using static RFpix.Audio.Pulse.PulseNative;

namespace RFpix.Audio.Pulse;

[SupportedOSPlatform("linux")]
public sealed class PulseAudioInput : IAudioInput
{
    public const int SampleRate = 48000;
    private const int Channels = 2;
    private const int ChunkFrames = SampleRate / 50;   // 20 ms

    private IntPtr _stream;
    private Thread? _thread;
    private volatile bool _running;

    public bool IsRunning => _running;
    public string FormatDescription { get; private set; } = "";
    public event EventHandler<AudioSamplesEventArgs>? SamplesAvailable;
    public event EventHandler<string>? Failed;

    public IReadOnlyList<AudioDeviceInfo> GetDevices()
    {
        try { return ListDevices(input: true); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new AudioException("Linux's sound library (libpulse) isn't installed. Install the libpulse0 package, then click Refresh.", ex);
        }
    }

    public void Start(string deviceId)
    {
        Stop();
        var spec = new SampleSpec { Format = SampleFloat32Le, Rate = SampleRate, Channels = Channels };
        var attr = new BufferAttr { MaxLength = uint.MaxValue, TLength = uint.MaxValue, PreBuf = uint.MaxValue, MinReq = uint.MaxValue,
                                    FragSize = ChunkFrames * Channels * 4 };
        IntPtr stream;
        int error;
        try { stream = pa_simple_new(null, "RFpix", StreamRecord, deviceId, "Receive", ref spec, IntPtr.Zero, ref attr, out error); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new AudioException("Linux's sound library (libpulse) isn't installed. Install the libpulse0 package.", ex);
        }
        if (stream == IntPtr.Zero)
            throw new AudioException($"Couldn't listen to that sound input ({ErrorText(error)}). Check the radio is switched on and plugged in, then click Refresh in Settings.");
        _stream = stream;
        _running = true;
        FormatDescription = $"{SampleRate} Hz, 32-bit, {Channels} channel(s)";
        _thread = new Thread(Read) { IsBackground = true, Name = "RFpix sound input" };
        _thread.Start();
    }

    private void Read()
    {
        var interleaved = new float[ChunkFrames * Channels];
        var left = new float[ChunkFrames];
        while (_running)
        {
            if (pa_simple_read(_stream, interleaved, (UIntPtr)(interleaved.Length * 4), out int error) < 0)
            {
                if (!_running) return;
                _running = false;
                Failed?.Invoke(this, $"The sound input stopped ({ErrorText(error)}). The radio may have been switched off or its USB cable " +
                                     "unplugged. Check it, then click Refresh in Settings and Start listening again.");
                return;
            }
            for (int i = 0; i < ChunkFrames; i++) left[i] = interleaved[i * Channels];
            try { SamplesAvailable?.Invoke(this, new AudioSamplesEventArgs(left, ChunkFrames, SampleRate)); }
            catch (Exception) { /* a problem further on mustn't stop the sound */ }
        }
    }

    public void Stop()
    {
        _running = false;
        // The reading thread finishes its last 20 ms, then the stream is closed.
        if (_thread is { } thread && thread != Thread.CurrentThread) thread.Join(500);
        _thread = null;
        var stream = Interlocked.Exchange(ref _stream, IntPtr.Zero);
        if (stream != IntPtr.Zero) pa_simple_free(stream);
        FormatDescription = "";
    }

    // The device's own recording level (as in the sound settings), for the Receive tab's slider.
    public double? GetRecordingLevel(string deviceId)
    {
        try { return SourceVolume(deviceId); }
        catch (Exception ex) when (ex is AudioException or DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    public bool SetRecordingLevel(string deviceId, double level)
    {
        try { return SetSourceVolume(deviceId, level); }
        catch (Exception ex) when (ex is AudioException or DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    public void Dispose() => Stop();
}
