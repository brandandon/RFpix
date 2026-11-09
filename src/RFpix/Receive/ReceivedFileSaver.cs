// ReceivedFileSaver.cs - saves received files to the "received" folder.
//
// File names come off the air, so they can't be trusted: a sender could (by
// accident or not) send a name like "..\..\Windows\something". Only the plain
// name part is kept, with any characters Windows doesn't allow replaced.
// Many stations send pictures called "img001.jpg" or similar, so each saved
// file gets the date, time and sender's callsign in front, e.g.
//   2026-10-11 1432 N0CALL img04.jpg
// and a number is added if that name is already taken.

using RFpix.HamDrm;

namespace RFpix.Receive;

public static class ReceivedFileSaver
{
    /// <summary>Inside the Received folder, digital (HamDRM) and analog SSTV pictures each have their own.</summary>
    public const string DigitalSubfolder = "Digital", AnalogSubfolder = "Analog";

    /// <summary>Saves a received file and returns the full path it was saved to.</summary>
    public static string Save(ReceivedFile file, string folder, string? callsign, DateTime when)
    {
        Directory.CreateDirectory(folder);
        string name = $"{when:yyyy-MM-dd HHmm} {(string.IsNullOrWhiteSpace(callsign) ? "" : Clean(callsign) + " ")}{Clean(file.Name)}";

        string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        // Each name is claimed in one step (a file that must be new), so two pictures saved at the
        // same moment can't both pick the same name and one overwrite the other.
        for (int n = 1; n <= 1000; n++)
        {
            string path = Path.Combine(folder, n == 1 ? name : $"{stem} ({n}){ext}");
            FileStream claimed;
            try { claimed = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
            catch (IOException) when (File.Exists(path)) { continue; }   // taken: the next number
            try
            {
                using (claimed) claimed.Write(file.Data);
                return path;
            }
            catch
            {
                try { File.Delete(path); } catch (IOException) { }   // no half-written picture left behind
                throw;
            }
        }
        throw new IOException($"Too many pictures named {name} in {folder}.");
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
