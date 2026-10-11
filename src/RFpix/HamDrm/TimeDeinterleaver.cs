// TimeDeinterleaver.cs - puts a frame's picture-data cells back in order.
//
// Before sending, the sender shuffles each frame's cells (the "cell interleaver").
// With long interleaving it also spreads them over 5 frames (2 seconds): of a
// frame's cells, a fifth go out in that frame, a fifth in the next, and so on.
// A short fade then damages a little of five frames, which the error correction
// can repair, rather than all of one frame, which it can't.
// The price: a frame can only be decoded once the 4 frames after it have arrived.
// Short interleaving shuffles within the frame only (depth 1).
//
// The rule (DRM standard 7.6, QSSTV's SymbolInterleaver.cpp): cell i of sent frame n
// holds cell table[i] of the original frame n - (i mod depth).

using System.Numerics;
using RFpix.HamDrm.Coding;

namespace RFpix.HamDrm;

public sealed class TimeDeinterleaver
{
    public const int LongDepth = 5;

    private readonly int _cells;
    private readonly int _depth;
    private readonly int[] _table;
    private readonly Dictionary<long, (Complex[] Cells, double[] Weights)> _received = new();
    private long? _first;

    public TimeDeinterleaver(int cellsPerFrame, HamDrmInterleave interleave)
    {
        _cells = cellsPerFrame;
        _depth = interleave == HamDrmInterleave.Long ? LongDepth : 1;
        _table = BitInterleaver.Table(cellsPerFrame, 5);
    }

    /// <summary>Adds a received frame (its data cells in sent order, channel divided out).
    /// Returns the original frames that have now become complete (usually one). Frames that
    /// never arrive count as missing (weight 0), so the error correction treats them as unknown.</summary>
    public List<(long Frame, Complex[] Cells, double[] Weights)> Add(long frame, Complex[] cells, double[] weights)
    {
        var ready = new List<(long, Complex[], double[])>();
        _first ??= frame;
        if (_lastOut is long last && frame - (_depth - 1) <= last) return ready;   // old or repeated frame
        _received[frame] = (cells, weights);

        // Every original frame whose last part has now arrived (or been skipped over).
        long from = _lastOut is long l ? l + 1 : _first.Value;
        // After a long gap, older original frames have no parts left to rebuild them from.
        from = Math.Max(from, frame - 2 * (_depth - 1));
        for (long original = from; original <= frame - (_depth - 1); original++)
        {
            var outCells = new Complex[_cells];
            var outWeights = new double[_cells];
            for (int i = 0; i < _cells; i++)
            {
                if (!_received.TryGetValue(original + i % _depth, out var sent)) continue;   // missing
                outCells[_table[i]] = sent.Cells[i];
                outWeights[_table[i]] = sent.Weights[i];
            }
            ready.Add((original, outCells, outWeights));
            _lastOut = original;
        }

        // Forget frames no longer needed.
        foreach (var old in _received.Keys.Where(f => f <= frame - _depth).ToList()) _received.Remove(old);
        return ready;
    }

    private long? _lastOut;
}
