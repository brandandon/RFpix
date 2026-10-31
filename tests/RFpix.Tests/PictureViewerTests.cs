// PictureViewerTests.cs - the picture viewer (double-click the Receive tab's picture) follows the
// Receive tab: its picture as it changes, and its caption under it. Run in an invisible window
// (Avalonia's "headless" mode).

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using RFpix.Views;
using Xunit;

namespace RFpix.Tests;

public class PictureViewerTests
{
    private static WriteableBitmap Bitmap(int width, int height) =>
        new(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

    [AvaloniaFact]
    public void It_follows_the_Receive_tabs_picture_and_caption()
    {
        var picture = new Image();
        var caption = new TextBlock();
        var viewer = new PictureViewerWindow(picture, caption);
        viewer.Show();
        viewer.UpdateLayout();
        var shown = viewer.GetVisualDescendants().OfType<Image>().Single();
        var texts = viewer.GetVisualDescendants().OfType<TextBlock>().ToList();
        var waiting = texts.Single(t => t.Text?.StartsWith("No picture received yet") == true);
        Assert.Null(shown.Source);
        Assert.True(waiting.IsVisible);

        // A picture arrives on the Receive tab: it appears in the viewer, with the caption.
        var first = Bitmap(320, 256);
        picture.Source = first;
        caption.Text = "Analog SSTV Martin 1  ·  21:30\nSaved as \"2026-10-31 2130 SSTV Martin 1.png\"";
        Assert.Same(first, shown.Source);
        Assert.False(waiting.IsVisible);
        Assert.Contains(texts, t => t.Text == caption.Text);

        // The next one replaces it.
        var second = Bitmap(640, 496);
        picture.Source = second;
        caption.Text = "HamDRM  ·  from N0CALL";
        Assert.Same(second, shown.Source);
        Assert.Contains(texts, t => t.Text == "HamDRM  ·  from N0CALL");

        // The picture fills the window and keeps its shape.
        Assert.Equal(Stretch.Uniform, shown.Stretch);
        viewer.Width = 1400;
        viewer.Height = 1000;
        viewer.UpdateLayout();
        double wide = shown.Bounds.Width;
        Assert.True(wide > 640, $"the picture should grow with the window (it's {wide:0} wide)");
        Assert.Equal(640.0 / 496, shown.Bounds.Width / shown.Bounds.Height, 2);
    }
}
