// FmSignalLayout.cs - the shape of RFpix's FM signal (new in RFpix 0.7.1).
//
// Made for FM radios' own audio (300 to 3000 Hz, through the microphone and speaker connections, not
// the data port), from what the bench measured (docs/BENCH_BASELINE.md, "How much FM can carry"):
//   - 115 tones side by side, one every 23.4 Hz from 305 to 2977 Hz (OFDM, like HamDRM but with closer
//     tones: an FM path doesn't fade, so slower symbols lose nothing).
//   - Every 12th tone is a known "pilot", to follow the sound cards' clocks drifting apart.
//   - The middle of the band (609-2484 Hz) arrives cleanest, so it carries more bits per tone than the
//     edges.
//   - Modern error correction (Ldpc.cs), with the bits of each block spread over a whole frame, so a
//     burst of FM crackle only dents each block.
//   - Frames of about 3 seconds, each starting with a known pair of symbols to find it by, and a header
//     saying the speed. Each frame stands alone: a station that tunes in late, or loses the repeater for
//     a few seconds, picks up at the next frame.
//   - The data is the file cut into fountain-code pieces (FountainFile.cs): any K of them rebuild it.

using System.Numerics;

namespace RFpix.FmSignal;

/// <summary>One speed of the FM signal.</summary>
/// <param name="Id">Its number in the frame header (0-15).</param>
/// <param name="Name">For the bench's tables.</param>
/// <param name="MidBits">Bits per tone in the middle of the band (2 = QPSK, 4 = 16-QAM, 6 = 64-QAM, 8 = 256-QAM).</param>
/// <param name="EdgeBits">Bits per tone at the edges of the band (below 600 Hz and above 2500 Hz).</param>
/// <param name="RateIndex">The error correction: 0 = 1/2 data, 1 = 2/3, 2 = 3/4, 3 = 5/6.</param>
/// <param name="QualityNeededDb">About the signal quality it needs (as the receiver measures it), from the bench.</param>
public sealed record FmSignalMode(int Id, string Name, int MidBits, int EdgeBits, int RateIndex, double QualityNeededDb)
{
    public Ldpc Code => Ldpc.ForRate(RateIndex);

    /// <summary>Bits carried by one data symbol, before error correction.</summary>
    public int BitsPerSymbol => FmSignalLayout.DataCarriers.Sum(k => FmSignalLayout.IsEdge(k) ? EdgeBits : MidBits);

    /// <summary>Error-corrected blocks in each frame.</summary>
    public int BlocksPerFrame => FmSignalLayout.DataSymbols * BitsPerSymbol / Ldpc.BlockBits;

    /// <summary>The size of each fountain piece: a block's data less its label (6 bytes) and check (4 bytes).</summary>
    public int PieceBytes => Code.DataBits / 8 - FmSignalLayout.PieceOverheadBytes;

    /// <summary>The picture data carried, in bits a second.</summary>
    public double BitsPerSecond => BlocksPerFrame * PieceBytes * 8 / FmSignalLayout.FrameSeconds;

    public static IReadOnlyList<FmSignalMode> All { get; } = new FmSignalMode[]
    {
        new(0, "FM 1: QPSK 1/2 (toughest)", 2, 2, 0, 8),
        new(1, "FM 2: 16-QAM 1/2", 4, 2, 0, 10),
        new(2, "FM 3: 16-QAM 2/3", 4, 4, 1, 13),
        new(3, "FM 4: 64-QAM 2/3", 6, 4, 1, 17),
        new(4, "FM 5: 64-QAM 3/4", 6, 6, 2, 19),
        new(5, "FM 6: 256-QAM 3/4 (fastest)", 8, 6, 2, 24),
    };

    /// <summary>The speeds the Send tab offers: FM 1 to FM 5 (FM 6 needed 30 dB on the bench, strong
    /// simplex only; it's still received).</summary>
    public static IReadOnlyList<FmSignalMode> Offered { get; } = All.Take(5).ToList();

    /// <summary>The speed's short name, e.g. "FM 3".</summary>
    public string ShortName => $"FM {Id + 1}";

    /// <summary>For the Gallery and the log, e.g. "RFpix FM 3 (16-QAM 2/3)".</summary>
    public string Description => $"RFpix {ShortName} ({Name[(Name.IndexOf(':') + 2)..].Replace(" (toughest)", "").Replace(" (fastest)", "")})";

    public static FmSignalMode? ById(int id) => All.FirstOrDefault(m => m.Id == id);
}

public static class FmSignalLayout
{
    public const int Rate = 48000;
    public const int FftSize = 2048;                  // tones 23.4375 Hz apart, symbols 42.7 ms long
    public const int Guard = 256;                     // 5.3 ms repeated in front of each symbol
    public const int SymbolLength = FftSize + Guard;  // 48 ms
    public const int FirstCarrier = 13, LastCarrier = 127;   // 305 to 2977 Hz
    private const int PilotSpacing = 12;
    public const int SyncSymbols = 2, HeaderSymbols = 1, DataSymbols = 60;
    public const int FrameSymbols = SyncSymbols + HeaderSymbols + DataSymbols;
    public const int FrameLength = FrameSymbols * SymbolLength;
    public static double FrameSeconds => (double)FrameLength / Rate;
    public const int PieceOverheadBytes = 10;         // file number, pieces needed, piece number (2 bytes each); check (4)
    public const int HeaderBits = 12, HeaderCopies = 8;

