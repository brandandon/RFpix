// FmSignalReceiver.cs - finds and decodes RFpix's FM signal in a recording (see FmSignalLayout.cs).
//
// Decode works on a whole recording at once (for tests); FmSignalLiveReceiver does the same on the live sound,
// using FindFrames and DecodeFrame. Each frame is handled on its own:
//   1. Find it: the two sync symbols are identical, so the sound matches itself one symbol later
//      ("Schmidl-Cox"); then the known sync sound pins the exact start.
//   2. Measure the path: how strong each tone arrives and its phase. A first look from the sync
//      symbols (smoothed across neighbouring tones) reads the header; then it's measured again on every symbol of the frame (an FM path
//      between fixed stations doesn't change over time, so 3 seconds of symbols pin it down closely).
//   3. Follow the sound cards' clocks drifting apart through the frame from the pilots: a drift shifts
//      every tone's phase in proportion to its frequency. Each pilot counts by how strongly it arrives.
//   4. Read the header (the speed), then turn every data tone into soft guesses for its bits, put each
//      block's bits back together, and decode it (Ldpc). A block whose check (CRC-32) passes gives one
//      fountain piece.
// Once enough pieces are in, the fountain code rebuilds the file.

using System.Numerics;
using RFpix.Dsp;
using RFpix.HamDrm.Coding;
using static RFpix.FmSignal.FmSignalLayout;

namespace RFpix.FmSignal;

/// <summary>What one frame gave.</summary>
/// <param name="Start">Where it starts in the recording, in samples.</param>
/// <param name="Mode">Its speed, if the header was read.</param>
/// <param name="Blocks">Its blocks.</param>
/// <param name="Good">Blocks that decoded with a good check.</param>
/// <param name="SnrDb">The signal-to-noise ratio measured on its tones (the average of the tones' ratios).</param>
/// <param name="LevelStep">For a level check frame (FmLevelCheck), which step it is (its Mode is then null).</param>
public sealed record FmFrame(int Start, FmSignalMode? Mode, int Blocks, int Good, double SnrDb, FmLevelStep? LevelStep = null);

/// <summary>What a recording gave.</summary>
/// <param name="Files">The files received complete.</param>
/// <param name="PiecesReceived">Pieces of the first file heard that arrived.</param>
/// <param name="PiecesNeeded">Pieces that rebuild it.</param>
public sealed record FmReception(IReadOnlyList<FmFrame> Frames, IReadOnlyList<FmReceivedFile> Files, int PiecesReceived, int PiecesNeeded)
{
    /// <summary>The first file received, if any (the bench sends one).</summary>
    public byte[]? File => Files.FirstOrDefault()?.Data;
    public string? Extension => Files.FirstOrDefault() is { } f ? Path.GetExtension(f.Name).TrimStart('.') : null;
}

public static class FmSignalReceiver
{
    public const double FindThreshold = 0.3;
    private static readonly float[] SyncSound = SyncPair();

    /// <summary>Decodes a whole recording (48 kHz).</summary>
    public static FmReception Decode(float[] sound)
    {
        // A repeater's sub-audible tone (or mains hum) in the sound would fool the frame finder (a steady
        // tone matches itself one symbol later), so notch it out first, as the HamDRM receiver does.
        var tones = new SubTones(Rate);
        sound = sound.Select(s => (float)tones.Process(s)).ToArray();

        var frames = new List<FmFrame>();
        var files = new List<FmReceivedFile>();
        var collector = new FmPieceCollector();
        var counts = new Dictionary<(int, int), HashSet<int>>();
        (int, int)? first = null;
        foreach (int start in FindFrames(sound))
        {
            var (frame, blocks) = DecodeFrame(sound, start);
            frames.Add(frame);
            foreach (var block in blocks)
            {
                var key = (block.FileNumber, block.SourcePieces);
                first ??= key;
                if (!block.IsInfo) (counts.TryGetValue(key, out var seen) ? seen : counts[key] = new()).Add(block.Number);
                if (collector.Add(block, frame.Mode, DateTime.MinValue) is { } done) files.Add(done);
            }
        }
        files.AddRange(collector.TransmissionEnded());
        int received = first is { } f && counts.TryGetValue(f, out var got) ? got.Count : 0;
        return new FmReception(frames, files, received, first?.Item2 ?? 0);
    }

    // ---- 1. finding the frames ----

