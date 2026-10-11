// HamDrmReceiver.cs - the HamDRM decoder, fed with audio from the audio layer.
//
// So far (milestone 1, decoder stages 1 to 4) it finds a HamDRM signal, names
// its mode, measures how far off frequency it is, reads every carrier in every
// symbol, finds where each 400 ms frame starts, measures the difference between
// the two sound cards' clocks, and reads each frame's FAC: the transmission's
// settings and the sender's callsign. Later stages read the picture data.
//
//   audio (48 kHz) -> Downconverter (12 kHz complex) -> ModeDetector -> FrequencyLock
//                  -> OfdmSymbols -> FrameSync + SampleClock
//                  -> ChannelEstimator -> Fac -> Status

using System.Numerics;
using RFpix.Dsp;

namespace RFpix.HamDrm;

public sealed class HamDrmReceiver
{
    private Downconverter? _downconverter;
    private readonly ModeDetector _detector = new();
    private readonly List<Complex> _converted = new();
    private int _sampleRate;

    public HamDrmReceiver()
    {
        // After every mode decision, work through the same stretch of sound.
        // (Subscribed here, first, so it's all ready before anyone else hears about the decision.)
        _detector.Decided += OnDecided;
    }

    private void OnDecided(ModeDetection detection)
    {
        Lock = FrequencyLock.Measure(_detector.Window, detection);
        if (detection.Mode is not { } mode || Lock is not { IsLocked: true } locked)
        {
            Symbols = null;
            Frame = null;
            FrameStartTime = null;
            if (detection.Mode is null)
            {
                // Signal gone: start afresh next time.
                _clock.Reset();
                _recentFrameStarts.Clear();
                ForgetFac();
            }
            return;
        }

        Symbols = OfdmSymbols.Read(_detector.Window, mode, detection.SymbolStart, locked.OffsetHz);

        Frame = FrameSync.Find(Symbols);
        FrameStartTime = null;
        if (Frame is { IsFound: true } frame && _downconverter is not null)
        {
            // Turn "sample N of this window" into "seconds since the audio began".
            long windowStart = _detector.SamplesSeen - ModeDetector.WindowLength;
            int step = _downconverter.InputRate / Downconverter.OutputRate;
            double start = (windowStart + frame.FrameStart) / (double)Downconverter.OutputRate
                         + (step - 1.0) / _downconverter.InputRate - _downconverter.Delay;

            // Frames come like clockwork, every 0.4 s. In heavy noise the frame finder
            // occasionally picks the wrong symbol, so only trust a frame start that
            // agrees with most of the recent ones.
            _recentFrameStarts.Enqueue(start);
            while (_recentFrameStarts.Count > FrameVotes) _recentFrameStarts.Dequeue();
            int agreeing = _recentFrameStarts.Count(t => FrameTimingAgrees(t, start));
            if (agreeing >= FrameVotesNeeded) FrameStartTime = start;

            // Read the FAC of each whole frame in this window that hasn't been read yet.
            // (The FAC's own checksum confirms the frame start, so no vote is needed.)
            ReadFacs(mode, frame, start);
        }

        // Frame starts, followed over several seconds, reveal the clock difference.
        if (FrameStartTime is double confirmed) _clock.AddFrameStart(confirmed);
    }

    // ---- the FAC ----

    private double? _lastFacTime;                          // when the last frame read started
    private HamDrmBandwidth _bandwidth = HamDrmBandwidth.Wide;   // best guess until a FAC says
    // Votes for each third of the callsign: how often each version has been heard.
    private readonly Dictionary<string, int>[] _labelVotes = { new(), new(), new() };

    private void ReadFacs(HamDrmMode mode, FrameSyncResult frame, double firstFrameTime)
    {
        var symbols = Symbols!;
        int perFrame = HamDrmModes.SymbolsPerFrame(mode);
        double symbolSeconds = HamDrmModes.SymbolLength(mode) / (double)Downconverter.OutputRate;
        // Wait until a few symbols after the frame are in the window too, so the channel
        // estimate for its last symbols has pilots on both sides.
        int after = CellLayout.For(mode, _bandwidth).PilotSymbolSpacing - 1;

        // Channel estimates for this window, worked out once per bandwidth when first needed.
        var channels = new Dictionary<HamDrmBandwidth, Complex[][]>();
        for (int f = 0; ; f++)
        {
            int s0 = frame.FirstSymbol + f * perFrame;
            if (s0 + perFrame + after > symbols.Count) break;
            double time = firstFrameTime + f * perFrame * symbolSeconds;
            if (_lastFacTime is double last && time < last + 0.2) continue;   // already read
            _lastFacTime = time;

            // Try the bandwidth the last FAC announced first, then the other one.
            // (They differ only in the pilots at the top edge.)
            FacInfo? info = null;
            foreach (var bandwidth in new[] { _bandwidth, Other(_bandwidth) })
            {
                var layout = CellLayout.For(mode, bandwidth);
                if (!channels.TryGetValue(bandwidth, out var channel))
                    channels[bandwidth] = channel = ChannelEstimator.Estimate(symbols, frame.FirstSymbol, layout);
                var cells = layout.FacCells.Select(c => symbols.Cells[s0 + c.Symbol][c.Carrier]).ToArray();
                var h = layout.FacCells.Select(c => channel[s0 + c.Symbol][c.Carrier]).ToArray();
                info = FacDecoder.Decode(cells, h);
                if (info is not null) break;
            }

            if (info is null) { FacFailed++; continue; }
            Check(info, time);
        }
    }

