// CoreAudioInput.cs - the Mac backend for sound coming in from the radio.
//
// An Audio Queue records from the chosen device in two channels of 32-bit floating point at
// 48 kHz (macOS converts from the device's own format and rate). Every 20 ms macOS hands over
// a buffer of sound on a thread of its own; the left channel is passed on (radios send the same
// on both, and the IC-9700 can put its second receiver on the right), and the buffer is given back.
//
// macOS asks once whether RFpix may use the microphone (the radio's sound input counts as one).
// If that was refused, the sound is silent: System Settings > Privacy & Security > Microphone.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static RFpix.Audio.CoreAudio.CoreAudioNative;

namespace RFpix.Audio.CoreAudio;

[SupportedOSPlatform("macos")]
public sealed class CoreAudioInput : IAudioInput
{
    public const int SampleRate = 48000;
    private const int Channels = 2;
    private const int BufferCount = 6;
    private const int BufferFrames = SampleRate / 50;   // 20 ms

    private readonly InputCallback _callback;   // kept so the garbage collector leaves it alone
    private IntPtr _queue;
    private volatile bool _running;
    private float[] _interleaved = new float[BufferFrames * Channels * 2];
    private float[] _buffer = new float[BufferFrames * 2];
    private long _lastSound;
    private Timer? _watchdog;

    public CoreAudioInput() => _callback = OnBuffer;

    public bool IsRunning => _running;
    public string FormatDescription { get; private set; } = "";
    public event EventHandler<AudioSamplesEventArgs>? SamplesAvailable;
    public event EventHandler<string>? Failed;

    public IReadOnlyList<AudioDeviceInfo> GetDevices() => ListDevices(input: true);

    public void Start(string deviceId)
    {
        Stop();
        try
        {
            var format = StreamFormat.Float(SampleRate, Channels);
            Check(AudioQueueNewInput(ref format, _callback, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out var queue), "open the sound input");
            _queue = queue;
            UseDevice(queue, deviceId);
            for (int i = 0; i < BufferCount; i++)
            {
                Check(AudioQueueAllocateBuffer(queue, BufferFrames * Channels * 4, out var buffer), "make room for the sound");
                Check(AudioQueueEnqueueBuffer(queue, buffer, 0, IntPtr.Zero), "make room for the sound");
            }
            _running = true;
            Interlocked.Exchange(ref _lastSound, Environment.TickCount64);
            int status = AudioQueueStart(queue, IntPtr.Zero);
            if (status != 0)
                throw new AudioException($"The Mac didn't let RFpix use that sound input (error {status}). If it's the radio, check " +
                                         "System Settings > Privacy & Security > Microphone and allow RFpix, then click Start listening again.");
            FormatDescription = $"{SampleRate} Hz, 32-bit, {Channels} channel(s)";
            // macOS says nothing when a device goes away: if no sound arrives for 3 seconds, say so.
            _watchdog = new Timer(_ => CheckStillComing(), null, 1000, 1000);
        }
        catch (AudioException)
        {
            Stop();
            throw;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Stop();
            throw new AudioException("The Mac's sound system couldn't be reached: " + ex.Message, ex);
        }
    }

    private void CheckStillComing()
    {
        if (!_running) return;
        if (Environment.TickCount64 - Interlocked.Read(ref _lastSound) < 3000) return;
        Stop();
        Failed?.Invoke(this, "No sound is coming from the radio's sound input. The radio may have been switched off or its USB cable " +
                             "unplugged. Check it, then click Refresh in Settings and Start listening again.");
    }

    // On macOS's own thread: a buffer of sound has arrived.
    private void OnBuffer(IntPtr userData, IntPtr queue, IntPtr buffer, IntPtr startTime, uint packets, IntPtr descriptions)
    {
        if (!_running) return;
        try
        {
            int bytes = Marshal.ReadInt32(buffer, BufferByteSizeOffset);
            IntPtr data = Marshal.ReadIntPtr(buffer, BufferDataOffset);
            int frames = bytes / (4 * Channels);
            if (frames > 0)
            {
                if (_interleaved.Length < frames * Channels) _interleaved = new float[frames * Channels];
                if (_buffer.Length < frames) _buffer = new float[frames];
                Marshal.Copy(data, _interleaved, 0, frames * Channels);
                for (int i = 0; i < frames; i++) _buffer[i] = _interleaved[i * Channels];   // left channel
                Interlocked.Exchange(ref _lastSound, Environment.TickCount64);
                SamplesAvailable?.Invoke(this, new AudioSamplesEventArgs(_buffer, frames, SampleRate));
            }
        }
        catch (Exception)
        {
            // Never let an error escape into macOS's thread; the watchdog notices if sound stops.
        }
        if (_running) AudioQueueEnqueueBuffer(queue, buffer, 0, IntPtr.Zero);
    }

    public void Stop()
    {
        _running = false;
        _watchdog?.Dispose();
        _watchdog = null;
        var queue = Interlocked.Exchange(ref _queue, IntPtr.Zero);
        if (queue != IntPtr.Zero)
        {
            AudioQueueStop(queue, true);
            AudioQueueDispose(queue, true);
        }
        FormatDescription = "";
    }

    public void Dispose() => Stop();
}
