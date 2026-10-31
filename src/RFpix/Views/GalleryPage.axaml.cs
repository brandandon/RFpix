// GalleryPage.axaml.cs - the code half of the Gallery page.
//
// What happens here:
//   1. Each time the page is shown (or a picture is received or sent while it shows), the
//      chosen folder is listed (GalleryIndex): the newest first, grouped by day, filtered
//      by the Find box. Received pictures are shown in two halves side by side, digital
//      (HamDRM) on the left and analog SSTV on the right; sent ones in one list, each saying
//      which it went out as.
//   2. A tile is made for each picture, and the thumbnails are drawn one by one in the
//      background (a big JPEG 2000 or AVIF takes a moment to decode). They're kept, so
//      coming back to the page is quick.
//   3. Clicking a tile shows the picture larger, with what's known about it.
//   4. Send this picture opens it on the Send tab (MainWindow does that: SendRequested).
//      Delete moves it to the Recycle Bin, after a second click to be sure.

using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using RFpix.Gallery;
using RFpix.Imaging;
using RFpix.Receive;
using RFpix.Settings;

namespace RFpix.Views;

public partial class GalleryPage : UserControl
{
    /// <summary>The most pictures shown at once (the newest); Find reaches the others.</summary>
    public const int MaxShown = 300;
    private const int ThumbnailSide = 300;   // pixels: sharp on a 150% screen

    /// <summary>Raised when "Send this picture" is clicked, with the picture's path.</summary>
    public static event Action<string>? SendRequested;

    private readonly Dictionary<string, (DateTime Written, Bitmap? Bitmap, int Width, int Height)> _thumbnails = new();
    private readonly DispatcherTimer _findTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private List<GalleryEntry> _entries = new();
    private readonly Dictionary<string, Button> _tiles = new();
    private GalleryEntry? _selected;
    private int _generation;      // a newer listing stops older thumbnail work
    private bool _stale = true;   // something changed while the page was hidden
    private bool _deleteArmed;

    public GalleryPage()
    {
        InitializeComponent();
        SendSupport.GalleryChanged += OnGalleryChanged;
        FindBox.TextChanged += (_, _) => { _findTimer.Stop(); _findTimer.Start(); };
        _findTimer.Tick += (_, _) => { _findTimer.Stop(); Reload(); };
        AttachedToVisualTree += (_, _) => { if (_stale) Reload(); };   // the first time it's shown
        ShowWhere();
    }

    // Thumbnails are kept by full path (a received and a sent picture can share a name).
    private static string Key(GalleryEntry entry) => entry.FullPath;

    private bool ShowingSent => Tabs.SelectedIndex == 1;

    private string Folder => ShowingSent ? PictureFolders.Sent : PictureFolders.Received;

