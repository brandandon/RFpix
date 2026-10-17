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

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.Input;
using Avalonia.VisualTree;
using RFpix.Audio;
using RFpix.Dsp;
using RFpix.HamDrm;
using RFpix.Imaging;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System.Runtime.InteropServices;
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
            Dispatcher.UIThread.Post(() => ShowReceivedFile(file, callsign));
        };

        // When a transmission ends: if it was one of RFpix's test transmissions, say how it
        // came through and add it to the test log.
        _receiver.TransmissionReported += report =>
        {
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

    // The device was changed (or Refresh clicked) on the Settings page: switch over if listening.
    private void OnSoundDevicesChanged()
    {
        if (_input.IsRunning && _listeningTo != AppSettings.Current.InputDeviceId) StartListening();
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
                ? "Pick the radio's sound input on the Settings page first."
                : "The sound input chosen on the Settings page isn't connected. Switch on or plug in the radio, or pick another device there.");
            return;
        }

        StopPlayback();   // the radio and a recording can't both feed the waterfall

        try
        {
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
        TransmissionText.Text = "";
        FileText.Text = "";
        // Start fresh next time, so a signal from one source isn't carried over to another.
        // (Through the pump, so it happens after any audio still waiting.)
        _pump.Post(_receiver.Reset);
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

    // On the sound card's (or player's) thread: just hand the chunk over and return.
    private void OnSamples(object? sender, AudioSamplesEventArgs e) =>
        _pump.Add(e.Samples, e.Count, e.SampleRate, sender);

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
        // ...and to the HamDRM receiver.
        _receiver.AddSamples(e.Samples, e.Count, e.SampleRate);
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

        SignalText.Text = _receiver.StatusText;
        TransmissionText.Text = _receiver.TransmissionText;
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
        PointerFrequencyText.Text = $"{hz:0} Hz";
    }

    private void OnWaterfallPointerExited(object? sender, PointerEventArgs e)
    {
        PointerFrequencyText.Text = "";
    }

    // ---- received files ----

    private static string ReceivedFolder => AppSettings.Current.ReceivedFolder ?? ReceivedFileSaver.DefaultFolder;

    // Counts received pictures, so a slow decode can't replace a newer picture on the screen.
    private int _pictureCount;

    private async void ShowReceivedFile(ReceivedFile file, string? callsign)
    {
        string from = string.IsNullOrEmpty(callsign) ? "" : $" from {callsign}";
        string savedAs;
        try
        {
            savedAs = Path.GetFileName(ReceivedFileSaver.Save(file, ReceivedFolder, callsign, DateTime.Now));
        }
        catch (Exception ex)
        {
            ShowError($"Received {file.Name}{from}, but couldn't save it to {ReceivedFolder}: {ex.Message}");
            return;
        }
        if (file.Problem is not null) ShowError($"{file.Name}: {file.Problem}");
        string caption = $"{file.Name}{from}  ·  {DateTime.Now:HH:mm}\nSaved as \"{savedAs}\"";
        int number = ++_pictureCount;

        // JPEG 2000 is decoded by RFpix (in the background: a big picture takes a moment);
        // anything else is shown if Avalonia can draw it (JPEG, PNG, BMP, GIF).
        Bitmap? bitmap = null;
        string? why = null;
        if (Jpeg2000.IsJpeg2000(file.Data))
        {
            try
            {
                var picture = await Task.Run(() => Jpeg2000.Decode(file.Data));
                bitmap = ToBitmap(picture);
            }
            catch (Exception ex)
            {
                why = ex is PictureException ? ex.Message : "This JPEG 2000 picture can't be shown.";
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

    /// <summary>Copies decoded pixels into a picture Avalonia can show.</summary>
    private static WriteableBitmap ToBitmap(DecodedPicture picture)
    {
        var bitmap = new WriteableBitmap(new Avalonia.PixelSize(picture.Width, picture.Height), new Avalonia.Vector(96, 96),
                                         PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using var buffer = bitmap.Lock();
        int rowBytes = picture.Width * 4;
        for (int y = 0; y < picture.Height; y++)
            Marshal.Copy(picture.Bgra, y * rowBytes, buffer.Address + y * buffer.RowBytes, rowBytes);
        return bitmap;
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
        _player.Dispose();
        _input.Dispose();
        _pump.Dispose();
    }
}
