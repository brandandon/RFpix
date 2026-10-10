// WavFileTests.cs - checks that RFpix reads WAV files correctly.
//
// Two kinds of test:
//   - the real HamDRM recordings in tests/recordings, and
//   - tiny WAV files built right here in memory, one for each storage format
//     other programs might use (8-, 16-, 24-, 32-bit, decimal, stereo...).

using RFpix.Audio;
using RFpix.Dsp;
using Xunit;

namespace RFpix.Tests;

public class WavFileTests
{
    // ---- the HamDRM recordings ----

    [Theory]
    [MemberData(nameof(Recordings.All), MemberType = typeof(Recordings))]
    public void Recording_reads_as_48kHz_mono_with_silence_either_side(string file)
    {
        var wav = WavFile.Read(Recordings.Path(file));

        Assert.Equal(48000, wav.SampleRate);
        Assert.Equal(1, wav.Channels);
        Assert.Equal(16, wav.BitsPerSample);
        Assert.InRange(wav.Duration.TotalSeconds, 20, 90);

        // Each recording starts and ends with 1 second of silence...
        Assert.Equal(0f, Peak(wav.Samples, 0, 47000));
        Assert.Equal(0f, Peak(wav.Samples, wav.Samples.Length - 47000, 47000));
        // ...and the signal in between is loud but not clipped.
        Assert.InRange(Peak(wav.Samples, 48000, wav.Samples.Length - 96000), 0.5f, 0.99f);
    }

    [Fact]
    public void Recording_04_signal_sits_between_350_and_2800_Hz()
    {
        var wav = WavFile.Read(Recordings.Path("04_modeB_2p5khz_qam16_long_high_rs0.wav"));

        // Run it through the same analyzer the waterfall uses and add up the power
        // in each frequency step, over the whole recording.
        var analyzer = new SpectrumAnalyzer(maxFrequency: 3000);
        double[]? power = null;
        double hzPerStep = 0;
        analyzer.LineReady += (line, hz) =>
        {
            power ??= new double[line.Length];
            hzPerStep = hz;
            for (int k = 0; k < line.Length; k++) power[k] += Math.Pow(10, line[k] / 10);
        };
        analyzer.AddSamples(wav.Samples, wav.Samples.Length, wav.SampleRate);

        Assert.NotNull(power);
        double Band(double from, double to) =>
            power!.Where((_, k) => k * hzPerStep >= from && k * hzPerStep < to).Average();

        double inside = Band(500, 2600);
        // Below and above the signal should be at least 15 dB (about 30 times) quieter.
        // (QSSTV's signal measures about 20 dB: HamDRM's edges fade out gradually.)
        Assert.True(inside > 30 * Band(50, 250), "too much energy below the signal");
        Assert.True(inside > 30 * Band(2950, 3000), "too much energy above the signal");
    }

    // ---- storage formats ----

    [Theory]
    [InlineData(1, 8)]
    [InlineData(1, 16)]
    [InlineData(1, 24)]
    [InlineData(1, 32)]
    [InlineData(3, 32)]   // 3 = decimal ("float") samples
    public void Reads_each_sample_format(int format, int bits)
    {
        float[] expected = { 0f, 0.5f, -0.5f, 0.25f, -1f };
        var wav = WavFile.Read(TestWav.Build(expected, 12000, channels: 1, format, bits));

        Assert.Equal(12000, wav.SampleRate);
        Assert.Equal(expected.Length, wav.Samples.Length);
        float tolerance = bits == 8 ? 0.01f : 0.0001f;
        for (int i = 0; i < expected.Length; i++)
            Assert.InRange(wav.Samples[i], expected[i] - tolerance, expected[i] + tolerance);
    }

    [Fact]
    public void Keeps_only_the_left_channel_of_a_stereo_file()
    {
        // Left gets 0.5, right gets -0.5.
        var wav = WavFile.Read(TestWav.Build(new[] { 0.5f, -0.5f, 0.5f, -0.5f }, 48000, channels: 2));

        Assert.Equal(2, wav.Channels);
        Assert.Equal(new[] { 0.5f, 0.5f }, wav.Samples);
    }

    [Fact]
    public void Skips_extra_chunks_and_reads_the_newer_extensible_header()
    {
        var bytes = TestWav.Build(new[] { 0.25f, -0.25f }, 48000, extensible: true, extraChunk: true);
        var wav = WavFile.Read(bytes);

        Assert.Equal(new[] { 0.25f, -0.25f }, wav.Samples);
    }

    [Fact]
    public void Reads_a_recording_that_was_cut_short()
    {
        // The header promises 4 samples but only 3 are there, as when a recording program crashes.
        var bytes = TestWav.Build(new[] { 0.1f, 0.2f, 0.3f, 0.4f }, 48000);
        Array.Resize(ref bytes, bytes.Length - 2);

        Assert.Equal(3, WavFile.Read(bytes).Samples.Length);
    }

    [Fact]
    public void Explains_files_it_cannot_play()
    {
        var notWav = System.Text.Encoding.ASCII.GetBytes("This is a text file, not sound.");
        var ex = Assert.Throws<AudioException>(() => WavFile.Read(notWav, "notes.txt"));
        Assert.Contains("isn't a WAV file", ex.Message);

        var compressed = TestWav.Build(new[] { 0f }, 8000, format: 2, bits: 4);   // ADPCM
        ex = Assert.Throws<AudioException>(() => WavFile.Read(compressed, "voice.wav"));
        Assert.Contains("16-bit WAV", ex.Message);

        ex = Assert.Throws<AudioException>(() => WavFile.Read(Recordings.Path("no-such-file.wav")));
        Assert.Contains("no-such-file.wav", ex.Message);
    }

    private static float Peak(float[] s, int start, int count) =>
        s.Skip(start).Take(count).Max(Math.Abs);
}
