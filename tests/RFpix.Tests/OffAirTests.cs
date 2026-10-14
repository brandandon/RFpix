// OffAirTests.cs - real transmissions recorded off the air (tests/recordings/offair).
// Unlike the clean recordings, these carry everything a real radio path does to a signal.

using RFpix.Audio;
using RFpix.HamDrm;
using Xunit;

namespace RFpix.Tests;

public class OffAirTests
{
    private static string OffAir(string name) => Path.Combine(Recordings.Folder, "offair", name);

    [Fact]
    public void Offair1_decodes_to_the_same_picture_as_QSSTV()
    {
        var audio = WavFile.Read(OffAir("offair1_modeE_2p5khz_qam16_long_low_rs0_fm70cm.wav")).Samples;
        var receiver = new HamDrmReceiver();
        ReceivedFile? received = null;
        int predicted = 0;
        var quality = new List<double>();
        receiver.FileReceived += f => received ??= f;
        receiver.Decided += _ =>
        {
            if (receiver.FramePredicted) predicted++;
            if (receiver.QualityDb is double q) quality.Add(q);
        };
        for (int pos = 0; pos + 480 <= audio.Length; pos += 480)
            receiver.AddSamples(audio[pos..(pos + 480)], 480, 48000);
        receiver.EndOfAudio();

        Assert.NotNull(received);
        Assert.Contains("july4-2024 QSLcopy.jp2", received!.Name);
        Assert.Equal(File.ReadAllBytes(OffAir("offair1_qsstv_decoded.jp2")), received.Data);
        Assert.True(predicted > 0, "this recording has frame-finder lapses that prediction covers");
        Assert.InRange(quality.Skip(quality.Count / 4).Average(), 12, 17);   // a modest FM link
        Assert.InRange(receiver.PacketsBad, 0, 3);                              // of 189
    }
}
