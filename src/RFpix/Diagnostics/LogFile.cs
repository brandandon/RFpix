// LogFile.cs - one run's log: a text file of what RFpix did, line by line, for finding problems.
//
// Each time RFpix starts it begins a new file, named after the moment it started
// ("RFpix log 2026-10-30 21-55-12.txt"), so a restart after a crash never writes over the log
// of the run that crashed. Only the newest runs are kept (10 normally): older files are
// deleted as a new one begins.
//
// Every line goes to the disk as it's written (nothing waits in memory), so the last lines
// before a crash are there afterwards. A line looks like
//   2026-10-30 21:55:12.345  Radio    ERROR: Couldn't connect to the radio: ...
// (when, which part of RFpix, then what happened).
//
// A file never grows past its limit (5 MB): near the limit (the last half MB) only errors are
// still written, and at the limit nothing more. Logging never stops RFpix: if the file can't
// be written (a full disk, say), the line is dropped.

using System.IO.Compression;
using System.Text;

namespace RFpix.Diagnostics;

public sealed class LogFile : IDisposable
{
    public const string NameStart = "RFpix log ";
    public const string Extension = ".txt";

    /// <summary>The runs kept, this one included.</summary>
    public const int RunsKept = 10;

    /// <summary>The most a file grows to; past <see cref="ErrorsOnlyFrom"/> only errors are written.</summary>
    public const long MaxBytes = 5L * 1024 * 1024;
    public const long ErrorsOnlyFrom = MaxBytes - 512 * 1024;

    // Room kept at the end for the line saying the file is full.
    private const int FullNoteRoom = 200;

    private readonly object _lock = new();
    private readonly long _errorsOnlyFrom, _maxBytes;
    private StreamWriter? _writer;
    private long _bytes;
    private bool _saidErrorsOnly, _saidFull;

    /// <summary>The file being written.</summary>
    public string Path { get; }

    /// <summary>Begins a new log in <paramref name="folder"/>, deleting the oldest so that
    /// <paramref name="keep"/> runs remain with this one. Throws if the file can't be made.</summary>
    public LogFile(string folder, DateTime started, int keep = RunsKept,
                   long errorsOnlyFrom = ErrorsOnlyFrom, long maxBytes = MaxBytes)
    {
        _errorsOnlyFrom = errorsOnlyFrom;
        _maxBytes = maxBytes;
        Directory.CreateDirectory(folder);
        DeleteOldest(folder, keep - 1);

        string name = $"{NameStart}{started:yyyy-MM-dd HH-mm-ss}";
        string path = System.IO.Path.Combine(folder, name + Extension);
        for (int n = 2; File.Exists(path); n++) path = System.IO.Path.Combine(folder, $"{name} ({n}){Extension}");
        // Others may read it (and Save log for help copies it) while it's being written.
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
        Path = path;
    }

    /// <summary>The logs in a folder, newest first.</summary>
    public static IReadOnlyList<string> Files(string folder)
    {
        if (!Directory.Exists(folder)) return Array.Empty<string>();
        return Directory.GetFiles(folder, NameStart + "*" + Extension)
                        .OrderByDescending(f => System.IO.Path.GetFileName(f), StringComparer.Ordinal)
                        .ToList();
    }

    private static void DeleteOldest(string folder, int keep)
    {
        foreach (string old in Files(folder).Skip(Math.Max(0, keep)))
        {
            try { File.Delete(old); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* in use: next time */ }
        }
    }

    /// <summary>Writes one line (a message over several lines is indented after the first).</summary>
    public void Write(DateTime when, string area, string message, bool error)
    {
        string text = Line(when, area, message, error);
        int bytes = Encoding.UTF8.GetByteCount(text);
        lock (_lock)
        {
            if (_writer is null) return;
            if (!error && _bytes + bytes > _errorsOnlyFrom)
            {
                if (!_saidErrorsOnly)
                {
                    _saidErrorsOnly = true;
                    Append(Line(when, "Log", "This log is nearly full: from here on, only errors are written.", false), force: false);
                }
                return;
            }
            Append(text, force: false);
        }
    }

    // (Called holding the lock.) Writes the text if it fits; the first time something doesn't,
    // says the file is full, in the room kept for that.
    private void Append(string text, bool force)
    {
        int bytes = Encoding.UTF8.GetByteCount(text);
        if (!force && _bytes + bytes > _maxBytes - FullNoteRoom)
        {
            if (_saidFull) return;
            _saidFull = true;
            Append(Line(DateTime.Now, "Log", "This log is full: nothing more is written.", false), force: true);
            return;
        }
        try
        {
            _writer!.Write(text);
            _bytes += bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // A full disk, say: the line is lost, RFpix carries on.
        }
    }

    /// <summary>How a line is written: when, the part of RFpix, then the message.</summary>
    public static string Line(DateTime when, string area, string message, bool error)
    {
        string[] lines = message.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        var text = new StringBuilder();
        text.Append(when.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture))
            .Append("  ").Append(area.PadRight(8)).Append(' ');
        if (error) text.Append("ERROR: ");
        text.Append(lines[0]).Append(Environment.NewLine);
        for (int i = 1; i < lines.Length; i++) text.Append("        ").Append(lines[i]).Append(Environment.NewLine);
        return text.ToString();
    }

    /// <summary>Puts the logs in <paramref name="logFolder"/> (the one being written too) into one
    /// zip file in <paramref name="toFolder"/>, for sending to someone helping. Returns its path.</summary>
    public static string Zip(string logFolder, string toFolder, DateTime now)
    {
        Directory.CreateDirectory(toFolder);
        string name = $"RFpix log {now:yyyy-MM-dd HHmm}";
        string path = System.IO.Path.Combine(toFolder, name + ".zip");
        for (int n = 2; File.Exists(path); n++) path = System.IO.Path.Combine(toFolder, $"{name} ({n}).zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (string file in Files(logFolder))
        {
            var entry = zip.CreateEntry(System.IO.Path.GetFileName(file), CompressionLevel.Optimal);
            entry.LastWriteTime = File.GetLastWriteTime(file);
            using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var target = entry.Open();
            source.CopyTo(target);
        }
        return path;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
