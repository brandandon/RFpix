// WindowFit.cs - puts the window back where it was last time, if that still makes sense.
//
// RFpix remembers the window's size and position when it closes (AppSettings.Window). Next
// time, the screens may be different: a second monitor unplugged, a laptop away from its
// desk screen, a changed display scaling. So the saved place is checked first:
//   - if the window's title bar wouldn't be on any screen, RFpix opens centred instead;
//   - if the window is bigger than the screen now, it's made to fit;
//   - if part of it hangs off the screen's edge, it's moved back on.
// Sizes are in the layout's units (they follow Windows' display scaling); positions and
// screen areas are in real pixels, as Windows gives them.

namespace RFpix.Settings;

public static class WindowFit
{
    /// <summary>A screen's usable area (without the taskbar), in pixels, and its display scaling (1.25 = 125%).</summary>
    public readonly record struct Screen(int X, int Y, int Width, int Height, double Scaling);

    /// <summary>The placement to open with, adjusted to fit, or null if the saved position
    /// isn't on any screen now (then open centred, at the saved size if it fits).</summary>
    public static WindowPlacement? Fit(WindowPlacement saved, IReadOnlyList<Screen> screens, double minWidth, double minHeight)
    {
        // The point that must be on a screen: a little way into the title bar.
        int px = saved.Left + 60, py = saved.Top + 15;
        var screen = screens.FirstOrDefault(s => px >= s.X && px < s.X + s.Width && py >= s.Y && py < s.Y + s.Height);
        if (screen.Width == 0) return null;

        double scale = screen.Scaling > 0 ? screen.Scaling : 1;
        double width = Math.Clamp(saved.Width, minWidth, Math.Max(minWidth, screen.Width / scale));
        double height = Math.Clamp(saved.Height, minHeight, Math.Max(minHeight, screen.Height / scale));
        int widthPx = (int)Math.Round(width * scale), heightPx = (int)Math.Round(height * scale);
        int left = Math.Max(screen.X, Math.Min(saved.Left, screen.X + screen.Width - widthPx));
        int top = Math.Max(screen.Y, Math.Min(saved.Top, screen.Y + screen.Height - heightPx));
        return saved with { Width = width, Height = height, Left = left, Top = top };
    }
}
