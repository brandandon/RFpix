// RadioPage.axaml.cs - the code half of the Radio page.
//
// What happens here:
//   1. The radio list, COM ports and baud rates are filled in, and the saved settings shown.
//   2. Picking a radio fills in the settings that suit it (e.g. the IC-9700's CI-V address).
//   3. Connect saves the settings and opens the radio through Hamlib (in the background:
//      a radio that doesn't answer can take a few seconds to time out). The connection
//      belongs to RadioService, so the Send page uses it too (and may open it itself).
//   4. While connected, the frequency and mode are read once a second.
//   5. Test PTT keys the transmitter for 2 seconds (switching to data mode first if that's
//      ticked) and then releases it, reporting each step.
//   6. The IC-9700's data-mode box is saved and used the moment it's changed, connected or not.
//      Everything else is saved by Connect, and can't change while connected.

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using RFpix.Radio;
using RFpix.Settings;

namespace RFpix.Views;

public partial class RadioPage : UserControl, IDisposable
{
    private static RadioConnection? Connection => RadioService.Current;
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _busy;       // a radio command is running in the background
    private bool _loading;    // filling in the controls, so changes aren't "the user picked"

    public RadioPage()
    {
        InitializeComponent();
        RadioBox.ItemsSource = RadioChoices.All;
        ModelBox.ItemsSource = HamlibModels.All();   // every radio Hamlib knows, for "Other radio"
        BaudBox.ItemsSource = RadioChoices.BaudRates;
        _pollTimer.Tick += async (_, _) => await PollAsync();
        RadioService.Changed += OnConnectionChanged;
        RadioService.KeyedTooLong += OnKeyedTooLong;
        LoadPorts();
        ShowSettings(AppSettings.Current.Radio);
    }

    // ---- showing and reading the settings ----

    private void ShowSettings(RadioSettings s)
    {
        _loading = true;
        RadioBox.SelectedItem = RadioChoices.Find(s.Choice);
        var model = s.OtherModel > 0 ? HamlibModels.Find(s.OtherModel) : null;
        ModelBox.SelectedItem = model;
        ModelBox.Text = model?.ToString() ?? (s.OtherModel > 0 ? s.OtherModel.ToString() : "");
        BaudBox.SelectedItem = RadioChoices.BaudRates.Contains(s.BaudRate) ? s.BaudRate : 38400;
        CivBox.Text = s.CivAddress ?? "";
        var choice = RadioChoices.Find(s.Choice);
        if (choice.Connection == "network") AddressBox.Text = s.Port ?? "";
        else SelectPort(s.Port);
        ShowPtt(s.Ptt);
        DataModeBox.IsChecked = s.DataModeForTransmit;
        _loading = false;
        UpdateVisibility();
    }

    private RadioSettings ReadSettings()
    {
        var choice = (RadioBox.SelectedItem as RadioChoice) ?? RadioChoices.Find("none");
        return new RadioSettings
        {
            Choice = choice.Id,
            OtherModel = ModelBox.SelectedItem is HamlibModel picked ? picked.Number : HamlibModels.NumberFrom(ModelBox.Text) ?? 0,
            Port = choice.Connection == "network" ? AddressBox.Text?.Trim() : (PortBox.SelectedItem as SerialPortInfo)?.Name,
            BaudRate = BaudBox.SelectedItem is int baud ? baud : 38400,
            CivAddress = string.IsNullOrWhiteSpace(CivBox.Text) ? null : CivBox.Text.Trim(),
            Ptt = PttVox.IsChecked == true ? PttMethod.Vox
                : PttRts.IsChecked == true ? PttMethod.Rts
                : PttDtr.IsChecked == true ? PttMethod.Dtr
                : PttMethod.Cat,
            DataModeForTransmit = DataModeBox.IsChecked == true && choice.Id == "ic9700",
        };
    }

    private void ShowPtt(PttMethod ptt)
    {
        PttCat.IsChecked = ptt == PttMethod.Cat;
        PttRts.IsChecked = ptt == PttMethod.Rts;
        PttDtr.IsChecked = ptt == PttMethod.Dtr;
        PttVox.IsChecked = ptt == PttMethod.Vox;
    }

