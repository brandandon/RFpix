// RememberedWindow.cs - a window of RFpix's own that remembers its size and place between runs
// (a second monitor included): the popped-out Receive window and the picture viewer.
//
// The place is saved in the settings when the window closes, and checked against the screens
// when it opens again (WindowFit), so a window last on a monitor that's since been unplugged
// comes back on one that's there.

using Avalonia;
using Avalonia.Controls;
using RFpix.Settings;

namespace RFpix.Views;

public abstract class RememberedWindow : Window
{
    private readonly Func<WindowPlacement?> _load;
    private readonly Action<WindowPlacement> _save;
    private WindowPlacement? _normal;   // the size and place when last neither maximized nor minimized

    /// <param name="load">Where the window was last time (null: never opened).</param>
    /// <param name="save">Keeps where it is now (RememberedWindow then saves the settings).</param>
    protected RememberedWindow(Func<WindowPlacement?> load, Action<WindowPlacement> save)
    {
        _load = load;
        _save = save;
        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
            Icon = main.Icon;
        PositionChanged += (_, _) => { if (WindowState == WindowState.Normal) _normal = Placement(); };
        Closing += (_, _) =>
        {
            try
            {
                _save((_normal ?? Placement()) with { Maximized = WindowState == WindowState.Maximized });
                AppSettings.Current.Save();
            }
            catch (Exception) { /* not worth stopping the window closing over */ }
        };
    }

    private WindowPlacement Placement() => new(ClientSize.Width, ClientSize.Height, Position.X, Position.Y, false);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if ((change.Property == ClientSizeProperty || change.Property == WindowStateProperty) && WindowState == WindowState.Normal)
            _normal = Placement();
    }

    /// <summary>Puts the window where it was last time. Called at the end of the window's own
    /// set-up, once its default and smallest sizes are set.</summary>
    protected void RestorePlacement()
    {
        if (_load() is not { } saved) { WindowStartupLocation = WindowStartupLocation.CenterScreen; return; }
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
