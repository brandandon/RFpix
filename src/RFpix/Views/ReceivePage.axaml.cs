// ReceivePage.axaml.cs - the code half of the Receive page.
//
// What happens here:
//   1. Fill the drop-down with the computer's recording devices.
//   2. When you press Start listening, open the chosen device through the audio layer.
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
//      if it's a picture, shown on the right.

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.Input;
using RFpix.Audio;
using RFpix.Dsp;
using RFpix.HamDrm;
using Avalonia.Media.Imaging;
using RFpix.Receive;
using RFpix.Settings;

namespace RFpix.Views;

public partial class ReceivePage : UserControl, IDisposable
{
    private readonly IAudioInput _input = AudioSystem.CreateInput();
    private readonly WavFileAudioInput _player = new();
    private readonly DispatcherTimer _meterTimer;
    private readonly SpectrumAnalyzer _analyzer = new(maxFrequency: 3000);
    private readonly HamDrmReceiver _receiver = new();

    // The latest loudness measurement, written by the audio thread and read by the timer.
    private volatile float _latestRms;

    // True while we fill the drop-down, so that doesn't count as "you picked a device".
    private bool _loadingDevices;

    public ReceivePage()
    {
        InitializeComponent();

        _input.SamplesAvailable += OnSamples;
        _input.Failed += OnAudioFailed;

        // The recording player feeds the same handler as the radio.
        _player.SamplesAvailable += OnSamples;
        _player.Failed += OnAudioFailed;
        _player.Finished += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            string name = _player.FileName ?? "the recording";
            StopPlayback();
            FormatText.Text = $"Finished playing {name}";
        });

        // A finished file arrives on the audio thread; save and show it on the screen's thread.
        _receiver.FileReceived += file =>
        {
            string? callsign = _receiver.Callsign;
            Dispatcher.UIThread.Post(() => ShowReceivedFile(file, callsign));
        };

        _meterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _meterTimer.Tick += (_, _) => UpdateMeter();

        // The analyzer works on the audio thread; the waterfall must be updated
        // on the screen's thread, so each finished line is "posted" across.
        _analyzer.LineReady += (line, hzPerStep) =>
            Dispatcher.UIThread.Post(() => Waterfall.AddLine(line, hzPerStep));

        LoadDevices();
    }

    // ---- device list ----

    private void LoadDevices()
    {
        _loadingDevices = true;
        try
        {
            var devices = _input.GetDevices();
            DeviceBox.ItemsSource = devices;

            var savedId = AppSettings.Current.InputDeviceId;
            DeviceBox.SelectedItem = devices.FirstOrDefault(d => d.Id == savedId);

            if (devices.Count == 0)
                ShowError("No recording devices found. Check the radio's USB cable and power, then click Refresh.");
            else if (savedId is not null && DeviceBox.SelectedItem is null)
                ShowError("The sound device you used last time isn't connected. Switch on or plug in the radio and click Refresh, or pick another device.");
            else
                HideError();
        }
        catch (Exception ex)
        {
            ShowError(AudioErrors.Explain(ex));
        }
        finally
        {
            _loadingDevices = false;
        }
        UpdateStartButton();
    }

    private void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        bool wasRunning = _input.IsRunning;
        StopListening();
        LoadDevices();
        if (wasRunning) StartListening();
    }

    private void OnDeviceChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingDevices || DeviceBox.SelectedItem is not AudioDeviceInfo device) return;

        // Remember the choice by its permanent ID.
        AppSettings.Current.InputDeviceId = device.Id;
        AppSettings.Current.Save();

        // Already listening? Switch straight over to the new device.
        if (_input.IsRunning) StartListening();
        UpdateStartButton();
    }

    // ---- start / stop ----

    private void OnStartStopClick(object? sender, RoutedEventArgs e)
    {
        if (_input.IsRunning) StopListening();
        else StartListening();
    }

    private void StartListening()
    {
        if (DeviceBox.SelectedItem is not AudioDeviceInfo device)
        {
            ShowError("Pick the radio's sound device first.");
            return;
        }

        StopPlayback();   // the radio and a recording can't both feed the waterfall

        try
        {
            _input.Start(device.Id);
            HideError();
            FormatText.Text = _input.FormatDescription;
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
        _input.Stop();
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
        _receiver.Reset();
    }

    private void UpdateStartButton()
    {
        StartButton.Content = _input.IsRunning ? "Stop" : "Start listening";
        StartButton.IsEnabled = _input.IsRunning || DeviceBox.SelectedItem is not null;
        PlayButton.Content = _player.IsRunning ? "Stop playback" : "Play recording…";
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

    // ---- audio arriving (runs on the audio thread, so keep it quick) ----

    private void OnSamples(object? sender, AudioSamplesEventArgs e)
    {
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

    private void ShowReceivedFile(ReceivedFile file, string? callsign)
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

        // Show it if it's a picture Avalonia can draw (JPEG, PNG, BMP, GIF).
        try
        {
            using var stream = new MemoryStream(file.Data);
            var old = PictureImage.Source as IDisposable;
            PictureImage.Source = new Bitmap(stream);
            old?.Dispose();
            PicturePlaceholder.IsVisible = false;
            PictureCaption.Text = $"{file.Name}{from}  ·  {DateTime.Now:HH:mm}\nSaved as \"{savedAs}\"";
        }
        catch
        {
            string why = Path.GetExtension(file.Name).ToLowerInvariant() is ".jp2" or ".j2k"
                ? "JPEG 2000 pictures can't be shown here yet"
                : "it isn't a picture RFpix can show";
            PictureCaption.Text = $"{file.Name}{from}  ·  {DateTime.Now:HH:mm}\nSaved as \"{savedAs}\" ({why})";
        }
        if (file.Problem is not null) ShowError($"{file.Name}: {file.Problem}");
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
        _player.Dispose();
        _input.Dispose();
    }
}
