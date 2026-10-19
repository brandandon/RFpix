// IcomCiv.cs - Icom's own radio commands ("CI-V"), for switching the IC-9700's data mode.
//
// Hamlib normally talks to the radio for RFpix. For data mode on the IC-9700 RFpix sends
// Icom's command itself, through Hamlib's open connection, because it only turns the
// radio's DATA setting on or off: the mode (FM, USB...) and its filter stay exactly as
// they were. (Changing the whole mode through Hamlib didn't switch Dan's IC-9700, Oct 19, 2026.)
//
// A CI-V message ("frame") is:  FE FE <to> <from> <command...> FD
//   FE FE   the start
//   to      the radio's address (the IC-9700's is A2 unless changed in its menu)
//   from    the computer's address, E0 by custom
//   FD      the end
// The radio answers FB ("OK") or FA ("not OK"), or with the information asked for.
// The commands used here (from Icom's IC-9700 CI-V reference):
//   04         read the mode and filter            answer: 04 <mode> <filter>
//   1A 06      read the data setting               answer: 1A 06 <data> <filter>
//   1A 06 d f  set it: d = 01 on / 00 off; f = the filter (1-3) when on, 00 when off

namespace RFpix.Radio;

public static class IcomCiv
{
    public const byte Preamble = 0xFE, End = 0xFD, Computer = 0xE0, Ok = 0xFB, NotOk = 0xFA;

    /// <summary>The IC-9700's address unless changed in its menu.</summary>
    public const byte Ic9700Address = 0xA2;

    /// <summary>A whole message to the radio: FE FE radio E0 body FD.</summary>
    public static byte[] Frame(byte radio, params byte[] body)
    {
        var frame = new byte[body.Length + 5];
        frame[0] = frame[1] = Preamble;
        frame[2] = radio;
        frame[3] = Computer;
        body.CopyTo(frame, 4);
        frame[^1] = End;
        return frame;
    }

    public static byte[] ReadMode(byte radio) => Frame(radio, 0x04);
    public static byte[] ReadDataMode(byte radio) => Frame(radio, 0x1A, 0x06);

    /// <summary>Turns data on (keeping <paramref name="filter"/>) or off.</summary>
    public static byte[] SetDataMode(byte radio, bool on, byte filter) =>
        Frame(radio, 0x1A, 0x06, on ? (byte)0x01 : (byte)0x00, on ? Math.Clamp(filter, (byte)1, (byte)3) : (byte)0x00);

    /// <summary>The body of a message the radio sent to the computer (between the addresses and FD),
    /// or null if this isn't one: the radio's echo of our own message, a message to someone
    /// else, or something damaged.</summary>
    public static byte[]? Answer(ReadOnlySpan<byte> frame, byte radio)
    {
        int start = frame.IndexOf(new byte[] { Preamble, Preamble });
        if (start < 0) return null;
        frame = frame[start..];
        while (frame.Length > 2 && frame[2] == Preamble) frame = frame[1..];   // extra FE in front
        int end = frame.IndexOf(End);
        if (end < 4 || frame[2] != Computer || frame[3] != radio) return null;
        return frame[4..end].ToArray();
    }

    /// <summary>The name of an Icom mode number, e.g. 05 → "FM".</summary>
    public static string ModeName(byte mode) => mode switch
    {
        0x00 => "LSB", 0x01 => "USB", 0x02 => "AM", 0x03 => "CW", 0x04 => "RTTY", 0x05 => "FM",
        0x07 => "CW-R", 0x08 => "RTTY-R", 0x17 => "DV", 0x22 => "DD",
        _ => $"mode {mode:X2}",
    };

    /// <summary>Whether a mode has a data version on the IC-9700 (LSB, USB, AM and FM do).</summary>
    public static bool HasDataMode(byte mode) => mode is 0x00 or 0x01 or 0x02 or 0x05;

