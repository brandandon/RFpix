// FacEncoder.cs - builds a frame's FAC (Fast Access Channel): the 45 cells that tell
// the receiver how the transmission is sent. The reverse of FacDecoder (see there
// for the 40 setting bits). Ported from QSSTV's FAC.cpp.

using System.Numerics;
using RFpix.HamDrm.Coding;

namespace RFpix.HamDrm;

public static class FacEncoder
{
    /// <summary>The most callsign characters the FAC carries (3 per frame, 3 frames).</summary>
    public const int MaxLabelLength = 9;

    private static readonly int[][] Steps =
        ConvolutionalCode.RepeatPattern(ConvolutionalCode.Patterns[6], FacDecoder.TotalBits);
    private static readonly int[] Shuffle = BitInterleaver.Table(2 * FacDecoder.Cells, 21);
    private static readonly double Point = (float)0.7071067811;   // QSSTV's single-precision value

    /// <summary>The 48 FAC bits (settings and checksum) for one frame.</summary>
    /// <param name="frameInSuperframe">0, 1 or 2.</param>
    /// <param name="label">The sender's callsign: characters 3 x frame to 3 x frame + 2 go in this frame.</param>
    /// <param name="unused">Bits 31-39, reserved: always 0 from RFpix. (QSSTV leaves stray values
    /// there; the tests use this to rebuild QSSTV's signal exactly.)</param>
    public static byte[] Bits(int frameInSuperframe, TransmitSettings settings, string label, int unused = 0)
    {
        var bits = new List<byte>(FacDecoder.TotalBits);
        void Put(int value, int count) { for (int i = count - 1; i >= 0; i--) bits.Add((byte)((value >> i) & 1)); }

        Put(frameInSuperframe switch { 0 => 0b11, 1 => 0b01, _ => 0b10 }, 2);
        Put(settings.Bandwidth == HamDrmBandwidth.Wide ? 1 : 0, 1);
        Put(settings.Interleave == HamDrmInterleave.Long ? 0 : 1, 1);
        Put(settings.Qam == 64 ? 0 : 1, 1);
        Put(settings.Protection == HamDrmProtection.High ? 0 : 1, 1);
        Put(1, 1);    // data
        Put(0, 2);    // packet ID
        Put(settings.Qam == 4 ? 1 : 0, 1);
        int length = Math.Min(label.Length, MaxLabelLength);
        for (int i = 3 * frameInSuperframe; i < 3 * frameInSuperframe + 3; i++)
            Put(i < length ? label[i] & 127 : 0, 7);
        Put(unused, 9);   // reserved
        Put(Crc.Crc8(bits, 0, FacDecoder.DataBits), 8);
        return bits.ToArray();
    }

    /// <summary>The frame's 45 FAC cells, in the order they go into the frame.</summary>
    public static Complex[] Encode(int frameInSuperframe, TransmitSettings settings, string label, int unused = 0)
    {
        var bits = Bits(frameInSuperframe, settings, label, unused);
        EnergyDispersal.Apply(bits);
        var coded = ConvolutionalCode.Encode(bits, Steps);
        var cells = new Complex[FacDecoder.Cells];
        for (int i = 0; i < cells.Length; i++)
            cells[i] = new Complex(coded[Shuffle[2 * i]] == 0 ? Point : -Point,
                                   coded[Shuffle[2 * i + 1]] == 0 ? Point : -Point);
        return cells;
    }
}
