// FountainBsrTests.cs - checks requests for missing pieces (BSR) for pictures sent with RFpix's repair
// code (the fountain code): a station asks only for as many pieces as it still needs; the sender answers
// with new repair pieces, numbered on from the last ones; and one answer fills the gaps of every station
// that asked, whichever pieces each one missed.

using RFpix.HamDrm;
using RFpix.HamDrm.Coding;
using RFpix.Send;
using Xunit;

namespace RFpix.Tests;

public class FountainBsrTests
{
    private static readonly TransmitSettings NetSettings =
        new(HamDrmMode.B, HamDrmBandwidth.Wide, 16, HamDrmInterleave.Long, HamDrmProtection.High);
    private const int FrameBytes = 130;   // what a frame carries with NetSettings (116-byte pieces)

    private static byte[] Picture(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    /// <summary>A picture wrapped with the fountain code at 20%, as RFpix sends it, and noted in a sent log.</summary>
    private static (string Name, byte[] Picture, SentLog Log, SentFile Sent) Sent(int length = 6400)
    {
        var picture = Picture(length, seed: 21);
        var (name, data) = FountainFile.WrapFor(picture, "jpg", "tower", NetSettings, repairPercent: 20);
        var log = new SentLog();
        var sent = log.Add(name, "tower.jpg", data, NetSettings);
        Assert.Equal(MotSender.PieceSizeFor(FrameBytes), sent.PieceSize);
        return (name, picture, log, sent);
    }

    /// <summary>A station that heard the picture with the packets in <paramref name="lost"/> missing.</summary>
    private static MotReceiver Station(SentFile sent, Func<int, bool> lost)
    {
        var mot = new MotReceiver();
        var packets = new MotSender(sent.Name, sent.Data, FrameBytes).Packets(FrameBytes);
        for (int i = 0; i < packets.Count; i++)
            if (!lost(i)) Assert.Null(mot.AddPacket(packets[i]));   // not enough to rebuild it
        return mot;
    }

    private static IncompleteFile Incomplete(MotReceiver station, SentFile sent) =>
        new(station.Unfinished(sent.TransportId)!, NetSettings, "W1AW", DateTime.Now);

    [Fact]
    public void A_station_asks_only_for_as_many_pieces_as_it_still_needs()
    {
        var (_, _, _, sent) = Sent();
        int k = FountainFile.SourcePiecesOf(sent.Name)!.Value;
        var station = Station(sent, i => i >= 10 && i < 35);   // a long fade: 25 frames lost
        var incomplete = Incomplete(station, sent);

        int have = incomplete.File.Pieces - incomplete.File.Missing.Count;
        Assert.True(have < k);
        Assert.Equal(k - have, incomplete.StillNeeded);
        Assert.Equal(incomplete.File.Missing.Take(k - have), incomplete.Request().Missing);
        Assert.True(incomplete.Request().Missing.Count < incomplete.File.Missing.Count);
    }

    [Fact]
    public void The_answer_is_new_repair_pieces_numbered_on_from_the_last()
    {
        var (_, _, log, sent) = Sent();
        var request = new BsrRequest(sent.TransportId, sent.PieceSize, new[] { 3, 9, 12, 40, 41 });
        var plan = log.Answer(request)!;

        Assert.True(plan.Fresh);
        Assert.Equal(FountainFile.FreshPiecesFor(5), plan.Pieces.Count);   // what was needed, and a few spare
        Assert.Equal(Enumerable.Range(sent.Pieces, plan.Pieces.Count), plan.Pieces);
        Assert.Equal((sent.Pieces + plan.Pieces.Count) * sent.PieceSize, plan.File.Data.Length);
        Assert.Equal(sent.Data, plan.File.Data[..sent.Data.Length]);   // what went out before is unchanged

        // Once sent, the next answer's pieces follow on from these (never the same ones twice).
        log.Sending(plan);
        var next = log.Answer(request)!;
        Assert.Equal(plan.Pieces[^1] + 1, next.Pieces[0]);

        // An ordinary picture's request is still answered with the pieces it lists.
        var plain = log.Add("moon.jpg", "moon.jpg", Picture(3000, 5), NetSettings);
        var plainPlan = log.Answer(new BsrRequest(plain.TransportId, plain.PieceSize, new[] { 2, 7 }))!;
        Assert.False(plainPlan.Fresh);
        Assert.Equal(new[] { 2, 7 }, plainPlan.Pieces);
    }

    [Fact]
    public void Requests_for_the_same_picture_combine_into_one_answer()
    {
        var (_, _, log, sent) = Sent();
        var few = log.Answer(new BsrRequest(sent.TransportId, sent.PieceSize, new[] { 1, 2 }))!;
        var many = log.Answer(new BsrRequest(sent.TransportId, sent.PieceSize, Enumerable.Range(10, 9).ToList()))!;
        var both = SentLog.Combine(few, many)!;
        Assert.Equal(many.Pieces, both.Pieces);   // enough for the station that needs the most
        Assert.Equal(2, both.Stations);
        Assert.Null(SentLog.Combine(few, few with { Fresh = false }));
    }

    [Fact]
    public void One_answer_completes_the_picture_for_three_stations_that_missed_different_pieces()
    {
        var (_, picture, log, sent) = Sent();
        // Three stations, each losing a different long stretch of the transmission.
        var stations = new[]
        {
            Station(sent, i => i >= 10 && i < 30),
            Station(sent, i => i >= 30 && i < 50),
            Station(sent, i => i >= 50 && i < 70),
        };
        var missingPieces = new HashSet<int>();
        FixPlan? answer = null;
        foreach (var station in stations)
        {
            var incomplete = Incomplete(station, sent);
            missingPieces.UnionWith(incomplete.File.Missing);
            var plan = log.Answer(incomplete.Request())!;
            answer = answer is null ? plan : SentLog.Combine(answer, plan);
        }
        Assert.NotNull(answer);
        Assert.Equal(3, answer!.Stations);
        log.Sending(answer);

        // The answer goes out once, and every station completes the picture from it.
        var packets = new MotSender(answer.File.Name, answer.File.Data, FrameBytes, onlyPieces: answer.Pieces).Packets(FrameBytes);
        foreach (var station in stations)
        {
            ReceivedFile? file = null;
            foreach (var packet in packets) file ??= station.AddPacket(packet);
            Assert.NotNull(file);
            Assert.Equal("tower.jpg", file!.Name);
            Assert.Equal(picture, file.Data);
        }
        // Sending each station the pieces it missed would have taken far more.
        Assert.True(answer.Pieces.Count * 3 < missingPieces.Count, $"{answer.Pieces.Count} new pieces against {missingPieces.Count} missed");
    }

    [Fact]
    public void A_station_keeps_its_pieces_when_the_picture_grows_with_new_repair_pieces()
    {
        var (_, picture, log, sent) = Sent();
        var station = Station(sent, i => i >= 10 && i < 35);
        int before = station.Unfinished(sent.TransportId)!.Pieces;
        var plan = log.Answer(Incomplete(station, sent).Request())!;

        // Only the answer's header arrives at first: the picture is now bigger, and nothing was lost.
        var packets = new MotSender(plan.File.Name, plan.File.Data, FrameBytes, onlyPieces: plan.Pieces).Packets(FrameBytes);
        Assert.Null(station.AddPacket(packets[0]));
        var grown = station.Unfinished(sent.TransportId)!;
        Assert.Equal(before + plan.Pieces.Count, grown.Pieces);
        Assert.Equal(sent.PieceSize, grown.PieceSize);

        ReceivedFile? file = null;
        foreach (var packet in packets.Skip(1)) file ??= station.AddPacket(packet);
        Assert.Equal(picture, file!.Data);
    }

    [Fact]
    public void A_test_sent_from_the_Beta_Testing_page_can_be_asked_for_its_missing_pieces()
    {
        // Dan's two-radio test, Nov 17, 2026: test 12 was asked for its pieces, and RFpix said it
        // "wasn't sent from here", because only the Send tab noted what it sent.
        var log = new SentLog();
        var test12 = TestTransmissions.All.Single(t => t.Number == 12);   // Fountain 50%
        var sent = test12.RecordSent(log);
        Assert.Equal(test12.SendName, sent.Name);
        var plan = log.Answer(new BsrRequest(sent.TransportId, sent.PieceSize, new[] { 4, 5, 6 }))!;
        Assert.True(plan.Fresh);

        var test10 = TestTransmissions.All.Single(t => t.Number == 10);   // RS2: answered with the pieces asked for
        var rs = test10.RecordSent(log);
        var rsPlan = log.Answer(new BsrRequest(rs.TransportId, rs.PieceSize, new[] { 4, 5 }))!;
        Assert.False(rsPlan.Fresh);
        Assert.Equal(new[] { 4, 5 }, rsPlan.Pieces);
    }
}
