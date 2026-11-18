// FountainCode.cs - RFpix's repair code: rebuilds a file from ANY of its pieces, as long as
// enough of them arrive.
//
// The file is cut into K equal pieces (the "source" pieces), which are sent as they are. Then
// come extra "repair" pieces: each one is a mix of all K source pieces, every source piece
// multiplied by its own number before they're added together. The numbers for repair piece r
// come from a fixed recipe (Coefficients), so the receiver knows them too.
//
// Any K pieces that arrive, source or repair, in any combination, are then K equations in K
// unknowns, which the receiver solves (Gaussian elimination, as taught in school, but in GF(256)).
// So it doesn't matter WHICH pieces a fade took: once K have arrived, the file can be rebuilt
// (very occasionally one more is needed: with exactly K, about 1 time in 250). That's the
// difference from Reed-Solomon, which can only rebuild a fixed share of each of its blocks.
//
// The arithmetic is in GF(256), where adding is XOR and every non-zero byte has an inverse, the
// same kind of number system Reed-Solomon uses (here with the polynomial x^8+x^4+x^3+x^2+1).
// There's no end to the repair pieces: more can always be made, numbered on from the last ones.
//
// The recipe for the numbers must never change once RFpix's fountain code is used on the air, or stations on
// different versions couldn't rebuild each other's files; a test checks it (FountainTests).

namespace RFpix.HamDrm.Coding;

public static class FountainCode
{
    // ---- GF(256) ----

    private static readonly byte[] Exp = new byte[512];   // power -> byte (twice round, to skip a "mod 255")
    private static readonly int[] Log = new int[256];     // byte -> power
    private static readonly byte[][] MulTable = new byte[256][];   // MulTable[a][b] = a x b

    static FountainCode()
    {
        int x = 1;
        for (int i = 0; i < 255; i++)
        {
            Exp[i] = Exp[i + 255] = (byte)x;
            Log[x] = i;
            x <<= 1;
            if ((x & 0x100) != 0) x ^= 0x11D;   // x^8 = x^4 + x^3 + x^2 + 1
        }
        Exp[510] = Exp[0];
        Exp[511] = Exp[1];
        for (int a = 0; a < 256; a++)
        {
            var row = new byte[256];
            if (a != 0)
                for (int b = 1; b < 256; b++) row[b] = Exp[Log[a] + Log[b]];
            MulTable[a] = row;
        }
    }

    private static byte Mul(byte a, byte b) => MulTable[a][b];

    private static byte Inverse(byte a) => a == 0
        ? throw new DivideByZeroException("Zero has no inverse.")
        : Exp[255 - Log[a]];

    /// <summary>dst += c x src, byte by byte.</summary>
    private static void AddScaled(byte[] dst, byte[] src, byte c)
    {
        if (c == 0) return;
        var row = MulTable[c];
        for (int i = 0; i < dst.Length; i++) dst[i] ^= row[src[i]];
    }

    // ---- the recipe ----

    /// <summary>The numbers repair piece <paramref name="repair"/> (0, 1, 2...) multiplies the
    /// <paramref name="k"/> source pieces by: never zero, and the same on every computer, forever.</summary>
    public static byte[] Coefficients(int repair, int k)
    {
        if (repair < 0) throw new ArgumentOutOfRangeException(nameof(repair));
        // SplitMix64, a small, well-known random-number recipe, started from the piece's number
        // and the file's size in pieces (written out here, so it can't change with .NET).
        ulong state = 0x5246706978466173UL ^ ((ulong)(uint)repair << 32) ^ (uint)k;   // "RFpixFas"
        var c = new byte[k];
        for (int i = 0; i < k; i++)
        {
            byte b;
            do
            {
                state += 0x9E3779B97F4A7C15UL;
                ulong z = state;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                z ^= z >> 31;
                b = (byte)(z >> 56);
            } while (b == 0);
            c[i] = b;
        }
        return c;
    }

    // ---- sending ----

