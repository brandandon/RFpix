// PictureTests.cs - checks that RFpix decodes the picture data back into the exact files sent.
//
// Limits come from measurements (Oct 11, 2026): every recording decodes to a file identical
// to the original; perfect files at 9 dB SNR for 4-QAM and 12 dB for 16-QAM; RS-protected
// files still come through at 11 dB, where pieces go missing.

using System.Numerics;
using RFpix.Audio;
using RFpix.HamDrm;
using RFpix.HamDrm.Coding;
using RFpix.Receive;
using Xunit;
using static RFpix.Tests.TestAudio;

namespace RFpix.Tests;

public class PictureTests
{
    // ---- the recordings, end to end ----

    [Theory]
    [MemberData(nameof(Recordings.All), MemberType = typeof(Recordings))]
    public void Every_recording_decodes_to_the_exact_original_file(string file)
    {
        var wav = WavFile.Read(Recordings.Path(file));
        var (received, text) = Play(wav.Samples);

        var original = Original(file);
        Assert.NotNull(received);
        Assert.Equal(Path.GetFileName(original), received!.Name);   // RS files: original name restored
        Assert.Equal(File.ReadAllBytes(original), received.Data);
        Assert.Null(received.Problem);
        Assert.StartsWith($"Received {received.Name}", text);
        if (file.Contains("_rs0")) Assert.Null(received.ReedSolomonLevel);
        else Assert.Equal(file[file.IndexOf("_rs") + 3] - '0', received.ReedSolomonLevel);
    }

    [Theory]
    [InlineData("04_modeB_2p5khz_qam16_long_high_rs0.wav", 25, 0)]
    [InlineData("07_modeE_2p5khz_qam4_long_high_rs0.wav", -150, -250)]
    [InlineData("15_modeA_2p5khz_qam64_long_high_rs0.wav", -40, 200)]
    public void Tuning_and_clock_errors_make_no_difference(string file, double offsetHz, double ppm)
    {
        var audio = ChangeClock(ShiftFrequency(WavFile.Read(Recordings.Path(file)).Samples, offsetHz, 48000), ppm);
        var (received, _) = Play(audio);

        Assert.Equal(File.ReadAllBytes(Original(file)), received?.Data);
    }

    [Theory]
    [InlineData("06_modeB_2p5khz_qam4_long_high_rs0.wav", 9)]
    [InlineData("07_modeE_2p5khz_qam4_long_high_rs0.wav", 9)]
    [InlineData("04_modeB_2p5khz_qam16_long_high_rs0.wav", 12)]
    [InlineData("01_modeA_2p5khz_qam64_long_high_rs0.wav", 20)]
    public void Decodes_perfectly_down_to_its_working_SNR(string file, double snrDb)
    {
        var audio = AddNoise(WavFile.Read(Recordings.Path(file)).Samples, snrDb, seed: 21);
        var (received, _) = Play(audio);

        Assert.Equal(File.ReadAllBytes(Original(file)), received?.Data);
    }

    [Fact]
    public void Reed_Solomon_rebuilds_pieces_lost_to_noise()
    {
        // At 11 dB SNR, 16-QAM loses about 1 frame in 7. A plain picture can't be finished...
        var plain = AddNoise(WavFile.Read(Recordings.Path("04_modeB_2p5khz_qam16_long_high_rs0.wav")).Samples, 11, seed: 21);
        Assert.Null(Play(plain).Received);

        // ...but an RS4-protected one is rebuilt.
        var file = "13_modeB_2p5khz_qam16_long_high_rs4.wav";
        var protectedAudio = AddNoise(WavFile.Read(Recordings.Path(file)).Samples, 11, seed: 21);
        var (received, text) = Play(protectedAudio);

        Assert.Equal(File.ReadAllBytes(Original(file)), received?.Data);
        Assert.True(received!.RepairedPieces > 0);
        Assert.Contains("rebuilt", text);
    }

