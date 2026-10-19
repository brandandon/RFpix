// MainWindow.axaml.cs - the code half of the main window.
// It switches pages when you click an item in the sidebar.

using System.Reflection;
using Avalonia.Controls;
using RFpix.Views;

namespace RFpix;

public partial class MainWindow : Window
{
    // Each page is created the first time it is opened, then kept,
    // so switching back to a page doesn't lose what was on it.
    private readonly Dictionary<string, Control> _pages = new();

    public MainWindow()
    {
        InitializeComponent();   // builds everything described in MainWindow.axaml

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"Version {version?.ToString(3)}";

        NavList.SelectedIndex = 0;   // start on the first page (TX/RX, on its Receive tab)
    }

    // When the window closes, let pages tidy up (for example, release the sound device).
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
