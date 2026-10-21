// FolderUploader.cs - uploads new pictures from a folder to a website, the way Dan's
// SSTV Auto Uploader does (so the website needs no change):
//   1. Watch the folder (C:\FTPIMAGE to start with) for new .jpg files, and wait until a
//      file has stopped changing for a few seconds (it may still be being written).
//   2. Upload it under a temporary name, and check the server got all of it, so a failed
//      upload never disturbs the pictures already on the site.
//   3. Shift the pictures on the server up one: image12.jpg is deleted, image11.jpg becomes
//      image12.jpg ... image1.jpg becomes image2.jpg. Then the new one becomes image1.jpg.
//   4. Move the file into the folder's "Archives" subfolder, named by its date and time.
// If the server can't be reached, it tries again 30 seconds later.
//
// RFpix puts a JPEG copy of each picture in the folder (UploadCopies), but anything else
// that saves JPEGs there (MMSSTV, say) is uploaded too.

using System.Globalization;

namespace RFpix.Upload;

/// <summary>A folder on a server: what the uploader needs (FTP, FTPS and SFTP all provide it).</summary>
public interface IRemoteFolder : IDisposable
{
    string WorkingFolder();
    IReadOnlyList<string> List();
    void Put(string localPath, string remoteName);
    long? Size(string remoteName);
    void Delete(string remoteName);
    void Rename(string from, string to);
}

/// <summary>The upload settings (Settings, FTP server tab). The password is kept encrypted.</summary>
public sealed class UploadSettings
{
    /// <summary>Save a JPEG copy of each received picture in <see cref="Folder"/> (stamped with the date and time).</summary>
    public bool SaveCopies { get; set; }
    public bool StampCopies { get; set; } = true;
    /// <summary>Copies of the pictures you send too.</summary>
    public bool CopySent { get; set; }
    public string Folder { get; set; } = @"C:\FTPIMAGE";

    /// <summary>Upload the pictures that arrive in <see cref="Folder"/>.</summary>
    public bool Upload { get; set; }
    public string ArchiveName { get; set; } = "Archives";
    public string Protocol { get; set; } = "FTP";   // FTP, FTPS or SFTP
    public string Host { get; set; } = "";
    public int Port { get; set; } = 21;
    public string Username { get; set; } = "";
    public string? ProtectedPassword { get; set; }
    /// <summary>A folder on the server to change to after logging in ("" for the login folder).</summary>
    public string RemoteFolder { get; set; } = "";
    public double StableSeconds { get; set; } = 3;
    public int ImageCount { get; set; } = 12;
    public string RemotePrefix { get; set; } = "image";
    public string RemoteExtension { get; set; } = ".jpg";

    public string RemoteName(int number) => $"{RemotePrefix}{number}{RemoteExtension}";

    /// <summary>What's wrong with the server settings, or null.</summary>
    public string? Problem()
    {
        if (string.IsNullOrWhiteSpace(Host)) return "Enter the server address.";
        if (string.IsNullOrWhiteSpace(Username)) return "Enter the user name.";
        if (Port is < 1 or > 65535) return "The port must be a number from 1 to 65535 (usually 21 for FTP and FTPS, 22 for SFTP).";
        if (ImageCount < 1) return "The number of pictures on the website must be 1 or more.";
        if (string.IsNullOrWhiteSpace(RemotePrefix) || !RemoteExtension.StartsWith('.')) return "Fill in the website file name (such as image and .jpg).";
        return null;
    }
}

public sealed class FolderUploader : IDisposable
{
    public const string TempRemoteName = "upload_tmp.part";
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    private readonly UploadSettings _settings;
    private readonly Func<UploadSettings, IRemoteFolder> _connect;
    private readonly CancellationTokenSource _stop = new();
    private Thread? _thread;

    /// <param name="connect">Opens the server's folder (logged in, and in RemoteFolder if set).</param>
    public FolderUploader(UploadSettings settings, Func<UploadSettings, IRemoteFolder> connect)
    {
        _settings = settings;
        _connect = connect;
    }

    /// <summary>A line for the log (from the uploader's own thread).</summary>
    public event Action<string>? Log;

