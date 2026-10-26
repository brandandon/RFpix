// LinuxFiles.cs - two things the Gallery does with files on Linux:
//   - putting a picture in the Trash, the way Linux desktops share it (the freedesktop.org
//     Trash: the file goes in ~/.local/share/Trash/files, with a note in .../info saying where
//     it came from and when), so the file manager can put it back;
//   - showing a picture in the file manager, with it selected where the file manager can
//     (most can, through "FileManager1" on D-Bus); otherwise its folder is opened.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;

namespace RFpix.Platform;

[SupportedOSPlatform("linux")]
public static class LinuxFiles
{
    /// <summary>The Trash folder: $XDG_DATA_HOME/Trash, usually ~/.local/share/Trash. Tests point it elsewhere.</summary>
    internal static string TrashFolder { get; set; } = Path.Combine(
        Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } data ? data
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"),
        "Trash");

    /// <summary>Moves a file to the Trash. Throws IOException if it can't.</summary>
    public static void MoveToTrash(string path)
    {
        string full = Path.GetFullPath(path);
        string files = Path.Combine(TrashFolder, "files"), info = Path.Combine(TrashFolder, "info");
        Directory.CreateDirectory(files);
        Directory.CreateDirectory(info);

        // A name not already in the Trash: "tower.jp2", "tower.2.jp2"...
        string stem = Path.GetFileNameWithoutExtension(full), ext = Path.GetExtension(full);
        string name = Path.GetFileName(full);
        for (int n = 2; File.Exists(Path.Combine(files, name)) || File.Exists(Path.Combine(info, name + ".trashinfo")); n++)
            name = $"{stem}.{n}{ext}";

        // The note first (the spec's order), then the file; the note is taken back if the move fails.
        string note = Path.Combine(info, name + ".trashinfo");
        File.WriteAllText(note, "[Trash Info]\n" +
                                $"Path={Uri.EscapeDataString(full).Replace("%2F", "/")}\n" +
                                $"DeletionDate={DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)}\n");
        try { File.Move(full, Path.Combine(files, name)); }   // (from another drive, this copies it then removes it)
        catch
        {
            File.Delete(note);
            throw;
        }
    }

    /// <summary>Opens the file manager with the file selected (or at least its folder open).</summary>
    public static void ShowInFileManager(string path)
    {
        string uri = new Uri(Path.GetFullPath(path)).AbsoluteUri;
        try
        {
            var start = new ProcessStartInfo("dbus-send") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (string arg in new[] { "--session", "--print-reply", "--dest=org.freedesktop.FileManager1", "--type=method_call",
                                           "/org/freedesktop/FileManager1", "org.freedesktop.FileManager1.ShowItems",
                                           $"array:string:{uri}", "string:" })
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start);
            if (process is not null && process.WaitForExit(3000) && process.ExitCode == 0) return;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }

        // No file manager answered: open the folder instead.
        var open = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
        open.ArgumentList.Add(Path.GetDirectoryName(Path.GetFullPath(path))!);
        Process.Start(open)?.Dispose();
    }
}
