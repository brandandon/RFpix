// CellLayout.cs - what every cell in a HamDRM frame is for.
//
// A frame is a grid: symbols down the side (15 or 20), carriers across the top.
// Each square ("cell") has one job:
//   - Pilot cells hold values the receiver knows in advance. Comparing what
//     arrives with what was sent shows how the radio path changed each carrier
//     (its strength and angle), so the other cells can be corrected.
//       * gain pilots: scattered across the grid in a diagonal pattern
//       * frequency pilots: the three steady tones at 725, 1475 and 1850 Hz
//       * time pilots: in the first symbol of each frame (used by FrameSync)
//   - FAC cells (45 per frame) carry the transmission's settings.
//   - Data cells carry the picture.
// Every frame of a transmission has the same layout.
//
// This is a port of QSSTV's CellMappingTable.cpp (from the Dream DRM project,
// adapted for ham use by PA0MBO). A test checks every cell against QSSTV's own
// table (tests/reference/qsstv-cell-layouts.txt).

using System.Numerics;

namespace RFpix.HamDrm;

/// <summary>The two HamDRM bandwidths.</summary>
public enum HamDrmBandwidth
{
    /// <summary>2.2 kHz: a few carriers fewer, for narrow filters.</summary>
    Narrow,
    /// <summary>2.5 kHz: the usual choice.</summary>
    Wide,
}

[Flags]
public enum CellKind
{
    /// <summary>Not a carrier this mode and bandwidth use.</summary>
    Unused = 0,
    Data = 1,
    Fac = 2,
    TimePilot = 4,
    FrequencyPilot = 8,
    GainPilot = 16,
    /// <summary>Added to GainPilot for the stronger pilots at the band edges.</summary>
    Boosted = 32,
}

public sealed class CellLayout
{
    public HamDrmMode Mode { get; }
    public HamDrmBandwidth Bandwidth { get; }
    public int SymbolsPerFrame { get; }
    public int FirstCarrier { get; }
    public int LastCarrier { get; }

    /// <summary>The 45 FAC cells as (symbol, carrier), in the order the FAC is written into them.</summary>
    public IReadOnlyList<(int Symbol, int Carrier)> FacCells { get; }

    /// <summary>How many picture-data cells one frame holds.</summary>
    public int DataCellsPerFrame => DataCells.Count;

    /// <summary>The picture-data cells as (symbol, carrier), in the order the data is written
    /// into them: symbol by symbol, low carrier to high.</summary>
    public IReadOnlyList<(int Symbol, int Carrier)> DataCells { get; }

    /// <summary>Gain pilots come back to the same carrier every this many symbols (5, 3 or 4).</summary>
    public int PilotSymbolSpacing { get; }

    private readonly CellKind[,] _kinds;      // [symbol, carrier number]
    private readonly Complex[,] _pilots;      // the value sent in each pilot cell

    public CellKind Kind(int symbol, int carrier) =>
        carrier < FirstCarrier || carrier > LastCarrier ? CellKind.Unused : _kinds[symbol, carrier];

    public bool IsPilot(int symbol, int carrier) =>
        (Kind(symbol, carrier) & (CellKind.TimePilot | CellKind.FrequencyPilot | CellKind.GainPilot)) != 0;

    /// <summary>The value sent in a pilot cell (zero for other cells).</summary>
    public Complex PilotValue(int symbol, int carrier) =>
        carrier < FirstCarrier || carrier > LastCarrier ? Complex.Zero : _pilots[symbol, carrier];

    // ---- one layout per mode and bandwidth, built the first time it's asked for ----

    private static readonly Dictionary<(HamDrmMode, HamDrmBandwidth), CellLayout> Cache = new();

