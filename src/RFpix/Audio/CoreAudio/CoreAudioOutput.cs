// CoreAudioOutput.cs - the Mac backend for sound going out to the radio.
//
// An Audio Queue plays to the chosen device: RFpix's one channel is copied to both channels,
// as 32-bit floating point at the sound's own rate (macOS converts to the device's format).
// Three buffers of 100 ms take turns: when macOS has played one, it hands it back and the next
// part of the sound goes in. After the last part, the queue is told to stop once it has played
// everything queued, and RFpix waits for that.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static RFpix.Audio.CoreAudio.CoreAudioNative;

namespace RFpix.Audio.CoreAudio;

[SupportedOSPlatform("macos")]
public sealed class CoreAudioOutput : IAudioOutput
{
    private const int Channels = 2;
    private const int BufferCount = 3;

    public IReadOnlyList<AudioDeviceInfo> GetDevices() => ListDevices(input: false);

    public async Task PlayAsync(string deviceId, float[] samples, int sampleRate, Func<double>? level, IProgress<double>? progress,
                                CancellationToken cancel)
    {
        using var player = new Player(samples, sampleRate, level is null ? null : new LevelRamp(level));
        player.Open(deviceId);
        using (cancel.Register(player.Abort))
        {
            while (!player.Finished)
            {
                progress?.Report(player.Fraction);
                await Task.Delay(50);
                player.StopWhenPlayed();
            }
        }
        cancel.ThrowIfCancellationRequested();
        progress?.Report(1);
    }

    // Calibration: silence is played, and the queue's count of samples played is read with the
    // time every 10 ms, on a thread of its own.
    public Task MeasureClockAsync(string deviceId, TimeSpan duration, Action<double, long, int> reading, CancellationToken cancel) =>
        Task.Factory.StartNew(() =>
        {
            const int rate = 48000;
            using var player = new Player(null, rate, null);
            player.Open(deviceId);
            var end = DateTime.UtcNow + duration;
            while (DateTime.UtcNow < end && !cancel.IsCancellationRequested)
            {
                Thread.Sleep(10);
                if (player.SamplesPlayed() is long played) reading(DateTime.UtcNow.Ticks / 1e7, played, rate);
            }
            player.Abort();
            cancel.ThrowIfCancellationRequested();
        }, cancel, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    public void Dispose() { }

    /// <summary>One sound being played (or, with no samples, silence until stopped).</summary>
    private sealed class Player : IDisposable
    {
        private readonly float[]? _samples;
        private readonly int _rate;
        private readonly LevelRamp? _level;
        private readonly OutputCallback _callback;   // kept so the garbage collector leaves it alone
        private readonly object _lock = new();
        private IntPtr _queue;
        private int _position;
        private bool _fed, _stopping;
        private volatile bool _aborted;
        private float[] _mono = Array.Empty<float>(), _stereo = Array.Empty<float>();

        public Player(float[]? samples, int rate, LevelRamp? level)
        {
            _samples = samples;
            _rate = rate;
            _level = level;
            _callback = OnBuffer;
        }

        public double Fraction => _samples is null || _samples.Length == 0 ? 1 : (double)_position / _samples.Length;

        public void Open(string deviceId)
        {
            try
            {
                var format = StreamFormat.Float(_rate, Channels);
                Check(AudioQueueNewOutput(ref format, _callback, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out var queue), "open the sound output");
                _queue = queue;
                UseDevice(queue, deviceId);
                int frames = _rate / 10;   // 100 ms each
                for (int i = 0; i < BufferCount; i++)
                {
                    Check(AudioQueueAllocateBuffer(queue, (uint)(frames * Channels * 4), out var buffer), "make room for the sound");
                    Fill(queue, buffer);
                }
                int status = AudioQueueStart(queue, IntPtr.Zero);
                if (status != 0) throw new AudioException(StartProblem(status, input: false));
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                throw new AudioException("The Mac's sound system couldn't be reached: " + ex.Message, ex);
            }
        }

        // On macOS's own thread: a buffer has been played; put the next part of the sound in it.
        private void OnBuffer(IntPtr userData, IntPtr queue, IntPtr buffer)
        {
            try { Fill(queue, buffer); }
            catch (Exception) { /* never let an error escape into macOS's thread */ }
        }

        private void Fill(IntPtr queue, IntPtr buffer)
        {
            if (_aborted) return;
            lock (_lock)
            {
                if (_aborted) return;
                int capacity = Marshal.ReadInt32(buffer, BufferCapacityOffset) / (4 * Channels);
                int frames;
                if (_samples is null)
                {
                    frames = capacity;   // silence, for calibration
                    if (_mono.Length < frames) _mono = new float[frames];
                    Array.Clear(_mono, 0, frames);
                }
                else
                {
                    frames = Math.Min(capacity, _samples.Length - _position);
                    if (frames <= 0) { _fed = true; return; }
                    if (_mono.Length < frames) _mono = new float[frames];
                    Array.Copy(_samples, _position, _mono, 0, frames);
                    _level?.Apply(_mono, 0, frames);
                    _position += frames;
                    if (_position >= _samples.Length) _fed = true;
                }
                if (_stereo.Length < frames * Channels) _stereo = new float[frames * Channels];
                for (int i = 0; i < frames; i++) _stereo[2 * i] = _stereo[2 * i + 1] = _mono[i];
                Marshal.Copy(_stereo, 0, Marshal.ReadIntPtr(buffer, BufferDataOffset), frames * Channels);
                Marshal.WriteInt32(buffer, BufferByteSizeOffset, frames * Channels * 4);
                AudioQueueEnqueueBuffer(queue, buffer, 0, IntPtr.Zero);
            }
        }

        /// <summary>Once all the sound is queued: tell the queue to stop after playing it.</summary>
        public void StopWhenPlayed()
        {
            lock (_lock)
            {
                if (!_fed || _stopping || _queue == IntPtr.Zero) return;
                _stopping = true;
                AudioQueueFlush(_queue);
                AudioQueueStop(_queue, false);   // returns at once; the queue stops when it's played everything
            }
        }

        public bool Finished
        {
            get
            {
                if (_aborted || _queue == IntPtr.Zero) return true;
                if (!_stopping) return false;
                uint size = 4;
                return AudioQueueGetProperty(_queue, QueueIsRunning, out uint running, ref size) != 0 || running == 0;
            }
        }

        public long? SamplesPlayed() =>
            _queue != IntPtr.Zero && AudioQueueGetCurrentTime(_queue, IntPtr.Zero, out var time, out _) == 0 ? (long)time.SampleTime : null;

        // Stopping at once waits for a buffer being filled, so the lock isn't held here
        // (the filling would wait for it, and neither would finish).
        public void Abort()
        {
            _aborted = true;
            var queue = _queue;
            if (queue != IntPtr.Zero) AudioQueueStop(queue, true);
        }

        public void Dispose()
        {
            _aborted = true;
            var queue = Interlocked.Exchange(ref _queue, IntPtr.Zero);
            if (queue == IntPtr.Zero) return;
            AudioQueueStop(queue, true);
            AudioQueueDispose(queue, true);
        }
    }
}
