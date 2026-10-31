// SstvModes.cs - the analog SSTV modes RFpix sends and receives, and their timing.
//
// Analog SSTV sends a picture as one tone that slides in frequency: 1500 Hz is black,
// 2300 Hz is white, and everything between is a shade. Each picture line is sent as one
// or more "scans" (a colour, or brightness), separated by short fixed tones, with a
// 1200 Hz "sync" pulse that tells the receiver where each line starts. Before the picture,
// a short "VIS" code (a few tones at 1100-1300 Hz and 1900 Hz) says which mode follows,
// so receivers switch to it by themselves.
//
// The modes here are the ones in common use, with the timings in "Proposal for SSTV Mode
// Specifications" (J. L. Barber N7CXI, the Dayton paper) as MMSSTV, QSSTV and EasyPal use them:
//   Martin 1 and 2     green, blue, red scans (RGB), sync at the start of each line
//   Scottie 1, 2, DX   green, blue, red, with the sync between blue and red
//   Robot 36 and 72    brightness, then colour difference (YCrCb), 240 lines
//   PD 50 to PD 290    two lines at a time: brightness, R-Y, B-Y, brightness (YCrCb)

namespace RFpix.Analog;

public enum SstvMode { Martin1, Martin2, Scottie1, Scottie2, ScottieDX, Robot36, Robot72, PD50, PD90, PD120, PD160, PD180, PD240, PD290 }

/// <summary>How a mode carries colour.</summary>
public enum SstvColor
{
    /// <summary>Green, blue and red scans (Martin, Scottie).</summary>
    Gbr,
    /// <summary>Brightness and colour difference on alternate lines (Robot 36, 72: both on every line).</summary>
    Robot,
    /// <summary>Two lines per sync: brightness, R-Y, B-Y, brightness (PD).</summary>
    PD,
}

/// <param name="Name">For the screen, e.g. "Martin 1".</param>
/// <param name="Vis">The 7-bit VIS code that announces the mode.</param>
/// <param name="Width">Pixels across, and lines down.</param>
/// <param name="LineMs">One line's time (for PD: one sync, which carries two lines).</param>
/// <param name="ScanMs">One scan's time (Robot: the brightness scan; the colour scans are half).</param>
public sealed record SstvModeInfo(SstvMode Mode, string Name, int Vis, SstvColor Color, int Width, int Height,
                                  double LineMs, double ScanMs, double SyncMs, double PorchMs, double GapMs)
{
    /// <summary>Lines of sound (PD sends two picture lines per sync).</summary>
    public int SoundLines => Color == SstvColor.PD ? Height / 2 : Height;

    /// <summary>A Scottie mode: one extra sync before the first line, and each line's sync between blue and red.</summary>
    public bool IsScottie => Mode is SstvMode.Scottie1 or SstvMode.Scottie2 or SstvMode.ScottieDX;

    /// <summary>The picture's time on the air, without the VIS code.</summary>
    public TimeSpan PictureTime => TimeSpan.FromMilliseconds(SoundLines * LineMs + (IsScottie ? SyncMs : 0));

    public override string ToString() => Name;
}

public static class SstvModes
{
    public const double BlackHz = 1500, WhiteHz = 2300, SyncHz = 1200;

    // VIS: 1900 Hz leader, a 1200 Hz break, the leader again, then a 1200 Hz start bit,
    // 7 data bits (lowest first, 1100 Hz = 1, 1300 Hz = 0), an even parity bit, and a stop bit.
    public const double LeaderHz = 1900, VisOneHz = 1100, VisZeroHz = 1300;
    public const double LeaderMs = 300, BreakMs = 10, BitMs = 30;
    public static TimeSpan VisTime => TimeSpan.FromMilliseconds(2 * LeaderMs + BreakMs + 10 * BitMs);

    public static readonly IReadOnlyList<SstvModeInfo> All = new SstvModeInfo[]
    {
        // Martin: sync 4.862, porch 0.572, then G, B, R scans each followed by a 0.572 ms gap.
        new(SstvMode.Martin1, "Martin 1", 44, SstvColor.Gbr, 320, 256, 446.446, 146.432, 4.862, 0.572, 0.572),
        new(SstvMode.Martin2, "Martin 2", 40, SstvColor.Gbr, 320, 256, 226.798, 73.216, 4.862, 0.572, 0.572),
        // Scottie: gap, G, gap, B, sync 9, gap, R (each gap 1.5 ms at black).
        new(SstvMode.Scottie1, "Scottie 1", 60, SstvColor.Gbr, 320, 256, 428.220, 138.240, 9.0, 1.5, 1.5),
        new(SstvMode.Scottie2, "Scottie 2", 56, SstvColor.Gbr, 320, 256, 277.692, 88.064, 9.0, 1.5, 1.5),
        new(SstvMode.ScottieDX, "Scottie DX", 76, SstvColor.Gbr, 320, 256, 1050.300, 345.600, 9.0, 1.5, 1.5),
        // Robot: sync 9, porch 3, Y, a 4.5 ms separator (black before R-Y, white before B-Y), porch 1.5 at 1900, colour.
        new(SstvMode.Robot36, "Robot 36", 8, SstvColor.Robot, 320, 240, 150.0, 88.0, 9.0, 3.0, 4.5),
        new(SstvMode.Robot72, "Robot 72", 12, SstvColor.Robot, 320, 240, 300.0, 138.0, 9.0, 3.0, 4.5),
        // PD: sync 20, porch 2.08, then Y (odd line), R-Y, B-Y, Y (even line), no gaps.
        new(SstvMode.PD50, "PD 50", 93, SstvColor.PD, 320, 256, 388.16, 91.52, 20.0, 2.08, 0),
        new(SstvMode.PD90, "PD 90", 99, SstvColor.PD, 320, 256, 703.04, 170.24, 20.0, 2.08, 0),
        new(SstvMode.PD120, "PD 120", 95, SstvColor.PD, 640, 496, 508.48, 121.6, 20.0, 2.08, 0),
        new(SstvMode.PD160, "PD 160", 98, SstvColor.PD, 512, 400, 804.416, 195.584, 20.0, 2.08, 0),
        new(SstvMode.PD180, "PD 180", 96, SstvColor.PD, 640, 496, 754.24, 183.04, 20.0, 2.08, 0),
        new(SstvMode.PD240, "PD 240", 97, SstvColor.PD, 640, 496, 1000.0, 244.48, 20.0, 2.08, 0),
        new(SstvMode.PD290, "PD 290", 94, SstvColor.PD, 800, 616, 937.28, 228.8, 20.0, 2.08, 0),
    };

    public static SstvModeInfo Get(SstvMode mode) => All.First(m => m.Mode == mode);

    /// <summary>The mode a VIS code announces, or null.</summary>
    public static SstvModeInfo? FromVis(int vis) => All.FirstOrDefault(m => m.Vis == vis);

    /// <summary>A shade (0 black to 255 white) as a frequency.</summary>
    public static double Frequency(double value) => BlackHz + (WhiteHz - BlackHz) * Math.Clamp(value, 0, 255) / 255;
}
