// SendProfile.cs - a named set of send settings, such as "Net repeater (70 cm)".
//
// A profile holds everything about how a picture goes out except the picture: the HamDRM
// settings (mode, bandwidth, QAM, interleave, protection), Reed-Solomon, the target
// transmit time the picture is fitted to, and for a repeater its time-out and how long
// to wait after keying before the data starts (the "lead-in", while the repeater opens).
// Pick one on the Send tab and the drop-downs fill in; change them and save the result
// as a new profile.

using System.Text.Json.Serialization;
using RFpix.HamDrm;

namespace RFpix.Send;

/// <param name="ReedSolomon">0 for none, or RS1 to RS4.</param>
/// <param name="TargetSeconds">The transmit time pictures are fitted to.</param>
/// <param name="TimeoutSeconds">The repeater's time-out: the longest it stays on the air for one
/// transmission (0 for no repeater). Nothing longer is sent.</param>
/// <param name="LeadInSeconds">Silence after keying, before the data, while the repeater opens.</param>
/// <param name="RepairPercent">RFpix's repair code (the fountain code): how much extra it adds, in
/// percent of the picture, or 0 for none. Used in place of Reed-Solomon (which is then 0).</param>
/// <param name="FmSpeed">0 to send HamDRM; 1 to 5 to send RFpix's own FM signal at that speed
/// (FM 1 to FM 5), which always carries the fountain code (RepairPercent). The HamDRM settings are kept
/// but not used then.</param>
public sealed record SendProfile(
    string Name, HamDrmMode Mode, HamDrmBandwidth Bandwidth, int Qam, HamDrmInterleave Interleave,
    HamDrmProtection Protection, int ReedSolomon, int TargetSeconds, int TimeoutSeconds = 0, double LeadInSeconds = 0.3,
    int RepairPercent = 0, int FmSpeed = 0)
{
    /// <summary>RFpix's FM signal at the chosen speed, or null for HamDRM.</summary>
    [JsonIgnore]
    public RFpix.FmSignal.FmSignalMode? FmMode => FmSpeed > 0 ? RFpix.FmSignal.FmSignalMode.ById(FmSpeed - 1) : null;

    /// <summary>For the screen, the log and the Gallery: "B, 2.5 kHz, 16-QAM..." or "RFpix FM 3 (16-QAM 2/3), fountain 20%".</summary>
    [JsonIgnore]
    public string SignalText => FmMode is { } m ? $"{m.Description}, fountain {Math.Max(RepairPercent, FountainChoices[0])}%" : Settings.Description;

    /// <summary>The fountain code's sizes on the Send page, in percent extra. From the test bench
    /// (Nov 14, 2026): 20% is the quickest (quicker than no repair data at all), 50% is as good as or
    /// better than RS2 and still quicker than no repair data, 75% beats RS2 on every fading path.</summary>
    public static IReadOnlyList<int> FountainChoices { get; } = new[] { 20, 50, 75 };

    /// <summary>Which of <see cref="FountainChoices"/> is closest to <paramref name="percent"/> (its place in the list).</summary>
    public static int NearestFountainChoice(int percent) =>
        Enumerable.Range(0, FountainChoices.Count).MinBy(i => Math.Abs(FountainChoices[i] - percent));

    /// <summary>For the screen and the log: "RS2", "fountain 20%" or "no repair data".</summary>
    [JsonIgnore]
    public string RepairText => FmMode is not null ? $"fountain {Math.Max(RepairPercent, FountainChoices[0])}%" : RepairPercent > 0 ? $"fountain {RepairPercent}%" : ReedSolomon > 0 ? $"RS{ReedSolomon}" : "no repair data";

    /// <summary>The HamDRM part, for the transmitter.</summary>
    [JsonIgnore]
    public TransmitSettings Settings => new(Mode, Bandwidth, Qam, Interleave, Protection);

    /// <summary>True if the two send the same way, whatever they're called.</summary>
    public bool SameAs(SendProfile other) => this with { Name = other.Name } == other;

    /// <summary>The net's proven repeater settings (Oct 2026: every test came through on the
    /// 70 cm repeater with these, after 0.3 s of lead-in), fitted to 2 minutes, with a
    /// 3-minute time-out (the usual).</summary>
    public static SendProfile NetRepeater { get; } = new("Net repeater (70 cm)", HamDrmMode.B, HamDrmBandwidth.Wide, 16,
        HamDrmInterleave.Long, HamDrmProtection.High, ReedSolomon: 0, TargetSeconds: 120, TimeoutSeconds: 180, LeadInSeconds: 0.3);

    /// <summary>The time on the air apart from the picture: the lead-in, the waterfall text
    /// (<paramref name="text"/>, before and after) and the short silence at the end.</summary>
    public TimeSpan Overhead(TimeSpan text) =>
        TimeSpan.FromSeconds(LeadInSeconds) + text + Transmission.Tail;

    /// <summary>The target time for fitting the picture: the chosen one, shortened if need be so
    /// the whole transmission (overhead included, and Auto's 3 extra seconds) stays inside the time-out.</summary>
    public int FittingSeconds(TimeSpan text)
    {
        if (TimeoutSeconds <= 0) return TargetSeconds;
        double room = TimeoutSeconds - Overhead(text).TotalSeconds - PictureFitter.AutoAllowance.TotalSeconds;
        return Math.Max(10, Math.Min(TargetSeconds, (int)Math.Floor(room)));
    }

    /// <summary>True if a transmission of this many seconds on the air (overhead included) fits the time-out.</summary>
    public bool FitsTimeout(TimeSpan onAir) => TimeoutSeconds <= 0 || onAir.TotalSeconds <= TimeoutSeconds;

    /// <summary>The profiles that come with RFpix (they can't be deleted).</summary>
    public static IReadOnlyList<SendProfile> BuiltIn { get; } = new[] { NetRepeater };
}
