// ContactLog.cs - logging a contact: saved in RFpix's own log file (ADIF, in Documents\RFpix),
// and sent to a logging program on this computer (HRD Logbook, for one) the way WSJT-X sends
// them: the contact's ADIF over UDP. HRD Logbook takes them in with QSO Forwarding > UDP Receive,
// on port 2333 (the port its help gives for WSJT-X).

using System.Net.Sockets;
using System.Text;
using RFpix.Diagnostics;

namespace RFpix.Logbook;

/// <summary>Settings > Logging.</summary>
public sealed class LoggingSettings
{
    /// <summary>Send each contact logged to a logging program listening on this address and port.</summary>
    public bool SendToLogger { get; set; } = true;
    public string Address { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 2333;
}

/// <summary>How a contact got on: saved in RFpix's file or not, sent or not (with why not).</summary>
public sealed record LogResult(bool Saved, string? SaveProblem, bool Sent, string? SendProblem);

public static class ContactLog
{
    private static readonly object Lock = new();

    /// <summary>Documents\RFpix\RFpix contacts.adi</summary>
    public static string DefaultFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RFpix", "RFpix contacts.adi");

    /// <summary>Saves the contact in the file and sends it to the logging program (if that's on).</summary>
    public static LogResult Log(Qso qso, LoggingSettings settings, string? file = null)
    {
        file ??= DefaultFile;
        string record = Adif.Record(qso);
        string? saveProblem = null, sendProblem = null;
        try { Append(file, record); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { saveProblem = ex.Message; }

        bool sent = false;
        if (settings.SendToLogger)
        {
            try
            {
                Send(Adif.Header(AppLog.Version) + record, settings.Address, settings.Port);
                sent = true;
            }
            catch (Exception ex) when (ex is SocketException or ArgumentException or FormatException) { sendProblem = ex.Message; }
        }

        string what = $"{qso.Call} on {qso.Band ?? "an unknown band"}";
        AppLog.Info("Logbook", $"Logged {what}" + (saveProblem is null ? ", saved in " + Path.GetFileName(file) : "") +
                               (sent ? $", sent to {settings.Address}:{settings.Port}" : "") + ".");
        if (saveProblem is not null) AppLog.Error("Logbook", $"Couldn't save {what} in {file}: {saveProblem}");
        if (sendProblem is not null) AppLog.Error("Logbook", $"Couldn't send {what} to {settings.Address}:{settings.Port}: {sendProblem}");
        return new LogResult(saveProblem is null, saveProblem, sent, sendProblem);
    }

    /// <summary>Adds a contact to the file (starting it with the header if it's new or empty).</summary>
    internal static void Append(string file, string record)
    {
        lock (Lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            bool fresh = !File.Exists(file) || new FileInfo(file).Length == 0;
            File.AppendAllText(file, (fresh ? Adif.Header(AppLog.Version) : "") + record, new UTF8Encoding(false));
        }
    }

    /// <summary>How many contacts the file holds (0 if there's no file).</summary>
    public static int Count(string? file = null)
    {
        file ??= DefaultFile;
        lock (Lock)
        {
            if (!File.Exists(file)) return 0;
            string text = File.ReadAllText(file);
            int n = 0;
            for (int at = text.IndexOf("<eor>", StringComparison.OrdinalIgnoreCase); at >= 0;
                 at = text.IndexOf("<eor>", at + 5, StringComparison.OrdinalIgnoreCase)) n++;
            return n;
        }
    }

    /// <summary>Empties RFpix's own file (Settings > Logging > Clear log file). A logging program
    /// keeps its own copy of what was sent to it.</summary>
    public static void Clear(string? file = null)
    {
        file ??= DefaultFile;
        lock (Lock) { if (File.Exists(file)) File.Delete(file); }
        AppLog.Info("Logbook", $"Cleared {Path.GetFileName(file)}.");
    }

    private static void Send(string adif, string address, int port)
    {
        if (port is < 1 or > 65535) throw new ArgumentException($"the port {port} isn't between 1 and 65535");
        using var udp = new UdpClient();
        byte[] bytes = Encoding.UTF8.GetBytes(adif);
        udp.Send(bytes, bytes.Length, address.Trim(), port);
    }
}
