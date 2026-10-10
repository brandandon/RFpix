// TestWav.cs - builds small WAV files in memory, so tests can try every storage format.

namespace RFpix.Tests;

public static class TestWav
{
    /// <summary>Makes WAV file bytes. <paramref name="samples"/> are between -1 and +1;
    /// for stereo, give them interleaved (left, right, left, right...).</summary>
    /// <param name="format">1 = whole numbers (PCM), 3 = decimal (float), anything else for error tests.</param>
    /// <param name="extensible">Write the newer "extensible" header.</param>
    /// <param name="extraChunk">Put a "LIST" notes chunk before the sound, as many programs do.</param>
    public static byte[] Build(float[] samples, int sampleRate, int channels = 1, int format = 1,
                               int bits = 16, bool extensible = false, bool extraChunk = false)
    {
        var data = new MemoryStream();
        var d = new BinaryWriter(data);
        foreach (float s in samples)
        {
            if (format == 3) d.Write(s);
            else if (bits == 8) d.Write((byte)Math.Round(s * 127 + 128));
            else if (bits == 16) d.Write((short)Math.Round(Math.Clamp(s * 32768, -32768, 32767)));
            else if (bits == 24)
            {
                int v = (int)Math.Round(Math.Clamp(s * 8388608, -8388608, 8388607));
                d.Write((byte)v); d.Write((byte)(v >> 8)); d.Write((byte)(v >> 16));
            }
            else if (bits == 32) d.Write((int)Math.Round(Math.Clamp(s * 2147483648.0, int.MinValue, int.MaxValue)));
            else d.Write((byte)0);   // unsupported formats: contents don't matter
        }
        byte[] sound = data.ToArray();

        var file = new MemoryStream();
        var w = new BinaryWriter(file);
        w.Write("RIFF"u8); w.Write(0); w.Write("WAVE"u8);

        if (extraChunk)
        {
            w.Write("LIST"u8); w.Write(5); w.Write("notes"u8); w.Write((byte)0);   // odd length + pad byte
        }

        int blockAlign = Math.Max(1, channels * bits / 8);
        w.Write("fmt "u8);
        w.Write(extensible ? 40 : 16);
        w.Write((short)(extensible ? 0xFFFE : format));
        w.Write((short)channels);
        w.Write(sampleRate);
        w.Write(sampleRate * blockAlign);
        w.Write((short)blockAlign);
        w.Write((short)bits);
        if (extensible)
        {
            w.Write((short)22);              // size of the extra part
            w.Write((short)bits);            // valid bits
            w.Write(0);                      // speaker positions
            w.Write((short)format);          // the real format code, first 2 bytes of a 16-byte ID
            w.Write(new byte[14]);
        }

        w.Write("data"u8); w.Write(sound.Length); w.Write(sound);

        byte[] bytes = file.ToArray();
        BitConverter.GetBytes(bytes.Length - 8).CopyTo(bytes, 4);   // fill in the total length
        return bytes;
    }
}
