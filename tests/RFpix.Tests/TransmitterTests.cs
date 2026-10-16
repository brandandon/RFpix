// TransmitterTests.cs - checks RFpix's HamDRM transmitter three ways:
//   1. against QSSTV's transmitter: every test recording (made by QSSTV) is rebuilt
//      sample for sample;
//   2. RFpix's own receiver decodes what it sends to the exact original file;
//   3. the pieces are made and ordered exactly as QSSTV does.
// (A fourth check, QSSTV's own receiver decoding RFpix's audio, was done by hand on
// Oct 15, 2026, with QSSTV's receiver run headless: five transmissions, all identical.)

using RFpix.Audio;
using RFpix.HamDrm;
using RFpix.HamDrm.Coding;
using Xunit;

namespace RFpix.Tests;

public class TransmitterTests
{
    // ---- 1. the same signal as QSSTV ----

    [Theory]
    [MemberData(nameof(Recordings.All), MemberType = typeof(Recordings))]
    public void Every_recording_is_rebuilt_sample_for_sample(string file)
    {
        var (name, data, settings) = WhatWasSent(file);
        // QSSTV leaves 18 in the FAC's reserved bits of every other frame; RFpix sends 0.
        // Put QSSTV's back, and everything else must already be identical.
        var tx = new HamDrmTransmitter(name, data, settings, "RFPIX") { ReservedFacBits = f => f % 2 == 1 ? 18 : 0 };
        var audio = tx.Audio();

        var recording = WavFile.Read(Recordings.Path(file)).Samples;
        Assert.Equal(recording.Length - 2 * 48000, audio.Length);   // the recordings have 1 s of silence each end
        int differing = 0;
        for (int i = 0; i < audio.Length; i++)
            if (audio[i] != recording[48000 + i]) differing++;
        Assert.Equal(0, differing);
    }

    [Fact]
    public void RFpix_sends_zeros_in_the_reserved_FAC_bits_and_a_good_checksum()
    {
        var settings = new TransmitSettings(HamDrmMode.B, HamDrmBandwidth.Wide, 16, HamDrmInterleave.Long, HamDrmProtection.High);
        for (int frame = 0; frame < 3; frame++)
        {
            var bits = FacEncoder.Bits(frame, settings, "KD9DD");
            Assert.All(bits.Skip(31).Take(9), b => Assert.Equal(0, b));
            Assert.Equal(Crc.Crc8(bits, 0, 40), Crc.ToNumber(bits, 40, 8));
        }
    }

    // ---- 2. RFpix's receiver decodes it ----

    public static IEnumerable<object[]> Transmissions() => new[]
    {
        // Settings no recording covers, and the longest callsign the FAC carries.
        new object[] { "img01.jpg", "A", "2.2", 64, "short", "low", "KD9DD/QRP" },
        new object[] { "img07.jpg", "E", "2.2", 4, "short", "low", "W1AW" },
        new object[] { "img14.jp2", "E", "2.5", 16, "long", "low", "KD9DD" },
    };

    [Theory]
    [MemberData(nameof(Transmissions))]
    public void RFpix_receiver_decodes_the_exact_file(string image, string mode, string bandwidth, int qam,
                                                      string interleave, string protection, string callsign)
    {
        var settings = new TransmitSettings(Enum.Parse<HamDrmMode>(mode),
            bandwidth == "2.5" ? HamDrmBandwidth.Wide : HamDrmBandwidth.Narrow, qam,
            interleave == "long" ? HamDrmInterleave.Long : HamDrmInterleave.Short,
            protection == "high" ? HamDrmProtection.High : HamDrmProtection.Low);
        var data = File.ReadAllBytes(Path.Combine(Recordings.Folder, "images", image));
        string name = "test-" + image;

        var (received, callsigns, fac) = Receive(new HamDrmTransmitter(name, data, settings, callsign));

        Assert.NotNull(received);
        Assert.Equal(name, received!.Name);
        Assert.Equal(data, received.Data);
        Assert.Contains(callsign, callsigns);
        Assert.Equal(settings.Qam, fac!.Qam);
        Assert.Equal(settings.Interleave, fac.Interleave);
        Assert.Equal(settings.Protection, fac.Protection);
        Assert.Equal(settings.Bandwidth, fac.Bandwidth);
    }

    [Fact]
    public void A_very_long_file_name_is_cut_to_80_characters_as_QSSTV_does()
    {
        var data = File.ReadAllBytes(Path.Combine(Recordings.Folder, "images", "img09.jpg"));
        string name = new string('x', 86) + ".jpg";
        var settings = new TransmitSettings(HamDrmMode.B, HamDrmBandwidth.Wide, 16, HamDrmInterleave.Long, HamDrmProtection.High);

        var (received, _, _) = Receive(new HamDrmTransmitter(name, data, settings, "KD9DD"));
        Assert.Equal(name[..MotSender.MaxNameLength], received?.Name);
        Assert.Equal(data, received!.Data);
    }

    // ---- 3. the pieces, as QSSTV makes them ----

