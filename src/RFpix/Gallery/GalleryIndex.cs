// GalleryIndex.cs - what the Gallery knows about each picture, beyond the picture itself.
//
// Pictures are ordinary files in the Received and Sent folders, so they can be copied,
// opened or deleted outside RFpix. Alongside them, each folder keeps a small hidden file,
// ".RFpix gallery.json", with what the file itself can't tell: who sent it, when, the
// settings it came with, the signal quality, and whether a fix (BSR) completed it.
// A picture with no entry there (copied in by hand, or saved before the Gallery existed)
// still shows: its date, time and callsign are read from its name where RFpix gave it
// one ("2026-10-20 1942 N0ABC tower.jp2"), or else the file's own date is used.

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RFpix.Gallery;

/// <summary>What's known about one picture in the gallery.</summary>
/// <param name="File">The file's name in its folder.</param>
/// <param name="Name">The picture's own name as sent (e.g. "tower.jp2").</param>
/// <param name="Callsign">Who sent it (for a sent picture: you).</param>
/// <param name="Settings">How it was sent, e.g. "Mode B · 2.5 kHz · 16-QAM · long interleave · high protection".</param>
/// <param name="ReedSolomon">0 for none, 1-4 for RS1-RS4; null if not known.</param>
/// <param name="QualityDb">The signal quality while it arrived.</param>
/// <param name="RepairedPieces">Pieces Reed-Solomon rebuilt.</param>
/// <param name="CompletedByBsr">True if pieces were missing and a fix (asked for with BSR) brought them.</param>
public sealed record GalleryEntry(string File, DateTime When, string? Name = null, string? Callsign = null,
                                  string? Settings = null, int? ReedSolomon = null, double? QualityDb = null,
                                  int RepairedPieces = 0, bool CompletedByBsr = false)
{
    /// <summary>The folder it's in (set when listed; not saved).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string? Folder { get; init; }

    /// <summary>Where the picture is (not saved: the file's name and its folder say).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string FullPath => System.IO.Path.Combine(Folder ?? "", File);

    /// <summary>An analog SSTV picture (else HamDRM): it's in an Analog folder, or was noted as analog
    /// (the pictures from before they were kept apart), or has the name RFpix gives analog pictures.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsAnalog =>
        System.IO.Path.GetFileName((Folder ?? "").TrimEnd('/', '\\')).Equals(RFpix.Receive.ReceivedFileSaver.AnalogSubfolder, StringComparison.OrdinalIgnoreCase)
        || Settings?.StartsWith("Analog SSTV", StringComparison.Ordinal) == true
        || Settings is null && Name is { } n && n.StartsWith("SSTV ", StringComparison.Ordinal) && n.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
}

public static class GalleryIndex
{
    /// <summary>The hidden file in each folder. The dot hides it on the Mac and Linux; on Windows
    /// it's given the Hidden attribute too.</summary>
    public const string IndexFileName = ".RFpix gallery.json";

    /// <summary>Its name before v0.6.0 (not hidden): renamed and hidden the first time it's read.</summary>
    public const string OldIndexFileName = "RFpix gallery.json";