    /// <summary>Pictures waiting to go (counted on each look at the folder).</summary>
    public int Waiting { get; private set; }

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "RFpix uploader" };
        _thread.Start();
    }

    public void Dispose()
    {
        _stop.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(5));
    }

    private void Say(string message) => Log?.Invoke(message);

    // ---- watching ----

    private readonly Dictionary<string, (long Size, DateTime Written, DateTime Since)> _seen = new();
    private readonly Dictionary<string, DateTime> _retryAfter = new();
    private readonly HashSet<string> _skip = new();

    private void Run()
    {
        Say($"Watching {_settings.Folder} for new pictures.");
        while (!_stop.IsCancellationRequested)
        {
            try { Look(DateTime.UtcNow); }
            catch (Exception ex) { Say($"Problem watching the folder: {ex.Message}"); _stop.Token.WaitHandle.WaitOne(2000); }
            _stop.Token.WaitHandle.WaitOne(500);
        }
        Say("Stopped watching.");
    }

    /// <summary>One look at the folder: uploads what's ready. (Public for the tests.)</summary>
    public void Look(DateTime now)
    {
        if (!Directory.Exists(_settings.Folder)) return;
        var present = new HashSet<string>();
        var ready = new List<(DateTime Written, string Path)>();
        foreach (var path in Directory.EnumerateFiles(_settings.Folder))
        {
            string ext = Path.GetExtension(path);
            if (!ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)) continue;
            present.Add(path);
            if (_skip.Contains(path)) continue;
            FileInfo info;
            try { info = new FileInfo(path); _ = info.Length; } catch (IOException) { continue; }
            if (!_seen.TryGetValue(path, out var before) || before.Size != info.Length || before.Written != info.LastWriteTimeUtc)
            {
                _seen[path] = (info.Length, info.LastWriteTimeUtc, now);   // new, or still changing
                continue;
            }
            if (info.Length == 0) continue;
            if ((now - before.Since).TotalSeconds >= _settings.StableSeconds &&
                now >= _retryAfter.GetValueOrDefault(path, DateTime.MinValue))
                ready.Add((info.LastWriteTimeUtc, path));
        }
        foreach (var gone in _seen.Keys.Where(p => !present.Contains(p)).ToList()) { _seen.Remove(gone); _retryAfter.Remove(gone); }
        _skip.IntersectWith(present);
        Waiting = ready.Count;

        foreach (var (_, path) in ready.OrderBy(r => r.Written))
        {
            if (_stop.IsCancellationRequested) return;
            Say($"New picture: {Path.GetFileName(path)}");
            try
            {
                using (File.OpenRead(path)) { }   // the writer has let go of it
                UploadOne(path);
            }
            catch (Exception ex)
            {
                Say($"Couldn't upload {Path.GetFileName(path)}: {ex.Message} Trying again in {RetryDelay.TotalSeconds:0} seconds.");
                _retryAfter[path] = now + RetryDelay;
                continue;
            }
            try { Archive(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Say($"Couldn't move {Path.GetFileName(path)} to {_settings.ArchiveName}: {ex.Message} (It won't be uploaded again.)");
                _skip.Add(path);
            }
            _seen.Remove(path);
            Waiting = Math.Max(0, Waiting - 1);
        }
    }

    // ---- uploading ----

    /// <summary>Uploads one picture as image1, shifting the others up (steps 2 and 3).</summary>
    public void UploadOne(string path)
    {
        var s = _settings;
        long size = new FileInfo(path).Length;
        if (size == 0) throw new IOException("The file is empty.");
        Say($"Connecting to {s.Host} ({s.Protocol}, port {s.Port})…");
        using var remote = _connect(s);
        remote.Put(path, TempRemoteName);
        long? onServer = remote.Size(TempRemoteName);
        if (onServer is long got && got != size)
        {
            try { remote.Delete(TempRemoteName); } catch (Exception) { }
            throw new IOException($"The server has {got} bytes of the {size}; the pictures on the site are left as they were.");
        }
        var names = remote.List().ToHashSet(StringComparer.Ordinal);
        string last = s.RemoteName(s.ImageCount);
        if (names.Contains(last)) remote.Delete(last);
        for (int i = s.ImageCount - 1; i >= 1; i--)
            if (names.Contains(s.RemoteName(i))) remote.Rename(s.RemoteName(i), s.RemoteName(i + 1));
        remote.Rename(TempRemoteName, s.RemoteName(1));
        Say($"Uploaded {Path.GetFileName(path)} as {s.RemoteName(1)}.");
    }

    /// <summary>Moves the file to the Archives subfolder, named by its date and time (step 4).</summary>
    public string Archive(string path)
    {
        string folder = Path.Combine(_settings.Folder, _settings.ArchiveName);
        Directory.CreateDirectory(folder);
        string stamp = File.GetLastWriteTime(path).ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);
        string to = Path.Combine(folder, stamp + ".jpg");
        for (int n = 1; File.Exists(to); n++) to = Path.Combine(folder, $"{stamp}_{n}.jpg");
        File.Move(path, to);
        return to;
    }

    /// <summary>Logs in and lists the server folder: for the Test button.</summary>
    public static string Test(UploadSettings settings, Func<UploadSettings, IRemoteFolder> connect)
    {
        using var remote = connect(settings);
        string where = remote.WorkingFolder();
        var names = remote.List();
        int ours = names.Count(n => n.StartsWith(settings.RemotePrefix, StringComparison.Ordinal));
        return $"Logged in. Server folder: {where}. {names.Count} file{(names.Count == 1 ? "" : "s")} there, {ours} starting with \"{settings.RemotePrefix}\".";
    }
}
