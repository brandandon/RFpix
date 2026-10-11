// ConvolutionalCode.cs - HamDRM's error-correcting code, and its Viterbi decoder.
//
// Encoding: the sender feeds the bits through a 7-bit shift register. For every
// bit going in, up to four "check" bits come out, each the parity (odd or even)
// of a different selection of the register's 7 bits. Each output bit therefore
// depends on 7 input bits, so damage to a few output bits can be repaired.
// Sending all four outputs would quadruple the data, so "puncturing" sends only
// some of them in a fixed repeating pattern; how many sets the code's strength.
// Six zero "tail" bits at the end empty the register, so it finishes in a known state.
//
// Decoding (Viterbi): the decoder tracks every possible register state (64)
// at once. At each step it scores both possible next bits against what was
// received, keeps only the best route into each state, and at the end traces
// the best route back. That route is the most likely original message.
//
// From QSSTV's ConvEncoder.cpp / ChannelCode.cpp and the DRM standard.

namespace RFpix.HamDrm.Coding;

public static class ConvolutionalCode
{
    /// <summary>Register length: each output depends on this many input bits.</summary>
    public const int ConstraintLength = 7;

    /// <summary>The tail bits added to empty the register.</summary>
    public const int TailBits = ConstraintLength - 1;

    private const int States = 1 << (ConstraintLength - 1);   // 64

    /// <summary>Which register bits each output takes the parity of (DRM's 133, 171, 145, 133
    /// in octal, written bit-reversed because the register shifts left).</summary>
    private static readonly int[] Generators = { 0x6D /*0155*/, 0x4F /*0117*/, 0x53 /*0123*/, 0x6D /*0155*/ };

    // ---- puncturing patterns ----
    // Each step of a pattern lists which of the four outputs are sent for one input bit.
    private static readonly int[] P1111 = { 0, 1, 2, 3 }, P0111 = { 0, 1, 2 }, P0011 = { 0, 1 },
                                  P0001 = { 0 }, P0101 = { 0, 2 };

    /// <summary>The DRM standard's 13 puncturing patterns (QSSTV's iPuncturingPatterns).
    /// Pattern 6 (3 bits in, 5 out: code rate 3/5) protects the FAC.</summary>
    public static readonly int[][][] Patterns =
    {
        new[] { P1111 },
        new[] { P1111, P0111, P0111 },
        new[] { P0111 },
        new[] { P0111, P0111, P0111, P0011 },
        new[] { P0011 },
        new[] { P0011, P0101, P0011, P0001 },
        new[] { P0011, P0001, P0011 },
        new[] { P0011, P0001 },
        new[] { P0011, P0001, P0001, P0011, P0001, P0001, P0011, P0001 },
        new[] { P0011, P0001, P0001 },
        new[] { P0011, P0001, P0001, P0001 },
        new[] { P0011, P0001, P0001, P0001, P0001, P0001, P0001 },
        new[] { P0011, P0001, P0001, P0001, P0001, P0001, P0001, P0001 },
    };

    /// <summary>The 12 patterns for the 6 tail bits of picture data (QSSTV's iPunctPatTailbits).
    /// Which one is used is chosen so the coded block fills its cells exactly.</summary>
    public static readonly int[][][] TailPatterns =
    {
        new[] { P0011, P0011, P0011, P0011, P0011, P0011 },
        new[] { P0111, P0011, P0011, P0011, P0011, P0011 },
        new[] { P0111, P0011, P0011, P0111, P0011, P0011 },
        new[] { P0111, P0111, P0011, P0111, P0011, P0011 },
        new[] { P0111, P0111, P0011, P0111, P0111, P0011 },
        new[] { P0111, P0111, P0111, P0111, P0111, P0011 },
        new[] { P0111, P0111, P0111, P0111, P0111, P0111 },
        new[] { P1111, P0111, P0111, P0111, P0111, P0111 },
        new[] { P1111, P0111, P0111, P1111, P0111, P0111 },
        new[] { P1111, P1111, P0111, P1111, P0111, P0111 },
        new[] { P1111, P1111, P0111, P1111, P0111, P1111 },
        new[] { P1111, P1111, P1111, P1111, P0111, P1111 },
    };

