// ReceivePage.axaml.cs - the code half of the Receive tab (on the TX/RX page).
//
// What happens here:
//   1. The radio's sound input is the one chosen on the Settings page (a change there
//      switches over at once, if listening).
//   2. When you press Start listening, open that device through the audio layer.
//   3. Each chunk of audio is measured (how loud is it?) and handed to the
//      spectrum analyzer, all on the audio thread.
//   4. Twenty times a second a timer moves the level meter to match.
//   5. Each time the analyzer finishes a line, it's added to the waterfall.
//   6. "Play recording…" does the same with a WAV file instead of the radio:
//      a second input (_player) feeds the very same steps 3 to 5.
//   7. The same audio also goes to the HamDRM receiver, which says whether it
//      hears a HamDRM signal, in which mode, and (from the FAC) the transmission's
//      settings and the sender's callsign. The timer shows those too.
//   8. When the receiver finishes a file, it's saved to the received folder and,
//      if it's a picture (JPEG, JPEG 2000, PNG...), shown on the right.
//   9. "Record" saves what the radio hears to a WAV file (see Receive/AudioRecorder.cs),
//      starting with the 30 seconds before it was pressed.
//  11. The same sound goes to the analog SSTV receiver (Analog/SstvReceiver.cs) too, so Martin,
//      Scottie, Robot and PD pictures are received as well, with nothing to switch: each
//      appears line by line in the picture box, and is saved (as PNG) when it ends.
//  10. A picture that ends with pieces missing is listed under "Missing pieces": Ask for
//      them sends a request (BSR) to its sender. A request someone else sends is passed
//      to the TX/RX page (BsrHeard), which offers to answer it.

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.Input;
using Avalonia;
using Avalonia.VisualTree;
using RFpix.Audio;
using RFpix.Dsp;
using RFpix.Gallery;
using RFpix.HamDrm;
using RFpix.Radio;
using RFpix.Imaging;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using RFpix.Receive;
using RFpix.Settings;

namespace RFpix.Views;

public partial class ReceivePage : UserControl, IDisposable
{
    private readonly IAudioInput _input = AudioSystem.CreateInput();
    private int _gapsAtLastReport;   // sound gaps counted up to the last transmission's end
    private readonly TestLog _testLog = new();   // test results, written to Test results.csv
    private readonly WavFileAudioInput _player = new();
    private readonly DispatcherTimer _meterTimer;
    private readonly SpectrumAnalyzer _analyzer = new(maxFrequency: 3000);
    private readonly HamDrmReceiver _receiver = new();
    private readonly RFpix.Analog.SstvReceiver _sstv = new();   // analog SSTV, alongside HamDRM
    private volatile int _analogLines;                         // lines of the analog picture so far
    private RFpix.Analog.SstvModeInfo? _analogMode;            // the analog picture shown in the boxes
    private string _analogFrom = "";                           // its sender, from the FSK ID
    private DateTime _analogBoxesUntil = DateTime.MinValue;    // the analog boxes stay a little after it ends
    private (string Folder, GalleryEntry Entry, DateTime Saved)? _lastAnalog;   // for the FSK ID that follows
    private string _analogCaption = "";
    private readonly IAudioOutput _output = AudioSystem.CreateOutput();   // for BSR requests
    private CancellationTokenSource? _asking;          // a BSR request going out
    private readonly HashSet<int> _askedFor = new();   // pictures asked for (transport IDs)

    /// <summary>Raised (on the screen's thread) when a request for missing pieces (BSR) is heard.</summary>
    public event Action<HeardBsr>? BsrHeard;
    private readonly AudioRecorder _recorder = new();
    // Takes audio off the sound card's thread at once; the work happens on its own thread.
    private readonly AudioPump _pump;
    // Callsigns heard while recording, for the file's name.
    private readonly List<string> _recordingCallsigns = new();

    // The latest loudness measurement, written by the audio thread and read by the timer.
    private volatile float _latestRms;