    // ---- the Reed-Solomon pieces ----

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void Reed_Solomon_repairs_what_it_promises(int level)
    {
        int k = RsFile.DataBytes(level), parity = 255 - k;
        var rs = new ReedSolomon(k);
        var random = new Random(level);
        for (int trial = 0; trial < 10; trial++)
        {
            var block = new byte[255];
            random.NextBytes(block.AsSpan(0, k));
            rs.Encode(block);
            var original = (byte[])block.Clone();
            Assert.Equal(0, rs.Decode(block, 0, Array.Empty<int>()));

            // Half as many wrong bytes as there are repair bytes, at unknown places.
            var damaged = (byte[])original.Clone();
            foreach (int at in Enumerable.Range(0, 255).OrderBy(_ => random.Next()).Take(parity / 2))
                damaged[at] ^= (byte)random.Next(1, 256);
            Assert.Equal(parity / 2, rs.Decode(damaged, 0, Array.Empty<int>()));
            Assert.Equal(original, damaged);

            // As many missing bytes as there are repair bytes, at known places.
            var erased = (byte[])original.Clone();
            var missing = Enumerable.Range(0, 255).OrderBy(_ => random.Next()).Take(parity).ToArray();
            foreach (int at in missing) erased[at] = 0;
            Assert.True(rs.Decode(erased, 0, missing) >= 0);
            Assert.Equal(original, erased);
        }
    }

    [Theory]
    [InlineData(1, 5926)] [InlineData(4, 6057)] [InlineData(2, 1)] [InlineData(3, 160 * 3 - 7)]
    public void RS_wrapping_matches_QSSTV_and_unwraps(int level, int size)
    {
        var file = new byte[size];
        new Random(size).NextBytes(file);
        var sent = RsFile.Wrap(file, "jpg", level);

        int k = RsFile.DataBytes(level);
        Assert.Equal((size + 7 + k - 1) / k * 255, sent.Length);   // whole 255-byte blocks
        var (back, extension) = RsFile.Unwrap(sent, level)!.Value;
        Assert.Equal("jpg", extension);
        Assert.Equal(file, back);
    }

    [Fact]
    public void RS_unwraps_the_recordings_sizes_exactly_as_QSSTV_sent_them()
    {
        // Recordings 10-13 carried wrapped files of these sizes (from QSSTV's transmitter).
        foreach (var (level, image, wrappedSize) in new[] { (1, "img10.jpg", 6885), (2, "img11.jpg", 8160), (3, "img12.jpg", 9690), (4, "img13.jpg", 12240) })
            Assert.Equal(wrappedSize, RsFile.Wrap(File.ReadAllBytes(Path.Combine(Recordings.Folder, "images", image)), "jpg", level).Length);
    }

    [Fact]
    public void RS_rebuilds_missing_pieces_up_to_its_limit()
    {
        var file = new byte[6000];
        new Random(7).NextBytes(file);
        var sent = RsFile.Wrap(file, "jpg", 2);   // RS2: 63 repair bytes per 255
        int pieceSize = 116;
        int pieces = (sent.Length + pieceSize - 1) / pieceSize;

        // Lose every 6th piece (about 17%: within RS2's 25% limit).
        bool Lost(int at) => at / pieceSize % 6 == 3;
        var damaged = sent.Select((b, i) => Lost(i) ? (byte)0 : b).ToArray();
        Assert.Equal(file, RsFile.Unwrap(damaged, 2, Lost)?.File);

        // Lose every 3rd piece (33%): too much.
        bool LostMore(int at) => at / pieceSize % 3 == 1;
        var worse = sent.Select((b, i) => LostMore(i) ? (byte)0 : b).ToArray();
        Assert.Null(RsFile.Unwrap(worse, 2, LostMore));
        Assert.True(pieces > 10);
    }

    // ---- the other pieces ----