    /// <summary>Makes repair pieces number <paramref name="first"/> onwards (<paramref name="count"/>
    /// of them) for the source pieces, all the same size.</summary>
    public static byte[][] Repair(IReadOnlyList<byte[]> source, int count, int first = 0)
    {
        int k = source.Count;
        if (k == 0) throw new ArgumentException("There must be at least one source piece.", nameof(source));
        int size = source[0].Length;
        if (source.Any(s => s.Length != size)) throw new ArgumentException("The source pieces must all be the same size.", nameof(source));

        var repairs = new byte[count][];
        for (int r = 0; r < count; r++)
        {
            var piece = new byte[size];
            var c = Coefficients(first + r, k);
            for (int i = 0; i < k; i++) AddScaled(piece, source[i], c[i]);
            repairs[r] = piece;
        }
        return repairs;
    }

    // ---- receiving ----

    /// <summary>Rebuilds the <paramref name="k"/> source pieces from the pieces that arrived, keyed by
    /// piece number: 0 to k-1 are the source pieces, k onwards the repair pieces (k is repair 0).
    /// Returns null if there isn't enough to rebuild them yet (fewer than k pieces, or, rarely, k
    /// pieces that happen not to be enough: one more then does it).</summary>
    public static byte[][]? Rebuild(int k, int size, IReadOnlyDictionary<int, byte[]> arrived)
    {
        if (k <= 0) throw new ArgumentOutOfRangeException(nameof(k));
        var result = new byte[k][];
        var missing = new List<int>();
        for (int i = 0; i < k; i++)
        {
            if (arrived.TryGetValue(i, out var piece)) result[i] = Checked(piece, size);
            else missing.Add(i);
        }
        if (missing.Count == 0) return result;

        var repairNumbers = arrived.Keys.Where(n => n >= k).Order().ToList();
        if (repairNumbers.Count < missing.Count) return null;

        // Each repair piece, with the source pieces that did arrive taken back out of it, leaves an
        // equation in the missing ones only: sum of (number x missing piece) = what's left.
        var column = new Dictionary<int, int>();   // missing piece -> its column in the equations
        for (int j = 0; j < missing.Count; j++) column[missing[j]] = j;
        var rows = new List<(byte[] Numbers, byte[] Value)>();
        foreach (int n in repairNumbers)
        {
            var c = Coefficients(n - k, k);
            var value = (byte[])Checked(arrived[n], size).Clone();
            var numbers = new byte[missing.Count];
            for (int i = 0; i < k; i++)
            {
                if (result[i] is { } known) AddScaled(value, known, c[i]);   // adding is taking away, in GF(256)
                else numbers[column[i]] = c[i];
            }
            rows.Add((numbers, value));
        }

        // Solve: for each missing piece in turn, find an equation that has it, scale that equation so
        // its number is 1, and take it out of all the others. (Spare equations are used if one turns
        // out to repeat what others already said.)
        int m = missing.Count;
        for (int col = 0; col < m; col++)
        {
            int pivot = -1;
            for (int r = col; r < rows.Count; r++)
                if (rows[r].Numbers[col] != 0) { pivot = r; break; }
            if (pivot < 0) return null;   // not enough different equations yet
            (rows[col], rows[pivot]) = (rows[pivot], rows[col]);

            var (numbers, value) = rows[col];
            byte scale = Inverse(numbers[col]);
            if (scale != 1)
            {
                var mul = MulTable[scale];
                for (int j = 0; j < m; j++) numbers[j] = mul[numbers[j]];
                for (int i = 0; i < size; i++) value[i] = mul[value[i]];
            }
            for (int r = 0; r < rows.Count; r++)
            {
                if (r == col) continue;
                byte f = rows[r].Numbers[col];
                if (f == 0) continue;
                AddScaled(rows[r].Numbers, numbers, f);
                AddScaled(rows[r].Value, value, f);
            }
        }
        for (int j = 0; j < m; j++) result[missing[j]] = rows[j].Value;
        return result;
    }

    private static byte[] Checked(byte[] piece, int size) =>
        piece.Length == size ? piece : throw new ArgumentException($"A piece is {piece.Length} bytes, not {size}.");
}
