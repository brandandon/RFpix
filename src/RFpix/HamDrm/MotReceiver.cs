// MotReceiver.cs - rebuilds files from the decoded picture data.
//
// Three layers, each with its own checksum:
//   1. Packets: each frame carries one. A header byte says whether it's the first
//      and/or last packet of a "data group", plus a counter (0-7) to spot gaps.
//   2. Data groups: one or more packets joined. Each is either the file's header
//      (type 3) or one numbered piece ("segment") of the file's body (type 4).
//      All groups of one file share a "transport ID".
//   3. The file (a MOT object): the header gives the body's size and the file's
//      name; the body is its segments joined in order.
// Senders repeat segments (QSSTV sends some up to 6 times), so a piece lost to
// noise often arrives again later.
//
// Formats from the DRM and DAB MOT standards, as QSSTV uses them (its sourcedecoder.cpp).

using System.Text;
using RFpix.HamDrm.Coding;

namespace RFpix.HamDrm;

/// <summary>A file received complete.</summary>
/// <param name="Name">The name the sender gave it, e.g. "img04.jpg". It comes off the air,
/// so clean it before using it as a file name.</param>
public sealed record ReceivedFile(string Name, byte[] Data, int TransportId)
{
    /// <summary>1 to 4 if the file came wrapped in Reed-Solomon protection (now removed).</summary>
    public int? ReedSolomonLevel { get; init; }

    /// <summary>Set if something went wrong with the file, in words for the operator.</summary>
    public string? Problem { get; init; }

    /// <summary>How many pieces of the transfer never arrived and were rebuilt by Reed-Solomon.</summary>
    public int RepairedPieces { get; init; }
}

/// <summary>A file that's still missing pieces, kept so a repeat or a fix can complete it.</summary>
/// <param name="Name">Null if its header never arrived.</param>
/// <param name="PieceSize">Bytes in a full piece (0 if no full piece arrived).</param>
/// <param name="Pieces">How many pieces the file has.</param>
/// <param name="Missing">The pieces that haven't arrived, in order.</param>
public sealed record UnfinishedFile(int TransportId, string? Name, int PieceSize, int Pieces, IReadOnlyList<int> Missing);

/// <summary>How far along the current file is.</summary>
/// <param name="Name">Null until the file's header has arrived.</param>
/// <param name="Size">The file's size in bytes, or 0 until the header has arrived.</param>
/// <param name="SegmentsExpected">0 until known (from the header, or the last segment).</param>
public sealed record TransferProgress(int TransportId, string? Name, int Size, int SegmentsReceived, int SegmentsExpected)
{
    /// <summary>0 to 1, or null if the total isn't known yet.</summary>
    public double? Fraction => SegmentsExpected > 0 ? Math.Min(1, (double)SegmentsReceived / SegmentsExpected) : null;
}

public sealed class MotReceiver
{
    private sealed class Transfer
    {
        public string? Name;
        public int BodySize = -1;
        public readonly Dictionary<int, byte[]> Segments = new();
        public int? LastSegment;
        public int SegmentSize;   // size of a full (not last) segment, once seen
        public bool Done;
    }

    private readonly Dictionary<int, Transfer> _transfers = new();
    private readonly List<int> _recent = new();   // transport IDs, the most recently heard last

    /// <summary>Files still missing pieces that are kept (the oldest are forgotten after this many).</summary>
    public const int KeepUnfinished = 10;
    private readonly Dictionary<int, (List<byte> Data, int Counter)> _assembling = new();   // per packet ID
    private int? _currentId;
    private readonly HashSet<int> _seen = new();
    private int? _seenId;

    /// <summary>The pieces (body segment numbers) of the current file that arrived in this
    /// transmission, whether or not an earlier one had already brought them.</summary>
    public IReadOnlyCollection<int> PiecesThisTransmission => _seen;

    /// <summary>Packets received with a good checksum, and with a bad one.</summary>
    public int PacketsGood { get; private set; }
    public int PacketsBad { get; private set; }