    [Fact]
    public void Data_packet_from_QSSTV_is_read_correctly()
    {
        // Frame 0 of recording 04, as QSSTV's transmitter built it: the file's header.
        var packet = Convert.FromHexString(
            "c82573008000121a34001a000192200d040185000000004600cc0a00696d6730342e6a70676245000000000000000000" +
            "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
            "00000000000000000000000000000000000000000000000000000000000000001bc5");
        Assert.Equal(130, packet.Length);
        Assert.True(MotReceiver.CheckCrc16(packet));

        var mot = new MotReceiver();
        Assert.Null(mot.AddPacket(packet));
        Assert.Equal("img04.jpg", mot.Progress!.Name);
        Assert.Equal(6434, mot.Progress.Size);

        packet[40] ^= 0x10;   // one damaged bit
        Assert.False(MotReceiver.CheckCrc16(packet));
    }

    [Fact]
    public void Deinterleaver_carries_on_past_a_missing_frame()
    {
        var d = new TimeDeinterleaver(100, HamDrmInterleave.Long);
        var cells = Enumerable.Range(0, 100).Select(i => new Complex(i, 0)).ToArray();
        var weights = Enumerable.Repeat(1.0, 100).ToArray();
        var outFrames = new List<(long Frame, Complex[] Cells, double[] Weights)>();
        foreach (long f in new long[] { 0, 1, 2, 3, 4, 5, 7, 8, 9 })   // frame 6 never arrives
            outFrames.AddRange(d.Add(f, cells, weights));

        Assert.Equal(new long[] { 0, 1, 2, 3, 4, 5 }, outFrames.Select(o => o.Frame));
        // Original frame 2 needs sent frames 2-6: a fifth of its cells came in frame 6, now missing.
        Assert.Equal(20, outFrames[2].Weights.Count(w => w == 0));
        Assert.All(outFrames[1].Weights, w => Assert.Equal(1.0, w));
    }

    [Theory]
    [InlineData("img04.jpg", "img04.jpg")]
    [InlineData("..\\..\\Windows\\evil.exe", "evil.exe")]
    [InlineData("../../.bashrc", "bashrc")]
    [InlineData("a:b?c*.jpg", "a_b_c_.jpg")]
    [InlineData("", "received")]
    public void Names_from_the_air_are_made_safe(string name, string expected)
    {
        Assert.Equal(expected, ReceivedFileSaver.Clean(name));
    }

    [Fact]
    public void Saved_files_never_overwrite_each_other()
    {
        string folder = Path.Combine(Path.GetTempPath(), "rfpix-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var file = new ReceivedFile("img04.jpg", new byte[] { 1, 2, 3 }, 1);
            var when = new DateTime(2026, 10, 11, 14, 32, 0);
            string first = ReceivedFileSaver.Save(file, folder, "KD9DD", when);
            string second = ReceivedFileSaver.Save(file, folder, "KD9DD", when);

            Assert.Equal("2026-10-11 1432 KD9DD img04.jpg", Path.GetFileName(first));
            Assert.Equal("2026-10-11 1432 KD9DD img04 (2).jpg", Path.GetFileName(second));
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(second));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    // ---- helpers ----

    /// <summary>The original picture a recording carries, e.g. images/img04.jpg.</summary>
    private static string Original(string recording) =>
        Directory.GetFiles(Path.Combine(Recordings.Folder, "images"), $"img{recording[..2]}.*").Single();

    /// <summary>Plays audio through a receiver; returns the first file received and the last
    /// file line shown while the signal was there.</summary>
    private static (ReceivedFile? Received, string FileText) Play(float[] audio)
    {
        var receiver = new HamDrmReceiver();
        ReceivedFile? received = null;
        string text = "";
        receiver.FileReceived += f => received ??= f;
        receiver.Decided += _ => { if (receiver.FileText is { Length: > 0 } t) text = t; };
        for (int pos = 0; pos + 480 <= audio.Length; pos += 480)
            receiver.AddSamples(audio[pos..(pos + 480)], 480, 48000);
        return (received, text);
    }
}
