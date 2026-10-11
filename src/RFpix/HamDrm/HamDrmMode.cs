// HamDrmMode.cs - the three HamDRM "robustness modes" and their timing.
//
// A HamDRM signal is a string of "symbols", each a short burst of sound.
// Every symbol is split into two parts:
//   - the useful part, which carries the data, and
//   - the guard, a copy of the END of the useful part, sent first.
// The guard soaks up echoes on the radio path (signals arriving a little late
// over a longer route) so they don't smear one symbol into the next.
//
// Each mode uses different lengths, which is how a receiver tells them apart.
// Lengths are counted in samples at 12 000 per second, as in QSSTV.

namespace RFpix.HamDrm;

public enum HamDrmMode { A, B, E }

public static class HamDrmModes
{
    public static readonly HamDrmMode[] All = { HamDrmMode.A, HamDrmMode.B, HamDrmMode.E };

    /// <summary>Samples in the useful part of a symbol.</summary>
    public static int UsefulLength(HamDrmMode mode) => mode switch
    {
        HamDrmMode.A => 288,   // 24 ms
        HamDrmMode.B => 256,   // 21.3 ms
        _ => 160,              // 13.3 ms (mode E)
    };

    /// <summary>Samples in the guard (the repeated piece at the start of a symbol).</summary>
    public static int GuardLength(HamDrmMode mode) => mode switch
    {
        HamDrmMode.A => 32,    // 2.7 ms: short guard, most room for data
        HamDrmMode.B => 64,    // 5.3 ms
        _ => 80,               // 6.7 ms: long guard, best on echoing paths (mode E)
    };

    /// <summary>Samples in a whole symbol (guard + useful part).</summary>
    public static int SymbolLength(HamDrmMode mode) => UsefulLength(mode) + GuardLength(mode);
}