    /// <summary>Where each frame starts: places where the sound matches itself one symbol later, each
    /// pinned and checked against the known sync sound (a steady tone or a voice can match itself too).</summary>
    public static List<int> FindFrames(float[] sound)
    {
        int lag = SymbolLength, window = SymbolLength;
        var starts = new List<int>();
        int last = sound.Length - window - lag;
        if (last <= 0) return starts;

        // The match at every position: sum of r[d+i]*r[d+i+lag] over i < window, against the power.
        var match = new float[last];
        double p = 0, e = 0;
        for (int i = 0; i < window; i++) { p += sound[i] * sound[i + lag]; e += (sound[i] * sound[i] + sound[i + lag] * sound[i + lag]) / 2.0; }
        double quiet = 1e-12 + sound.Average(v => (double)v * v) * 1e-3 * window;
        for (int d = 0; d < last; d++)
        {
            match[d] = e > quiet ? (float)(p / e) : 0;
            double a = sound[d], b = sound[d + lag], c = sound[d + window], f = d + window + lag < sound.Length ? sound[d + window + lag] : 0;
            p += c * f - a * b;
            e += (c * c + f * f - a * a - b * b) / 2.0;
        }

        // The best place in each stretch above the threshold (at least a symbol apart), pinned and checked.
        var found = new List<(int Start, double Score)>();
        for (int d = 0; d < last; d++)
        {
            if (match[d] < FindThreshold) continue;
            int end = d, best = d;
            while (end < last && end - best <= SymbolLength)
            {
                if (match[end] > match[best]) best = end;
                end++;
            }
            var (start, score) = Pin(sound, best);
            if (score > CheckThreshold) found.Add((start, score));
            d = end;
        }
        // Frames can't overlap: keep the better of any two too close together.
        var kept = new List<int>();
        foreach (var (start, _) in found.OrderByDescending(f => f.Score))
            if (kept.All(k => Math.Abs(k - start) > FrameLength - 2 * SymbolLength)) kept.Add(start);
        kept.Sort();
        return kept;
    }

    public const double CheckThreshold = 0.25;

    /// <summary>The exact start near a rough one: where the known sound of the two sync symbols lines up best,
    /// and how well it matches there (1 = perfectly, 0 = not at all).</summary>
    public static (int Start, double Score) Pin(float[] sound, int rough)
    {
        int best = rough;
        double bestScore = double.MinValue;
        for (int d = rough - Guard - 64; d <= rough + Guard + 64; d++)
        {
            if (d < 0 || d + SyncSound.Length > sound.Length) continue;
            double score = 0;
            for (int i = 0; i < SyncSound.Length; i += 2) score += SyncSound[i] * sound[d + i];
            if (score > bestScore) { bestScore = score; best = d; }
        }
        if (best < 0 || best + SyncSound.Length > sound.Length) return (rough, 0);
        double refPower = 0, power = 0;
        for (int i = 0; i < SyncSound.Length; i += 2) { refPower += SyncSound[i] * SyncSound[i]; power += sound[best + i] * sound[best + i]; }
        return (best, power > 0 ? bestScore / Math.Sqrt(refPower * power) : 0);
    }

    private static float[] SyncPair()
    {
        var one = Symbol(Sync);
        return one.Concat(one).ToArray();
    }

    private static float[] Symbol(Complex[] cells)
    {
        var re = new double[FftSize];
        var im = new double[FftSize];
        foreach (int k in Carriers)
        {
            re[k] = cells[k].Real; im[k] = -cells[k].Imaginary;
            re[FftSize - k] = cells[k].Real; im[FftSize - k] = cells[k].Imaginary;
        }
        Fft.Transform(re, im);
        return Enumerable.Range(0, SymbolLength).Select(i => (float)(re[(i - Guard + FftSize) % FftSize] / FftSize)).ToArray();
    }

    // ---- 2 to 4: one frame ----

