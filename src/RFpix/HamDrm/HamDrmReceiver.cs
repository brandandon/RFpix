// HamDrmReceiver.cs - the HamDRM decoder, fed with audio from the audio layer.
//
// For now (milestone 1, decoder stage 1) it only finds a HamDRM signal and
// names its mode. Later stages add frequency lock, data recovery and the image.
//
//   audio (48 kHz) -> Downconverter (12 kHz complex) -> ModeDetector -> Status

using System.Numerics;
using RFpix.Dsp;

namespace RFpix.HamDrm;

public sealed class HamDrmReceiver
{
    private Downconverter? _downconverter;
    private readonly ModeDetector _detector = new();
    private readonly List<Complex> _converted = new();
    private int _sampleRate;

    /// <summary>What the receiver hears right now.</summary>
    public ModeDetection Detection => _detector.Current;

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

    /// <summary>A one-line description for the screen, e.g. "HamDRM mode B (strength 96%)".</summary>
    public string StatusText =>
        Problem ?? (Detection.Mode is { } mode
            ? $"HamDRM mode {mode}  (strength {Detection.Strength:P0})"
            : "No HamDRM signal");

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
