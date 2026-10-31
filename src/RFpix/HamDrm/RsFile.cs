// RsFile.cs - QSSTV/EasyPal's "RS1" to "RS4" file wrapping, around a Reed-Solomon code.
//
// Wrapping (the sender):
//   1. Put a 7-byte header in front of the file: how much padding the last block
//      needs, how many blocks there are, and the file's 3-letter extension.
//   2. Cut that into k-byte pieces and turn each into a 255-byte Reed-Solomon block.
//   3. Send the blocks "across": byte 0 of every block, then byte 1 of every block,
//      and so on. A lost piece of the transfer then costs each block only a few
//      bytes, which its repair data can rebuild.
//   The file is sent with extension .rs1 to .rs4 instead of its own.
// Unwrapping does the reverse. The header holds only 3 letters, so RFpix's AVIF
// pictures (".avif") are wrapped as "avf" and get their 4 letters back when unwrapped. Ported from QSSTV's reedsolomoncoder.cpp.

using System.Text;
using RFpix.HamDrm.Coding;

namespace RFpix.HamDrm;

public static class RsFile
{
    /// <summary>Data bytes per 255-byte block for RS1 to RS4 (more repair data = stronger).</summary>
    public static int DataBytes(int level) => level switch
    {
        1 => 224, 2 => 192, 3 => 160, 4 => 128,
        _ => throw new ArgumentOutOfRangeException(nameof(level), "RS levels are 1 to 4."),
    };

    /// <summary>The RS level a file name shows (img10.rs1 -> 1), or null if it isn't wrapped.</summary>
    public static int? LevelOf(string name)
    {
        string ext = Path.GetExtension(name).ToLowerInvariant();
        return ext is ".rs1" or ".rs2" or ".rs3" or ".rs4" ? ext[3] - '0' : null;
    }

    /// <summary>Wraps a file for sending, as QSSTV does. Returns the bytes to send; the file's
    /// name should then end in .rs1 to .rs4 in place of its extension.</summary>
    public static byte[] Wrap(byte[] file, string extension, int level)
    {
        int k = DataBytes(level);
        var rs = new ReedSolomon(k);
        int blocks = (file.Length + 7 + k - 1) / k;

        // The 7-byte header, then the file, then zeros to fill the last block.
        var data = new byte[blocks * k];
        data[0] = (byte)(k - file.Length % k);       // QSSTV's "surplus" value
        data[1] = (byte)(blocks % 256);
        data[2] = (byte)(blocks / 256);
        var ext = Encoding.Latin1.GetBytes(Shortened(extension.TrimStart('.')));
        for (int i = 0; i < 3 && i < ext.Length; i++) data[3 + i] = ext[i];
        file.CopyTo(data, 7);

        var coded = new byte[blocks * ReedSolomon.BlockSize];
        for (int bl = 0; bl < blocks; bl++)
        {
            Array.Copy(data, bl * k, coded, bl * ReedSolomon.BlockSize, k);
            rs.Encode(coded, bl * ReedSolomon.BlockSize);
        }

        // Send across the blocks: sent[c * blocks + b] = block b, byte c.
        var sent = new byte[coded.Length];
        for (int bl = 0; bl < blocks; bl++)
            for (int c = 0; c < ReedSolomon.BlockSize; c++)
                sent[c * blocks + bl] = coded[bl * ReedSolomon.BlockSize + c];
        return sent;
    }

    /// <summary>Unwraps a received .rs1-.rs4 file.</summary>
    /// <param name="received">The bytes as received (missing ones can be anything).</param>
    /// <param name="level">1 to 4, from the file name.</param>
    /// <param name="missing">Which received bytes are known to be missing (from pieces of the
    /// transfer that never arrived), or null if all arrived.</param>
    /// <returns>The original file and its extension (e.g. "jpg"), or null if it couldn't be repaired.</returns>
    public static (byte[] File, string Extension)? Unwrap(byte[] received, int level, Func<int, bool>? missing = null)
    {
        int k = DataBytes(level);
        var rs = new ReedSolomon(k);
        int blocks = (received.Length + ReedSolomon.BlockSize - 1) / ReedSolomon.BlockSize;
        if (blocks == 0) return null;

        // Gather each block's bytes back together, noting which are missing.
        var coded = new byte[blocks * ReedSolomon.BlockSize];
        var erasures = new List<int>[blocks];
        for (int bl = 0; bl < blocks; bl++) erasures[bl] = new List<int>();
        for (int bl = 0; bl < blocks; bl++)
            for (int c = 0; c < ReedSolomon.BlockSize; c++)
            {
                int at = c * blocks + bl;
                if (at < received.Length) coded[bl * ReedSolomon.BlockSize + c] = received[at];
                if (at >= received.Length || (missing?.Invoke(at) ?? false)) erasures[bl].Add(c);
            }

        // Repair each block, and keep its data part.
        var data = new byte[blocks * k];
        for (int bl = 0; bl < blocks; bl++)
        {
            if (rs.Decode(coded, bl * ReedSolomon.BlockSize, erasures[bl]) < 0) return null;
            Array.Copy(coded, bl * ReedSolomon.BlockSize, data, bl * k, k);
        }

        // Read the 7-byte header.
        int storedBlocks = data[1] + 256 * data[2];
        if (storedBlocks != blocks) return null;
        int size = blocks * k - data[0];
        if (size < 0) return null;
        // When a file's length lands in the last 7 bytes of a block, QSSTV's header says the
        // file is one block longer than it is; like QSSTV, just take what's there.
        size = Math.Min(size, data.Length - 7);
        string extension = Lengthened(Encoding.Latin1.GetString(data, 3, 3).TrimEnd('\0'));
        var file = data[7..(7 + size)];
        return (extension == "avif" ? WithoutPadding(file) : file, extension);
    }

    /// <summary>An AVIF file without zeros after its end. (The quirk above can leave up to a
    /// block of padding on the end; JPEG and JPEG 2000 readers ignore it, but an AVIF
    /// reader could take the zeros for a broken extra section.) An AVIF is a row of
    /// "boxes", each starting with its length, so its true end is where the last box ends.</summary>
    internal static byte[] WithoutPadding(byte[] file)
    {
        long end = 0;
        while (end + 8 <= file.Length)
        {
            long size = ((long)file[end] << 24) | ((long)file[end + 1] << 16) | ((long)file[end + 2] << 8) | file[end + 3];
            if (size < 8 || end + size > file.Length) break;
            end += size;
        }
        if (end == 0 || end == file.Length) return file;
        for (long i = end; i < file.Length; i++) if (file[i] != 0) return file;   // not just padding: leave it
        return file[..(int)end];
    }

    // The extensions longer than the header's 3 letters, and what's stored in their place.
    private static string Shortened(string extension) =>
        extension.Equals("avif", StringComparison.OrdinalIgnoreCase) ? "avf" : extension;

    private static string Lengthened(string extension) =>
        extension.Equals("avf", StringComparison.OrdinalIgnoreCase) ? "avif" : extension;
}
