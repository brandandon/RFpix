// TimeInterleaver.cs - shuffles each frame's picture-data cells before sending: the
// reverse of TimeDeinterleaver (see there for why).
//
// The rule (DRM standard 7.6, QSSTV's SymbolInterleaver.cpp): cell i of sent frame n
// holds cell table[i] of the original frame n - (i mod depth). Before the first frame
// there's nothing to take, so the first few frames of a long-interleaved transmission
// are partly zeros, as in QSSTV.

using System.Numerics;
using RFpix.HamDrm.Coding;

namespace RFpix.HamDrm;

public sealed class TimeInterleaver
{
    private readonly int _cells;
    private readonly int _depth;
    private readonly int[] _table;
    private readonly Complex[][] _recent;   // the last frames, newest first
    
    public TimeInterleaver(int cellsPerFrame, HamDrmInterleave interleave)
    {
        _cells = cellsPerFrame;
        _depth = interleave == HamDrmInterleave.Long ? TimeDeinterleaver.LongDepth : 1;
        _table = BitInterleaver.Table(cellsPerFrame, 5);
        _recent = new Complex[_depth][];
        for (int d = 0; d < _depth; d++) _recent[d] = new Complex[cellsPerFrame];
    }

    /// <summary>Takes the next original frame's cells; returns the cells to send in this frame.</summary>
    public Complex[] Add(Complex[] cells)
    {
        if (cells.Length != _cells) throw new ArgumentException($"Expected {_cells} cells, got {cells.Length}.");
        for (int d = _depth - 1; d > 0; d--) _recent[d] = _recent[d - 1];
        _recent[0] = cells;
        var sent = new Complex[_cells];
        for (int i = 0; i < _cells; i++) sent[i] = _recent[i % _depth][_table[i]];
        return sent;
    }
}
