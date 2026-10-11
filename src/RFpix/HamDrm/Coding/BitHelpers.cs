// BitHelpers.cs - three small pieces used on HamDRM's bits:
//   - the bit interleaver, which shuffles coded bits so a burst of damage
//     (a fade, a click) is spread out and the decoder can repair it;
//   - energy dispersal, which scrambles the data with a fixed pseudo-random
//     pattern so long runs of 0s or 1s don't make lopsided signals;
//   - the 8-bit CRC checksum, which proves a block arrived intact.
// All from QSSTV (BlockInterleaver.cpp, EnergyDispersal.cpp, CRC.cpp) and the DRM standard.

namespace RFpix.HamDrm.Coding;

public static class BitInterleaver
{
    /// <summary>The shuffle order for a block of <paramref name="length"/> bits: the sender
    /// sends bit table[i] in position i. <paramref name="t0"/> is 21 for the FAC.</summary>
    public static int[] Table(int length, int t0)
    {
        // The DRM standard's formula (7.3.3): with s the next power of two at or above
        // the length, each position is (t0 x previous + s/4 - 1) mod s, skipping any
        // that fall past the end.
        int s = 1;
        while (s < length) s <<= 1;
        int q = s / 4 - 1;
        var table = new int[length];
        for (int i = 1; i < length; i++)
        {
            int v = (t0 * table[i - 1] + q) % s;
            while (v >= length) v = (t0 * v + q) % s;
            table[i] = v;
        }
        return table;
    }

    /// <summary>Puts received values back in their original order.</summary>
    public static T[] Unshuffle<T>(IReadOnlyList<T> received, int[] table)
    {
        var original = new T[received.Count];
        for (int i = 0; i < received.Count; i++) original[table[i]] = received[i];
        return original;
    }
}

public static class EnergyDispersal
{
    /// <summary>Scrambles (or unscrambles: it's the same operation) bits in place.</summary>
    public static void Apply(byte[] bits)
    {
        // A 9-bit shift register, all ones to start; each step's new bit is bit 5 XOR
        // bit 9 (the polynomial x^9 + x^5 + 1), and it's XORed onto the data.
        int register = 0x1FF;
        for (int i = 0; i < bits.Length; i++)
        {
            int prbs = ((register >> 4) ^ (register >> 8)) & 1;
            register = ((register << 1) | prbs) & 0x1FF;
            bits[i] ^= (byte)prbs;
        }
    }
}

public static class Crc
{
    /// <summary>DRM's 8-bit CRC (polynomial x^8 + x^4 + x^3 + x^2 + 1, start all ones,
    /// result inverted) of <paramref name="count"/> bits starting at <paramref name="start"/>.</summary>
    public static int Crc8(IReadOnlyList<byte> bits, int start, int count)
    {
        int crc = 0xFF;
        for (int i = start; i < start + count; i++)
        {
            int top = ((crc >> 7) ^ bits[i]) & 1;
            crc = ((crc << 1) & 0xFF) ^ (top != 0 ? 0x1D : 0);
        }
        return ~crc & 0xFF;
    }

    /// <summary>Reads <paramref name="count"/> bits as a number, first bit most significant.</summary>
    public static int ToNumber(IReadOnlyList<byte> bits, int start, int count)
    {
        int n = 0;
        for (int i = start; i < start + count; i++) n = (n << 1) | bits[i];
        return n;
    }
}
