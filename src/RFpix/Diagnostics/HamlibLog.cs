// HamlibLog.cs - Hamlib's own detailed messages, for the log.
//
// Hamlib can say everything it does: each command sent to the radio, byte by byte, and what
// came back (or that nothing did). That's what's needed when a radio won't connect. But RFpix
// reads the radio's frequency once a second, and writing all of that would add several MB an
// hour saying "all fine". So:
//   - every message is caught (Hamlib hands them over instead of printing them) and the last
//     few hundred are remembered;
//   - while connecting, they're also written to the log as they come;
//   - when talking to the radio fails, the remembered ones not yet written are written, so the
//     log shows what was sent and what came back just before the error.

using System.Text;
using RFpix.Radio;

namespace RFpix.Diagnostics;

internal static class HamlibLog
{
    /// <summary>How many of Hamlib's lines are remembered.</summary>
    public const int Remembered = 400;

    /// <summary>Where the lines go: the log (tests put their own here).</summary>
    public static HamlibDetail Detail { get; set; } = new(Remembered, () => DateTime.Now, AppLog.Hamlib);

    private static HamlibNative.DebugCallback? _callback;   // kept so the garbage collector leaves it alone
    private static readonly object Lock = new();
    private static volatile bool _formatBroken;

    /// <summary>Asks Hamlib for all its messages (once, before Hamlib is first used). Throws as
    /// Hamlib's other functions do if the library can't be loaded.</summary>
    public static void Attach()
    {
        lock (Lock)
        {
            if (_callback is not null) return;
            try
            {
                var callback = new HamlibNative.DebugCallback(OnMessage);
                HamlibNative.rig_set_debug_callback(callback, IntPtr.Zero);
                _callback = callback;
                HamlibNative.rig_set_debug(HamlibNative.DebugTrace);
                // Hamlib can have threads of its own: as RFpix closes, they mustn't call into it.
                AppDomain.CurrentDomain.ProcessExit += (_, _) => Detach();
            }
            catch (EntryPointNotFoundException)
            {
                HamlibNative.rig_set_debug(0);   // a Hamlib without this: quiet, as before
            }
        }
    }

    /// <summary>Hamlib's messages off again (as RFpix closes).</summary>
    public static void Detach()
    {
        lock (Lock)
        {
            if (_callback is null) return;
            try
            {
                HamlibNative.rig_set_debug(0);
                HamlibNative.rig_set_debug_callback(null, IntPtr.Zero);
            }
            catch (Exception) { /* closing anyway */ }
            // _callback stays set, so a message already on its way still finds it.
        }
    }

    /// <summary>Writes Hamlib's lines to the log as they come, for this long (while connecting).</summary>
    public static void WriteFor(TimeSpan time) => Detail.WriteFor(time);

    /// <summary>Writes the remembered lines not yet written (when talking to the radio failed).</summary>
    public static void WriteRecent() => Detail.WriteRecent();

    // On whichever thread called Hamlib (Hamlib lets only one message through at a time).
    private static int OnMessage(int level, IntPtr data, IntPtr format, IntPtr values)
    {
        if (_formatBroken) return 0;
        try { Detail.Add(HamlibNative.Format(format, values)); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _formatBroken = true;   // the C library isn't there: no more Hamlib lines this run
        }
        catch (Exception)
        {
            // Never let an error escape into Hamlib.
        }
        return 0;
    }
}

/// <summary>Hamlib's lines: put together from the pieces Hamlib sends, remembered, and written
/// when asked (see HamlibLog).</summary>
internal sealed class HamlibDetail
{
    private readonly int _remember;
    private readonly Func<DateTime> _now;
    private readonly Action<IReadOnlyList<(DateTime When, string Text)>> _write;
    private readonly object _lock = new();
    private readonly Queue<(DateTime When, string Text)> _recent = new();   // not yet written
    private readonly StringBuilder _partial = new();
    private DateTime _writeUntil = DateTime.MinValue;

    public HamlibDetail(int remember, Func<DateTime> now, Action<IReadOnlyList<(DateTime When, string Text)>> write)
    {
        _remember = remember;
        _now = now;
        _write = write;
    }

    /// <summary>A piece of text from Hamlib: usually one line, but a line can come in several pieces.</summary>
    public void Add(string text)
    {
        lock (_lock)
        {
            List<(DateTime, string)>? now = null;
            foreach (char c in text)
            {
                if (c != '\n') { if (c != '\r') _partial.Append(c); continue; }
                string line = _partial.ToString().TrimEnd();
                _partial.Clear();
                if (line.Length == 0) continue;
                var when = _now();
                if (when < _writeUntil) (now ??= new()).Add((when, line));
                else
                {
                    _recent.Enqueue((when, line));
                    while (_recent.Count > _remember) _recent.Dequeue();
                }
            }
            if (now is not null) _write(now);
        }
    }

    public void WriteFor(TimeSpan time)
    {
        lock (_lock)
        {
            var until = _now() + time;
            if (until > _writeUntil) _writeUntil = until;
            _recent.Clear();   // what came before (the list of radios, say) isn't about this
        }
    }

    public void WriteRecent()
    {
        lock (_lock)
        {
            if (_recent.Count == 0) return;
            var lines = _recent.ToList();
            _recent.Clear();
            _write(lines);
        }
    }
}
