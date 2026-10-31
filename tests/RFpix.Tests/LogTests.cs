// LogTests.cs - checks RFpix's log (Diagnostics): one file per run, only the last 10 kept,
// lines on the disk as they're written, the size limit, the zip for help, the settings summary
// (and that it never holds the FTP login), and how Hamlib's lines are put together and kept.
// (Hamlib's real messages reaching the log: RadioTests, with Hamlib's pretend radio.)

using System.IO.Compression;
using RFpix.Diagnostics;
using RFpix.HamDrm;
using RFpix.Radio;
using RFpix.Settings;
using RFpix.Upload;
using Xunit;

namespace RFpix.Tests;

public sealed class LogTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "RFpix log test " + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (Exception) { }
    }

    // Reads a log while it's still open for writing, as Notepad or Save log for help would.
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static readonly DateTime Started = new(2026, 10, 30, 21, 55, 12);

    [Fact]
    public void A_log_is_named_by_when_RFpix_started_and_each_line_is_on_the_disk_at_once()
    {
        using var log = new LogFile(_folder, Started);
        Assert.Equal("RFpix log 2026-10-30 21-55-12.txt", Path.GetFileName(log.Path));

        log.Write(Started.AddSeconds(1), "Radio", "Connecting.", error: false);
        // Still open (as if RFpix had then crashed): the line is there already.
        Assert.Contains("2026-10-30 21:55:13.000  Radio    Connecting.", ReadShared(log.Path));
    }

    [Fact]
    public void Lines_show_when_where_and_errors_and_carry_on_indented()
    {
        string line = LogFile.Line(Started, "Send", "It went wrong:\nfirst detail\nsecond detail", error: true);
        string[] lines = line.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.Equal("2026-10-30 21:55:12.000  Send     ERROR: It went wrong:", lines[0]);
        Assert.Equal("        first detail", lines[1]);
        Assert.Equal("        second detail", lines[2]);
    }

    [Fact]
    public void Only_the_last_ten_runs_are_kept()
    {
        Directory.CreateDirectory(_folder);
        for (int day = 1; day <= 12; day++)
            File.WriteAllText(Path.Combine(_folder, $"RFpix log 2026-10-{day:00} 08-00-00.txt"), "an old run");
        File.WriteAllText(Path.Combine(_folder, "notes.txt"), "not a log: left alone");

        using var log = new LogFile(_folder, Started);

        var kept = LogFile.Files(_folder);
        Assert.Equal(LogFile.RunsKept, kept.Count);
        Assert.Equal(log.Path, kept[0]);   // newest first
        Assert.DoesNotContain(kept, f => f.Contains("2026-10-03"));   // the oldest three are gone
        Assert.Contains(kept, f => f.Contains("2026-10-04"));
        Assert.True(File.Exists(Path.Combine(_folder, "notes.txt")));
    }

    [Fact]
    public void Starting_twice_in_the_same_second_makes_two_files()
    {
        using var first = new LogFile(_folder, Started);
        using var second = new LogFile(_folder, Started);
        Assert.NotEqual(first.Path, second.Path);
        Assert.Equal("RFpix log 2026-10-30 21-55-12 (2).txt", Path.GetFileName(second.Path));
    }

    [Fact]
    public void Near_its_limit_a_log_keeps_only_errors_and_it_never_grows_past_it()
    {
        string path;
        using (var log = new LogFile(_folder, Started, errorsOnlyFrom: 3000, maxBytes: 6000))
        {
            path = log.Path;
            for (int i = 0; i < 100; i++) log.Write(Started, "Receive", $"Routine line {i}", error: false);
            log.Write(Started, "Radio", "An error near the limit", error: true);
            for (int i = 0; i < 200; i++) log.Write(Started, "Radio", $"Error {i}", error: true);
        }
        string text = File.ReadAllText(path);
        Assert.True(new FileInfo(path).Length <= 6000, $"{new FileInfo(path).Length} bytes");
        Assert.Contains("only errors are written", text);
        Assert.Contains("An error near the limit", text);
        Assert.DoesNotContain("Routine line 99", text);
        Assert.Contains("This log is full", text);
    }

    [Fact]
    public void Save_log_for_help_zips_every_log_including_the_one_being_written()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "RFpix log 2026-10-29 19-00-00.txt"), "yesterday's run");
        using var log = new LogFile(_folder, Started);
        log.Write(Started, "Radio", "Lost contact with the radio.", error: true);

        string desktop = Path.Combine(_folder, "Desktop");
        string zip = LogFile.Zip(_folder, desktop, Started);
        Assert.Equal("RFpix log 2026-10-30 2155.zip", Path.GetFileName(zip));

        using var archive = ZipFile.OpenRead(zip);
        Assert.Equal(2, archive.Entries.Count);
        using var reader = new StreamReader(archive.GetEntry(Path.GetFileName(log.Path))!.Open());
        Assert.Contains("Lost contact with the radio.", reader.ReadToEnd());

        // A second one the same minute doesn't replace the first.
        Assert.Equal("RFpix log 2026-10-30 2155 (2).zip", Path.GetFileName(LogFile.Zip(_folder, desktop, Started)));
    }

    // ---- the settings at the top of each log ----

    private static AppSettings Settings() => new()
    {
        Callsign = "N0CALL",
        GridSquare = "AA00aa",
        Radio = new RadioSettings { Choice = "ft991a", Port = "COM4", BaudRate = 38400, Ptt = PttMethod.Cat },
        InputDeviceId = "in-1",
        OutputDeviceId = "out-9",
        Upload = new UploadSettings
        {
            SaveCopies = true, Upload = true, Protocol = "SFTP",
            Host = "ftp.example.org", Username = "user77", ProtectedPassword = "aes:secretpw",
        },
    };

    private static IReadOnlyList<string> Summary(AppSettings s) => LogSummary.Lines(s,
        id => id == "in-1" ? "USB Audio CODEC" : null,
        id => null,   // the output chosen isn't plugged in
        port => port == "COM4" ? "Silicon Labs Dual CP2105 USB to UART Bridge: Enhanced COM Port" : null,
        number => number == 3073 ? "Icom IC-7300" : null,
        "/home/n0call/Documents/RFpix");

    [Fact]
    public void The_settings_summary_names_the_radio_port_and_sound_devices()
    {
        string text = string.Join("\n", Summary(Settings()));
        Assert.Contains("Callsign: N0CALL", text);
        Assert.Contains("Radio: Yaesu FT-991A (Hamlib model 1035), port COM4 (Silicon Labs Dual CP2105 USB to UART Bridge: Enhanced COM Port), " +
                        "38400 baud, PTT by CAT command", text);
        Assert.Contains("Sound input (receive): USB Audio CODEC", text);
        Assert.Contains("Sound output (send): (the one chosen isn't connected now)", text);
        Assert.Contains("uploading: on, by SFTP", text);
        Assert.Contains("Analog pictures without their VIS code: kept in the Gallery from 50%, uploaded from 50%", text);
        var off = Settings();
        off.KeepNoVis = false;
        off.Upload.NoVisMinPercent = 75;
        Assert.Contains("Analog pictures without their VIS code: not kept in the Gallery, uploaded from 75%",
            string.Join("\n", Summary(off)));
    }

    [Fact]
    public void The_settings_summary_never_holds_the_FTP_login()
    {
        string text = string.Join("\n", Summary(Settings()));
        Assert.DoesNotContain("user77", text);
        Assert.DoesNotContain("secretpw", text);
        Assert.DoesNotContain("ftp.example.org", text);
    }

    [Fact]
    public void Each_kind_of_radio_connection_is_described()
    {
        Assert.Equal("Radio: Icom IC-9700 (Hamlib model 3081), port COM5, 19200 baud, PTT by RTS, CI-V address A2, data mode while transmitting",
            LogSummary.Radio(new RadioSettings { Choice = "ic9700", Port = "COM5", BaudRate = 19200, Ptt = PttMethod.Rts, CivAddress = "A2", DataModeForTransmit = true }, null, null));
        Assert.Equal("Radio: Icom IC-7300 (Hamlib model 3073), port /dev/ttyUSB0, 115200 baud, PTT by CAT command",
            LogSummary.Radio(new RadioSettings { Choice = "other", OtherModel = 3073, Port = "/dev/ttyUSB0", BaudRate = 115200 }, null, "Icom IC-7300"));
        Assert.Equal("Radio: Shared through flrig, at localhost:12345, PTT by CAT command",
            LogSummary.Radio(new RadioSettings { Choice = "flrig", Port = "localhost:12345" }, null, null));
        Assert.Equal("Radio: No radio control (VOX only)", LogSummary.Radio(new RadioSettings { Choice = "none" }, null, null));
    }

    [Fact]
    public void A_HamDRM_transmission_is_summed_up_in_one_line()
    {
        var report = new TransmissionReport(Started, "tower.jp2", null, 80, 120, "N0CALL", HamDrmMode.B, null,
                                            new[] { 17.0, 18.0, 19.0 }, 300, 2);
        Assert.Equal("HamDRM transmission ended: mode B, from N0CALL, \"tower.jp2\": 80 of 120 pieces, quality 18 dB, 300 packets good, 2 bad",
                     LogSummary.Transmission(report));
    }

    // ---- Hamlib's lines ----

    private sealed class Captured
    {
        public DateTime Now = Started;
        public readonly List<string> Written = new();
        public HamlibDetail Detail(int remember) =>
            new(remember, () => Now, lines => Written.AddRange(lines.Select(l => l.Text)));
    }

    [Fact]
    public void Hamlib_lines_that_come_in_pieces_are_joined_and_only_the_last_few_remembered()
    {
        var captured = new Captured();
        var detail = captured.Detail(remember: 3);
        detail.Add("rig_open: ");
        detail.Add("called\n");
        detail.Add("write_block(): TX 5 bytes\r\n0000\t46 41 3b\n\nread_string(): RX 0 bytes\n");
        Assert.Empty(captured.Written);   // not connecting, no error: only remembered

        detail.WriteRecent();   // something failed
        Assert.Equal(new[] { "write_block(): TX 5 bytes", "0000\t46 41 3b", "read_string(): RX 0 bytes" }, captured.Written);

        detail.WriteRecent();   // nothing new since
        Assert.Equal(3, captured.Written.Count);
    }

    [Fact]
    public void While_connecting_Hamlib_lines_are_written_as_they_come()
    {
        var captured = new Captured();
        var detail = captured.Detail(remember: 400);
        detail.Add("from before (the list of radios)\n");
        detail.WriteFor(TimeSpan.FromSeconds(15));
        captured.Now = Started.AddSeconds(5);
        detail.Add("rig_open called\n");
        Assert.Equal(new[] { "rig_open called" }, captured.Written);

        captured.Now = Started.AddSeconds(20);   // connected a while ago: back to remembering only
        detail.Add("rig_get_freq called\n");
        Assert.Single(captured.Written);
        detail.WriteRecent();
        Assert.Equal(new[] { "rig_open called", "rig_get_freq called" }, captured.Written);   // not what came before
    }
}
