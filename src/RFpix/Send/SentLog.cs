// SentLog.cs - the last few pictures sent, so a request for missing pieces can be answered.
//
// When another station asks for missing pieces (a BSR, see HamDrm/Bsr.cs), the request
// names the picture only by its transport ID, a number made from its file name. RFpix
// looks that number up here, among the last 6 pictures it sent (as QSSTV keeps), and
// checks the request makes sense for that picture (the right piece size, pieces it has).
// Then it can send just those pieces again: a "fix".
//
// A picture sent with RFpix's repair code (the fountain code) is answered differently: with new repair
// pieces, numbered on from the last ones sent. Any new repair piece fills a gap in any station's copy,
// whichever pieces it missed, so one short answer covers every station that asked.
//
// Pictures sent with RFpix's FM signal are kept here too (by their number in the signal), and
// a station's request for more pieces (FmRequest, heard by the FM receiver) is answered the same way, with
// new repair pieces in the FM signal at the picture's own speed.
//
// The log lives only while RFpix runs, as in QSSTV.

using RFpix.FmSignal;
using RFpix.HamDrm;
using RFpix.HamDrm.Coding;

namespace RFpix.Send;

/// <summary>A picture as it went out: the exact bytes, name and settings, so its pieces can be sent again.</summary>
/// <param name="Name">The name it went out under (e.g. "tower.rs2").</param>
/// <param name="ShownName">The picture's own name, for the screen (e.g. "tower.jp2").</param>
/// <param name="LeadInSeconds">The key-up lead-in it was sent with (a fix uses the same).</param>
/// <param name="FmMode">For a picture sent with RFpix's FM signal: its speed (TransportId is then its number
/// in the signal, and FmSourcePieces how many pieces rebuild it). Null for HamDRM.</param>
public sealed record SentFile(string Name, string ShownName, byte[] Data, TransmitSettings Settings,
                              int TransportId, int PieceSize, int Pieces, DateTime Sent, double LeadInSeconds = 0.3,
                              FmSignalMode? FmMode = null, int FmSourcePieces = 0);

/// <summary>What to send in answer to a request: which picture, and which of its pieces.</summary>
/// <param name="Fresh">For a picture with RFpix's repair code: true when the pieces are new repair pieces
/// (added to the end of <see cref="SentFile.Data"/>), which fill every asking station's gaps at once.</param>
/// <param name="Stations">How many requests this answers (a repair-coded picture's requests combine).</param>
public sealed record FixPlan(SentFile File, IReadOnlyList<int> Pieces, BsrRequest Request, bool Fresh = false, int Stations = 1)
{
    /// <summary>The fix's sound: only those pieces, with the picture's own settings (HamDRM only).</summary>
    public HamDrmTransmitter Transmitter(string callsign) =>
        new(File.Name, File.Data, File.Settings, callsign, onlyPieces: Pieces);

    /// <summary>The fix's sound: HamDRM, or RFpix's FM signal for a picture sent with it (the new repair pieces
    /// at its own speed, with the sender's callsign and the picture's name).</summary>
    public float[] Audio(string callsign) => File.FmMode is { } mode
        ? FmSignalTransmitter.Audio(File.Data, File.FmSourcePieces, mode, (ushort)File.TransportId,
                                    new FmFileInfo(callsign, File.ShownName), firstPiece: Pieces[0])
        : Transmitter(callsign).Audio();

    /// <summary>How long the fix is on the air, in seconds (the lead-in not included).</summary>
    public double Seconds => File.FmMode is { } mode ? FmSignalTransmitter.Seconds(Pieces.Count, mode)
                                                     : Transmitter("X").Duration.TotalSeconds;
}

public sealed class SentLog
{
    public const int Keep = 6;

    private readonly object _lock = new();
    private readonly List<SentFile> _files = new();

    /// <summary>The log RFpix's pages share.</summary>
    public static SentLog Current { get; } = new();

    /// <summary>The pictures in the log, the latest first.</summary>
    public IReadOnlyList<SentFile> Files { get { lock (_lock) return _files.AsEnumerable().Reverse().ToList(); } }

    /// <summary>Notes a picture being sent (the same picture sent again replaces its older entry).</summary>
    public SentFile Add(string name, string shownName, byte[] data, TransmitSettings settings, double leadInSeconds = 0.3)
    {
        var tx = new HamDrmTransmitter(name, data, settings, "X");
        var file = new SentFile(name, shownName, data, settings, tx.TransportId, tx.PieceSize, tx.Pieces, DateTime.Now, leadInSeconds);
        lock (_lock)
        {
            _files.RemoveAll(f => f.TransportId == file.TransportId);
            _files.Add(file);
            while (_files.Count > Keep) _files.RemoveAt(0);
        }
        return file;
    }

