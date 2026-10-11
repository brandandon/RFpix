// WavWriter.cs - writes sound to a WAV file, a little at a time.
//
// The file is 16-bit mono PCM, the plainest kind of WAV: every sound program can
// open it, and RFpix's "Play recording…" button plays it back. A WAV file starts
// with a 44-byte header that includes the length of the sound, which isn't known
// until the end, so the header is written first with blanks and filled in on Dispose.

namespace RFpix.Audio;

public sealed class WavWriter : IDisposable
{
    private const int HeaderBytes = 44;
    private readonly FileStream _file;
    private readonly byte[] _buffer = new byte[8192];
    private long _samples;

    public int SampleRate { get; }

    /// <summary>Samples written so far.</summary>
    public long Samples => _samples;

    /// <summary>Length of the sound written so far.</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds((double)_samples / SampleRate);

    /// <summary>The file's size so far, in bytes.</summary>
    public long Bytes => HeaderBytes + _samples * 2;

    /// <summary>Creates (or replaces) the file. Throws IOException or UnauthorizedAccessException
    /// if it can't be written.</summary>
    public WavWriter(string path, int sampleRate)
    {
        SampleRate = sampleRate;
        _file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
        WriteHeader();   // with blanks for the lengths, for now
    }

    /// <summary>Adds sound. Samples run from -1 to +1; anything beyond is clipped.</summary>
    public void Write(ReadOnlySpan<float> samples)
    {
        while (samples.Length > 0)
        {
            int n = Math.Min(samples.Length, _buffer.Length / 2);
            for (int i = 0; i < n; i++)
            {
                short s = (short)Math.Round(Math.Clamp(samples[i], -1f, 1f) * 32767f);
                _buffer[i * 2] = (byte)s;
                _buffer[i * 2 + 1] = (byte)(s >> 8);
            }
            _file.Write(_buffer, 0, n * 2);
            _samples += n;
            samples = samples[n..];
        }
    }

    /// <summary>Fills in the lengths and closes the file.</summary>
    public void Dispose()
    {
        try
        {
            _file.Position = 0;
            WriteHeader();
        }
        finally
        {
            _file.Dispose();
        }
    }

    private void WriteHeader()
    {
        long dataBytes = _samples * 2;
        var h = new byte[HeaderBytes];
        void Text(int at, string s) { for (int i = 0; i < 4; i++) h[at + i] = (byte)s[i]; }
        void U32(int at, long v) => BitConverter.TryWriteBytes(h.AsSpan(at, 4), (uint)Math.Min(v, uint.MaxValue));
        void U16(int at, int v) => BitConverter.TryWriteBytes(h.AsSpan(at, 2), (ushort)v);

        Text(0, "RIFF"); U32(4, 36 + dataBytes); Text(8, "WAVE");
        Text(12, "fmt "); U32(16, 16);
        U16(20, 1);                 // PCM
        U16(22, 1);                 // mono
        U32(24, SampleRate);
        U32(28, SampleRate * 2);    // bytes per second
        U16(32, 2);                 // bytes per sample
        U16(34, 16);                // bits per sample
        Text(36, "data"); U32(40, dataBytes);
        _file.Write(h);
    }
}
