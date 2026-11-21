// FmSpecTests.cs - checks that the published description of RFpix's FM signal (docs/RFPIX_FM_SPEC.md and
// the tables in docs/rfpix-fm) still matches the code. The FCC asks that a new mode's technical details be
// documented publicly; if the signal changes, these tests fail until the spec is brought up to date.

using System.Numerics;
using RFpix.FmSignal;
using Xunit;

namespace RFpix.Tests;

public class FmSpecTests
{
    private static string SpecFolder { get; } = FindSpecFolder();

    private static string FindSpecFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RFpix.sln"))) dir = dir.Parent;
        if (dir is null) throw new DirectoryNotFoundException("Couldn't find the RFpix repository folder (RFpix.sln).");
        return Path.Combine(dir.FullName, "docs", "rfpix-fm");
    }

    private static List<int[]> Table(string name) =>
        File.ReadLines(Path.Combine(SpecFolder, name))
            .Where(line => line.Trim().Length > 0 && !line.StartsWith('#'))
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray())
            .ToList();

    private static int Quadrant(Complex value)
    {
        int q = (int)Math.Round((value.Phase - Math.PI / 4) / (Math.PI / 2));
        return ((q % 4) + 4) % 4;
    }

    [Fact]
    public void The_published_sync_and_pilot_table_matches()
    {
        var table = Table("sync-and-pilots.txt");
        Assert.Equal(FmSignalLayout.Carriers, table.Select(row => row[0]));
        foreach (var row in table)
        {
            int k = row[0];
            Assert.Equal(row[1], Quadrant(FmSignalLayout.Sync[k]));
            Assert.Equal(row[2], Quadrant(FmSignalLayout.Pilot(k, 1)));   // symbol 1's sign is +1
        }
    }

    [Theory]
    [InlineData(0, "ldpc-rate-1-2.txt")]
    [InlineData(1, "ldpc-rate-2-3.txt")]
    [InlineData(2, "ldpc-rate-3-4.txt")]
    [InlineData(3, "ldpc-rate-5-6.txt")]
    public void The_published_error_correction_tables_match(int rate, string file)
    {
        var code = Ldpc.ForRate(rate);
        var checks = Table(file);
        Assert.Equal(code.Length - code.DataBits, checks.Count);
        var member = checks.Select(c => c.ToHashSet()).ToArray();

        // With one data bit set, the staircase parity changes at each check holding that bit.
        var data = new byte[code.DataBits];
        for (int bit = 0; bit < code.DataBits; bit++)
        {
            data[bit] = 1;
            var block = code.Encode(data);
            byte before = 0;
            for (int m = 0; m < checks.Count; m++)
            {
                byte parity = block[code.DataBits + m];
                Assert.True((parity != before) == member[m].Contains(bit), $"data bit {bit}, check {m}");
                before = parity;
            }
            data[bit] = 0;
        }
    }

    [Fact]
    public void The_published_check_values_match()
    {
        // The spec's section 11.
        var random = new Random(9102);
        Assert.Equal(new[] { 841453, 513582, 956712, 315715, 629236 }, Enumerable.Range(0, 5).Select(_ => random.Next(1000000)));
        Assert.Equal(new[] { 24208, 16577, 12196, 2573, 17740, 8879, 21485, 19013 }, FmSignalLayout.Spread(FmSignalMode.All[2])[..8]);
        Assert.Equal("11101110011011111011011100011111", string.Concat(FmSignalLayout.Scrambler(32)));
        Assert.Equal("-++++---+--+-++-++----+++-+--+-+",
            string.Concat(Enumerable.Range(0, 32).Select(s => FmSignalLayout.Pilot(13, s) == FmSignalLayout.Pilot(13, 1) ? '+' : '-')));
        Assert.Equal(0xCBF43926u, FmSignalLayout.Crc32("123456789"u8));
        Assert.Equal(new byte[] { 241, 204, 106, 42, 149, 217, 234 }, RFpix.HamDrm.Coding.FountainCode.Coefficients(0, 7));
    }

    [Fact]
    public void The_published_speeds_match()
    {
        // The spec's section 6: id, mid bits, edge bits, data bits a block, bits a symbol, blocks a frame, piece bytes.
        var expected = new[]
        {
            (0, 2, 2, 1440, 208, 4, 170), (1, 4, 2, 1440, 358, 7, 170), (2, 4, 4, 1920, 416, 8, 230),
            (3, 6, 4, 1920, 566, 11, 230), (4, 6, 6, 2160, 624, 13, 260), (5, 8, 6, 2160, 774, 16, 260),
        };
        Assert.Equal(expected, FmSignalMode.All.Select(m =>
            (m.Id, m.MidBits, m.EdgeBits, m.Code.DataBits, m.BitsPerSymbol, m.BlocksPerFrame, m.PieceBytes)));
        Assert.Equal(new[] { 13, 25, 37, 49, 61, 73, 85, 97, 109, 121, 127 }, FmSignalLayout.PilotCarriers);
        Assert.Equal(145152, FmSignalLayout.FrameLength);
    }
}
