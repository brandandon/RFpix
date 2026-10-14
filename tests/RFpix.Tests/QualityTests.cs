// QualityTests.cs - checks the signal-quality figure on the status line ("quality 14 dB"),
// which shows how cleanly the picture data arrives, so radio levels can be adjusted by it.

using RFpix.Audio;
using RFpix.HamDrm;
using Xunit;
using static RFpix.Tests.TestAudio;

namespace RFpix.Tests;

public class QualityTests
{
    [Fact]
    public void A_clean_signal_reads_high_quality_with_no_warning()
    {
        var (quality, status) = Measure("01_modeA_2p5khz_qam64_long_high_rs0.wav", snrDb: null);
        Assert.InRange(quality, 25, 45);
        Assert.Matches(@"strength \d+ ?%  ·  quality [234]\d dB$", status);   // e.g. "… · strength 98% · quality 33 dB"
        Assert.DoesNotContain("needs about", status);
    }

    [Theory]
    [InlineData("06_modeB_2p5khz_qam4_long_high_rs0.wav", 9, 7)]      // reads close to the noise level added
    [InlineData("04_modeB_2p5khz_qam16_long_high_rs0.wav", 15, 13)]
    [InlineData("01_modeA_2p5khz_qam64_long_high_rs0.wav", 20, 18.5)]
    public void Quality_follows_the_noise(string file, double snrDb, double expected)
    {
        var (quality, _) = Measure(file, snrDb);
        Assert.InRange(quality, expected - 1.5, expected + 1.5);
    }

    [Fact]
    public void A_signal_too_poor_for_its_QAM_says_what_it_needs()
    {
        // 64-QAM at 16 dB of noise: pictures don't come through, and the line says why.
        var (_, status) = Measure("01_modeA_2p5khz_qam64_long_high_rs0.wav", snrDb: 16);
        Assert.Contains("(64-QAM needs about 17)", status);
    }

    [Fact]
    public void The_needed_quality_rises_with_QAM()
    {
        Assert.Equal(6, HamDrmReceiver.QualityNeeded(4, HamDrmProtection.High));
        Assert.Equal(11, HamDrmReceiver.QualityNeeded(16, HamDrmProtection.High));
        Assert.Equal(11, HamDrmReceiver.QualityNeeded(16, HamDrmProtection.Low));
        Assert.Equal(17, HamDrmReceiver.QualityNeeded(64, HamDrmProtection.High));
        Assert.Equal(18, HamDrmReceiver.QualityNeeded(64, HamDrmProtection.Low));
    }

    // Plays a recording (with noise, if given) and returns the quality averaged over the
    // second half of the transmission, and a status line from then.
    private static (double Quality, string Status) Measure(string file, double? snrDb)
    {
        var audio = WavFile.Read(Recordings.Path(file)).Samples;
        if (snrDb is double snr) audio = AddNoise(audio, snr, seed: 21);
        var receiver = new HamDrmReceiver();
        var readings = new List<double>();
        string status = "";
        receiver.Decided += _ =>
        {
            if (receiver.QualityDb is double q)
            {
                readings.Add(q);
                status = receiver.StatusText;
            }
        };
        for (int pos = 0; pos + 480 <= audio.Length; pos += 480)
            receiver.AddSamples(audio[pos..(pos + 480)], 480, 48000);
        return (readings.Skip(readings.Count / 2).Average(), status);
    }
}
