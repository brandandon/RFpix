// PreEmphasisTests.cs - checks FM's treble boost that RFpix adds to its HamDRM sound on FM (PreEmphasis):
// the treble boosted 6 dB more for each doubling of frequency, the average strength unchanged, and a
// picture sent with it still decoding.

using RFpix.Dsp;
using RFpix.HamDrm;
using Xunit;

namespace RFpix.Tests;

public class PreEmphasisTests
{
    private static float[] Tone(double hz) =>
        Enumerable.Range(0, 48000).Select(i => (float)(0.3 * Math.Sin(2 * Math.PI * hz * i / 48000))).ToArray();

    private static double Rms(float[] x, int skip = 4800) => Math.Sqrt(x.Skip(skip).Average(v => (double)v * v));

    [Fact]
    public void The_treble_is_boosted_6_dB_for_each_doubling_and_the_level_kept()
    {
        // A tone alone keeps its level (the boost is scaled to the same average strength).
        Assert.InRange(Rms(PreEmphasis.Apply(Tone(1000), 48000)) / Rms(Tone(1000)), 0.98, 1.02);

        // Two tones together: 2800 Hz comes out about 20*log10(2800/1400) = 6 dB stronger, relative to
        // 1400 Hz, than it went in (a little less, as the boost starts at 212 Hz).
        var low = Tone(1400);
        var high = Tone(2800);
        var both = low.Zip(high, (a, b) => a + b).ToArray();
        var boosted = PreEmphasis.Apply(both, 48000);
        double Strength(float[] x, double hz)
        {
            double s = 0, c = 0;
            for (int i = 4800; i < x.Length; i++) { s += x[i] * Math.Sin(2 * Math.PI * hz * i / 48000); c += x[i] * Math.Cos(2 * Math.PI * hz * i / 48000); }
            return Math.Sqrt(s * s + c * c);
        }
        double gainDb = 20 * Math.Log10(Strength(boosted, 2800) / Strength(boosted, 1400));
        Assert.InRange(gainDb, 5.0, 6.5);
    }

    [Fact]
    public void A_picture_sent_with_the_boost_still_decodes()
    {
        // (Played straight in, without the receiving radio's matching treble cut: the receiver copes.)
        var picture = File.ReadAllBytes(Path.Combine(Recordings.Folder, "images", "img04.jpg"));
        var settings = new TransmitSettings(HamDrmMode.B, HamDrmBandwidth.Wide, 16, HamDrmInterleave.Long, HamDrmProtection.High);
        var sound = PreEmphasis.Apply(new HamDrmTransmitter("boost.jpg", picture, settings, "N0CALL").Audio(), 48000);
        var (received, _, _) = TransmitterTests.ReceiveSound(sound);
        Assert.Equal(picture, received?.Data);
    }
}
