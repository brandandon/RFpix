// WaterfallPictureWindow.axaml.cs - the code half of the "WF PIC" window. It returns the
// picture's tone strengths, ready to send (ShowDialog<double[][]?>), or null if cancelled.

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using RFpix.Imaging;
using RFpix.Send;
using RFpix.Settings;

namespace RFpix.Views;

public partial class WaterfallPictureWindow : Window
{
    private DecodedPicture? _picture;
    private double[][]? _levels;

    public WaterfallPictureWindow()
    {
        InitializeComponent();
        InvertBox.IsCheckedChanged += (_, _) => ShowPreview();
    }

    private async void OnChooseClick(object? sender, RoutedEventArgs e)
    {
        var start = AppSettings.Current.LastPictureFolder is { } folder && Directory.Exists(folder)
            ? await StorageProvider.TryGetFolderFromPathAsync(folder) : null;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a picture to draw on the waterfall",
            AllowMultiple = false,
            SuggestedStartLocation = start,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Pictures") { Patterns = new[] { "*.jpg", "*.jpeg", "*.png", "*.bmp", "*.gif", "*.webp", "*.jp2", "*.avif", "*.tif", "*.tiff" } },
            },
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        try
        {
            _picture = await Task.Run(() => Pictures.Load(path));
            FileText.Text = Path.GetFileName(path);
        }
        catch (Exception ex) when (ex is PictureException or IOException or UnauthorizedAccessException)
        {
            FileText.Text = $"Couldn't open {Path.GetFileName(path)}: {ex.Message}";
            _picture = null;
        }
        ShowPreview();
    }

    private void ShowPreview()
    {
        if (SendButton is null) return;   // still being built
        var old = PreviewImage.Source as IDisposable;
        if (_picture is null)
        {
            _levels = null;
            PreviewImage.Source = null;
        }
        else
        {
            _levels = WaterfallPicture.Levels(_picture, InvertBox.IsChecked == true);
            PreviewImage.Source = Bitmaps.From(WaterfallPicture.Preview(_levels));
        }
        old?.Dispose();
        PreviewHint.IsVisible = _levels is null;
        SendButton.IsEnabled = _levels is not null;
        TimeText.Text = _levels is null ? "" : $"Takes {WaterfallPicture.Duration(_levels.Length).TotalSeconds:0.0} s on the air";
    }

    private void OnSendClick(object? sender, RoutedEventArgs e)
    {
        if (_levels is not null) Close(_levels);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
