// FountainFile.cs - wraps a file in RFpix's repair code (FountainCode) for sending, and
// unwraps it on receiving, the way RsFile does for QSSTV's Reed-Solomon.
//
// What goes out: the file with a small header in front, cut into K pieces of the size HamDRM sends
// a file in (the last one padded with zeros), followed by the repair pieces:
//
//     [ source piece 0 ][ source piece 1 ] ... [ source piece K-1 ][ repair 0 ] ... [ repair R-1 ]
//
// The 8-byte header (inside the source pieces, so it's rebuilt along with the file):
//     byte 0     the format: 1
//     bytes 1-3  the file's length in bytes (up to 16 MB, low byte first)
//     bytes 4-7  the file's extension ("jpg", "jp2", "avif"), padded with zeros
//
// The receiver needs K and the piece size. The piece size is the size HamDRM's pieces already have
// (each piece of the file goes in one HamDRM piece, so a piece lost on the air is a piece missing
// here). K travels in the file's name: "tower.fc33" is a file of 33 pieces with repair pieces after
// them, the way QSSTV's "tower.rs2" says Reed-Solomon RS2. Only RFpix can rebuild such a file.

using System.Text;
using System.Text.RegularExpressions;

namespace RFpix.HamDrm.Coding;

public static class FountainFile
{
    public const int HeaderBytes = 8;
    private const byte Format = 1;
    public const int MaxFileBytes = 0xFFFFFF;

    /// <summary>The name a wrapped file is sent under: its name without the extension, then ".fc" and
    /// the number of source pieces, e.g. "tower.fc33".</summary>
    public static string NameFor(string baseName, int sourcePieces) => $"{baseName}.fc{sourcePieces}";

    /// <summary>The number of source pieces, if the name is a wrapped file's ("tower.fc33" gives 33),
    /// or null.</summary>
    public static int? SourcePiecesOf(string name)
    {
        var m = Regex.Match(name, @"\.fc([1-9][0-9]{0,4})$");
        if (!m.Success) return null;
        int k = int.Parse(m.Groups[1].Value);
        return k <= MaxSourcePieces ? k : null;
    }

    /// <summary>The most source pieces a file may have (HamDRM numbers pieces up to 32767, and the
    /// repair pieces come after them).</summary>
    public const int MaxSourcePieces = 16384;

    /// <summary>Wraps a file for sending with these settings: the name to send it under and the bytes.
    /// The pieces are the size these settings' HamDRM pieces are.</summary>
    public static (string Name, byte[] Data) WrapFor(byte[] file, string extension, string baseName,
                                                     TransmitSettings settings, int repairPercent)
    {
        var (data, k) = Wrap(file, extension, HamDrmTransmitter.PieceSizeFor(settings), repairPercent);
        return (NameFor(baseName, k), data);
    }

    /// <summary>How many source pieces a file of <paramref name="fileBytes"/> needs (with the header).</summary>
    public static int SourcePieces(int fileBytes, int pieceSize) => (HeaderBytes + fileBytes + pieceSize - 1) / pieceSize;

    /// <summary>How many repair pieces add <paramref name="percent"/>% to <paramref name="sourcePieces"/>
    /// (rounded up, so any extra at all gives at least one).</summary>
    public static int RepairPieces(int sourcePieces, int percent) =>
        percent <= 0 ? 0 : (int)Math.Ceiling(sourcePieces * percent / 100.0);

