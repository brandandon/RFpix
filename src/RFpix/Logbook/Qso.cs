// Qso.cs - one contact for the logbook (Log QSO on the TX/RX page), and how the Log QSO window
// is filled in to start with.

namespace RFpix.Logbook;

/// <summary>A contact, as logged. Text that's empty is left out of the log.</summary>
/// <param name="FrequencyMHz">The frequency (MHz), or null if not known.</param>
public sealed record Qso(
    string Call, string Name, DateTime StartUtc, double? FrequencyMHz, string Mode,
    string RstSent, string RstReceived, string Grid, string Comment, string MyCall, string MyGrid)
{
    /// <summary>The band for the frequency ("20m"), or null outside the ham bands.</summary>
    public string? Band => FrequencyMHz is double mhz ? Bands.For(mhz) : null;
}

/// <summary>What RFpix knows about the last picture received, for filling in Log QSO.</summary>
/// <param name="Detail">For the comment: the analog mode ("Scottie 2"), or "HamDRM".</param>
public sealed record LastPicture(string Call, DateTime ReceivedUtc, bool Digital, string Detail);

public static class QsoDraft
{
    /// <summary>The Log QSO window's boxes to start with. The call comes from the last picture
    /// received (if its sender is known), or else the Send tab's To box; the name from the Send
    /// tab's To name when that's the same station. The time is the last picture's (if it's
    /// theirs), or now. RST sent is the Send tab's RST box; RST received and their grid are left
    /// for you to fill in.</summary>
    public static Qso Make(LastPicture? last, string sendTo, string sendToName, string sendRst,
                           DateTime nowUtc, double? frequencyHz, string? myCall, string? myGrid)
    {
        sendTo = (sendTo ?? "").Trim().ToUpperInvariant();
        string call = last?.Call is { Length: > 0 } heard ? heard.Trim().ToUpperInvariant() : sendTo;
        string name = call.Length > 0 && call == sendTo ? (sendToName ?? "").Trim() : "";
        bool theirs = last is not null && last.Call.Trim().ToUpperInvariant() == call;
        string comment = theirs ? (last!.Digital ? "HamDRM" : last.Detail) : "";
        return new Qso(call, name, theirs ? last!.ReceivedUtc : nowUtc,
                       frequencyHz is double hz && hz > 0 ? hz / 1e6 : null, "SSTV",
                       (sendRst ?? "").Trim(), "", "", comment, (myCall ?? "").Trim().ToUpperInvariant(), (myGrid ?? "").Trim());
    }
}

/// <summary>The amateur bands, as the logbook format (ADIF) names them.</summary>
public static class Bands
{
    private static readonly (double From, double To, string Name)[] All =
    {
        (1.8, 2.0, "160m"), (3.5, 4.0, "80m"), (5.06, 5.45, "60m"), (7.0, 7.3, "40m"), (10.1, 10.15, "30m"),
        (14.0, 14.35, "20m"), (18.068, 18.168, "17m"), (21.0, 21.45, "15m"), (24.89, 24.99, "12m"),
        (28.0, 29.7, "10m"), (50.0, 54.0, "6m"), (144.0, 148.0, "2m"), (222.0, 225.0, "1.25m"),
        (420.0, 450.0, "70cm"), (902.0, 928.0, "33cm"), (1240.0, 1300.0, "23cm"),
    };

    public static string? For(double mhz) => All.FirstOrDefault(b => mhz >= b.From && mhz <= b.To).Name;
}
