// FskId.cs - the "FSK ID" that MMSSTV and QSSTV send after an analog SSTV picture: the
// sender's callsign as a short warble, which they show as who sent the picture.
//
// The sound (as MMSSTV and QSSTV make it):
//   300 ms of 1500 Hz, then 100 ms of 2100 Hz and 22 ms of 1900 Hz (together the start code);
//   then 6-bit symbols, 22 ms a bit, lowest bit first, 1900 Hz for a 1 and 2100 Hz for a 0:
//     0x2A (the header), each character of the callsign (its ASCII code less 0x20),
//     0x01 (the end), and a check symbol: all the characters' symbols XORed together;
//   then 100 ms of 1900 Hz.
// The receiver (FskIdDecoder) waits for the 1500 and 2100 Hz start, times every bit from the
// first move to 1900 Hz, and only believes a callsign whose check symbol matches.

namespace RFpix.Analog;

public static class FskId
{
    public const double BitMs = 22, LeadHz = 1500, OneHz = 1900, ZeroHz = 2100;
    public const int Header = 0x2A, EndMark = 0x01, MaxLength = 9;

    /// <summary>The callsign as it's sent: capitals, only the characters FSK ID can carry, at most 9.</summary>
    public static string Clean(string? callsign) =>
        new string((callsign ?? "").Trim().ToUpperInvariant().Where(c => c is > ' ' and <= '_').Take(MaxLength).ToArray());

    /// <summary>The 6-bit symbols after the start code: header, characters, end, check.</summary>
    public static int[] Symbols(string callsign)
    {
        var chars = Clean(callsign).Select(c => (c - 0x20) & 0x3F).ToList();
        int check = chars.Aggregate(0, (x, c) => x ^ c) & 0x3F;
        return new[] { Header }.Concat(chars).Append(EndMark).Append(check).ToArray();
    }

    /// <summary>The tones (Hz, ms) of the FSK ID for a callsign; none if it's empty.</summary>
    public static IReadOnlyList<(double Hz, double Ms)> Tones(string? callsign)
    {
        string call = Clean(callsign);
        var tones = new List<(double, double)>();
        if (call.Length == 0) return tones;
        tones.Add((LeadHz, 300));
        tones.Add((ZeroHz, 100));
        tones.Add((OneHz, BitMs));
        foreach (int symbol in Symbols(call))
            for (int bit = 0; bit < 6; bit++)
                tones.Add((((symbol >> bit) & 1) == 1 ? OneHz : ZeroHz, BitMs));
        tones.Add((OneHz, 100));
        return tones;
    }

    public static TimeSpan Duration(string? callsign) => TimeSpan.FromMilliseconds(Tones(callsign).Sum(t => t.Ms));
}

/// <summary>Listens to a frequency track (Hz per sample, 0 for silence) for an FSK ID.</summary>
public sealed class FskIdDecoder
{
    private readonly double _rate;
    private readonly double _bit;           // samples per bit
    private readonly float[] _ring;         // the last 2 ms, to smooth the frequency
    private int _ringAt, _ringFilled;
    private double _ringSum;

    private enum State { Lead, Start, Bits }
    private State _state = State.Lead;
    private long _n;                        // samples so far
    private long _run;                      // samples in a row near the expected tone
    private long _leadStart, _leadEnd = -1000;   // the stretch of 1500 Hz so far
    private long _bitsFrom;                 // where the first bit (the start code's 1900 Hz) began
    private int _bitIndex;
    private double _bitSum;
    private int _bitCount;
    private readonly List<int> _symbols = new();
    private int _symbol, _symbolBits;

    /// <param name="rate">Track samples per second.</param>
    public FskIdDecoder(double rate)
    {
        _rate = rate;
        _bit = FskId.BitMs * rate / 1000;
        _ring = new float[Math.Max(1, (int)(rate / 500))];   // 2 ms
    }

    /// <summary>A callsign has been read.</summary>
    public event Action<string>? Received;

    public void Add(float hz)
    {
        long n = _n++;
        // A 2 ms average, so the decisions about where tones start aren't upset by noise.
        _ringSum += hz - _ring[_ringAt];
        _ring[_ringAt] = hz;
        _ringAt = (_ringAt + 1) % _ring.Length;
        if (_ringFilled < _ring.Length) _ringFilled++;
        double smooth = _ringSum / _ringFilled;

        switch (_state)
        {
            case State.Lead:   // at least 100 ms of 1500 Hz, then (within a few ms) 2100 Hz
                if (Near(smooth, FskId.LeadHz))
                {
                    if (n - _leadEnd > 0.003 * _rate) _leadStart = n;   // a new stretch of 1500 Hz (short dips are noise)
                    _run = n - _leadStart + 1;
                    _leadEnd = n;
                }
                else if (Near(smooth, FskId.ZeroHz) && _run >= 0.1 * _rate && n - _leadEnd < 0.005 * _rate)
                {
                    _state = State.Start;
                    _run = 1;
                }
                break;
            case State.Start:  // at least 70 ms of 2100 Hz, then the move to 1900 Hz starts the bits
                if (Near(smooth, FskId.ZeroHz)) _run++;
                else if (_run >= 0.07 * _rate && smooth > 0 && smooth < (FskId.OneHz + FskId.ZeroHz) / 2)
                {
                    // The average lags behind the sound by half its length.
                    _bitsFrom = n - _ring.Length / 2;
                    _state = State.Bits;
                    _bitIndex = 0; _bitSum = 0; _bitCount = 0;
                    _symbols.Clear(); _symbol = 0; _symbolBits = 0;
                }
                else if (!Near(smooth, FskId.ZeroHz, 250)) Reset();
                break;
            case State.Bits:
                if (hz <= 0) { Reset(); break; }   // the signal has gone
                double into = n - _bitsFrom - _bitIndex * _bit;
                if (into >= 0.25 * _bit && into < 0.75 * _bit) { _bitSum += hz; _bitCount++; }   // the bit's middle
                if (into >= _bit) EndBit();
                break;
        }
    }

    private void EndBit()
    {
        bool one = _bitCount > 0 && _bitSum / _bitCount < (FskId.OneHz + FskId.ZeroHz) / 2;
        int index = _bitIndex++;
        _bitSum = 0; _bitCount = 0;
        if (index == 0)   // the start code's last bit: must be 1900 Hz
        {
            if (!one) Reset();
            return;
        }
        if (one) _symbol |= 1 << _symbolBits;
        if (++_symbolBits < 6) return;
        int symbol = _symbol;
        _symbol = 0; _symbolBits = 0;
        _symbols.Add(symbol);
        if (_symbols.Count == 1 && symbol != FskId.Header) { Reset(); return; }
        int end = _symbols.IndexOf(FskId.EndMark, 1);
        if (end < 0)
        {
            if (_symbols.Count > FskId.MaxLength + 1) Reset();   // too long for a callsign
            return;
        }
        if (_symbols.Count < end + 2) return;                    // waiting for the check symbol
        var chars = _symbols.GetRange(1, end - 1);
        int check = chars.Aggregate(0, (x, c) => x ^ c) & 0x3F;
        if (chars.Count > 0 && check == _symbols[end + 1])
            Received?.Invoke(new string(chars.Select(c => (char)(c + 0x20)).ToArray()));
        Reset();
    }

    private void Reset()
    {
        _state = State.Lead;
        _run = 0;
        _leadEnd = -1000;
    }

    private static bool Near(double hz, double target, double within = 80) => Math.Abs(hz - target) <= within;
}
