// AudioRecorder.cs - records what the radio hears to a WAV file.
//
// While RFpix is listening, the last 30 seconds of sound are always kept in memory
// (about 6 MB). Pressing Record starts the file with those 30 seconds, so a
// transmission you only noticed once it had started is still caught from the beginning.
//
// Audio arrives on the audio thread while Start and Stop come from the screen, so a
// lock lets only one of them in at a time.

using RFpix.Audio;

namespace RFpix.Receive;

public sealed class AudioRecorder
{
    /// <summary>How much sound before pressing Record goes into the file.</summary>
    public static readonly TimeSpan PreRoll = TimeSpan.FromSeconds(30);

    /// <summary>Recordings stop by themselves after this long (about 660 MB at 48 kHz),
    /// in case one is left running.</summary>
    public static readonly TimeSpan MaxLength = TimeSpan.FromHours(1);

    /// <summary>Documents\RFpix\Recordings.</summary>
    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RFpix", "Recordings");

    private readonly object _lock = new();
    private float[] _ring = Array.Empty<float>();   // the last 30 seconds, going round and round
    private int _ringPos, _ringFilled, _sampleRate;
    private WavWriter? _writer;
    private string? _path;

    /// <summary>Raised (on the audio thread) when a recording stops by itself: it reached
    /// <see cref="MaxLength"/>, the sound changed sample rate, or the disk couldn't be written.
    /// Gives the saved file's path (or null) and the reason.</summary>
    public event Action<string?, string>? StoppedByItself;

    public bool IsRecording
    {
        get { lock (_lock) return _writer is not null; }
    }

    /// <summary>Length and size of the recording so far (including the 30 seconds before).</summary>
    public (TimeSpan Length, long Bytes) Progress
    {
        get { lock (_lock) return _writer is null ? default : (_writer.Duration, _writer.Bytes); }
    }

    /// <summary>Feed in audio exactly as it comes from the audio layer.</summary>
    public void AddSamples(float[] samples, int count, int sampleRate)
    {
        string? stoppedPath = null, why = null;
        lock (_lock)
        {
            if (sampleRate != _sampleRate)
            {
                if (_writer is not null) (stoppedPath, why) = (Close(), "The sound's sample rate changed.");
                _sampleRate = sampleRate;
                _ring = new float[(int)(sampleRate * PreRoll.TotalSeconds)];
                _ringPos = _ringFilled = 0;
            }

            if (_writer is not null)
            {
                try
                {
                    _writer.Write(samples.AsSpan(0, count));
                    if (_writer.Duration >= MaxLength)
                        (stoppedPath, why) = (Close(), $"Recordings stop after {MaxLength.TotalMinutes:0} minutes.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    (stoppedPath, why) = (TryClose(), $"Couldn't write the recording: {ex.Message}");
                }
            }

            // Keep the last 30 seconds.
            for (int i = 0; i < count; i++)
            {
                _ring[_ringPos] = samples[i];
                if (++_ringPos == _ring.Length) _ringPos = 0;
            }
            _ringFilled = Math.Min(_ringFilled + count, _ring.Length);
        }
        if (why is not null) StoppedByItself?.Invoke(stoppedPath, why);
    }

    /// <summary>Starts a recording in the folder, beginning with the last 30 seconds heard.
    /// Returns the file's path. Throws IOException or UnauthorizedAccessException if the file
    /// can't be made, or InvalidOperationException if no sound has arrived yet.</summary>
    public string Start(string folder, DateTime when)
    {
        lock (_lock)
        {
            if (_writer is not null) return _path!;
            if (_sampleRate == 0) throw new InvalidOperationException("No sound has arrived yet.");

            Directory.CreateDirectory(folder);
            string path = FreeName(folder, $"{when:yyyy-MM-dd HHmm} recording");
            _writer = new WavWriter(path, _sampleRate);
            _path = path;

            // The 30 seconds before: oldest first.
            int start = (_ringPos - _ringFilled + _ring.Length) % _ring.Length;
            int first = Math.Min(_ringFilled, _ring.Length - start);
            _writer.Write(_ring.AsSpan(start, first));
            _writer.Write(_ring.AsSpan(0, _ringFilled - first));
            return path;
        }
    }

    /// <summary>Stops recording. The callsigns heard during it (up to 3) replace "recording"
    /// in the file's name, e.g. "2026-10-11 1530 KD9DD.wav". Returns the saved file's path,
    /// or null if nothing was being recorded.</summary>
    public string? Stop(IEnumerable<string>? callsigns = null)
    {
        lock (_lock)
        {
            string? path = TryClose();
            var heard = (callsigns ?? Array.Empty<string>())
                .Select(Clean).Where(c => c.Length > 0).Distinct().Take(3).ToList();
            if (path is null || heard.Count == 0) return path;

            string name = Path.GetFileNameWithoutExtension(path);
            if (name.EndsWith(" recording")) name = name[..^" recording".Length];
            else if (name.Contains(" recording (")) name = name[..name.IndexOf(" recording (")];
            try
            {
                string renamed = FreeName(Path.GetDirectoryName(path)!, name + " " + string.Join(" ", heard));
                File.Move(path, renamed);
                return renamed;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return path;   // keep the name it has
            }
        }
    }

    // A callsign comes off the air: keep letters and digits, and turn anything else ("/") into "-".
    private static string Clean(string callsign) =>
        new string(callsign.Trim().Select(c => char.IsAsciiLetterOrDigit(c) ? char.ToUpperInvariant(c) : '-').ToArray()).Trim('-');

    /// <summary>Forgets the 30 seconds kept in memory, e.g. when switching to another source.
    /// Stops any recording first.</summary>
    public string? Reset()
    {
        lock (_lock)
        {
            string? path = TryClose();
            _sampleRate = 0;
            _ring = Array.Empty<float>();
            _ringPos = _ringFilled = 0;
            return path;
        }
    }

    private string? Close()
    {
        string? path = _path;
        _writer!.Dispose();
        _writer = null;
        _path = null;
        return path;
    }

    private string? TryClose()
    {
        if (_writer is null) return null;
        try { return Close(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _writer = null;
            string? path = _path;
            _path = null;
            return path;
        }
    }

    // "name.wav", or "name (2).wav" if that's taken.
    private static string FreeName(string folder, string name)
    {
        string path = Path.Combine(folder, name + ".wav");
        for (int n = 2; File.Exists(path); n++) path = Path.Combine(folder, $"{name} ({n}).wav");
        return path;
    }
}
