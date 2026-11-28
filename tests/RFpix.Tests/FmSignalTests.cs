// FmSignalTests.cs - checks RFpix's new FM signal (FmSignal, a prototype on the bench so far): its error
// correction, its QAM points, and whole files sent through the FM model, with the sound cards' clocks
// apart, a repeater's tone in the sound, and a station tuning in late.

using RFpix.FmSignal;
using RFpix.Tests.Bench;
using Xunit;

namespace RFpix.Tests;

public class FmSignalTests
{
    private static byte[] File(int bytes, int seed = 1)
    {
        var data = new byte[bytes];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static double Gaussian(Random random) =>
        Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());

    [Fact]
    public void The_error_correction_puts_right_a_noisy_block()
    {
        var random = new Random(3);
        for (int rate = 0; rate < Ldpc.Rates.Length; rate++)
        {
            var code = Ldpc.ForRate(rate);
            var data = Enumerable.Range(0, code.DataBits).Select(_ => (byte)random.Next(2)).ToArray();
            var block = code.Encode(data);
            Assert.True(code.Passes(block));
            Assert.Equal(data, block[..code.DataBits]);   // the data goes as it is, parity after

            // Noise at 4 dB Eb/N0 (each bit sent as +1 or -1): well inside what each code handles.
            double r = (double)code.DataBits / code.Length;
            double sigma = Math.Sqrt(1 / (2 * r * Math.Pow(10, 0.4)));
            var soft = block.Select(b => { double x = (b == 0 ? 1 : -1) + sigma * Gaussian(random); return 2 * x / (sigma * sigma); }).ToArray();
            int wrong = soft.Select((v, i) => (v < 0 ? 1 : 0) != block[i] ? 1 : 0).Sum();
            Assert.True(wrong > 20, $"rate {rate}: the noise should make some bits wrong ({wrong})");
            Assert.Equal(data, code.Decode(soft));
        }
    }

    [Fact]
    public void Too_much_noise_is_reported_not_guessed()
    {
        var random = new Random(4);
        var code = Ldpc.ForRate(2);
        var block = code.Encode(new byte[code.DataBits]);
        var soft = block.Select(_ => 2 * Gaussian(random)).ToArray();   // pure noise
        Assert.Null(code.Decode(soft, maxRounds: 20));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(8)]
    public void Every_QAM_point_reads_back_as_its_own_bits(int bits)
    {
        var soft = new double[bits];
        double power = 0;
        for (int value = 0; value < 1 << bits; value++)
        {
            var b = Enumerable.Range(0, bits).Select(i => (byte)((value >> (bits - 1 - i)) & 1)).ToArray();
            var point = Qam.Map(b);
            power += point.Magnitude * point.Magnitude;
            Assert.Equal(point, Qam.Nearest(point * 1.0001, bits));
            Qam.Soft(point, bits, 0.01, soft);
            for (int i = 0; i < bits; i++) Assert.True(b[i] == 0 ? soft[i] > 0 : soft[i] < 0, $"value {value}, bit {i}");
        }
        Assert.InRange(power / (1 << bits), 0.99, 1.01);   // average power 1
    }

    private static (FmReception Reception, byte[] File) Send(FmSignalMode mode, int bytes, int percent, Func<float[], float[]> path)
    {
        var file = File(bytes);
        var (data, k) = FmSignalTransmitter.Wrap(file, "jpg", mode, percent);
        var signal = FmSignalTransmitter.Audio(data, k, mode);
        var audio = new float[signal.Length + 96000];
        Array.Copy(signal, 0, audio, 48000, signal.Length);
        return (FmSignalReceiver.Decode(path(audio)), file);
    }

    [Fact]
    public void A_file_comes_through_a_clean_line_at_every_speed()
    {
        foreach (var mode in FmSignalMode.All)
        {
            var (reception, file) = Send(mode, 3000, 20, audio => audio);
            Assert.Equal(file, reception.File);
            Assert.Equal("jpg", reception.Extension);
            Assert.All(reception.Frames, f => Assert.Equal(mode, f.Mode));
        }
    }