    /// <summary>Decodes the frame starting at <paramref name="start"/> (from FindFrames): what it was, and its
    /// blocks that decoded with a good check.</summary>
    public static (FmFrame Frame, List<FmBlock> Blocks) DecodeFrame(float[] sound, int start)
    {
        var found = new List<FmBlock>();
        // Every symbol's tones, the FFT window starting half a guard early (into the guard), so a small
        // spread either way stays inside it.
        var y = new Complex[FrameSymbols][];
        for (int s = 0; s < FrameSymbols; s++) y[s] = Tones(sound, start + s * SymbolLength + Guard / 2);

        // 2. A first look at the path, from the two sync symbols, smoothed across neighbouring tones.
        var raw = new Complex[FftSize];
        foreach (int k in Carriers) raw[k] = (y[0][k] + y[1][k]) / (2 * Sync[k]);
        var h = new Complex[FftSize];
        Smooth(raw, h);
        var path = Track(y, h);

        // 4a. The header.
        int first = SyncSymbols;
        var headerSoft = new double[HeaderBits];
        for (int t = 0; t < HeaderBits * HeaderCopies; t++)
        {
            int k = DataCarriers[t];
            headerSoft[t % HeaderBits] += (y[first][k] * Complex.Conjugate(path(first, k))).Real;
        }
        int value = 0;
        foreach (double v in headerSoft) value = value << 1 | (v < 0 ? 1 : 0);
        if (FmLevelCheck.FromHeader(value) is { } step) return (new FmFrame(start, null, 0, 0, MeasureKnown(y, h, value), step), found);
        var mode = FmSignalMode.ById(value >> 8);
        if (mode is null) return (new FmFrame(start, null, 0, 0, double.NaN), found);

        // 2 again, closer: the path measured on every symbol of the frame (the syncs, the header and the
        // pilots are known; for the data, the nearest QAM point stands in), twice over. Smoothing across
        // tones blurs the real path too much: the radios' filters make it vary from tone to tone.
        for (int pass = 0; pass < 2; pass++)
        {
            var sent = Guess(y, path, mode, value);
            var better = new Complex[FftSize];
            foreach (int k in Carriers)
            {
                Complex top = 0;
                double bottom = 0;
                for (int s = 0; s < FrameSymbols; s++)
                {
                    var r = path(s, k) / h[k] * sent[s][k];    // what was sent, with the clocks' drift
                    top += y[s][k] * Complex.Conjugate(r);
                    bottom += (r * Complex.Conjugate(r)).Real;
                }
                better[k] = bottom > 0 ? top / bottom : h[k];
            }
            h = better;
            path = Track(y, h);
        }
        Complex Path(int s, int k) => path(s, k);

        // 4b. The data tones: noise measured on each tone from how far the points land from the nearest
        // QAM point, smoothed across neighbouring tones.
        int firstData = SyncSymbols + HeaderSymbols;
        var noise = new double[DataCarriers.Length];
        for (int t = 0; t < DataCarriers.Length; t++)
        {
            int k = DataCarriers[t], bits = BitsAt(mode, t);
            double sum = 0;
            for (int s = firstData; s < FrameSymbols; s++)
            {
                var hk = Path(s, k);
                var z = y[s][k] / hk;
                sum += Complex.Abs(z - Qam.Nearest(z, bits)) * Complex.Abs(z - Qam.Nearest(z, bits)) * hk.Magnitude * hk.Magnitude;
            }
            noise[t] = sum / DataSymbols;
        }
        var smoothNoise = new double[noise.Length];
        for (int t = 0; t < noise.Length; t++)
        {
            int from = Math.Max(0, t - 3), to = Math.Min(noise.Length - 1, t + 3);
            double avg = 0;
            for (int u = from; u <= to; u++) avg += noise[u];
            smoothNoise[t] = Math.Max(avg / (to - from + 1), 1e-12);
        }
        double snrSum = 0;
        for (int t = 0; t < DataCarriers.Length; t++)
        {
            double hp = Path(firstData, DataCarriers[t]).Magnitude;
            snrSum += hp * hp / smoothNoise[t];
        }
        double snrDb = 10 * Math.Log10(snrSum / DataCarriers.Length);

        var positions = Positions(mode);
        var soft = new double[positions.Length];
        var one = new double[8];
        int p = 0;
        for (int s = 0; s < DataSymbols; s++)
            for (int t = 0; t < DataCarriers.Length; t++)
            {
                int k = DataCarriers[t], bits = BitsAt(mode, t);
                var hk = Path(firstData + s, k);
                var z = y[firstData + s][k] / hk;
                Qam.Soft(z, bits, smoothNoise[t] / (hk.Magnitude * hk.Magnitude), one.AsSpan(0, bits));
                for (int b = 0; b < bits; b++) soft[p++] = one[b];
            }
        var scrambler = Scrambler(positions.Length);
        for (int i = 0; i < soft.Length; i++) if (scrambler[i] == 1) soft[i] = -soft[i];

        var spread = Spread(mode);
        var code = mode.Code;
        int good = 0;
        var block = new double[Ldpc.BlockBits];
        for (int b = 0; b < mode.BlocksPerFrame; b++)
        {
            for (int j = 0; j < block.Length; j++) block[j] = Math.Clamp(soft[spread[b * Ldpc.BlockBits + j]], -50, 50);
            var bitsOut = code.Decode(block);
            if (bitsOut is null) continue;
            var bytes = new byte[bitsOut.Length / 8];
            for (int i = 0; i < bitsOut.Length; i++) bytes[i / 8] |= (byte)(bitsOut[i] << (7 - i % 8));
            uint crc = (uint)(bytes[^4] << 24 | bytes[^3] << 16 | bytes[^2] << 8 | bytes[^1]);
            if (Crc32(bytes.AsSpan(0, bytes.Length - 4)) != crc) continue;
            good++;
            found.Add(new FmBlock(bytes[0] << 8 | bytes[1], bytes[2] << 8 | bytes[3], bytes[4] << 8 | bytes[5], bytes[6..^4]));
        }
        return (new FmFrame(start, mode, mode.BlocksPerFrame, good, snrDb), found);
    }