    /// <summary>The file currently arriving, or null.</summary>
    public TransferProgress? Progress =>
        _currentId is int id && _transfers.TryGetValue(id, out var t) ? ProgressOf(id, t) : null;

    /// <summary>Adds one decoded packet (a frame's bytes). Returns a file if this packet completed one.</summary>
    public ReceivedFile? AddPacket(byte[] packet)
    {
        if (packet.Length < 4 || !CheckCrc16(packet)) { PacketsBad++; return null; }
        PacketsGood++;

        byte header = packet[0];
        bool first = (header & 0x80) != 0, last = (header & 0x40) != 0;
        int packetId = (header >> 4) & 3;
        bool hasLength = (header & 0x08) != 0;     // "padding indicator": byte 1 says how much is data
        int counter = header & 7;
        int length = hasLength ? packet[1] : packet.Length - 3;
        int start = hasLength ? 2 : 1;
        if (start + length > packet.Length - 2) return null;

        // Join packets into a data group, checking the counter for gaps.
        if (first) _assembling[packetId] = (new List<byte>(), counter);
        else if (!_assembling.TryGetValue(packetId, out var part) || counter != (part.Counter + 1) % 8)
        {
            _assembling.Remove(packetId);   // a packet went missing: this group is lost
            return null;
        }
        var group = _assembling[packetId];
        group.Data.AddRange(packet.AsSpan(start, length).ToArray());
        _assembling[packetId] = (group.Data, counter);
        if (!last) return null;
        _assembling.Remove(packetId);

        return AddDataGroup(group.Data.ToArray());
    }

    private ReceivedFile? AddDataGroup(byte[] g)
    {
        if (g.Length < 4) return null;
        byte h = g[0];
        bool extension = (h & 0x80) != 0, hasCrc = (h & 0x40) != 0, hasSegment = (h & 0x20) != 0, hasUser = (h & 0x10) != 0;
        int type = h & 0x0F;
        int end = g.Length;
        if (hasCrc)
        {
            if (!CheckCrc16(g)) return null;
            end -= 2;
        }

        int pos = 2 + (extension ? 2 : 0);   // skip header, counters and any extension
        int segment = 0;
        bool lastSegment = false;
        if (hasSegment)
        {
            lastSegment = (g[pos] & 0x80) != 0;
            segment = ((g[pos] & 0x7F) << 8) | g[pos + 1];
            pos += 2;
        }
        int transportId = 0;
        if (hasUser)
        {
            int access = g[pos++];
            int fieldLength = access & 0x0F;
            if ((access & 0x10) != 0 && fieldLength >= 2) transportId = (g[pos] << 8) | g[pos + 1];
            pos += fieldLength;
        }
        if (pos + 2 > end) return null;
        int size = ((g[pos] & 0x1F) << 8) | g[pos + 1];   // top 3 bits: repetition count (not needed)
        pos += 2;
        if (pos + size > end) return null;
        var data = g.AsSpan(pos, size).ToArray();

        if (type != 3 && type != 4) return null;   // only file headers and bodies matter here
        if (!_transfers.TryGetValue(transportId, out var t)) _transfers[transportId] = t = new Transfer();
        if (_currentId != transportId)
        {
            _recent.Remove(transportId);
            _recent.Add(transportId);
            // Keep only the latest few unfinished files (never the one arriving now).
            while (_recent.Count > KeepUnfinished)
            {
                _transfers.Remove(_recent[0]);
                _recent.RemoveAt(0);
            }
        }
        _currentId = transportId;
        if (type == 4)
        {
            // Which pieces this transmission brought by itself (for test reports): a repeat
            // of a file also fills in pieces an earlier transmission missed.
            if (_seenId != transportId) { _seen.Clear(); _seenId = transportId; }
            _seen.Add(segment);
        }
        if (t.Done) return null;   // already received: this is a repeat

        if (type == 3)
        {
            // A different file under the same transport ID (IDs come from a 16-bit hash of
            // the name, so this is rare): start that transfer afresh rather than mix them.
            var header = new Transfer();
            ReadHeader(header, data);
            if (t.Name is not null && header.Name is not null && (header.Name != t.Name || header.BodySize != t.BodySize))
                _transfers[transportId] = t = new Transfer();
            ReadHeader(t, data);
        }
        else
        {
            t.Segments.TryAdd(segment, data);
            if (lastSegment) t.LastSegment = segment;
            else t.SegmentSize = Math.Max(t.SegmentSize, size);
        }
        return TryFinish(transportId, t);
    }

