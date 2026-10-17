// GapTests.cs - checks that sound lost on its way in is noticed and filled with silence,
// and why that matters: Dan's off-air recordings (Oct 16, 2026) had 10 ms of sound
// missing now and then, and a picture with 4 such gaps failed. The same 4 gaps filled
// with silence cost nothing.

using RFpix.Audio;
using RFpix.HamDrm;
using RFpix.Send;
using Xunit;
using static RFpix.Tests.TestAudio;

namespace RFpix.Tests;

public class GapTests
{
    [Fact]
    public void Steady_sound_has_no_gaps()
    {
        var gaps = new GapDetector(48000);
        for (long pos = 1000; pos < 100_000; pos += 480) Assert.Equal(0, gaps.Missing(pos, 480));
        Assert.Equal(0, gaps.Gaps);
    }

    [Fact]
    public void A_jump_in_Windows_numbering_is_a_gap_of_exactly_that_length()
    {
        var gaps = new GapDetector(48000);
        Assert.Equal(0, gaps.Missing(0, 480));
        Assert.Equal(0, gaps.Missing(480, 480));
        Assert.Equal(480, gaps.Missing(1440, 480));   // 960 to 1440 never arrived: 10 ms
        Assert.Equal(0, gaps.Missing(1920, 441));      // chunks needn't be equal
        Assert.Equal(1, gaps.Gaps);
    }

    [Fact]
    public void An_unreliable_position_or_a_huge_jump_starts_afresh_without_filling()
    {
        var gaps = new GapDetector(48000);
        gaps.Missing(0, 480);
        Assert.Equal(0, gaps.Missing(5000, 480, positionValid: false));   // Windows says: don't trust it
        Assert.Equal(0, gaps.Missing(9000, 480));                          // so this one only sets a new start
        Assert.Equal(0, gaps.Missing(9480 + 10 * 48000, 480));             // 10 s missing: not filled
        Assert.Equal(1, gaps.Gaps);
    }

    [Fact]
    public void Four_10ms_gaps_spoil_a_picture_but_filled_with_silence_cost_nothing()
    {
        var test = TestTransmissions.All.Single(t => t.Number == 5);   // the one that failed on the air
        var signal = new HamDrmTransmitter(test.FileName, test.Picture(), test.Settings, "KD9DD").Audio();
        var clean = new float[signal.Length + 96000];
        Array.Copy(signal, 0, clean, 48000, signal.Length);
        var noisy = AddNoise(clean, 22, seed: 5);   // about the quality measured on the air
        int[] gapsAt = { 600_000, 1_000_000, 1_400_000, 1_800_000 };

        var dropped = noisy.ToList();
        foreach (int at in gapsAt.Reverse()) dropped.RemoveRange(at, 480);
        var filled = (float[])noisy.Clone();
        foreach (int at in gapsAt) Array.Clear(filled, at, 480);

        Assert.Null(Decode(dropped.ToArray()));
        Assert.Equal(test.Picture(), Decode(filled)?.Data);
    }

    private static ReceivedFile? Decode(float[] audio)
    {
        var receiver = new HamDrmReceiver();
        ReceivedFile? received = null;
        receiver.FileReceived += f => received ??= f;
        for (int pos = 0; pos + 480 <= audio.Length; pos += 480) receiver.AddSamples(audio[pos..(pos + 480)], 480, 48000);
        receiver.EndOfAudio();
        return received;
    }
}