    [Fact]
    public void A_file_comes_through_a_repeater_with_the_clocks_apart()
    {
        // The receiving sound card runs 100 parts per million fast: the frame drifts by 14 samples.
        var mode = FmSignalMode.All[2];
        var (reception, file) = Send(mode, 3000, 20,
            audio => FmChannel.Apply(Resample(audio, 1 + 100e-6), FmCondition.RepeaterRight, cnrDb: 20, seed: 5));
        Assert.Equal(file, reception.File);
    }

    [Fact]
    public void A_repeaters_tone_in_the_sound_doesnt_stop_it()
    {
        var mode = FmSignalMode.All[2];
        var (reception, file) = Send(mode, 3000, 20, audio => FmChannel.Apply(audio, FmCondition.RepeaterTone, cnrDb: 20, seed: 6));
        Assert.Equal(file, reception.File);
        Assert.All(reception.Frames, f => Assert.Equal(f.Blocks, f.Good));
    }

    [Theory]
    [InlineData(3)]   // FM 4
    [InlineData(4)]   // FM 5
    public void The_fast_speeds_come_through_a_repeater_that_cuts_the_bass(int speed)
    {
        // The net's repeater (Nov 20, 2026) left the lowest tones 15-25 dB down. The receiver let those weak
        // pilots throw whole frames out of line, and FM 4 and FM 5 failed at a good signal; not now.
        var mode = FmSignalMode.All[speed];
        var (reception, file) = Send(mode, 6000, 20, audio => FmChannel.Apply(audio, FmCondition.RepeaterNet with { DriveDb = -9 }, cnrDb: 30, seed: 11));
        Assert.Equal(file, reception.File);
        Assert.All(reception.Frames, f => Assert.Equal(f.Blocks, f.Good));
    }

    [Fact]
    public void A_station_that_tunes_in_late_still_gets_the_file()
    {
        // With fountain 50%, missing the first 2 seconds (the whole first frame, as it can't be found
        // without its start) still leaves enough: 4 frames of 7 pieces were sent, 18 are needed.
        var mode = FmSignalMode.All[1];
        var (reception, file) = Send(mode, 3000, 50, audio => audio[(48000 * 3)..]);
        Assert.Equal(file, reception.File);
        Assert.Equal(3, reception.Frames.Count);
    }

    /// <summary>Feeds sound to the live receiver 10 ms at a time, as the radio does, then 7 seconds of quiet.</summary>
    private static List<FmReceivedFile> Listen(float[] sound, FmSignalLiveReceiver? receiver = null)
    {
        receiver ??= new FmSignalLiveReceiver();
        var files = new List<FmReceivedFile>();
        receiver.FileReceived += (file, _) => files.Add(file);
        var all = sound.Concat(new float[48000 * 7]).ToArray();
        for (int pos = 0; pos + 480 <= all.Length; pos += 480) receiver.AddSamples(all[pos..(pos + 480)], 480, 48000);
        return files;
    }

    [Fact]
    public void The_live_receiver_gets_the_file_with_its_name_and_callsign()
    {
        var mode = FmSignalMode.All[2];
        var file = File(6000);
        var (data, k) = FmSignalTransmitter.Wrap(file, "jpg", mode, 20);
        var signal = FmSignalTransmitter.Audio(data, k, mode, fileNumber: 77, new FmFileInfo("N0CALL", "tower.jpg"));
        var audio = new float[48000].Concat(signal).ToArray();
        var files = Listen(FmChannel.Apply(audio, FmCondition.RepeaterRight, cnrDb: 20, seed: 9));
        var received = Assert.Single(files);
        Assert.Equal(file, received.Data);
        Assert.Equal("tower.jpg", received.Name);
        Assert.Equal("N0CALL", received.Callsign);
        Assert.Equal(mode, received.Mode);
    }