    public ReceivePage()
    {
        InitializeComponent();

        _pump = new AudioPump(ProcessSamples);
        _pump.Failed += ex => Dispatcher.UIThread.Post(() => ShowError($"Something went wrong decoding the audio: {ex.Message}"));

        _input.SamplesAvailable += OnSamples;
        _input.Failed += OnAudioFailed;

        // The recording player feeds the same handler as the radio.
        _player.SamplesAvailable += OnSamples;
        _player.Failed += OnAudioFailed;
        _player.Finished += (_, _) => _pump.Post(() =>
        {
            // The recording is over (and all its audio processed), so its transmission is
            // too: show any picture still waiting.
            _receiver.EndOfAudio();
            _sstv.End();
            Dispatcher.UIThread.Post(() =>
            {
                string name = _player.FileName ?? "the recording";
                StopPlayback();
                FormatText.Text = $"Finished playing {name}";
            });
        });

        // A finished file arrives on the audio thread; save and show it on the screen's thread.
        _receiver.FileReceived += file =>
        {
            string? callsign = _receiver.Callsign;
            double? quality = _receiver.QualityDb;
            Dispatcher.UIThread.Post(() => { ShowReceivedFile(file, callsign, quality); ShowIncomplete(); });
        };
        _receiver.BsrReceived += bsr => Dispatcher.UIThread.Post(() => BsrHeard?.Invoke(bsr));

        // Analog SSTV (on the audio thread): show each picture as it arrives, and save it at the end.
        _sstv.Started += mode => { _analogLines = 0; Dispatcher.UIThread.Post(() => ShowAnalogStart(mode)); };
        _sstv.Progress += (picture, lines) => { _analogLines = lines; Dispatcher.UIThread.Post(() => ShowAnalogProgress(picture, lines)); };
        _sstv.Finished += picture => Dispatcher.UIThread.Post(() => ShowAnalogPicture(picture));
        _sstv.IdReceived += call => Dispatcher.UIThread.Post(() => ShowAnalogId(call));

        // When a transmission ends: if it was one of RFpix's test transmissions, say how it
        // came through and add it to the test log.
        _receiver.TransmissionReported += report =>
        {
            Dispatcher.UIThread.Post(ShowIncomplete);
            int gaps = _input.Gaps - _gapsAtLastReport;
            _gapsAtLastReport = _input.Gaps;
            if (TestResults.Recognise(report, Math.Max(0, gaps)) is not { } result) return;
            string? problem = null;
            try { result = _testLog.Add(result); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problem = $"Couldn't add it to the test log ({ex.Message}). Is the file open in Excel?";
            }
            Dispatcher.UIThread.Post(() =>
            {
                TestResultText.Text = result.Text;
                TestPanel.IsVisible = true;
                if (problem is not null) ShowError(problem);
            });
        };

        // A recording that stops by itself (too long, disk full): say so on the screen's thread.
        _recorder.StoppedByItself += (path, why) => Dispatcher.UIThread.Post(() =>
        {
            ShowRecordingSaved(path, why);
            UpdateStartButton();
        });

        _meterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _meterTimer.Tick += (_, _) => UpdateMeter();

        // The analyzer works on the audio thread; the waterfall must be updated
        // on the screen's thread, so each finished line is "posted" across.
        _analyzer.LineReady += (line, hzPerStep) =>
            Dispatcher.UIThread.Post(() => Waterfall.AddLine(line, hzPerStep));

        AppSettings.SoundDevicesChanged += OnSoundDevicesChanged;
        LayoutUpdated += OnLayoutUpdated;
        UpdateStartButton();
    }

    // ---- the sound device (chosen on the Settings page) ----

    /// <summary>The radio's sound input chosen on the Settings page, if it's plugged in.</summary>
    private AudioDeviceInfo? ChosenDevice()
    {
        var id = AppSettings.Current.InputDeviceId;
        if (id is null) return null;
        try { return _input.GetDevices().FirstOrDefault(d => d.Id == id); }
        catch (Exception) { return null; }
    }

    // The device was changed (or Refresh clicked, or the sound card calibrated) on the Settings page:
    // switch over if listening.
    private void OnSoundDevicesChanged()
    {
        double ppm = AppSettings.Correction(AppSettings.Current.InputClock, AppSettings.Current.InputDeviceId);
        bool correctionChanged = (_clockCorrector?.Ratio ?? 1) != (ppm != 0 ? ClockCorrector.InputRatio(ppm) : 1);
        if (_input.IsRunning && (_listeningTo != AppSettings.Current.InputDeviceId || correctionChanged))
        {
            _input.Stop();
            StartListening();
        }
        UpdateStartButton();
    }

    private string? _listeningTo;   // the device ID being listened to
    private string _deviceName = "";

