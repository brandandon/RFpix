// FmSignalLiveReceiver.cs - listens for RFpix's FM signal in the radio's sound as it comes in, beside
// the HamDRM receiver.
//
// It does what FmSignalReceiver does for a whole recording, a little at a time:
//   - the sound goes through the tone notch (SubTones), into a store of the last few seconds;
//   - the frame finder runs along it as it arrives (the two identical sync symbols match each other);
//     a find is pinned and checked against the known sync sound;
//   - once a frame's 3 seconds are all in, it's decoded, and its pieces go to the collector
//     (FmPieceCollector), which rebuilds each file as soon as enough pieces are in.
// It needs 48 kHz sound, as RFpix's sound devices give.

using RFpix.Dsp;
using static RFpix.FmSignal.FmSignalLayout;

namespace RFpix.FmSignal;

/// <summary>What the last frame heard said, for the Receive tab.</summary>
/// <param name="Mode">Its speed.</param>
/// <param name="QualityDb">Its signal quality (signal-to-noise on its tones).</param>
/// <param name="Good">Its blocks that came through.</param>
/// <param name="Blocks">Its blocks.</param>
/// <param name="File">The file it was carrying, if any piece of one came through.</param>
public sealed record FmHeard(FmSignalMode Mode, double QualityDb, int Good, int Blocks, FmUnfinishedFile? File, string? Callsign);

/// <summary>How one file came through in one transmission (for the test log).</summary>
/// <param name="Name">Its name, if its details arrived.</param>
/// <param name="File">The file, if it was completed.</param>
/// <param name="PiecesSeen">The piece numbers this transmission brought.</param>
/// <param name="SourcePieces">Pieces that rebuild it.</param>
/// <param name="PiecesSent">About how many pieces were sent (the highest number heard, plus one).</param>
/// <param name="FrameQualities">Each frame's signal quality, in dB.</param>
public sealed record FmTransmissionReport(DateTime Ended, string? Name, FmReceivedFile? File, IReadOnlyList<int> PiecesSeen,
                                          int SourcePieces, int PiecesSent, string? Callsign, FmSignalMode? Mode,
                                          IReadOnlyList<double> FrameQualities, bool ReceivedEarlier = false,
                                          int BlocksGood = 0, int Blocks = 0)
{
    /// <summary>For the log: frames heard (and about how many were sent), blocks decoded, and the quality, so a
    /// transmission that didn't complete shows whether frames went unheard or arrived and failed.</summary>
    public string Details
    {
        get
        {
            int perFrame = Mode?.BlocksPerFrame ?? 0;
            // The pieces go out in order, so the span of piece numbers heard says about how many frames were sent
            // (a missing-pieces answer starts part way along).
            int span = PiecesSeen.Count > 0 ? PiecesSeen.Max() - PiecesSeen.Min() + 1 : 0;
            string sent = perFrame > 0 && span > 0 ? $" (about {(span + perFrame - 1) / perFrame} sent)" : "";
            string quality = FrameQualities.Count == 0 ? "" : $", quality {FrameQualities.Min():0} to {FrameQualities.Max():0} dB";
            return $"{FrameQualities.Count} frames heard{sent}, {BlocksGood} of {Blocks} blocks decoded{quality}";
        }
    }
}

public sealed class FmSignalLiveReceiver
{
    private readonly object _lock = new();
    private SubTones _tones = new(Rate);
    private FmPieceCollector _collector = new();

    // The last few seconds of sound: _sound[0] is sample number _soundStart, counting from the start.
    private float[] _sound = new float[4 * FrameLength];
    private long _soundStart;
    private int _soundCount;
    private long End => _soundStart + _soundCount;
    private double _power = 1e-6;                    // the sound's average power, slowly followed

