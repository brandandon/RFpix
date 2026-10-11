// FrameSync.cs - finds which symbol starts each 400 ms frame.
//
// HamDRM groups its symbols into frames: 15 symbols in modes A and B, 20 in mode E.
// The first symbol of every frame carries "time reference cells": a set of
// carriers sent with fixed angles that the receiver knows in advance. In other
// symbols those carriers carry data, so their angles look random.
//
// The test for each symbol: take neighbouring pairs of those carriers, undo the
// known angles, and see whether the pair now points the same way. Neighbouring
// carriers travel the radio path almost identically, so in the frame's first
// symbol they agree; elsewhere they don't. Adding that up over every frame in
// the window makes the first symbol stand out clearly.
//
// QSSTV does the same (getsymbolidx.cpp, by PA0MBO, from Andreas Dittrich's diorama).

using System.Numerics;

namespace RFpix.HamDrm;

/// <param name="FirstSymbol">Index (in the <see cref="OfdmSymbols"/>) of the first symbol that starts a frame.</param>
/// <param name="FrameStart">Where that frame starts in the window, in 12 kHz samples.</param>
/// <param name="Clarity">How much the frame's first symbol stood out: its score divided by the
/// next-best symbol's. 1 means no clear winner; clean signals give about 3 to 5.</param>
public sealed record FrameSyncResult(int FirstSymbol, int FrameStart, double Clarity)
{
    /// <summary>Below this the frame start isn't trustworthy.</summary>
    public const double MinimumClarity = 1.3;

    public bool IsFound => Clarity >= MinimumClarity;
}

public static class FrameSync
{
    public static FrameSyncResult? Find(OfdmSymbols symbols)
    {
        var mode = symbols.Mode;
        int perFrame = HamDrmModes.SymbolsPerFrame(mode);
        if (symbols.Count < perFrame) return null;   // need at least one whole frame

        var (carriers, angles) = HamDrmModes.TimeReferenceCells(mode);
        // Turning by -angle undoes each cell's known angle.
        var undo = angles.Select(a => Complex.FromPolarCoordinates(1, -2 * Math.PI * a / 1024)).ToArray();

        // Score each place within a frame, averaged over every frame in the window.
        // (Averaged, not added up: a window holds about two frames, so some places
        // appear twice and others once, and a once-only place mustn't lose for that.)
        var score = new double[perFrame];
        var seen = new int[perFrame];
        for (int s = 0; s < symbols.Count; s++)
        {
            var cells = symbols.Cells[s];
            double agreement = 0;
            for (int i = 0; i + 1 < carriers.Length; i++)
            {
                if (carriers[i + 1] != carriers[i] + 1) continue;   // only next-door neighbours
                Complex a = cells[carriers[i]] * undo[i];
                Complex b = cells[carriers[i + 1]] * undo[i + 1];
                agreement += (a * Complex.Conjugate(b)).Real;      // large when they point the same way
            }
            score[s % perFrame] += Math.Abs(agreement);
            seen[s % perFrame]++;
        }
        for (int p = 0; p < perFrame; p++) score[p] /= seen[p];

        int best = 0;
        for (int p = 1; p < perFrame; p++) if (score[p] > score[best]) best = p;
        double nextBest = score.Where((_, p) => p != best).Max();
        double clarity = nextBest > 0 ? score[best] / nextBest : 1;

        return new FrameSyncResult(best, symbols.Starts[best], clarity);
    }
}
