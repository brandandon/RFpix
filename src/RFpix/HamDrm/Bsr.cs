// Bsr.cs - asking for the missing pieces of a picture ("BSR"), as EasyPal and QSSTV do.
//
// When a picture arrives with pieces missing, the receiving station can send a short
// request back: a tiny file called "bsr.bin", sent as an ordinary HamDRM transmission.
// It lists which pieces didn't arrive. The sending station's program recognises the
// request as one for a picture it sent (by the picture's transport ID), and sends only
// those pieces again (a "fix"). The receiver adds them to what it already has.
//
// The bsr.bin file is plain text, one number per line (QSSTV's sourcedecoder.cpp, storeBSR):
//   the picture's transport ID
//   H_OK
//   the size of a piece in bytes
//   the missing pieces' numbers, one per line
//   -99                        (the end of the list)
//   the picture's file name    (EasyPal style only)
//   its mode, as 5 digits      (EasyPal style only; see ModeCodeOf)
// QSSTV's "compatible" style shortens runs: "5, -1, 9" means pieces 5 to 9.
// EasyPal style (the one RFpix sends, as QSSTV recommends) lists every piece.

using System.Globalization;
using System.Text;

namespace RFpix.HamDrm;

/// <summary>A request for missing pieces: which picture (by transport ID) and which pieces.</summary>
/// <param name="PieceSize">Bytes in a piece, as the requester counted them (0 if not given).</param>
/// <param name="Missing">The piece numbers asked for, in order.</param>
/// <param name="FileName">The picture's name (EasyPal style only).</param>
/// <param name="ModeCode">The transmission's settings as 5 digits (EasyPal style only).</param>
public sealed record BsrRequest(int TransportId, int PieceSize, IReadOnlyList<int> Missing,
                                string? FileName = null, int? ModeCode = null)
{
    /// <summary>The name the request goes out under.</summary>
    public const string FileNameOnAir = "bsr.bin";

    /// <summary>The request as the bsr.bin file's bytes.</summary>
    /// <param name="compact">QSSTV's "compatible" style (runs shortened, no name or mode);
    /// false for EasyPal style.</param>
    public byte[] ToBytes(bool compact = false)
    {
        if (Missing.Count == 0) throw new InvalidOperationException("There are no missing pieces to ask for.");
        var text = new StringBuilder();
        void Line(object value) => text.Append(Convert.ToString(value, CultureInfo.InvariantCulture)).Append('\n');

        Line(TransportId);
        Line("H_OK");
        Line(PieceSize);
        Line(Missing[0]);
        // As QSSTV writes it: in compact style a run of neighbours becomes "-1" then the next
        // piece that isn't a neighbour, so the run's end is rounded up to that piece.
        int previous = Missing[0];
        bool inRun = false;
        for (int i = 1; i < Missing.Count; i++)
        {
            int piece = Missing[i];
            if (compact && piece == previous + 1) inRun = true;
            else
            {
                if (inRun) { Line(-1); inRun = false; }
                Line(piece);
            }
            previous = piece;
        }
        if (inRun) { Line(-1); Line(Missing[^1]); }
        Line(-99);
        if (!compact)
        {
            Line(FileName ?? "");
            text.Append((ModeCode ?? 0).ToString("00000", CultureInfo.InvariantCulture));
        }
        return Encoding.Latin1.GetBytes(text.ToString());
    }

    /// <summary>Reads a bsr.bin file (either style), or returns null if it isn't one.
    /// Follows QSSTV's reader (drmtx.cpp, processFIX).</summary>
    public static BsrRequest? Parse(byte[] data)
    {
        if (data.Length > 100_000) return null;   // far bigger than any real request
        var lines = Encoding.Latin1.GetString(data).Replace("\r", "")
                                .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 4 || lines[1].Trim() != "H_OK") return null;
        if (!int.TryParse(lines[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) || id < 0 || id > 0xFFFF) return null;
        int.TryParse(lines[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pieceSize);
        if (!int.TryParse(lines[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int first) || first < 0) return null;

        var missing = new List<int> { first };
        int next = first + 1;   // the piece after the last one listed
        bool inRun = false;
        int i = 4;
        for (; i < lines.Length; i++)
        {
            if (!int.TryParse(lines[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int piece)) return null;
            if (piece == -99) { i++; break; }
            if (piece < 0) { inRun = true; continue; }
            if (piece > 32767) return null;   // piece numbers are 15 bits on the air
            if (inRun)
            {
                for (int p = next; p < piece; p++) missing.Add(p);
                inRun = false;
            }
            missing.Add(piece);
            next = piece + 1;
        }
        string? name = i < lines.Length ? lines[i] : null;
        int? mode = i + 1 < lines.Length && int.TryParse(lines[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int m) ? m : null;
        return new BsrRequest(id, Math.Max(0, pieceSize), missing.Distinct().Order().ToList(), name, mode);
    }

    /// <summary>The settings as QSSTV's 5-digit mode number: mode (A 0, B 1, E 2), bandwidth
    /// (2.2/2.3 kHz 0, 2.5 kHz 1), protection (high 0, low 1), QAM (4 0, 16 1, 64 2), interleave
    /// (long 0, short 1). Mode B, 2.5 kHz, high, 16-QAM, long is 11010.</summary>
    public static int ModeCodeOf(TransmitSettings s) =>
        (int)s.Mode * 10000 + (s.Bandwidth == HamDrmBandwidth.Wide ? 1000 : 0) + (s.Protection == HamDrmProtection.Low ? 100 : 0)
        + (s.Qam switch { 4 => 0, 64 => 2, _ => 1 }) * 10 + (s.Interleave == HamDrmInterleave.Short ? 1 : 0);
}

/// <summary>QSSTV gives each bsr.bin transmission transport ID 0, 1 or 2 in turn (real pictures'
/// IDs are never below 3), so a receiver doesn't mix up two requests.</summary>
public static class BsrTransportIds
{
    private static int _last = 2;

    public static int Next() => Interlocked.Increment(ref _last) % 3;
}