    private static readonly HashSet<string> PictureExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".jp2", ".j2k", ".jpc", ".avif", ".png", ".bmp", ".gif", ".webp" };

    private static readonly object Lock = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    // A virus scanner or backup program can have the file open for a moment: try again, a few times.
    private const int Attempts = 5;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);


    /// <summary>Is this a picture file the Gallery shows?</summary>
    public static bool IsPicture(string path) => PictureExtensions.Contains(Path.GetExtension(path));

    /// <summary>Notes a picture just saved in <paramref name="folder"/>.</summary>
    public static void Add(string folder, GalleryEntry entry)
    {
        lock (Lock)
        {
            if (ReadForChange(folder) is not { } entries) return;
            entries[entry.File] = entry;
            Write(folder, entries);
        }
    }

    /// <summary>Forgets a picture (after it's deleted).</summary>
    public static void Remove(string folder, string file)
    {
        lock (Lock)
        {
            if (ReadForChange(folder) is { } entries && entries.Remove(file)) Write(folder, entries);
        }
    }

    /// <summary>What the folder's Gallery file says (no pictures looked at).</summary>
    internal static List<GalleryEntry> Entries(string folder)
    {
        lock (Lock) return Read(folder).Values.ToList();
    }

    /// <summary>Adds entries from another folder's Gallery file (pictures moved here), with any
    /// pictures given new names on the way (<paramref name="renamed"/>: old name to new). An entry
    /// already here for the same name is kept.</summary>
    internal static void MergeInto(string folder, IEnumerable<GalleryEntry> moved, IReadOnlyDictionary<string, string> renamed)
    {
        lock (Lock)
        {
            if (ReadForChange(folder) is not { } entries) return;
            foreach (var entry in moved)
            {
                string file = renamed.TryGetValue(entry.File, out var newName) ? newName : entry.File;
                if (renamed.ContainsKey(entry.File) || !entries.ContainsKey(file)) entries[file] = entry with { File = file };
            }
            Write(folder, entries);
        }
    }

    /// <summary>Every picture in the folder, the newest first, with what's known about it.</summary>
    public static List<GalleryEntry> List(string folder)
    {
        Dictionary<string, GalleryEntry> entries;
        lock (Lock) entries = Read(folder);
        var list = new List<GalleryEntry>();
        if (!Directory.Exists(folder)) return list;
        foreach (var path in Directory.EnumerateFiles(folder))
        {
            if (!IsPicture(path)) continue;
            string file = Path.GetFileName(path);
            var entry = entries.TryGetValue(file, out var e) ? e : FromName(file, File.GetLastWriteTime(path));
            list.Add(entry with { Folder = folder });
        }
        return list.OrderByDescending(e => e.When).ThenByDescending(e => e.File, StringComparer.Ordinal).ToList();
    }

    /// <summary>Every picture in all these folders, the newest first.</summary>
    public static List<GalleryEntry> List(IEnumerable<string> folders) =>
        folders.SelectMany(List).OrderByDescending(e => e.When).ThenByDescending(e => e.File, StringComparer.Ordinal).ToList();

    // RFpix's own names: "2026-10-20 1942 N0ABC tower.jp2", "2026-10-20 1942 tower.jp2", maybe " (2)".
    private static readonly Regex SavedName =
        new(@"^(?<date>\d{4}-\d{2}-\d{2}) (?<time>\d{4}) (?:(?<call>[A-Z0-9/_]{3,12}) )?(?<name>.+?)(?: \(\d+\))?(?<ext>\.[^.]+)$");

    /// <summary>What a file's name says about it, for a picture with no entry.</summary>
    public static GalleryEntry FromName(string file, DateTime fileTime)
    {
        var m = SavedName.Match(file);
        if (m.Success && DateTime.TryParseExact(m.Groups["date"].Value + " " + m.Groups["time"].Value, "yyyy-MM-dd HHmm",
                                                CultureInfo.InvariantCulture, DateTimeStyles.None, out var when))
        {
            string? call = m.Groups["call"].Success && m.Groups["call"].Value.Any(char.IsDigit) ? m.Groups["call"].Value : null;
            string name = call is null && m.Groups["call"].Success
                ? m.Groups["call"].Value + " " + m.Groups["name"].Value   // not a callsign: part of the name
                : m.Groups["name"].Value;
            return new GalleryEntry(file, when, name + m.Groups["ext"].Value, call);
        }
        return new GalleryEntry(file, fileTime, file);
    }

    /// <summary>A name to save a sent picture under: "2026-10-20 1942 tower.jp2", numbered if taken.</summary>
    public static string SaveSent(string folder, string name, byte[] data, DateTime when)
    {
        Directory.CreateDirectory(folder);
        string file = $"{when:yyyy-MM-dd HHmm} {name}";
        string path = Path.Combine(folder, file);
        string stem = Path.GetFileNameWithoutExtension(file), ext = Path.GetExtension(file);
        for (int n = 2; System.IO.File.Exists(path); n++) path = Path.Combine(folder, $"{stem} ({n}){ext}");
        System.IO.File.WriteAllBytes(path, data);
        return path;
    }

    /// <summary>Where the folder's hidden file is. An older visible one is renamed and hidden first.</summary>
    internal static string IndexPath(string folder)
    {
        string path = Path.Combine(folder, IndexFileName), old = Path.Combine(folder, OldIndexFileName);
        try
        {
            if (!System.IO.File.Exists(path) && System.IO.File.Exists(old))
            {
                System.IO.File.Move(old, path);
                Hide(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }   // read-only folder: leave it
        return path;
    }

    /// <summary>Hides the file on Windows (elsewhere its leading dot already does).</summary>
    internal static void Hide(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        try { System.IO.File.SetAttributes(path, System.IO.File.GetAttributes(path) | FileAttributes.Hidden); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Writes the folder's entries to its hidden file (replacing it safely).</summary>
    internal static void WriteFile(string path, IEnumerable<GalleryEntry> entries)
    {
        System.IO.File.WriteAllText(path + ".new", JsonSerializer.Serialize(entries.ToList(), Json));
        // Windows won't always replace a hidden file: show it for the moment of replacing.
        if (OperatingSystem.IsWindows() && System.IO.File.Exists(path))
            System.IO.File.SetAttributes(path, System.IO.File.GetAttributes(path) & ~(FileAttributes.Hidden | FileAttributes.ReadOnly));
        System.IO.File.Move(path + ".new", path, overwrite: true);
        Hide(path);
    }

    /// <summary>For listing: what the file says (nothing, if it's damaged or can't be read; the
    /// pictures still show).</summary>
    private static Dictionary<string, GalleryEntry> Read(string folder) => TryRead(folder) ?? new();

    /// <summary>For adding or removing: the same, but null if the file is there and couldn't be read
    /// even after trying again. Then it isn't written either: writing it from nothing would lose the
    /// details of every other picture in the folder.</summary>
    private static Dictionary<string, GalleryEntry>? ReadForChange(string folder)
    {
        var entries = TryRead(folder);
        if (entries is null)
            RFpix.Diagnostics.AppLog.Error("Gallery", $"Couldn't read the Gallery's file in {folder} (another program has it open?), " +
                                                      "so this change to it was left out. The pictures themselves are fine.");
        return entries;
    }

    private static Dictionary<string, GalleryEntry>? TryRead(string folder)
    {
        string path = IndexPath(folder);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                if (!System.IO.File.Exists(path)) return new();
                var list = JsonSerializer.Deserialize<List<GalleryEntry>>(System.IO.File.ReadAllText(path), Json) ?? new();
                return list.Where(e => !string.IsNullOrEmpty(e.File)).GroupBy(e => e.File).ToDictionary(g => g.Key, g => g.Last());
            }
            catch (JsonException)
            {
                return new();   // damaged: only the extra details are lost; the pictures still show
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == Attempts) return null;
                Thread.Sleep(RetryDelay);
            }
        }
    }

    private static void Write(string folder, Dictionary<string, GalleryEntry> entries)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Directory.CreateDirectory(folder);
                // Entries for pictures deleted outside RFpix are dropped as the index is written.
                var kept = entries.Values.Where(e => System.IO.File.Exists(Path.Combine(folder, e.File))).OrderBy(e => e.When).ToList();
                WriteFile(IndexPath(folder), kept);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt < Attempts) { Thread.Sleep(RetryDelay); continue; }
                // Details are a nicety: the pictures themselves are saved already.
                RFpix.Diagnostics.AppLog.Error("Gallery", $"Couldn't write the Gallery's file in {folder}: {ex.Message}");
                return;
            }
        }
    }
}
