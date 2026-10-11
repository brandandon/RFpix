// FacDecoder.cs - reads the FAC (Fast Access Channel): 45 cells in every frame that
// announce how the rest of the transmission is sent.
//
// The sender's steps, which we undo in reverse:
//   1. 40 bits of settings + an 8-bit CRC checksum = 48 bits
//   2. scrambled ("energy dispersal")
//   3. error-correction coded at rate 3/5 (with 6 tail bits): 90 bits
//   4. shuffled (bit interleaver, t0 = 21)
//   5. paired into 45 four-point ("4-QAM") cells: the first bit of each pair sets
//      the cell's left/right side (0 = right), the second its top/bottom (0 = top).
//
// The 40 setting bits (from QSSTV's FAC.cpp):
//   0-1   which frame of 3 this is (a "superframe" is 3 frames, 1.2 s)
//   2     bandwidth: 0 = 2.2 kHz, 1 = 2.5 kHz
//   3     interleaving: 0 = long, 1 = short
//   4     0 = 64-QAM, 1 = 16-QAM or 4-QAM (bit 9 tells which)
//   5     protection: 0 = high, 1 = low (QSSTV calls it "normal")
//   6     1 = data (pictures and files); 0 would be audio
//   7-8   packet ID
//   9     1 = 4-QAM
//   10-30 three characters (7 bits each) of the sender's callsign; frames 1, 2
//         and 3 of a superframe carry characters 1-3, 4-6 and 7-9
//   31-39 not used

using System.Numerics;
using RFpix.HamDrm.Coding;

namespace RFpix.HamDrm;

public enum HamDrmInterleave { Long, Short }

public enum HamDrmProtection { High, Low }

/// <summary>One frame's FAC, decoded and checked.</summary>
/// <param name="FrameInSuperframe">0, 1 or 2.</param>
/// <param name="Qam">How many points each data cell's pattern has: 4, 16 or 64
/// (more points carry more bits per cell, but need a cleaner signal).</param>
/// <param name="LabelPart">This frame's three callsign characters (may be empty).</param>
public sealed record FacInfo(
    int FrameInSuperframe, HamDrmBandwidth Bandwidth, HamDrmInterleave Interleave, int Qam,
    HamDrmProtection Protection, bool IsData, int PacketId, string LabelPart)
{
    /// <summary>Do two FACs describe the same transmission? (Everything but the frame number
    /// and callsign letters, which change from frame to frame.)</summary>
    public bool SameSettings(FacInfo other) =>
        Bandwidth == other.Bandwidth && Interleave == other.Interleave && Qam == other.Qam &&
        Protection == other.Protection && IsData == other.IsData && PacketId == other.PacketId;

    /// <summary>For the screen, e.g. "2.5 kHz · 16-QAM · long interleave · high protection".</summary>
    public string Description =>
        $"{(Bandwidth == HamDrmBandwidth.Wide ? "2.5" : "2.2")} kHz  ·  {Qam}-QAM  ·  " +
        $"{Interleave.ToString().ToLowerInvariant()} interleave  ·  {Protection.ToString().ToLowerInvariant()} protection";
}

public static class FacDecoder
{
    public const int Cells = 45;
    public const int DataBits = 40;
    public const int TotalBits = 48;   // with the CRC

    private static readonly int[][] Steps =
        ConvolutionalCode.RepeatPattern(ConvolutionalCode.Patterns[6], TotalBits);
    private static readonly int[] Shuffle = BitInterleaver.Table(2 * Cells, 21);

    /// <summary>Decodes one frame's FAC. Returns null if the checksum fails.</summary>
    /// <param name="received">The 45 FAC cells as received, in order.</param>
    /// <param name="channel">The channel at each of those cells (from <see cref="ChannelEstimator"/>).</param>
    public static FacInfo? Decode(IReadOnlyList<Complex> received, IReadOnlyList<Complex> channel)
    {
        // Step 5 undone: each cell gives two "soft" bits. received x conj(channel) is the
        // cell with the channel's turn undone, and scaled by the channel's strength, so
        // cells that came through strongly count for more.
        var soft = new double[2 * Cells];
        for (int i = 0; i < Cells; i++)
        {
            Complex z = received[i] * Complex.Conjugate(channel[i]);
            soft[2 * i] = z.Real;          // right = 0, left = 1
            soft[2 * i + 1] = z.Imaginary; // top = 0, bottom = 1
        }

        var ordered = BitInterleaver.Unshuffle(soft, Shuffle);          // step 4 undone
        var bits = ConvolutionalCode.Decode(ordered, Steps, TotalBits); // step 3 undone
        EnergyDispersal.Apply(bits);                                    // step 2 undone

        // The checksum stops most damaged FACs, but by chance about 1 in 256 slips through.
        // The receiver catches those by checking that FACs agree with each other.
        if (Crc.Crc8(bits, 0, DataBits) != Crc.ToNumber(bits, DataBits, 8)) return null;
        return Parse(bits);
    }

    private static FacInfo Parse(byte[] b)
    {
        // Frame identity: 00 or 11 = first frame, 01 = second, 10 = third (as QSSTV reads it).
        int frame = (2 * b[0] + b[1]) % 3;
        int qam = b[4] == 0 ? 64 : b[9] == 1 ? 4 : 16;

        var label = new System.Text.StringBuilder();
        for (int c = 0; c < 3; c++)
        {
            int ch = Crc.ToNumber(b, 10 + 7 * c, 7);
            if (ch >= 32 && ch < 127) label.Append((char)ch);   // printable characters only
        }

        return new FacInfo(
            frame,
            b[2] == 1 ? HamDrmBandwidth.Wide : HamDrmBandwidth.Narrow,
            b[3] == 0 ? HamDrmInterleave.Long : HamDrmInterleave.Short,
            qam,
            b[5] == 0 ? HamDrmProtection.High : HamDrmProtection.Low,
            b[6] == 1,
            Crc.ToNumber(b, 7, 2),
            label.ToString());
    }
}
