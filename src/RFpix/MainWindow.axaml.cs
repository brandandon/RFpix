// MainWindow.axaml.cs - the code half of the main window.
// It switches pages when you click an item in the sidebar, and remembers the window's size
// and position (and whether it was maximized) from one run to the next. The first time,
// it opens at the size in MainWindow.axaml (1200 x 760), centred.

using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using RFpix.Settings;
using RFpix.Views;

namespace RFpix;

public partial class MainWindow : Window
{
    // Each page is created the first time it is opened, then kept in place (hidden when another
    // is showing), so switching back to a page is instant and doesn't lose what was on it.
    private readonly Dictionary<string, Control> _pages = new();
    private readonly Dictionary<string, RFpix.Controls.FitToWindow> _fits = new();

    // The window's last normal (not maximized or minimized) size and position, kept up to date
    // so it can be saved even if RFpix is closed while maximized.
    private WindowPlacement? _normal;

    public MainWindow()
    {
        InitializeComponent();   // builds everything described in MainWindow.axaml

        // "0.5.2-beta" is shown as "0.5.2 beta".
        var assembly = Assembly.GetExecutingAssembly();
        string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
                         ?? assembly.GetName().Version?.ToString(3) ?? "";
        VersionText.Text = $"Version {version.Replace('-', ' ')}";

        if (OperatingSystem.IsMacOS()) RepairMacDeviceChoices();
        NavList.SelectedIndex = 0;   // start on the first page (TX/RX, on its Receive tab)
        RestorePlacement();
        GalleryPage.SendRequested += OnGallerySend;
        RFpix.Upload.UploadService.Restart();   // uploading pictures to a website, if that's set up
        _ = CheckForUpdateAsync(version);
    }

    // ---- the Mac: sound devices saved garbled by 0.5.2 to 0.6.0 (see Audio/DeviceIdRepair.cs) ----

    private static void RepairMacDeviceChoices()
    {
        var s = AppSettings.Current;
        if (!new[] { s.InputDeviceId, s.OutputDeviceId, s.InputClock?.DeviceId, s.OutputClock?.DeviceId }.Any(RFpix.Audio.DeviceIdRepair.LooksGarbled))
            return;
        try
        {
            using var input = RFpix.Audio.AudioSystem.CreateInput();
            using var output = RFpix.Audio.AudioSystem.CreateOutput();
            var inputs = input.GetDevices();
            var outputs = output.GetDevices();
            // A choice that can't be matched (that device isn't plugged in now) is cleared, to be picked again.
            string? Mend(string? id, IReadOnlyList<RFpix.Audio.AudioDeviceInfo> devices) =>
                RFpix.Audio.DeviceIdRepair.LooksGarbled(id) ? RFpix.Audio.DeviceIdRepair.Find(id!, devices)?.Id : id;
            ClockCalibration? MendClock(ClockCalibration? c, IReadOnlyList<RFpix.Audio.AudioDeviceInfo> devices)
            {
                if (c is null || !RFpix.Audio.DeviceIdRepair.LooksGarbled(c.DeviceId)) return c;
                var device = RFpix.Audio.DeviceIdRepair.Find(c.DeviceId, devices);
                return device is null ? null : c with { DeviceId = device.Id, DeviceName = device.Name };
            }
            s.InputDeviceId = Mend(s.InputDeviceId, inputs);
            s.OutputDeviceId = Mend(s.OutputDeviceId, outputs);
            s.InputClock = MendClock(s.InputClock, inputs);
            s.OutputClock = MendClock(s.OutputClock, outputs);
            s.Save();
        }
        catch (Exception) { /* the sound system isn't answering: leave them for now */ }
    }

    // ---- is there a newer RFpix? ----

    private RFpix.Update.UpdateInfo? _update;

    private async Task CheckForUpdateAsync(string version)
    {
        string? linux = RFpix.Update.UpdateCheck.LinuxKind();
        string? installer = RFpix.Update.UpdateCheck.InstallerPattern(OperatingSystem.IsWindows(), OperatingSystem.IsMacOS(),
                                                                       System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture, linux);
        if (installer is null || !AppSettings.Current.CheckForUpdates || RFpix.Update.AppVersion.Parse(version) is not { } running) return;
        await Task.Delay(TimeSpan.FromSeconds(5));   // let RFpix finish starting first
        _update = await RFpix.Update.UpdateCheck.CheckAsync(running, installer);
        if (_update is null) return;
        UpdateText.Text = $"RFpix {_update.Version} is out (you have {running}).";
        UpdateHint.Text = OperatingSystem.IsWindows()
            ? "Download it and open the installer: it closes RFpix and updates it. Your settings and pictures stay."
            : linux == "appimage"
            ? "Download the new AppImage, make it runnable (Properties > Permissions, or chmod +x) and use it instead of this one. Your settings and pictures stay."
            : linux == "deb"
            ? "Download it, quit RFpix and open the .deb to install it over this one. Your settings and pictures stay."
            : "Download it, quit RFpix and open the installer. Your settings and pictures stay.";
        UpdateNotice.IsVisible = true;
    }

