// Ldpc.cs - modern error correction for RFpix's FM signal: an LDPC code ("low-density parity check",
// the kind used in Wi-Fi, 5G and DVB-S2).
//
// Each block of data bits gets parity bits added. Every parity bit is the sum (XOR) of a few data bits
// and the parity bit before it (a "staircase", which makes adding them quick: this is an
// "irregular repeat-accumulate" code). Each data bit takes part in 3 of the sums. The receiver gets a
// guess for every bit with how sure it is (soft decisions) and passes messages back and forth between
// the bits and the sums until every sum checks out, typically in 5 to 30 rounds.
//
// The codes are made the same way every time from a fixed seed, so sender and receiver agree. No two
// data bits share more than one sum (no "4-cycles"), which keeps the decoding sharp.

namespace RFpix.FmSignal;

public sealed class Ldpc
{
    /// <summary>Bits in every block, sent.</summary>
    public int Length { get; }
    /// <summary>Data bits in every block.</summary>
    public int DataBits { get; }
    private int Checks => Length - DataBits;

    // For each check (sum): the data bits in it. The parity bits follow the staircase.
    private readonly int[][] _checkData;
    // The whole check matrix, edge by edge, for decoding.
    private readonly int[] _checkStart;   // check c's edges are _checkStart[c] .. _checkStart[c+1]-1
    private readonly int[] _edgeBit;      // the bit on each edge

    private const int DataDegree = 3;
    private const double Scale = 0.8;     // the "normalized min-sum" correction

    public Ldpc(int length, int dataBits, int seed)
    {
        if (dataBits <= 0 || dataBits >= length) throw new ArgumentOutOfRangeException(nameof(dataBits));
        Length = length;
        DataBits = dataBits;
        _checkData = Build(Checks, dataBits, seed);

        var start = new int[Checks + 1];
        var edges = new List<int>();
        for (int c = 0; c < Checks; c++)
        {
            start[c] = edges.Count;
            edges.AddRange(_checkData[c]);
            if (c > 0) edges.Add(dataBits + c - 1);   // the parity bit before
            edges.Add(dataBits + c);                  // this check's own parity bit
        }
        start[Checks] = edges.Count;
        _checkStart = start;
        _edgeBit = edges.ToArray();
    }

    /// <summary>The codes RFpix's FM signal uses: 2880 bits a block, at 1/2, 2/3, 3/4 or 5/6 data.</summary>
    public const int BlockBits = 2880;
    private static readonly Ldpc?[] Cache = new Ldpc?[4];
    public static readonly (int Top, int Bottom)[] Rates = { (1, 2), (2, 3), (3, 4), (5, 6) };

    public static Ldpc ForRate(int rateIndex)
    {
        lock (Cache)
        {
            var (top, bottom) = Rates[rateIndex];
            return Cache[rateIndex] ??= new Ldpc(BlockBits, BlockBits * top / bottom, seed: 7100 + rateIndex);
        }
    }

    /// <summary>Which data bits go into each check: each data bit into 3 checks, spread evenly, with no
    /// two data bits sharing more than one check.</summary>
    private static int[][] Build(int checks, int dataBits, int seed)
    {
        var random = new Random(seed);
        var rows = Enumerable.Range(0, checks).Select(_ => new List<int>()).ToArray();
        var rowSets = Enumerable.Range(0, checks).Select(_ => new HashSet<int>()).ToArray();
        // Data bits in a shuffled order, so the least-filled checks don't always go to neighbours.
        foreach (int bit in Enumerable.Range(0, dataBits).OrderBy(_ => random.Next()))
        {
            var chosen = new List<int>(DataDegree);
            for (int d = 0; d < DataDegree; d++)
            {
                int pick = -1;
                // Try the least-filled checks first, at random among them; allow fuller ones if needed.
                int least = rows.Min(r => r.Count);
                for (int extra = 0; extra < 50 && pick < 0; extra++)
                {
                    var candidates = Enumerable.Range(0, checks)
                        .Where(c => rows[c].Count <= least + extra / 5 && !chosen.Contains(c)).ToList();
                    for (int attempt = 0; attempt < 20 && candidates.Count > 0; attempt++)
                    {
                        int c = candidates[random.Next(candidates.Count)];
                        if (chosen.All(other => !rowSets[c].Overlaps(rowSets[other]))) { pick = c; break; }
                    }
                }
                if (pick < 0)   // (never in practice for RFpix's sizes) accept a 4-cycle rather than fail
                    pick = Enumerable.Range(0, checks).First(c => !chosen.Contains(c));
                chosen.Add(pick);
            }
            foreach (int c in chosen) { rows[c].Add(bit); rowSets[c].Add(bit); }
        }
        return rows.Select(r => r.OrderBy(b => b).ToArray()).ToArray();
    }

    /// <summary>The block to send for these data bits (0 or 1 each): the data, then the parity bits.</summary>
    public byte[] Encode(ReadOnlySpan<byte> data)
    {
        if (data.Length != DataBits) throw new ArgumentException($"Expected {DataBits} bits.", nameof(data));
        var block = new byte[Length];
        data.CopyTo(block);
        byte before = 0;
        for (int c = 0; c < Checks; c++)
        {
            int sum = before;
            foreach (int bit in _checkData[c]) sum ^= data[bit];
            block[DataBits + c] = before = (byte)sum;
        }
        return block;
    }

    /// <summary>Whether a block passes every check.</summary>
    public bool Passes(ReadOnlySpan<byte> block)
    {
        for (int c = 0; c < Checks; c++)
        {
            int sum = 0;
            for (int e = _checkStart[c]; e < _checkStart[c + 1]; e++) sum ^= block[_edgeBit[e]];
            if (sum != 0) return false;
        }
        return true;
    }

    /// <summary>Decodes a block from soft guesses: for each bit, positive means probably 0 and negative
    /// probably 1, larger for surer. Returns the data bits, or null if it couldn't make every check work.</summary>
    public byte[]? Decode(ReadOnlySpan<double> soft, int maxRounds = 60)
    {
        if (soft.Length != Length) throw new ArgumentException($"Expected {Length} values.", nameof(soft));
        int edges = _edgeBit.Length;
        var toBit = new double[edges];        // check -> bit messages
        var total = soft.ToArray();           // each bit's belief: its own guess plus all messages
        var hard = new byte[Length];

        for (int round = 0; round < maxRounds; round++)
        {
            for (int c = 0; c < Checks; c++)
            {
                int from = _checkStart[c], to = _checkStart[c + 1];
                double min1 = double.MaxValue, min2 = double.MaxValue;
                int minAt = -1, signs = 0;
                for (int e = from; e < to; e++)
                {
                    double q = total[_edgeBit[e]] - toBit[e];   // what the bit says, without this check
                    double a = Math.Abs(q);
                    if (q < 0) signs ^= 1;
                    if (a < min1) { min2 = min1; min1 = a; minAt = e; }
                    else if (a < min2) min2 = a;
                }
                for (int e = from; e < to; e++)
                {
                    int bit = _edgeBit[e];
                    double q = total[bit] - toBit[e];
                    int sign = signs ^ (q < 0 ? 1 : 0);
                    double r = Scale * (e == minAt ? min2 : min1);
                    double message = sign == 0 ? r : -r;
                    total[bit] = q + message;          // update straight away ("layered": converges faster)
                    toBit[e] = message;
                }
            }
            for (int i = 0; i < Length; i++) hard[i] = total[i] < 0 ? (byte)1 : (byte)0;
            if (Passes(hard)) return hard[..DataBits];
        }
        return null;
    }
}