    // About 1 damaged FAC in 256 passes its checksum by chance, so a FAC is only trusted
    // when it agrees with the last trusted one: same settings, and the right frame number
    // for how much time has passed (frames go 0, 1, 2, 0, ... every 0.4 s). A FAC that
    // doesn't agree waits as a "candidate"; if the next one agrees with it, both are
    // trusted (that's how a new transmission with new settings is picked up).
    private (FacInfo Info, double Time)? _trusted, _candidate;

    private void Check(FacInfo info, double time)
    {
        if (_trusted is { } t && Agrees(t, info, time))
        {
            Trust(info, time);
        }
        else if (_candidate is { } c && Agrees(c, info, time))
        {
            Trust(c.Info, c.Time);
            Trust(info, time);
        }
        else
        {
            if (_candidate is not null) FacFailed++;   // the old candidate never agreed with anything
            _candidate = (info, time);
        }
    }

    private static bool Agrees((FacInfo Info, double Time) earlier, FacInfo info, double time)
    {
        int framesLater = (int)Math.Round((time - earlier.Time) / SampleClock.FramePeriod);
        int expected = ((earlier.Info.FrameInSuperframe + framesLater) % 3 + 3) % 3;
        return info.FrameInSuperframe == expected && info.SameSettings(earlier.Info);
    }

    private void Trust(FacInfo info, double time)
    {
        FacGood++;
        Fac = info;
        _trusted = (info, time);
        _candidate = null;
        _bandwidth = info.Bandwidth;

        // The callsign letters have less protection than the settings (a damaged letter can
        // slip past the checksum while the settings are right), so each third of the
        // callsign must arrive the same way twice before it's shown.
        var votes = _labelVotes[info.FrameInSuperframe];
        votes[info.LabelPart] = votes.GetValueOrDefault(info.LabelPart) + 1;
        var parts = _labelVotes.Select(v => v.Where(p => p.Value >= 2).OrderByDescending(p => p.Value)
                                              .Select(p => p.Key).FirstOrDefault()).ToArray();
        if (parts.All(part => part is not null)) Callsign = string.Concat(parts).Trim();
    }

    private static HamDrmBandwidth Other(HamDrmBandwidth b) =>
        b == HamDrmBandwidth.Wide ? HamDrmBandwidth.Narrow : HamDrmBandwidth.Wide;

    private void ForgetFac()
    {
        Fac = null;
        Callsign = null;
        FacGood = FacFailed = 0;
        _lastFacTime = null;
        _trusted = _candidate = null;
        _bandwidth = HamDrmBandwidth.Wide;
        foreach (var votes in _labelVotes) votes.Clear();
    }

    /// <summary>The settings from the latest trusted FAC (passed its checksum and agreed
    /// with the FACs around it), or null.</summary>
    public FacInfo? Fac { get; private set; }

    /// <summary>The sender's callsign, once each third of it has arrived the same way twice
    /// (2.4 s on a clean signal); null until then. Empty if the sender didn't set one.</summary>
    public string? Callsign { get; private set; }

    /// <summary>How many frames' FACs were trusted, or failed (checksum, or didn't agree with
    /// the FACs around them), since the signal appeared.</summary>
    public int FacGood { get; private set; }
    public int FacFailed { get; private set; }

    /// <summary>A second line for the screen: the transmission's settings and callsign,
    /// e.g. "2.5 kHz · 16-QAM · long interleave · high protection  ·  from RFPIX  ·  FAC 25/25 ok".</summary>
    public string TransmissionText
    {
        get
        {
            if (Problem is not null || Detection.Mode is null) return "";
            var fac = Fac;
            int good = FacGood, total = FacGood + FacFailed;
            if (fac is null)
                return total == 0 ? "Reading the transmission's settings…"
                                  : $"Reading the transmission's settings…  (FAC 0/{total} ok)";
            string from = Callsign switch { null => "…", "" => "(no callsign)", var c => c };
            return $"{fac.Description}  ·  from {from}  ·  FAC {good}/{total} ok";
        }
    }

