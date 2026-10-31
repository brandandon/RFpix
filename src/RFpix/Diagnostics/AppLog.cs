// AppLog.cs - RFpix's log, for the whole program: everything it does that might matter when
// something goes wrong.
//
// What's written: RFpix starting and closing (and crashing, with the details); the settings
// that matter for problems (LogSummary.cs; never a password or the FTP user name); the radio
// connecting, disconnecting, keying and releasing, with Hamlib's own detail while connecting
// and before any radio error (HamlibLog.cs); the sound devices starting and stopping;
// transmissions and pictures sent and received; website uploads; and every error RFpix shows.
//
// It's always on. The files are in the settings folder's Logs folder:
//   Windows: C:\Users\<you>\AppData\Roaming\RFpix\Logs
//   Mac:     ~/Library/Application Support/RFpix/Logs
//   Linux:   ~/.config/RFpix/Logs
// One per run, the last 10 kept, each at most 5 MB (LogFile.cs). Settings > Misc has
// "Save log for help" (all of them in one zip file on the desktop) and "Open log folder".
//
// Until Start is called (as in the tests), nothing is written.

using System.Reflection;
using System.Runtime.InteropServices;

namespace RFpix.Diagnostics;

public static class AppLog
{
    private static LogFile? _file;
    private static Exception? _crash;

    /// <summary>Where the logs are kept.</summary>
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RFpix", "Logs");

    /// <summary>The log being written, or null if there isn't one.</summary>
    public static string? CurrentFile => _file?.Path;

    /// <summary>The version running, as shown: "0.6.0 beta".</summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
                         ?? assembly.GetName().Version?.ToString(3) ?? "";
        return version.Replace('-', ' ');
    }

    /// <summary>Begins this run's log (when RFpix starts).</summary>
    public static void Start()
    {
        try { _file = new LogFile(Folder, DateTime.Now); }
        catch (Exception) { _file = null; return; }   // no log this time; RFpix runs anyway
        Info("RFpix", $"RFpix {Version} started.");
        Info("RFpix", $"Computer: {RuntimeInformation.OSDescription}, {RuntimeInformation.OSArchitecture}" +
                      $" (RFpix running as {RuntimeInformation.ProcessArchitecture}), .NET {Environment.Version}");
    }

    /// <summary>Something that happened. <paramref name="area"/> is the part of RFpix: "Radio", "Send"...</summary>
    public static void Info(string area, string message) => _file?.Write(DateTime.Now, area, message, error: false);

    /// <summary>Something that went wrong (usually what RFpix showed on the screen).</summary>
    public static void Error(string area, string message) => _file?.Write(DateTime.Now, area, message, error: true);

    /// <summary>Hamlib's own lines, each with the time it said it.</summary>
    public static void Hamlib(IEnumerable<(DateTime When, string Text)> lines)
    {
        if (_file is not { } file) return;
        foreach (var (when, text) in lines) file.Write(when, "Hamlib", text, error: false);
    }

    /// <summary>RFpix is about to close because of an error it didn't expect: what it was, in full.</summary>
    public static void Crash(Exception ex)
    {
        if (ReferenceEquals(Interlocked.Exchange(ref _crash, ex), ex)) return;   // already written
        Error("RFpix", $"RFpix stopped because of an error it didn't expect:\n{ex}");
    }

    /// <summary>RFpix is closing normally.</summary>
    public static void Stop()
    {
        Info("RFpix", "RFpix closed.");
        _file?.Dispose();
        _file = null;
    }

    /// <summary>All the logs in one zip file in <paramref name="folder"/> (the desktop), for sending
    /// to whoever is helping. Returns its path.</summary>
    public static string SaveForHelp(string folder)
    {
        Info("RFpix", "Saving the logs for help.");
        return LogFile.Zip(Folder, folder, DateTime.Now);
    }

    /// <summary>The desktop, where "Save log for help" puts the zip file (or Documents, or the
    /// home folder, on a computer without one).</summary>
    public static string HelpFolder()
    {
        foreach (var folder in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.UserProfile })
        {
            string path = Environment.GetFolderPath(folder);
            if (path.Length > 0 && Directory.Exists(path)) return path;
        }
        return Folder;
    }
}
