// HamDrmTransmitter.cs - turns a file into HamDRM audio, ready for the radio.
//
// The receiver's steps, in reverse:
//   1. MotSender cuts the file into packets, one per frame (400 ms).
//   2. MscEncoder codes each frame's packet into data cells.
//   3. TimeInterleaver shuffles the cells (over 5 frames for long interleaving).
//   4. FacEncoder makes the frame's 45 settings cells, with the callsign.
//   5. Every symbol of every frame: data, settings and pilot cells go on their carriers,
//      and the carriers are added up into sound (an "inverse Fourier transform"), with a
//      guard copied in front. The carriers land at 350 Hz + k x spacing.
// The sound level is QSSTV's: about -15 dBFS on average, peaks near -2 dBFS.
//
// Ported from QSSTV's transmitter (Dream DRM, PA0MBO, ON4QZ). Tests check it against
// QSSTV's own transmitter: the same packets, the same cells and the same audio.

using System.Numerics;

namespace RFpix.HamDrm;

/// <summary>How a transmission is sent: everything the FAC announces.</summary>
public sealed record TransmitSettings(
    HamDrmMode Mode, HamDrmBandwidth Bandwidth, int Qam, HamDrmInterleave Interleave, HamDrmProtection Protection)
{
    /// <summary>For the screen, e.g. "Mode B · 2.5 kHz · 16-QAM · long interleave · high protection".</summary>
    public string Description =>
        $"Mode {Mode}  ·  {(Bandwidth == HamDrmBandwidth.Wide ? "2.5" : "2.2")} kHz  ·  {Qam}-QAM  ·  " +
        $"{Interleave.ToString().ToLowerInvariant()} interleave  ·  {Protection.ToString().ToLowerInvariant()} protection";
}

public sealed class HamDrmTransmitter
{
    public const int SampleRate = 48000;
    public const double FrameSeconds = 0.4;

    /// <summary>QSSTV's output scale: a sample is the signal times 1000, as a 16-bit number.</summary>
    private const double Scale = 1000;

    private readonly TransmitSettings _settings;
    private readonly string _callsign;
    private readonly CellLayout _layout;
    private readonly MscEncoder _msc;
    private readonly List<byte[]> _packets;

    /// <summary>How many frames go out.</summary>
    public int Frames { get; }

    /// <summary>How long the transmission lasts.</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds(Frames * FrameSeconds);

    /// <summary>The file's number on the air (from its name).</summary>
    public int TransportId { get; }

    /// <summary>For the tests: the reserved FAC bits for each frame. QSSTV sends 18 there in every
    /// other frame (a leftover in its buffers); RFpix sends 0, as the standard says.</summary>
    internal Func<int, int>? ReservedFacBits { get; init; }

    /// <summary>Prepares a transmission. Nothing is generated until <see cref="Audio"/>.</summary>
    /// <param name="name">The file name the receiver saves it as (e.g. "test04.jpg").</param>
    /// <param name="callsign">The sender's callsign, shown by the receiver (up to 9 characters).</param>
    public HamDrmTransmitter(string name, byte[] data, TransmitSettings settings, string callsign)
    {
        _settings = settings;
        _callsign = callsign.Trim().ToUpperInvariant();
        if (_callsign.Length > FacEncoder.MaxLabelLength)
            throw new ArgumentException($"HamDRM carries at most {FacEncoder.MaxLabelLength} callsign characters.");
        _layout = CellLayout.For(settings.Mode, settings.Bandwidth);
        _msc = new MscEncoder(_layout.DataCellsPerFrame, settings.Qam, settings.Protection);
        var mot = new MotSender(name, data, _msc.DataBits / 8);
        TransportId = mot.TransportId;
        _packets = mot.Packets(_msc.DataBits / 8);
        // QSSTV makes one more packet than it sends: it stops right after making it.
        Frames = _packets.Count - 1;
    }

    /// <summary>The frame's bits: its packet, then zeros up to what the frame holds.</summary>
    public byte[] FrameBits(int frame)
    {
        var bits = new byte[_msc.DataBits];
        var packet = _packets[frame];
        for (int i = 0; i < 8 * packet.Length; i++) bits[i] = (byte)((packet[i / 8] >> (7 - i % 8)) & 1);
        return bits;
    }

    /// <summary>Makes the whole transmission's audio: 48 kHz, mono, -1 to 1.</summary>
    public float[] Audio()
    {
        var mode = _settings.Mode;
        int n = HamDrmModes.UsefulLength(mode) * 4;   // samples in a symbol's useful part at 48 kHz
        int guard = HamDrmModes.GuardLength(mode) * 4;
        int symbolLength = n + guard;
        int dc = n / 8;                               // the bin at 6000 Hz, where carrier 0 starts
        int symbols = _layout.SymbolsPerFrame;
        var audio = new float[Frames * symbols * symbolLength];

        // One turn around the circle in n steps, for adding up the carriers.
        var turn = new Complex[n];
        for (int i = 0; i < n; i++) turn[i] = Complex.FromPolarCoordinates(1, 2 * Math.PI * i / n);
        // The move from 6000 Hz down to 350 Hz: 5650 Hz is 113/960 of the sample rate.
        var shift = new Complex[960];
        for (int i = 0; i < 960; i++) shift[i] = Complex.FromPolarCoordinates(1, -2 * Math.PI * i / 960);

        var interleaver = new TimeInterleaver(_layout.DataCellsPerFrame, _settings.Interleave);
        int first = _layout.FirstCarrier, last = _layout.LastCarrier;
        var carriers = new Complex[symbols][];
        var x = new Complex[n];
        long t = 0;   // samples so far

        for (int frame = 0; frame < Frames; frame++)
        {
            // This frame's cells, carrier by carrier.
            var data = interleaver.Add(_msc.Encode(FrameBits(frame)));
            var fac = FacEncoder.Encode(frame % 3, _settings, _callsign, ReservedFacBits?.Invoke(frame) ?? 0);
            for (int s = 0; s < symbols; s++)
            {
                carriers[s] = new Complex[last + 1];
                for (int k = first; k <= last; k++) carriers[s][k] = _layout.PilotValue(s, k);   // zero if not a pilot
            }
            for (int i = 0; i < data.Length; i++)
            {
                var (s, k) = _layout.DataCells[i];
                carriers[s][k] = data[i];
            }
            for (int i = 0; i < fac.Length; i++)
            {
                var (s, k) = _layout.FacCells[i];
                carriers[s][k] = fac[i];
            }

            // Each symbol: add the carriers up, copy the guard in front, move down to 350 Hz.
            for (int s = 0; s < symbols; s++)
            {
                Array.Clear(x);
                for (int k = first; k <= last; k++)
                {
                    var c = carriers[s][k];
                    if (c == Complex.Zero) continue;
                    int bin = dc + k;
                    for (int i = 0, p = 0; i < n; i++, p = (p + bin) % n) x[i] += c * turn[p];
                }
                for (int i = 0; i < symbolLength; i++, t++)
                {
                    var value = x[(i - guard + n) % n] * shift[(int)(113 * t % 960)];
                    audio[t] = (float)(Math.Clamp((int)(value.Real * Scale), short.MinValue, short.MaxValue) / 32768.0);
                }
            }
        }
        return audio;
    }
}
