// SpectrumAnalyzer.cs - turns the stream of audio into waterfall lines.
//
// Audio arrives in small chunks. This class keeps the most recent 4096 samples
// (about 85 milliseconds at 48000 Hz) and, every 2048 new samples, runs an FFT
// over them. Each FFT becomes one line of the waterfall: a list of loudness
// values (in dB), one per frequency step from 0 Hz up to the top of the display.

namespace RFpix.Dsp;

public sealed class SpectrumAnalyzer
{
    /// <summary>Samples per FFT. 4096 at 48 kHz gives about 11.7 Hz per step.</summary>
    public const int FftSize = 4096;

    private readonly int _hop;                       // new samples between lines
    private readonly float[] _recent = new float[FftSize];
    private int _writePos;                           // where the next sample goes in _recent
    private int _filled;                             // how many samples we have so far
    private int _sinceLastLine;

    private readonly double[] _window = new double[FftSize];
    private readonly double[] _re = new double[FftSize];
    private readonly double[] _im = new double[FftSize];

    /// <summary>Highest frequency shown, in Hz. HamDRM signals sit between about 300 and 2800 Hz.</summary>
    public double MaxFrequency { get; }

    /// <summary>A new line is ready: loudness in dB for each frequency step, and Hz per step.
    /// Raised on the audio thread.</summary>
    public event Action<float[], double>? LineReady;

    public SpectrumAnalyzer(double maxFrequency = 3000, int hop = 2048)
    {
        MaxFrequency = maxFrequency;
        _hop = hop;

        // A "Hann window" gently fades each slice in and out. Without it, the hard
        // edges of the slice smear energy across the whole display.
        for (int i = 0; i < FftSize; i++)
        {
            _window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftSize - 1));
        }
    }

    /// <summary>Feed in audio. Call this for every chunk from the audio layer.</summary>
    public void AddSamples(float[] samples, int count, int sampleRate)
    {
        for (int i = 0; i < count; i++)
        {
            _recent[_writePos] = samples[i];
            _writePos = (_writePos + 1) % FftSize;
            if (_filled < FftSize) _filled++;

            if (++_sinceLastLine >= _hop && _filled == FftSize)
            {
                _sinceLastLine = 0;
                MakeLine(sampleRate);
            }
        }
    }

    private void MakeLine(int sampleRate)
    {
        // Copy the recent samples, oldest first, applying the window.
        for (int i = 0; i < FftSize; i++)
        {
            _re[i] = _recent[(_writePos + i) % FftSize] * _window[i];
            _im[i] = 0;
        }

        Fft.Transform(_re, _im);

        double hzPerStep = (double)sampleRate / FftSize;
        int steps = Math.Min(FftSize / 2, (int)(MaxFrequency / hzPerStep));
        var line = new float[steps];

        for (int k = 0; k < steps; k++)
        {
            double magnitude = Math.Sqrt(_re[k] * _re[k] + _im[k] * _im[k]) / (FftSize / 4.0);
            line[k] = (float)(20 * Math.Log10(magnitude + 1e-12));
        }

        LineReady?.Invoke(line, hzPerStep);
    }
}
