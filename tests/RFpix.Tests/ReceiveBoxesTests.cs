// ReceiveBoxesTests.cs - checks that the Receive tab's boxes keep the last picture's details until the
// next picture really starts: noise between pictures (which can briefly look like a HamDRM signal) mustn't
// replace an analog picture's boxes or wipe a HamDRM picture's settings (Dan, 20 m, Nov 15, 2026).

using RFpix.HamDrm;
using RFpix.Receive;
using Xunit;

namespace RFpix.Tests;

public class ReceiveBoxesTests
{
    private static readonly FacInfo Settings =
        new(0, HamDrmBandwidth.Wide, HamDrmInterleave.Long, 16, HamDrmProtection.High, IsData: true, PacketId: 0, LabelPart: "N0C");

    [Fact]
    public void A_HamDRM_signal_takes_the_boxes_only_once_its_settings_are_read()
    {
        var guess = new HeardSettings(HamDrmMode.E, Fac: null, ReedSolomon: null, Callsign: null);   // noise, say
        var read = guess with { Mode = HamDrmMode.B, Fac = Settings };

        Assert.Equal(BoxesShow.Analog, ReceiveBoxes.Next(BoxesShow.Analog, analogArriving: false, guess));
        Assert.False(ReceiveBoxes.ShowsNewSettings(BoxesShow.Digital, guess));   // the last settings stay
        Assert.Equal(BoxesShow.Digital, ReceiveBoxes.Next(BoxesShow.Analog, analogArriving: false, read));
        Assert.True(ReceiveBoxes.ShowsNewSettings(BoxesShow.Digital, read));
        Assert.Equal(BoxesShow.Analog, ReceiveBoxes.Next(BoxesShow.Digital, analogArriving: true, read));
        Assert.Equal(BoxesShow.Digital, ReceiveBoxes.Next(BoxesShow.Digital, analogArriving: false, heard: null));
        Assert.Equal(BoxesShow.Nothing, ReceiveBoxes.Next(BoxesShow.Nothing, analogArriving: false, heard: null));
    }

    [Fact]
    public void Band_noise_never_takes_the_boxes()
    {
        // 20 seconds of noise through the HamDRM receiver: whatever it briefly guesses, the boxes
        // (after an analog picture) stay on the analog picture.
        var receiver = new HamDrmReceiver();
        var boxes = BoxesShow.Analog;
        receiver.Decided += _ => boxes = ReceiveBoxes.Next(boxes, analogArriving: false, receiver.Heard);
        var noise = TestAudio.Noise(48000 * 20, 0.1, seed: 15);
        for (int pos = 0; pos + 480 <= noise.Length; pos += 480) receiver.AddSamples(noise[pos..(pos + 480)], 480, 48000);
        Assert.Equal(BoxesShow.Analog, boxes);
    }
}
