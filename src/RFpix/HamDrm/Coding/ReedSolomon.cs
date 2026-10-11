// ReedSolomon.cs - the Reed-Solomon code QSSTV and EasyPal use for their "RS1" to
// "RS4" file protection.
//
// A block is 255 bytes: k bytes of the file, then 255 - k bytes of repair data
// worked out from them. Up to 255 - k bytes known to be missing ("erasures"),
// or half that many wrong bytes in unknown places, can be repaired per block.
// The arithmetic is done in GF(256), a number system where adding is XOR and
// every non-zero byte has an inverse, which is what makes the repair possible.
//
// A faithful port of Phil Karn KA9Q's rs.c (1996-1999, GPL), as included in QSSTV:
// field polynomial x^8 + x^4 + x^3 + x^2 + 1, first root alpha^1.

namespace RFpix.HamDrm.Coding;

public sealed class ReedSolomon
{
    public const int BlockSize = 255;
    private const int NN = 255;
    private const int A0 = NN;   // stands for "zero" in power (index) form

    private static readonly int[] AlphaTo = new int[NN + 1];   // power -> byte
    private static readonly int[] IndexOf = new int[NN + 1];   // byte -> power

    static ReedSolomon()
    {
        // Build the field from the polynomial 1 + x^2 + x^3 + x^4 + x^8.
        int[] pp = { 1, 0, 1, 1, 1, 0, 0, 0, 1 };
        int mask = 1;
        AlphaTo[8] = 0;
        for (int i = 0; i < 8; i++)
        {
            AlphaTo[i] = mask;
            IndexOf[AlphaTo[i]] = i;
            if (pp[i] != 0) AlphaTo[8] ^= mask;
            mask <<= 1;
        }
        IndexOf[AlphaTo[8]] = 8;
        mask >>= 1;
        for (int i = 9; i < NN; i++)
        {
            AlphaTo[i] = AlphaTo[i - 1] >= mask ? AlphaTo[8] ^ ((AlphaTo[i - 1] ^ mask) << 1) : AlphaTo[i - 1] << 1;
            IndexOf[AlphaTo[i]] = i;
        }
        IndexOf[0] = A0;
        AlphaTo[NN] = 0;
    }

    private readonly int _kk;          // data bytes per block
    private readonly int[] _gg;        // generator polynomial, power form

    /// <param name="dataBytes">224 (RS1), 192 (RS2), 160 (RS3) or 128 (RS4).</param>
    public ReedSolomon(int dataBytes)
    {
        _kk = dataBytes;
        int parity = NN - _kk;
        _gg = new int[parity + 1];
        _gg[0] = 1;
        for (int i = 0; i < parity; i++)
        {
            _gg[i + 1] = 1;
            for (int j = i; j > 0; j--)
                _gg[j] = _gg[j] != 0 ? _gg[j - 1] ^ AlphaTo[Modnn(IndexOf[_gg[j]] + 1 + i)] : _gg[j - 1];
            _gg[0] = AlphaTo[Modnn(IndexOf[_gg[0]] + 1 + i)];
        }
        for (int i = 0; i <= parity; i++) _gg[i] = IndexOf[_gg[i]];
    }

    public int DataBytes => _kk;

    /// <summary>Fills block[k..254] with repair bytes for block[0..k-1].</summary>
    public void Encode(byte[] block, int offset = 0)
    {
        int parity = NN - _kk;
        var bb = new int[parity];
        for (int i = _kk - 1; i >= 0; i--)
        {
            int feedback = IndexOf[block[offset + i] ^ bb[parity - 1]];
            if (feedback != A0)
            {
                for (int j = parity - 1; j > 0; j--)
                    bb[j] = _gg[j] != A0 ? bb[j - 1] ^ AlphaTo[Modnn(_gg[j] + feedback)] : bb[j - 1];
                bb[0] = AlphaTo[Modnn(_gg[0] + feedback)];
            }
            else
            {
                for (int j = parity - 1; j > 0; j--) bb[j] = bb[j - 1];
                bb[0] = 0;
            }
        }
        for (int i = 0; i < parity; i++) block[offset + _kk + i] = (byte)bb[i];
    }

