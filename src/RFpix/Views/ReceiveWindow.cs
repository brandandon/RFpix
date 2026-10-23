// ReceiveWindow.cs - the Receive tab in a window of its own ("Pop out" on the TX/RX page),
// so receiving and sending can be watched side by side, or on two monitors.
//
// The Receive tab itself moves into this window: listening, recording and decoding carry on
// without a break. Closing the window puts it back on the TX/RX page. Its size and place are
// remembered (AppSettings.ReceiveWindow), checked against the screens as for the main window.

using Avalonia;
using Avalonia.Controls;
using RFpix.Settings;

namespace RFpix.Views;

public sealed class ReceiveWindow : Window
{
    private WindowPlacement? _normal;

    public ReceiveWindow(Control receiveTab)
    {
        Title = "RFpix – Receive";
        Width = 1000;
        Height = 760;
        MinWidth = 700;
        MinHeight = 500;
        Padding = new Thickness(24, 16);
        Content = new RFpix.Controls.FitToWindow(receiveTab);   // shrinks to fit a small window
        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            Icon = main.Icon;
        RestorePlacement();
        PositionChanged += (_, _) => { if (WindowState == WindowState.Normal) _normal = Placement(); };
        Closing += (_, _) =>
        {
            AppSettings.Current.ReceiveWindow = (_normal ?? Placement()) with { Maximized = WindowState == WindowState.Maximized };
            AppSettings.Current.Save();
        };
    }

    /// <summary>Gives the Receive tab back (the window closes empty).</summary>
    public Control? TakeBack()
    {
        var tab = (Content as RFpix.Controls.FitToWindow)?.Release();
        Content = null;
        return tab;
    }

    private WindowPlacement Placement() => new(ClientSize.Width, ClientSize.Height, Position.X, Position.Y, false);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if ((change.Property == ClientSizeProperty || change.Property == WindowStateProperty) && WindowState == WindowState.Normal)
            _normal = Placement();
    }

    private void RestorePlacement()
    {
        if (AppSettings.Current.ReceiveWindow is not { } saved) { WindowStartupLocation = WindowStartupLocation.CenterScreen; return; }
        try
        {
            var screens = Screens.All.Select(s => new WindowFit.Screen(
                s.WorkingArea.X, s.WorkingArea.Y, s.WorkingArea.Width, s.WorkingArea.Height, s.Scaling)).ToList();
            if (WindowFit.Fit(saved, screens, MinWidth, MinHeight) is { } fit)
            {
                Width = fit.Width;
                Height = fit.Height;
                WindowStartupLocation = WindowStartupLocation.Manual;
                Position = new PixelPoint(fit.Left, fit.Top);
            }
            else
            {
                Width = saved.Width;
                Height = saved.Height;
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
            if (saved.Maximized) WindowState = WindowState.Maximized;
        }
        catch (Exception)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }
}