    /// <summary>3. The clocks: for each symbol after the syncs, the phase at the middle of the band and its
    /// slope across it, from the pilots, with straight lines fitted through the frame (the drift is steady).
    /// Returns the whole path: each symbol's and tone's strength and phase.</summary>
    private static Func<int, int, Complex> Track(Complex[][] y, Complex[] h)
    {
        int first = SyncSymbols, count = FrameSymbols - SyncSymbols;
        var slope = new double[count];
        var phase = new double[count];
        var gain = new double[count];
        const int middle = (FirstCarrier + LastCarrier) / 2;
        double pilotPower = Math.Max(1e-30, PilotCarriers.Average(k => (h[k] * Complex.Conjugate(h[k])).Real));
        for (int i = 0; i < count; i++)
        {
            int s = first + i;
            // Each pilot weighted by how strongly it arrives (y times the conjugate of what's expected, rather
            // than y divided by it): a pilot the path has nearly wiped out (the radios' bass cut can leave the
            // lowest tones 15-25 dB down, Dan's net repeater, Nov 20, 2026) is mostly noise and counts for little.
            // Weighted equally, such pilots threw whole frames out of line.
            var z = PilotCarriers.Select(k => y[s][k] * Complex.Conjugate(h[k] * Pilot(k, s)) / pilotPower).ToArray();
            Complex turn = 0;
            for (int j = 1; j < z.Length; j++)
            {
                int dk = PilotCarriers[j] - PilotCarriers[j - 1];
                var step = z[j] * Complex.Conjugate(z[j - 1]);
                turn += Complex.FromPolarCoordinates(step.Magnitude, step.Phase / dk);
            }
            slope[i] = turn.Phase;
            Complex mean = 0;
            for (int j = 0; j < z.Length; j++) mean += z[j] * Complex.FromPolarCoordinates(1, -slope[i] * (PilotCarriers[j] - middle));
            mean /= z.Length;
            phase[i] = mean.Phase;
            gain[i] = mean.Magnitude;
        }
        for (int i = 1; i < count; i++)   // unwrap the phase from symbol to symbol
        {
            while (phase[i] - phase[i - 1] > Math.PI) phase[i] -= 2 * Math.PI;
            while (phase[i] - phase[i - 1] < -Math.PI) phase[i] += 2 * Math.PI;
        }
        var slopeFit = Line(slope);
        var phaseFit = Line(phase);
        double g = gain.Average();
        return (s, k) => h[k] * Complex.FromPolarCoordinates(g, phaseFit(s - first) + slopeFit(s - first) * (k - middle));
    }

