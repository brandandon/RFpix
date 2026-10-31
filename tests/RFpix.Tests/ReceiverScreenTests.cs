// ReceiverScreenTests.cs - the Receive tab reads the decoder 20 times a second while the audio
// thread is working through the sound. This plays a recording on one thread and, on another,
// reads everything the Receive tab reads, as fast as it can: nothing may go wrong on either side.
// (Code review, Oct 2026: reading the file in progress while it was being added to could fail.)

using RFpix.Audio;
using RFpix.HamDrm;
using Xunit;

namespace RFpix.Tests;

public class ReceiverScreenTests
{
    [Fact]
    public async Task The_screen_can_read_the_decoder_while_the_audio_is_worked_through()
    {
        var audio = WavFile.Read(Recordings.Path("04_modeB_2p5khz_qam16_long_high_rs0.wav")).Samples;
        var receiver = new HamDrmReceiver();
        ReceivedFile? received = null;
        receiver.FileReceived += f => received ??= f;

        // The audio thread.
        var feeding = Task.Run(() =>
        {
            for (int pos = 0; pos + 480 <= audio.Length; pos += 480)
            {
                receiver.AddSamples(audio[pos..(pos + 480)], 480, 48000);
                Thread.Yield();   // sound comes in pieces, with gaps between (much shorter here than on the air)
            }
            receiver.EndOfAudio();
        });

        // The screen: everything the Receive tab reads, over and over until the audio is done.
        int readings = 0, withProgress = 0;
        while (!feeding.IsCompleted)
        {
            _ = receiver.FileText;
            _ = receiver.Heard;
            _ = receiver.Signal;
            _ = receiver.TransmissionText;
            _ = receiver.StatusText;
            _ = receiver.Incomplete;
            if (receiver.FileProgress is { SegmentsReceived: > 0 }) withProgress++;
            readings++;
            Thread.Yield();   // much faster than the screen's 20 a second, but leaving the audio thread room
        }
        await feeding;   // anything that went wrong on the audio side shows here

        Assert.Equal("img04.jpg", received?.Name);
        Assert.True(readings > 10, $"only {readings} readings");   // (a Raspberry Pi manages fewer than a PC)
        Assert.True(withProgress > 0, "the screen saw the picture arriving");
        Assert.StartsWith("Received img04.jpg", receiver.FileText);
    }
}