    public static IEnumerable<int> Carriers => Enumerable.Range(FirstCarrier, LastCarrier - FirstCarrier + 1);
    public static bool IsPilot(int k) => (k - FirstCarrier) % PilotSpacing == 0 || k == LastCarrier;
    public static bool IsEdge(int k) => k < 26 || k > 106;
    public static int[] PilotCarriers { get; } = Carriers.Where(IsPilot).ToArray();
    public static int[] DataCarriers { get; } = Carriers.Where(k => !IsPilot(k)).ToArray();

    /// <summary>The known values on every tone of the two sync symbols.</summary>
    public static Complex[] Sync { get; } = Known(seed: 9001);
    private static readonly Complex[] PilotBase = Known(seed: 9002);

    /// <summary>A pilot's value: fixed per tone, with its sign changing symbol to symbol (so the pilots
    /// don't make steady tones).</summary>
    public static Complex Pilot(int k, int symbol) => PilotSign(symbol) * PilotBase[k];
    private static int PilotSign(int symbol) => ((0x5A3C96E1u >> (symbol % 32)) & 1) == 0 ? 1 : -1;

    private static Complex[] Known(int seed)
    {
        var random = new Random(seed);
        var values = new Complex[FftSize];
        foreach (int k in Carriers)
            values[k] = Complex.FromPolarCoordinates(1, Math.PI / 4 + Math.PI / 2 * random.Next(4));
        return values;
    }

    /// <summary>The scrambling bits, the same every frame: the data bits are XORed with them so that even
    /// a run of zeros sends a varied, noise-like signal.</summary>
    public static byte[] Scrambler(int count)
    {
        var bits = new byte[count];
        uint state = 0xACE1u;
        for (int i = 0; i < count; i++)
        {
            state ^= state << 13; state ^= state >> 17; state ^= state << 5;
            bits[i] = (byte)(state & 1);
        }
        return bits;
    }

    /// <summary>Where each block's bits go in the frame: a shuffle of all the frame's bit positions, the
    /// same every time for each speed. Block b's bit j goes to position Spread[b * 2880 + j].</summary>
    public static int[] Spread(FmSignalMode mode)
    {
        int total = DataSymbols * mode.BitsPerSymbol;
        var positions = Enumerable.Range(0, total).ToArray();
        var random = new Random(9100 + mode.Id);
        for (int i = total - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (positions[i], positions[j]) = (positions[j], positions[i]);
        }
        return positions;
    }

    /// <summary>For each of the frame's bit positions: which data symbol, which data tone (index into
    /// DataCarriers), and which bit of that tone's QAM point.</summary>
    public static (int Symbol, int Tone, int Bit)[] Positions(FmSignalMode mode)
    {
        var list = new List<(int, int, int)>(DataSymbols * mode.BitsPerSymbol);
        for (int s = 0; s < DataSymbols; s++)
            for (int t = 0; t < DataCarriers.Length; t++)
                for (int b = 0, bits = BitsAt(mode, t); b < bits; b++)
                    list.Add((s, t, b));
        return list.ToArray();
    }

    public static int BitsAt(FmSignalMode mode, int tone) => IsEdge(DataCarriers[tone]) ? mode.EdgeBits : mode.MidBits;

    // ---- the frame's own check on each piece ----

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }
}

/// <summary>Square QAM with Gray coding: neighbouring points differ by one bit.</summary>
public static class Qam
{
    /// <summary>The point for these bits (first half for the real part, second half for the imaginary),
    /// scaled so the average power is 1.</summary>
    public static Complex Map(ReadOnlySpan<byte> bits)
    {
        int half = bits.Length / 2;
        double scale = Scale(bits.Length);
        return new Complex(Level(bits[..half]) * scale, Level(bits[half..]) * scale);
    }

    private static double Scale(int bits)
    {
        int levels = 1 << (bits / 2);
        return 1 / Math.Sqrt(2.0 * (levels * levels - 1) / 3);
    }

    private static int Level(ReadOnlySpan<byte> bits)
    {
        int gray = 0;
        foreach (byte b in bits) gray = gray << 1 | b;
        int index = gray;                       // undo the Gray code
        for (int shift = 1; shift < bits.Length; shift <<= 1) index ^= index >> shift;
        int levels = 1 << bits.Length;
        return 2 * index - (levels - 1);
    }

    /// <summary>The nearest point to <paramref name="z"/>.</summary>
    public static Complex Nearest(Complex z, int bits)
    {
        double scale = Scale(bits);
        int levels = 1 << (bits / 2);
        double Snap(double v)
        {
            int i = (int)Math.Round((v / scale + levels - 1) / 2);
            i = Math.Clamp(i, 0, levels - 1);
            return (2 * i - (levels - 1)) * scale;
        }
        return new Complex(Snap(z.Real), Snap(z.Imaginary));
    }

    /// <summary>Soft guesses for each bit of the point nearest <paramref name="z"/> (positive: probably 0),
    /// given the noise power on z.</summary>
    public static void Soft(Complex z, int bits, double noise, Span<double> output)
    {
        int half = bits / 2;
        double scale = Scale(bits);
        SoftLevel(z.Real / scale, half, noise / (scale * scale), output[..half]);
        SoftLevel(z.Imaginary / scale, half, noise / (scale * scale), output[half..]);
    }

    private static void SoftLevel(double x, int bits, double noise, Span<double> output)
    {
        int levels = 1 << bits;
        for (int b = 0; b < bits; b++)
        {
            double best0 = double.MaxValue, best1 = double.MaxValue;
            for (int index = 0; index < levels; index++)
            {
                int gray = index ^ (index >> 1);
                double d = x - (2 * index - (levels - 1));
                d *= d;
                if (((gray >> (bits - 1 - b)) & 1) == 0) best0 = Math.Min(best0, d); else best1 = Math.Min(best1, d);
            }
            output[b] = (best1 - best0) / noise;
        }
    }
}