    // Received pictures are in the Received folder's Digital and Analog folders (and, from
    // before they were kept apart, the Received folder itself).
    private string[] Folders => ShowingSent ? new[] { Folder }
        : new[] { Folder, Path.Combine(Folder, ReceivedFileSaver.DigitalSubfolder), Path.Combine(Folder, ReceivedFileSaver.AnalogSubfolder) };

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && IsVisible && _stale) Reload();
    }

    private void OnGalleryChanged()
    {
        if (IsVisible) Reload();
        else _stale = true;
    }

    private void OnTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DaysPanel is null) return;   // still being built
        _selected = null;
        Reload();
    }

    // ---- the list ----

    private async void Reload()
    {
        _stale = false;
        int generation = ++_generation;
        string folder = Folder;
        var folders = Folders;
        string find = (FindBox.Text ?? "").Trim();
        List<GalleryEntry> entries;
        string? problem = null;
        try
        {
            entries = await Task.Run(() => GalleryIndex.List(folders));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            entries = new();
            problem = $"Couldn't read {folder}: {ex.Message}";
        }
        if (generation != _generation) return;

        int total = entries.Count;
        if (find.Length > 0)
            entries = entries.Where(e => Contains(e.Callsign, find) || Contains(e.Name, find) || Contains(e.File, find)).ToList();

        ReceivedTab.Content = ShowingSent ? "Received" : $"Received ({total})";
        SentTab.Content = ShowingSent ? $"Sent ({total})" : "Sent";
        TilesScroll.IsVisible = ShowingSent;
        SplitPanel.IsVisible = !ShowingSent;
        DaysPanel.Children.Clear();
        DigitalDays.Children.Clear();
        AnalogDays.Children.Clear();
        _tiles.Clear();
        EmptyText.IsVisible = false;
        if (ShowingSent)
        {
            _entries = entries.Take(MaxShown).ToList();
            BuildDays(DaysPanel, EmptyText, _entries, entries.Count, "Pictures you send are kept here.");
        }
        else
        {
            // Each half shows its newest pictures.
            var digital = entries.Where(e => !e.IsAnalog).ToList();
            var analog = entries.Where(e => e.IsAnalog).ToList();
            DigitalHeading.Text = $"Digital ({digital.Count})";
            AnalogHeading.Text = $"Analog ({analog.Count})";
            var shownDigital = digital.Take(MaxShown).ToList();
            var shownAnalog = analog.Take(MaxShown).ToList();
            BuildDays(DigitalDays, DigitalEmpty, shownDigital, digital.Count, "Digital (HamDRM) pictures you receive are kept here.");
            BuildDays(AnalogDays, AnalogEmpty, shownAnalog, analog.Count, "Analog SSTV pictures you receive are kept here.");
            _entries = shownDigital.Concat(shownAnalog).OrderByDescending(e => e.When).ToList();
        }
        if (problem is not null)
        {
            DigitalEmpty.IsVisible = AnalogEmpty.IsVisible = false;
            EmptyText.Text = problem;
            EmptyText.IsVisible = true;
        }
        if (_selected is not null && _entries.FirstOrDefault(e => e.FullPath == _selected.FullPath) is { } still) Select(still);
        else ShowDetails(null);
        await LoadThumbnailsAsync(generation, folder);
    }

    private static bool Contains(string? text, string find) =>
        text is not null && text.Contains(find, StringComparison.OrdinalIgnoreCase);

    /// <summary>The tiles for some pictures, by day, into <paramref name="days"/> (or a note if there are none).</summary>
    private void BuildDays(Panel days, TextBlock empty, List<GalleryEntry> shown, int matching, string emptyMessage)
    {
        empty.IsVisible = shown.Count == 0;
        if (shown.Count == 0)
        {
            empty.Text = FindBox.Text?.Trim().Length > 0 ? "No pictures match." : emptyMessage;
            return;
        }
        foreach (var day in shown.GroupBy(e => e.When.Date))
        {
            days.Children.Add(new TextBlock { Classes = { "DayHeading" }, Text = DayName(day.Key) });
            var wrap = new WrapPanel();
            foreach (var entry in day) wrap.Children.Add(MakeTile(entry));
            days.Children.Add(wrap);
        }
        if (matching > shown.Count)
            days.Children.Add(new TextBlock
            {
                Classes = { "Hint" }, Margin = new Thickness(0, 6, 0, 0), TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Text = $"Showing the newest {shown.Count} of {matching}. Type in Find to look for older ones.",
            });
    }

    private static string DayName(DateTime day) =>
        day == DateTime.Today ? "Today"
        : day == DateTime.Today.AddDays(-1) ? "Yesterday"
        : day.Year == DateTime.Today.Year ? day.ToString("dddd, MMM d")
        : day.ToString("MMM d, yyyy");

    private Button MakeTile(GalleryEntry entry)
    {
        var image = new Image { Stretch = Avalonia.Media.Stretch.UniformToFill, Height = 108 };
        RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
        if (_thumbnails.TryGetValue(Key(entry), out var t)) image.Source = t.Bitmap;
        string who = ShowingSent ? "" : entry.Callsign is { Length: > 0 } c ? $"{c}  ·  " : "";
        string kind = ShowingSent ? (entry.IsAnalog ? "  ·  Analog" : "  ·  Digital") : "";
        var tile = new Button
        {
            Classes = { "Tile" },
            Content = new StackPanel
            {
                Spacing = 3,
                Children =
                {
                    new Border { CornerRadius = new CornerRadius(3), ClipToBounds = true, Height = 108,
                                 Background = Avalonia.Media.Brushes.Transparent, Child = image },
                    new TextBlock { Text = $"{who}{entry.When:HH:mm}{kind}", FontWeight = Avalonia.Media.FontWeight.SemiBold, FontSize = 13 },
                    new TextBlock { Text = entry.Name ?? entry.File, FontSize = 12, Opacity = 0.75,
                                    TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis },
                },
            },
            Tag = image,
        };
        ToolTip.SetTip(tile, entry.File);
        tile.Click += (_, _) => Select(entry);
        tile.DoubleTapped += (_, _) => OpenFullSize(entry);
        _tiles[Key(entry)] = tile;
        return tile;
    }

    // The thumbnails, one at a time in the background (kept for next time).
    private async Task LoadThumbnailsAsync(int generation, string folder)
    {
        foreach (var entry in _entries.ToList())
        {
            if (generation != _generation) return;
            string path = entry.FullPath;
            DateTime written;
            try { written = File.GetLastWriteTimeUtc(path); } catch (IOException) { continue; }
            if (_thumbnails.TryGetValue(path, out var have) && have.Written == written) continue;

            var made = await Task.Run(() => MakeThumbnail(path));
            if (made is not { } m) continue;
            var bitmap = Bitmaps.From(m.Small);
            _thumbnails.TryGetValue(path, out var old);
            _thumbnails[path] = (written, bitmap, m.Width, m.Height);
            bool current = generation == _generation;
            if (current && _tiles.TryGetValue(Key(entry), out var tile) && tile.Tag is Image image) image.Source = bitmap;
            // (An older thumbnail of a changed file is let go once nothing shows it.)
            if (old.Bitmap is not null && !ReferenceEquals(DetailImage.Source, old.Bitmap) && current) old.Bitmap.Dispose();
            if (!current) return;
            if (_selected?.FullPath == entry.FullPath) ShowDetails(_selected);
        }
    }

    private static (DecodedPicture Small, int Width, int Height)? MakeThumbnail(string path)
    {
        try
        {
            var picture = Pictures.Decode(File.ReadAllBytes(path));
            var (w, h) = PictureScaler.FitLongSide(picture.Width, picture.Height, ThumbnailSide);
            return (PictureScaler.Resize(picture, w, h), picture.Width, picture.Height);
        }
        catch (Exception ex) when (ex is PictureException or IOException or UnauthorizedAccessException) { return null; }
    }

    // ---- the picture clicked ----

    private void Select(GalleryEntry entry)
    {
        if (_selected is not null && _tiles.TryGetValue(Key(_selected), out var before)) before.Classes.Remove("Selected");
        _selected = entry;
        if (_tiles.TryGetValue(Key(entry), out var tile)) tile.Classes.Add("Selected");
        ShowDetails(entry);
    }

    private int _detailNumber;   // a slower decode can't replace a newer choice

    private async void ShowDetails(GalleryEntry? entry)
    {
        _deleteArmed = false;
        DeleteButton.Content = "Delete";
        DetailsPanel.IsVisible = entry is not null;
        if (entry is null) return;
        int number = ++_detailNumber;

        DetailName.Text = entry.Name ?? entry.File;
        string path = Key(entry);
        _thumbnails.TryGetValue(path, out var thumb);
        long bytes = 0;
        try { bytes = new FileInfo(path).Length; } catch (IOException) { }

        var rows = new List<(string, string)>();
        if (!ShowingSent) rows.Add(("From", entry.Callsign ?? "not known"));
        rows.Add((ShowingSent ? "Sent" : "Received", entry.When.ToString("MMM d, HH:mm")));
        string size = thumb.Bitmap is not null ? $"{thumb.Width} × {thumb.Height}  ·  " : "";
        var format = PictureFormatOf(path);
        rows.Add(("Picture", $"{size}{bytes / 1000.0:0.0} kB" + (format is null ? "" : $"  ·  {format}")));
        if (entry.Settings is not null)
            rows.Add(("Sent as", entry.Settings + (entry.ReedSolomon is int rs ? (rs == 0 ? "  ·  no Reed-Solomon" : $"  ·  RS{rs}") : "")));
        if (entry.QualityDb is double q) rows.Add(("Signal", $"{q:0} dB"));
        if (entry.CompletedByBsr) rows.Add(("Pieces", "completed by the pieces asked for (BSR)"));
        if (entry.RepairedPieces > 0) rows.Add(("Repaired", $"{entry.RepairedPieces} pieces rebuilt by Reed-Solomon"));
        ShowRows(rows);
        SendThisButton.IsEnabled = true;

        // The picture, larger: decoded again at a size that suits the box.
        DetailImage.Source = thumb.Bitmap;
        var larger = await Task.Run(() =>
        {
            try
            {
                var picture = Pictures.Decode(File.ReadAllBytes(path));
                var (w, h) = PictureScaler.FitLongSide(picture.Width, picture.Height, 1024);
                return PictureScaler.Resize(picture, w, h);
            }
            catch (Exception ex) when (ex is PictureException or IOException or UnauthorizedAccessException) { return null; }
        });
        if (number != _detailNumber) return;
        if (larger is null) { DetailStatus.Text = "This picture can't be shown."; return; }
        var old = DetailImage.Source;
        DetailImage.Source = Bitmaps.From(larger);
        if (old is Bitmap b && !_thumbnails.Values.Any(t => ReferenceEquals(t.Bitmap, b))) b.Dispose();
    }

    private static string? PictureFormatOf(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var head = new byte[32];
            int n = stream.Read(head, 0, head.Length);
            return PictureFormats.Detect(head.AsSpan(0, n))?.DisplayName();
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private void ShowRows(List<(string Label, string Value)> rows)
    {
        DetailGrid.Children.Clear();
        DetailGrid.RowDefinitions.Clear();
        for (int i = 0; i < rows.Count; i++)
        {
            DetailGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = new TextBlock { Text = rows[i].Label, Classes = { "DetailLabel" } };
            var value = new TextBlock { Text = rows[i].Value, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
            Grid.SetRow(label, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            DetailGrid.Children.Add(label);
            DetailGrid.Children.Add(value);
        }
        DetailStatus.Text = "Send this picture opens it on the Send tab, ready to fit and send. Double-click the picture to open it full size.";
    }

    private void OnDetailDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_selected is { } entry) OpenFullSize(entry);
    }

    private async void OpenFullSize(GalleryEntry entry)
    {
        try
        {
            var launcher = TopLevel.GetTopLevel(this)?.Launcher;
            if (launcher is not null) await launcher.LaunchFileInfoAsync(new FileInfo(entry.FullPath));
        }
        catch (Exception ex)
        {
            DetailStatus.Text = $"Couldn't open it: {ex.Message}";
            RFpix.Diagnostics.AppLog.Error("Gallery", DetailStatus.Text);
        }
    }

    private void OnSendThisClick(object? sender, RoutedEventArgs e)
    {
        if (_selected is { } entry) SendRequested?.Invoke(entry.FullPath);
    }

    private void OnShowInFolderClick(object? sender, RoutedEventArgs e)
    {
        if (_selected is not { } entry) return;
        try
        {
            string path = entry.FullPath;
            if (OperatingSystem.IsWindows()) Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (OperatingSystem.IsMacOS()) RFpix.Platform.MacFiles.ShowInFinder(path);
            else if (OperatingSystem.IsLinux()) RFpix.Platform.LinuxFiles.ShowInFileManager(path);
            else OnOpenFolderClick(sender, e);
        }
        catch (Exception ex)
        {
            DetailStatus.Text = $"Couldn't open the folder: {ex.Message}";
            RFpix.Diagnostics.AppLog.Error("Gallery", DetailStatus.Text);
        }
    }

    // ---- changing the pictures folder ----

    private string? _newRoot;   // the folder picked, while asking whether to move the pictures there

    private void ShowWhere() => WhereText.Text = $"Pictures are kept in {PictureFolders.Root}";

    private async void OnChangeFolderClick(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null || MovePanel.IsVisible && !MoveButtons.IsEnabled) return;   // a move is under way
        var start = Directory.Exists(PictureFolders.Root) ? await top.StorageProvider.TryGetFolderFromPathAsync(PictureFolders.Root) : null;
        var picked = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose the folder for your pictures (Received and Sent go inside it)",
            SuggestedStartLocation = start,
        });
        if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } path) return;

        string oldRoot = PictureFolders.Root;
        if (PictureFolders.Problem(oldRoot, path) is { } problem)
        {
            RFpix.Diagnostics.AppLog.Error("Gallery", problem);
            ShowMovePanel(problem, buttons: false);
            return;
        }
        int count = await Task.Run(() => PictureFolders.CountPictures(oldRoot));
        if (count == 0)
        {
            UseRoot(path);   // nothing to move
            return;
        }
        _newRoot = path;
        ShowMovePanel($"You have {count} picture{(count == 1 ? "" : "s")} in {oldRoot}. Move them to {path}, " +
                      "or leave them where they are and start empty there?", buttons: true);
    }

    private void ShowMovePanel(string text, bool buttons)
    {
        MoveQuestion.Text = text;
        MoveButtons.IsVisible = buttons;
        MoveButtons.IsEnabled = true;
        MoveStatus.IsVisible = false;
        MovePanel.IsVisible = true;
        if (!buttons) _ = HideMovePanelSoon();
    }

    private async Task HideMovePanelSoon()
    {
        await Task.Delay(TimeSpan.FromSeconds(8));
        if (!MoveButtons.IsVisible) MovePanel.IsVisible = false;
    }

    private async void OnMoveClick(object? sender, RoutedEventArgs e)
    {
        if (_newRoot is not { } newRoot) return;
        string oldRoot = PictureFolders.Root;
        MoveButtons.IsEnabled = false;
        MoveStatus.IsVisible = true;
        MoveStatus.Text = "Copying…";
        var result = await Task.Run(() => PictureFolders.Move(oldRoot, newRoot,
            (done, total) => Dispatcher.UIThread.Post(() => MoveStatus.Text = $"Copying {done} of {total}…")));
        if (result.Problem is { } problem)
        {
            MoveButtons.IsEnabled = true;
            MoveStatus.Text = problem;
            RFpix.Diagnostics.AppLog.Error("Gallery", $"Moving the pictures to {newRoot}: {problem}");
            return;
        }
        UseRoot(newRoot);
        RFpix.Diagnostics.AppLog.Info("Gallery", $"Moved {result.Moved} files from {oldRoot} to {newRoot}; pictures now go there.");
        MoveButtons.IsVisible = false;
        MoveQuestion.Text = $"Moved {result.Moved} file{(result.Moved == 1 ? "" : "s")} to {newRoot}." +
                            (result.Renamed > 0 ? $" {result.Renamed} had the same name as a picture already there, so \"(2)\" was added to their names." : "");
        MoveStatus.IsVisible = false;
        _ = HideMovePanelSoon();
    }

    private void OnStartEmptyClick(object? sender, RoutedEventArgs e)
    {
        if (_newRoot is not { } newRoot) return;
        string oldRoot = PictureFolders.Root;
        UseRoot(newRoot);
        RFpix.Diagnostics.AppLog.Info("Gallery", $"Pictures now go to {newRoot} (the earlier ones stay in {oldRoot}).");
        ShowMovePanel($"New pictures now go to {newRoot}. Your earlier pictures are still in {oldRoot}.", buttons: false);
    }

    private void OnMoveCancelClick(object? sender, RoutedEventArgs e)
    {
        _newRoot = null;
        MovePanel.IsVisible = false;
    }

    // From now on, pictures are saved in (and the Gallery shows) this folder.
    private void UseRoot(string root)
    {
        _newRoot = null;
        AppSettings.Current.PicturesFolder = root;
        AppSettings.Current.ReceivedFolder = null;
        AppSettings.Current.Save();
        _selected = null;
        ShowWhere();
        Reload();
    }

    private async void OnOpenFolderClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var launcher = TopLevel.GetTopLevel(this)?.Launcher;
            if (launcher is not null) await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(Folder));
        }
        catch (Exception ex)
        {
            EmptyText.Text = $"Couldn't open {Folder}: {ex.Message}";
            RFpix.Diagnostics.AppLog.Error("Gallery", EmptyText.Text);
            EmptyText.IsVisible = true;
        }
    }

    // Delete: the first click asks, the second moves it to the Recycle Bin (the Trash on the Mac).
    private void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (_selected is not { } entry) return;
        if (!_deleteArmed)
        {
            _deleteArmed = true;
            DeleteButton.Content = "Click again to delete";
            return;
        }
        string folder = entry.Folder ?? Folder, path = entry.FullPath;
        try
        {
            if (OperatingSystem.IsWindows())
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                                                                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            else if (OperatingSystem.IsMacOS()) RFpix.Platform.MacFiles.MoveToTrash(path);
            else if (OperatingSystem.IsLinux()) RFpix.Platform.LinuxFiles.MoveToTrash(path);
            else File.Delete(path);
            GalleryIndex.Remove(folder, entry.File);
            if (_thumbnails.Remove(path, out var t))
            {
                if (ReferenceEquals(DetailImage.Source, t.Bitmap)) DetailImage.Source = null;
                t.Bitmap?.Dispose();
            }
            _selected = null;
            Reload();
        }
        catch (Exception ex)
        {
            _deleteArmed = false;
            DeleteButton.Content = "Delete";
            DetailStatus.Text = $"Couldn't delete it: {ex.Message}";
            RFpix.Diagnostics.AppLog.Error("Gallery", DetailStatus.Text);
        }
    }
}