    private readonly SampleClock _clock = new();

    // A frame start is trusted when at least 3 of the last 5 readings (itself included) agree.
    private const int FrameVotes = 5;
    private const int FrameVotesNeeded = 3;
    private readonly Queue<double> _recentFrameStarts = new();

    /// <summary>Do two frame starts fit the same 0.4 s rhythm, to within 2 ms?</summary>
    private static bool FrameTimingAgrees(double a, double b)
    {
        double apart = Math.IEEERemainder(a - b, SampleClock.FramePeriod);   // -0.2 to +0.2 s
        return Math.Abs(apart) < 0.002;
    }

    /// <summary>What the receiver hears right now.</summary>
    public ModeDetection Detection => _detector.Current;

    /// <summary>The latest tuning-error measurement, or null when there's no signal.</summary>
    public FrequencyLockResult? Lock { get; private set; }

    /// <summary>Every carrier in every symbol of the latest window, or null when not locked.</summary>
    public OfdmSymbols? Symbols { get; private set; }

    /// <summary>Where frames start in the latest window, or null when not locked.</summary>
    public FrameSyncResult? Frame { get; private set; }

    /// <summary>When the latest frame started, in seconds since the audio began
    /// (allowing for the downconverter's small delay). Null until found and confirmed
    /// by most of the recent readings (so it takes about half a second to appear).</summary>
    public double? FrameStartTime { get; private set; }

    /// <summary>The clock difference between the sending and receiving sound cards in ppm
    /// (positive: the receiving card runs fast), from the last several seconds of frames;
    /// null until at least 4 seconds have been heard.</summary>
    public double? ClockPpm
    {
        get
        {
            lock (_lock) return _clock.Ppm;   // the audio thread may be adding a frame start
        }
    }

    /// <summary>Each mode's latest score, for troubleshooting.</summary>
    public IReadOnlyDictionary<HamDrmMode, double> Scores => _detector.Scores;

    /// <summary>Raised about 5 times a second with the latest decision (on the audio thread).</summary>
    public event Action<ModeDetection>? Decided
    {
        add => _detector.Decided += value;
        remove => _detector.Decided -= value;
    }

    /// <summary>Set when the audio can't be decoded at all (e.g. an unusual sample rate).</summary>
    public string? Problem { get; private set; }

    /// <summary>A one-line description for the screen,
    /// e.g. "HamDRM mode B  ·  offset +2.5 Hz  ·  clock -40 ppm  ·  strength 96%".</summary>
    public string StatusText
    {
        get
        {
            if (Problem is not null) return Problem;
            var detection = Detection;
            if (detection.Mode is not { } mode) return "No HamDRM signal";

            var lockResult = Lock;
            string offset = lockResult is { IsLocked: true }
                ? $"offset {lockResult.OffsetHz:+0.0;-0.0;0.0} Hz"
                : "finding frequency…";
            string clock = ClockPpm is double ppm ? $"  ·  clock {ppm:+0;-0;0} ppm" : "";
            return $"HamDRM mode {mode}  ·  {offset}{clock}  ·  strength {detection.Strength:P0}";
        }
    }

    /// <summary>Feed in audio exactly as it comes from the audio layer.</summary>
    public void AddSamples(float[] samples, int count, int sampleRate)
    {
        // Audio arrives on the audio thread, but Reset comes from the screen's
        // thread. The lock lets only one of them in at a time.
        lock (_lock)
        {
            if (sampleRate != _sampleRate) Restart(sampleRate);
            if (_downconverter is null) return;

            _converted.Clear();
            _downconverter.Process(samples.AsSpan(0, count), _converted);
            _detector.AddSamples(_converted);
        }
    }

    /// <summary>Forget everything heard so far, e.g. when switching between the radio and a recording.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _sampleRate = 0;
            _downconverter = null;
            _detector.Reset();
            Lock = null;
            Symbols = null;
            Frame = null;
            FrameStartTime = null;
            _clock.Reset();
            _recentFrameStarts.Clear();
            ForgetFac();
            Problem = null;
        }
    }

    private readonly object _lock = new();

    private void Restart(int sampleRate)
    {
        Reset();
        _sampleRate = sampleRate;
        if (Downconverter.Supports(sampleRate))
            _downconverter = new Downconverter(sampleRate);
        else
            Problem = $"HamDRM decoding needs 48000 Hz sound; this source is {sampleRate} Hz. " +
                      "Set the radio's sound device to 48000 Hz in Windows Sound settings.";
    }
}
