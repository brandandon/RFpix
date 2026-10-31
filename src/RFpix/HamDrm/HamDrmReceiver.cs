// HamDrmReceiver.cs - the HamDRM decoder, fed with audio from the audio layer.
//
// It finds a HamDRM signal, names its mode, measures how far off frequency it is,
// reads every carrier in every symbol, finds where each 400 ms frame starts,
// measures the difference between the two sound cards' clocks, reads each frame's
// FAC (the transmission's settings and the sender's callsign), and decodes the
// picture data into files.
//
//   audio (48 kHz) -> Downconverter (12 kHz complex) -> ModeDetector -> FrequencyLock
//                  -> OfdmSymbols -> FrameSync + SampleClock
//                  -> ChannelEstimator -> FacDecoder            (settings, callsign)
//                                      -> TimeDeinterleaver -> MscDecoder -> MotReceiver (files)

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
                // Signal gone: the transmission is over, so show what it brought.
                TransmissionEnded();
                // Start afresh next time.
                _clock.Reset();
                _recentFrameStarts.Clear();
                _lastFoundFrame = null;
                ForgetFac();
            }
            return;
        }

        Symbols = OfdmSymbols.Read(_detector.Window, mode, detection.SymbolStart, locked.OffsetHz);

        long windowStart = _detector.SamplesSeen - ModeDetector.WindowLength;
        Frame = FrameSync.Find(Symbols);
        FramePredicted = false;
        if (Frame is { IsFound: true } found) _lastFoundFrame = (windowStart + found.FrameStart, mode);
        else if (PredictFrame(Symbols, mode, windowStart) is { } predicted)
        {
            // The frame finder had a lapse (it happens for a look or two in a distorted signal):
            // read the frame where it's due. Its FAC checksum still confirms the reading.
            Frame = predicted;
            FramePredicted = true;
        }
        FrameStartTime = null;
        if (Frame is { IsFound: true } frame && _downconverter is not null)
        {
            // Turn "sample N of this window" into "seconds since the audio began".
            int step = _downconverter.InputRate / Downconverter.OutputRate;
            double start = (windowStart + frame.FrameStart) / (double)Downconverter.OutputRate
                         + (step - 1.0) / _downconverter.InputRate - _downconverter.Delay;

            // Frames come like clockwork, every 0.4 s. In heavy noise the frame finder
            // occasionally picks the wrong symbol, so only trust a frame start that
            // agrees with most of the recent ones.
            // (A predicted frame start isn't a measurement, so it doesn't vote.)
            if (!FramePredicted)
            {
                _recentFrameStarts.Enqueue(start);
                while (_recentFrameStarts.Count > FrameVotes) _recentFrameStarts.Dequeue();
                int agreeing = _recentFrameStarts.Count(t => FrameTimingAgrees(t, start));
                if (agreeing >= FrameVotesNeeded) FrameStartTime = start;
            }

            // Read the FAC of each whole frame in this window that hasn't been read yet.
            // (The FAC's own checksum confirms the frame start, so no vote is needed.)
            ReadFacs(mode, frame, start);
        }

        // Frame starts, followed over several seconds, reveal the clock difference.
        if (FrameStartTime is double confirmed) _clock.AddFrameStart(confirmed);
    }

    // Where the last frame start found was, in 12 kHz samples since the audio began.
    private (long Sample, HamDrmMode Mode)? _lastFoundFrame;

    /// <summary>True when this window's frame start was predicted from earlier ones, not found.</summary>
    public bool FramePredicted { get; private set; }

    /// <summary>How long a frame start can be predicted after the last one found.</summary>
    public const double FramePredictionSeconds = 3;

    /// <summary>Where a frame should start in this window, from the last frame start found
    /// (frames come every 0.4 s exactly), or null if that's too long ago or doesn't line up
    /// with a symbol in this window.</summary>
    private FrameSyncResult? PredictFrame(OfdmSymbols symbols, HamDrmMode mode, long windowStart)
    {
        if (_lastFoundFrame is not { } last || last.Mode != mode || symbols.Count == 0) return null;
        const int frameSamples = (int)(SampleClock.FramePeriod * Downconverter.OutputRate);   // 4800
        long since = windowStart - last.Sample;
        if (since > FramePredictionSeconds * Downconverter.OutputRate) return null;

        // The first frame start at or after the window's first symbol.
        int ts = HamDrmModes.SymbolLength(mode);
        long firstInWindow = last.Sample + (long)Math.Ceiling((double)(windowStart + symbols.Starts[0] - ts / 2 - last.Sample) / frameSamples) * frameSamples;
        int position = (int)(firstInWindow - windowStart);

        // It must fall on one of this window's symbols (within a quarter symbol).
        int nearest = 0;
        for (int s = 1; s < symbols.Count; s++)
            if (Math.Abs(symbols.Starts[s] - position) < Math.Abs(symbols.Starts[nearest] - position)) nearest = s;
        if (Math.Abs(symbols.Starts[nearest] - position) > ts / 4) return null;
        return new FrameSyncResult(nearest, symbols.Starts[nearest], FrameSyncResult.MinimumClarity);
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

            // Keep the frame's cells and channel for the picture data, using the channel
            // estimate made with the bandwidth that worked (or the best guess).
            var usedBandwidth = info?.Bandwidth ?? _bandwidth;
            if (!channels.TryGetValue(usedBandwidth, out var frameChannel))
                channels[usedBandwidth] = frameChannel = ChannelEstimator.Estimate(symbols, frame.FirstSymbol, CellLayout.For(mode, usedBandwidth));
            KeepFrame(mode, time, symbols.Cells[s0..(s0 + perFrame)], frameChannel[s0..(s0 + perFrame)], facRead: info is not null);

            if (info is null) FacFailed++;
            else Check(info, time);
            DecodePictureData();
        }
    }

    // ---- the picture data ----

    // Frames waiting to be decoded (until the settings are known from the FAC).
    private readonly Queue<(long Number, HamDrmMode Mode, Complex[][] Cells, Complex[][] Channel, bool FacRead)> _waitingFrames = new();
    private double? _frameZeroTime;   // when the first frame of this signal started
    private (HamDrmMode, HamDrmBandwidth, int, HamDrmProtection, HamDrmInterleave)? _dataSettings;
    private TimeDeinterleaver? _deinterleaver;
    private MscDecoder? _mscDecoder;
    private MotReceiver _mot = new();

    /// <summary>For diagnostics: raised for every frame kept for picture decoding (number, time, FAC read).</summary>
    public event Action<long, double, bool>? FrameKept;

    private void KeepFrame(HamDrmMode mode, double time, Complex[][] cells, Complex[][] channel, bool facRead)
    {
        _transmissionMode = mode;
        _transmissionFirstFrame ??= time;
        _transmissionLastFrame = time;
        _frameZeroTime ??= time;
        long number = (long)Math.Round((time - _frameZeroTime.Value) / SampleClock.FramePeriod);
        FrameKept?.Invoke(number, time, facRead);
        _waitingFrames.Enqueue((number, mode, cells, channel, facRead));
        while (_waitingFrames.Count > 12) _waitingFrames.Dequeue();   // settings never arrived
    }

    private void DecodePictureData()
    {
        if (Fac is not { } fac) return;   // settings not known yet: frames wait
        while (_waitingFrames.TryDequeue(out var frame))
        {
            var layout = CellLayout.For(frame.Mode, fac.Bandwidth);
            var settings = (frame.Mode, fac.Bandwidth, fac.Qam, fac.Protection, fac.Interleave);
            if (_dataSettings != settings)
            {
                // New settings (or a new transmission): start the decoding chain afresh.
                _dataSettings = settings;
                _deinterleaver = new TimeDeinterleaver(layout.DataCellsPerFrame, fac.Interleave);
                _mscDecoder = new MscDecoder(layout.DataCellsPerFrame, fac.Qam, fac.Protection);
            }

            // The frame's data cells, in the order they were sent, with the channel divided out.
            var cells = new Complex[layout.DataCellsPerFrame];
            var weights = new double[layout.DataCellsPerFrame];
            for (int i = 0; i < cells.Length; i++)
            {
                var (sym, k) = layout.DataCells[i];
                Complex h = frame.Channel[sym][k];
                double power = h.Real * h.Real + h.Imaginary * h.Imaginary;
                if (power < 1e-12) continue;
                cells[i] = frame.Cells[sym][k] / h;
                // A frame whose FAC couldn't be read is suspect: its cells count as missing.
                weights[i] = frame.FacRead ? power : 0;
            }

            // Quality is measured on a frame once the next frame's FAC has been read too, which
            // shows the signal lasted all through it (not the silence as a transmission ends).
            if (frame.FacRead && _qualityPending is { } pending) MeasureQuality(pending.Cells, pending.Weights, pending.Qam);
            _qualityPending = frame.FacRead ? ((Complex[])cells.Clone(), (double[])weights.Clone(), fac.Qam) : null;

            foreach (var original in _deinterleaver!.Add(frame.Number, cells, weights))
            {
                var bits = _mscDecoder!.Decode(original.Cells, original.Weights);
                var packet = MscDecoder.ToBytes(bits);
                if (_mot.AddPacket(packet) is { } file) Hold(file);
            }
        }
    }

    // A file received complete is held until the transmission ends, so the picture appears
    // when the sender stops (senders keep repeating pieces for a few seconds after the last one).
    private ReceivedFile? _heldFile;

    private void Hold(ReceivedFile file)
    {
        if (_heldFile is not null) Finish(_heldFile);   // a second file in one transmission
        _heldFile = file;
    }

    // ---- signal quality ----
    //
    // How cleanly the picture-data cells arrive: each cell, once the radio path is undone,
    // should sit exactly on one of the QAM points that could have been sent. The distance
    // to the nearest point is the error. Quality = power of the points / power of the errors,
    // in dB (the "MER", modulation error ratio). Averaged over the last few frames.
    private double _qualitySignal, _qualityError;
    private (Complex[] Cells, double[] Weights, int Qam)? _qualityPending;

    /// <summary>Signal quality in dB, or null until picture data has been measured.</summary>
    public double? QualityDb => _qualityError > 0 ? 10 * Math.Log10(_qualitySignal / _qualityError) : null;

    /// <summary>Roughly the quality each QAM needs for pictures to come through
    /// (measured with noise added to the clean recordings).</summary>
    public static double QualityNeeded(int qam, HamDrmProtection protection) => (qam, protection) switch
    {
        (4, _) => QualityNeeded4,
        (16, HamDrmProtection.High) => QualityNeeded16High,
        (16, _) => QualityNeeded16Low,
        (_, HamDrmProtection.High) => QualityNeeded64High,
        _ => QualityNeeded64Low,
    };
    // The quality at which the test pictures still came through, with multi-stage decoding
    // (Oct 13, 2026: recordings 06, 07, 04, 02, 01 and 05 with noise added in 0.5 dB steps).
    private const double QualityNeeded4 = 6, QualityNeeded16High = 11, QualityNeeded16Low = 11,
                         QualityNeeded64High = 17, QualityNeeded64Low = 18;

    private void MeasureQuality(Complex[] cells, double[] weights, int qam)
    {
        // DRM's QAM points sit at odd numbers (±1, ±3, ±5, ±7) once scaled up by this much.
        int levels = qam == 4 ? 2 : qam == 16 ? 4 : 8;
        double scale = Math.Sqrt(qam == 4 ? 2 : qam == 16 ? 10 : 42);
        double signal = 0, error = 0;
        for (int i = 0; i < cells.Length; i++)
        {
            if (weights[i] <= 0) continue;
            var c = cells[i] * scale;
            // An empty cell: at the start of a long-interleaved transmission the sender has
            // nothing yet for part of each frame and sends nothing there. Not an error.
            if (c.Magnitude < EmptyCell) continue;
            var point = new Complex(NearestLevel(c.Real, levels), NearestLevel(c.Imaginary, levels));
            signal += point.Real * point.Real + point.Imaginary * point.Imaginary;
            var e = c - point;
            error += e.Real * e.Real + e.Imaginary * e.Imaginary;
        }
        // Each new frame counts for a quarter: the figure follows changes within a few seconds.
        // (And for the whole transmission's report, every frame counts the same.)
        if (error > 0) _transmissionFrames.Add(10 * Math.Log10(signal / error));
        _qualitySignal = 0.75 * _qualitySignal + 0.25 * signal;
        _qualityError = 0.75 * _qualityError + 0.25 * error;
    }

    // Far inside the smallest point (at ±1 ±1, size 1.4): noise rarely puts a real cell there.
    private const double EmptyCell = 0.35;

    private static double NearestLevel(double v, int levels)
    {
        // Levels are -(levels-1), ..., -1, +1, ..., +(levels-1) in steps of 2.
        double p = 2 * Math.Floor(v / 2) + 1;
        return Math.Clamp(p, -(levels - 1), levels - 1);
    }

    /// <summary>The quality, for the screen: "quality 14 dB (64-QAM needs about 20)".</summary>
    public string QualityText
    {
        get
        {
            if (QualityDb is not double q || Fac is not { } fac) return "";
            double needed = QualityNeeded(fac.Qam, fac.Protection);
            return $"quality {q:0} dB" + (q < needed + 3 ? $" ({fac.Qam}-QAM needs about {needed:0})" : "");
        }
    }

    // Packet counts when the current transmission began, so its own packets can be counted.
    private int _goodAtStart, _badAtStart;
    // What the last transmission brought, for the file line once it's over.
    private string? _endedText;

    private int TransmissionPacketsGood => PacketsGood - _goodAtStart;
    private int TransmissionPacketsBad => PacketsBad - _badAtStart;

    private void TransmissionEnded()
    {
        // Called again and again while there's no signal: only the first time counts.
        if (_heldFile is null && TransmissionPacketsGood + TransmissionPacketsBad == 0) return;
        var progress = _mot.Progress;   // the file this transmission carried, if its name is known
        var seen = _mot.PiecesThisTransmission.Order().ToArray();

        // A Reed-Solomon file with pieces missing is rebuilt now, once every repeat has had its chance.
        var file = _heldFile ?? _mot.FinishWithRepairs();
        _heldFile = null;
        if (file is not null) Finish(file);

        // Close the books on this transmission, so the next one starts with a clean line.
        var unfinished = _mot.EndOfTransmission();
        // Remember how a picture that's still missing pieces was sent, and by whom, for a BSR.
        if (unfinished is not null && _transmissionMode is { } heardMode && Fac is { } heardFac)
            _heardFor[unfinished.TransportId] = new(new TransmitSettings(heardMode, heardFac.Bandwidth, heardFac.Qam,
                heardFac.Interleave, heardFac.Protection), Callsign, DateTime.Now);
        var kept = _mot.Unfinished().Select(u => u.TransportId).ToHashSet();
        foreach (int gone in _heardFor.Keys.Where(id => !kept.Contains(id)).ToList()) _heardFor.Remove(gone);
        _endedText = file is { Name: BsrRequest.FileNameOnAir } ? BsrText(file)
            : file is not null ? ReceivedText(file)
            : unfinished is { Name: { } name } ? $"Couldn't finish {name}: {unfinished.SegmentsReceived} of {unfinished.SegmentsExpected} pieces arrived" +
                  (RsFile.LevelOf(name) is not null ? ", too few for Reed-Solomon to rebuild the rest." : ".")
            : TransmissionPacketsGood == 0 ? $"No file came through: none of the {TransmissionPacketsBad} data packets could be read."
            : "No file came through.";

        int expected = unfinished?.SegmentsExpected ?? progress?.SegmentsExpected ?? 0;
        int? transportId = file?.TransportId ?? unfinished?.TransportId ?? progress?.TransportId;
        bool continues = Continues(_previousTransmission, _transmissionFirstFrame, transportId);
        _previousTransmission = (_transmissionLastFrame, transportId);
        TransmissionReported?.Invoke(new TransmissionReport(
            DateTime.Now, file?.Name ?? unfinished?.Name ?? progress?.Name, file,
            file is not null ? Math.Max(0, expected - file.RepairedPieces) : unfinished?.SegmentsReceived ?? progress?.SegmentsReceived ?? 0,
            expected, Callsign, _transmissionMode, Fac, _transmissionFrames.ToArray(),
            TransmissionPacketsGood, TransmissionPacketsBad)
        {
            PiecesSeen = seen,
            ContinuesPrevious = continues,
        });

        _goodAtStart = PacketsGood;
        _badAtStart = PacketsBad;
        _transmissionFrames.Clear();
        _transmissionMode = null;
        _transmissionFirstFrame = _transmissionLastFrame = null;
    }

    // Was this "transmission" really the rest of the previous one, after the signal dropped out
    // for a few seconds? The sender kept sending in between, so this one's frames fall exactly on
    // the previous one's 400 ms beat. A new transmission starts at a time of its own: the chance of
    // it landing within 5 ms of the old beat by accident is about 1 in 40.
    private static bool Continues((double? LastFrame, int? TransportId)? previous, double? firstFrame, int? transportId)
    {
        if (previous is not { LastFrame: double last } prev || firstFrame is not double first) return false;
        if (transportId is null || prev.TransportId != transportId) return false;
        double gap = first - last;
        if (gap <= 0 || gap > MaxDropout.TotalSeconds) return false;
        double offBeat = gap - Math.Round(gap / SampleClock.FramePeriod) * SampleClock.FramePeriod;
        return Math.Abs(offBeat) <= 0.005;
    }

    /// <summary>The longest dropout after which a transmission still counts as continuing.</summary>
    public static readonly TimeSpan MaxDropout = TimeSpan.FromSeconds(20);

    private double? _transmissionFirstFrame, _transmissionLastFrame;
    private (double? LastFrame, int? TransportId)? _previousTransmission;

    // For the transmission's report: its mode, and the quality of every frame measured.
    private HamDrmMode? _transmissionMode;
    private readonly List<double> _transmissionFrames = new();

    /// <summary>Raised (on the audio thread) when a transmission ends: what it carried and how it went.</summary>
    public event Action<TransmissionReport>? TransmissionReported;

    private static string ReceivedText(ReceivedFile file) =>
        file.Problem is not null ? $"Received {file.Name}, but: {file.Problem}"
        : $"Received {file.Name}  ·  {file.Data.Length / 1000.0:0.0} kB" +
          (file.ReedSolomonLevel is int rs ? $"  ·  Reed-Solomon RS{rs}" : "") +
          (file.RepairedPieces > 0 ? $" rebuilt {file.RepairedPieces} missing pieces" : "");

    /// <summary>Call when the audio stops (e.g. a recording has finished playing), so a file
    /// still waiting for the end of the transmission is finished.</summary>
    public void EndOfAudio()
    {
        lock (_lock)
        {
            TransmissionEnded();
            CopyProgress();
        }
    }

    private void Finish(ReceivedFile file)
    {
        // A request for missing pieces isn't a picture: it's passed on, not shown or saved.
        if (file.Name == BsrRequest.FileNameOnAir)
        {
            if (BsrRequest.Parse(file.Data) is { } request) BsrReceived?.Invoke(new HeardBsr(request, Callsign, DateTime.Now));
            return;
        }
        // (An .rs1-.rs4 file arrives already unwrapped by the MotReceiver.)
        if (_transmissionMode is { } mode && Fac is { } fac)
            file = file with { Settings = new TransmitSettings(mode, fac.Bandwidth, fac.Qam, fac.Interleave, fac.Protection) };
        LastFile = file;
        FileReceived?.Invoke(file);
    }

    private string BsrText(ReceivedFile file) =>
        BsrRequest.Parse(file.Data) is { } r
            ? $"Heard a request for {r.Missing.Count} missing pieces (BSR)" + (Callsign is { Length: > 0 } c ? $" from {c}" : "") + "."
            : "Heard a request for missing pieces (BSR) that couldn't be read.";

    /// <summary>Raised (on the audio thread) when a request for missing pieces (a BSR) arrives,
    /// whoever it's for.</summary>
    public event Action<HeardBsr>? BsrReceived;

    // How each unfinished picture was sent, by transport ID.
    private readonly Dictionary<int, (TransmitSettings Settings, string? Callsign, DateTime Heard)> _heardFor = new();

    /// <summary>Pictures still missing pieces, the most recently heard first: what a BSR can ask for.</summary>
    public IReadOnlyList<IncompleteFile> Incomplete
    {
        get
        {
            lock (_lock)
            {
                var list = new List<IncompleteFile>();
                foreach (var u in _mot.Unfinished())
                {
                    if (u.Name == BsrRequest.FileNameOnAir || u.TransportId == _mot.Progress?.TransportId && Detection.Mode is not null)
                        continue;   // not a picture, or still arriving
                    if (_heardFor.TryGetValue(u.TransportId, out var h)) list.Add(new IncompleteFile(u, h.Settings, h.Callsign, h.Heard));
                }
                return list;
            }
        }
    }

    /// <summary>Raised (on the audio thread) when a file has been received complete.</summary>
    public event Action<ReceivedFile>? FileReceived;

    /// <summary>The most recent file received complete, or null.</summary>
    public ReceivedFile? LastFile { get; private set; }

    /// <summary>The file currently arriving, or null.</summary>
    /// <remarks>For the screen, a copy, made each time a piece of audio has been worked through
    /// (CopyProgress): the screen reads this many times a second while the audio thread may be adding
    /// to the files being put together, and reading those directly at that moment could fail and close
    /// RFpix. On the audio thread itself (in the receiver's own events), where the lock is held, it's
    /// read directly.</remarks>
    public TransferProgress? FileProgress => Monitor.IsEntered(_lock) ? _mot.Progress : Volatile.Read(ref _progress);

    private TransferProgress? _progress;

    // (Called holding the lock.)
    private void CopyProgress() => Volatile.Write(ref _progress, _mot.Progress);

    /// <summary>Data packets received with a good or bad checksum (one per frame).</summary>
    public int PacketsGood => _mot.PacketsGood;
    public int PacketsBad => _mot.PacketsBad;

    /// <summary>A third line for the screen, about the file arriving,
    /// e.g. "Receiving img04.jpg  ·  6.3 kB  ·  34 of 56 pieces (61%)".</summary>
    public string FileText
    {
        get
        {
            var progress = FileProgress;   // the file arriving in this transmission, if any
            var last = LastFile;
            if (last is not null && progress?.TransportId == last.TransportId) return ReceivedText(last);
            if (progress is null)
            {
                int good = TransmissionPacketsGood, bad = TransmissionPacketsBad;
                if (Detection.Mode is null || good + bad == 0) return _endedText ?? "";   // between transmissions
                // A signal, but no file yet. If no data at all gets through, say so.
                return good == 0 && bad >= 5
                    ? $"The picture data can't be read: 0 of {bad} packets came through. The signal may be too weak or distorted" +
                      (Fac is { } fac ? $" for {fac.Qam}-QAM." : ".")
                    : "Waiting for a file…";
            }
            string name = progress.Name is { } n
                ? (RsFile.LevelOf(n) is int rsLevel ? $"{n} (Reed-Solomon RS{rsLevel})" : n)
                : "a file";
            string size = progress.Size > 0 ? $"  ·  {progress.Size / 1000.0:0.0} kB" : "";
            string pieces = progress.SegmentsExpected > 0
                ? $"  ·  {progress.SegmentsReceived} of {progress.SegmentsExpected} pieces ({progress.Fraction:P0})"
                : $"  ·  {progress.SegmentsReceived} pieces";
            return $"Receiving {name}{size}{pieces}";
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
        // A new signal starts the picture-data chain afresh (files in progress are kept,
        // so a transmission that fades out for a moment can carry on).
        _waitingFrames.Clear();
        _frameZeroTime = null;
        _qualitySignal = _qualityError = 0;
        _qualityPending = null;
        _dataSettings = null;
        _deinterleaver = null;
        _mscDecoder = null;
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

    /// <summary>The transmission's settings as heard so far, for the screen's boxes (each null
    /// until known): mode, the FAC's settings, Reed-Solomon level (0 for none, known once the
    /// file's name has arrived) and callsign. All null between transmissions.</summary>
    public HeardSettings? Heard
    {
        get
        {
            if (Problem is not null || Detection.Mode is not { } mode) return null;
            int? rs = null;
            if (FileProgress?.Name is { } name) rs = RsFile.LevelOf(name) ?? 0;
            else if (LastFile is { } last && FileProgress?.TransportId == last.TransportId) rs = last.ReedSolomonLevel ?? 0;
            return new HeardSettings(mode, Fac, rs, Callsign);
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
            string quality = QualityText is { Length: > 0 } q ? $"  ·  {q}" : "";
            return $"HamDRM mode {mode}  ·  {offset}{clock}  ·  strength {detection.Strength:P0}{quality}";
        }
    }

    /// <summary>The signal as heard now, for the screen's boxes, or null with no signal (or a problem).</summary>
    public SignalReading? Signal
    {
        get
        {
            if (Problem is not null || Detection is not { Mode: { } mode } detection) return null;
            double? needed = Fac is { } fac ? QualityNeeded(fac.Qam, fac.Protection) : null;
            return new SignalReading(mode, Lock is { IsLocked: true } l ? l.OffsetHz : null, ClockPpm,
                                     detection.Strength, needed is null ? null : QualityDb, needed);
        }
    }

    /// <summary>Feed in audio exactly as it comes from the audio layer.</summary>
    public void AddSamples(float[] samples, int count, int sampleRate)
    {
        // Audio arrives on the audio thread, but Reset comes from the screen's
        // thread. The lock lets only one of them in at a time.
        lock (_lock)
        {
            try
            {
                if (sampleRate != _sampleRate) Restart(sampleRate);
                if (_downconverter is null) return;

                _converted.Clear();
                _downconverter.Process(samples.AsSpan(0, count), _converted);
                _detector.AddSamples(_converted);
            }
            finally
            {
                CopyProgress();   // for the screen (see FileProgress)
            }
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
            _lastFoundFrame = null;
            ForgetFac();
            _mot = new MotReceiver();   // a new source: forget files in progress too
            _heardFor.Clear();
            LastFile = null;
            _heldFile = null;
            _endedText = null;
            _goodAtStart = _badAtStart = 0;
            Problem = null;
            CopyProgress();
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

/// <summary>How one transmission went, for test reports and logs.</summary>
/// <param name="Name">The file's name, if its header arrived.</param>
/// <param name="File">The file, if it came through complete.</param>
/// <param name="PiecesExpected">Pieces the file has (0 if unknown).</param>
/// <param name="Settings">The settings announced (FAC), if read.</param>
/// <param name="PiecesReceived">Pieces of the file now held (an earlier transmission of the same file
/// may have brought some): all of them if it came through.</param>
/// <param name="FrameQualities">The signal quality of each frame measured, in dB.</param>
public sealed record TransmissionReport(
    DateTime Ended, string? Name, ReceivedFile? File, int PiecesReceived, int PiecesExpected,
    string? Callsign, HamDrmMode? Mode, FacInfo? Settings, IReadOnlyList<double> FrameQualities,
    int PacketsGood, int PacketsBad)
{
    /// <summary>The pieces this transmission brought by itself (piece numbers).</summary>
    public IReadOnlyList<int> PiecesSeen { get; init; } = Array.Empty<int>();

    /// <summary>True if this is the rest of the previous transmission, after the signal dropped
    /// out for a few seconds (the sender never stopped).</summary>
    public bool ContinuesPrevious { get; init; }

    /// <summary>The transmission's typical signal quality: the middle of its frames' figures
    /// (half were better, half worse), so a few frames lost in a dropout don't drag it down.</summary>
    public double? QualityDb
    {
        get
        {
            if (FrameQualities.Count == 0) return null;
            var sorted = FrameQualities.Order().ToArray();
            int mid = sorted.Length / 2;
            return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
        }
    }
}

/// <summary>A picture still missing pieces, and how it was sent (see <see cref="HamDrmReceiver.Incomplete"/>).</summary>
public sealed record IncompleteFile(UnfinishedFile File, TransmitSettings Settings, string? Callsign, DateTime Heard)
{
    /// <summary>The request for its missing pieces, EasyPal style (with its name and mode).</summary>
    public BsrRequest Request() =>
        new(File.TransportId, File.PieceSize, File.Missing, File.Name, BsrRequest.ModeCodeOf(Settings));

    /// <summary>The request's sound: bsr.bin sent with the same settings the picture came with
    /// (as QSSTV does), from <paramref name="callsign"/>.</summary>
    /// <param name="transportId">0-2; the next in turn if not given (only to work out the length, pass one).</param>
    public HamDrmTransmitter Transmitter(string callsign, int? transportId = null) =>
        new(BsrRequest.FileNameOnAir, Request().ToBytes(), Settings, callsign, transportId: transportId ?? BsrTransportIds.Next());
}

/// <summary>The signal being received (see <see cref="HamDrmReceiver.Signal"/>).</summary>
/// <param name="OffsetHz">How far off frequency it is; null while it's still being found.</param>
/// <param name="ClockPpm">How fast or slow the sender's sound card runs; null until measured.</param>
/// <param name="Strength">0 to 1: how clearly the mode was recognised.</param>
/// <param name="QualityDb">The signal quality; null until measured (and the settings read).</param>
/// <param name="QualityNeededDb">The quality these settings need to come through.</param>
public sealed record SignalReading(HamDrmMode Mode, double? OffsetHz, double? ClockPpm, double Strength,
                                   double? QualityDb, double? QualityNeededDb)
{
    /// <summary>Good enough for the settings in use (null if not known yet).</summary>
    public bool? QualityGood => QualityDb is double q && QualityNeededDb is double n ? q >= n : null;
}

/// <summary>A request for missing pieces that was heard, and who sent it.</summary>
public sealed record HeardBsr(BsrRequest Request, string? Callsign, DateTime Heard);

/// <summary>What's known about the transmission being received (see <see cref="HamDrmReceiver.Heard"/>).</summary>
public sealed record HeardSettings(HamDrmMode Mode, FacInfo? Fac, int? ReedSolomon, string? Callsign);
