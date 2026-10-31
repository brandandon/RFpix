// PictureViewerWindow.cs - RFpix's own picture viewer: double-click the picture on the Receive
// tab and it opens here, in a window that can be made as big as wanted (on a second monitor too).
//
//   - It follows the Receive tab: it shows whatever picture the Receive tab shows, and each new
//     one appears as it arrives, filling in, with the Receive tab's caption under it.
//   - The picture fills the window and keeps its shape.
//   - There's only one (the Receive tab brings it to the front if it's open already).
//   - Its size and place are remembered (AppSettings.PictureViewer; see RememberedWindow).
//   - No zoom, Save or Send: the Gallery does those for any picture.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RFpix.Settings;

namespace RFpix.Views;

public sealed class PictureViewerWindow : RememberedWindow
{
    /// <param name="picture">The Receive tab's picture, followed as it changes.</param>
    /// <param name="caption">The Receive tab's caption, followed as it changes.</param>
    public PictureViewerWindow(Image picture, TextBlock caption)
        : base(() => AppSettings.Current.PictureViewer, placement => AppSettings.Current.PictureViewer = placement)
    {
        Title = "RFpix – Received picture";
        Width = 960;
        Height = 760;
        MinWidth = 320;
        MinHeight = 260;
        Padding = new Thickness(12);

        var image = new Image { Stretch = Stretch.Uniform };
        var none = new TextBlock
        {
            Text = "No picture received yet. Each new picture appears here as it arrives.",
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16),
            Opacity = 0.7,
        };
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 8, 2, 0) };

        // Follow the Receive tab. Its picture changes the moment a new one is set there, before the
        // Receive tab lets go of the old one, so the old one is never shown after it's gone.
        void ShowPicture() { image.Source = picture.Source; none.IsVisible = picture.Source is null; }
        void ShowCaption() => text.Text = caption.Text;
        void OnPictureChanged(object? sender, AvaloniaPropertyChangedEventArgs e) { if (e.Property == Image.SourceProperty) ShowPicture(); }
        void OnCaptionChanged(object? sender, AvaloniaPropertyChangedEventArgs e) { if (e.Property == TextBlock.TextProperty) ShowCaption(); }
        ShowPicture();
        ShowCaption();
        picture.PropertyChanged += OnPictureChanged;
        caption.PropertyChanged += OnCaptionChanged;
        Closed += (_, _) =>
        {
            picture.PropertyChanged -= OnPictureChanged;
            caption.PropertyChanged -= OnCaptionChanged;
            image.Source = null;
        };

        var grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        var area = new Panel { Children = { none, image } };
        Grid.SetRow(text, 1);
        grid.Children.Add(area);
        grid.Children.Add(text);
        Content = grid;
        RestorePlacement();
    }
}
