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

    /// <summary>Hz between neighbouring carriers: 41.7 (A), 46.9 (B) or 75 (E).</summary>
    public static double CarrierSpacing(HamDrmMode mode) => 12000.0 / UsefulLength(mode);

    /// <summary>Symbols in one 400 ms frame.</summary>
    public static int SymbolsPerFrame(HamDrmMode mode) => mode == HamDrmMode.E ? 20 : 15;

    // ---- carriers ----
    // Carrier number k sits at 350 Hz + k x the carrier spacing (QSSTV's layout).

    /// <summary>Audio frequency of carrier number k, in Hz, as sent.</summary>
    public static double CarrierFrequency(HamDrmMode mode, int k) => 350 + k * CarrierSpacing(mode);

    /// <summary>Lowest carrier number used.</summary>
    public static int FirstCarrier(HamDrmMode mode) => mode == HamDrmMode.A ? 2 : 1;

    /// <summary>Highest carrier number used at 2.5 kHz bandwidth (2.2 kHz stops a few sooner).</summary>
    public static int LastCarrier(HamDrmMode mode) => mode switch
    {
        HamDrmMode.A => 58,   // 2.2 kHz: 54
        HamDrmMode.B => 51,   // 2.2 kHz: 45
        _ => 31,              // 2.2 kHz: 29
    };

    /// <summary>The three steady pilot carriers (always at 725, 1475 and 1850 Hz).</summary>
    public static int[] PilotCarriers(HamDrmMode mode) => mode switch
    {
        HamDrmMode.A => new[] { 9, 27, 36 },
        HamDrmMode.B => new[] { 8, 24, 32 },
        _ => new[] { 5, 15, 20 },
    };

    /// <summary>The "time reference cells": carriers sent with fixed, known phase angles in
    /// the first symbol of every frame, so a receiver can find where frames start.
    /// Angles are in 1024ths of a full turn. From QSSTV's drm.cpp tables.</summary>
    public static (int[] Carriers, int[] Angles1024) TimeReferenceCells(HamDrmMode mode) => mode switch
    {
        HamDrmMode.A => (new[] { 6, 7, 11, 12, 15, 16, 23, 29, 30, 33, 34, 38, 39, 41, 45, 46 },
                         new[] { 973, 205, 717, 264, 357, 357, 952, 440, 856, 88, 88, 68, 836, 836, 836, 1008 }),
        HamDrmMode.B => (new[] { 6, 10, 11, 14, 17, 18, 27, 28, 30, 33, 34, 38, 40, 41, 44 },
                         new[] { 304, 331, 108, 620, 192, 704, 44, 432, 588, 844, 651, 651, 651, 460, 950 }),
        _ => (new[] { 7, 8, 13, 14, 21, 22, 26, 27 },
              new[] { 432, 331, 108, 620, 192, 704, 44, 304 }),
    };
}
