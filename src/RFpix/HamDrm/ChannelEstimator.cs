// ChannelEstimator.cs - works out how the radio path changed every cell.
//
// On its way from the sender, each carrier comes out stronger or weaker and
// turned by some angle (filters, the radio, the ionosphere, timing). That change,
// the "channel", has to be undone before a cell can be read. Pilot cells show it
// directly: divide what arrived by the known value that was sent.
//
// Pilots only cover some cells, so the rest are filled in by drawing straight
// lines between them ("linear interpolation"), first along each carrier from
// symbol to symbol, then across the carriers within each symbol. The channel
// changes smoothly in both directions, so straight lines between nearby pilots
// are close enough.

using System.Numerics;

namespace RFpix.HamDrm;

public static class ChannelEstimator
{
    /// <summary>The channel for every cell: H[symbol][k]. A received cell divided by
    /// its H gives back (roughly) what was sent.</summary>
    /// <param name="symbols">The carriers read from the window.</param>
    /// <param name="firstFrameSymbol">Which symbol starts a frame (from <see cref="FrameSync"/>).</param>
    /// <param name="layout">Where the pilots are, for this mode and bandwidth.</param>
    public static Complex[][] Estimate(OfdmSymbols symbols, int firstFrameSymbol, CellLayout layout)
    {
        int count = symbols.Count;
        int first = layout.FirstCarrier, last = layout.LastCarrier;
        int perFrame = layout.SymbolsPerFrame;

        var h = new Complex[count][];
        var known = new bool[count, last + 1];
        for (int s = 0; s < count; s++)
        {
            h[s] = new Complex[last + 1];
            int frameSymbol = ((s - firstFrameSymbol) % perFrame + perFrame) % perFrame;
            for (int k = first; k <= last; k++)
            {
                if (!layout.IsPilot(frameSymbol, k)) continue;
                h[s][k] = symbols.Cells[s][k] / layout.PilotValue(frameSymbol, k);
                known[s, k] = true;
            }
        }

        // Step 1: along each carrier, from symbol to symbol.
        var carrierHasPilots = new bool[last + 1];
        var points = new List<int>();
        for (int k = first; k <= last; k++)
        {
            points.Clear();
            for (int s = 0; s < count; s++) if (known[s, k]) points.Add(s);
            if (points.Count == 0) continue;
            carrierHasPilots[k] = true;
            FillBetween(points, i => h[i][k], (i, v) => h[i][k] = v, count);
        }

        // Step 2: across the carriers, within each symbol.
        var carriers = new List<int>();
        for (int k = first; k <= last; k++) if (carrierHasPilots[k]) carriers.Add(k);
        for (int s = 0; s < count; s++)
        {
            var row = h[s];
            FillBetween(carriers, k => row[k], (k, v) => row[k] = v, last + 1, start: first);
        }
        return h;
    }

    /// <summary>Fills every position from start to length-1 by straight lines between the
    /// known points (in increasing order); before the first and after the last point, the
    /// nearest known value is kept.</summary>
    private static void FillBetween(List<int> points, Func<int, Complex> get, Action<int, Complex> set,
                                    int length, int start = 0)
    {
        if (points.Count == 0) return;
        for (int i = start; i < points[0]; i++) set(i, get(points[0]));
        for (int p = 0; p + 1 < points.Count; p++)
        {
            int a = points[p], b = points[p + 1];
            Complex va = get(a), vb = get(b);
            for (int i = a + 1; i < b; i++)
                set(i, va + (vb - va) * ((i - a) / (double)(b - a)));
        }
        for (int i = points[^1] + 1; i < length; i++) set(i, get(points[^1]));
    }
}