    private async void OnUpdateDownloadClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_update is null) return;
        try { await Launcher.LaunchUriAsync(new Uri(_update.DownloadUrl)); }
        catch (Exception) { /* no browser: the notice stays, so it can be tried again */ }
    }

    // Hidden until RFpix is next started.
    private void OnUpdateNotNowClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => UpdateNotice.IsVisible = false;

    // The Gallery's "Send this picture": over to TX/RX, Send tab, with the picture open.
    private void OnGallerySend(string path)
    {
        NavList.SelectedIndex = 0;
        if (GetPage("TxRx") is TxRxPage txRx) txRx.ShowSendTab(path);
    }

    // ---- remembering the window's size and position ----

    private void RestorePlacement()
    {
        if (AppSettings.Current.Window is not { } saved) return;   // first run: as in MainWindow.axaml
        try
        {
            var screens = Screens.All.Select(s => new WindowFit.Screen(
                s.WorkingArea.X, s.WorkingArea.Y, s.WorkingArea.Width, s.WorkingArea.Height, s.Scaling)).ToList();
            var fit = WindowFit.Fit(saved, screens, MinWidth, MinHeight);
            if (fit is null && Screens.Primary is { } primary)
            {
                // Its old place is gone (a monitor unplugged?): open centred, at a size that fits.
                var area = primary.WorkingArea;
                fit = WindowFit.Fit(saved with { Left = area.X, Top = area.Y }, screens, MinWidth, MinHeight);
                if (fit is not null) { Width = fit.Width; Height = fit.Height; }
            }
            else if (fit is not null)
            {
                Width = fit.Width;
                Height = fit.Height;
                WindowStartupLocation = WindowStartupLocation.Manual;
                Position = new PixelPoint(fit.Left, fit.Top);
            }
            if (saved.Maximized) WindowState = WindowState.Maximized;
        }
        catch (Exception)
        {
            // If the screens can't be read, just open as on the first run.
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if ((change.Property == ClientSizeProperty || change.Property == WindowStateProperty) && WindowState == WindowState.Normal)
            _normal = new WindowPlacement(ClientSize.Width, ClientSize.Height, Position.X, Position.Y, false);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        PositionChanged += (_, _) =>
        {
            if (WindowState == WindowState.Normal)
                _normal = new WindowPlacement(ClientSize.Width, ClientSize.Height, Position.X, Position.Y, false);
        };
    }

    private void SavePlacement()
    {
        var normal = WindowState == WindowState.Normal
            ? new WindowPlacement(ClientSize.Width, ClientSize.Height, Position.X, Position.Y, false)
            : _normal;
        if (normal is null) return;
        AppSettings.Current.Window = normal with { Maximized = WindowState == WindowState.Maximized };
        AppSettings.Current.Save();
    }

    // When the window closes, let pages tidy up (for example, release the sound device).
    // Closing: remember where the window was (while it still has a size and position).
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        try { SavePlacement(); } catch (Exception) { /* not worth stopping RFpix closing over */ }
    }

    protected override void OnClosed(EventArgs e)
    {
        GalleryPage.SendRequested -= OnGallerySend;
        RFpix.Upload.UploadService.Stop();
        foreach (var page in _pages.Values.OfType<IDisposable>())
        {
            page.Dispose();
        }
        RFpix.Radio.RadioService.Close();   // release PTT, restore the mode, close the port
        base.OnClosed(e);
    }

    // Runs whenever a different sidebar item is picked.
    private void OnNavSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedItem is ListBoxItem item && item.Tag is string pageName)
        {
            // Show this page and hide the others (they keep everything, ready to show again).
            var shown = GetPage(pageName);
            foreach (var (name, page) in _pages)
            {
                page.IsVisible = page == shown;
                _fits[name].IsVisible = page == shown;
            }
        }
    }

    // Finds the page with that name, creating it the first time.
    private Control GetPage(string pageName)
    {
        if (!_pages.TryGetValue(pageName, out var page))
        {
            page = pageName switch
            {
                "TxRx"     => new TxRxPage(),   // the Receive and Send tabs
                "Gallery"  => new GalleryPage(),
                "Settings" => new SettingsPage(),   // Audio, Radio and Misc tabs
                "Alpha"    => new AlphaTestingPage(),   // the built-in test transmissions
                _ => new TextBlock { Text = $"Unknown page: {pageName}" }
            };
            _pages[pageName] = page;
            // Each page shrinks to fit the window when it's too small for it (FitToWindow).
            var fit = new RFpix.Controls.FitToWindow(page);
            _fits[pageName] = fit;
            PageHost.Children.Add(fit);
        }
        return page;
    }
}