    [Fact]
    public void Transport_number_and_piece_order_match_QSSTV()
    {
        // Recording 04: mode B, 2.5 kHz, 16-QAM high carries 130 bytes a frame, so 116-byte
        // pieces; img04.jpg (6434 bytes) is 56 of them. Numbers read from QSSTV's own packets.
        var data = File.ReadAllBytes(Path.Combine(Recordings.Folder, "images", "img04.jpg"));
        var mot = new MotSender("img04.jpg", data, 130);
        Assert.Equal(0x1A34, mot.TransportId);
        Assert.Equal(116, mot.PieceSize);
        Assert.Equal(56, mot.BodyPieces);

        // Run-in: header, last piece, pieces 0-22 (24 counted); body: all 56 with header and
        // last piece before 0 and 50; run-out: header, last piece, pieces 0-8 (10 counted).
        var expected = new List<int> { -1, 55 };
        expected.AddRange(Enumerable.Range(0, 23));
        for (int i = 0; i < 56; i++) { if (i % 50 == 0) expected.AddRange(new[] { -1, 55 }); expected.Add(i); }
        expected.AddRange(new[] { -1, 55 });
        expected.AddRange(Enumerable.Range(0, 9));
        Assert.Equal(expected, mot.Order);

        // One packet a frame for the whole list, then 5 more from its start; QSSTV stops
        // as soon as it has made the last of those, so that one is never sent.
        var packets = mot.Packets(130);
        Assert.Equal(expected.Count + MotSender.ExtraGroups, packets.Count);
        Assert.All(packets, p => Assert.True(MotReceiver.CheckCrc16(p)));
    }

    [Fact]
    public void The_audio_lasts_exactly_the_frames_sent()
    {
        var data = File.ReadAllBytes(Path.Combine(Recordings.Folder, "images", "img04.jpg"));
        var settings = new TransmitSettings(HamDrmMode.B, HamDrmBandwidth.Wide, 16, HamDrmInterleave.Long, HamDrmProtection.High);
        var tx = new HamDrmTransmitter("img04.jpg", data, settings, "KD9DD");
        Assert.Equal(100, tx.Frames);
        Assert.Equal(TimeSpan.FromSeconds(40), tx.Duration);
        Assert.Equal(100 * 19200, tx.Audio().Length);   // 400 ms frames at 48 kHz
    }

    [Fact]
    public void Impossible_requests_are_refused_with_a_reason()
    {
        var settings = new TransmitSettings(HamDrmMode.B, HamDrmBandwidth.Wide, 16, HamDrmInterleave.Long, HamDrmProtection.High);
        Assert.Contains("empty", Assert.Throws<ArgumentException>(() => new HamDrmTransmitter("a.jpg", Array.Empty<byte>(), settings, "KD9DD")).Message);
        Assert.Contains("9", Assert.Throws<ArgumentException>(() => new HamDrmTransmitter("a.jpg", new byte[10], settings, "KD9DD/QRP/M")).Message);
    }

    // ---- helpers ----

    /// <summary>The file, name and settings a test recording was made from (its name says them).</summary>
    private static (string Name, byte[] Data, TransmitSettings Settings) WhatWasSent(string file)
    {
        // e.g. 10_modeB_2p5khz_qam16_long_high_rs1.wav
        var p = Path.GetFileNameWithoutExtension(file).Split('_');
        var settings = new TransmitSettings(
            Enum.Parse<HamDrmMode>(p[1][4..]),
            p[2] == "2p5khz" ? HamDrmBandwidth.Wide : HamDrmBandwidth.Narrow,
            int.Parse(p[3][3..]),
            p[4] == "long" ? HamDrmInterleave.Long : HamDrmInterleave.Short,
            p[5] == "high" ? HamDrmProtection.High : HamDrmProtection.Low);
        int rs = p[6][2] - '0';
        string image = Directory.GetFiles(Path.Combine(Recordings.Folder, "images"), $"img{p[0]}.*").Single();
        var data = File.ReadAllBytes(image);
        string name = Path.GetFileName(image);
        if (rs > 0)
        {
            data = RsFile.Wrap(data, "jpg", rs);   // as QSSTV does: the file goes out wrapped, as .rs1-.rs4
            name = Path.GetFileNameWithoutExtension(image) + ".rs" + rs;
        }
        return (name, data, settings);
    }

    /// <summary>Plays a transmission (with a second of silence each side) through a new receiver.</summary>
    internal static (ReceivedFile? File, HashSet<string> Callsigns, FacInfo? Fac) Receive(HamDrmTransmitter tx)
    {
        var signal = tx.Audio();
        var audio = new float[signal.Length + 2 * 48000];
        Array.Copy(signal, 0, audio, 48000, signal.Length);

        var receiver = new HamDrmReceiver();
        ReceivedFile? received = null;
        FacInfo? fac = null;
        var callsigns = new HashSet<string>();
        receiver.FileReceived += f => received ??= f;
        receiver.Decided += _ =>
        {
            if (receiver.Callsign is { } c) callsigns.Add(c);
            fac ??= receiver.Fac;
        };
        for (int pos = 0; pos + 480 <= audio.Length; pos += 480)
            receiver.AddSamples(audio[pos..(pos + 480)], 480, 48000);
        receiver.EndOfAudio();
        return (received, callsigns, fac);
    }
}
