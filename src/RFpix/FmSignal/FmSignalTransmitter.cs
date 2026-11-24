// FmSignalTransmitter.cs - turns a file into RFpix's FM signal (see FmSignalLayout.cs).
//
// The file is wrapped with the fountain code (FountainFile), cut into pieces, and each piece goes into
// one error-corrected block with a label (which file, how many pieces it needs, which piece this is)
// and a check (CRC-32). The blocks fill frames of about 3 seconds; any room left in the last frame is
// filled with more fountain repair pieces, which cost no extra time. Some blocks carry the file's
// details instead: the sender's callsign and the file's name (FmPieces.cs).

using System.Numerics;
using RFpix.Dsp;
using RFpix.HamDrm.Coding;
using static RFpix.FmSignal.FmSignalLayout;

namespace RFpix.FmSignal;

public static class FmSignalTransmitter
{
    /// <summary>The average level of the sound: the same as RFpix's HamDRM sound, so the Transmit level slider
    /// means the same for both (its peaks reach about 5 times this).</summary>
    public const double Level = 0.175;

    /// <summary>Wraps a file with the fountain code for this speed: the bytes of all the pieces, and how many
    /// pieces rebuild it.</summary>
    public static (byte[] Data, int SourcePieces) Wrap(byte[] file, string extension, FmSignalMode mode, int repairPercent) =>
        FountainFile.Wrap(file, extension, mode.PieceBytes, repairPercent);

    /// <summary>How many frames carry this many pieces (with the file's details, if sent).</summary>
    public static int Frames(int pieces, FmSignalMode mode, bool withInfo = true)
    {
        int frames = Math.Max(1, (pieces + mode.BlocksPerFrame - 1) / mode.BlocksPerFrame);
        while (frames * mode.BlocksPerFrame - (withInfo ? InfoFrames(frames) : 0) < pieces) frames++;
        return frames;
    }

    /// <summary>How many pieces the frames that carry <paramref name="pieces"/> hold, the room left in the
    /// last frame included (it's filled with more new repair pieces).</summary>
    public static int Slots(int pieces, FmSignalMode mode, bool withInfo = true)
    {
        int frames = Frames(pieces, mode, withInfo);
        return frames * mode.BlocksPerFrame - (withInfo ? InfoFrames(frames) : 0);
    }

    /// <summary>How long this many pieces take to send, in seconds (with the file's details).</summary>
    public static double Seconds(int pieces, FmSignalMode mode) => Frames(pieces, mode) * FrameSeconds;

    /// <summary>Whether frame <paramref name="frame"/> of <paramref name="frames"/> carries the file's details:
    /// the first, every fourth after it, and the last.</summary>
    public static bool HasInfo(int frame, int frames) => frame % 4 == 0 || frame == frames - 1;
    private static int InfoFrames(int frames) => Enumerable.Range(0, frames).Count(f => HasInfo(f, frames));

    /// <summary>The sound for a wrapped file (from Wrap): its pieces from <paramref name="firstPiece"/> on
    /// (0 for all; later, to answer a request for missing pieces with new repair pieces added by
    /// FountainFile.Extend). Any room left in the last frame is filled with more new repair pieces.</summary>
    /// <param name="info">The sender's callsign and the file's name, sent in some frames (null: not sent).</param>
    public static float[] Audio(byte[] data, int sourcePieces, FmSignalMode mode, ushort fileNumber = 1, FmFileInfo? info = null,
                                int firstPiece = 0)
    {
        int pieceBytes = mode.PieceBytes;
        if (data.Length % pieceBytes != 0) throw new ArgumentException("The data isn't whole pieces of this speed's size.", nameof(data));
        int total = data.Length / pieceBytes;
        int pieces = total - firstPiece;
        if (pieces <= 0) throw new ArgumentOutOfRangeException(nameof(firstPiece));
        int perFrame = mode.BlocksPerFrame;
        int frames = Frames(pieces, mode, info is not null);
        int slots = frames * perFrame - (info is not null ? InfoFrames(frames) : 0);
        if (slots > pieces) data = FountainFile.Extend(data, sourcePieces, pieceBytes, slots - pieces);
        var infoPayload = info?.ToPayload(pieceBytes);

        // What each frame's blocks carry: the details first where they go, then pieces in order.
        int next = firstPiece;
        var plan = new List<(int Number, byte[] Payload)>[frames];
        for (int f = 0; f < frames; f++)
        {
            plan[f] = new();
            if (infoPayload is not null && HasInfo(f, frames)) plan[f].Add((FmBlock.InfoNumber, infoPayload));
            while (plan[f].Count < perFrame)
            {
                plan[f].Add((next, data.AsSpan(next * pieceBytes, pieceBytes).ToArray()));
                next++;
            }
        }

        return Sound(plan, mode, fileNumber, sourcePieces);
    }

    /// <summary>The speed requests for missing pieces go at: the toughest, as the station asking may be weak.</summary>
    public static FmSignalMode RequestMode => FmSignalMode.All[0];

    /// <summary>A request for missing pieces of a file (one frame, about 3 seconds, at the toughest speed,
    /// the request in every block of it): the sender answers with that many new repair pieces.</summary>
    public static float[] RequestAudio(FmRequest request)
    {
        var mode = RequestMode;
        var block = (FmBlock.RequestNumber, request.ToPayload(mode.PieceBytes));
        var plan = new[] { Enumerable.Repeat(block, mode.BlocksPerFrame).ToList() };
        return Sound(plan, mode, (ushort)request.FileNumber, request.SourcePieces);
    }

