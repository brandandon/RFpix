// MscDecoder.cs - turns one frame's picture-data cells back into bits.
//
// The MSC (Main Service Channel) is the picture data. It's coded like the FAC,
// but split into "levels", one per bit position in each cell:
//   4-QAM:  each cell side carries 1 bit  -> 1 level
//   16-QAM: each side carries 2 bits      -> 2 levels
//   64-QAM: each side carries 3 bits      -> 3 levels
// Bits nearer a point's "coarse" position are easier to read than the "fine"
// ones, so each level gets its own code strength: strong for the hard levels,
// light for the easy ones. Protection "high" or "low" picks the set of strengths.
//
// Undoing it, for one frame, level by level ("multi-stage decoding", as QSSTV does):
//   1. For every cell, work out how likely the level's bit is 0 or 1 ("soft bits"),
//      by comparing the cell with every point it could have been.
//   2. Unshuffle those bits and Viterbi-decode them.
//   3. Code the decoded bits again, exactly as the sender did. Now this level's bit
//      is known in every cell, which rules out half the points for the next level:
//      its soft bits are worked out only among the points that are still possible.
//      The strongly protected level goes first, so later levels lean on solid ground.
//   4. Two more rounds, each level now using all the others' decoded bits.
//   5. Join the levels' bits, and undo the scrambling (energy dispersal).
// Reading the levels one at a time like this gains about 1 dB over reading each
// on its own, for 16- and 64-QAM (4-QAM has only one level).
// Ported from QSSTV's MLC.cpp and QAMMapping.cpp (Dream DRM, PA0MBO); checked bit
// for bit against QSSTV's transmitter on every recording.

using System.Numerics;
using RFpix.HamDrm.Coding;

namespace RFpix.HamDrm;

public sealed class MscDecoder
{
    // Where each point sits along one side of the cell, by its bits (first bit most significant).
    private static readonly double[] Qam4 = { 0.7071067811, -0.7071067811 };
    private static readonly double[] Qam16 = { 0.9486832980, -0.3162277660, 0.3162277660, -0.9486832980 };
    private static readonly double[] Qam64 =
        { 1.0801234497, -0.1543033499, 0.4629100498, -0.7715167498, 0.7715167498, -0.4629100498, 0.1543033499, -1.0801234497 };

    private readonly int _cells;
    private readonly int _levels;
    private readonly double[] _axis;
    private readonly int[][][] _steps;        // per level
    private readonly int[] _dataBits;         // per level
    private readonly int[]?[] _shuffle;       // per level (null = not shuffled)

    /// <summary>How many data bits one frame carries.</summary>
    public int DataBits { get; }

    /// <summary>How many whole bytes that is: the frame's data packet.</summary>
    public int PacketBytes => DataBits / 8;

    public MscDecoder(int cellsPerFrame, int qam, HamDrmProtection protection)
    {
        _cells = cellsPerFrame;
        bool high = protection == HamDrmProtection.High;
        (int[] codeRates, int?[] shuffleT0) = qam switch
        {
            // Code strength per level (an index into the puncturing patterns), from the
            // DRM standard's tables (QSSTV's iCodRateCombMSC..SM); the bit shuffle per level.
            4 => (new[] { 6 }, new int?[] { 21 }),
            16 => (high ? new[] { 2, 7 } : new[] { 4, 9 }, new int?[] { 13, 21 }),
            64 => (high ? new[] { 0, 4, 9 } : new[] { 2, 7, 10 }, new int?[] { null, 13, 21 }),
            _ => throw new ArgumentException($"HamDRM has no {qam}-QAM."),
        };
        _levels = codeRates.Length;
        _axis = qam switch { 4 => Qam4, 16 => Qam16, _ => Qam64 };

        _steps = new int[_levels][][];
        _dataBits = new int[_levels];
        _shuffle = new int[]?[_levels];
        for (int j = 0; j < _levels; j++)
        {
            _steps[j] = ConvolutionalCode.FillCells(cellsPerFrame, codeRates[j], out _dataBits[j]);
            if (shuffleT0[j] is int t0) _shuffle[j] = BitInterleaver.Table(2 * cellsPerFrame, t0);
        }
        DataBits = _dataBits.Sum();
    }

