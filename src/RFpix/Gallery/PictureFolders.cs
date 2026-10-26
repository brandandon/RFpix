// PictureFolders.cs - where the Gallery's pictures are kept, and moving them somewhere else.
//
// The pictures folder (Documents\RFpix to start with) holds two folders:
//   Received - pictures received, in its Digital and Analog folders
//   Sent     - pictures you've sent
// Each of those keeps its hidden Gallery file (see GalleryIndex) beside its pictures.
// The Gallery page's "Change folder…" picks a new pictures folder, and either moves the
// pictures there or leaves them and starts empty.
//
// Moving is careful, because these are the only copies of people's pictures:
//   1. every file is copied to the new place first (a name already taken there gets " (2)");
//   2. each copy is checked: it must be there, the same size as the original;
//   3. only when every file has arrived are the Gallery files merged and the originals removed.
// If anything goes wrong before step 3, the copies made so far are removed again and the
// originals are left exactly as they were.

using RFpix.Settings;

namespace RFpix.Gallery;

public static class PictureFolders
{
    public const string ReceivedName = "Received", SentName = "Sent";

    /// <summary>The first pictures folder: Documents\RFpix.</summary>
    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RFpix");

    /// <summary>The pictures folder now in use.</summary>
    public static string Root => AppSettings.Current.PicturesFolder is { Length: > 0 } root ? root : DefaultRoot;

    /// <summary>Where received pictures go (in its Digital and Analog folders).</summary>
    public static string Received => AppSettings.Current.ReceivedFolder is { Length: > 0 } older ? older : Path.Combine(Root, ReceivedName);

    /// <summary>Where copies of the pictures you send go.</summary>
    public static string Sent => Path.Combine(Root, SentName);

    /// <summary>The Received and Sent folders inside a pictures folder.</summary>
    public static string[] Inside(string root) => new[] { Path.Combine(root, ReceivedName), Path.Combine(root, SentName) };

    /// <summary>How many pictures are in a pictures folder's Received and Sent folders.</summary>
    public static int CountPictures(string root) =>
        Inside(root).Where(Directory.Exists)
                    .Sum(folder => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Count(GalleryIndex.IsPicture));

    /// <summary>Why a folder can't be the new pictures folder (null: it can).</summary>
    public static string? Problem(string oldRoot, string newRoot)
    {
        string from = Full(oldRoot), to = Full(newRoot);
        if (string.Equals(from, to, PathComparison)) return "That's the folder already in use.";
        foreach (string inside in Inside(from).Select(Full))
            if (IsWithin(to, inside)) return "The new folder can't be inside the Received or Sent folder.";
        return null;
    }

    /// <summary>What a move did.</summary>
    public sealed record MoveResult(int Moved, int Renamed, string? Problem);

    /// <summary>Moves the Received and Sent folders' contents from one pictures folder to another
    /// (see the top of this file). <paramref name="progress"/> is told the files copied so far and the total.</summary>
    public static MoveResult Move(string oldRoot, string newRoot, Action<int, int>? progress = null)
    {
        if (Problem(oldRoot, newRoot) is { } problem) return new MoveResult(0, 0, problem);

        // What's to move: every file in Received and Sent (and their folders), except the Gallery files.
        var plan = new List<(string From, string To, string Folder, string OldName, string NewName)>();
        var claimed = new HashSet<string>(PathComparer);
        foreach (var (fromFolder, toFolder) in Inside(oldRoot).Zip(Inside(newRoot)))
        {
            if (!Directory.Exists(fromFolder)) continue;
            foreach (string file in Directory.EnumerateFiles(fromFolder, "*", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(file);
                if (IsIndexFile(name) || name.EndsWith(".new", StringComparison.Ordinal)) continue;
                string relativeFolder = Path.GetRelativePath(fromFolder, Path.GetDirectoryName(file)!);
                string destFolder = Path.GetFullPath(Path.Combine(toFolder, relativeFolder));
                string destName = FreeName(destFolder, name, claimed);
                string dest = Path.Combine(destFolder, destName);
                claimed.Add(dest);
                plan.Add((file, dest, destFolder, name, destName));
            }
        }

        // 1 and 2: copy, and check each copy.
        var made = new List<string>();
        try
        {
            for (int i = 0; i < plan.Count; i++)
            {
                var step = plan[i];
                Directory.CreateDirectory(step.Folder);
                File.Copy(step.From, step.To, overwrite: false);
                made.Add(step.To);
                File.SetLastWriteTimeUtc(step.To, File.GetLastWriteTimeUtc(step.From));
                if (!File.Exists(step.To) || new FileInfo(step.To).Length != new FileInfo(step.From).Length)
                    throw new IOException($"{step.OldName} didn't copy correctly.");
                progress?.Invoke(i + 1, plan.Count);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            foreach (string copy in made)
                try { File.Delete(copy); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return new MoveResult(0, 0, $"Nothing was moved: {ex.Message} Your pictures are all still in {oldRoot}.");
        }

        // 3: merge the Gallery files (with any names changed), then remove the originals.
        foreach (var group in plan.GroupBy(p => Path.GetDirectoryName(p.From)!, PathComparer))
        {
            string fromFolder = group.Key, toFolder = group.First().Folder;
            var renamed = group.Where(p => p.OldName != p.NewName).ToDictionary(p => p.OldName, p => p.NewName);
            GalleryIndex.MergeInto(toFolder, GalleryIndex.Entries(fromFolder), renamed);
        }
        foreach (var step in plan)
            try { File.Delete(step.From); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        foreach (string folder in Inside(oldRoot).Where(Directory.Exists))
        {
            foreach (string index in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Where(f => IsIndexFile(Path.GetFileName(f))))
                try { File.Delete(index); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            RemoveEmptyFolders(folder);
        }
        return new MoveResult(plan.Count, plan.Count(p => p.OldName != p.NewName), null);
    }

    private static bool IsIndexFile(string name) =>
        name.Equals(GalleryIndex.IndexFileName, StringComparison.OrdinalIgnoreCase) ||
        name.Equals(GalleryIndex.OldIndexFileName, StringComparison.OrdinalIgnoreCase);

    // A name not yet taken in the folder (or by another file in this move): "tower.jp2", "tower (2).jp2"...
    private static string FreeName(string folder, string name, HashSet<string> claimed)
    {
        string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        string candidate = name;
        for (int n = 2; File.Exists(Path.Combine(folder, candidate)) || claimed.Contains(Path.Combine(folder, candidate)); n++)
            candidate = $"{stem} ({n}){ext}";
        return candidate;
    }

    private static void RemoveEmptyFolders(string folder)
    {
        foreach (string sub in Directory.EnumerateDirectories(folder)) RemoveEmptyFolders(sub);
        try { if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsWithin(string path, string folder) =>
        string.Equals(path, folder, PathComparison) ||
        path.StartsWith(folder + Path.DirectorySeparatorChar, PathComparison);

    // Windows and the Mac don't mind capitals in names; Linux does.
    private static StringComparison PathComparison =>
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private static StringComparer PathComparer => OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
}