    /// <summary>The sound of frames whose blocks are planned.</summary>
    private static float[] Sound(IReadOnlyList<List<(int Number, byte[] Payload)>> plan, FmSignalMode mode, ushort fileNumber, int sourcePieces)
    {
        int frames = plan.Count, perFrame = mode.BlocksPerFrame;
        var sound = new float[frames * FrameLength];
        var spread = Spread(mode);
        var positions = Positions(mode);
        var scrambler = Scrambler(positions.Length);
        var code = mode.Code;
        var point = new byte[8];
        for (int f = 0; f < frames; f++)
        {
            // The frame's bits: each block's bits spread over the whole frame.
            var bits = new byte[positions.Length];
            for (int b = 0; b < perFrame; b++)
            {
                var (number, payload) = plan[f][b];
                var block = code.Encode(BlockData(payload, fileNumber, sourcePieces, number, code.DataBits));
                for (int j = 0; j < block.Length; j++) bits[spread[b * Ldpc.BlockBits + j]] = block[j];
            }
            for (int i = 0; i < bits.Length; i++) bits[i] ^= scrambler[i];

            int start = f * FrameLength;
            for (int s = 0; s < SyncSymbols; s++) WriteSymbol(sound, start + s * SymbolLength, Sync);
            WriteSymbol(sound, start + SyncSymbols * SymbolLength, HeaderCells(mode, f));
            var cells = new Complex[DataSymbols][];
            for (int s = 0; s < DataSymbols; s++)
            {
                cells[s] = new Complex[FftSize];
                foreach (int k in PilotCarriers) cells[s][k] = Pilot(k, SyncSymbols + HeaderSymbols + s);
            }
            int p = 0;
            for (int s = 0; s < DataSymbols; s++)
                for (int t = 0; t < DataCarriers.Length; t++)
                {
                    int n = BitsAt(mode, t);
                    for (int b = 0; b < n; b++) point[b] = bits[p++];
                    cells[s][DataCarriers[t]] = Qam.Map(point.AsSpan(0, n));
                }
            for (int s = 0; s < DataSymbols; s++)
                WriteSymbol(sound, start + (SyncSymbols + HeaderSymbols + s) * SymbolLength, cells[s]);
        }

        // Set the level.
        double rms = Math.Sqrt(sound.Average(v => (double)v * v));
        float gain = (float)(Level / rms);
        for (int i = 0; i < sound.Length; i++) sound[i] = Math.Clamp(sound[i] * gain, -1f, 1f);
        return sound;
    }

    /// <summary>A block's data bits: the label, the piece and the check.</summary>
    private static byte[] BlockData(ReadOnlySpan<byte> piece, ushort fileNumber, int sourcePieces, int pieceNumber, int dataBits)
    {
        var bytes = new byte[dataBits / 8];
        bytes[0] = (byte)(fileNumber >> 8); bytes[1] = (byte)fileNumber;
        bytes[2] = (byte)(sourcePieces >> 8); bytes[3] = (byte)sourcePieces;
        bytes[4] = (byte)(pieceNumber >> 8); bytes[5] = (byte)pieceNumber;
        piece.CopyTo(bytes.AsSpan(6));
        uint crc = Crc32(bytes.AsSpan(0, bytes.Length - 4));
        bytes[^4] = (byte)(crc >> 24); bytes[^3] = (byte)(crc >> 16); bytes[^2] = (byte)(crc >> 8); bytes[^1] = (byte)crc;
        var bits = new byte[dataBits];
        for (int i = 0; i < dataBits; i++) bits[i] = (byte)((bytes[i / 8] >> (7 - i % 8)) & 1);
        return bits;
    }

    /// <summary>The header symbol: the speed (4 bits) and the frame number (8 bits), each bit sent 8 times
    /// on tones spread across the band, plus the pilots.</summary>
    private static Complex[] HeaderCells(FmSignalMode mode, int frame)
    {
        var cells = new Complex[FftSize];
        foreach (int k in PilotCarriers) cells[k] = Pilot(k, SyncSymbols);
        int value = mode.Id << 8 | (frame & 0xFF);
        for (int t = 0; t < DataCarriers.Length; t++)
        {
            int bit = t % HeaderBits;
            int b = t < HeaderBits * HeaderCopies ? (value >> (HeaderBits - 1 - bit)) & 1 : 0;
            cells[DataCarriers[t]] = b == 0 ? 1 : -1;
        }
        return cells;
    }

    /// <summary>Writes one symbol (tone values in, sound out), with its guard in front.</summary>
    internal static void WriteSymbol(float[] sound, int at, Complex[] cells)
    {
        var re = new double[FftSize];
        var im = new double[FftSize];
        foreach (int k in Carriers)
        {
            re[k] = cells[k].Real; im[k] = -cells[k].Imaginary;               // conjugate: inverse FFT by a forward one
            re[FftSize - k] = cells[k].Real; im[FftSize - k] = cells[k].Imaginary;
        }
        Fft.Transform(re, im);
        for (int i = 0; i < SymbolLength; i++)
            sound[at + i] = (float)(re[(i - Guard + FftSize) % FftSize] / FftSize);
    }
}
