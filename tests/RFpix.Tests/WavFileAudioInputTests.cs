// WavFileAudioInputTests.cs - checks the recording player that sits behind the audio layer.

using System.Diagnostics;
using RFpix.Audio;
using Xunit;

namespace RFpix.Tests;

public class WavFileAudioInputTests
{
    [Fact]
    public void Hands_out_the_whole_recording_in_10ms_chunks_then_says_finished()
    {
        var file = Recordings.Path("04_modeB_2p5khz_qam16_long_high_rs0.wav");
        var expected = WavFile.Read(file).Samples;

        using var player = new WavFileAudioInput { Speed = 0 };   // 0 = as fast as possible
        var received = new List<float>();
        var chunkSizes = new HashSet<int>();
        using var finished = new ManualResetEventSlim();
        player.SamplesAvailable += (_, e) =>
        {
            chunkSizes.Add(e.Count);
            Assert.Equal(48000, e.SampleRate);
            received.AddRange(e.Samples.Take(e.Count));
        };
        player.Finished += (_, _) => finished.Set();

        player.Start(file);

        Assert.True(finished.Wait(TimeSpan.FromSeconds(30)), "playback never finished");
        Assert.Equal(expected, received);
        Assert.Contains(480, chunkSizes);   // 10 ms at 48 kHz
        Assert.Equal(player.Duration, player.Position);
    }

    [Fact]
    public void Plays_at_normal_speed_by_default()
    {
        var path = WriteTempWav(seconds: 0.5);
        try
        {
            using var player = new WavFileAudioInput();
            using var finished = new ManualResetEventSlim();
            player.Finished += (_, _) => finished.Set();

            var clock = Stopwatch.StartNew();
            player.Start(path);
            Assert.True(finished.Wait(TimeSpan.FromSeconds(5)));

            // Half a second of sound should take about half a second to play.
            Assert.InRange(clock.Elapsed.TotalSeconds, 0.4, 2.0);
            Assert.False(player.IsRunning);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Stop_ends_playback_early_without_saying_finished()
    {
        using var player = new WavFileAudioInput();
        bool finished = false;
        player.Finished += (_, _) => finished = true;

        player.Start(Recordings.Path("04_modeB_2p5khz_qam16_long_high_rs0.wav"));
        Assert.True(player.IsRunning);
        Thread.Sleep(100);
        player.Stop();

        Assert.False(player.IsRunning);
        Assert.False(finished);
        Assert.InRange(player.Position.TotalSeconds, 0.01, 5);
    }

    [Fact]
    public void A_bad_file_gives_a_plain_message_and_nothing_plays()
    {
        using var player = new WavFileAudioInput();
        var ex = Assert.Throws<AudioException>(() => player.Start(Recordings.Path("no-such-file.wav")));

        Assert.Contains("no-such-file.wav", ex.Message);
        Assert.False(player.IsRunning);
    }

    private static string WriteTempWav(double seconds)
    {
        var samples = new float[(int)(48000 * seconds)];
        for (int i = 0; i < samples.Length; i++) samples[i] = 0.5f * MathF.Sin(2 * MathF.PI * 1000 * i / 48000);
        var path = Path.Combine(Path.GetTempPath(), $"rfpix-test-{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, TestWav.Build(samples, 48000));
        return path;
    }
}
