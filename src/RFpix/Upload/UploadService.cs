// UploadService.cs - runs the folder uploader for the whole of RFpix (started when RFpix
// opens, and again whenever the FTP server settings change), saves the JPEG copies it
// uploads, and keeps the last lines of its log for the FTP server tab.

using RFpix.Imaging;
using RFpix.Settings;

namespace RFpix.Upload;

public static class UploadService
{
    private static readonly object Lock = new();
    private static FolderUploader? _uploader;
    private static readonly List<string> _log = new();

    /// <summary>A line was added to the log (raised on the uploader's thread).</summary>
    public static event Action<string>? Logged;

    /// <summary>The latest lines, oldest first.</summary>
    public static IReadOnlyList<string> RecentLog { get { lock (Lock) return _log.ToList(); } }

    public static bool Running { get { lock (Lock) return _uploader is not null; } }

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

    /// <summary>Starts (or restarts, or stops) uploading, as the settings say.</summary>
    public static void Restart()
    {
        FolderUploader? old;
        lock (Lock) { old = _uploader; _uploader = null; }
        old?.Dispose();
        var s = AppSettings.Current.Upload;
        if (!s.Upload) return;
        if (s.Problem() is { } problem) { Add($"Not uploading: {problem}"); return; }
        try { Directory.CreateDirectory(s.Folder); }
        catch (Exception ex) { Add($"Not uploading: couldn't use the folder {s.Folder} ({ex.Message})."); return; }
        var uploader = new FolderUploader(Copy(s), RemoteFolders.Connect);
        uploader.Log += Add;
        lock (Lock) _uploader = uploader;
        uploader.Start();
    }

    public static void Stop()
    {
        FolderUploader? old;
        lock (Lock) { old = _uploader; _uploader = null; }
        old?.Dispose();
    }

    // The uploader works from its own copy, so settings being typed don't change under it.
    private static UploadSettings Copy(UploadSettings s) =>
        System.Text.Json.JsonSerializer.Deserialize<UploadSettings>(System.Text.Json.JsonSerializer.Serialize(s))!;

    /// <summary>Saves a JPEG copy of a picture in the upload folder, stamped with the time (if
    /// that's set), for the website. Returns the path, or null if copies are off.</summary>
    /// <param name="sent">True for a picture you sent (copied only if that's ticked).</param>
    public static string? SaveCopy(DecodedPicture picture, DateTime when, bool sent = false)
    {
        var s = AppSettings.Current.Upload;
        if (!s.SaveCopies || (sent && !s.CopySent)) return null;
        try
        {
            Directory.CreateDirectory(s.Folder);
            var stamped = s.StampCopies ? TimeStamp.Stamp(picture, TimeStamp.Text(when)) : picture;
            var jpeg = Pictures.EncodeJpeg(stamped, 90);
            string name = $"{when:yyyyMMdd_HHmmss}";
            string path = Path.Combine(s.Folder, name + ".jpg");
            for (int n = 2; File.Exists(path); n++) path = Path.Combine(s.Folder, $"{name}_{n}.jpg");
            // Written under another name first, so the uploader never sees half a file.
            File.WriteAllBytes(path + ".part", jpeg);
            File.Move(path + ".part", path);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PictureException)
        {
            Add($"Couldn't save a copy in {s.Folder}: {ex.Message}");
            return null;
        }
    }

    /// <summary>The same for a picture's file (decoded first).</summary>
    public static string? SaveCopy(byte[] pictureFile, DateTime when, bool sent = false)
    {
        var s = AppSettings.Current.Upload;
        if (!s.SaveCopies || (sent && !s.CopySent)) return null;
        try { return SaveCopy(Pictures.Decode(pictureFile), when, sent); }
        catch (PictureException ex) { Add($"Couldn't make a copy for the website: {ex.Message}"); return null; }
    }
}