    /// <summary>A CI-V address typed as hex ("A2", "0xA2"), or the IC-9700's if blank.</summary>
    public static byte Address(string? typed)
    {
        if (string.IsNullOrWhiteSpace(typed)) return Ic9700Address;
        string hex = typed.Trim();
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
        return byte.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out byte b) && b > 0
            ? b : throw new RadioException($"\"{typed}\" isn't a CI-V address. It's a hex number such as A2.");
    }
}

/// <summary>Switches an Icom radio's DATA setting on for transmitting and off again, by CI-V,
/// checking each time that it took.</summary>
/// <param name="exchange">Sends a CI-V message and returns the radio's answer body (see
/// <see cref="IcomCiv.Answer"/>); the text says what's being done, for error messages.</param>
public sealed class IcomDataMode(byte radio, Func<byte[], string, byte[]> exchange, TimeSpan settle)
{
    /// <summary>Turns DATA on (if it's off), keeping the mode and filter. Returns the mode it was
    /// in (e.g. "FM") if it switched, or null if it left the radio alone, with a few words on what happened.</summary>
    public (string? Mode, string Report) On()
    {
        var data = Ask(IcomCiv.ReadDataMode(radio), 0x1A, 0x06, "read the data setting");
        if (data.Length >= 3 && data[2] != 0) return (null, "already in data mode");

        var mode = Ask(IcomCiv.ReadMode(radio), 0x04, null, "read the mode");
        byte number = mode.Length >= 2 ? mode[1] : (byte)0xFF;
        byte filter = mode.Length >= 3 && mode[2] is >= 1 and <= 3 ? mode[2] : (byte)1;
        string name = IcomCiv.ModeName(number);
        if (!IcomCiv.HasDataMode(number)) return (null, $"{name} has no data mode, so it was left as it is");

        Tell(IcomCiv.SetDataMode(radio, on: true, filter), "switch the radio to data mode");
        if (settle > TimeSpan.Zero) Thread.Sleep(settle);
        var check = Ask(IcomCiv.ReadDataMode(radio), 0x1A, 0x06, "check the data setting");
        if (check.Length < 3 || check[2] == 0)
            throw new RadioException($"The IC-9700 stayed in {name}: it didn't switch to {name}-D. Check its CI-V address " +
                                     "(Settings > Radio) matches the radio's menu, and that its \"CI-V USB Port\" is set to " +
                                     "\"Unlink from [REMOTE]\".");
        return (name, $"{name} → {name}-D");
    }

    /// <summary>Turns DATA off again after transmitting (<paramref name="mode"/> is what On returned).</summary>
    public void Off(string mode)
    {
        if (settle > TimeSpan.Zero) Thread.Sleep(settle);
        Tell(IcomCiv.SetDataMode(radio, on: false, 0), "switch the radio back from data mode");
        var check = Ask(IcomCiv.ReadDataMode(radio), 0x1A, 0x06, "check the data setting");
        if (check.Length >= 3 && check[2] != 0)
            throw new RadioException($"The IC-9700 is still in {mode}-D after transmitting. Switch DATA off on the radio.");
    }

    /// <summary>Sends a command that answers OK (FB) or not OK (FA).</summary>
    private void Tell(byte[] frame, string what)
    {
        var answer = exchange(frame, what);
        if (answer.Length == 1 && answer[0] == IcomCiv.NotOk)
            throw new RadioException($"The radio refused to {what} (it answered \"not OK\").");
    }

    /// <summary>Sends a question and returns the answer's body, which must start with the command asked.</summary>
    private byte[] Ask(byte[] frame, byte command, byte? subcommand, string what)
    {
        var answer = exchange(frame, what);
        bool matches = answer.Length >= 1 && answer[0] == command && (subcommand is null || (answer.Length >= 2 && answer[1] == subcommand));
        if (!matches) throw new RadioException($"Couldn't {what}: the radio's answer didn't make sense. Check the CI-V address.");
        return answer;
    }
}
