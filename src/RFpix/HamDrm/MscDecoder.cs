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
// Undoing it, for one frame:
//   1. For every cell, work out how likely each of its bits is 0 or 1
//      ("soft bits"), by comparing it with every point it could have been.
//   2. For each level: unshuffle the bits, then Viterbi-decode them.
//   3. Join the levels' bits, and undo the scrambling (energy dispersal).
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

        // Step 1: soft bits for every level.
        var soft = new double[_levels][];
        for (int j = 0; j < _levels; j++) soft[j] = new double[2 * _cells];
        var bitScores = new double[_levels];
        for (int i = 0; i < _cells; i++)
        {
            SoftBits(cells[i].Real, weights[i], bitScores);
            for (int j = 0; j < _levels; j++) soft[j][2 * i] = bitScores[j];
            SoftBits(cells[i].Imaginary, weights[i], bitScores);
            for (int j = 0; j < _levels; j++) soft[j][2 * i + 1] = bitScores[j];
        }

        // Step 2: each level separately.
        var bits = new byte[DataBits];
        int at = 0;
        for (int j = 0; j < _levels; j++)
        {
            IReadOnlyList<double> levelSoft = _shuffle[j] is { } table ? BitInterleaver.Unshuffle(soft[j], table) : soft[j];
            var decoded = ConvolutionalCode.Decode(levelSoft, _steps[j], _dataBits[j]);
            Array.Copy(decoded, 0, bits, at, decoded.Length);
            at += decoded.Length;
        }

        // Step 3.
        EnergyDispersal.Apply(bits);
        return bits;
    }

    /// <summary>For one side of a cell (value x), how likely each bit is 0 (positive) or 1
    /// (negative). For each bit: the distance to the nearest point where that bit is 1, minus
    /// the distance to the nearest point where it's 0 (the "max-log" rule), scaled by the weight.</summary>
    private void SoftBits(double x, double weight, double[] scores)
    {
        for (int j = 0; j < _levels; j++)
        {
            double nearest0 = double.MaxValue, nearest1 = double.MaxValue;
            for (int p = 0; p < _axis.Length; p++)
            {
                double d = (x - _axis[p]) * (x - _axis[p]);
                if (((p >> (_levels - 1 - j)) & 1) == 0) nearest0 = Math.Min(nearest0, d);
                else nearest1 = Math.Min(nearest1, d);
            }
            scores[j] = weight * (nearest1 - nearest0);
        }
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
