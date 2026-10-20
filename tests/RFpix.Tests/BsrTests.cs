// BsrTests.cs - checks asking for missing pieces (BSR) and answering with a fix.

using System.Text;
using RFpix.HamDrm;
using RFpix.Send;
using Xunit;

namespace RFpix.Tests;

public class BsrTests
{
    private static readonly TransmitSettings NetSettings =
        new(HamDrmMode.B, HamDrmBandwidth.Wide, 16, HamDrmInterleave.Long, HamDrmProtection.High);

    // ---- the bsr.bin file ----

    [Fact]
    public void A_request_is_written_as_EasyPal_and_QSSTV_write_it()
    {
        var r = new BsrRequest(1234, 100, new[] { 3, 4, 5, 9 }, "tower.jp2", 11010);
        Assert.Equal("1234\nH_OK\n100\n3\n4\n5\n9\n-99\ntower.jp2\n11010", Encoding.Latin1.GetString(r.ToBytes()));
        // QSSTV's "compatible" style: a run becomes -1 and the next piece listed.
        Assert.Equal("1234\nH_OK\n100\n3\n-1\n9\n-99\n", Encoding.Latin1.GetString(r.ToBytes(compact: true)));
        var run = new BsrRequest(7, 100, new[] { 10, 11, 12 });
        Assert.Equal("7\nH_OK\n100\n10\n-1\n12\n-99\n", Encoding.Latin1.GetString(run.ToBytes(compact: true)));
    }

