// LevelRamp.cs - applies a level that can change while sound is playing.
//
// The Send page's level slider works live: move it during a transmission and the
// sound going to the radio follows within a few hundredths of a second. A sudden
// jump in level would make a click (a burst of splatter on the air), so each change
// is spread smoothly over the next chunk of sound instead.

namespace RFpix.Audio;

public sealed class LevelRamp
{
    private readonly Func<double> _target;
    private double _current;

    /// <param name="target">Read once per chunk: the level wanted now, as a multiplier (0 to 1).</param>
    public LevelRamp(Func<double> target)
    {
        _target = target;
        _current = Clamp(target());
    }

    /// <summary>The level applied at the end of the last chunk.</summary>
    public double Current => _current;

    /// <summary>Scales a chunk in place, moving smoothly from the last level to the one wanted now.</summary>
    public void Apply(float[] buffer, int offset, int count)
    {
        double target = Clamp(_target());
        if (count == 0) return;
        double step = (target - _current) / count;
        for (int i = 0; i < count; i++)
        {
            _current += step;
            buffer[offset + i] *= (float)_current;
        }
        _current = target;
    }

    private static double Clamp(double level) => double.IsFinite(level) ? Math.Clamp(level, 0, 1) : 0;
}
