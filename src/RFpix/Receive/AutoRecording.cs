// AutoRecording.cs - "Record each picture" (Receive tab; asked by Dan, Nov 28, 2026): when to start and stop an
// automatic recording, and clearing out old ones (Settings > Misc: kept 1 to 7 days, and at most so many MB).
//
// An automatic recording starts when a picture of the kind RFpix is listening for is found (an analog picture,
// HamDRM with its settings read, RFpix's FM signal, or RF-Hybrid), beginning a few seconds before, and stops a few
// seconds after it ends. They go in Documents\RFpix\Recordings\Automatic, so recordings made with the Record
// button are never cleared out.

namespace RFpix.Receive;

/// <summary>What the screen should do with the automatic recording, from <see cref="AutoRecordPolicy.Update"/>.</summary>
public enum AutoRecordStep { Nothing, Start, Stop }

/// <summary>When an automatic recording starts and stops. The screen asks every moment, passing whether a picture is
/// arriving and whether an automatic recording is running.</summary>
public sealed class AutoRecordPolicy
{
    /// <summary>How much of the sound before the picture was found goes in the recording.</summary>
    public static readonly TimeSpan Lead = TimeSpan.FromSeconds(10);

    /// <summary>How long after the picture ends the recording carries on (a fade that comes back is in the same file).</summary>
    public static readonly TimeSpan Tail = TimeSpan.FromSeconds(5);

    private DateTime? _quietSince;
    private bool _held;

    /// <summary>The next step: start when a picture arrives and nothing is recording, stop once it has been over for
    /// <see cref="Tail"/>. <paramref name="otherRecording"/> is a recording made with the Record button: then
    /// nothing starts (that one already has the sound).</summary>
    public AutoRecordStep Update(bool busy, DateTime now, bool autoRecording, bool otherRecording = false)
    {
        if (busy) _quietSince = null;
        else _held = false;   // the picture is over: the next one may be recorded
        if (!autoRecording)
            return busy && !otherRecording && !_held ? AutoRecordStep.Start : AutoRecordStep.Nothing;
        if (busy) return AutoRecordStep.Nothing;
        _quietSince ??= now;
        if (now - _quietSince.Value < Tail) return AutoRecordStep.Nothing;
        _quietSince = null;
        return AutoRecordStep.Stop;
    }

    /// <summary>The automatic recording was stopped some other way (Stop recording clicked, or it couldn't start):
    /// don't start another until this picture is over.</summary>
    public void Hold()
    {
        _held = true;
        _quietSince = null;
    }
}

/// <summary>Clears out old automatic recordings.</summary>
public static class AutoRecordCleanup
{
    /// <summary>Where automatic recordings go: Documents\RFpix\Recordings\Automatic.</summary>
    public static string Folder => Path.Combine(AudioRecorder.DefaultFolder, "Automatic");

    public const int MinDays = 1, MaxDays = 7;
    public const int MinMb = 10, MaxMb = 1_000_000;

    /// <summary>The MB typed on Settings > Misc, or null if it isn't a whole number from 10 to 1,000,000.</summary>
    public static int? ParseMb(string? text) =>
        int.TryParse((text ?? "").Trim().Replace(",", ""), out int mb) && mb is >= MinMb and <= MaxMb ? mb : null;

    /// <summary>Deletes the .wav files in the folder older than <paramref name="days"/> (1 to 7), then the oldest
    /// until they take at most <paramref name="maxMb"/> MB (1 MB = 1,000,000 bytes). The file being recorded
    /// (<paramref name="keep"/>) is never deleted. Returns the names of the files deleted; files that can't be
    /// deleted (open elsewhere) are left for next time.</summary>
    public static IReadOnlyList<string> Clean(string folder, int days, int maxMb, DateTime now, string? keep = null)
    {
        var deleted = new List<string>();
        if (!Directory.Exists(folder)) return deleted;
        days = Math.Clamp(days, MinDays, MaxDays);
        List<FileInfo> files;
        try
        {
            files = new DirectoryInfo(folder).GetFiles("*.wav")
                .Where(f => keep is null || !string.Equals(f.FullName, Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.LastWriteTime).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return deleted; }

        long total = files.Sum(f => f.Length);
        long cap = Math.Max(0, maxMb) * 1_000_000L;
        foreach (var f in files)
        {
            bool old = now - f.LastWriteTime > TimeSpan.FromDays(days);
            if (!old && total <= cap) break;   // oldest first: the rest are newer, and they fit
            long length = f.Length;   // before deleting: afterwards FileInfo can't tell
            try
            {
                f.Delete();
                deleted.Add(f.Name);
                total -= length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return deleted;
    }
}
