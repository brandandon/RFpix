// SendPage.axaml.cs - the code half of the Send page.
//
// What happens here:
//   1. The test transmissions are listed, each with how long it takes; picking one
//      shows its picture.
//   2. The callsign, sound output and level are remembered between runs.
//   3. Send: make the HamDRM sound (in the background, about a second), connect to
//      the radio if the Radio page hasn't already (and keying needs it), then key,
//      play and un-key (Transmission.SendAsync). Send becomes Stop meanwhile, and the
//      level slider still works: the sound follows it as it plays.
//   4. Save as sound file: the same sound, written to a WAV file instead.

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using RFpix.Audio;
using RFpix.HamDrm;
using RFpix.Radio;
using RFpix.Send;
using RFpix.Settings;

namespace RFpix.Views;

/// <summary>One line of the test list.</summary>
public sealed record TestListItem(TestTransmission Test, string Title, string Detail);

public partial class SendPage : UserControl, IDisposable
{
    private readonly IAudioOutput _output = AudioSystem.CreateOutput();
    private CancellationTokenSource? _sending;   // set while sending; cancelling it = Stop
    private bool _loading;
    private double _gain;   // the slider's level as a multiplier, read by the sound as it plays

    public SendPage()
    {
        InitializeComponent();
        _loading = true;
        TestList.ItemsSource = TestTransmissions.All.Select(t => new TestListItem(t,
            $"Test {t.Number}: {t.Purpose}", $"{t.SettingsText}  ·  {Duration(t)}")).ToList();
        CallsignBox.Text = AppSettings.Current.Callsign ?? "";
        LevelSlider.Value = Math.Clamp(Math.Round(AppSettings.Current.TransmitLevelDb), LevelSlider.Minimum, 0);
        LevelText.Text = LevelLabel;
        _gain = Gain;
        LoadDevices();
        _loading = false;
        TestList.SelectedIndex = 0;
    }

    /// <summary>How long a test takes on the air, e.g. "41 s".</summary>
    private static string Duration(TestTransmission t)
    {
        var tx = t.Transmitter("TEST");
        return $"{tx.Duration.TotalSeconds:0} s";
    }

    private TestTransmission? Selected => (TestList.SelectedItem as TestListItem)?.Test;