    // The frame finder: the match at position _at, as running sums.
    private long _at;
    private double _p, _e;
    private bool _sumsReady;
    private long _bestAt = -1;
    private double _best;
    private readonly List<long> _frames = new();     // frames found, waiting for all their sound
    private long _lastFound = long.MinValue / 2;
    private long _lastDecodedEnd = long.MinValue / 2;
    private bool _ended = true;                      // no transmission going on

    /// <summary>A file received complete (on the audio thread).</summary>
    public event Action<FmReceivedFile, double?>? FileReceived;

    /// <summary>Another station asked for missing pieces of a file (on the audio thread).</summary>
    public event Action<FmRequest>? RequestHeard;

    /// <summary>A transmission ended: how each file in it came through (on the audio thread).</summary>
    public event Action<IReadOnlyList<FmTransmissionReport>>? TransmissionEnded;

    // This transmission so far: each file's pieces, details and result, and the frames' qualities.
    private sealed class HeardFile { public readonly HashSet<int> Seen = new(); public int Highest = -1; public FmFileInfo? Info; public FmReceivedFile? File; public FmSignalMode? Mode; public bool Earlier; }
    private readonly Dictionary<(int, int), HeardFile> _thisTransmission = new();
    private readonly List<double> _qualities = new();
    private int _blocksGood, _blocks;   // this transmission's, for the log

    /// <summary>A frame decoded (on the audio thread).</summary>
    public event Action<FmFrame>? FrameDecoded;

    /// <summary>A step of a level check arrived: which step, and its quality (dB).</summary>
    public event Action<FmLevelStep, double>? LevelStepHeard;

    /// <summary>A level check is over: each step heard and its quality.</summary>
    public event Action<FmLevelResult>? LevelCheckHeard;

    private readonly List<(FmLevelStep Step, double QualityDb)> _levelSteps = new();
    private long _levelLastEnd;

    private void FinishLevelCheck(List<Action> events)
    {
        if (_levelSteps.Count == 0) return;
        var result = new FmLevelResult(_levelSteps.ToList());
        _levelSteps.Clear();
        events.Add(() => LevelCheckHeard?.Invoke(result));
    }

    /// <summary>The last frame heard, while its transmission lasts; null between transmissions.</summary>
    public FmHeard? Heard { get; private set; }

    /// <summary>Why the signal can't be received, if it can't.</summary>
    public string? Problem { get; private set; }

    /// <summary>Files heard but not complete, the most recent first (for asking for missing pieces).</summary>
    public IReadOnlyList<FmUnfinishedFile> Unfinished { get { lock (_lock) return _collector.Unfinished; } }

    public void AddSamples(float[] samples, int count, int sampleRate)
    {
        var events = new List<Action>();
        lock (_lock)
        {
            if (sampleRate != Rate)
            {
                Problem = $"RFpix's FM signal needs 48000 Hz sound (this is {sampleRate} Hz).";
                return;
            }
            Problem = null;
            MakeRoom(count);
            for (int i = 0; i < count; i++)
            {
                float s = (float)_tones.Process(samples[i]);
                _sound[_soundCount++] = s;
                _power += (s * s - _power) * 1e-5;
            }
            Find(events);
            DecodeReady(events, atEnd: false);
            CheckEnded(events);
        }
        foreach (var e in events) e();
    }

    /// <summary>The sound has ended (a recording finished): decode what's there.</summary>
    public void EndOfAudio()
    {
        var events = new List<Action>();
        lock (_lock)
        {
            DecodeReady(events, atEnd: true);
            FinishLevelCheck(events);
            EndTransmission(events);
        }
        foreach (var e in events) e();
    }

