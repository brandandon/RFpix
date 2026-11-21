// FmSignalBsrTests.cs - checks asking for missing pieces of a picture sent with RFpix's FM signal: the
// request (one short frame in the FM signal), the sender's answer from its sent log (new repair pieces at
// the picture's own speed), and the picture completing at the station that asked.

using RFpix.FmSignal;
using RFpix.HamDrm.Coding;
using RFpix.Send;
using Xunit;

namespace RFpix.Tests;

public class FmSignalBsrTests
{
    /// <summary>Feeds sound to a live receiver 10 ms at a time, then 7 seconds of quiet.</summary>
    private static void Play(FmSignalLiveReceiver receiver, float[] sound)
    {
        var all = new float[24000].Concat(sound).Concat(new float[48000 * 7]).ToArray();
        for (int pos = 0; pos + 480 <= all.Length; pos += 480) receiver.AddSamples(all[pos..(pos + 480)], 480, 48000);
    }

    [Fact]
    public void A_request_goes_out_in_one_frame_and_is_heard_as_a_request()
    {
        var request = new FmRequest(FileNumber: 4321, SourcePieces: 40, PieceBytes: 230, Needed: 9, Callsign: "N0CALL");
        var sound = FmSignalTransmitter.RequestAudio(request);
        Assert.Equal(FmSignalLayout.FrameLength, sound.Length);   // about 3 seconds

        var receiver = new FmSignalLiveReceiver();
        var heard = new List<FmRequest>();
        int files = 0, reports = 0;
        receiver.RequestHeard += r => heard.Add(r);
        receiver.FileReceived += (_, _) => files++;
        receiver.TransmissionEnded += _ => reports++;
        Play(receiver, sound);
        Assert.Equal(request, Assert.Single(heard));
        Assert.Equal(0, files);
        Assert.Equal(0, reports);           // not a picture: nothing for the test log
        Assert.Empty(receiver.Unfinished);  // nor a picture missing pieces
    }

    [Fact]
    public void A_picture_cut_short_is_completed_by_asking()
    {
        var test = TestTransmissions.All.Single(t => t.Number == 16);   // FM 4
        var log = new SentLog();
        var sent = test.RecordSent(log);
        Assert.Equal(test.FmFileNumber, sent.TransportId);
        var sound = test.Audio("N0CALL");
        Assert.Equal(sent.Pieces, FmSignalTransmitter.Slots(sent.Pieces, test.FmMode!));   // the log holds every piece that went out

        // The receiving station hears only the first frame.
        var receiver = new FmSignalLiveReceiver();
        var files = new List<FmReceivedFile>();
        receiver.FileReceived += (f, _) => files.Add(f);
        Play(receiver, sound[..FmSignalLayout.FrameLength]);
        Assert.Empty(files);
        var missing = Assert.Single(receiver.Unfinished);
        Assert.Equal("rfpix-test-16.jpg", missing.Name);
        Assert.True(missing.StillNeeded > 0);

        // It asks; the sender's log answers with new repair pieces, numbered on from the last ones sent.
        var request = new FmRequest(missing.FileNumber, missing.SourcePieces, missing.PieceBytes, missing.StillNeeded, "N1CALL");
        var plan = log.Answer(request)!;
        Assert.True(plan.Fresh);
        Assert.Equal(sent.Pieces, plan.Pieces[0]);
        Assert.True(plan.Pieces.Count >= FountainFile.FreshPiecesFor(missing.StillNeeded));
        Assert.Equal(plan.Seconds, plan.Audio("N0CALL").Length / 48000.0, 3);
        log.Sending(plan);

        Play(receiver, plan.Audio("N0CALL"));
        var file = Assert.Single(files);
        Assert.Equal(test.Picture(), file.Data);
        Assert.Equal("rfpix-test-16.jpg", file.Name);

        // A second request (another station) is answered with more new pieces, after these.
        var again = log.Answer(request with { Callsign = "N2CALL" })!;
        Assert.Equal(plan.Pieces[^1] + 1, again.Pieces[0]);
    }

    [Fact]
    public void Requests_for_pictures_not_sent_from_here_are_not_answered()
    {
        var log = new SentLog();
        var test = TestTransmissions.All.Single(t => t.Number == 15);
        var sent = test.RecordSent(log);
        Assert.Null(log.Answer(new FmRequest(sent.TransportId + 1, sent.FmSourcePieces, sent.PieceSize, 5, "N0CALL")));   // another file
        Assert.Null(log.Answer(new FmRequest(sent.TransportId, sent.FmSourcePieces, sent.PieceSize + 1, 5, "N0CALL")));   // another speed
        Assert.NotNull(log.Answer(new FmRequest(sent.TransportId, sent.FmSourcePieces, sent.PieceSize, 5, "N0CALL")));
        // A HamDRM request with the same number isn't taken for it.
        Assert.Null(log.Answer(new RFpix.HamDrm.BsrRequest(sent.TransportId, sent.PieceSize, new[] { 1, 2 })));
    }

    [Fact]
    public void Two_stations_requests_combine_into_one_answer()
    {
        var log = new SentLog();
        var test = TestTransmissions.All.Single(t => t.Number == 14);
        var sent = test.RecordSent(log);
        var a = log.Answer(new FmRequest(sent.TransportId, sent.FmSourcePieces, sent.PieceSize, 3, "N1CALL"))!;
        var b = log.Answer(new FmRequest(sent.TransportId, sent.FmSourcePieces, sent.PieceSize, 12, "N2CALL"))!;
        var both = SentLog.Combine(a, b)!;
        Assert.Equal(2, both.Stations);
        Assert.Equal(b.Pieces, both.Pieces);   // enough for the one that needs the most
    }
}