    /// <summary>The MOT header: 7 bytes of sizes and type, then parameters (we only need the name).</summary>
    private static void ReadHeader(Transfer t, byte[] d)
    {
        if (d.Length < 7) return;
        t.BodySize = (d[0] << 20) | (d[1] << 12) | (d[2] << 4) | (d[3] >> 4);
        int pos = 7;
        while (pos < d.Length)
        {
            int lengthType = d[pos] >> 6, id = d[pos] & 0x3F;
            int length, start;
            switch (lengthType)
            {
                case 0: length = 0; start = pos + 1; break;
                case 1: length = 1; start = pos + 1; break;
                case 2: length = 4; start = pos + 1; break;
                default:
                    if (pos + 1 >= d.Length) return;
                    if ((d[pos + 1] & 0x80) != 0)
                    {
                        if (pos + 2 >= d.Length) return;
                        length = ((d[pos + 1] & 0x7F) << 8) | d[pos + 2];
                        start = pos + 3;
                    }
                    else { length = d[pos + 1] & 0x7F; start = pos + 2; }
                    break;
            }
            if (start + length > d.Length) return;
            // Parameter 12, "ContentName": a character-set byte, then the name.
            if (id == 12 && length > 1) t.Name = Encoding.Latin1.GetString(d, start + 1, length - 1);
            pos = start + length;
        }
    }

    private static ReceivedFile? TryFinish(int transportId, Transfer t)
    {
        if (t.Name is null || t.BodySize < 0) return null;
        int? rsLevel = RsFile.LevelOf(t.Name);

        int total = t.LastSegment is int last ? last + 1
                  : t.SegmentSize > 0 ? (t.BodySize + t.SegmentSize - 1) / t.SegmentSize
                  : 0;
        if (total == 0) return null;
        int have = Enumerable.Range(0, total).Count(t.Segments.ContainsKey);

        if (have == total && t.LastSegment is not null)
        {
            var body = new List<byte>(t.BodySize);
            for (int i = 0; i < total; i++) body.AddRange(t.Segments[i]);
            if (body.Count < t.BodySize) return null;
            var data = body.GetRange(0, t.BodySize).ToArray();
            t.Done = true;
            return rsLevel is int level ? Unwrapped(t.Name, data, level, transportId, missing: null)
                                        : new ReceivedFile(t.Name, data, transportId);
        }

        return null;
    }

    /// <summary>Call when the transmission has ended (after <see cref="FinishWithRepairs"/>).
    /// Returns the progress of a file that didn't finish, or null. Files already received are
    /// forgotten, so the same file sent again later is shown again; unfinished ones are kept,
    /// so a repeat of the same file (same transport ID) can fill in the missing pieces.</summary>
    public TransferProgress? EndOfTransmission()
    {
        TransferProgress? unfinished = null;
        if (_currentId is int id && _transfers.TryGetValue(id, out var t) && !t.Done && t.Name is not null)
            unfinished = ProgressOf(id, t);
        foreach (int done in _transfers.Where(p => p.Value.Done).Select(p => p.Key).ToList())
        {
            _transfers.Remove(done);
            _recent.Remove(done);
        }
        _assembling.Clear();
        _currentId = null;
        _seen.Clear();
        _seenId = null;
        return unfinished;
    }

