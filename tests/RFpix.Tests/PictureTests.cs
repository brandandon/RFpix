// PictureTests.cs - checks that RFpix decodes the picture data back into the exact files sent.
//
// Limits come from measurements (Oct 13, 2026, with multi-stage decoding): every recording
// decodes to a file identical to the original; perfect files at 7.5-8 dB SNR for 4-QAM,
// 11-11.5 dB for 16-QAM and 15-16.5 dB for 64-QAM; an RS4-protected file still comes
// through at 9.5 dB, where pieces go missing. Pictures appear 0.8 s after the
// signal stops, never before.

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
        var (received, text, _) = Play(wav.Samples);

        var original = Original(file);
        Assert.NotNull(received);
        Assert.Equal(Path.GetFileName(original), received!.Name);   // RS files: original name restored
        Assert.Equal(File.ReadAllBytes(original), received.Data);
        Assert.Null(received.Problem);
        Assert.StartsWith($"Received {received.Name}", text);
        Assert.Equal(0, received.RepairedPieces);   // a clean signal: nothing to rebuild
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
        var (received, _, _) = Play(audio);

        Assert.Equal(File.ReadAllBytes(Original(file)), received?.Data);
    }

    [Theory]
    [InlineData("06_modeB_2p5khz_qam4_long_high_rs0.wav", 8)]
    [InlineData("07_modeE_2p5khz_qam4_long_high_rs0.wav", 7.5)]
    [InlineData("04_modeB_2p5khz_qam16_long_high_rs0.wav", 11)]
    [InlineData("02_modeA_2p5khz_qam16_short_low_rs0.wav", 11.5)]
    [InlineData("01_modeA_2p5khz_qam64_long_high_rs0.wav", 15)]
    [InlineData("05_modeB_2p2khz_qam64_short_low_rs0.wav", 16.5)]
    public void Decodes_perfectly_down_to_its_working_SNR(string file, double snrDb)
    {
        var audio = AddNoise(WavFile.Read(Recordings.Path(file)).Samples, snrDb, seed: 21);
        var (received, _, _) = Play(audio);

        Assert.Equal(File.ReadAllBytes(Original(file)), received?.Data);
    }

    [Fact]
    public void Reed_Solomon_rebuilds_pieces_lost_to_noise()
    {
        // At 9.5 dB SNR, 16-QAM loses about 1 frame in 10. A plain picture can't be finished...
        var plain = AddNoise(WavFile.Read(Recordings.Path("04_modeB_2p5khz_qam16_long_high_rs0.wav")).Samples, 9.5, seed: 21);
        Assert.Null(Play(plain).Received);

        // ...but an RS4-protected one is rebuilt.
        var file = "13_modeB_2p5khz_qam16_long_high_rs4.wav";
        var protectedAudio = AddNoise(WavFile.Read(Recordings.Path(file)).Samples, 9.5, seed: 21);
        var (received, text, _) = Play(protectedAudio);

        Assert.Equal(File.ReadAllBytes(Original(file)), received?.Data);
        Assert.True(received!.RepairedPieces > 0);
        Assert.Contains("rebuilt", text);
    }

    [Theory]
    [InlineData("04_modeB_2p5khz_qam16_long_high_rs0.wav")]
    [InlineData("13_modeB_2p5khz_qam16_long_high_rs4.wav")]
    public void The_picture_appears_when_the_transmission_ends(string file)
    {
        // As off the air: the signal, then band noise (no "end of audio" from a player).
        var wav = WavFile.Read(Recordings.Path(file));
        double level = Math.Sqrt(wav.Samples.Skip(48000).Take(48000 * 10).Average(s => (double)s * s));
        var audio = wav.Samples.Concat(Noise(48000 * 6, level, seed: 3)).ToArray();
        double signalEnd = wav.Duration.TotalSeconds - 1.0;

        var (received, text, at) = Play(audio, endOfAudio: false);

        Assert.Equal(File.ReadAllBytes(Original(file)), received?.Data);
        Assert.InRange(at, signalEnd, signalEnd + 1.5);   // after the signal stops (measured: 0.8 s), not before
        Assert.StartsWith("Received", text);

        // While the sender is still going, the picture waits and the third line shows the progress.
        var (early, earlyText, _) = Play(audio[..((int)((signalEnd - 1) * 48000))], endOfAudio: false);
        Assert.Null(early);
        Assert.StartsWith("Receiving", earlyText);
        Assert.Contains("pieces (100", earlyText);   // all pieces in ("100%" or "100 %", by language setting)
    }

    [Fact]
    public void An_unfinished_picture_is_reported_and_doesnt_hang_over_the_next_transmission()
    {
        // As on the air: a picture cut short (recording 04 stops 12 s early), then a
        // transmission whose data can't be read (64-QAM buried in noise), then a good one.
        var cut = WavFile.Read(Recordings.Path("04_modeB_2p5khz_qam16_long_high_rs0.wav")).Samples;
        cut = cut[..(cut.Length - 13 * 48000)];
        var unreadable = AddNoise(WavFile.Read(Recordings.Path("01_modeA_2p5khz_qam64_long_high_rs0.wav")).Samples, 8, seed: 21);
        var good = WavFile.Read(Recordings.Path("06_modeB_2p5khz_qam4_long_high_rs0.wav")).Samples;
        var gap = new float[3 * 48000];

        var receiver = new HamDrmReceiver();
        var received = new List<ReceivedFile>();
        receiver.FileReceived += received.Add;
        var lines = new List<string>();
        void Feed(float[] audio)
        {
            for (int pos = 0; pos + 480 <= audio.Length; pos += 480)
            {
                receiver.AddSamples(audio[pos..(pos + 480)], 480, 48000);
                if (pos % 48000 == 0) lines.Add(receiver.FileText);
            }
        }

        Feed(cut); Feed(gap);
        Assert.Empty(received);
        Assert.StartsWith("Couldn't finish img04.jpg:", receiver.FileText);
        Assert.Matches(@"\d+ of \d+ pieces arrived\.$", receiver.FileText);

        lines.Clear();
        Feed(unreadable[48000..]); 
        Assert.DoesNotContain(lines, l => l.StartsWith("Receiving img04"));        // the old picture's progress is gone
        Assert.Contains(lines, l => l.StartsWith("The picture data can't be read: 0 of"));
        Feed(gap);
        Assert.StartsWith("No file came through: none of the", receiver.FileText);

        Feed(good); Feed(gap);
        Assert.Single(received);
        Assert.Equal("img06.jpg", received[0].Name);                              // and the next picture comes through
        Assert.StartsWith("Received img06.jpg", receiver.FileText);
    }

    [Fact]
    public void The_same_picture_sent_twice_is_shown_twice()
    {
        var audio = WavFile.Read(Recordings.Path("04_modeB_2p5khz_qam16_long_high_rs0.wav")).Samples;
        var receiver = new HamDrmReceiver();
        int count = 0;
        receiver.FileReceived += _ => count++;
        foreach (var copy in new[] { audio, new float[3 * 48000], audio, new float[3 * 48000] })
            for (int pos = 0; pos + 480 <= copy.Length; pos += 480)
                receiver.AddSamples(copy[pos..(pos + 480)], 480, 48000);
        Assert.Equal(2, count);
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

    // Code review, Oct 2026: also at the very edge, and just past it.
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void Reed_Solomon_at_its_limits_repairs_or_says_it_cant_and_changes_nothing(int level)
    {
        int k = RsFile.DataBytes(level), parity = 255 - k;
        var rs = new ReedSolomon(k);
        var random = new Random(100 + level);
        for (int trial = 0; trial < 6; trial++)
        {
            var original = new byte[255];
            random.NextBytes(original.AsSpan(0, k));
            rs.Encode(original);
            var places = Enumerable.Range(0, 255).OrderBy(_ => random.Next()).ToArray();

            // Missing and wrong bytes together, exactly at the limit (a wrong byte costs two repair bytes).
            int wrong = parity / 4, gone = parity - 2 * wrong;
            var mixed = (byte[])original.Clone();
            foreach (int at in places.Take(gone)) mixed[at] = 0;
            foreach (int at in places.Skip(gone).Take(wrong)) mixed[at] ^= (byte)random.Next(1, 256);
            Assert.True(rs.Decode(mixed, 0, places.Take(gone).ToArray()) >= 0);
            Assert.Equal(original, mixed);

            // One missing byte more than there are repair bytes: past repair, and left as it was.
            var erased = (byte[])original.Clone();
            foreach (int at in places.Take(parity + 1)) erased[at] = 0;
            var before = (byte[])erased.Clone();
            Assert.Equal(-1, rs.Decode(erased, 0, places.Take(parity + 1).ToArray()));
            Assert.Equal(before, erased);

            // One wrong byte more than half the repair bytes, at unknown places: the same.
            var damaged = (byte[])original.Clone();
            foreach (int at in places.Take(parity / 2 + 1)) damaged[at] ^= (byte)random.Next(1, 256);
            before = (byte[])damaged.Clone();
            Assert.Equal(-1, rs.Decode(damaged, 0, Array.Empty<int>()));
            Assert.Equal(before, damaged);
        }
    }

    // An AVIF that came Reed-Solomon wrapped loses the padding zeros after its last "box" (each box
    // starts with its length). Code review, Oct 2026: with damaged lengths too.
    [Fact]
    public void AVIF_padding_is_trimmed_and_damaged_lengths_leave_the_file_as_it_came()
    {
        static byte[] Box(uint length, byte fill)
        {
            var box = new byte[length];
            box[0] = (byte)(length >> 24); box[1] = (byte)(length >> 16); box[2] = (byte)(length >> 8); box[3] = (byte)length;
            box[4] = (byte)'t'; box[5] = (byte)'e'; box[6] = (byte)'s'; box[7] = (byte)'t';
            for (int i = 8; i < box.Length; i++) box[i] = fill;
            return box;
        }
        var boxes = Box(24, 1).Concat(Box(40, 2)).ToArray();
        var padded = boxes.Concat(new byte[100]).ToArray();
        Assert.Equal(boxes, RsFile.WithoutPadding(padded));      // trimmed to the last box's end
        Assert.Equal(boxes, RsFile.WithoutPadding(boxes));       // nothing to trim

        foreach (uint damaged in new uint[] { 0, 3, 7, 10_000, uint.MaxValue })
        {
            var file = (byte[])padded.Clone();
            file[24] = (byte)(damaged >> 24); file[25] = (byte)(damaged >> 16); file[26] = (byte)(damaged >> 8); file[27] = (byte)damaged;
            var result = RsFile.WithoutPadding(file);
            // Never past the file, never into a box: either untouched or cut at the first box's end.
            Assert.True(result.Length == file.Length || result.Length == 24, $"length {damaged} gave {result.Length} bytes");
            Assert.Equal(file[..result.Length], result);
        }

        var notPadding = boxes.Concat(new byte[] { 0, 0, 9, 0 }).ToArray();
        Assert.Equal(notPadding, RsFile.WithoutPadding(notPadding));   // something after the end that isn't zeros: kept
        Assert.Equal(new byte[] { 1, 2, 3 }, RsFile.WithoutPadding(new byte[] { 1, 2, 3 }));   // too short to be boxes
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

    // Code review, Oct 2026: a round trip through the sender's interleaver, with a short dropout and a
    // long one. Every cell that comes back must be the right one in the right place, and exactly the
    // cells sent in the lost frames must be marked missing; frames come out in order, each once.
    [Fact]
    public void Deinterleaver_after_dropouts_puts_every_cell_back_and_marks_exactly_whats_lost()
    {
        const int cells = 120, frames = 40;   // 120 cells: 24 sent in each of the 5 frames
        var tx = new TimeInterleaver(cells, HamDrmInterleave.Long);
        var rx = new TimeDeinterleaver(cells, HamDrmInterleave.Long);
        var sent = Enumerable.Range(0, frames)
            .Select(n => tx.Add(Enumerable.Range(0, cells).Select(j => new Complex(n * 1000 + j, 0)).ToArray())).ToList();
        var dropped = new HashSet<long> { 7, 8 };   // under a second
        for (long f = 20; f < 30; f++) dropped.Add(f);   // 4 seconds
        var ones = Enumerable.Repeat(1.0, cells).ToArray();

        var outFrames = new List<(long Frame, Complex[] Cells, double[] Weights)>();
        for (long f = 0; f < frames; f++)
            if (!dropped.Contains(f)) outFrames.AddRange(rx.Add(f, sent[(int)f], ones));
        Assert.Empty(rx.Add(33, sent[33], ones));   // a frame heard again late is ignored

        var numbers = outFrames.Select(o => o.Frame).ToList();
        Assert.Equal(numbers.OrderBy(n => n).Distinct(), numbers);   // in order, each once
        Assert.Contains(35L, numbers);                                // and it carried on to the end
        foreach (var (frame, got, weights) in outFrames)
        {
            for (int j = 0; j < cells; j++)
                if (weights[j] != 0) Assert.Equal((double)(frame * 1000 + j), got[j].Real);   // the right cell
            int lostParts = Enumerable.Range(0, TimeDeinterleaver.LongDepth).Count(d => dropped.Contains(frame + d));
            Assert.Equal(lostParts * cells / TimeDeinterleaver.LongDepth, weights.Count(w => w == 0));
        }
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

    // Code review, Oct 2026: a name from the air is shown in the caption, the Gallery and the log, so
    // it's cleaned where it arrives (the file on disk already had a clean name of its own).
    [Fact]
    public void Names_from_the_air_are_made_fit_to_show()
    {
        Assert.Equal("july4-2024 QSLcopy.jp2", MotReceiver.CleanName("july4-2024 QSLcopy.jp2"));   // ordinary: unchanged
        Assert.Equal("img04.jpg", MotReceiver.CleanName("img04.jpg\0\0"));                          // zeros at the end
        Assert.Equal("a_b.jpg", MotReceiver.CleanName("a\u001bb.jpg"));                              // a control character
        Assert.Equal(200, MotReceiver.CleanName(new string('x', 500) + ".jpg").Length);
        Assert.Equal(2, RsFile.LevelOf(MotReceiver.CleanName("tower.jpg.rs2\0")));                   // still read as RS2
    }

    /// <summary>Plays audio through a receiver, then (like the app when a recording finishes)
    /// tells it the audio has ended. Returns the first file received, the last file line shown,
    /// and when the file arrived (seconds into the audio).</summary>
    private static (ReceivedFile? Received, string FileText, double At) Play(float[] audio, bool endOfAudio = true)
    {
        var receiver = new HamDrmReceiver();
        ReceivedFile? received = null;
        string text = "";
        double at = double.NaN, now = 0;
        receiver.FileReceived += f => { if (received is null) { received = f; at = now; } };
        receiver.Decided += _ => { if (receiver.FileText is { Length: > 0 } t) text = t; };
        for (int pos = 0; pos + 480 <= audio.Length; pos += 480)
        {
            now = (pos + 480) / 48000.0;
            receiver.AddSamples(audio[pos..(pos + 480)], 480, 48000);
        }
        if (endOfAudio)
        {
            receiver.EndOfAudio();
            if (receiver.FileText is { Length: > 0 } t) text = t;
        }
        return (received, text, at);
    }
}