    /// <summary>Forget everything (switching between the radio and a recording).</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _levelSteps.Clear();
            _tones = new SubTones(Rate);
            _collector = new FmPieceCollector();
            _soundStart = 0; _soundCount = 0; _power = 1e-6;
            _at = 0; _sumsReady = false; _bestAt = -1; _best = 0;
            _frames.Clear();
            _lastFound = _lastDecodedEnd = long.MinValue / 2;
            _ended = true;
            _thisTransmission.Clear();
            _qualities.Clear();
            Heard = null;
        }
    }

    // ---- finding frames ----

    private void Find(List<Action> events)
    {
        int lag = SymbolLength, window = SymbolLength;
        while (_at + window + lag < End)
        {
            if (!_sumsReady)
            {
                _p = _e = 0;
                for (int i = 0; i < window; i++)
                {
                    float a = At(_at + i), b = At(_at + i + lag);
                    _p += a * b;
                    _e += (a * a + b * b) / 2.0;
                }
                _sumsReady = true;
            }
            double quiet = 1e-12 + _power * 1e-3 * window;
            double match = _e > quiet ? _p / _e : 0;
            if (match >= FmSignalReceiver.FindThreshold && match > _best) { _best = match; _bestAt = _at; }
            if (_bestAt >= 0 && _at - _bestAt > SymbolLength)
            {
                long found = _bestAt;
                _bestAt = -1; _best = 0;
                var (start, score) = FmSignalReceiver.Pin(_sound, (int)(found - _soundStart));
                long absolute = start + _soundStart;
                if (score > FmSignalReceiver.CheckThreshold && absolute - _lastFound > FrameLength - 2 * SymbolLength)
                {
                    _frames.Add(absolute);
                    _lastFound = absolute;
                    // The next frame can't start for most of a frame: skip ahead.
                    _at = Math.Max(_at + 1, absolute + FrameLength - 2 * SymbolLength);
                    _sumsReady = false;
                    continue;
                }
            }
            // Slide the sums along one sample.
            {
                float a = At(_at), b = At(_at + lag), c = At(_at + window), d = At(_at + window + lag);
                _p += c * d - a * b;
                _e += (c * c + d * d - a * a - b * b) / 2.0;
                _at++;
            }
        }
    }

    // ---- decoding frames ----

    private void DecodeReady(List<Action> events, bool atEnd)
    {
        while (_frames.Count > 0 && (End >= _frames[0] + FrameLength || atEnd))
        {
            long start = _frames[0];
            _frames.RemoveAt(0);
            var frameSound = new float[FrameLength];
            for (int i = 0; i < FrameLength; i++)
            {
                long n = start + i;
                if (n >= _soundStart && n < End) frameSound[i] = _sound[n - _soundStart];
            }
            var (frame, blocks) = FmSignalReceiver.DecodeFrame(frameSound, 0);
            frame = frame with { Start = (int)Math.Min(int.MaxValue, start) };
            events.Add(() => FrameDecoded?.Invoke(frame));
            if (frame.LevelStep is { } step && !double.IsNaN(frame.SnrDb))
            {
                // A level check: its steps come one after another; a step numbered no higher than the last
                // starts a new one.
                if (_levelSteps.Count > 0 && step.Step <= _levelSteps[^1].Step.Step) FinishLevelCheck(events);
                double q = frame.SnrDb;
                _levelSteps.Add((step, q));
                _levelLastEnd = start + FrameLength;
                events.Add(() => LevelStepHeard?.Invoke(step, q));
                if (step.Step == FmLevelCheck.Steps - 1) FinishLevelCheck(events);
                continue;
            }
            if (frame.Mode is null) continue;
            // A request for missing pieces (the same in every block of its frame): passed on once.
            if (blocks.Select(FmRequest.From).FirstOrDefault(r => r is not null) is { } request)
            {
                events.Add(() => RequestHeard?.Invoke(request));
                continue;
            }
            _lastDecodedEnd = start + FrameLength;
            _ended = false;
            var now = DateTime.Now;
            double quality = frame.SnrDb;
            _qualities.Add(quality);
            _blocksGood += frame.Good;
            _blocks += frame.Blocks;
            foreach (var block in blocks)
            {
                var key = (block.FileNumber, block.SourcePieces);
                if (!_thisTransmission.TryGetValue(key, out var heard)) _thisTransmission[key] = heard = new HeardFile();
                heard.Mode = frame.Mode;
                if (block.IsInfo) heard.Info ??= FmFileInfo.FromPayload(block.Payload);
                else { heard.Seen.Add(block.Number); heard.Highest = Math.Max(heard.Highest, block.Number); }
                if (_collector.Add(block, frame.Mode, now) is { } file)
                {
                    events.Add(() => FileReceived?.Invoke(file, quality));
                    var done = _thisTransmission.FirstOrDefault(h => h.Key.Item1 == file.FileNumber && h.Key.Item2 == file.SourcePieces).Value;
                    if (done is not null) done.File = file;
                }
                else if (heard.File is null && _collector.Earlier(block) is { } earlier)
                {
                    // Received complete before (sent again, or the same file from another station: every station's
                    // test 17 is alike, Nov 27, 2026): this transmission counts as complete too, not "not complete".
                    heard.File = earlier;
                    heard.Earlier = true;
                }
            }
            var current = _collector.Current;
            string? call = current?.Callsign ?? Heard?.Callsign;
            Heard = new FmHeard(frame.Mode, frame.SnrDb, frame.Good, frame.Blocks, current, call);
        }
    }

    /// <summary>No frame for two frames' time: the transmission is over.</summary>
    private void CheckEnded(List<Action> events)
    {
        if (_levelSteps.Count > 0 && _frames.Count == 0 && End - _levelLastEnd > 2L * FrameLength) FinishLevelCheck(events);
        if (!_ended && _frames.Count == 0 && End - _lastDecodedEnd > 2L * FrameLength) EndTransmission(events);
    }

    private void EndTransmission(List<Action> events)
    {
        _ended = true;
        double? quality = Heard?.QualityDb;
        foreach (var file in _collector.TransmissionEnded())
        {
            events.Add(() => FileReceived?.Invoke(file, quality));
            if (_thisTransmission.TryGetValue((file.FileNumber, file.SourcePieces), out var h)) h.File = file;
        }
        if (_thisTransmission.Count > 0)
        {
            var qualities = _qualities.ToList();
            var reports = _thisTransmission.Select(h => new FmTransmissionReport(DateTime.Now, h.Value.File?.Name ?? h.Value.Info?.Name,
                h.Value.File, h.Value.Seen.Order().ToList(), h.Key.Item2, h.Value.Highest + 1,
                h.Value.Info?.Callsign is { Length: > 0 } c ? c : null, h.Value.Mode, qualities, h.Value.Earlier, _blocksGood, _blocks)).ToList();
            events.Add(() => TransmissionEnded?.Invoke(reports));
        }
        _thisTransmission.Clear();
        _qualities.Clear();
        _blocksGood = _blocks = 0;
        Heard = null;
    }

    // ---- the store of sound ----

    private float At(long n) => n >= _soundStart && n < End ? _sound[n - _soundStart] : 0;

    /// <summary>Makes room for <paramref name="count"/> more samples, dropping what's no longer needed: the
    /// finder needs the last two symbols behind it, and a frame waiting to be decoded all of its own.</summary>
    private void MakeRoom(int count)
    {
        if (_soundCount + count <= _sound.Length) return;
        long keepFrom = _at - 2 * SymbolLength;
        if (_frames.Count > 0) keepFrom = Math.Min(keepFrom, _frames[0] - SymbolLength);
        keepFrom = Math.Clamp(keepFrom, _soundStart, End);
        int drop = (int)(keepFrom - _soundStart);
        if (drop > 0)
        {
            Array.Copy(_sound, drop, _sound, 0, _soundCount - drop);
            _soundStart += drop;
            _soundCount -= drop;
        }
        if (_soundCount + count > _sound.Length) Array.Resize(ref _sound, Math.Max(_sound.Length * 2, _soundCount + count));
    }
}
