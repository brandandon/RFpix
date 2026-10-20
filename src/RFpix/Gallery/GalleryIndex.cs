// GalleryIndex.cs - what the Gallery knows about each picture, beyond the picture itself.
//
// Pictures are ordinary files in the Received and Sent folders, so they can be copied,
// opened or deleted outside RFpix. Alongside them, each folder keeps a small file,
// "RFpix gallery.json", with what the file itself can't tell: who sent it, when, the
// settings it came with, the signal quality, and whether a fix (BSR) completed it.
// A picture with no entry there (copied in by hand, or saved before the Gallery existed)
// still shows: its date, time and callsign are read from its name where RFpix gave it
// one ("2026-10-20 1942 W9ABC tower.jp2"), or else the file's own date is used.

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
                                  int RepairedPieces = 0, bool CompletedByBsr = false);

public static class GalleryIndex
{
    public const string IndexFileName = "RFpix gallery.json";

    private static readonly HashSet<string> PictureExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".jp2", ".j2k", ".jpc", ".avif", ".png", ".bmp", ".gif", ".webp" };

    private static readonly object Lock = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>The default Sent folder: Documents\RFpix\Sent.</summary>
    public static string DefaultSentFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RFpix", "Sent");

    /// <summary>Is this a picture file the Gallery shows?</summary>
    public static bool IsPicture(string path) => PictureExtensions.Contains(Path.GetExtension(path));

    /// <summary>Notes a picture just saved in <paramref name="folder"/>.</summary>
    public static void Add(string folder, GalleryEntry entry)
    {
        lock (Lock)
        {
            var entries = Read(folder);
            entries[entry.File] = entry;
            Write(folder, entries);
        }
    }

    /// <summary>Forgets a picture (after it's deleted).</summary>
    public static void Remove(string folder, string file)
    {
        lock (Lock)
        {
            var entries = Read(folder);
            if (entries.Remove(file)) Write(folder, entries);
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
            if (entries.TryGetValue(file, out var e)) list.Add(e);
            else list.Add(FromName(file, File.GetLastWriteTime(path)));
        }
        return list.OrderByDescending(e => e.When).ThenByDescending(e => e.File, StringComparer.Ordinal).ToList();
    }

    // RFpix's own names: "2026-10-20 1942 W9ABC tower.jp2", "2026-10-20 1942 tower.jp2", maybe " (2)".
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

    private static Dictionary<string, GalleryEntry> Read(string folder)
    {
        try
        {
            string path = Path.Combine(folder, IndexFileName);
            if (!System.IO.File.Exists(path)) return new();
            var list = JsonSerializer.Deserialize<List<GalleryEntry>>(System.IO.File.ReadAllText(path), Json) ?? new();
            return list.Where(e => !string.IsNullOrEmpty(e.File)).GroupBy(e => e.File).ToDictionary(g => g.Key, g => g.Last());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();   // a damaged index only loses the extra details; the pictures still show
        }
    }

    private static void Write(string folder, Dictionary<string, GalleryEntry> entries)
    {
        try
        {
            Directory.CreateDirectory(folder);
            // Entries for pictures deleted outside RFpix are dropped as the index is written.
            var kept = entries.Values.Where(e => System.IO.File.Exists(Path.Combine(folder, e.File))).OrderBy(e => e.When).ToList();
            string path = Path.Combine(folder, IndexFileName);
            System.IO.File.WriteAllText(path + ".new", JsonSerializer.Serialize(kept, Json));
            System.IO.File.Move(path + ".new", path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }   // details are a nicety
    }
}