    /// <summary>A level check frame (FmLevelCheck): everything it sends is known, so the path is measured on
    /// all of it, and the signal-to-noise ratio from how far each tone lands from what was sent, exactly, even
    /// where the data couldn't be decoded. Worked out as for a picture's frames (the average of the tones'
    /// ratios), so the figures compare with the Quality box.</summary>
    private static double MeasureKnown(Complex[][] y, Complex[] h, int header)
    {
        var sent = FmLevelCheck.Sent(header);
        var path = Track(y, h);
        for (int pass = 0; pass < 2; pass++)
        {
            var better = new Complex[FftSize];
            foreach (int k in Carriers)
            {
                Complex top = 0;
                double bottom = 0;
                for (int s = 0; s < FrameSymbols; s++)
                {
                    var r = path(s, k) / h[k] * sent[s][k];
                    top += y[s][k] * Complex.Conjugate(r);
                    bottom += (r * Complex.Conjugate(r)).Real;
                }
                better[k] = bottom > 0 ? top / bottom : h[k];
            }
            h = better;
            path = Track(y, h);
        }
        int firstData = SyncSymbols + HeaderSymbols;
        double snrSum = 0;
        foreach (int k in DataCarriers)
        {
            double noise = 0;
            for (int s = firstData; s < FrameSymbols; s++)
            {
                var e = y[s][k] - path(s, k) * sent[s][k];
                noise += (e * Complex.Conjugate(e)).Real;
            }
            noise = Math.Max(noise / DataSymbols, 1e-12);
            double hp = path(firstData, k).Magnitude;
            snrSum += hp * hp / noise;
        }
        return 10 * Math.Log10(snrSum / DataCarriers.Length);
    }

    /// <summary>What was most likely sent on every symbol and tone: the syncs, pilots and header as known,
    /// the data as the nearest QAM point.</summary>
    private static Complex[][] Guess(Complex[][] y, Func<int, int, Complex> path, FmSignalMode mode, int header)
    {
        var sent = new Complex[FrameSymbols][];
        for (int s = 0; s < FrameSymbols; s++)
        {
            sent[s] = new Complex[FftSize];
            if (s < SyncSymbols) { foreach (int k in Carriers) sent[s][k] = Sync[k]; continue; }
            foreach (int k in PilotCarriers) sent[s][k] = Pilot(k, s);
            for (int t = 0; t < DataCarriers.Length; t++)
            {
                int k = DataCarriers[t];
                if (s == SyncSymbols)
                {
                    int bit = t < HeaderBits * HeaderCopies ? (header >> (HeaderBits - 1 - t % HeaderBits)) & 1 : 0;
                    sent[s][k] = bit == 0 ? 1 : -1;
                }
                else sent[s][k] = Qam.Nearest(y[s][k] / path(s, k), BitsAt(mode, t));
            }
        }
        return sent;
    }

    /// <summary>One symbol's tones: the FFT of the sound from <paramref name="at"/> (zeros past the end).</summary>
    private static Complex[] Tones(float[] sound, int at)
    {
        var re = new double[FftSize];
        var im = new double[FftSize];
        for (int i = 0; i < FftSize; i++)
            if (at + i >= 0 && at + i < sound.Length) re[i] = sound[at + i];
        Fft.Transform(re, im);
        var tones = new Complex[FftSize];
        foreach (int k in Carriers) tones[k] = new Complex(re[k], im[k]);
        return tones;
    }

    /// <summary>Smooths the path across neighbouring tones: for each tone, the straight line that best fits it
    /// and the two either side, taken at the tone. (A line follows a path that changes steadily across the
    /// band, as it does at the edges where the radios' filters cut in; the average with one phase slope
    /// taken out for the whole band, used before, was badly off at the bass edge on the air.)</summary>
    private static void Smooth(Complex[] raw, Complex[] smooth)
    {
        const int half = 2;
        foreach (int k in Carriers)
        {
            int from = Math.Max(FirstCarrier, k - half), to = Math.Min(LastCarrier, k + half);
            double n = 0, sx = 0, sxx = 0;
            Complex sy = 0, sxy = 0;
            for (int j = from; j <= to; j++)
            {
                double x = j - k;
                n++; sx += x; sxx += x * x; sy += raw[j]; sxy += x * raw[j];
            }
            double det = n * sxx - sx * sx;
            smooth[k] = det > 1e-9 ? (sy * sxx - sxy * sx) / det : sy / n;
        }
    }

    /// <summary>The least-squares straight line through the values (index 0, 1, 2...).</summary>
    private static Func<double, double> Line(double[] values)
    {
        int n = values.Length;
        double mx = (n - 1) / 2.0, my = values.Average(), sxy = 0, sxx = 0;
        for (int i = 0; i < n; i++) { sxy += (i - mx) * (values[i] - my); sxx += (i - mx) * (i - mx); }
        double b = sxx > 0 ? sxy / sxx : 0;
        return x => my + b * (x - mx);
    }
}
