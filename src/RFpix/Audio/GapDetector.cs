// GapDetector.cs - notices when sound went missing on its way in.
//
// Windows numbers every sample it captures (the "device position"). If a chunk is
// lost, because a program or driver was late or the USB sound link hiccupped, the
// numbering jumps ahead. Spotting the jump tells us exactly how much is missing,
// and putting that much silence in its place keeps the timing right.
//
// Why it matters: HamDRM's decoder locks onto the exact timing of the signal. Ten
// missing milliseconds (one of Windows' chunks) shift everything after them, and the
// decoder loses a frame or two while it finds the timing again: enough to spoil a
// picture (Dan's off-air tests, Oct 16, 2026). Ten milliseconds of silence instead
// damage only a sliver of one frame, which the error correction repairs.

namespace RFpix.Audio;

public sealed class GapDetector
{
    private long? _expected;   // the position the next chunk should start at

    /// <summary>Gaps found so far.</summary>
    public int Gaps { get; private set; }

    /// <summary>Gaps longer than this aren't filled (something bigger went wrong; start afresh).</summary>
    public long MaxFill { get; }

    public GapDetector(int sampleRate) => MaxFill = 2L * sampleRate;

    /// <summary>For each chunk as it arrives: where it starts (Windows' device position) and how
    /// long it is. Returns how many samples of silence to put in before it (usually 0).</summary>
    /// <param name="positionValid">False if Windows says the position is unreliable this time.</param>
    public long Missing(long position, int length, bool positionValid = true)
    {
        long missing = 0;
        if (positionValid && _expected is long expected && position > expected)
        {
            missing = position - expected;
            Gaps++;
            if (missing > MaxFill) missing = 0;
        }
        _expected = positionValid ? position + length : null;
        return missing;
    }

    public void Reset()
    {
        _expected = null;
        Gaps = 0;
    }
}