    [Fact]
    public void A_request_is_read_back_in_either_style()
    {
        var r = new BsrRequest(1234, 100, new[] { 3, 4, 5, 9 }, "tower.jp2", 11010);
        var back = BsrRequest.Parse(r.ToBytes())!;
        Assert.Equal(1234, back.TransportId);
        Assert.Equal(100, back.PieceSize);
        Assert.Equal(new[] { 3, 4, 5, 9 }, back.Missing);
        Assert.Equal("tower.jp2", back.FileName);
        Assert.Equal(11010, back.ModeCode);

        // Compact style asks for the whole run, up to the next piece listed (as QSSTV reads it).
        var compact = BsrRequest.Parse(r.ToBytes(compact: true))!;
        Assert.Equal(new[] { 3, 4, 5, 6, 7, 8, 9 }, compact.Missing);
        Assert.Null(compact.FileName);

        // Windows line ends are fine; anything else isn't a request.
        Assert.NotNull(BsrRequest.Parse(Encoding.Latin1.GetBytes("1234\r\nH_OK\r\n100\r\n3\r\n-99\r\n")));
        Assert.Null(BsrRequest.Parse(Encoding.Latin1.GetBytes("hello")));
        Assert.Null(BsrRequest.Parse(Encoding.Latin1.GetBytes("1234\nNOPE\n100\n3\n-99\n")));
        Assert.Null(BsrRequest.Parse(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10 }));
    }

    [Fact]
    public void The_mode_is_written_as_QSSTVs_five_digits()
    {
        Assert.Equal(11010, BsrRequest.ModeCodeOf(NetSettings));
        Assert.Equal(1121, BsrRequest.ModeCodeOf(new(HamDrmMode.A, HamDrmBandwidth.Wide, 64, HamDrmInterleave.Short, HamDrmProtection.Low)));
        Assert.Equal(20000, BsrRequest.ModeCodeOf(new(HamDrmMode.E, HamDrmBandwidth.Narrow, 4, HamDrmInterleave.Long, HamDrmProtection.High)));
    }

    [Fact]
    public void Requests_take_transport_IDs_0_1_and_2_in_turn()
    {
        var ids = Enumerable.Range(0, 6).Select(_ => BsrTransportIds.Next()).ToArray();
        Assert.All(ids, id => Assert.InRange(id, 0, 2));
        Assert.Equal(3, ids.Distinct().Count());
    }

    // ---- sending only some pieces ----

    [Fact]
    public void A_fix_sends_only_the_pieces_asked_for_between_the_run_in_and_run_out()
    {
        var data = Picture(30000);
        var full = new MotSender("pic.jp2", data, 100);
        var fix = new MotSender("pic.jp2", data, 100, onlyPieces: new[] { 20, 7, 7, 999 });
        Assert.Equal(full.TransportId, fix.TransportId);
        Assert.True(fix.Order.Count < full.Order.Count / 2);
        // The middle of the list is just 7 and 20 (in order, once each; 999 isn't in the file).
        var runInPart = full.Order.TakeWhile((_, i) => i < IndexAfterRunIn(full.Order)).ToList();
        Assert.Equal(runInPart, fix.Order.Take(runInPart.Count));
        Assert.Equal(new[] { 7, 20 }, fix.Order.Skip(runInPart.Count).Take(2));
        Assert.Throws<ArgumentException>(() => new MotSender("pic.jp2", data, 100, onlyPieces: new[] { 999 }));
        Assert.Equal(1, new MotSender("bsr.bin", data, 100, transportId: 1).TransportId);
    }

    // The run-in ends after 24 entries that count (header and last piece count once together).
    private static int IndexAfterRunIn(IReadOnlyList<int> order)
    {
        int counter = 0, i = 0;
        while (counter < MotSender.RunIn)
        {
            if (order[i] < 0) { i += 2; counter++; continue; }   // header, then the last piece
            i++; counter++;
        }
        return i;
    }

    // ---- the sent log ----

    [Fact]
    public void A_request_is_answered_only_for_a_picture_sent_from_here()
    {
        var log = new SentLog();
        var sent = log.Add("tower.jp2", "tower.jp2", Picture(5000), NetSettings);
        Assert.True(sent.Pieces > 10);

        var plan = log.Answer(new BsrRequest(sent.TransportId, sent.PieceSize, new[] { 1, 2, 8 }))!;
        Assert.Same(sent, plan.File);
        Assert.Equal(new[] { 1, 2, 8 }, plan.Pieces);

        Assert.Null(log.Answer(new BsrRequest(sent.TransportId + 1, sent.PieceSize, new[] { 1 })));          // another picture
        Assert.Null(log.Answer(new BsrRequest(sent.TransportId, sent.PieceSize + 1, new[] { 1 })));          // same number, other piece size
        Assert.Null(log.Answer(new BsrRequest(sent.TransportId, sent.PieceSize, new[] { 1, sent.Pieces })));  // a piece it doesn't have

        for (int i = 0; i < SentLog.Keep; i++) log.Add($"other{i}.jp2", $"other{i}.jp2", Picture(500 + i), NetSettings);
        Assert.Null(log.Answer(new BsrRequest(sent.TransportId, sent.PieceSize, new[] { 1 })));               // too long ago
    }

    // ---- the whole exchange, over the air ----

    [Fact]
    public void Missing_pieces_are_asked_for_and_the_fix_completes_the_picture()
    {
        var data = Picture(9000);
        var log = new SentLog();
        log.Add("tower.jp2", "tower.jp2", data, NetSettings);
        var tx = new HamDrmTransmitter("tower.jp2", data, NetSettings, "KD9DD");

        // The picture arrives with a burst of noise wiping out about 4 seconds in the middle.
        var sound = tx.Audio();
        var rng = new Random(5);
        int from = sound.Length / 2, length = 4 * 48000;
        for (int i = from; i < from + length; i++) sound[i] = (float)(rng.NextDouble() - 0.5);

        var receiver = new HamDrmReceiver();
        ReceivedFile? received = null;
        receiver.FileReceived += f => received = f;
        Play(receiver, sound);
        Assert.Null(received);
        var incomplete = Assert.Single(receiver.Incomplete);
        Assert.Equal("tower.jp2", incomplete.File.Name);
        Assert.Equal("KD9DD", incomplete.Callsign);
        Assert.Equal(NetSettings, incomplete.Settings);
        Assert.NotEmpty(incomplete.File.Missing);

        // The receiving station sends its request; the sending station hears it.
        var sender = new HamDrmReceiver();
        HeardBsr? heard = null;
        sender.BsrReceived += b => heard = b;
        ReceivedFile? stray = null;
        sender.FileReceived += f => stray = f;
        Play(sender, incomplete.Transmitter("N0CALL").Audio());
        Assert.NotNull(heard);
        Assert.Null(stray);   // the request isn't saved as a picture
        Assert.Equal("N0CALL", heard!.Callsign);
        Assert.Equal(incomplete.File.Missing, heard.Request.Missing);

        // The sender answers with only those pieces, and the picture is complete.
        var plan = log.Answer(heard.Request)!;
        var fix = plan.Transmitter("KD9DD");
        Assert.True(fix.Duration < tx.Duration / 2);
        Play(receiver, fix.Audio());
        Assert.NotNull(received);
        Assert.Equal(data, received!.Data);
        Assert.Empty(receiver.Incomplete);
    }

    private static void Play(HamDrmReceiver receiver, float[] signal)
    {
        var audio = new float[signal.Length + 2 * 48000];
        Array.Copy(signal, 0, audio, 48000, signal.Length);
        for (int pos = 0; pos + 480 <= audio.Length; pos += 480)
            receiver.AddSamples(audio[pos..(pos + 480)], 480, 48000);
        receiver.EndOfAudio();
    }

    private static byte[] Picture(int length)
    {
        var data = new byte[length];
        new Random(length).NextBytes(data);
        return data;
    }
}