    /// <summary>Call when the transmission has ended. If the file being received is protected by
    /// Reed-Solomon and still has pieces missing, tries to rebuild them. Returns the file if that
    /// worked, or null (nothing to finish, or too many pieces missing).</summary>
    public ReceivedFile? FinishWithRepairs()
    {
        if (_currentId is not int id || !_transfers.TryGetValue(id, out var t) || t.Done) return null;
        if (t.Name is null || t.BodySize < 0 || RsFile.LevelOf(t.Name) is not int rs || t.SegmentSize <= 0) return null;

        int total = t.LastSegment is int last ? last + 1 : (t.BodySize + t.SegmentSize - 1) / t.SegmentSize;
        int have = Enumerable.Range(0, total).Count(t.Segments.ContainsKey);
        if (have == total) return null;   // (a complete file is finished as its last piece arrives)

        // Missing pieces' bytes are marked as known-missing ("erasures") and rebuilt from the
        // repair data. Each block can rebuild at most 255 - k of its bytes, so don't try with
        // less than k/255 of the pieces in.
        if (have * ReedSolomon.BlockSize < total * RsFile.DataBytes(rs)) return null;
        var data = new byte[t.BodySize];
        var missing = new bool[total];
        for (int i = 0; i < total; i++)
        {
            if (t.Segments.TryGetValue(i, out var seg))
                Array.Copy(seg, 0, data, i * t.SegmentSize, Math.Min(seg.Length, t.BodySize - i * t.SegmentSize));
            else missing[i] = true;
        }
        var file = Unwrapped(t.Name, data, rs, id, at => missing[Math.Min(at / t.SegmentSize, total - 1)]);
        if (file.Problem is not null) return null;
        t.Done = true;
        return file with { RepairedPieces = total - have };
    }

    private static ReceivedFile Unwrapped(string name, byte[] data, int level, int transportId, Func<int, bool>? missing)
    {
        if (RsFile.Unwrap(data, level, missing) is var (file, extension))
            return new ReceivedFile(Path.GetFileNameWithoutExtension(name) + "." + extension, file, transportId)
                   { ReedSolomonLevel = level };
        return new ReceivedFile(name, data, transportId)
               { ReedSolomonLevel = level, Problem = "The Reed-Solomon protection couldn't repair this file." };
    }

    /// <summary>The files still missing pieces whose size is known, the most recently heard first.</summary>
    public IReadOnlyList<UnfinishedFile> Unfinished()
    {
        var list = new List<UnfinishedFile>();
        for (int i = _recent.Count - 1; i >= 0; i--)
            if (_transfers.TryGetValue(_recent[i], out var t) && !t.Done && UnfinishedOf(_recent[i], t) is { } u) list.Add(u);
        return list;
    }

    /// <summary>The file with this transport ID if it's still missing pieces, or null.</summary>
    public UnfinishedFile? Unfinished(int transportId) =>
        _transfers.TryGetValue(transportId, out var t) && !t.Done ? UnfinishedOf(transportId, t) : null;

    private static UnfinishedFile? UnfinishedOf(int id, Transfer t)
    {
        int total = t.LastSegment is int last ? last + 1
                  : t.BodySize > 0 && t.SegmentSize > 0 ? (t.BodySize + t.SegmentSize - 1) / t.SegmentSize
                  : 0;
        if (total == 0) return null;
        var missing = Enumerable.Range(0, total).Where(i => !t.Segments.ContainsKey(i)).ToList();
        return missing.Count == 0 ? null : new UnfinishedFile(id, t.Name, t.SegmentSize, total, missing);
    }

    private static TransferProgress ProgressOf(int id, Transfer t)
    {
        int expected = t.LastSegment is int last ? last + 1
                     : t.BodySize > 0 && t.SegmentSize > 0 ? (t.BodySize + t.SegmentSize - 1) / t.SegmentSize
                     : 0;
        int received = t.Done ? expected : t.Segments.Count;
        return new TransferProgress(id, t.Name, Math.Max(0, t.BodySize), received, expected);
    }

    /// <summary>Checks the 16-bit CRC in the last two bytes against all the bytes before them.</summary>
    public static bool CheckCrc16(byte[] data)
    {
        int sent = (data[^2] << 8) | data[^1];
        return Crc.Crc16(data.AsSpan(0, data.Length - 2)) == sent;
    }
}
