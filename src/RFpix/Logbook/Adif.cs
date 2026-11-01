// Adif.cs - contacts written in ADIF, the logbook format every logging program reads
// (adif.org). Each field is <name:length>value; a contact ends with <eor>. RFpix's own log file
// and the contacts sent to a logging program (HRD Logbook, say) are both written this way.

using System.Globalization;
using System.Text;

namespace RFpix.Logbook;

public static class Adif
{
    public const string Version = "3.1.4";

    /// <summary>The file's header, before the contacts.</summary>
    public static string Header(string programVersion) =>
        $"RFpix log of contacts (ADIF {Version}): import it into any logging program.\n" +
        Field("adif_ver", Version) + Field("programid", "RFpix") + Field("programversion", programVersion) + "<eoh>\n";

    /// <summary>One contact, as one line ending with &lt;eor&gt;. Empty fields are left out.</summary>
    public static string Record(Qso q)
    {
        var s = new StringBuilder();
        s.Append(Field("call", q.Call));
        s.Append(Field("name", q.Name));
        s.Append(Field("qso_date", q.StartUtc.ToString("yyyyMMdd", CultureInfo.InvariantCulture)));
        s.Append(Field("time_on", q.StartUtc.ToString("HHmmss", CultureInfo.InvariantCulture)));
        if (q.Band is { } band) s.Append(Field("band", band));
        if (q.FrequencyMHz is double mhz) s.Append(Field("freq", mhz.ToString("0.000000", CultureInfo.InvariantCulture)));
        s.Append(Field("mode", q.Mode));
        s.Append(Field("rst_sent", q.RstSent));
        s.Append(Field("rst_rcvd", q.RstReceived));
        s.Append(Field("gridsquare", q.Grid));
        s.Append(Field("comment", q.Comment));
        s.Append(Field("station_callsign", q.MyCall));
        s.Append(Field("operator", q.MyCall));
        s.Append(Field("my_gridsquare", q.MyGrid));
        s.Append("<eor>\n");
        return s.ToString();
    }

    /// <summary>"&lt;call:5&gt;N3CHX " (nothing if the value is empty). ADIF files are plain ASCII,
    /// so accents are dropped (é → e) and other characters become "?".</summary>
    public static string Field(string name, string? value)
    {
        string v = Ascii(value?.Trim() ?? "");
        return v.Length == 0 ? "" : $"<{name}:{v.Length}>{v} ";
    }

    // Common accented letters, and the plain letter written for each. (A table rather than .NET's
    // Normalize, which needs language data the Linux version runs without.)
    private const string Accented = "ÀÁÂÃÄÅàáâãäåÇçÈÉÊËèéêëÌÍÎÏìíîïÑñÒÓÔÕÖØòóôõöøÙÚÛÜùúûüÝýÿŠšŽžŁłŚśŹźŻżĆćŃńĘęĄąŐőŰűČčŘřĚěŮů";
    private const string Plain    = "AAAAAAaaaaaaCcEEEEeeeeIIIIiiiiNnOOOOOOooooooUUUUuuuuYyySsZzLlSsZzZzCcNnEeAaOoUuCcRrEeUu";

    private static string Ascii(string text)
    {
        var s = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (char.IsLowSurrogate(c)) continue;   // the second half of a symbol like an emoji: one "?" for it
            int accented = Accented.IndexOf(c);
            if (accented >= 0) s.Append(Plain[accented]);
            else if (c is 'ß') s.Append("ss");
            else s.Append(c >= 32 && c < 127 ? c : c is '\r' or '\n' or '\t' ? ' ' : '?');
        }
        return s.ToString();
    }
}