    private void OnTestChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Selected is not { } test) return;
        using var stream = new MemoryStream(test.Picture());
        var old = Preview.Source as IDisposable;
        Preview.Source = new Bitmap(stream);
        old?.Dispose();
    }

    // ---- remembered choices ----

    private void LoadDevices()
    {
        try
        {
            var devices = _output.GetDevices();
            OutputBox.ItemsSource = devices;
            OutputBox.SelectedItem = devices.FirstOrDefault(d => d.Id == AppSettings.Current.OutputDeviceId);
        }
        catch (AudioException ex)
        {
            OutputBox.ItemsSource = null;
            ShowStatus(ex.Message, problem: true);
        }
    }

    private void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        _loading = true;
        LoadDevices();
        _loading = false;
    }

    private void OnOutputChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || OutputBox.SelectedItem is not AudioDeviceInfo device) return;
        AppSettings.Current.OutputDeviceId = device.Id;
        AppSettings.Current.Save();
    }

    private void OnLevelChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (LevelText is null) return;   // still being built
        LevelText.Text = LevelLabel;
        Volatile.Write(ref _gain, Gain);   // a transmission in progress follows the slider at once
        if (_loading) return;
        AppSettings.Current.TransmitLevelDb = LevelSlider.Value;
        AppSettings.Current.Save();
    }

    private string LevelLabel => LevelSlider.Value == 0 ? "0 dB" : $"{LevelSlider.Value:0} dB".Replace('-', '\u2212');

    /// <summary>The level as a multiplier: 0 dB = 1, -20 dB = 0.1.</summary>
    private double Gain => Math.Pow(10, LevelSlider.Value / 20);

    private void OnCallsignLostFocus(object? sender, RoutedEventArgs e) => SaveCallsign();

    private string SaveCallsign()
    {
        string call = (CallsignBox.Text ?? "").Trim().ToUpperInvariant();
        CallsignBox.Text = call;
        if (call != (AppSettings.Current.Callsign ?? ""))
        {
            AppSettings.Current.Callsign = call;
            AppSettings.Current.Save();
        }
        return call;
    }

    // ---- sending ----

    private async void OnSendClick(object? sender, RoutedEventArgs e)
    {
        if (_sending is not null)   // the button says Stop
        {
            _sending.Cancel();
            ShowStatus("Stopping…", problem: false);
            return;
        }
        if (Selected is not { } test) return;
        string call = SaveCallsign();
        if (call.Length == 0) { ShowStatus("Type your callsign first: it's sent with the transmission.", problem: true); return; }
        if (OutputBox.SelectedItem is not AudioDeviceInfo device) { ShowStatus("Pick the sound output that goes to the radio.", problem: true); return; }

        var radioSettings = AppSettings.Current.Radio;
        bool hasControl = RadioChoices.Find(radioSettings.Choice).Connection != "none";
        bool keyed = radioSettings.Ptt != PttMethod.Vox;
        if (keyed && !hasControl)
        {
            ShowStatus("Keying is set to CAT, RTS or DTR but no radio is chosen. Pick your radio on the Radio page, or choose VOX there.", problem: true);
            return;
        }

        _sending = new CancellationTokenSource();
        var cancel = _sending.Token;
        SendButton.Content = "Stop";
        SaveButton.IsEnabled = TestList.IsEnabled = false;
        Progress.Value = 0;
        Progress.IsVisible = true;
        try
        {
            ShowStatus("Preparing the transmission…", problem: false);
            var (sound, seconds) = await Task.Run(() =>
            {
                var tx = test.Transmitter(call);
                return (Transmission.Prepare(tx.Audio(), 1, keyed), tx.Duration.TotalSeconds);
            });

            // Connect if keying or data mode needs the radio and the Radio page hasn't connected.
            var radio = RadioService.Current;
            if (radio is null && hasControl)
            {
                ShowStatus("Connecting to the radio…", problem: false);
                radio = await RadioService.ConnectAsync(radioSettings);
            }

            ShowStatus($"Transmitting test {test.Number} ({seconds:0} s)…", problem: false);
            var progress = new Progress<double>(f =>
            {
                Progress.Value = f;
                ShowStatus($"Transmitting test {test.Number}: {f * seconds:0} of {seconds:0} s", problem: false);
            });
            await Transmission.SendAsync(sound, _output, device.Id, radio, () => Volatile.Read(ref _gain), progress, cancel);
            ShowStatus($"Sent test {test.Number} as {call}.", problem: false);
        }
        catch (OperationCanceledException)
        {
            ShowStatus("Stopped. The transmitter is released.", problem: false);
        }
        catch (Exception ex) when (ex is AudioException or RadioException)
        {
            ShowStatus(ex.Message, problem: true);
        }
        finally
        {
            _sending.Dispose();
            _sending = null;
            SendButton.Content = "Send";
            SaveButton.IsEnabled = TestList.IsEnabled = true;
            Progress.IsVisible = false;
        }
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (Selected is not { } test || TopLevel.GetTopLevel(this) is not { } top) return;
        string call = SaveCallsign();
        if (call.Length == 0) { ShowStatus("Type your callsign first: it's sent with the transmission.", problem: true); return; }

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save the transmission as a sound file",
            SuggestedFileName = $"rfpix-test-{test.Number}.wav",
            DefaultExtension = "wav",
            FileTypeChoices = new[] { new FilePickerFileType("WAV sound file") { Patterns = new[] { "*.wav" } } },
        });
        if (file?.TryGetLocalPath() is not { } path) return;

        try
        {
            ShowStatus("Making the sound file…", problem: false);
            double level = Gain;
            await Task.Run(() =>
            {
                var tx = test.Transmitter(call);
                var sound = Transmission.Prepare(tx.Audio(), level, keyed: false);
                using var wav = new WavWriter(path, Transmission.SampleRate);
                wav.Write(sound);
            });
            ShowStatus($"Saved {Path.GetFileName(path)}.", problem: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowStatus($"Couldn't save the sound file: {ex.Message}", problem: true);
        }
    }

    private void ShowStatus(string message, bool problem)
    {
        StatusText.Text = message;
        if (problem) StatusText.Foreground = Brushes.OrangeRed;
        else StatusText.ClearValue(TextBlock.ForegroundProperty);
    }

    public void Dispose()
    {
        _sending?.Cancel();   // closing RFpix mid-send: stop (Transmission un-keys)
        _output.Dispose();
    }
}
