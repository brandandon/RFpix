// SubToneTests.cs - checks that a repeater's sub-audible tone (CTCSS) in the received sound doesn't
// spoil HamDRM decoding. FM radios often pass the tone out with the sound: Dan's FT-991A did, through a
// repeater with a 123 Hz tone (Nov 17, 2026), and only 49 of 70 pieces came through. Before the
// receiver notched such tones out (SubTones), a tone 5 dB below the picture signal stopped decoding.

using System.Numerics;
using RFpix.Dsp;
using RFpix.HamDrm;
using Xunit;

namespace RFpix.Tests;

public class SubToneTests
{
    private static readonly TransmitSettings NetSettings =
        new(HamDrmMode.B, HamDrmBandwidth.Wide, 16, HamDrmInterleave.Long, HamDrmProtection.High);

    [Theory]
    [InlineData(67.0)]    // the lowest CTCSS tone
    [InlineData(123.0)]   // the repeater in Dan's recording
    [InlineData(254.1)]   // the highest
    [InlineData(60.0)]    // mains hum
    public void A_strong_sub_audible_tone_in_the_sound_doesnt_spoil_the_picture(double hz)
    {
        var picture = File.ReadAllBytes(Path.Combine(Recordings.Folder, "images", "img04.jpg"));
        var signal = new HamDrmTransmitter("tone.jpg", picture, NetSettings, "N0CALL").Audio();
        // The tone 5 dB below the picture signal, from start to end.
        double power = signal.Average(s => (double)s * s);
        double amplitude = Math.Sqrt(2 * power * Math.Pow(10, -5 / 10.0));
        var sound = signal.Select((s, i) => (float)(s + amplitude * Math.Sin(2 * Math.PI * hz * i / 48000))).ToArray();

        var (received, _, _) = TransmitterTests.ReceiveSound(sound);
        Assert.NotNull(received);
        Assert.Equal(picture, received!.Data);
    }

    /// <summary>A steady tone and some band noise, for the tone finder.</summary>
    private static float[] ToneInNoise(double hz, double seconds, double toneLevel, int seed = 1)
    {
        var noise = TestAudio.Noise((int)(48000 * seconds), 0.1, seed);
        return noise.Select((v, i) => (float)(v + toneLevel * Math.Sin(2 * Math.PI * hz * i / 48000))).ToArray();
    }

    [Theory]
    [InlineData(67.0)]
    [InlineData(123.0)]
    [InlineData(254.1)]
    public void A_tone_is_found_within_a_couple_of_seconds_and_notched_out(double hz)
    {
        var finder = new SubTones(48000);
        var sound = ToneInNoise(hz, 4, toneLevel: 0.2);
        var output = sound.Select(finder.Process).ToArray();
        Assert.Single(finder.Removing);
        Assert.InRange(finder.Removing[0], hz - 1, hz + 1);
        // In the last second the tone is gone: what's left is about the noise's strength.
        double before = sound.Skip(3 * 48000).Average(v => (double)v * v);
        double after = output.Skip(3 * 48000).Average(v => v * v);
        double noise = 0.1 * 0.1;
        Assert.True(after < 1.3 * noise, $"before {before:0.0000}, after {after:0.0000}, noise {noise:0.0000}");
    }

    [Fact]
    public void With_no_tone_the_sound_is_left_exactly_as_it_was()
    {
        var finder = new SubTones(48000);
        var sound = TestAudio.Noise(48000 * 3, 0.1, seed: 4);
        var output = sound.Select(finder.Process).ToArray();
        Assert.Empty(finder.Removing);
        Assert.Equal(sound.Select(v => (double)v), output);
    }

    [Fact]
    public void A_weak_tone_is_left_alone()
    {
        // 30 dB below the noise around it: it can't hurt the picture, so there's no notch.
        var finder = new SubTones(48000);
        foreach (var v in ToneInNoise(123, 3, toneLevel: 0.1 * Math.Sqrt(2) * 0.03)) finder.Process(v);
        Assert.Empty(finder.Removing);
    }
}
