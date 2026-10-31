// SettingsPage.axaml.cs - the code half of the Settings page.
//
// Radio tab: the RadioPage control, which looks after itself, and under it the radio's sound
// devices: its input (what the TX/RX page listens to) and output (what it sends through). Each
// is remembered by its permanent Windows ID; a change takes effect at once (the TX/RX page is
// told through AppSettings.SoundDevicesChanged).
// Misc tab: the callsign (used by the Send tab and the Beta Testing page), the grid square
// (%qth in templates), the waterfall text
// sent before and after each picture, the theme, and sound-card calibration: for 2 minutes the
// radio's sound input is listened to and silence played to its output, both counted against
// the computer's clock (SoundCardClock.cs); the results are saved, and used from then on.
// Also on Misc: the log (Diagnostics/AppLog.cs). Save log for help puts all the logs in one zip
// file on the desktop and shows it; Open log folder opens the folder they're kept in.

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using RFpix.Audio;
using RFpix.Diagnostics;
using RFpix.Send;
using RFpix.Settings;
using RFpix.Templates;

namespace RFpix.Views;

public partial class SettingsPage : UserControl, IDisposable
{
    private readonly IAudioInput _input = AudioSystem.CreateInput();     // only to list the devices
    private readonly IAudioOutput _output = AudioSystem.CreateOutput();
    private bool _loading;   // filling the lists, so changes aren't "the user picked"

    public SettingsPage()
    {
        InitializeComponent();
        if (OperatingSystem.IsMacOS()) UseMacWords();
        if (OperatingSystem.IsLinux()) UseLinuxWords();
        // New versions: checked for wherever RFpix was installed from a download.
        UpdatesPanel.IsVisible = RFpix.Update.UpdateCheck.InstallerPattern(OperatingSystem.IsWindows(), OperatingSystem.IsMacOS(),
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture, RFpix.Update.UpdateCheck.LinuxKind()) is not null;
        CheckForUpdatesBox.IsChecked = AppSettings.Current.CheckForUpdates;
        CheckForUpdatesBox.IsCheckedChanged += (_, _) =>
        {
            AppSettings.Current.CheckForUpdates = CheckForUpdatesBox.IsChecked == true;
            AppSettings.Current.Save();
        };

        // The sound devices go on the Radio tab, under the Test PTT note.
        ((Panel)AudioPanel.Parent!).Children.Remove(AudioPanel);
        AudioPanel.ClearValue(Grid.RowProperty);
        AudioPanel.IsVisible = true;
        RadioTab.Below.Children.Add(AudioPanel);

        // Show the saved theme: 0 = follow Windows, 1 = light, 2 = dark.
        ThemeBox.SelectedIndex = AppSettings.Current.Theme switch
        {
            "Light" => 1,
            "Dark" => 2,
            _ => 0
        };
        CallsignBox.Text = AppSettings.Current.Callsign ?? "";
        GridBox.Text = AppSettings.Current.GridSquare ?? "";
        WaterfallBeforeBox.MaxLength = WaterfallAfterBox.MaxLength = WaterfallText.MaxLength;
        WaterfallBeforeBox.Text = AppSettings.Current.WaterfallTextBefore;
        WaterfallAfterBox.Text = AppSettings.Current.WaterfallTextAfter;
        LoadDevices();
        ShowCalibration();
    }

    // ---- sound devices ----

    private void LoadDevices()
    {
        _loading = true;
        var problems = new List<string>();
        try
        {
            var inputs = _input.GetDevices();
            InputBox.ItemsSource = inputs;
            InputBox.SelectedItem = inputs.FirstOrDefault(d => d.Id == AppSettings.Current.InputDeviceId);
            if (inputs.Count == 0) problems.Add("No recording devices found. Check the radio's USB cable and power, then click Refresh.");
            else if (AppSettings.Current.InputDeviceId is not null && InputBox.SelectedItem is null)
                problems.Add("The sound input used last time isn't connected. Switch on or plug in the radio and click Refresh, or pick another.");
        }
        catch (Exception ex) { problems.Add(AudioErrors.Explain(ex)); }
        try
        {
            var outputs = _output.GetDevices();
            OutputBox.ItemsSource = outputs;
            OutputBox.SelectedItem = outputs.FirstOrDefault(d => d.Id == AppSettings.Current.OutputDeviceId);
            if (AppSettings.Current.OutputDeviceId is not null && OutputBox.SelectedItem is null)
                problems.Add("The sound output used last time isn't connected. Switch on or plug in the radio and click Refresh, or pick another.");
        }
        catch (Exception ex) { problems.Add(AudioErrors.Explain(ex)); }
        _loading = false;
        DeviceProblemText.Text = string.Join("\n", problems.Distinct());
        DeviceProblemText.IsVisible = problems.Count > 0;
        foreach (string problem in problems.Distinct()) AppLog.Error("Settings", problem);
    }