    /// <summary>The steps for one level of a frame's picture data, which must fill exactly
    /// 2 x <paramref name="cells"/> coded bits. Also says how many data bits that carries.
    /// (DRM standard 7.3.1: M = RX x floor((2N - 12) / RY), and the leftover picks the tail pattern.)</summary>
    public static int[][] FillCells(int cells, int patternIndex, out int dataBits)
    {
        var pattern = Patterns[patternIndex];
        int inPerPattern = pattern.Length;                       // RX: bits in per repeat
        int outPerPattern = pattern.Sum(step => step.Length);    // RY: bits out per repeat
        int repeats = (2 * cells - 12) / outPerPattern;
        dataBits = inPerPattern * repeats;
        int leftover = 2 * cells - 12 - outPerPattern * repeats;

        var steps = new int[dataBits + TailBits][];
        for (int i = 0; i < dataBits; i++) steps[i] = pattern[i % inPerPattern];
        for (int i = 0; i < TailBits; i++) steps[dataBits + i] = TailPatterns[leftover][i];
        return steps;
    }

    /// <summary>The outputs to send at each step, repeating one pattern for the whole
    /// message including its tail (how the FAC does it).</summary>
    public static int[][] RepeatPattern(int[][] pattern, int dataBits)
    {
        var steps = new int[dataBits + TailBits][];
        for (int i = 0; i < steps.Length; i++) steps[i] = pattern[i % pattern.Length];
        return steps;
    }

    /// <summary>How many coded bits a set of steps produces.</summary>
    public static int CodedLength(int[][] steps) => steps.Sum(s => s.Length);

    /// <summary>Encodes bits (0 or 1). <paramref name="steps"/> must have one entry per
    /// data bit plus <see cref="TailBits"/> more.</summary>
    public static byte[] Encode(IReadOnlyList<byte> bits, int[][] steps)
    {
        var output = new List<byte>(CodedLength(steps));
        int register = 0;
        for (int i = 0; i < steps.Length; i++)
        {
            int bit = i < bits.Count ? bits[i] & 1 : 0;   // tail bits are zeros
            register = ((register << 1) | bit) & 0x7F;
            foreach (int g in steps[i]) output.Add(Parity(register & Generators[g]));
        }
        return output.ToArray();
    }

    /// <summary>Finds the most likely original bits.</summary>
    /// <param name="soft">One number per coded bit: positive means "probably 0", negative
    /// "probably 1", and the size says how sure (0 = no idea).</param>
    /// <param name="steps">The same steps the sender used.</param>
    /// <param name="dataBits">How many data bits to return (the tail is dropped).</param>
    public static byte[] Decode(IReadOnlyList<double> soft, int[][] steps, int dataBits)
    {
        // score[state]: how well the best route into this state matches what was received.
        var score = new double[States];
        var next = new double[States];
        Array.Fill(score, double.NegativeInfinity);
        score[0] = 0;   // the register starts empty
        var cameFrom = new byte[steps.Length, States];   // previous state * 2 + bit, per step

        int pos = 0;
        for (int i = 0; i < steps.Length; i++)
        {
            Array.Fill(next, double.NegativeInfinity);
            var outputs = steps[i];
            bool tail = i >= dataBits;
            for (int state = 0; state < States; state++)
            {
                if (double.IsNegativeInfinity(score[state])) continue;
                for (int bit = 0; bit <= (tail ? 0 : 1); bit++)
                {
                    int register = (state << 1) | bit;           // all 7 bits
                    double s = score[state];
                    for (int j = 0; j < outputs.Length; j++)
                    {
                        double r = soft[pos + j];
                        s += Parity(register & Generators[outputs[j]]) == 0 ? r : -r;
                    }
                    int to = register & (States - 1);
                    if (s > next[to])
                    {
                        next[to] = s;
                        cameFrom[i, to] = (byte)((state << 1) | bit);
                    }
                }
            }
            pos += outputs.Length;
            (score, next) = (next, score);
        }

        // The tail brought the register back to state 0: trace the best route back from there.
        var bits = new byte[steps.Length];
        int at = 0;
        for (int i = steps.Length - 1; i >= 0; i--)
        {
            int from = cameFrom[i, at];
            bits[i] = (byte)(from & 1);
            at = from >> 1;
        }
        return bits[..dataBits];
    }

    private static byte Parity(int x) => (byte)(System.Numerics.BitOperations.PopCount((uint)x) & 1);
}
