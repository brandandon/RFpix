// FountainTests.cs - checks RFpix's repair code (FountainCode, FountainFile): that a file is
// rebuilt from any pieces that arrive, as long as there are enough of them, whichever were lost;
// that it says so plainly when there aren't; and that its recipe never changes. Then that it goes
// over the air: sent under a name like "tower.fc33", and received and rebuilt by the receiver.

using System.Text.Json;
using System.Text.Json.Nodes;
using RFpix.HamDrm;
using RFpix.HamDrm.Coding;
using RFpix.Send;
using Xunit;

namespace RFpix.Tests;

public class FountainTests
{
    private static byte[][] Pieces(int count, int size, int seed)
    {
        var random = new Random(seed);
        return Enumerable.Range(0, count).Select(_ => { var b = new byte[size]; random.NextBytes(b); return b; }).ToArray();
    }

    /// <summary>The pieces numbered in <paramref name="which"/>: source pieces first, then repairs.</summary>
    private static Dictionary<int, byte[]> Arrived(byte[][] source, byte[][] repairs, IEnumerable<int> which) =>
        which.ToDictionary(n => n, n => n < source.Length ? source[n] : repairs[n - source.Length]);

    private static void AssertSame(byte[][] expected, byte[][]? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Length, actual!.Length);
        for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], actual[i]);
    }

    [Fact]
    public void Any_mix_of_one_more_than_K_pieces_rebuilds_the_file()
    {
        const int k = 60, repairs = 40;
        var source = Pieces(k, 50, seed: 1);
        var repair = FountainCode.Repair(source, repairs);
        var random = new Random(2);
        for (int trial = 0; trial < 200; trial++)
        {
            var which = Enumerable.Range(0, k + repairs).OrderBy(_ => random.Next()).Take(k + 1);
            AssertSame(source, FountainCode.Rebuild(k, 50, Arrived(source, repair, which)));
        }
    }

    [Fact]
    public void Exactly_K_pieces_are_nearly_always_enough()
    {
        // With exactly K, about 1 time in 250 the pieces happen not to be enough (one more then is).
        const int k = 60, repairs = 40;
        var source = Pieces(k, 50, seed: 3);
        var repair = FountainCode.Repair(source, repairs);
        var random = new Random(4);
        int rebuilt = 0;
        for (int trial = 0; trial < 500; trial++)
        {
            var which = Enumerable.Range(0, k + repairs).OrderBy(_ => random.Next()).Take(k);
            var result = FountainCode.Rebuild(k, 50, Arrived(source, repair, which));
            if (result is null) continue;
            AssertSame(source, result);   // when it says it rebuilt them, they're right
            rebuilt++;
        }
        Assert.True(rebuilt >= 490, $"Only {rebuilt} of 500 rebuilt.");
    }

    [Fact]
    public void A_long_fade_that_takes_only_file_pieces_is_rebuilt()
    {
        // The worst case for Reed-Solomon: every lost piece in one stretch. Here it doesn't matter.
        const int k = 300, repairs = 60;
        var source = Pieces(k, 200, seed: 5);
        var repair = FountainCode.Repair(source, repairs);
        var which = Enumerable.Range(0, k + repairs).Where(n => n < 100 || n >= 160);
        AssertSame(source, FountainCode.Rebuild(k, 200, Arrived(source, repair, which)));
    }

    [Fact]
    public void Too_few_pieces_say_so_rather_than_guess()
    {
        const int k = 30;
        var source = Pieces(k, 40, seed: 6);
        var repair = FountainCode.Repair(source, 10);
        // One short of K, however they're mixed.
        Assert.Null(FountainCode.Rebuild(k, 40, Arrived(source, repair, Enumerable.Range(0, k - 1))));
        Assert.Null(FountainCode.Rebuild(k, 40, Arrived(source, repair, Enumerable.Range(11, k - 1))));
        Assert.Null(FountainCode.Rebuild(k, 40, new Dictionary<int, byte[]>()));
    }

    [Fact]
    public void Nothing_lost_needs_no_repair_pieces()
    {
        var source = Pieces(10, 16, seed: 7);
        AssertSame(source, FountainCode.Rebuild(10, 16, Arrived(source, Array.Empty<byte[]>(), Enumerable.Range(0, 10))));
    }

    [Fact]
    public void More_repair_pieces_can_be_made_later_numbered_on()
    {
        // So a request for missing pieces can be answered with new repair pieces.
        var source = Pieces(20, 32, seed: 8);
        var all = FountainCode.Repair(source, 8);
        var later = FountainCode.Repair(source, 3, first: 5);
        for (int i = 0; i < 3; i++) Assert.Equal(all[5 + i], later[i]);
    }

    [Fact]
    public void The_recipe_never_changes()
    {
        // If these change, stations on different versions of RFpix couldn't rebuild each
        // other's files. (Worked out by the first version, Nov 14, 2026.)
        Assert.Equal("42FF6D68", Convert.ToHexString(FountainCode.Coefficients(0, 4)));
        Assert.Equal("3C3C63917826276A553E", Convert.ToHexString(FountainCode.Coefficients(7, 10)));
        var source = Enumerable.Range(0, 4).Select(i => Enumerable.Range(0, 8).Select(j => (byte)(i * 8 + j)).ToArray()).ToArray();
        var repair = FountainCode.Repair(source, 2);
        Assert.Equal("9C24F14946FE2B93", Convert.ToHexString(repair[0]));
        Assert.Equal("6C43321DD0FF8EA1", Convert.ToHexString(repair[1]));
    }

    [Theory]
    [InlineData(1, "jpg")]
    [InlineData(192, "jpg")]       // with the header, exactly one 200-byte piece
    [InlineData(6400, "jp2")]
    [InlineData(60000, "avif")]
    public void A_file_comes_back_whole_with_its_extension(int size, string extension)
    {
        var file = new byte[size];
        new Random(size).NextBytes(file);
        var (data, k) = FountainFile.Wrap(file, extension, pieceSize: 200, repairPercent: 20);
        Assert.Equal(k + FountainFile.RepairPieces(k, 20), data.Length / 200);

        // Lose one fewer pieces than there are repair pieces, from the start of the file (one spare,
        // as with exactly K the pieces now and then aren't quite enough).
        int lost = data.Length / 200 - k - 1;
        var back = FountainFile.Unwrap(data, k, 200, n => n >= lost);
        Assert.NotNull(back);
        Assert.Equal(file, back!.Value.File);
        Assert.Equal(extension, back.Value.Extension);
    }

    [Fact]
    public void A_file_one_piece_short_isnt_returned()
    {
        var file = new byte[5000];
        new Random(9).NextBytes(file);
        var (data, k) = FountainFile.Wrap(file, "jpg", pieceSize: 100, repairPercent: 20);
        int lost = data.Length / 100 - k + 1;
        Assert.Null(FountainFile.Unwrap(data, k, 100, n => n >= lost));
    }

    [Fact]
    public void Twenty_percent_extra_on_a_small_picture()
    {
        // For a 6.4 kB picture in 200-byte pieces: 33 file pieces and 7 repair pieces.
        Assert.Equal(33, FountainFile.SourcePieces(6400, 200));
        Assert.Equal(7, FountainFile.RepairPieces(33, 20));
        Assert.Equal(0, FountainFile.RepairPieces(33, 0));
        Assert.Throws<ArgumentException>(() => FountainFile.Wrap(Array.Empty<byte>(), "jpg", 200, 20));
    }

    // ---- over the air ----

    private static readonly TransmitSettings NetSettings =
        new(HamDrmMode.B, HamDrmBandwidth.Wide, 16, HamDrmInterleave.Long, HamDrmProtection.High);

    [Fact]
    public void The_name_says_how_many_pieces_the_file_has()
    {
        Assert.Equal("tower.fc33", FountainFile.NameFor("tower", 33));
        Assert.Equal(33, FountainFile.SourcePiecesOf("tower.fc33"));
        Assert.Equal(1, FountainFile.SourcePiecesOf("a.b.fc1"));
        Assert.Null(FountainFile.SourcePiecesOf("tower.jpg"));
        Assert.Null(FountainFile.SourcePiecesOf("tower.rs2"));
        Assert.Null(FountainFile.SourcePiecesOf("tower.fc"));
        Assert.Null(FountainFile.SourcePiecesOf("tower.fc0"));
        Assert.Null(FountainFile.SourcePiecesOf("tower.fc99999"));
    }

    /// <summary>Plays the packets of a file into a MOT receiver, leaving out those in
    /// <paramref name="lost"/>. Returns the file and the packet that completed it.</summary>
    private static (ReceivedFile? File, int At) PlayPackets(List<byte[]> packets, Func<int, bool> lost)
    {
        var mot = new MotReceiver();
        for (int i = 0; i < packets.Count; i++)
            if (!lost(i) && mot.AddPacket(packets[i]) is { } file) return (file, i);
        return (null, -1);
    }

    [Fact]
    public void A_fade_in_the_middle_of_the_picture_is_rebuilt()
    {
        var picture = new byte[6400];
        new Random(10).NextBytes(picture);
        const int frameBytes = 130;
        int pieceSize = MotSender.PieceSizeFor(frameBytes);
        var (data, k) = FountainFile.Wrap(picture, "jpg", pieceSize, repairPercent: 20);
        string name = FountainFile.NameFor("tower", k);
        var packets = new MotSender(name, data, frameBytes).Packets(frameBytes);
        // A fade two thirds of the way through (past the pieces the run-in repeats), a twelfth long.
        int fadeStart = packets.Count * 2 / 3, fadeLength = packets.Count / 12;
        bool Fade(int i) => i >= fadeStart && i < fadeStart + fadeLength;

        var (file, _) = PlayPackets(packets, Fade);
        Assert.NotNull(file);
        Assert.Equal("tower.jpg", file!.Name);
        Assert.Equal(picture, file.Data);
        Assert.Equal(k, file.RepairCodePieces);
        Assert.True(file.RepairedPieces > 0);
        Assert.Null(file.Problem);

        // The same fade loses the same picture sent without the repair code.
        var plain = new MotSender("tower.jpg", picture, frameBytes).Packets(frameBytes);
        Assert.Null(PlayPackets(plain, i => i >= plain.Count * 2 / 3 && i < plain.Count * 2 / 3 + plain.Count / 12).File);
    }

    [Fact]
    public void With_nothing_lost_the_file_is_ready_before_the_transmission_ends()
    {
        // Once enough pieces are in, the rest aren't waited for. (The last piece, a repair piece, comes
        // early and often with the header, so it may stand in for the file's last piece or so.)
        var picture = new byte[3000];
        new Random(11).NextBytes(picture);
        const int frameBytes = 130;
        var (data, k) = FountainFile.Wrap(picture, "jp2", MotSender.PieceSizeFor(frameBytes), repairPercent: 30);
        var packets = new MotSender(FountainFile.NameFor("moon", k), data, frameBytes).Packets(frameBytes);
        var (file, at) = PlayPackets(packets, _ => false);
        Assert.Equal(picture, file!.Data);
        Assert.Equal("moon.jp2", file.Name);
        Assert.InRange(file.RepairedPieces, 0, 2);
        Assert.True(at < packets.Count * 85 / 100, $"Finished at packet {at} of {packets.Count}.");
    }

    [Fact]
    public void A_repair_coded_file_goes_out_without_repeats()
    {
        // Header twice, then each piece once with the header again every 25: no run-in or run-out.
        var data = new byte[116 * 60];
        var mot = new MotSender("tower.fc50", data, 130);
        Assert.Equal(60, mot.BodyPieces);
        Assert.Equal(new[] { -1, -1, 0, 1 }, mot.Order.Take(4));
        Assert.Equal(Enumerable.Range(0, 60), mot.Order.Where(p => p >= 0));
        Assert.Equal(2 + 2, mot.Order.Count(p => p < 0));   // at the start, then before pieces 25 and 50
        Assert.Equal(-1, mot.Order[2 + 25]);

        // The same file sent plainly takes many more pieces (QSSTV's run-in and run-out).
        var plain = new MotSender("tower.jpg", data, 130);
        Assert.True(plain.Order.Count >= mot.Order.Count + 30, $"{plain.Order.Count} against {mot.Order.Count}");
        Assert.Equal(new[] { -1, 59, 0 }, plain.Order.Take(3));   // (unchanged)
    }

    [Fact]
    public void RFpix_sends_and_receives_a_repair_coded_picture_over_the_air()
    {
        var picture = File.ReadAllBytes(Path.Combine(Recordings.Folder, "images", "img04.jpg"));
        var (name, data) = FountainFile.WrapFor(picture, "jpg", "img04", NetSettings, repairPercent: 20);
        Assert.StartsWith("img04.fc", name);
        var tx = new HamDrmTransmitter(name, data, NetSettings, "N0CALL");
        Assert.Equal(HamDrmTransmitter.PieceSizeFor(NetSettings), tx.PieceSize);
        int k = FountainFile.SourcePiecesOf(name)!.Value;
        Assert.Equal(k + FountainFile.RepairPieces(k, 20), tx.Pieces);

        var (received, callsigns, _) = TransmitterTests.Receive(tx);
        Assert.NotNull(received);
        Assert.Equal("img04.jpg", received!.Name);
        Assert.Equal(picture, received.Data);
        Assert.NotNull(received.RepairCodePieces);
        Assert.Contains("N0CALL", callsigns);
    }

    [Fact]
    public void Auto_fits_a_repair_coded_picture_inside_the_target_time()
    {
        var profile = SendProfile.NetRepeater with { RepairPercent = 20 };
        int max = PictureFitter.MaxFileBytes(FountainFile.NameFor("picture", 9999), profile);
        int plain = PictureFitter.MaxFileBytes("picture.jpg", SendProfile.NetRepeater);
        // Only a little less picture than with no repair data: the 20% extra mostly fits in the time
        // a plain picture spends on QSSTV's repeats (Nov 14, 2026: 94%).
        Assert.InRange(max, plain * 88 / 100, plain);
        var (name, data) = FountainFile.WrapFor(new byte[max], "jpg", "picture", profile.Settings, 20);
        Assert.True(PictureFitter.DurationFor(name, data.Length, profile.Settings) <= TimeSpan.FromSeconds(profile.TargetSeconds));
    }

    [Fact]
    public void Profiles_saved_before_the_repair_code_still_load()
    {
        var json = JsonNode.Parse(JsonSerializer.Serialize(SendProfile.NetRepeater))!.AsObject();
        json.Remove("RepairPercent");
        var loaded = JsonSerializer.Deserialize<SendProfile>(json.ToJsonString())!;
        Assert.Equal(0, loaded.RepairPercent);
        Assert.True(loaded.SameAs(SendProfile.NetRepeater));
        Assert.Equal("no repair data", loaded.RepairText);
        Assert.Equal("fountain 20%", (loaded with { RepairPercent = 20 }).RepairText);
        Assert.Equal("RS2", (loaded with { ReedSolomon = 2 }).RepairText);
    }

    [Fact]
    public void The_Send_page_offers_fountain_20_50_and_75()
    {
        Assert.Equal(new[] { 20, 50, 75 }, SendProfile.FountainChoices);
        Assert.Equal(0, SendProfile.NearestFountainChoice(20));
        Assert.Equal(1, SendProfile.NearestFountainChoice(45));
        Assert.Equal(2, SendProfile.NearestFountainChoice(100));
    }
}