    /// <summary>Decodes one frame.</summary>
    /// <param name="cells">The frame's data cells with the channel already divided out.</param>
    /// <param name="weights">How much to trust each cell (the channel's strength squared;
    /// 0 for a cell that's missing).</param>
    /// <returns>The frame's <see cref="DataBits"/> bits.</returns>
    public byte[] Decode(IReadOnlyList<Complex> cells, IReadOnlyList<double> weights)
    {
        if (cells.Count != _cells) throw new ArgumentException($"Expected {_cells} cells, got {cells.Count}.");

        // Each cell has two sides (real, imaginary), each carrying one bit per level.
        int sides = 2 * _cells;
        var x = new double[sides];
        var w = new double[sides];
        for (int i = 0; i < _cells; i++)
        {
            x[2 * i] = cells[i].Real;
            x[2 * i + 1] = cells[i].Imaginary;
            w[2 * i] = w[2 * i + 1] = weights[i];
        }

        // known[level][side]: that level's bit in that side once decoded (0 or 1), -1 until then.
        var known = new sbyte[_levels][];
        for (int j = 0; j < _levels; j++) Array.Fill(known[j] = new sbyte[sides], (sbyte)-1);
        var decoded = new byte[_levels][];

        int rounds = _levels == 1 ? 1 : 1 + ExtraRounds;
        var soft = new double[sides];
        for (int round = 0; round < rounds; round++)
            for (int j = 0; j < _levels; j++)
            {
                // Steps 1 and 2: soft bits among the points still possible, then Viterbi.
                for (int n = 0; n < sides; n++) soft[n] = SoftBit(x[n], w[n], j, known, n);
                IReadOnlyList<double> levelSoft = _shuffle[j] is { } table ? BitInterleaver.Unshuffle(soft, table) : soft;
                decoded[j] = ConvolutionalCode.Decode(levelSoft, _steps[j], _dataBits[j]);

                // Step 3: code them again, and put each coded bit back in its side.
                if (round == rounds - 1 && j == _levels - 1) break;   // nothing left that needs it
                var coded = ConvolutionalCode.Encode(decoded[j], _steps[j]);
                for (int n = 0; n < sides; n++)
                {
                    int m = _shuffle[j] is { } t ? t[n] : n;   // the coded bit sent in side n
                    known[j][n] = m < coded.Length ? (sbyte)coded[m] : (sbyte)-1;
                }
            }

        // Step 5.
        var bits = new byte[DataBits];
        int at = 0;
        for (int j = 0; j < _levels; j++)
        {
            Array.Copy(decoded[j], 0, bits, at, decoded[j].Length);
            at += decoded[j].Length;
        }
        EnergyDispersal.Apply(bits);
        return bits;
    }

    /// <summary>Further rounds after the first, each level using all the others' decoded bits.
    /// Two gain about 2 dB over none for 64-QAM; a third adds nothing (measured Oct 13, 2026).</summary>
    public static int ExtraRounds { get; set; } = 2;

    /// <summary>For one side of a cell (value x), how likely level j's bit is 0 (positive) or 1
    /// (negative): the distance to the nearest point where that bit is 1, minus the distance to
    /// the nearest point where it's 0 (the "max-log" rule), scaled by the weight. Only points that
    /// agree with the other levels' bits already decoded for this side are considered.</summary>
    private double SoftBit(double x, double weight, int j, sbyte[][] known, int side)
    {
        double nearest0 = double.MaxValue, nearest1 = double.MaxValue;
        for (int p = 0; p < _axis.Length; p++)
        {
            if (!Possible(p, j, known, side)) continue;
            double d = (x - _axis[p]) * (x - _axis[p]);
            if (BitOf(p, j) == 0) nearest0 = Math.Min(nearest0, d);
            else nearest1 = Math.Min(nearest1, d);
        }
        return weight * (nearest1 - nearest0);
    }

    // Point p's bit for level j (level 0 is the most significant bit of the point's number).
    private int BitOf(int p, int j) => (p >> (_levels - 1 - j)) & 1;

    private bool Possible(int p, int j, sbyte[][] known, int side)
    {
        for (int l = 0; l < _levels; l++)
            if (l != j && known[l][side] >= 0 && BitOf(p, l) != known[l][side]) return false;
        return true;
    }

    /// <summary>Packs bits (first bit most significant) into bytes, dropping any leftover bits.</summary>
    public static byte[] ToBytes(byte[] bits)
    {
        var bytes = new byte[bits.Length / 8];
        for (int i = 0; i < bytes.Length; i++)
            for (int b = 0; b < 8; b++) bytes[i] = (byte)((bytes[i] << 1) | bits[8 * i + b]);
        return bytes;
    }
}
