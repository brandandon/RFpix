// SettingsPage.axaml.cs - the code half of the Settings page.
//
// The sound devices: the radio's input (what the TX/RX page listens to) and output (what
// it sends through). Each is remembered by its permanent Windows ID; a change takes effect
// at once (the TX/RX page is told through AppSettings.SoundDevicesChanged).

using Avalonia.Controls;
using Avalonia.Interactivity;
using RFpix.Audio;
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
        _input.Dispose();
        _output.Dispose();
    }
}
