// RadioSettings.cs - how RFpix reaches the radio, saved with the other settings.
//
// RadioChoices lists the radios on the Radio page, each with the settings that
// suit it to start with (so picking "Icom IC-9700" fills in what worked in QSSTV).

namespace RFpix.Radio;

/// <summary>How the transmitter is keyed.</summary>
public enum PttMethod
{
    /// <summary>A command over the control cable (CAT / CI-V).</summary>
    Cat,
    /// <summary>The control port's RTS line.</summary>
    Rts,
    /// <summary>The control port's DTR line.</summary>
    Dtr,
    /// <summary>The radio keys itself when it hears RFpix's audio; RFpix sends no key command.</summary>
    Vox,
}

public sealed class RadioSettings
{
    /// <summary>Which entry of <see cref="RadioChoices.All"/> (by its Id). "none" = no radio control.</summary>
    public string Choice { get; set; } = "none";

    /// <summary>Hamlib's number for the radio model (only used for "Other radio").</summary>
    public int OtherModel { get; set; }

    /// <summary>The control port, e.g. "COM4", or for a network program "localhost:4532".</summary>
    public string? Port { get; set; }

    public int BaudRate { get; set; } = 38400;

    /// <summary>Icom only: the radio's CI-V address in hex, e.g. "A2" (the IC-9700's default).</summary>
    public string? CivAddress { get; set; }

    public PttMethod Ptt { get; set; } = PttMethod.Cat;

    /// <summary>Switch the radio into data mode while transmitting (USB → USB-D) and back afterwards.</summary>
    public bool DataModeForTransmit { get; set; }

    /// <summary>How the transmitter is really keyed: with no radio control it can only be VOX,
    /// whatever <see cref="Ptt"/> says (it may hold an older choice).</summary>
    public PttMethod Keying() => RadioChoices.Find(Choice).Connection == "none" ? PttMethod.Vox : Ptt;
}

/// <summary>A radio on the Radio page's list.</summary>
/// <param name="HamlibModel">Hamlib's model number (0 = no radio control, -1 = typed in by the user).</param>
/// <param name="Connection">"serial" (a COM port), "network" (another program), or "none".</param>
public sealed record RadioChoice(string Id, string Name, int HamlibModel, string Connection,
                                 int BaudRate, string? CivAddress, PttMethod Ptt, bool DataMode, string Hint)
{
    public override string ToString() => Name;   // what the drop-down shows
}

public static class RadioChoices
{
    public static readonly IReadOnlyList<RadioChoice> All = new RadioChoice[]
    {
        new("ft991a", "Yaesu FT-991A", 1035, "serial", 38400, null, PttMethod.Cat, false,
            "Use the \"Enhanced COM Port\". The baud rate must match the radio's menu 031 CAT RATE."),
        new("ic9700", "Icom IC-9700", 3081, "serial", 19200, "A2", PttMethod.Rts, true,
            "Use the first of the radio's two COM ports. CI-V address A2 is the radio's default; " +
            "PTT by RTS is what worked in QSSTV (CAT works too)."),
        new("other", "Other radio (Hamlib model number)", -1, "serial", 9600, null, PttMethod.Cat, false,
            "Hamlib knows hundreds of radios: type part of yours below (such as \"7300\") and pick it from the list."),
        new("rigctld", "Shared through rigctld (another program owns the radio)", 2, "network", 0, null, PttMethod.Cat, false,
            "When another program already has the radio's port, run Hamlib's rigctld; RFpix connects to it (usually localhost:4532)."),
        new("flrig", "Shared through flrig", 4, "network", 0, null, PttMethod.Cat, false,
            "When flrig controls the radio, RFpix goes through it (usually localhost:12345)."),
        new("none", "No radio control (VOX only)", 0, "none", 0, null, PttMethod.Vox, false,
            "RFpix sends no commands: the radio's VOX keys it when RFpix's audio starts."),
    };

    public static RadioChoice Find(string? id) => All.FirstOrDefault(c => c.Id == id) ?? All[^1];

    /// <summary>Common baud rates for the drop-down.</summary>
    public static readonly int[] BaudRates = { 4800, 9600, 19200, 38400, 57600, 115200 };
}
