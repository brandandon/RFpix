// TemplateTests.cs - checks overlay templates: the fill-in codes, saving and reading them back,
// and the ones RFpix comes with. (TemplateDrawingTests checks the drawing itself.)

using RFpix.Templates;
using Xunit;

namespace RFpix.Tests;

public class TemplateTests
{
    private static readonly TemplateFields Fields = new("KD9DD", "W9ABC", "Bob", "595", new DateTime(2026, 10, 20, 19, 42, 0));

    [Fact]
    public void Codes_are_filled_in()
    {
        Assert.Equal("W9ABC de KD9DD", Fields.Fill("%to de %me"));
        Assert.Equal("Hi Bob, RST 595", Fields.Fill("Hi %name, RST %rst"));
        Assert.Equal("Oct 20, 2026 19:42", Fields.Fill("%date %time"));
        Assert.EndsWith("Z", Fields.Fill("%utc"));
        Assert.Equal("KD9DD", Fields.Fill("%ME"));   // in capitals too
    }

    [Fact]
    public void Codes_left_empty_leave_no_gaps_or_blank_lines()
    {
        var noTo = Fields with { To = "" };
        Assert.Equal("de KD9DD", noTo.Fill("%to de %me"));
        Assert.Equal("CQ\nde KD9DD", noTo.Fill("CQ\n%to\nde %me"));
    }

    [Fact]
    public void A_template_says_which_codes_it_uses()
    {
        var reply = TemplateStore.BuiltIn.Single(t => t.Name == "Reply");
        Assert.Contains("%to", reply.Codes());
        Assert.Contains("%rst", reply.Codes());
        Assert.DoesNotContain(reply.Codes(), c => c == "%name");
    }

    [Fact]
    public void RFpix_comes_with_four_templates_inside_the_picture()
    {
        Assert.Equal(4, TemplateStore.BuiltIn.Count);
        foreach (var t in TemplateStore.BuiltIn)
        {
            Assert.True(t.BuiltIn);
            Assert.All(t.Items, i => Assert.True(i.X >= 0 && i.Y >= 0 && i.X + i.Width <= 1.0001 && i.Y + i.Height <= 1.0001, $"{t.Name}: {i.Describe()}"));
        }
    }

    [Fact]
    public void Your_templates_are_saved_and_read_back()
    {
        string folder = Path.Combine(Path.GetTempPath(), "rfpix-templates-" + Guid.NewGuid().ToString("N"));
        try
        {
            var mine = TemplateStore.BuiltIn[0].Copy("Net night");
            mine.Items[0].Opacity = 0.3;
            mine.Items.Add(new OverlayItem { Kind = OverlayKind.Image, ImageData = Convert.ToBase64String(new byte[] { 1, 2, 3 }) });
            TemplateStore.Save(mine, folder);
            Assert.True(File.Exists(mine.Path));

            var all = TemplateStore.All(folder);
            Assert.Equal(5, all.Count);
            var back = all.Single(t => t.Name == "Net night");
            Assert.False(back.BuiltIn);
            Assert.Equal(0.3, back.Items[0].Opacity);
            Assert.Equal(OverlayKind.Image, back.Items[^1].Kind);
            Assert.Equal(mine.Items.Count, back.Items.Count);

            // Names must be new: not RFpix's, not another of yours. Saving it again replaces it.
            Assert.Throws<ArgumentException>(() => TemplateStore.Save(TemplateStore.BuiltIn[0].Copy("Reply"), folder));
            Assert.Throws<ArgumentException>(() => TemplateStore.Save(TemplateStore.BuiltIn[0].Copy("net NIGHT"), folder));
            back.Items.RemoveAt(0);
            TemplateStore.Save(back, folder);
            Assert.Equal(5, TemplateStore.All(folder).Count);

            File.WriteAllText(Path.Combine(folder, "broken" + TemplateStore.Extension), "{ not a template");
            Assert.Equal(5, TemplateStore.All(folder).Count);   // a damaged file is skipped
            TemplateStore.Delete(back);
            Assert.Equal(4, TemplateStore.All(folder).Count);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