    public static CellLayout For(HamDrmMode mode, HamDrmBandwidth bandwidth)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue((mode, bandwidth), out var layout))
                Cache[(mode, bandwidth)] = layout = new CellLayout(mode, bandwidth);
            return layout;
        }
    }

    private CellLayout(HamDrmMode mode, HamDrmBandwidth bandwidth)
    {
        Mode = mode;
        Bandwidth = bandwidth;
        SymbolsPerFrame = HamDrmModes.SymbolsPerFrame(mode);
        FirstCarrier = HamDrmModes.FirstCarrier(mode);
        LastCarrier = HamDrmModes.LastCarrier(mode, bandwidth);
        var t = Tables.For(mode);

        _kinds = new CellKind[SymbolsPerFrame, LastCarrier + 1];
        _pilots = new Complex[SymbolsPerFrame, LastCarrier + 1];
        var facSet = new HashSet<(int, int)>(t.Fac);
        var (timeCarriers, timeAngles) = HamDrmModes.TimeReferenceCells(mode);
        int[] boosted = bandwidth == HamDrmBandwidth.Narrow ? t.BoostedNarrow : t.BoostedWide;
        int x = t.X, y = t.Y, k0 = t.K0;
        PilotSymbolSpacing = y;
        int firstGain = (int)(x / 2.0 + 0.5);   // half the spacing, rounded up

        for (int s = 0; s < SymbolsPerFrame; s++)
        {
            // Gain pilots sit at k = firstGain + x*(s mod y) + x*y*p, for whole numbers p.
            // Start p just low enough to reach the first carrier.
            int p = (int)((FirstCarrier - firstGain - x * Mod(s, y)) / (double)(x * y));

            for (int k = FirstCarrier; k <= LastCarrier; k++)
            {
                // Everything starts as picture data; later rules take some cells over.
                CellKind kind = CellKind.Data;
                Complex value = Complex.Zero;

                if (facSet.Contains((s, k))) kind = CellKind.Fac;

                if (k == firstGain + x * Mod(s, y) + x * y * p)
                {
                    p++;
                    kind = CellKind.GainPilot;
                    // The pilot's angle, by the DRM standard's formula (8.4.4.3.1):
                    // angle = 4 Z[n,m] + p W[n,m] + p^2 (1 + s) Q, in 1024ths of a turn.
                    int n = Mod(s, y), m = s / y;
                    int pp = (int)((k - k0 - n * x) / (double)(x * y));
                    int angle = Mod(4 * t.Z[n, m] + pp * t.W[n, m] + pp * pp * (1 + s) * t.Q, 1024);
                    bool isBoosted = Array.IndexOf(boosted, k) >= 0;
                    if (isBoosted) kind |= CellKind.Boosted;
                    value = Turn(isBoosted ? 2 : Math.Sqrt(2), angle);
                }

                if (s == 0)
                {
                    int i = Array.IndexOf(timeCarriers, k);
                    if (i >= 0)
                    {
                        kind = (kind & CellKind.GainPilot) != 0 ? kind | CellKind.TimePilot : CellKind.TimePilot;
                        value = Turn(Math.Sqrt(2), timeAngles[i]);
                    }
                }

                int f = Array.IndexOf(t.FreqPilotCarriers, k);
                if (f >= 0)
                {
                    kind = (kind & (CellKind.TimePilot | CellKind.GainPilot)) != 0 ? kind | CellKind.FrequencyPilot : CellKind.FrequencyPilot;
                    int angle = t.FreqPilotAngles[f];
                    // Mode E: the first two pilots are turned half a turn in odd symbols.
                    if (mode == HamDrmMode.E && f != t.FreqPilotCarriers.Length - 1 && s % 2 == 1)
                        angle = Mod(angle + 512, 1024);
                    value = Turn(Math.Sqrt(2), angle);
                }

                _kinds[s, k] = kind;
                _pilots[s, k] = value;
            }
        }

        FacCells = t.Fac.Where(c => (_kinds[c.Symbol, c.Carrier] & CellKind.Fac) != 0).ToArray();
        var dataCells = new List<(int, int)>();
        for (int s = 0; s < SymbolsPerFrame; s++)
            for (int k = FirstCarrier; k <= LastCarrier; k++)
                if (_kinds[s, k] == CellKind.Data) dataCells.Add((s, k));
        DataCells = dataCells;
    }

    /// <summary>A value of the given size, turned by angle/1024 of a full turn.</summary>
    private static Complex Turn(double size, int angle1024) =>
        Complex.FromPolarCoordinates(size, 2 * Math.PI * angle1024 / 1024);

    /// <summary>QSSTV's modulus, copied exactly (including how it treats negatives).</summary>
    private static int Mod(int a, int b) => a < 0 ? a % b + b : a % b;

    // ---- the tables, from QSSTV's TableCarMap.h ----

    private sealed record Tables(
        int X, int Y, int K0, int Q, int[,] W, int[,] Z,
        int[] BoostedNarrow, int[] BoostedWide,
        int[] FreqPilotCarriers, int[] FreqPilotAngles,
        (int Symbol, int Carrier)[] Fac)
    {
        public static Tables For(HamDrmMode mode) => mode switch
        {
            HamDrmMode.A => A,
            HamDrmMode.B => B,
            _ => E,
        };

        private static readonly Tables A = new(
            X: 4, Y: 5, K0: 2, Q: 36,
            W: new[,] { { 228, 341, 455 }, { 455, 569, 683 }, { 683, 796, 910 }, { 910, 0, 114 }, { 114, 228, 341 } },
            Z: new[,] { { 0, 81, 248 }, { 18, 106, 106 }, { 122, 116, 31 }, { 129, 129, 39 }, { 33, 32, 111 } },
            BoostedNarrow: new[] { 2, 4, 50, 54 }, BoostedWide: new[] { 2, 6, 54, 58 },
            FreqPilotCarriers: new[] { 9, 27, 36 }, FreqPilotAngles: new[] { 205, 836, 215 },
            Fac: new[] { (1,10), (1,22), (1,30), (1,50), (2,14), (2,26), (2,34), (3,18), (3,30), (3,38), (4,22), (4,34), (4,42),
                         (5,18), (5,26), (5,38), (5,46), (6,22), (6,30), (6,42), (6,50), (7,26), (7,34), (7,46), (8,10), (8,30),
                         (8,38), (8,50), (9,14), (9,34), (9,42), (10,18), (10,38), (10,46), (11,10), (11,22), (11,42), (11,50),
                         (12,14), (12,26), (12,46), (13,18), (13,30), (14,22), (14,34) });

        private static readonly Tables B = new(
            X: 2, Y: 3, K0: 1, Q: 12,
            W: new[,] { { 512, 0, 512, 0, 512 }, { 0, 512, 0, 512, 0 }, { 512, 0, 512, 0, 512 } },
            Z: new[,] { { 0, 57, 164, 64, 12 }, { 168, 255, 161, 106, 118 }, { 25, 232, 132, 233, 38 } },
            BoostedNarrow: new[] { 1, 3, 43, 45 }, BoostedWide: new[] { 1, 3, 49, 51 },
            FreqPilotCarriers: new[] { 8, 24, 32 }, FreqPilotAngles: new[] { 331, 651, 555 },
            Fac: new[] { (0,21), (1,11), (1,23), (1,35), (2,13), (2,25), (2,37), (3,15), (3,27), (3,39), (4,5), (4,17), (4,29),
                         (4,41), (5,7), (5,19), (5,31), (6,9), (6,21), (6,33), (7,11), (7,23), (7,35), (8,13), (8,25), (8,37),
                         (9,15), (9,27), (9,39), (10,5), (10,17), (10,29), (10,41), (11,7), (11,19), (11,31), (12,9), (12,21),
                         (12,33), (13,11), (13,23), (13,35), (14,13), (14,25), (14,37) });

        private static readonly Tables E = new(
            X: 1, Y: 4, K0: 1, Q: 10,
            W: new[,] { { 512, 0, 512, 0, 512 }, { 0, 512, 0, 512, 0 }, { 512, 0, 512, 0, 512 }, { 0, 512, 0, 512, 0 } },
            Z: new[,] { { 0, 57, 164, 64, 12 }, { 168, 255, 161, 106, 118 }, { 25, 232, 132, 233, 38 }, { 168, 255, 161, 106, 118 } },
            BoostedNarrow: new[] { 1, 29, 0, 0 }, BoostedWide: new[] { 1, 31, 0, 0 },
            FreqPilotCarriers: new[] { 5, 15, 20 }, FreqPilotAngles: new[] { 788, 1014, 332 },
            Fac: new[] { (1,7), (1,23), (2,8), (2,16), (2,24), (3,9), (3,17), (4,10), (4,18), (5,11), (5,19), (6,4), (6,12),
                         (7,13), (7,21), (8,6), (8,14), (8,22), (9,7), (9,23), (10,8), (10,16), (10,24), (11,9), (11,13),
                         (11,17), (12,10), (12,18), (13,11), (13,19), (14,4), (14,12), (14,16), (15,13), (15,21), (16,6),
                         (16,14), (16,22), (17,7), (17,23), (18,8), (18,16), (18,24), (19,9), (19,17) });
    }
}