    /// <summary>Notes a picture being sent with RFpix's FM signal: the pieces as they go out (the room left in
    /// the last frame is filled with more repair pieces, so they're added here too).</summary>
    public SentFile AddFm(string shownName, byte[] data, int sourcePieces, FmSignalMode mode, ushort fileNumber, double leadInSeconds = 0.3)
    {
        int pieces = data.Length / mode.PieceBytes;
        int slots = FmSignalTransmitter.Slots(pieces, mode);
        if (slots > pieces) data = FountainFile.Extend(data, sourcePieces, mode.PieceBytes, slots - pieces);
        var file = new SentFile(shownName, shownName, data, new TransmitSettings(HamDrmMode.B, HamDrmBandwidth.Wide, 16, HamDrmInterleave.Long, HamDrmProtection.High), fileNumber, mode.PieceBytes, slots, DateTime.Now, leadInSeconds,
                                mode, sourcePieces);
        lock (_lock)
        {
            _files.RemoveAll(f => f.FmMode is not null && f.TransportId == file.TransportId);
            _files.Add(file);
            while (_files.Count > Keep) _files.RemoveAt(0);
        }
        return file;
    }

    /// <summary>The answer to a request for more pieces of a picture sent with RFpix's FM signal, or null if
    /// it isn't one sent from here.</summary>
    public FixPlan? Answer(FmRequest request)
    {
        SentFile? file;
        lock (_lock) file = _files.LastOrDefault(f => f.FmMode is not null && f.TransportId == request.FileNumber &&
                                                      f.FmSourcePieces == request.SourcePieces && f.PieceSize == request.PieceBytes);
        if (file is null || request.Needed <= 0) return null;
        var bsr = new BsrRequest(file.TransportId, file.PieceSize, Enumerable.Range(0, Math.Min(request.Needed, file.Pieces)).ToList(), file.ShownName);
        return FreshFm(file, FountainFile.FreshPiecesFor(request.Needed), bsr);
    }

    private static FixPlan FreshFm(SentFile file, int count, BsrRequest request)
    {
        // Whole frames: as many new repair pieces as fill the frames they need.
        count = FmSignalTransmitter.Slots(count, file.FmMode!);
        var data = FountainFile.Extend(file.Data, file.FmSourcePieces, file.PieceSize, count);
        var grown = file with { Data = data, Pieces = file.Pieces + count };
        return new FixPlan(grown, Enumerable.Range(file.Pieces, count).ToList(), request, Fresh: true);
    }

    /// <summary>The answer to a request, or null if it isn't for a picture sent from here.</summary>
    public FixPlan? Answer(BsrRequest request)
    {
        SentFile? file;
        lock (_lock) file = _files.LastOrDefault(f => f.FmMode is null && f.TransportId == request.TransportId);
        if (file is null) return null;
        // Another station's picture can share the number by chance: the piece size (when given)
        // must match, and the pieces asked for must be ones this picture has.
        if (request.PieceSize > 0 && request.PieceSize != file.PieceSize) return null;
        var pieces = request.Missing.Where(p => p >= 0 && p < file.Pieces).Distinct().Order().ToList();
        if (pieces.Count == 0 || pieces.Count < request.Missing.Count) return null;
        // A picture with RFpix's repair code: the request lists only as many pieces as the station still
        // needs. It's answered with that many new repair pieces (and a few spare), numbered on from the
        // last ones sent, rather than the pieces it lists.
        if (FountainFile.SourcePiecesOf(file.Name) is int k)
            return Fresh(file, k, FountainFile.FreshPiecesFor(pieces.Count), request, stations: 1);
        return new FixPlan(file, pieces, request);
    }

    /// <summary>Two answers for the same repair-coded picture made into one: enough new repair pieces for the
    /// station that needs the most covers the others too. Null if they can't be combined (another picture,
    /// or not new repair pieces).</summary>
    public static FixPlan? Combine(FixPlan a, FixPlan b)
    {
        if (!a.Fresh || !b.Fresh || a.File.TransportId != b.File.TransportId || a.File.Name != b.File.Name || a.File.FmMode != b.File.FmMode) return null;
        var bigger = a.Pieces.Count >= b.Pieces.Count ? a : b;
        return bigger with { Stations = a.Stations + b.Stations };
    }

    private static FixPlan Fresh(SentFile file, int k, int count, BsrRequest request, int stations)
    {
        var data = FountainFile.Extend(file.Data, k, file.PieceSize, count);
        var grown = file with { Data = data, Pieces = file.Pieces + count };
        return new FixPlan(grown, Enumerable.Range(file.Pieces, count).ToList(), request, Fresh: true, stations);
    }

    /// <summary>Call as an answer of new repair pieces goes out: the picture in the log now includes them,
    /// so the next answer's repair pieces are numbered on from these.</summary>
    public void Sending(FixPlan plan)
    {
        if (!plan.Fresh) return;
        lock (_lock)
        {
            int i = _files.FindIndex(f => f.TransportId == plan.File.TransportId && f.Name == plan.File.Name && f.FmMode == plan.File.FmMode);
            if (i >= 0 && _files[i].Pieces < plan.File.Pieces) _files[i] = plan.File;
        }
    }

    public void Clear()
    {
        lock (_lock) _files.Clear();
    }
}
