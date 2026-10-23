// FitToWindowTests.cs - checks that pages shrink to fit a small window (FitToWindow), never
// below 60%, never growing; and that the parts meant to fill the room (Flex, the Send tab's
// square picture boxes) take the room there is instead of making the page shrink.
// These lay the parts out in an invisible window (Avalonia's "headless" mode).

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Themes.Fluent;
using RFpix.Controls;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(RFpix.Tests.HeadlessApp))]

namespace RFpix.Tests;

public sealed class HeadlessApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<HeadlessApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public class FitToWindowTests
{
    private static FitToWindow Show(Control page, double width = 800, double height = 600)
    {
        var fit = new FitToWindow(page);
        var window = new Window { Content = fit, Width = width, Height = height, SizeToContent = SizeToContent.Manual };
        window.Show();
        window.UpdateLayout();
        return fit;
    }

    [AvaloniaFact]
    public void A_page_that_fits_stays_its_normal_size()
    {
        Assert.Equal(1, Show(new Border { Height = 400 }).Scale, 3);
    }

    [AvaloniaFact]
    public void A_page_too_tall_shrinks_just_enough()
    {
        Assert.Equal(0.75, Show(new Border { Height = 800 }).Scale, 2);   // 600 / 800
    }

    [AvaloniaFact]
    public void A_page_never_shrinks_below_60_percent()
    {
        Assert.Equal(FitToWindow.MinScale, Show(new Border { Height = 2000 }).Scale, 3);
    }

    [AvaloniaFact]
    public void A_part_that_fills_the_room_doesnt_make_the_page_shrink()
    {
        var page = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        page.Children.Add(new Border { Height = 300 });
        var flex = new Flex { Min = 100, Child = new ScrollViewer { Content = new Border { Height = 5000 } } };   // a long list
        Grid.SetRow(flex, 1);
        page.Children.Add(flex);
        var fit = Show(page);
        Assert.Equal(1, fit.Scale, 3);
        Assert.Equal(300, flex.Bounds.Height, 0);   // it takes what's left: 600 - 300
    }

    [AvaloniaFact]
    public void The_square_picture_boxes_shrink_to_the_height_left()
    {
        var page = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        page.Children.Add(new Border { Height = 200 });
        var box = new SquareBox { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        Grid.SetRow(box, 1);
        page.Children.Add(box);
        var fit = Show(page, 800, 600);
        Assert.Equal(1, fit.Scale, 3);
        Assert.Equal(400, box.Bounds.Height, 0);   // 600 - 200, not the 800 of its width
        Assert.Equal(400, box.Bounds.Width, 0);    // still square
    }

    [AvaloniaFact]
    public void Taking_the_page_out_gives_it_back_at_normal_size()
    {
        var page = new Border { Height = 800 };
        var fit = Show(page);
        Assert.True(fit.Scale < 1);
        Assert.Same(page, fit.Release());
        Assert.Null(page.RenderTransform);
        Assert.Null(page.Parent);
    }
}
