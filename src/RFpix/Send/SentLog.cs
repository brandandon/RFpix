// SentLog.cs - the last few pictures sent, so a request for missing pieces can be answered.
//
// When another station asks for missing pieces (a BSR, see HamDrm/Bsr.cs), the request
// names the picture only by its transport ID, a number made from its file name. RFpix
// looks that number up here, among the last 6 pictures it sent (as QSSTV keeps), and
// checks the request makes sense for that picture (the right piece size, pieces it has).
// Then it can send just those pieces again: a "fix".
//
// The log lives only while RFpix runs, as in QSSTV.

using RFpix.HamDrm;

namespace RFpix.Send;

/// <summary>A picture as it went out: the exact bytes, name and settings, so its pieces can be sent again.</summary>
/// <param name="Name">The name it went out under (e.g. "tower.rs2").</param>
/// <param name="ShownName">The picture's own name, for the screen (e.g. "tower.jp2").</param>
/// <param name="LeadInSeconds">The key-up lead-in it was sent with (a fix uses the same).</param>
public sealed record SentFile(string Name, string ShownName, byte[] Data, TransmitSettings Settings,
                              int TransportId, int PieceSize, int Pieces, DateTime Sent, double LeadInSeconds = 0.3);

/// <summary>What to send in answer to a request: which picture, and which of its pieces.</summary>
public sealed record FixPlan(SentFile File, IReadOnlyList<int> Pieces, BsrRequest Request)
{
    /// <summary>The fix's sound: only those pieces, with the picture's own settings.</summary>
    public HamDrmTransmitter Transmitter(string callsign) =>
        new(File.Name, File.Data, File.Settings, callsign, onlyPieces: Pieces);
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

    /// <summary>The answer to a request, or null if it isn't for a picture sent from here.</summary>
    public FixPlan? Answer(BsrRequest request)
    {
        SentFile? file;
        lock (_lock) file = _files.LastOrDefault(f => f.TransportId == request.TransportId);
        if (file is null) return null;
        // Another station's picture can share the number by chance: the piece size (when given)
        // must match, and the pieces asked for must be ones this picture has.
        if (request.PieceSize > 0 && request.PieceSize != file.PieceSize) return null;
        var pieces = request.Missing.Where(p => p >= 0 && p < file.Pieces).Distinct().Order().ToList();
        if (pieces.Count == 0 || pieces.Count < request.Missing.Count) return null;
        return new FixPlan(file, pieces, request);
    }

    public void Clear()
    {
        lock (_lock) _files.Clear();
    }
}