    /// <summary>Repairs a 255-byte block in place. <paramref name="erasures"/> lists byte
    /// positions known to be missing. Returns how many bytes were repaired, or -1 if the
    /// block is too damaged (it's then left unchanged).</summary>
    public int Decode(byte[] block, int offset, IReadOnlyList<int> erasures)
    {
        int parity = NN - _kk;
        int noEras = erasures.Count;
        if (noEras > parity) return -1;

        var lambda = new int[parity + 1];
        var s = new int[parity + 1];
        var b = new int[parity + 1];
        var t = new int[parity + 1];
        var omega = new int[parity + 1];
        var root = new int[parity];
        var reg = new int[parity + 1];
        var loc = new int[parity];

        // Syndromes: all zero means the block is already correct.
        for (int i = 1; i <= parity; i++) s[i] = block[offset];
        for (int j = 1; j < NN; j++)
        {
            if (block[offset + j] == 0) continue;
            int tmp = IndexOf[block[offset + j]];
            for (int i = 1; i <= parity; i++) s[i] ^= AlphaTo[Modnn(tmp + i * j)];
        }
        bool synError = false;
        for (int i = 1; i <= parity; i++)
        {
            if (s[i] != 0) synError = true;
            s[i] = IndexOf[s[i]];
        }
        if (!synError) return 0;

        // Erasure locator polynomial.
        lambda[0] = 1;
        if (noEras > 0)
        {
            lambda[1] = AlphaTo[Modnn(erasures[0])];
            for (int i = 1; i < noEras; i++)
            {
                int u = Modnn(erasures[i]);
                for (int j = i + 1; j > 0; j--)
                {
                    int tmp = IndexOf[lambda[j - 1]];
                    if (tmp != A0) lambda[j] ^= AlphaTo[Modnn(u + tmp)];
                }
            }
        }
        for (int i = 0; i < parity + 1; i++) b[i] = IndexOf[lambda[i]];

        // Berlekamp-Massey: find the error+erasure locator polynomial.
        int r = noEras, el = noEras;
        while (++r <= parity)
        {
            int discr = 0;
            for (int i = 0; i < r; i++)
                if (lambda[i] != 0 && s[r - i] != A0) discr ^= AlphaTo[Modnn(IndexOf[lambda[i]] + s[r - i])];
            discr = IndexOf[discr];
            if (discr == A0)
            {
                Array.Copy(b, 0, b, 1, parity);
                b[0] = A0;
            }
            else
            {
                t[0] = lambda[0];
                for (int i = 0; i < parity; i++)
                    t[i + 1] = b[i] != A0 ? lambda[i + 1] ^ AlphaTo[Modnn(discr + b[i])] : lambda[i + 1];
                if (2 * el <= r + noEras - 1)
                {
                    el = r + noEras - el;
                    for (int i = 0; i <= parity; i++)
                        b[i] = lambda[i] == 0 ? A0 : Modnn(IndexOf[lambda[i]] - discr + NN);
                }
                else
                {
                    Array.Copy(b, 0, b, 1, parity);
                    b[0] = A0;
                }
                Array.Copy(t, lambda, parity + 1);
            }
        }

        int degLambda = 0;
        for (int i = 0; i < parity + 1; i++)
        {
            lambda[i] = IndexOf[lambda[i]];
            if (lambda[i] != A0) degLambda = i;
        }

        // Chien search: the locator's roots are the error positions.
        Array.Copy(lambda, 1, reg, 1, parity);
        int count = 0;
        for (int i = 1, k = NN - 1; i <= NN; i++, k = Modnn(NN + k - 1))
        {
            int q = 1;
            for (int j = degLambda; j > 0; j--)
                if (reg[j] != A0)
                {
                    reg[j] = Modnn(reg[j] + j);
                    q ^= AlphaTo[reg[j]];
                }
            if (q != 0) continue;
            root[count] = i;
            loc[count] = k;
            if (++count == degLambda) break;
        }
        if (degLambda != count) return -1;

        // Forney: the size of each error.
        int degOmega = 0;
        for (int i = 0; i < parity; i++)
        {
            int tmp = 0;
            for (int j = Math.Min(degLambda, i); j >= 0; j--)
                if (s[i + 1 - j] != A0 && lambda[j] != A0) tmp ^= AlphaTo[Modnn(s[i + 1 - j] + lambda[j])];
            if (tmp != 0) degOmega = i;
            omega[i] = IndexOf[tmp];
        }
        omega[parity] = A0;

        var fixes = new (int Pos, int Value)[count];
        for (int j = count - 1; j >= 0; j--)
        {
            int num1 = 0;
            for (int i = degOmega; i >= 0; i--)
                if (omega[i] != A0) num1 ^= AlphaTo[Modnn(omega[i] + i * root[j])];
            int num2 = AlphaTo[Modnn(root[j] * 0 + NN)];   // first root alpha^1: (B0 - 1) = 0
            int den = 0;
            for (int i = Math.Min(degLambda, parity - 1) & ~1; i >= 0; i -= 2)
                if (lambda[i + 1] != A0) den ^= AlphaTo[Modnn(lambda[i + 1] + i * root[j])];
            if (den == 0) return -1;
            fixes[j] = (loc[j], num1 != 0 ? AlphaTo[Modnn(IndexOf[num1] + IndexOf[num2] + NN - IndexOf[den])] : 0);
        }
        // Only change the block once the whole repair is known to work.
        foreach (var (pos, value) in fixes) block[offset + pos] ^= (byte)value;
        return count;
    }

    private static int Modnn(int x)
    {
        while (x >= NN)
        {
            x -= NN;
            x = (x >> 8) + (x & NN);
        }
        return x;
    }
}
