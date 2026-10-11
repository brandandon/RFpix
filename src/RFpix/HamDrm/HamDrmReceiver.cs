// HamDrmReceiver.cs - the HamDRM decoder, fed with audio from the audio layer.
//
// So far (milestone 1, decoder stages 1 and 2) it finds a HamDRM signal, names
// its mode, and measures how far off frequency it is. Later stages add frame
// timing, data recovery and the image.
//
//   audio (48 kHz) -> Downconverter (12 kHz complex) -> ModeDetector -> FrequencyLock -> Status

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
        // After every mode decision, measure the tuning error on the same stretch of sound.
        // (Subscribed here, first, so it's ready before anyone else hears about the decision.)
        _detector.Decided += d => Lock = FrequencyLock.Measure(_detector.Window, d);
    }

    /// <summary>What the receiver hears right now.</summary>
    public ModeDetection Detection => _detector.Current;

    /// <summary>The latest tuning-error measurement, or null when there's no signal.</summary>
    public FrequencyLockResult? Lock { get; private set; }

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
    /// e.g. "HamDRM mode B  ·  offset +2.5 Hz  ·  strength 96%".</summary>
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
            return $"HamDRM mode {mode}  ·  {offset}  ·  strength {detection.Strength:P0}";
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
