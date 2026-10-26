// TemplateDrawingTests.cs - checks templates are drawn where they should be, at any picture size.

using RFpix.Imaging;
using RFpix.Templates;
using Xunit;

namespace RFpix.Tests;

public class TemplateDrawingTests
{
    private static readonly TemplateFields Fields = new("KD9DD", "W9ABC", "Bob", "595", new DateTime(2026, 10, 20, 19, 42, 0));

    private static DecodedPicture Grey(int w, int h)
    {
        var bgra = new byte[4 * w * h];
        Array.Fill(bgra, (byte)128);
        return new DecodedPicture(w, h, bgra);
    }

    private static (int B, int G, int R) At(DecodedPicture p, double fx, double fy)
    {
        int x = Math.Min(p.Width - 1, (int)(fx * p.Width)), y = Math.Min(p.Height - 1, (int)(fy * p.Height));
        int i = 4 * (y * p.Width + x);
        return (p.Bgra[i], p.Bgra[i + 1], p.Bgra[i + 2]);
    }

    [Theory]
    [InlineData(320, 256)]
    [InlineData(1280, 960)]
    public void A_box_lands_in_the_same_place_at_any_size(int w, int h)
    {
        var t = new OverlayTemplate { Items = { new() { Kind = OverlayKind.Box, X = 0.5, Y = 0.5, Width = 0.5, Height = 0.5, Fill = "#FF0000", Opacity = 1 } } };
        var drawn = OverlayRenderer.Draw(Grey(w, h), t, Fields);
        Assert.Equal((0, 0, 255), At(drawn, 0.75, 0.75));     // inside: red
        Assert.Equal((128, 128, 128), At(drawn, 0.25, 0.25)); // outside: as it was
    }

    private static DecodedPicture Solid(int w, int h, byte b, byte g, byte r)
    {
        var bgra = new byte[4 * w * h];
        for (int i = 0; i < bgra.Length; i += 4) { bgra[i] = b; bgra[i + 1] = g; bgra[i + 2] = r; bgra[i + 3] = 255; }
        return new DecodedPicture(w, h, bgra);
    }

    [Fact]
    public void A_received_picture_is_drawn_whole_in_its_box()
    {
        var t = new OverlayTemplate { Items = { new() { Kind = OverlayKind.Received, X = 0.5, Y = 0, Width = 0.5, Height = 0.5, Opacity = 1 } } };
        Assert.True(t.UsesReceivedPicture);
        var theirs = Solid(320, 256, 0, 0, 255);   // a red picture, 5:4
        var drawn = OverlayRenderer.Draw(Grey(1280, 960), t, Fields with { Received = theirs });
        Assert.Equal((0, 0, 255), At(drawn, 0.75, 0.25));      // in the box: their picture
        Assert.Equal((128, 128, 128), At(drawn, 0.25, 0.75));  // elsewhere: as it was
        // It keeps its shape: the box is 640 x 480 (4:3) and their picture 5:4, so it's as tall as
        // the box and 600 wide, centred: a 20-pixel strip at each side of the box stays as it was.
        Assert.Equal((128, 128, 128), At(drawn, 0.505, 0.25));
        Assert.Equal((0, 0, 255), At(drawn, 0.52, 0.25));
    }

    [Fact]
    public void Without_a_received_picture_nothing_is_drawn_except_in_previews()
    {
        var t = new OverlayTemplate { Items = { new() { Kind = OverlayKind.Received, X = 0, Y = 0, Width = 1, Height = 1, Opacity = 1, KeepShape = false } } };
        Assert.Equal((128, 128, 128), At(OverlayRenderer.Draw(Grey(200, 200), t, Fields), 0.3, 0.3));   // sending: nothing
        var preview = OverlayRenderer.Draw(Grey(200, 200), t, Fields with { Preview = true });
        Assert.NotEqual((128, 128, 128), At(preview, 0.1, 0.1));                                          // the stand-in
    }

    [Fact]
    public void A_see_through_box_mixes_with_the_picture()
    {
        var t = new OverlayTemplate { Items = { new() { Kind = OverlayKind.Box, X = 0, Y = 0, Width = 1, Height = 1, Fill = "#000000", Opacity = 0.5 } } };
        int b = At(OverlayRenderer.Draw(Grey(100, 100), t, Fields), 0.5, 0.5).B;
        Assert.InRange(b, 58, 70);   // half of 128
    }

    [Theory]
    [InlineData(320, 256)]
    [InlineData(1280, 960)]
    public void Text_is_drawn_inside_its_rectangle_scaled_with_the_picture(int w, int h)
    {
        var t = new OverlayTemplate { Items = { new() { Kind = OverlayKind.Text, X = 0.1, Y = 0.1, Width = 0.8, Height = 0.2, Text = "%me", Size = 0.15, Color = "#FFFFFF", OutlineColor = null } } };
        var drawn = OverlayRenderer.Draw(Grey(w, h), t, Fields);
        int inside = 0, outside = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                bool white = drawn.Bgra[4 * (y * w + x)] > 200;
                bool inRect = x >= 0.1 * w - 1 && x <= 0.9 * w + 1 && y >= 0.1 * h - 1 && y <= 0.3 * h + 1;
                if (white) { if (inRect) inside++; else outside++; }
            }
        Assert.True(inside > 0.005 * w * h, $"{inside} white pixels in the rectangle");
        Assert.Equal(0, outside);
    }

    [Fact]
    public void Every_built_in_template_draws_at_analog_and_digital_sizes()
    {
        foreach (var t in TemplateStore.BuiltIn)
            foreach (var (w, h) in new[] { (320, 256), (640, 496), (1280, 960) })
            {
                var drawn = OverlayRenderer.Draw(Grey(w, h), t, Fields);
                Assert.Equal(w, drawn.Width);
                Assert.NotEqual(Grey(w, h).Bgra, drawn.Bgra);   // something was drawn
            }
    }
}