    private void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        LoadDevices();
        AppSettings.RaiseSoundDevicesChanged();   // a device that came back can be used again
    }

    private void OnInputChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || InputBox.SelectedItem is not AudioDeviceInfo device) return;
        AppSettings.Current.InputDeviceId = device.Id;
        AppSettings.Current.Save();
        AppLog.Info("Settings", $"Sound input (receive) chosen: {device.Name}");
        AppSettings.RaiseSoundDevicesChanged();
        ShowCalibration();   // a measurement belongs to one device
    }

    private void OnOutputChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || OutputBox.SelectedItem is not AudioDeviceInfo device) return;
        AppSettings.Current.OutputDeviceId = device.Id;
        AppSettings.Current.Save();
        AppLog.Info("Settings", $"Sound output (send) chosen: {device.Name}");
        AppSettings.RaiseSoundDevicesChanged();
        ShowCalibration();
    }

    // ---- callsign ----

    // Saved as it's typed, in capitals.
    private void OnCallsignChanged(object? sender, TextChangedEventArgs e)
    {
        string call = (CallsignBox.Text ?? "").Trim().ToUpperInvariant();
        if (call == (AppSettings.Current.Callsign ?? "")) return;
        AppSettings.Current.Callsign = call;
        AppSettings.Current.Save();
    }

    private void OnCallsignLostFocus(object? sender, RoutedEventArgs e) =>
        CallsignBox.Text = (CallsignBox.Text ?? "").Trim().ToUpperInvariant();

    // ---- grid square ----

    /// <summary>Saved as it's typed once it's a whole grid square (AA00 or AA00aa), written the usual way.</summary>
    private void OnGridChanged(object? sender, TextChangedEventArgs e)
    {
        if (GridProblem is null) return;   // still being built
        string typed = (GridBox.Text ?? "").Trim();
        if (typed.Length == 0)
        {
            GridProblem.Text = "";
            if (AppSettings.Current.GridSquare is not null) { AppSettings.Current.GridSquare = null; AppSettings.Current.Save(); }
            return;
        }
        if (GridSquare.Normalise(typed) is not { } grid) { GridProblem.Text = "4 or 6 characters, like AA00 or AA00aa"; return; }
        GridProblem.Text = "";
        if (grid == AppSettings.Current.GridSquare) return;
        AppSettings.Current.GridSquare = grid;
        AppSettings.Current.Save();
    }

    private void OnGridLostFocus(object? sender, RoutedEventArgs e)
    {
        if (GridSquare.Normalise(GridBox.Text ?? "") is { } grid) GridBox.Text = grid;
    }

    // ---- waterfall text ----

    private void OnWaterfallTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (WaterfallBeforeBox is null || WaterfallAfterBox is null) return;   // still being built
        string before = WaterfallBeforeBox.Text ?? "", after = WaterfallAfterBox.Text ?? "";
        if (before == AppSettings.Current.WaterfallTextBefore && after == AppSettings.Current.WaterfallTextAfter) return;
        AppSettings.Current.WaterfallTextBefore = before;
        AppSettings.Current.WaterfallTextAfter = after;
        AppSettings.Current.Save();
    }

    // ---- sound-card calibration ----

    /// <summary>The measurement time: long enough to be sure to about 1 ppm.</summary>
    private static readonly TimeSpan CalibrationTime = TimeSpan.FromMinutes(2);

    /// <summary>The most a sound card is believed to be off; more means the measurement went wrong.</summary>
    private const double MaxPpm = 1000;

    /// <summary>How sure a measurement must be, in ppm, to be used.</summary>
    private const double MaxUncertaintyPpm = 5;

    private CancellationTokenSource? _calibrating;

    private void ShowCalibration()
    {
        InputClockText.Text = Describe(AppSettings.Current.InputClock, AppSettings.Current.InputDeviceId);
        OutputClockText.Text = Describe(AppSettings.Current.OutputClock, AppSettings.Current.OutputDeviceId);
        ClearCalibrationButton.IsEnabled = _calibrating is null &&
            (AppSettings.Current.InputClock is not null || AppSettings.Current.OutputClock is not null);
    }

    private static string Describe(ClockCalibration? c, string? deviceInUse)
    {
        if (c is null) return "not measured (no correction)";
        string speed = Math.Abs(c.Ppm) < 0.05 ? "exactly right" : $"{Math.Abs(c.Ppm):0.0} ppm {(c.Ppm > 0 ? "fast" : "slow")}";
        string text = $"{speed}  ·  {c.DeviceName}, measured {c.Measured.ToLocalTime():MMM d, yyyy}";
        return c.DeviceId == deviceInUse ? text : text + "  ·  not used: a different device is chosen now";
    }

    private static double Now() => DateTime.UtcNow.Ticks / 1e7;

    private async void OnCalibrateClick(object? sender, RoutedEventArgs e)
    {
        if (_calibrating is not null) { _calibrating.Cancel(); return; }   // the button says Stop
        string? inputId = AppSettings.Current.InputDeviceId, outputId = AppSettings.Current.OutputDeviceId;
        if (inputId is null || outputId is null)
        {
            ShowCalibrationStatus("Pick the radio's sound input and output on the Radio tab first.", problem: true);
            return;
        }
        string inputName = (InputBox.SelectedItem as AudioDeviceInfo)?.Name ?? "the sound input";
        string outputName = (OutputBox.SelectedItem as AudioDeviceInfo)?.Name ?? "the sound output";

        _calibrating = new CancellationTokenSource();
        var cancel = _calibrating.Token;
        CalibrateButton.Content = "Stop";
        ClearCalibrationButton.IsEnabled = false;
        CalibrationProgress.Value = 0;
        CalibrationProgress.IsVisible = true;
        ShowCalibrationStatus("Measuring… keep RFpix open; the radio stays receiving.", problem: false);

        ClockMeasurement? input = null, output = null;
        long inputSamples = 0;
        using var listener = AudioSystem.CreateInput();
        using var player = AudioSystem.CreateOutput();
        void OnInput(object? s, AudioSamplesEventArgs a)
        {
            input ??= new ClockMeasurement(a.SampleRate);
            inputSamples += a.Count;
            input.Add(Now(), inputSamples);
        }
        listener.SamplesAvailable += OnInput;

        // Show how it's going twice a second.
        var started = DateTime.UtcNow;
        var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) =>
        {
            double done = (DateTime.UtcNow - started) / CalibrationTime;
            CalibrationProgress.Value = Math.Clamp(done, 0, 1);
            ShowCalibrationStatus($"Measuring… {Math.Max(0, (CalibrationTime - (DateTime.UtcNow - started)).TotalSeconds):0} s to go  ·  " +
                                  $"input {Sofar(input)}  ·  output {Sofar(output)}", problem: false);
        };
        timer.Start();
        try
        {
            listener.Start(inputId);
            await player.MeasureClockAsync(outputId, CalibrationTime, (t, samples, rate) =>
            {
                output ??= new ClockMeasurement(rate);
                output.Add(t, samples);
            }, cancel);
            listener.Stop();

            var results = new List<string>();
            var inResult = Result(input, inputId, inputName, "input", results);
            var outResult = Result(output, outputId, outputName, "output", results);
            if (inResult is not null) AppSettings.Current.InputClock = inResult;
            if (outResult is not null) AppSettings.Current.OutputClock = outResult;
            AppSettings.Current.Save();
            AppSettings.RaiseSoundDevicesChanged();   // the Receive tab starts using it at once
            ShowCalibrationStatus(string.Join("  ", results), problem: inResult is null || outResult is null);
        }
        catch (OperationCanceledException)
        {
            ShowCalibrationStatus("Stopped: nothing was changed.", problem: false);
        }
        catch (AudioException ex)
        {
            ShowCalibrationStatus(ex.Message, problem: true);
        }
        finally
        {
            timer.Stop();
            listener.SamplesAvailable -= OnInput;
            listener.Stop();
            _calibrating.Dispose();
            _calibrating = null;
            CalibrateButton.Content = "Measure (2 minutes)";
            CalibrationProgress.IsVisible = false;
            ShowCalibration();
        }
    }

    private static string Sofar(ClockMeasurement? m) =>
        m?.Ppm is double ppm ? $"{ppm:+0.0;-0.0} ppm" : "starting";

    /// <summary>A finished measurement, if it's believable, and a sentence about it either way.</summary>
    private static ClockCalibration? Result(ClockMeasurement? m, string deviceId, string name, string which, List<string> said)
    {
        if (m?.Ppm is not double ppm || m.UncertaintyPpm is not double sure)
        {
            said.Add($"The {which} gave no readings: is {name} still connected?");
            return null;
        }
        if (Math.Abs(ppm) > MaxPpm || sure > MaxUncertaintyPpm)
        {
            said.Add($"The {which} measurement was unsteady ({ppm:+0;-0} ppm, give or take {sure:0}), so it wasn't saved. " +
                     "Close other sound programs and try again.");
            return null;
        }
        said.Add($"{char.ToUpper(which[0])}{which[1..]}: {Math.Abs(ppm):0.0} ppm {(ppm >= 0 ? "fast" : "slow")} (give or take {sure:0.0}).");
        return new ClockCalibration(deviceId, name, Math.Round(ppm, 1), DateTime.UtcNow);
    }

    private void OnClearCalibrationClick(object? sender, RoutedEventArgs e)
    {
        AppSettings.Current.InputClock = AppSettings.Current.OutputClock = null;
        AppSettings.Current.Save();
        AppSettings.RaiseSoundDevicesChanged();
        ShowCalibration();
        ShowCalibrationStatus("No correction now.", problem: false);
    }

    private void ShowCalibrationStatus(string message, bool problem) => StatusLine.Show(CalibrationStatus, message, problem, "Calibration");

    // ---- the log (Diagnostics/AppLog.cs) ----

    // All the logs in one zip file on the desktop, shown in its folder, ready to attach to an email.
    private async void OnSaveLogClick(object? sender, RoutedEventArgs e)
    {
        SaveLogButton.IsEnabled = false;
        try
        {
            string folder = AppLog.HelpFolder();
            string zip = await Task.Run(() => AppLog.SaveForHelp(folder));
            ShowLogStatus($"Saved \"{Path.GetFileName(zip)}\" in {folder}. Attach it to an email to whoever is helping you.", problem: false);
            ShowInFolder(zip);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ShowLogStatus($"Couldn't save the log: {ex.Message}", problem: true);
        }
        finally
        {
            SaveLogButton.IsEnabled = true;
        }
    }

    private async void OnOpenLogFolderClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppLog.Folder);
            var launcher = TopLevel.GetTopLevel(this)?.Launcher;
            if (launcher is not null) await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(AppLog.Folder));
        }
        catch (Exception ex)
        {
            ShowLogStatus($"Couldn't open {AppLog.Folder}: {ex.Message}", problem: true);
        }
    }

    /// <summary>Opens the file's folder with the file picked out (Explorer, Finder, the Linux file manager).</summary>
    private void ShowInFolder(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows()) System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (OperatingSystem.IsMacOS()) RFpix.Platform.MacFiles.ShowInFinder(path);
            else if (OperatingSystem.IsLinux()) RFpix.Platform.LinuxFiles.ShowInFileManager(path);
        }
        catch (Exception)
        {
            // The message says where it is.
        }
    }

    private void ShowLogStatus(string message, bool problem) => StatusLine.Show(LogStatus, message, problem, "Settings");

    // ---- theme ----

    // Runs when a different theme is picked.
    private void OnThemeChanged(object? sender, SelectionChangedEventArgs e)
    {
        var theme = ThemeBox.SelectedIndex switch
        {
            1 => "Light",
            2 => "Dark",
            _ => "System"
        };

        App.ApplyTheme(theme);
        AppSettings.Current.Theme = theme;
        AppSettings.Current.Save();
    }

    public void Dispose()
    {
        _calibrating?.Cancel();
        RadioTab.Dispose();
        FtpTab.Dispose();
        _input.Dispose();
        _output.Dispose();
    }

    // The hints name Windows' settings; on the Mac, the Mac's own.
    private void UseMacWords()
    {
        FollowSystemItem.Content = "Follow the Mac";
        CalibrationHint.Text = CalibrationHint.Text?.Replace(
            "first make sure Windows' clock is set right (Settings > Time & language > Date & time > Sync now)",
            "first make sure the Mac's clock is set right (System Settings > General > Date & Time > Set time and date automatically)");
        DeviceHint.Text = DeviceHint.Text?.Replace("Renaming them in Windows Sound settings", "Renaming them in Audio MIDI Setup");
    }

    // ... and on Linux, Linux's.
    private void UseLinuxWords()
    {
        FollowSystemItem.Content = "Follow the system";
        CalibrationHint.Text = CalibrationHint.Text?.Replace(
            "first make sure Windows' clock is set right (Settings > Time & language > Date & time > Sync now)",
            "first make sure the computer's clock is set right (most Linux systems set it from the internet: check Date & Time in the system settings)");
        DeviceHint.Text = "Radios usually appear as \"USB Audio CODEC\" (\"PCM2901 Audio Codec\" on some). Not your computer's own speakers or " +
                          "microphone. If the radio isn't listed, check it's switched on and plugged in, then click Refresh.";
    }
}
