// FmPieces.cs - what RFpix's FM signal carries in its blocks, and putting a file back together.
//
// Each error-corrected block holds one fountain piece of a file, labelled with the file's number (made
// up for each picture), how many pieces rebuild it, and the piece's own number. A few blocks instead
// hold the file's details: the sender's callsign and the file's name (piece number 65535). They go in
// the first frame, every fourth frame after it, and the last frame, so a station that tunes in late
// still gets them.
//
// The collector keeps the pieces of each file heard and rebuilds it with the fountain code as soon as
// enough are in. A file rebuilt before its details arrive waits for them until its transmission ends.

using System.Text;
using RFpix.HamDrm.Coding;

namespace RFpix.FmSignal;

/// <summary>One decoded block.</summary>
/// <param name="FileNumber">Which file it belongs to (each picture sent gets its own number).</param>
/// <param name="SourcePieces">How many pieces rebuild that file.</param>
/// <param name="Number">The piece's number, or <see cref="InfoNumber"/> for the file's details.</param>
/// <param name="Payload">The piece (or the details).</param>
public sealed record FmBlock(int FileNumber, int SourcePieces, int Number, byte[] Payload)
{
    public const int InfoNumber = 0xFFFF;
    /// <summary>A request for missing pieces of the file (FmRequest), from a station that's receiving it.</summary>
    public const int RequestNumber = 0xFFFE;
    public bool IsInfo => Number == InfoNumber;
    public bool IsRequest => Number == RequestNumber;
}

/// <summary>A request for missing pieces of a file sent with RFpix's FM signal: which file (its number,
/// pieces needed and piece size) and how many more pieces the asking station needs. Any new repair pieces
/// will do, so the sender answers with that many (and a few spare), which also fill any other station's gaps.</summary>
public sealed record FmRequest(int FileNumber, int SourcePieces, int PieceBytes, int Needed, string Callsign)
{
    private const byte Version = 1;

    public byte[] ToPayload(int bytes)
    {
        var payload = new byte[bytes];
        var call = Encoding.ASCII.GetBytes(Callsign.Length > 12 ? Callsign[..12] : Callsign);
        payload[0] = Version;
        payload[1] = (byte)(PieceBytes >> 8); payload[2] = (byte)PieceBytes;
        payload[3] = (byte)(Needed >> 8); payload[4] = (byte)Needed;
        payload[5] = (byte)call.Length;
        call.CopyTo(payload, 6);
        return payload;
    }

    /// <summary>The request in a block, or null if it isn't one RFpix understands.</summary>
    public static FmRequest? From(FmBlock block)
    {
        var p = block.Payload;
        if (!block.IsRequest || p.Length < 6 || p[0] != Version) return null;
        int callLength = p[5];
        if (6 + callLength > p.Length) return null;
        int pieceBytes = p[1] << 8 | p[2], needed = p[3] << 8 | p[4];
        if (pieceBytes == 0 || needed == 0) return null;
        return new FmRequest(block.FileNumber, block.SourcePieces, pieceBytes, needed, Encoding.ASCII.GetString(p, 6, callLength));
    }
}

/// <summary>A file's details, sent with it: who sent it, and its name.</summary>
public sealed record FmFileInfo(string Callsign, string Name)
{
    private const byte Version = 1;

    /// <summary>The details as a block's payload of <paramref name="bytes"/> (names cut short to fit).</summary>
    public byte[] ToPayload(int bytes)
    {
        var payload = new byte[bytes];
        var call = Encoding.ASCII.GetBytes(Callsign.Length > 12 ? Callsign[..12] : Callsign);
        var name = Encoding.UTF8.GetBytes(Name);
        int room = bytes - 3 - call.Length;
        if (name.Length > Math.Min(room, 255)) name = name[..Math.Min(room, 255)];
        payload[0] = Version;
        payload[1] = (byte)call.Length;
        call.CopyTo(payload, 2);
        payload[2 + call.Length] = (byte)name.Length;
        name.CopyTo(payload, 3 + call.Length);
        return payload;
    }

    /// <summary>The details from a block's payload, or null if it isn't details RFpix understands.</summary>
    public static FmFileInfo? FromPayload(byte[] payload)
    {
        if (payload.Length < 3 || payload[0] != Version) return null;
        int callLength = payload[1];
        if (2 + callLength + 1 > payload.Length) return null;
        int nameLength = payload[2 + callLength];
        if (3 + callLength + nameLength > payload.Length) return null;
        string call = Encoding.ASCII.GetString(payload, 2, callLength);
        string name = Encoding.UTF8.GetString(payload, 3 + callLength, nameLength);
        return new FmFileInfo(call, name);
    }
}

