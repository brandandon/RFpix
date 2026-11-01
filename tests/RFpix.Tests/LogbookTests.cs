// LogbookTests.cs - Log QSO: contacts written in ADIF, saved in RFpix's log file, and sent to a
// logging program over UDP (as WSJT-X sends them to HRD Logbook); and how the Log QSO window is
// filled in.

using System.Net;
using System.Net.Sockets;
using System.Text;
using RFpix.Logbook;
using RFpix.Views;
using Xunit;

namespace RFpix.Tests;

public sealed class LogbookTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rfpix-logbook-" + Guid.NewGuid().ToString("N"));
    private string File1 => Path.Combine(_folder, "contacts.adi");

    public void Dispose() { if (Directory.Exists(_folder)) Directory.Delete(_folder, true); }

    private static Qso Sample() => new("N3CHX", "Bob", new DateTime(2026, 11, 1, 18, 10, 30, DateTimeKind.Utc), 14.230,
                                       "SSTV", "595", "57", "FN20", "Scottie 2", "N0CALL", "AA00aa");

    [Fact]
    public void A_contact_is_written_in_ADIF()
    {
        Assert.Equal("<call:5>N3CHX <name:3>Bob <qso_date:8>20261101 <time_on:6>181030 <band:3>20m <freq:9>14.230000 " +
                     "<mode:4>SSTV <rst_sent:3>595 <rst_rcvd:2>57 <gridsquare:4>FN20 <comment:9>Scottie 2 " +
                     "<station_callsign:6>N0CALL <operator:6>N0CALL <my_gridsquare:6>AA00aa <eor>\n", Adif.Record(Sample()));
        // Empty boxes are left out; no frequency, no band.
        var bare = Sample() with { Name = "", FrequencyMHz = null, RstReceived = "", Grid = "", Comment = "" };
        string record = Adif.Record(bare);
        Assert.DoesNotContain("<name", record);
        Assert.DoesNotContain("<band", record);
        Assert.DoesNotContain("<freq", record);
        Assert.DoesNotContain("<rst_rcvd", record);
        Assert.Contains("<rst_sent:3>595", record);
    }

    [Fact]
    public void ADIF_text_is_plain_ASCII()
    {
        Assert.Equal("<name:4>Rene ", Adif.Field("name", "René"));
        Assert.Equal("<comment:9>nice ? 73 ", Adif.Field("comment", "nice 📻 73"));   // a symbol: one "?"
        Assert.Equal("", Adif.Field("comment", "  "));
    }

    [Theory]
    [InlineData(14.230, "20m")]
    [InlineData(7.171, "40m")]
    [InlineData(3.845, "80m")]
    [InlineData(28.680, "10m")]
    [InlineData(145.500, "2m")]
    [InlineData(433.400, "70cm")]
    [InlineData(13.0, null)]
    public void The_band_comes_from_the_frequency(double mhz, string? band) => Assert.Equal(band, Bands.For(mhz));

    [Fact]
    public void The_file_starts_with_a_header_once_and_can_be_cleared()
    {
        Assert.Equal(0, ContactLog.Count(File1));
        ContactLog.Append(File1, Adif.Record(Sample()));
        ContactLog.Append(File1, Adif.Record(Sample() with { Call = "W9BED" }));
        string text = File.ReadAllText(File1);
        Assert.Equal(1, Count(text, "<eoh>"));
        Assert.True(text.IndexOf("<eoh>", StringComparison.Ordinal) < text.IndexOf("<call:", StringComparison.Ordinal));
        Assert.Contains("<call:5>W9BED", text);
        Assert.Equal(2, ContactLog.Count(File1));
        ContactLog.Clear(File1);
        Assert.Equal(0, ContactLog.Count(File1));
        ContactLog.Append(File1, Adif.Record(Sample()));   // a new header after clearing
        Assert.Equal(1, Count(File.ReadAllText(File1), "<eoh>"));
    }

    private static int Count(string text, string what)
    {
        int n = 0;
        for (int at = text.IndexOf(what, StringComparison.Ordinal); at >= 0; at = text.IndexOf(what, at + 1, StringComparison.Ordinal)) n++;
        return n;
    }

    [Fact]
    public void A_logged_contact_is_saved_and_sent_to_the_logging_program()
    {
        // A pretend logging program listening on this computer, as HRD Logbook does.
        using var logger = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)logger.Client.LocalEndPoint!).Port;
        var result = ContactLog.Log(Sample(), new LoggingSettings { Address = "127.0.0.1", Port = port }, File1);
        Assert.True(result.Saved);
        Assert.True(result.Sent);
        var arrived = logger.ReceiveAsync();
        Assert.True(arrived.Wait(TimeSpan.FromSeconds(5)), "nothing arrived");
        string sent = Encoding.UTF8.GetString(arrived.Result.Buffer);
        Assert.Contains("<programid:5>RFpix", sent);
        Assert.Contains("<eoh>", sent);
        Assert.Contains("<call:5>N3CHX", sent);
        Assert.EndsWith("<eor>\n", sent);
        Assert.Equal(1, ContactLog.Count(File1));
    }

    [Fact]
    public void Sending_can_be_off_and_a_bad_port_is_reported_but_the_contact_is_still_saved()
    {
        var off = ContactLog.Log(Sample(), new LoggingSettings { SendToLogger = false }, File1);
        Assert.True(off.Saved);
        Assert.False(off.Sent);
        Assert.Null(off.SendProblem);
        var bad = ContactLog.Log(Sample(), new LoggingSettings { Port = 0 }, File1);
        Assert.True(bad.Saved);
        Assert.False(bad.Sent);
        Assert.NotNull(bad.SendProblem);
        Assert.Equal(2, ContactLog.Count(File1));
    }

    [Fact]
    public void Log_QSO_is_filled_in_from_the_last_picture_and_the_Send_tab()
    {
        var now = new DateTime(2026, 11, 1, 18, 30, 0, DateTimeKind.Utc);
        var heard = new LastPicture("n3chx", new DateTime(2026, 11, 1, 18, 10, 0, DateTimeKind.Utc), Digital: false, "Scottie 2");

        // From the last picture: their call, its time, its mode in the comment; the name only if
        // the Send tab's To is the same station.
        var q = QsoDraft.Make(heard, "N3CHX", "Bob", "595", now, 14_230_000, "n0call", "AA00aa");
        Assert.Equal("N3CHX", q.Call);
        Assert.Equal("Bob", q.Name);
        Assert.Equal(heard.ReceivedUtc, q.StartUtc);
        Assert.Equal(14.23, q.FrequencyMHz!.Value, 6);
        Assert.Equal("20m", q.Band);
        Assert.Equal("SSTV", q.Mode);
        Assert.Equal("595", q.RstSent);
        Assert.Equal("", q.RstReceived);
        Assert.Equal("Scottie 2", q.Comment);
        Assert.Equal("N0CALL", q.MyCall);
        Assert.Equal("", QsoDraft.Make(heard, "W9BED", "Ann", "595", now, null, "N0CALL", "").Name);   // To is someone else

        // A digital picture: "HamDRM" in the comment.
        Assert.Equal("HamDRM", QsoDraft.Make(heard with { Digital = true }, "", "", "", now, null, "", "").Comment);

        // Nothing received: the Send tab's To, and now.
        var fromSend = QsoDraft.Make(null, "w9bed", "Ann", "59", now, null, "N0CALL", "");
        Assert.Equal("W9BED", fromSend.Call);
        Assert.Equal("Ann", fromSend.Name);
        Assert.Equal(now, fromSend.StartUtc);
        Assert.Null(fromSend.FrequencyMHz);
        Assert.Equal("", fromSend.Comment);
    }

    [Theory]
    [InlineData("14.23", 14.23)]
    [InlineData("14.230000", 14.23)]
    [InlineData("14,230", 14.23)]
    [InlineData("14.230.000", 14.23)]
    [InlineData("145.500", 145.5)]
    public void Frequencies_are_read_as_typed(string text, double mhz) =>
        Assert.Equal(mhz, LogQsoWindow.ParseFrequency(text)!.Value, 6);

    [Theory]
    [InlineData("N3CHX", true)]
    [InlineData("KD9DD/P", true)]
    [InlineData("VE3/N0CALL", true)]
    [InlineData("AB", false)]
    [InlineData("HELLO", false)]
    [InlineData("N0 CALL", false)]
    public void Calls_are_checked(string call, bool ok) => Assert.Equal(ok, LogQsoWindow.IsCall(call));
}
