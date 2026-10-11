// ReceivedFileSaver.cs - saves received files to the "received" folder.
//
// File names come off the air, so they can't be trusted: a sender could (by
// accident or not) send a name like "..\..\Windows\something". Only the plain
// name part is kept, with any characters Windows doesn't allow replaced.
// Many stations send pictures called "img001.jpg" or similar, so each saved
// file gets the date, time and sender's callsign in front, e.g.
//   2026-10-11 1432 KD9DD img04.jpg
// and a number is added if that name is already taken.

using RFpix.HamDrm;

namespace RFpix.Receive;

public static class ReceivedFileSaver
{
    /// <summary>The default folder: Documents\RFpix\Received.</summary>
    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RFpix", "Received");

    /// <summary>Saves a received file and returns the full path it was saved to.</summary>
    public static string Save(ReceivedFile file, string folder, string? callsign, DateTime when)
    {
        Directory.CreateDirectory(folder);
        string name = $"{when:yyyy-MM-dd HHmm} {(string.IsNullOrWhiteSpace(callsign) ? "" : Clean(callsign) + " ")}{Clean(file.Name)}";

        string path = Path.Combine(folder, name);
        string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        for (int n = 2; File.Exists(path); n++) path = Path.Combine(folder, $"{stem} ({n}){ext}");

        File.WriteAllBytes(path, file.Data);
        return path;
    }

    /// <summary>Makes a name from the air safe to use as a file name.</summary>
    public static string Clean(string name)
    {
        // Keep only the last part of anything that looks like a path.
        name = name.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];

        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '|', '?', '*' }).ToHashSet();
        var chars = name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray();
        name = new string(chars).Trim().TrimStart('.');   // no hidden ".name" or ".." names
        if (name.Length > 80) name = name[..60] + name[^20..];
        return name.Length == 0 ? "received" : name;
    }
}