    /// <summary>Wraps a file for sending: returns the bytes to send (all the pieces, one after another)
    /// and how many of them are source pieces.</summary>
    public static (byte[] Data, int SourcePieces) Wrap(byte[] file, string extension, int pieceSize, int repairPercent)
    {
        if (file.Length == 0) throw new ArgumentException("The file is empty.", nameof(file));
        if (file.Length > MaxFileBytes) throw new ArgumentException($"The file is over {MaxFileBytes / 1_000_000} MB.", nameof(file));
        if (pieceSize < HeaderBytes) throw new ArgumentOutOfRangeException(nameof(pieceSize));

        int k = SourcePieces(file.Length, pieceSize);
        if (k > MaxSourcePieces) throw new ArgumentException($"The file is too big for the repair code at {pieceSize}-byte pieces.", nameof(file));
        var stream = new byte[k * pieceSize];
        stream[0] = Format;
        stream[1] = (byte)file.Length;
        stream[2] = (byte)(file.Length >> 8);
        stream[3] = (byte)(file.Length >> 16);
        var ext = Encoding.Latin1.GetBytes(extension.TrimStart('.').ToLowerInvariant());
        for (int i = 0; i < 4 && i < ext.Length; i++) stream[4 + i] = ext[i];
        file.CopyTo(stream, HeaderBytes);

        var source = Enumerable.Range(0, k).Select(i => stream.AsSpan(i * pieceSize, pieceSize).ToArray()).ToArray();
        var repairs = FountainCode.Repair(source, RepairPieces(k, repairPercent));

        var data = new byte[(k + repairs.Length) * pieceSize];
        stream.CopyTo(data, 0);
        for (int r = 0; r < repairs.Length; r++) repairs[r].CopyTo(data, (k + r) * pieceSize);
        return (data, k);
    }

    /// <summary>The bytes sent, with <paramref name="count"/> new repair pieces added at the end, numbered on
    /// from the last ones: for answering a request for missing pieces. New repair pieces fill any
    /// station's gaps, whichever pieces each one missed.</summary>
    public static byte[] Extend(byte[] data, int sourcePieces, int pieceSize, int count)
    {
        if (data.Length % pieceSize != 0 || data.Length / pieceSize < sourcePieces)
            throw new ArgumentException("That isn't a repair-coded file of this piece size.", nameof(data));
        int total = data.Length / pieceSize;
        var source = Enumerable.Range(0, sourcePieces).Select(i => data.AsSpan(i * pieceSize, pieceSize).ToArray()).ToArray();
        var more = FountainCode.Repair(source, count, first: total - sourcePieces);
        var result = new byte[data.Length + count * pieceSize];
        data.CopyTo(result, 0);
        for (int i = 0; i < count; i++) more[i].CopyTo(result, data.Length + i * pieceSize);
        return result;
    }

    /// <summary>How many new repair pieces to send for a station that still needs <paramref name="needed"/>:
    /// a few more, for any lost on the way and the odd time one more is needed.</summary>
    public static int FreshPiecesFor(int needed) => needed + 1 + needed / 5;

    /// <summary>Unwraps a received file. <paramref name="received"/> holds the pieces one after another,
    /// as sent (missing ones can hold anything); <paramref name="arrived"/> says which pieces came.
    /// Returns null if there aren't enough pieces yet, or what was rebuilt isn't a file.</summary>
    public static (byte[] File, string Extension)? Unwrap(byte[] received, int sourcePieces, int pieceSize, Func<int, bool> arrived)
    {
        if (sourcePieces <= 0 || pieceSize < HeaderBytes || received.Length % pieceSize != 0) return null;
        int total = received.Length / pieceSize;
        if (total < sourcePieces) return null;

        var pieces = new Dictionary<int, byte[]>();
        for (int n = 0; n < total; n++)
            if (arrived(n)) pieces[n] = received.AsSpan(n * pieceSize, pieceSize).ToArray();

        var source = FountainCode.Rebuild(sourcePieces, pieceSize, pieces);
        if (source is null) return null;

        var stream = new byte[sourcePieces * pieceSize];
        for (int i = 0; i < sourcePieces; i++) source[i].CopyTo(stream, i * pieceSize);
        if (stream[0] != Format) return null;
        int length = stream[1] | stream[2] << 8 | stream[3] << 16;
        if (length == 0 || HeaderBytes + length > stream.Length || SourcePieces(length, pieceSize) != sourcePieces) return null;
        string extension = Encoding.Latin1.GetString(stream, 4, 4).TrimEnd('\0');
        return (stream.AsSpan(HeaderBytes, length).ToArray(), extension);
    }
}
