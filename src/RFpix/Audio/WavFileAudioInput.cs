// WavFileAudioInput.cs - plays a WAV file into RFpix as if it were the radio.
//
// It sits behind the same IAudioInput "audio layer" as the sound card, so the
// level meter, waterfall and (later) the decoder can't tell the difference.
// That lets us test with the same recorded signals every time, and decode
// recordings that club members send in.
//
// How it works: the whole file is read into memory, then a background thread
// hands it out in 10-millisecond chunks, waiting between chunks so it plays at
// normal speed (or faster, see Speed).

using System.Diagnostics;

namespace RFpix.Audio;

public sealed class WavFileAudioInput : IAudioInput
{
    private WavAudio? _audio;
    private Thread? _thread;
    private volatile bool _stopRequested;
    private long _position;                 // samples handed out so far

    /// <summary>1 = normal speed (the default). 2 = twice as fast, and so on.
    /// 0 = as fast as the computer can go, for automated tests.</summary>
    public double Speed { get; set; } = 1;

    /// <summary>The file being played, or null.</summary>
    public string? FileName { get; private set; }

    public bool IsRunning => _thread is { IsAlive: true } && !_stopRequested;

    public string FormatDescription => _audio?.Description ?? "";

    /// <summary>How much has been played so far.</summary>
    public TimeSpan Position => _audio is null ? TimeSpan.Zero
        : TimeSpan.FromSeconds((double)Interlocked.Read(ref _position) / _audio.SampleRate);

    /// <summary>Length of the whole file.</summary>
    public TimeSpan Duration => _audio?.Duration ?? TimeSpan.Zero;

    public event EventHandler<AudioSamplesEventArgs>? SamplesAvailable;
    public event EventHandler<string>? Failed;

    /// <summary>Raised (on the playback thread) when the end of the file is reached.
    /// Not raised if playback is stopped early with <see cref="Stop"/>.</summary>
    public event EventHandler? Finished;

    /// <summary>A file isn't a device, so there's nothing to list.</summary>
    public IReadOnlyList<AudioDeviceInfo> GetDevices() => Array.Empty<AudioDeviceInfo>();

    /// <summary>Starts playing. For this input the "device" is the path of the WAV file.
    /// Throws <see cref="AudioException"/> if the file can't be read.</summary>
    public void Start(string path)
    {
        Stop();
        _audio = WavFile.Read(path);       // throws AudioException with a plain message
        FileName = Path.GetFileName(path);
        Interlocked.Exchange(ref _position, 0);
        _stopRequested = false;

        _thread = new Thread(PlayLoop) { IsBackground = true, Name = "RFpix WAV playback" };
        _thread.Start();
    }

    public void Stop()
    {
        _stopRequested = true;
        // Wait for the thread to finish, unless Stop was called from inside it
        // (for example from a SamplesAvailable handler), which would wait forever.
        if (_thread is not null && _thread != Thread.CurrentThread) _thread.Join();
        _thread = null;
    }

    private void PlayLoop()
    {
        var audio = _audio!;
        int chunk = Math.Max(1, audio.SampleRate / 100);   // 10 milliseconds of sound
        var buffer = new float[chunk];
        var clock = Stopwatch.StartNew();

        try
        {
            long sent = 0;
            while (!_stopRequested && sent < audio.Samples.Length)
            {
                // At normal speed, don't get ahead of the clock: wait until it's time.
                if (Speed > 0)
                {
                    double due = clock.Elapsed.TotalSeconds * audio.SampleRate * Speed;
                    if (sent >= due)
                    {
                        Thread.Sleep(2);
                        continue;
                    }
                }

                int count = (int)Math.Min(chunk, audio.Samples.Length - sent);
                Array.Copy(audio.Samples, sent, buffer, 0, count);
                sent += count;
                Interlocked.Exchange(ref _position, sent);
                SamplesAvailable?.Invoke(this, new AudioSamplesEventArgs(buffer, count, audio.SampleRate));
            }

            if (!_stopRequested) Finished?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Failed?.Invoke(this, $"Playback of \"{FileName}\" stopped: {ex.Message}");
        }
    }

    public void Dispose() => Stop();
}
