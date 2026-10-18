// SendProfile.cs - a named set of send settings, such as "Net repeater (70 cm)".
//
// A profile holds everything about how a picture goes out except the picture: the HamDRM
// settings (mode, bandwidth, QAM, interleave, protection), Reed-Solomon, and the target
// transmit time the picture is fitted to. Pick one on the Send tab and the drop-downs
// fill in; change them and save the result as a new profile.

using System.Text.Json.Serialization;
using RFpix.HamDrm;

namespace RFpix.Send;

/// <param name="ReedSolomon">0 for none, or RS1 to RS4.</param>
/// <param name="TargetSeconds">The transmit time pictures are fitted to.</param>
public sealed record SendProfile(
    string Name, HamDrmMode Mode, HamDrmBandwidth Bandwidth, int Qam, HamDrmInterleave Interleave,
    HamDrmProtection Protection, int ReedSolomon, int TargetSeconds)
{
    /// <summary>The HamDRM part, for the transmitter.</summary>
    [JsonIgnore]
    public TransmitSettings Settings => new(Mode, Bandwidth, Qam, Interleave, Protection);

    /// <summary>True if the two send the same way, whatever they're called.</summary>
    public bool SameAs(SendProfile other) => this with { Name = other.Name } == other;

    /// <summary>The net's proven repeater settings (Oct 2026: every test came through on the
    /// 70 cm repeater with these), fitted to 2 minutes.</summary>
    public static SendProfile NetRepeater { get; } = new("Net repeater (70 cm)", HamDrmMode.B, HamDrmBandwidth.Wide, 16,
        HamDrmInterleave.Long, HamDrmProtection.High, ReedSolomon: 0, TargetSeconds: 120);

    /// <summary>The profiles that come with RFpix (they can't be deleted).</summary>
    public static IReadOnlyList<SendProfile> BuiltIn { get; } = new[] { NetRepeater };
}
