// SampleClock.cs - measures how far apart the sending and receiving sound cards' clocks are.
//
// A sound card set to 48000 samples a second never runs at exactly 48000. HamDRM
// frames are sent exactly 0.4 seconds apart by the sender's clock. If the receiving
// card runs 100 parts per million (ppm) fast, it collects 100 ppm more samples per
// second, so by its own count the frames seem to arrive 0.40004 seconds apart.
//
// So we note when each frame starts, over the last several seconds, and draw the
// best straight line through "frame number" against "start time". The line's
// slope is the frame spacing as the receiver sees it, and its difference from
// 0.4 s is the clock difference. A longer stretch gives a more precise answer:
// 100 ppm builds up to 1 ms over 10 seconds, while each frame start is accurate
// to about 0.1 ms.
//
// HamDRM is sensitive to clock differences: the symbols slowly drift out of step,
// which the later decoding stages must follow.

namespace RFpix.HamDrm;

public sealed class SampleClock
{
    /// <summary>Seconds between frame starts, by the sender's clock.</summary>
    public const double FramePeriod = 0.4;

    /// <summary>Only frame starts from this many recent seconds are used.</summary>
    public const double HistorySeconds = 12;

    /// <summary>No answer until the frame starts cover at least this many seconds.</summary>
    public const double MinimumSpanSeconds = 4;

    private readonly List<double> _starts = new();

    /// <summary>Note when a frame started, in seconds since the audio began.</summary>
    public void AddFrameStart(double time)
    {
        _starts.Add(time);
        _starts.RemoveAll(t => t < time - HistorySeconds);
    }

    public void Reset() => _starts.Clear();

    /// <summary>The clock difference in ppm, positive when the receiving sound card runs fast
    /// compared with the sending one; null until there are enough frame starts.</summary>
    public double? Ppm
    {
        get
        {
            if (_starts.Count < 10 || _starts[^1] - _starts[0] < MinimumSpanSeconds) return null;

            // Number each start by its frame, counting from the first one kept.
            // (Several decisions see the same frame; that's fine, it just counts more.)
            double first = _starts[0];
            var points = _starts.Select(t => (Frame: Math.Round((t - first) / FramePeriod), Time: t)).ToList();

            // Fit the line, then fit again without any start more than 1 ms off it
            // (a rare misread symbol would otherwise bend the line).
            var (slope, intercept) = FitLine(points);
            var good = points.Where(p => Math.Abs(p.Time - (intercept + slope * p.Frame)) < 0.001).ToList();
            if (good.Count < 10) return null;
            (slope, _) = FitLine(good);

            return (slope / FramePeriod - 1) * 1e6;
        }
    }

    /// <summary>The straight line Time = intercept + slope x Frame that fits the points best
    /// ("least squares": the one with the smallest total squared miss).</summary>
    private static (double Slope, double Intercept) FitLine(List<(double Frame, double Time)> points)
    {
        double meanX = points.Average(p => p.Frame), meanY = points.Average(p => p.Time);
        double num = 0, den = 0;
        foreach (var (x, y) in points)
        {
            num += (x - meanX) * (y - meanY);
            den += (x - meanX) * (x - meanX);
        }
        double slope = den > 0 ? num / den : FramePeriod;
        return (slope, meanY - slope * meanX);
    }
}
