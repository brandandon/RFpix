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
    // Each page is created the first time it is opened, then kept,
    // so switching back to a page doesn't lose what was on it.
    private readonly Dictionary<string, Control> _pages = new();

    // The window's last normal (not maximized or minimized) size and position, kept up to date
    // so it can be saved even if RFpix is closed while maximized.
    private WindowPlacement? _normal;

    public MainWindow()
    {
        InitializeComponent();   // builds everything described in MainWindow.axaml

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"Version {version?.ToString(3)}";

        NavList.SelectedIndex = 0;   // start on the first page (TX/RX, on its Receive tab)
        RestorePlacement();
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
            PageHost.Content = GetPage(pageName);
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
        }
        return page;
    }
}
