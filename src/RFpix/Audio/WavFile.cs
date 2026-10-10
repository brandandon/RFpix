// WavFile.cs - reads a .wav sound file into memory.
//
// A WAV file is a short header describing the sound (how many samples per
// second, how many channels, how many bits per sample) followed by the samples
// themselves. The header is split into labelled "chunks": "fmt " describes the
// sound and "data" holds it. Other chunks (notes, cue points) are skipped.
//
// We turn whatever is in the file into the same thing the sound card gives us:
// one channel of numbers between -1 and +1. Like the live input, only the first
// (left) channel is kept, because radios send the same audio on both.

namespace RFpix.Audio;

/// <summary>The sound from a WAV file, ready to use.</summary>
/// <param name="Samples">One channel of samples between -1 and +1.</param>
/// <param name="SampleRate">Samples per second, e.g. 48000.</param>
/// <param name="Channels">How many channels the file had (only the first is kept).</param>
/// <param name="BitsPerSample">How the file stored each sample: 8, 16, 24 or 32.</param>
public sealed record WavAudio(float[] Samples, int SampleRate, int Channels, int BitsPerSample)
{
    public TimeSpan Duration => TimeSpan.FromSeconds((double)Samples.Length / SampleRate);

    /// <summary>A short description such as "48000 Hz, 16-bit, 1 channel(s), 0:42".</summary>
    public string Description =>
        $"{SampleRate} Hz, {BitsPerSample}-bit, {Channels} channel(s), {Duration:m\\:ss}";
}

public static class WavFile
{
    // Format codes stored in the "fmt " chunk.
    private const int FormatPcm = 1;          // whole numbers (the usual kind)
    private const int FormatFloat = 3;        // decimal numbers between -1 and +1
    private const int FormatExtensible = 0xFFFE; // newer header; the real code comes later in the chunk

    /// <summary>Reads a WAV file. Throws <see cref="AudioException"/> with a plain
    /// explanation if the file can't be used.</summary>
    public static WavAudio Read(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            throw new AudioException($"Couldn't open \"{Path.GetFileName(path)}\": {ex.Message}", ex);
        }
        return Read(bytes, Path.GetFileName(path));
    }

    /// <summary>Reads WAV data already in memory. <paramref name="name"/> is only used in error messages.</summary>
    public static WavAudio Read(byte[] bytes, string name = "the file")
    {
        string Problem(string what) => $"\"{name}\" can't be played: {what}";

        if (bytes.Length < 12 || Tag(bytes, 0) != "RIFF" || Tag(bytes, 8) != "WAVE")
            throw new AudioException(Problem("it isn't a WAV file."));

        int format = 0, channels = 0, sampleRate = 0, bits = 0;
        int dataStart = -1, dataLength = 0;

        // Walk through the chunks: each is a 4-letter label, a 4-byte length, then its contents.
        int pos = 12;
        while (pos + 8 <= bytes.Length)
        {
            string tag = Tag(bytes, pos);
            int length = BitConverter.ToInt32(bytes, pos + 4);
            int body = pos + 8;
            if (length < 0) break;

            if (tag == "fmt " && length >= 16 && body + 16 <= bytes.Length)
            {
                format = BitConverter.ToUInt16(bytes, body);
                channels = BitConverter.ToUInt16(bytes, body + 2);
                sampleRate = BitConverter.ToInt32(bytes, body + 4);
                bits = BitConverter.ToUInt16(bytes, body + 14);
                if (format == FormatExtensible && length >= 26 && body + 26 <= bytes.Length)
                    format = BitConverter.ToUInt16(bytes, body + 24);
            }
            else if (tag == "data")
            {
                dataStart = body;
                // Some programs write a wrong length (or 0) if recording was cut short:
                // use whatever is actually there.
                dataLength = Math.Min(length, bytes.Length - body);
                if (length == 0) dataLength = bytes.Length - body;
                break;
            }

            pos = body + length + (length & 1);   // chunks are padded to an even length
        }

        if (channels == 0 || sampleRate == 0)
            throw new AudioException(Problem("its sound format description is missing."));
        if (dataStart < 0)
            throw new AudioException(Problem("it contains no sound."));

        bool supported = (format == FormatPcm && bits is 8 or 16 or 24 or 32)
                      || (format == FormatFloat && bits == 32);
        if (!supported)
            throw new AudioException(Problem(
                $"it uses a sound format RFpix doesn't read (code {format}, {bits}-bit). " +
                "Save it as ordinary 16-bit WAV and try again."));

        int bytesPerSample = bits / 8;
        int frameSize = bytesPerSample * channels;
        int frames = dataLength / frameSize;
        var samples = new float[frames];

        for (int i = 0; i < frames; i++)
        {
            int o = dataStart + i * frameSize;   // first (left) channel only
            samples[i] = format == FormatFloat
                ? BitConverter.ToSingle(bytes, o)
                : bits switch
                {
                    8 => (bytes[o] - 128) / 128f,                       // 8-bit WAV is stored 0..255
                    16 => BitConverter.ToInt16(bytes, o) / 32768f,
                    24 => ((bytes[o] | bytes[o + 1] << 8 | (sbyte)bytes[o + 2] << 16)) / 8388608f,
                    _ => BitConverter.ToInt32(bytes, o) / 2147483648f,
                };
        }

        return new WavAudio(samples, sampleRate, channels, bits);
    }

    private static string Tag(byte[] b, int at) =>
        at + 4 <= b.Length ? System.Text.Encoding.ASCII.GetString(b, at, 4) : "";
}
