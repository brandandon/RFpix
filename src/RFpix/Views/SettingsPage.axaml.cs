// SettingsPage.axaml.cs - the code half of the Settings page.
//
// Audio tab: the radio's input (what the TX/RX page listens to) and output (what it sends
// through). Each is remembered by its permanent Windows ID; a change takes effect at once
// (the TX/RX page is told through AppSettings.SoundDevicesChanged).
// Radio tab: the RadioPage control, which looks after itself.
// Misc tab: the callsign (used by the Send tab and the Alpha Testing page), the waterfall text
// sent before and after each picture, and the theme.

using Avalonia.Controls;
using Avalonia.Interactivity;
using RFpix.Audio;
using RFpix.Send;
using RFpix.Settings;

namespace RFpix.Views;

public partial class SettingsPage : UserControl, IDisposable
{
    private readonly IAudioInput _input = AudioSystem.CreateInput();     // only to list the devices
    private readonly IAudioOutput _output = AudioSystem.CreateOutput();
    private bool _loading;   // filling the lists, so changes aren't "the user picked"

    public SettingsPage()
    {
        InitializeComponent();

        // Show the saved theme: 0 = follow Windows, 1 = light, 2 = dark.
        ThemeBox.SelectedIndex = AppSettings.Current.Theme switch
        {
            "Light" => 1,
            "Dark" => 2,
            _ => 0
        };
        CallsignBox.Text = AppSettings.Current.Callsign ?? "";
        WaterfallBeforeBox.MaxLength = WaterfallAfterBox.MaxLength = WaterfallText.MaxLength;
        WaterfallBeforeBox.Text = AppSettings.Current.WaterfallTextBefore;
        WaterfallAfterBox.Text = AppSettings.Current.WaterfallTextAfter;
        LoadDevices();
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
        AppSettings.RaiseSoundDevicesChanged();
    }

    private void OnOutputChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || OutputBox.SelectedItem is not AudioDeviceInfo device) return;
        AppSettings.Current.OutputDeviceId = device.Id;
        AppSettings.Current.Save();
        AppSettings.RaiseSoundDevicesChanged();
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
        RadioTab.Dispose();
        _input.Dispose();
        _output.Dispose();
    }
}
