// WindowFitTests.cs - checks the window opens where it was, or somewhere sensible.

using RFpix.Settings;
using Xunit;

namespace RFpix.Tests;

public class WindowFitTests
{
    private static readonly WindowFit.Screen Laptop = new(0, 0, 1920, 1040, 1.25);       // 1920 x 1080 at 125%, minus taskbar
    private static readonly WindowFit.Screen RightMonitor = new(1920, 0, 2560, 1400, 1.0);

    [Fact]
    public void A_window_that_still_fits_opens_exactly_where_it_was()
    {
        var saved = new WindowPlacement(1200, 760, 100, 50, Maximized: false);
        Assert.Equal(saved, WindowFit.Fit(saved, new[] { Laptop }, 800, 560));
    }

    [Fact]
    public void A_window_on_an_unplugged_monitor_opens_centred_instead()
    {
        var onRightMonitor = new WindowPlacement(1200, 760, 2200, 100, false);
        Assert.NotNull(WindowFit.Fit(onRightMonitor, new[] { Laptop, RightMonitor }, 800, 560));
        Assert.Null(WindowFit.Fit(onRightMonitor, new[] { Laptop }, 800, 560));
    }

    [Fact]
    public void A_window_too_big_for_the_screen_is_made_to_fit_and_moved_on()
    {
        // 1700 x 1000 layout units is 2125 x 1250 pixels at 125%: more than the laptop has.
        var big = new WindowPlacement(1700, 1000, 300, 200, false);
        var fitted = WindowFit.Fit(big, new[] { Laptop }, 800, 560)!;
        Assert.Equal(1920 / 1.25, fitted.Width, 3);
        Assert.Equal(1040 / 1.25, fitted.Height, 3);
        Assert.Equal((0, 0), (fitted.Left, fitted.Top));
    }

    [Fact]
    public void A_window_hanging_off_the_edge_is_moved_back_on()
    {
        var hanging = new WindowPlacement(1000, 700, 1200, 600, false);   // 1250 x 875 pixels from (1200, 600)
        var fitted = WindowFit.Fit(hanging, new[] { Laptop }, 800, 560)!;
        Assert.Equal((1920 - 1250, 1040 - 875), (fitted.Left, fitted.Top));
        Assert.Equal((1000.0, 700.0), (fitted.Width, fitted.Height));
    }

    [Fact]
    public void Too_small_a_saved_size_grows_to_the_smallest_allowed()
    {
        var tiny = new WindowPlacement(300, 200, 10, 10, true);
        var fitted = WindowFit.Fit(tiny, new[] { RightMonitor with { X = 0 } }, 800, 560)!;
        Assert.Equal((800.0, 560.0), (fitted.Width, fitted.Height));
        Assert.True(fitted.Maximized);
    }
}
