// TemplatePickerWindow.axaml.cs - the code half of the template picker. It returns the name of
// the template picked ("" for None), or null if cancelled (ShowDialog<string?>). Each tile shows
// the template drawn on the picture (or on a sample, with no picture open yet); "Edit" under a
// tile opens the editor (TemplateEditorWindow), and the tiles are made again after it closes.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using RFpix.Imaging;
using RFpix.Templates;

namespace RFpix.Views;

public partial class TemplatePickerWindow : Window
{
    private const int ThumbnailWidth = 240;
    private readonly DecodedPicture _sample;   // small, for the thumbnails
    private readonly DecodedPicture _full;     // for the editor
    private readonly string? _current;
    private readonly TemplateFields _fields;
    private int _generation;

    public TemplatePickerWindow() : this(null, null, TemplateFields.Sample(null, null)) { }

    /// <param name="picture">The picture to show the templates on (null: a sample).</param>
    /// <param name="current">The template in use (shown highlighted).</param>
    public TemplatePickerWindow(DecodedPicture? picture, string? current, TemplateFields fields)
    {
        InitializeComponent();
        _current = current;
        _fields = fields;
        _full = picture ?? SamplePicture(1280, 960);
        _sample = SmallCopy(_full);
        Opened += (_, _) => Fill();
    }

    /// <summary>A sample to show templates on: a sky over a field.</summary>
    public static DecodedPicture SamplePicture(int w, int h)
    {
        var bgra = new byte[4 * w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = 4 * (y * w + x);
                double fy = (double)y / h, fx = (double)x / w;
                bool ground = fy > 0.62 + 0.05 * Math.Sin(fx * 6);
                bgra[i] = (byte)(ground ? 40 : 230 - 90 * fy);
                bgra[i + 1] = (byte)(ground ? 120 + 30 * fy : 190 - 60 * fy);
                bgra[i + 2] = (byte)(ground ? 50 : 120 - 60 * fy);
                bgra[i + 3] = 255;
            }
        return new DecodedPicture(w, h, bgra);
    }

    private static DecodedPicture SmallCopy(DecodedPicture p)
    {
        var (w, h) = PictureScaler.FitLongSide(p.Width, p.Height, ThumbnailWidth * 2);
        return PictureScaler.Resize(p, w, h);
    }

    private async void Fill()
    {
        int generation = ++_generation;
        TilesPanel.Children.Clear();
        TilesPanel.Children.Add(Tile(null, _current is null or ""));
        var templates = TemplateStore.All();
        var images = new List<(OverlayTemplate, Image)>();
        foreach (var t in templates)
        {
            var tile = Tile(t, t.Name == _current);
            TilesPanel.Children.Add(tile);
            images.Add((t, (Image)((StackPanel)((Button)((StackPanel)tile).Children[0]).Content!).Children[0]));
        }
        // The thumbnails, drawn in the background one by one.
        foreach (var (t, image) in images)
        {
            var drawn = await Task.Run(() => OverlayRenderer.Draw(_sample, t, _fields));
            if (generation != _generation) return;
            image.Source = Bitmaps.From(drawn);
        }
    }

    private Control Tile(OverlayTemplate? t, bool current)
    {
        var image = new Image { Width = ThumbnailWidth, Height = ThumbnailWidth * _sample.Height / (double)_sample.Width, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapInterpolationMode(image, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
        if (t is null) image.Source = Bitmaps.From(_sample);
        var button = new Button
        {
            Padding = new Thickness(6),
            BorderThickness = new Thickness(current ? 2 : 1),
            BorderBrush = current ? (IBrush?)Application.Current?.FindResource("SystemControlHighlightAccentBrush") ?? Brushes.DodgerBlue : null,
            Content = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    image,
                    new TextBlock { Text = t?.Name ?? "None", FontWeight = FontWeight.SemiBold },
                },
            },
        };
        ToolTip.SetTip(button, t is null ? "Send the picture as it is" : $"Use \"{t.Name}\"" + (t.BuiltIn ? " (comes with RFpix)" : ""));
        button.Click += (_, _) => Close(t?.Name ?? "");
        var tile = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 12, 12) };
        tile.Children.Add(button);
        if (t is not null)
        {
            var edit = new Button
            {
                Content = t.BuiltIn ? "Copy and edit…" : "Edit…",
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(8, 2),
                FontSize = 12,
            };
            edit.Click += async (_, _) => await EditAsync(t);
            tile.Children.Add(edit);
        }
        return tile;
    }

    private async Task EditAsync(OverlayTemplate? t)
    {
        var editor = new TemplateEditorWindow(t, _full, _fields);
        await editor.ShowDialog(this);
        Fill();
    }

    private async void OnNewClick(object? sender, RoutedEventArgs e) => await EditAsync(null);

    private async void OnOpenFolderClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(TemplateStore.DefaultFolder);
            await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(TemplateStore.DefaultFolder));
        }
        catch (Exception) { }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