    // The waterfall ends at the Record button's right edge; the picture takes the rest.
    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (RecordButton.TranslatePoint(new Avalonia.Point(RecordButton.Bounds.Width, 0), WaterfallRow) is not { } edge) return;
        double width = Math.Max(160, Math.Round(edge.X));
        if (Math.Abs(WaterfallRow.ColumnDefinitions[0].Width.Value - width) > 0.5)
            WaterfallRow.ColumnDefinitions[0].Width = new GridLength(width);
    }

    // ---- start / stop ----

    private void OnStartStopClick(object? sender, RoutedEventArgs e)
    {
        if (_input.IsRunning) StopListening();
        else StartListening();
    }

    private void StartListening()
    {
        if (ChosenDevice() is not { } device)
        {
            ShowError(AppSettings.Current.InputDeviceId is null
                ? "Pick the radio's sound input in Settings (Radio tab) first."
                : "The sound input chosen in Settings (Radio tab) isn't connected. Switch on or plug in the radio, or pick another device there.");
            return;
        }

        StopPlayback();   // the radio and a recording can't both feed the waterfall

        try
        {
            double ppm = AppSettings.Correction(AppSettings.Current.InputClock, device.Id);
            _clockCorrector = ppm != 0 ? new ClockCorrector(ClockCorrector.InputRatio(ppm)) : null;
            _input.Start(device.Id);
            _listeningTo = device.Id;
            _deviceName = device.Name;
            _gapsAtLastReport = 0;   // a new start counts gaps afresh
            HideError();
            FormatText.Text = $"{device.Name}  ·  {_input.FormatDescription}";
            _meterTimer.Start();
        }
        catch (AudioException ex)
        {
            ShowError(ex.Message);
        }
        UpdateStartButton();
    }

    private void StopListening()
    {
        if (_recorder.IsRecording) StopRecording();
        _input.Stop();
        _pump.Post(() => _recorder.Reset());   // forget the last 30 seconds: the next start is a fresh source
        if (!_player.IsRunning) ResetMeter();
        UpdateStartButton();
    }

    // Nothing is feeding the waterfall any more: empty the meter and clear the status line.
    private void ResetMeter()
    {
        _meterTimer.Stop();
        _latestRms = 0;
        LevelMeter.Value = 0;
        LevelMeter.ClearValue(ForegroundProperty);
        LevelText.Text = "Not listening";
        FormatText.Text = "";
        SignalText.Text = "";
        SignalText.IsVisible = false;
        _analogBoxesUntil = DateTime.MinValue;
        ShowSignal();   // empty boxes
        ShowHeard();
        TransmissionText.Text = "";
        FileText.Text = "";
        // Start fresh next time, so a signal from one source isn't carried over to another.
        // (Through the pump, so it happens after any audio still waiting.)
        _pump.Post(() =>
        {
            _receiver.Reset();
            _sstv.End();
            Dispatcher.UIThread.Post(ShowIncomplete);
        });
    }

    private void UpdateStartButton()
    {
        StartButton.Content = _input.IsRunning ? "Stop" : "Start listening";
        StartButton.IsEnabled = _input.IsRunning || AppSettings.Current.InputDeviceId is not null;
        PlayButton.Content = _player.IsRunning ? "Stop playback" : "Play recording…";
        RecordButton.IsEnabled = _input.IsRunning;   // records the radio only, not a recording playing
        RecordButton.Content = _recorder.IsRecording ? "Stop recording" : "Record";
    }

    // ---- recording what the radio hears ----

    private static string RecordingsFolder => AudioRecorder.DefaultFolder;

    private void OnRecordClick(object? sender, RoutedEventArgs e)
    {
        if (_recorder.IsRecording)
        {
            StopRecording();
            return;
        }
        try
        {
            _recordingCallsigns.Clear();
            string path = _recorder.Start(RecordingsFolder, DateTime.Now);
            RecordText.Text = $"Recording to \"{Path.GetFileName(path)}\"…";
            RecordText.Foreground = Brushes.OrangeRed;
            RecordText.IsVisible = true;
            HideError();
        }
        catch (InvalidOperationException)
        {
            ShowError("No sound has come from the radio yet. Wait a moment, then press Record again.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError($"Couldn't start a recording in {RecordingsFolder}: {ex.Message}");
        }
        UpdateStartButton();
    }

    private void StopRecording()
    {
        var (length, bytes) = _recorder.Progress;
        string? path = _recorder.Stop(_recordingCallsigns);
        ShowRecordingSaved(path, null, length, bytes);
        UpdateStartButton();
    }

    private void ShowRecordingSaved(string? path, string? why, TimeSpan length = default, long bytes = 0)
    {
        RecordText.ClearValue(TextBlock.ForegroundProperty);
        RecordText.IsVisible = true;
        if (path is null)
        {
            RecordText.Text = why ?? "";
            return;
        }
        // Next time "Play recording…" is pressed, start in the recordings folder.
        AppSettings.Current.LastRecordingFolder = Path.GetDirectoryName(path);
        AppSettings.Current.Save();
        string size = bytes > 0 ? $" ({length:h\\:mm\\:ss}, {bytes / 1e6:0.0} MB)" : "";
        RecordText.Text = (why is null ? "" : why + " ") + $"Saved \"{Path.GetFileName(path)}\"{size} in Documents\\RFpix\\Recordings";
    }

    // ---- playing a recording ----

    private async void OnPlayClick(object? sender, RoutedEventArgs e)
    {
        if (_player.IsRunning)
        {
            StopPlayback();
            return;
        }

        // Ask which file to play, starting in the folder used last time.
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null) return;

        IStorageFolder? startFolder = null;
        var lastFolder = AppSettings.Current.LastRecordingFolder;
        if (lastFolder is not null && Directory.Exists(lastFolder))
            startFolder = await storage.TryGetFolderFromPathAsync(lastFolder);

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Play a recording",
            AllowMultiple = false,
            SuggestedStartLocation = startFolder,
            FileTypeFilter = new[] { new FilePickerFileType("WAV sound files") { Patterns = new[] { "*.wav" } } },
        });

        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path is null) return;   // cancelled

        AppSettings.Current.LastRecordingFolder = Path.GetDirectoryName(path);
        AppSettings.Current.Save();

        StopListening();   // the radio and a recording can't both feed the waterfall
        try
        {
            _player.Start(path);
            HideError();
            _meterTimer.Start();
        }
        catch (AudioException ex)
        {
            ShowError(ex.Message);
        }
        UpdateStartButton();
    }

    private void StopPlayback()
    {
        _player.Stop();
        if (!_input.IsRunning) ResetMeter();
        UpdateStartButton();
    }

    // ---- audio arriving ----

    // Sound-card calibration: the radio's sound is re-timed to the input's true clock (set on
    // the sound card's thread at each start, used only there).
    private ClockCorrector? _clockCorrector;
    private readonly List<float> _corrected = new();
    private float[] _correctedArray = Array.Empty<float>();

    // On the sound card's (or player's) thread: just hand the chunk over and return.
    private void OnSamples(object? sender, AudioSamplesEventArgs e)
    {
        if (sender == _input && _clockCorrector is { } corrector)
        {
            _corrected.Clear();
            corrector.Process(e.Samples.AsSpan(0, e.Count), _corrected);
            if (_correctedArray.Length < _corrected.Count) _correctedArray = new float[_corrected.Count * 2];
            _corrected.CopyTo(_correctedArray);
            _pump.Add(_correctedArray, _corrected.Count, e.SampleRate, sender);
        }
        else _pump.Add(e.Samples, e.Count, e.SampleRate, sender);
    }

    // On the pump's worker thread, chunk by chunk, in order.
    private void ProcessSamples(float[] samples, int count, int sampleRate, object? source)
    {
        var e = new AudioSamplesEventArgs(samples, count, sampleRate);
        // RMS ("root mean square") is the usual way to measure how loud audio is.
        double sum = 0;
        for (int i = 0; i < e.Count; i++)
        {
            float s = e.Samples[i];
            sum += s * s;
        }
        if (e.Count > 0) _latestRms = (float)Math.Sqrt(sum / e.Count);

        // Feed the same audio to the waterfall's spectrum analyzer...
        _analyzer.AddSamples(e.Samples, e.Count, e.SampleRate);
        // ...and to the HamDRM receiver, and the analog SSTV one.
        _receiver.AddSamples(e.Samples, e.Count, e.SampleRate);
        if (e.SampleRate == RFpix.Analog.SstvReceiver.InputRate) _sstv.AddSamples(e.Samples.AsSpan(0, e.Count));
        // The radio's sound is also kept for recording (the last 30 seconds, or all of it while recording).
        if (source == _input) _recorder.AddSamples(e.Samples, e.Count, e.SampleRate);
    }

    // The radio was unplugged (or a recording couldn't be played): show why, on the screen's own thread.
    private void OnAudioFailed(object? sender, string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (sender == _player) StopPlayback();
            else StopListening();
            ShowError(message);
        });
    }

    // ---- the signal, in boxes ----

    private static readonly IBrush GoodBrush = new SolidColorBrush(Color.Parse("#2E7D32"));
    private static readonly IBrush BadBrush = new SolidColorBrush(Color.Parse("#C62828"));

    private void ShowSignal()
    {
        // Above the boxes: a problem; otherwise nothing.
        string line = _receiver.Problem ?? "";
        SignalText.Text = line;
        SignalText.IsVisible = line.Length > 0;

        // An analog picture arriving (or just ended): only its boxes. Otherwise the digital ones.
        // The boxes show only while a picture is arriving; their space stays, empty, otherwise.
        bool analog = ShowingAnalog;
        bool digital = !analog && (_receiver.Signal is not null || _receiver.Heard is not null);
        Show(DigitalBoxes, digital);
        Show(AnalogBoxes, analog);
        Waterfall.Analog = analog || AppSettings.Current.SendAnalog;   // the marks follow Send as
        if (analog)
        {
            AnalogMode.Text = _analogMode?.Name ?? Empty;
            AnalogLine.Text = _analogMode is null ? Empty : _analogLines.ToString();
            AnalogOf.Text = _analogMode?.Height.ToString() ?? Empty;
            AnalogFrom.Text = _analogFrom.Length > 0 ? _analogFrom : Empty;
            return;
        }

        // The boxes stay, empty until there's a reading (a no-break space keeps each one's height).
        var s = _receiver.Signal;
        SignalOffset.Text = s?.OffsetHz is double hz ? $"{hz:+0.0;-0.0;0.0} Hz" : Empty;
        SignalClock.Text = s?.ClockPpm is double ppm ? $"{Math.Clamp(ppm, -999, 999):+0;-0;0} ppm" : Empty;
        SignalStrength.Text = s is not null ? $"{s.Strength:P0}" : Empty;
        SignalQuality.Text = s?.QualityDb is double q ? $"{q:0} dB" + (s.QualityNeededDb is double n ? $" ({n:0})" : "") : Empty;
        switch (s?.QualityGood)
        {
            case true:
                QualityBox.Background = GoodBrush;
                SignalQuality.Foreground = QualityLabel.Foreground = Brushes.White;
                break;
            case false:
                QualityBox.Background = BadBrush;
                SignalQuality.Foreground = QualityLabel.Foreground = Brushes.White;
                break;
            default:
                QualityBox.ClearValue(Border.BackgroundProperty);
                SignalQuality.ClearValue(TextBlock.ForegroundProperty);
                QualityLabel.ClearValue(TextBlock.ForegroundProperty);
                break;
        }
    }

    // ---- the received transmission's settings, in boxes ----

    /// <summary>Shows or hides boxes, keeping their space either way (so nothing below them moves).</summary>
    private static void Show(Control boxes, bool show)
    {
        boxes.Opacity = show ? 1 : 0;
        boxes.IsHitTestVisible = show;
    }

    /// <summary>An empty box's text: a no-break space, so the box keeps its height.</summary>
    private const string Empty = "\u00A0";

    /// <summary>Whether the analog boxes are showing: while an analog picture arrives, and for a few
    /// seconds after (its FSK ID comes then), unless a HamDRM transmission starts.</summary>
    private bool ShowingAnalog => _sstv.Receiving is not null || DateTime.Now < _analogBoxesUntil && _receiver.Heard is null;

    private void ShowHeard()
    {
        var heard = ShowingAnalog ? null : _receiver.Heard;
        if (heard is not null)
        {
            var fac = heard.Fac;
            HeardMode.Text = heard.Mode.ToString();
            HeardBandwidth.Text = fac is null ? "…" : fac.Bandwidth == HamDrmBandwidth.Wide ? "2.5 kHz" : "2.2 kHz";
            HeardQam.Text = fac is null ? "…" : fac.Qam.ToString();
            HeardInterleave.Text = fac?.Interleave.ToString() ?? "…";
            HeardProtection.Text = fac?.Protection.ToString() ?? "…";
            HeardReedSolomon.Text = heard.ReedSolomon switch { null => "…", 0 => "None", var rs => $"RS{rs}" };
            HeardCallsign.Text = heard.Callsign switch { null => "…", "" => "(none)", var c => c };
            int good = _receiver.FacGood, total = _receiver.FacGood + _receiver.FacFailed;
            TransmissionText.Text = fac is null ? _receiver.TransmissionText : $"Settings read from {good} of {total} frames";
        }
        else
        {
            // Between transmissions: empty boxes.
            HeardMode.Text = HeardBandwidth.Text = HeardQam.Text = HeardInterleave.Text =
                HeardProtection.Text = HeardReedSolomon.Text = HeardCallsign.Text = Empty;
            TransmissionText.Text = "";
        }
    }

    // ---- level meter (runs on the screen's thread, 20 times a second) ----

    private void UpdateMeter()
    {
        // An unfinished test waits a little in case the signal only dropped out; then it's logged.
        try { _testLog.Flush(DateTime.Now); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowError($"Couldn't add a test to the test log ({ex.Message}). Is the file open in Excel?"); }

        // Convert to decibels: 0 dB is the loudest possible, -60 dB is very quiet.
        double db = 20 * Math.Log10(Math.Max(_latestRms, 1e-6));

        // Show -60 dB .. 0 dB as an empty .. full bar.
        LevelMeter.Value = Math.Clamp((db + 60) / 60 * 100, 0, 100);
        LevelText.Text = $"{db:0} dB";

        if (db > -6)
            LevelMeter.Foreground = Brushes.OrangeRed;   // too loud
        else
            LevelMeter.ClearValue(ForegroundProperty);   // back to the theme's colour

        ShowSignal();
        ShowHeard();
        FileText.Text = _receiver.FileText;

        // While recording, show how long and how big, and note any callsign heard for the file's name.
        if (_recorder.IsRecording)
        {
            var (length, bytes) = _recorder.Progress;
            RecordText.Text = $"● Recording  ·  {length:h\\:mm\\:ss}  ·  {bytes / 1e6:0.0} MB  (includes the 30 seconds before)";
            if (_receiver.Callsign is { Length: > 0 } call && !_recordingCallsigns.Contains(call)) _recordingCallsigns.Add(call);
        }

        // Sound Windows lost on the way in (each gap is filled with silence so the timing holds),
        // and audio lost because the computer fell far behind (should never happen): say so.
        if (_input.IsRunning)
        {
            string text = $"{_deviceName}  ·  {_input.FormatDescription}";
            if (_clockCorrector is not null)
                text += $"  ·  clock corrected {AppSettings.Correction(AppSettings.Current.InputClock, _listeningTo):+0.0;-0.0} ppm";
            if (_input.Gaps > 0) text += $"  ·  {_input.Gaps} gap{(_input.Gaps == 1 ? "" : "s")} in the sound from Windows (filled)";
            if (_pump.Dropped > 0) text += $"  ·  {_pump.Dropped} chunks of audio lost: the computer is too busy";
            FormatText.Text = text;
        }

        // While a recording plays, show which one and how far along it is.
        if (_player.IsRunning)
            FormatText.Text = $"Playing {_player.FileName}  ·  {_player.Position:m\\:ss} / {_player.Duration:m\\:ss}";
    }

    // ---- frequency readout when pointing at the waterfall ----

    private void OnWaterfallPointerMoved(object? sender, PointerEventArgs e)
    {
        double x = e.GetPosition(Waterfall).X;
        double width = Waterfall.Bounds.Width;
        if (width <= 0) return;

        double hz = Math.Clamp(x / width, 0, 1) * Waterfall.DisplayMaxFrequency;
        // Near a mark, say what it is (within about 3 pixels).
        string? mark = Waterfall.MarkName(hz, 3 * Waterfall.DisplayMaxFrequency / width);
        PointerFrequencyText.Text = mark is null ? $"{hz:0} Hz" : $"{hz:0} Hz ({mark})";
    }

    private void OnWaterfallPointerExited(object? sender, PointerEventArgs e)
    {
        PointerFrequencyText.Text = "";
    }

    // ---- received files ----

    private static string ReceivedFolder => AppSettings.Current.ReceivedFolder ?? ReceivedFileSaver.DefaultFolder;

    // Digital (HamDRM) and analog SSTV pictures are kept apart, in two folders inside it.
    private static string DigitalFolder => Path.Combine(ReceivedFolder, ReceivedFileSaver.DigitalSubfolder);
    private static string AnalogFolder => Path.Combine(ReceivedFolder, ReceivedFileSaver.AnalogSubfolder);

    // Counts received pictures, so a slow decode can't replace a newer picture on the screen.
    private int _pictureCount;

    private async void ShowReceivedFile(ReceivedFile file, string? callsign, double? quality)
    {
        string from = string.IsNullOrEmpty(callsign) ? "" : $" from {callsign}";
        string savedAs;
        try
        {
            var when = DateTime.Now;
            savedAs = Path.GetFileName(ReceivedFileSaver.Save(file, DigitalFolder, callsign, when));
            // What the Gallery shows about it.
            GalleryIndex.Add(DigitalFolder, new GalleryEntry(savedAs, when, file.Name, string.IsNullOrEmpty(callsign) ? null : callsign,
                file.Settings?.Description, file.ReedSolomonLevel ?? (file.Settings is null ? null : 0), quality,
                file.RepairedPieces, _askedFor.Remove(file.TransportId)));
            SendSupport.RaiseGalleryChanged();
            // A JPEG copy for the website, stamped with the time (Settings, FTP server tab).
            var data = file.Data;
            if (PictureFormats.Detect(data) is not null) _ = Task.Run(() => RFpix.Upload.UploadService.SaveCopy(data, when, analog: false));
        }
        catch (Exception ex)
        {
            ShowError($"Received {file.Name}{from}, but couldn't save it to {DigitalFolder}: {ex.Message}");
            return;
        }
        if (file.Problem is not null) ShowError($"{file.Name}: {file.Problem}");
        string caption = $"{file.Name}{from}  ·  {DateTime.Now:HH:mm}\nSaved as \"{savedAs}\"";
        int number = ++_pictureCount;

        // JPEG 2000 and AVIF are decoded by RFpix (in the background: a big picture takes a
        // moment); anything else is shown if Avalonia can draw it (JPEG, PNG, BMP, GIF).
        Bitmap? bitmap = null;
        string? why = null;
        var format = PictureFormats.Detect(file.Data);
        if (format is PictureFormat.Jpeg2000 or PictureFormat.Avif)
        {
            try
            {
                var picture = await Task.Run(() => Pictures.Decode(file.Data));
                bitmap = Bitmaps.From(picture);
            }
            catch (Exception ex)
            {
                why = ex is PictureException ? ex.Message : $"This {format.Value.DisplayName()} picture can't be shown.";
            }
        }
        else
        {
            try
            {
                using var stream = new MemoryStream(file.Data);
                bitmap = new Bitmap(stream);
            }
            catch
            {
                why = "It isn't a picture RFpix can show.";
            }
        }

        if (number != _pictureCount)
        {
            bitmap?.Dispose();   // a newer picture arrived while this one was decoded
            return;
        }
        if (bitmap is not null)
        {
            var old = PictureImage.Source as IDisposable;
            PictureImage.Source = bitmap;
            old?.Dispose();
            PicturePlaceholder.IsVisible = false;
            PictureCaption.Text = caption;
        }
        else
        {
            PictureCaption.Text = $"{caption}\n{why}";
        }
    }

    private async void OnOpenTestResultsClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var launcher = TopLevel.GetTopLevel(this)?.Launcher;
            if (!File.Exists(TestResults.LogPath)) { ShowError("No test transmissions have been received yet."); return; }
            if (launcher is not null) await launcher.LaunchFileInfoAsync(new FileInfo(TestResults.LogPath));
        }
        catch (Exception ex)
        {
            ShowError($"Couldn't open {TestResults.LogPath}: {ex.Message}");
        }
    }

    private async void OnOpenFolderClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(ReceivedFolder);
            var launcher = TopLevel.GetTopLevel(this)?.Launcher;
            if (launcher is not null) await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(ReceivedFolder));
        }
        catch (Exception ex)
        {
            ShowError($"Couldn't open {ReceivedFolder}: {ex.Message}");
        }
    }

    // ---- analog SSTV ----

    /// <summary>How long the analog boxes stay after a picture ends (its FSK ID comes in the first two seconds).</summary>
    private static readonly TimeSpan AnalogBoxesLinger = TimeSpan.FromSeconds(10);

    private void ShowAnalogStart(RFpix.Analog.SstvModeInfo mode)
    {
        ++_pictureCount;   // a HamDRM picture still being decoded won't replace this one
        _analogMode = mode;
        _analogFrom = "";
        _lastAnalog = null;
        _analogBoxesUntil = DateTime.MaxValue;
        PictureCaption.Text = $"Receiving analog SSTV: {mode.Name} ({mode.Width} × {mode.Height})" +
                              (_sstv.ReceivingLate ? ", joined partway (its VIS code was missed)" : "");
    }

    private void ShowAnalogProgress(DecodedPicture picture, int lines)
    {
        var old = PictureImage.Source as IDisposable;
        PictureImage.Source = Bitmaps.From(picture);
        old?.Dispose();
        PicturePlaceholder.IsVisible = false;
    }

    private async void ShowAnalogPicture(RFpix.Analog.SstvPicture got)
    {
        ShowAnalogProgress(got.Picture, got.Lines);
        _analogBoxesUntil = DateTime.Now + AnalogBoxesLinger;
        var when = got.Ended;
        string status = got.JoinedLate ? "  ·  joined partway (no VIS code)"
                      : got.Complete ? "" : $"  ·  cut short: {got.Lines} of {got.Mode.Height} lines";
        try
        {
            var png = await Task.Run(() => Pictures.EncodePng(got.Picture));
            string name = $"SSTV {got.Mode.Name}.png";
            string savedAs = Path.GetFileName(ReceivedFileSaver.Save(new ReceivedFile(name, png, 0), AnalogFolder, null, when));
            var entry = new GalleryEntry(savedAs, when, name, null, $"Analog SSTV  ·  {got.Mode.Name}" + (got.JoinedLate ? "  ·  joined partway" : ""));
            GalleryIndex.Add(AnalogFolder, entry);
            _lastAnalog = (AnalogFolder, entry, DateTime.Now);
            SendSupport.RaiseGalleryChanged();
            var picture = got.Picture;
            _ = Task.Run(() => RFpix.Upload.UploadService.SaveCopy(picture, when, analog: true));
            _analogCaption = $"Analog SSTV {got.Mode.Name}  ·  {when:HH:mm}{status}";
            PictureCaption.Text = $"{_analogCaption}\nSaved as \"{savedAs}\"";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PictureException)
        {
            ShowError($"Received an analog SSTV picture ({got.Mode.Name}), but couldn't save it to {AnalogFolder}: {ex.Message}");
        }
    }

    /// <summary>An FSK ID: the sender's callsign, shown in the From box and added to the picture just saved.</summary>
    private void ShowAnalogId(string call)
    {
        _analogFrom = call;
        if (_sstv.Receiving is null && DateTime.Now < _analogBoxesUntil) _analogBoxesUntil = DateTime.Now + AnalogBoxesLinger;
        if (_lastAnalog is not { } last || DateTime.Now - last.Saved > TimeSpan.FromSeconds(20)) return;
        var (folder, entry, _) = last;
        _lastAnalog = null;
        try
        {
            GalleryIndex.Add(folder, entry with { Callsign = call });
            SendSupport.RaiseGalleryChanged();
            PictureCaption.Text = $"{_analogCaption}  ·  from {call}\nSaved as \"{entry.File}\"";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The picture is saved already; only the callsign beside it is lost.
        }
    }

    // ---- missing pieces (BSR) ----

    private IReadOnlyList<IncompleteFile> _incomplete = Array.Empty<IncompleteFile>();

    /// <summary>Lists the pictures still missing pieces (the latest first), keeping the choice.</summary>
    private void ShowIncomplete()
    {
        var chosen = MissingBox.SelectedIndex >= 0 && MissingBox.SelectedIndex < _incomplete.Count
            ? _incomplete[MissingBox.SelectedIndex].File.TransportId : (int?)null;
        _incomplete = _receiver.Incomplete;
        MissingBox.ItemsSource = _incomplete.Select(Describe).ToList();
        int index = chosen is int id ? _incomplete.ToList().FindIndex(i => i.File.TransportId == id) : -1;
        MissingBox.SelectedIndex = _incomplete.Count == 0 ? -1 : Math.Max(0, index);
        MissingPanel.IsVisible = _incomplete.Count > 0 || _asking is not null;
        ShowAskHint();
    }

    private static string Describe(IncompleteFile i) =>
        $"{i.File.Name ?? "(name not received)"}" + (string.IsNullOrEmpty(i.Callsign) ? "" : $" from {i.Callsign}") +
        $"  ·  {i.File.Missing.Count} of {i.File.Pieces} missing  ·  {i.Heard:HH:mm}";

    private void OnMissingChanged(object? sender, SelectionChangedEventArgs e) => ShowAskHint();

    private void ShowAskHint()
    {
        if (_asking is not null) return;   // the request's progress is showing
        if (MissingBox.SelectedIndex < 0 || MissingBox.SelectedIndex >= _incomplete.Count) { AskText.Text = ""; return; }
        var i = _incomplete[MissingBox.SelectedIndex];
        var seconds = i.Transmitter("X", transportId: 0).Duration.TotalSeconds;
        string who = string.IsNullOrEmpty(i.Callsign) ? "The sender's" : $"{i.Callsign}'s";
        AskText.Text = $"Sends a short request ({seconds:0} s) in the settings the picture came with. {who} RFpix, EasyPal or QSSTV " +
                       "can then send just those pieces, and the picture completes here. Keep listening for them.";
    }

    private async void OnAskClick(object? sender, RoutedEventArgs e)
    {
        if (_asking is not null) { _asking.Cancel(); return; }   // the button says Stop
        if (MissingBox.SelectedIndex < 0 || MissingBox.SelectedIndex >= _incomplete.Count) return;
        var picture = _incomplete[MissingBox.SelectedIndex];
        string call = SendSupport.Callsign;
        if (SendSupport.Problem(_output, call) is { } problem) { ShowAskStatus(problem, problem: true); return; }
        if (SendSupport.IsSending) { ShowAskStatus("RFpix is already transmitting. Wait for it to finish.", problem: true); return; }

        _asking = new CancellationTokenSource();
        AskButton.Content = "Stop";
        MissingBox.IsEnabled = false;
        AskProgress.Value = 0;
        AskProgress.IsVisible = true;
        string what = $"a request for {picture.File.Missing.Count} missing pieces";
        try
        {
            await SendSupport.SendSoundAsync(() => picture.Transmitter(call).Audio(), what, _output,
                SendSupport.SavedGain, m => ShowAskStatus(m, problem: false),
                new Progress<double>(f => AskProgress.Value = f), _asking.Token, SendSupport.SavedLeadIn);
            _askedFor.Add(picture.File.TransportId);
            ShowAskStatus($"Asked for {picture.File.Missing.Count} missing pieces of {picture.File.Name ?? "the picture"}. " +
                          "If the sender answers, the picture completes here and is saved.", problem: false);
        }
        catch (OperationCanceledException)
        {
            ShowAskStatus("Stopped. The transmitter is released.", problem: false);
        }
        catch (Exception ex) when (ex is AudioException or RadioException)
        {
            ShowAskStatus(ex.Message, problem: true);
        }
        finally
        {
            _asking.Dispose();
            _asking = null;
            AskButton.Content = "Ask for them (BSR)";
            MissingBox.IsEnabled = true;
            AskProgress.IsVisible = false;
            MissingPanel.IsVisible = _incomplete.Count > 0 || AskText.Text?.Length > 0;
        }
    }

    private void ShowAskStatus(string message, bool problem)
    {
        AskText.Text = message;
        if (problem) AskText.Foreground = Brushes.OrangeRed;
        else AskText.ClearValue(TextBlock.ForegroundProperty);
    }

    // ---- messages ----

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorPanel.IsVisible = true;
    }

    private void HideError() => ErrorPanel.IsVisible = false;

    public void Dispose()
    {
        _meterTimer.Stop();
        AppSettings.SoundDevicesChanged -= OnSoundDevicesChanged;
        try { _testLog.WriteHeld(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (_recorder.IsRecording) _recorder.Stop(_recordingCallsigns);   // closing RFpix: keep what was recorded
        _asking?.Cancel();   // closing RFpix mid-request: stop (Transmission un-keys)
        _output.Dispose();
        _player.Dispose();
        _input.Dispose();
        _pump.Dispose();
    }
}
