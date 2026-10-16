// MscEncoder.cs - turns one frame's picture-data bits into cells: the reverse of MscDecoder.
//
// For one frame:
//   1. Scramble the bits (energy dispersal), so long runs of 0s or 1s don't make
//      lopsided signals.
//   2. Split them into levels, one per bit position in each cell (1 for 4-QAM,
//      2 for 16-QAM, 3 for 64-QAM). The first level gets the first bits, and so on.
//   3. Error-correction code each level at its own strength, then shuffle it.
//   4. Each cell side (real, imaginary) takes one coded bit from every level; together
//      they pick one of the 2, 4 or 8 positions along that side.
// Ported from QSSTV's MLC.cpp and QAMMapping.cpp; a test checks every cell against
// QSSTV's own transmitter.

using System.Numerics;
using RFpix.HamDrm.Coding;

namespace RFpix.HamDrm;

public sealed class MscEncoder
{
    private readonly int _cells;
    private readonly int _levels;
    private readonly double[] _axis;
    private readonly int[][][] _steps;
    private readonly int[] _dataBits;
    private readonly int[]?[] _shuffle;

    /// <summary>How many data bits one frame carries (the same as <see cref="MscDecoder.DataBits"/>).</summary>
    public int DataBits { get; }

    public MscEncoder(int cellsPerFrame, int qam, HamDrmProtection protection)
    {
        _cells = cellsPerFrame;
        (int[] codeRates, int?[] shuffleT0) = MscDecoder.Levels(qam, protection);
        _levels = codeRates.Length;
        // QSSTV's point positions are stored to single precision; use exactly the same values.
        _axis = MscDecoder.Axis(qam).Select(v => (double)(float)v).ToArray();
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

    /// <summary>Encodes one frame's <see cref="DataBits"/> bits (one bit per byte) into its cells.</summary>
    public Complex[] Encode(IReadOnlyList<byte> frameBits)
    {
        if (frameBits.Count != DataBits) throw new ArgumentException($"Expected {DataBits} bits, got {frameBits.Count}.");
        var bits = frameBits.ToArray();
        EnergyDispersal.Apply(bits);                                     // step 1

        int sides = 2 * _cells;
        var point = new int[sides];   // each side's position number, built up level by level
        int at = 0;
        for (int j = 0; j < _levels; j++)
        {
            var levelBits = bits.AsSpan(at, _dataBits[j]).ToArray();      // step 2
            at += _dataBits[j];
            var coded = ConvolutionalCode.Encode(levelBits, _steps[j]);  // step 3
            for (int n = 0; n < sides; n++)
            {
                int m = _shuffle[j] is { } table ? table[n] : n;           // side n sends coded bit m
                point[n] = (point[n] << 1) | coded[m];                     // step 4: level 0 is the top bit
            }
        }

        var cells = new Complex[_cells];
        for (int i = 0; i < _cells; i++) cells[i] = new Complex(_axis[point[2 * i]], _axis[point[2 * i + 1]]);
        return cells;
    }
}