    // Picking a radio fills in what suits it.
    private void OnRadioChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || RadioBox.SelectedItem is not RadioChoice choice) return;
        if (choice.BaudRate > 0) BaudBox.SelectedItem = choice.BaudRate;
        CivBox.Text = choice.CivAddress ?? "";
        ShowPtt(choice.Ptt);
        DataModeBox.IsChecked = choice.DataMode;
        if (choice.Connection == "network")
            AddressBox.Text = choice.Id == "flrig" ? "localhost:12345" : "localhost:4532";
        UpdateVisibility();
    }

    // The data-mode box counts at once: saved, and used by the open connection from its next transmission.
    private void OnDataModeChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading || StatusText is null) return;
        bool on = DataModeBox.IsChecked == true;
        AppSettings.Current.Radio.DataModeForTransmit = on;
        AppSettings.Current.Save();
        if (Connection is { } radio)
        {
            radio.DataModeForTransmit = on;
            ShowStatus(on ? "Data mode switching is on, from the next transmission." : "Data mode switching is off.", problem: false);
        }
    }

    private void UpdateVisibility()
    {
        var choice = (RadioBox.SelectedItem as RadioChoice) ?? RadioChoices.Find("none");
        RadioHint.Text = choice.Hint;
        ModelPanel.IsVisible = choice.Id == "other";
        SerialPanel.IsVisible = choice.Connection == "serial";
        CivPanel.IsVisible = choice.Id is "ic9700" or "other";
        NetworkPanel.IsVisible = choice.Connection == "network";
        // RTS and DTR are lines on a COM port; without radio control only VOX is possible.
        PttRts.IsEnabled = PttDtr.IsEnabled = choice.Connection == "serial";
        PttCat.IsEnabled = choice.Connection != "none";
        DataModeBox.IsVisible = choice.Id == "ic9700";   // data mode switching is for the IC-9700 only
        ConnectButton.IsEnabled = choice.Connection != "none" || Connection is not null;
        if (choice.Connection == "none") { ShowPtt(PttMethod.Vox); DataModeBox.IsChecked = false; }
    }

    private void LoadPorts()
    {
        string? selected = (PortBox.SelectedItem as SerialPortInfo)?.Name ?? AppSettings.Current.Radio.Port;
        PortBox.ItemsSource = SerialPorts.List();
        SelectPort(selected);
    }

    private void SelectPort(string? name) =>
        PortBox.SelectedItem = (PortBox.ItemsSource as IEnumerable<SerialPortInfo>)?.FirstOrDefault(p => p.Name == name);

    private void OnRefreshPortsClick(object? sender, RoutedEventArgs e) => LoadPorts();

    // ---- connecting ----

    private async void OnConnectClick(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (Connection is not null)
        {
            await DisconnectAsync("Disconnected.");
            return;
        }

        var settings = ReadSettings();
        AppSettings.Current.Radio = settings;
        AppSettings.Current.Save();

        _busy = true;
        ConnectButton.IsEnabled = false;
        ShowStatus("Connecting…", problem: false);
        try
        {
            await RadioService.ConnectAsync(settings);
            ShowStatus($"Connected to the {RadioChoices.Find(settings.Choice).Name}.", problem: false);
        }
        catch (RadioException ex)
        {
            ShowStatus(ex.Message, problem: true);
        }
        finally
        {
            _busy = false;
            UpdateButtons();
        }
        await PollAsync();
    }

    private async Task DisconnectAsync(string message)
    {
        _pollTimer.Stop();
        await RadioService.DisconnectAsync();   // releases PTT, restores the mode
        FrequencyText.Text = "";
        ShowStatus(message, problem: false);
        UpdateButtons();
    }

    // The connection opened or closed, here or on another page (Send connects by itself).
    private void OnConnectionChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (Connection is not null)
        {
            if (!_pollTimer.IsEnabled) _pollTimer.Start();
        }
        else
        {
            _pollTimer.Stop();
            FrequencyText.Text = "";
        }
        UpdateButtons();
    });

    private void OnKeyedTooLong() => Dispatcher.UIThread.Post(() =>
        ShowStatus("The transmit time limit was reached, so RFpix released the transmitter.", problem: true));

    private void UpdateButtons()
    {
        bool connected = Connection is not null;
        ConnectButton.Content = connected ? "Disconnect" : "Connect";
        ConnectButton.IsEnabled = !_busy && (connected || (RadioBox.SelectedItem as RadioChoice)?.Connection != "none");
        TestPttButton.IsEnabled = connected && !_busy;
        // The settings can't change under an open connection.
        RadioBox.IsEnabled = SerialPanel.IsEnabled = NetworkPanel.IsEnabled = ModelPanel.IsEnabled = PttPanel.IsEnabled = !connected;
    }

    // ---- frequency and mode, once a second ----

    private async Task PollAsync()
    {
        if (_busy || Connection is not { } radio) return;
        _busy = true;
        try
        {
            var (hz, mode) = await Task.Run(() => (radio.ReadFrequency(), radio.ReadMode()));
            FrequencyText.Text = $"{RadioConnection.FormatFrequency(hz)}   {RadioConnection.ModeName(mode)}";
        }
        catch (RadioException ex)
        {
            _busy = false;
            await DisconnectAsync("Lost contact with the radio. " + ex.Message);
            ShowStatus("Lost contact with the radio. " + ex.Message, problem: true);
        }
        finally
        {
            _busy = false;
            UpdateButtons();
        }
    }

    // ---- the PTT test ----

    private async void OnTestPttClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || Connection is not { } radio) return;
        var settings = AppSettings.Current.Radio;
        _busy = true;
        UpdateButtons();
        try
        {
            ShowStatus(settings.Ptt == PttMethod.Vox
                ? "VOX: RFpix sends no key command" + (radio.DataModeForTransmit ? ", only switching data mode for 2 seconds…" : ". To check VOX trips, send a test from the Alpha Testing page.")
                : "Transmitting for 2 seconds…", problem: false);

            // Key with a 5-second limit: even if something below went wrong, the radio is released.
            await Task.Run(() => radio.Key(TimeSpan.FromSeconds(5)));
            await Task.Delay(TimeSpan.FromSeconds(2));
            await Task.Run(radio.Unkey);

            string modes = radio.DataModeForTransmit && radio.LastDataModeReport.Length > 0
                ? $" Data mode: {radio.LastDataModeReport}." : "";
            ShowStatus(settings.Ptt == PttMethod.Vox
                ? $"Done.{modes}"
                : $"Done: keyed for 2 seconds and released.{modes} Did the radio show TX?", problem: false);
        }
        catch (RadioException ex)
        {
            ShowStatus(ex.Message, problem: true);
            try { await Task.Run(radio.Unkey); } catch (RadioException) { }
        }
        finally
        {
            _busy = false;
            UpdateButtons();
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
        _pollTimer.Stop();
        RadioService.Changed -= OnConnectionChanged;
        RadioService.KeyedTooLong -= OnKeyedTooLong;
        // MainWindow closes the connection itself (RadioService.Close) when RFpix closes.
    }
}