/// <summary>A file received complete.</summary>
/// <param name="FileNumber">Its number in the signal.</param>
/// <param name="Data">The file itself.</param>
/// <param name="Name">Its name: the sender's, or one made up if the details never arrived.</param>
/// <param name="Callsign">The sender's callsign, if the details arrived.</param>
/// <param name="Mode">The speed it came at (the last frame's).</param>
/// <param name="PiecesReceived">Pieces that arrived.</param>
/// <param name="SourcePieces">Pieces it had: any that many rebuild it.</param>
public sealed record FmReceivedFile(int FileNumber, byte[] Data, string Name, string? Callsign, FmSignalMode? Mode,
                                    int PiecesReceived, int SourcePieces);

/// <summary>A file still missing pieces.</summary>
/// <param name="StillNeeded">How many more pieces would rebuild it (any of them).</param>
public sealed record FmUnfinishedFile(int FileNumber, string? Name, string? Callsign, FmSignalMode? Mode, int PieceBytes,
                                      int SourcePieces, int PiecesReceived, int HighestPiece, DateTime LastHeard)
{
    public int StillNeeded => Math.Max(0, SourcePieces - PiecesReceived);
}

public sealed class FmPieceCollector
{
    private sealed class File
    {
        public int Number, SourcePieces, PieceBytes;
        public readonly Dictionary<int, byte[]> Pieces = new();
        public FmFileInfo? Info;
        public FmSignalMode? Mode;
        public byte[]? Rebuilt;
        public string? Extension;
        public bool Done;
        public DateTime LastHeard;
    }

    private readonly List<File> _files = new();
    private const int Remembered = 20;

    /// <summary>Adds a block; returns any file that it completes.</summary>
    public FmReceivedFile? Add(FmBlock block, FmSignalMode? mode, DateTime when)
    {
        if (block.IsRequest) return null;   // another station asking for pieces, not part of the file
        // (A file's details are the same size as its pieces: every block of a speed carries the same.)
        var file = _files.FirstOrDefault(f => f.Number == block.FileNumber && f.SourcePieces == block.SourcePieces &&
                                              f.PieceBytes == block.Payload.Length);
        if (file is null)
        {
            file = new File { Number = block.FileNumber, SourcePieces = block.SourcePieces, PieceBytes = block.Payload.Length };
            _files.Add(file);
            if (_files.Count > Remembered) _files.RemoveAt(0);
        }
        file.LastHeard = when;
        file.Mode = mode ?? file.Mode;
        if (file.Done) return null;
        if (block.IsInfo)
        {
            file.Info ??= FmFileInfo.FromPayload(block.Payload);
            return file.Rebuilt is not null && file.Info is not null ? Finish(file) : null;
        }
        file.Pieces[block.Number] = block.Payload;
        if (file.Rebuilt is null && file.Pieces.Count >= file.SourcePieces)
        {
            int total = file.Pieces.Keys.Max() + 1;
            var all = new byte[total * file.PieceBytes];
            foreach (var (n, piece) in file.Pieces) piece.CopyTo(all, n * file.PieceBytes);
            if (FountainFile.Unwrap(all, file.SourcePieces, file.PieceBytes, file.Pieces.ContainsKey) is { } unwrapped)
                (file.Rebuilt, file.Extension) = unwrapped;
        }
        return file.Rebuilt is not null && file.Info is not null ? Finish(file) : null;
    }

    /// <summary>The transmission has ended (or the recording): files rebuilt without their details are
    /// given out now, with a made-up name.</summary>
    public IReadOnlyList<FmReceivedFile> TransmissionEnded() =>
        _files.Where(f => !f.Done && f.Rebuilt is not null).ToList().Select(Finish).ToList();

    /// <summary>Files heard but not complete, the most recent first.</summary>
    public IReadOnlyList<FmUnfinishedFile> Unfinished =>
        _files.Where(f => !f.Done && f.Pieces.Count > 0 && f.Rebuilt is null).OrderByDescending(f => f.LastHeard)
              .Select(f => new FmUnfinishedFile(f.Number, f.Info?.Name, f.Info?.Callsign, f.Mode, f.PieceBytes, f.SourcePieces,
                                                f.Pieces.Count, f.Pieces.Keys.Max(), f.LastHeard)).ToList();

    /// <summary>The file being received now (most recently heard, not done), if any.</summary>
    public FmUnfinishedFile? Current => Unfinished.FirstOrDefault();

    private static FmReceivedFile Finish(File file)
    {
        file.Done = true;
        string extension = string.IsNullOrEmpty(file.Extension) ? "bin" : file.Extension;
        string name = file.Info is { Name.Length: > 0 } info ? info.Name : $"rfpix-fm-{file.Number}.{extension}";
        if (Path.GetExtension(name).Length == 0) name += "." + extension;
        var result = new FmReceivedFile(file.Number, file.Rebuilt!, name, file.Info?.Callsign is { Length: > 0 } c ? c : null,
                                        file.Mode, file.Pieces.Count, file.SourcePieces);
        file.Pieces.Clear();   // the memory back; the file stays known so late pieces are ignored
        return result;
    }
}
