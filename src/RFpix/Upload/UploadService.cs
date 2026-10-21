// UploadService.cs - runs the folder uploader for the whole of RFpix (started when RFpix
// opens, and again whenever the FTP server settings change), saves the JPEG copies it
// uploads, and keeps the last lines of its log for the FTP server tab.

using RFpix.Imaging;
using RFpix.Settings;

namespace RFpix.Upload;

public static class UploadService
{
    private static readonly object Lock = new();
    private static readonly List<FolderUploader> _uploaders = new();   // digital and analog
    private static readonly List<string> _log = new();

    /// <summary>A line was added to the log (raised on the uploader's thread).</summary>
    public static event Action<string>? Logged;

    /// <summary>The latest lines, oldest first.</summary>
    public static IReadOnlyList<string> RecentLog { get { lock (Lock) return _log.ToList(); } }

    public static bool Running { get { lock (Lock) return _uploaders.Count > 0; } }

    public static void Add(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        lock (Lock)
        {
            _log.Add(line);
            if (_log.Count > 200) _log.RemoveAt(0);
        }
        Logged?.Invoke(line);
    }

    /// <summary>Starts (or restarts, or stops) uploading, as the settings say: one uploader for the
    /// Digital copies, one for the Analog ones.</summary>
    public static void Restart()
    {
        Stop();
        var s = AppSettings.Current.Upload;
        if (!s.Upload) return;
        if (s.Problem() is { } problem) { Add($"Not uploading: {problem}"); return; }
        var started = new List<FolderUploader>();
        foreach (bool analog in new[] { false, true })
        {
            var kind = Copy(s).For(analog);
            try { Directory.CreateDirectory(kind.Folder); }
            catch (Exception ex) { Add($"Not uploading {(analog ? "analog" : "digital")} pictures: couldn't use the folder {kind.Folder} ({ex.Message})."); continue; }
            var uploader = new FolderUploader(kind, RemoteFolders.Connect);
            string what = analog ? "Analog" : "Digital";
            uploader.Log += m => Add($"{what}: {m}");
            started.Add(uploader);
        }
        lock (Lock) _uploaders.AddRange(started);
        foreach (var u in started) u.Start();
    }

    public static void Stop()
    {
        List<FolderUploader> old;
        lock (Lock) { old = _uploaders.ToList(); _uploaders.Clear(); }
        foreach (var u in old) u.Dispose();
    }

    // The uploaders work from their own copy, so settings being typed don't change under them.
    private static UploadSettings Copy(UploadSettings s) =>
        System.Text.Json.JsonSerializer.Deserialize<UploadSettings>(System.Text.Json.JsonSerializer.Serialize(s))!;

    /// <summary>Saves a JPEG copy of a picture in the upload folder (its Digital or Analog
    /// subfolder), stamped with the time (if that's set), for the website. Returns the path, or
    /// null if copies are off.</summary>
    /// <param name="sent">True for a picture you sent (copied only if that's ticked).</param>
    public static string? SaveCopy(DecodedPicture picture, DateTime when, bool analog, bool sent = false)
    {
        var s = AppSettings.Current.Upload;
        if (!s.SaveCopies || (sent && !s.CopySent)) return null;
        string folder = s.CopyFolder(analog);
        try
        {
            Directory.CreateDirectory(folder);
            var stamped = s.StampCopies ? TimeStamp.Stamp(picture, TimeStamp.Text(when)) : picture;
            var jpeg = Pictures.EncodeJpeg(stamped, 90);
            string name = $"{when:yyyyMMdd_HHmmss}";
            string path = Path.Combine(folder, name + ".jpg");
            for (int n = 2; File.Exists(path); n++) path = Path.Combine(folder, $"{name}_{n}.jpg");
            // Written under another name first, so the uploader never sees half a file.
            File.WriteAllBytes(path + ".part", jpeg);
            File.Move(path + ".part", path);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PictureException)
        {
            Add($"Couldn't save a copy in {folder}: {ex.Message}");
            return null;
        }
    }

    /// <summary>The same for a picture's file (decoded first).</summary>
    public static string? SaveCopy(byte[] pictureFile, DateTime when, bool analog, bool sent = false)
    {
        var s = AppSettings.Current.Upload;
        if (!s.SaveCopies || (sent && !s.CopySent)) return null;
        try { return SaveCopy(Pictures.Decode(pictureFile), when, analog, sent); }
        catch (PictureException ex) { Add($"Couldn't make a copy for the website: {ex.Message}"); return null; }
    }
}