    [Fact]
    public void The_same_file_from_two_stations_is_complete_both_times_each_from_its_own_sender()
    {
        // Nov 27, 2026: every station's test 17 is the same file; once one had come through, the next station's
        // was logged "not complete" (and the test result too), though it had arrived perfectly.
        var mode = FmSignalMode.All[3];
        var file = File(3000);
        var (data, k) = FmSignalTransmitter.Wrap(file, "jpg", mode, 20);
        var one = FmSignalTransmitter.Audio(data, k, mode, fileNumber: 21, new FmFileInfo("N0CALL", "rfpix-test-21.jpg"));
        var two = FmSignalTransmitter.Audio(data, k, mode, fileNumber: 21, new FmFileInfo("N1CALL", "rfpix-test-21.jpg"));
        var gap = new float[48000 * 8];
        var audio = new float[48000].Concat(one).Concat(gap).Concat(two).Concat(gap).ToArray();
        var rx = new FmSignalLiveReceiver();
        var reports = new List<FmTransmissionReport>();
        int files = 0;
        rx.TransmissionEnded += r => reports.AddRange(r);
        rx.FileReceived += (_, _) => files++;
        for (int i = 0; i < audio.Length; i += 4800) rx.AddSamples(audio[i..Math.Min(audio.Length, i + 4800)], Math.Min(4800, audio.Length - i), 48000);
        rx.EndOfAudio();
        Assert.Equal(1, files);   // saved once
        Assert.Equal(2, reports.Count);
        Assert.All(reports, r => Assert.NotNull(r.File));
        Assert.Equal(new[] { "N0CALL", "N1CALL" }, reports.Select(r => r.Callsign));
        Assert.Equal(new[] { false, true }, reports.Select(r => r.ReceivedEarlier));
        Assert.Contains("frames heard", reports[0].Details);
    }

    [Fact]
    public void A_late_station_gets_the_details_from_a_later_frame()
    {
        // The details go in the first frame and the last: missing the first, they come in the last.
        var mode = FmSignalMode.All[1];
        var file = File(3000);
        var (data, k) = FmSignalTransmitter.Wrap(file, "jpg", mode, 50);
        var signal = FmSignalTransmitter.Audio(data, k, mode, fileNumber: 5, new FmFileInfo("N0CALL", "late.jpg"));
        var files = Listen(signal[(48000 * 2)..]);
        var received = Assert.Single(files);
        Assert.Equal(file, received.Data);
        Assert.Equal("late.jpg", received.Name);
    }

    [Fact]
    public void The_live_receiver_keeps_two_pictures_apart()
    {
        var mode = FmSignalMode.All[3];
        var one = File(4000, seed: 1);
        var two = File(5000, seed: 2);
        float[] Sound(byte[] f, ushort number, string name)
        {
            var (data, k) = FmSignalTransmitter.Wrap(f, "jpg", mode, 20);
            return FmSignalTransmitter.Audio(data, k, mode, number, new FmFileInfo("N0CALL", name));
        }
        var audio = new float[24000].Concat(Sound(one, 1, "one.jpg")).Concat(new float[48000]).Concat(Sound(two, 2, "two.jpg")).ToArray();
        var files = Listen(audio);
        Assert.Equal(new[] { "one.jpg", "two.jpg" }, files.Select(f => f.Name));
        Assert.Equal(one, files[0].Data);
        Assert.Equal(two, files[1].Data);
    }

    [Fact]
    public void Band_noise_finds_no_frames()
    {
        var random = new Random(8);
        var noise = Enumerable.Range(0, 48000 * 10).Select(_ => (float)(0.1 * Gaussian(random))).ToArray();
        var reception = FmSignalReceiver.Decode(FmChannel.Apply(noise, FmCondition.RepeaterTone, cnrDb: 20, seed: 8));
        Assert.Empty(reception.Frames);
        Assert.Null(reception.File);
    }

    /// <summary>The sound as heard by a sound card running <paramref name="ratio"/> times fast (windowed-sinc interpolation).</summary>
    private static float[] Resample(float[] x, double ratio)
    {
        int n = (int)(x.Length / ratio) - 20;
        var y = new float[n];
        for (int i = 0; i < n; i++)
        {
            double t = i * ratio, sum = 0;
            int c = (int)t;
            for (int j = c - 15; j <= c + 16; j++)
            {
                if (j < 0 || j >= x.Length) continue;
                double d = t - j;
                double sinc = d == 0 ? 1 : Math.Sin(Math.PI * d) / (Math.PI * d);
                sum += x[j] * sinc * (0.5 + 0.5 * Math.Cos(Math.PI * d / 16));
            }
            y[i] = (float)sum;
        }
        return y;
    }
}
