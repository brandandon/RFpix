// HamDrmReceiver.cs - the HamDRM decoder, fed with audio from the audio layer.
//
// So far (milestone 1, decoder stages 1 to 3) it finds a HamDRM signal, names
// its mode, measures how far off frequency it is, reads every carrier in every
// symbol, finds where each 400 ms frame starts, and measures the difference
// between the two sound cards' clocks. Later stages read the data and the image.
//
//   audio (48 kHz) -> Downconverter (12 kHz complex) -> ModeDetector -> FrequencyLock
//                  -> OfdmSymbols -> FrameSync + SampleClock -> Status

using System.Numerics;
using RFpix.Dsp;

namespace RFpix.HamDrm;

public sealed class HamDrmReceiver
{
    private Downconverter? _downconverter;
    private readonly ModeDetector _detector = new();
    private readonly List<Complex> _converted = new();
    private int _sampleRate;

    public HamDrmReceiver()
    {
        // After every mode decision, work through the same stretch of sound.
        // (Subscribed here, first, so it's all ready before anyone else hears about the decision.)
        _detector.Decided += OnDecided;
    }

    private void OnDecided(ModeDetection detection)
    {
        Lock = FrequencyLock.Measure(_detector.Window, detection);
        if (detection.Mode is not { } mode || Lock is not { IsLocked: true } locked)
        {
            Symbols = null;
            Frame = null;
            FrameStartTime = null;
            if (detection.Mode is null)
            {
                // Signal gone: start afresh next time.
                _clock.Reset();
                _recentFrameStarts.Clear();
            }
            return;
        }

        Symbols = OfdmSymbols.Read(_detector.Window, mode, detection.SymbolStart, locked.OffsetHz);

        Frame = FrameSync.Find(Symbols);
        FrameStartTime = null;
        if (Frame is { IsFound: true } frame && _downconverter is not null)
        {
            // Turn "sample N of this window" into "seconds since the audio began".
            long windowStart = _detector.SamplesSeen - ModeDetector.WindowLength;
            int step = _downconverter.InputRate / Downconverter.OutputRate;
            double start = (windowStart + frame.FrameStart) / (double)Downconverter.OutputRate
                         + (step - 1.0) / _downconverter.InputRate - _downconverter.Delay;

            // Frames come like clockwork, every 0.4 s. In heavy noise the frame finder
            // occasionally picks the wrong symbol, so only trust a frame start that
            // agrees with most of the recent ones.
            _recentFrameStarts.Enqueue(start);
            while (_recentFrameStarts.Count > FrameVotes) _recentFrameStarts.Dequeue();
            int agreeing = _recentFrameStarts.Count(t => FrameTimingAgrees(t, start));
            if (agreeing >= FrameVotesNeeded) FrameStartTime = start;
        }

        // Frame starts, followed over several seconds, reveal the clock difference.
        if (FrameStartTime is double confirmed) _clock.AddFrameStart(confirmed);
    }

    private readonly SampleClock _clock = new();

    // A frame start is trusted when at least 3 of the last 5 readings (itself included) agree.
    private const int FrameVotes = 5;
    private const int FrameVotesNeeded = 3;
    private readonly Queue<double> _recentFrameStarts = new();

    /// <summary>Do two frame starts fit the same 0.4 s rhythm, to within 2 ms?</summary>
    private static bool FrameTimingAgrees(double a, double b)
    {
        double apart = Math.IEEERemainder(a - b, SampleClock.FramePeriod);   // -0.2 to +0.2 s
        return Math.Abs(apart) < 0.002;
    }

    /// <summary>What the receiver hears right now.</summary>
    public ModeDetection Detection => _detector.Current;

    /// <summary>The latest tuning-error measurement, or null when there's no signal.</summary>
    public FrequencyLockResult? Lock { get; private set; }

    /// <summary>Every carrier in every symbol of the latest window, or null when not locked.</summary>
    public OfdmSymbols? Symbols { get; private set; }

    /// <summary>Where frames start in the latest window, or null when not locked.</summary>
    public FrameSyncResult? Frame { get; private set; }

    /// <summary>When the latest frame started, in seconds since the audio began
    /// (allowing for the downconverter's small delay). Null until found and confirmed
    /// by most of the recent readings (so it takes about half a second to appear).</summary>
    public double? FrameStartTime { get; private set; }

    /// <summary>The clock difference between the sending and receiving sound cards in ppm
    /// (positive: the receiving card runs fast), from the last several seconds of frames;
    /// null until at least 4 seconds have been heard.</summary>
    public double? ClockPpm
    {
        get
        {
            lock (_lock) return _clock.Ppm;   // the audio thread may be adding a frame start
        }
    }

    /// <summary>Each mode's latest score, for troubleshooting.</summary>
    public IReadOnlyDictionary<HamDrmMode, double> Scores => _detector.Scores;

    /// <summary>Raised about 5 times a second with the latest decision (on the audio thread).</summary>
    public event Action<ModeDetection>? Decided
    {
        add => _detector.Decided += value;
        remove => _detector.Decided -= value;
    }

    /// <summary>Set when the audio can't be decoded at all (e.g. an unusual sample rate).</summary>
    public string? Problem { get; private set; }

    /// <summary>A one-line description for the screen,
    /// e.g. "HamDRM mode B  ·  offset +2.5 Hz  ·  clock -40 ppm  ·  strength 96%".</summary>
    public string StatusText
    {
        get
        {
            if (Problem is not null) return Problem;
            var detection = Detection;
            if (detection.Mode is not { } mode) return "No HamDRM signal";

            var lockResult = Lock;
            string offset = lockResult is { IsLocked: true }
                ? $"offset {lockResult.OffsetHz:+0.0;-0.0;0.0} Hz"
                : "finding frequency…";
            string clock = ClockPpm is double ppm ? $"  ·  clock {ppm:+0;-0;0} ppm" : "";
            return $"HamDRM mode {mode}  ·  {offset}{clock}  ·  strength {detection.Strength:P0}";
        }
    }

    /// <summary>Feed in audio exactly as it comes from the audio layer.</summary>
    public void AddSamples(float[] samples, int count, int sampleRate)
    {
        // Audio arrives on the audio thread, but Reset comes from the screen's
        // thread. The lock lets only one of them in at a time.
        lock (_lock)
        {
            if (sampleRate != _sampleRate) Restart(sampleRate);
            if (_downconverter is null) return;

            _converted.Clear();
            _downconverter.Process(samples.AsSpan(0, count), _converted);
            _detector.AddSamples(_converted);
        }
    }

    /// <summary>Forget everything heard so far, e.g. when switching between the radio and a recording.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _sampleRate = 0;
            _downconverter = null;
            _detector.Reset();
            Lock = null;
            Symbols = null;
            Frame = null;
            FrameStartTime = null;
            _clock.Reset();
            _recentFrameStarts.Clear();
            Problem = null;
        }
    }

    private readonly object _lock = new();

    private void Restart(int sampleRate)
    {
        Reset();
        _sampleRate = sampleRate;
        if (Downconverter.Supports(sampleRate))
            _downconverter = new Downconverter(sampleRate);
        else
            Problem = $"HamDRM decoding needs 48000 Hz sound; this source is {sampleRate} Hz. " +
                      "Set the radio's sound device to 48000 Hz in Windows Sound settings.";
    }
}
