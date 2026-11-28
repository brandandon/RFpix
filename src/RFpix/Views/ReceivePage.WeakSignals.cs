// ReceivePage.WeakSignals.cs - part of ReceivePage (ReceivePage.axaml.cs): "Weak signals", how weak analog pixels are
// drawn (chosen by Dan, Nov 28, 2026, from his recordings beside MMSSTV's copies): Shorter guessing (to start with),
// Own share or No smoothing. New pictures follow the choice. The last few analog pictures are kept with their
// measurements, so one can be drawn again another way in a split second, even while the next picture arrives:
// "‹ Earlier" and "Later ›" step through them, and changing Weak signals redraws the one shown, replacing its copy
// in the Gallery (and its website copy, if that hasn't been uploaded yet).

using Avalonia.Interactivity;
using RFpix.Analog;
using RFpix.Diagnostics;
using RFpix.Imaging;
using RFpix.Settings;

namespace RFpix.Views;

public partial class ReceivePage
{
    /// <summary>An analog picture received, kept so it can be drawn again.</summary>
    private sealed class KeptAnalog(SstvPicture picture, string caption)
    {
        public SstvPicture Picture { get; } = picture;
        public DecodedPicture Shown { get; set; } = picture.Picture;
        public AnalogDrawing Drawing { get; set; } = picture.Drawing;
        public string Caption { get; set; } = caption;           // the caption's first line
        public string Note { get; set; } = "";                   // "Saved as …", or why it wasn't
        public string? GalleryPath { get; set; }                 // its file in the Gallery, if it was kept there
        public Task<string?>? WebsiteCopy { get; set; }          // its website copy being made, if one was
        public DateTime At { get; } = DateTime.Now;
        public object? Bitmap { get; set; }                      // what's on the screen for it
    }

    /// <summary>How many recent analog pictures are kept (a few MB each).</summary>
    internal const int KeptPictures = 5;

    private readonly List<KeptAnalog> _kept = new();
    private int _keptAt = -1;        // the kept picture last shown
    private bool _settingWeakBox;    // the box set by the code, not changed by hand

    internal static readonly string[] DrawingNames = { "Shorter guessing", "Own share", "No smoothing" };

    /// <summary>Sets up Weak signals (from the constructor).</summary>
    private void SetUpWeakSignals()
    {
        WeakBox.ItemsSource = DrawingNames;
        SetWeakBox(AppSettings.Current.AnalogDrawing);
        UpdateKeptButtons();
    }

    private void SetWeakBox(AnalogDrawing drawing)
    {
        _settingWeakBox = true;
        WeakBox.SelectedIndex = Math.Clamp((int)drawing, 0, DrawingNames.Length - 1);
        _settingWeakBox = false;
    }

    /// <summary>A finished analog picture (from ShowAnalogPicture, once it's on the screen): kept, the oldest
    /// dropped beyond <see cref="KeptPictures"/>.</summary>
    private KeptAnalog KeepAnalog(SstvPicture picture, string caption)
    {
        var kept = new KeptAnalog(picture, caption) { Bitmap = PictureImage.Source };
        _kept.Add(kept);
        if (_kept.Count > KeptPictures) _kept.RemoveAt(0);
        _keptAt = _kept.Count - 1;
        SetWeakBox(kept.Drawing);
        UpdateKeptButtons();
        return kept;
    }

    /// <summary>An FSK ID just after a picture: its sender, in the kept picture's caption too.</summary>
    private void NoteKeptSender(string call)
    {
        if (_kept.Count == 0 || DateTime.Now - _kept[^1].At > TimeSpan.FromSeconds(20)) return;
        var last = _kept[^1];
        if (!last.Caption.Contains("  ·  from ")) last.Caption += $"  ·  from {call}";
    }

    /// <summary>The kept picture shown on the screen now, if one is (another picture may have taken its place).</summary>
    private KeptAnalog? KeptOnScreen() =>
        _keptAt >= 0 && _keptAt < _kept.Count && ReferenceEquals(PictureImage.Source, _kept[_keptAt].Bitmap) ? _kept[_keptAt] : null;

    private void UpdateKeptButtons()
    {
        bool onScreen = KeptOnScreen() is not null;
        int at = onScreen ? _keptAt : _kept.Count;   // from another picture: Earlier goes to the newest kept
        EarlierButton.IsEnabled = at > 0;
        LaterButton.IsEnabled = onScreen && _keptAt < _kept.Count - 1;
    }

    private void OnEarlierClick(object? sender, RoutedEventArgs e)
    {
        int at = KeptOnScreen() is not null ? _keptAt - 1 : _kept.Count - 1;
        if (at >= 0) ShowKept(at);
    }

    private void OnLaterClick(object? sender, RoutedEventArgs e)
    {
        if (KeptOnScreen() is not null && _keptAt + 1 < _kept.Count) ShowKept(_keptAt + 1);
    }

    private void ShowKept(int at)
    {
        _keptAt = at;
        var kept = _kept[at];
        var old = PictureImage.Source as IDisposable;
        var bitmap = Bitmaps.From(kept.Shown);
        PictureImage.Source = bitmap;
        old?.Dispose();   // (a kept picture is drawn afresh each time it's shown)
        kept.Bitmap = bitmap;
        PicturePlaceholder.IsVisible = false;
        PictureCaption.Text = KeptCaption(kept);
        SetWeakBox(kept.Drawing);
        UpdateKeptButtons();
    }

    private string KeptCaption(KeptAnalog kept) =>
        $"{kept.Caption}\n{kept.Note}" + (_kept.Count > 1 ? $"  ·  picture {_kept.IndexOf(kept) + 1} of the last {_kept.Count}" : "") +
        $"  ·  weak signals: {DrawingNames[(int)kept.Drawing].ToLowerInvariant()}";

    private async void OnWeakChanged(object? sender, Avalonia.Controls.SelectionChangedEventArgs e)
    {
        if (_settingWeakBox || WeakBox.SelectedIndex < 0) return;
        var drawing = (AnalogDrawing)WeakBox.SelectedIndex;
        if (AppSettings.Current.AnalogDrawing != drawing)
        {
            AppSettings.Current.AnalogDrawing = drawing;   // new pictures (read as each starts)
            AppSettings.Current.Save();
            AppLog.Info("Receive", $"Weak signals: {DrawingNames[(int)drawing]} for analog pictures from now on.");
        }
        // The picture on the screen, drawn again that way.
        if (KeptOnScreen() is not { } kept || kept.Drawing == drawing || kept.Picture.Redraw is not { } redraw) return;
        DecodedPicture picture;
        try { picture = await Task.Run(() => redraw(drawing)); }
        catch (Exception ex)   // (it shouldn't happen; but a picture can't be drawn again then)
        {
            ShowError($"Couldn't draw the picture again: {ex.Message}");
            return;
        }
        kept.Shown = picture;
        kept.Drawing = drawing;
        if (ReferenceEquals(PictureImage.Source, kept.Bitmap))   // still on the screen
        {
            var old = PictureImage.Source as IDisposable;
            var bitmap = Bitmaps.From(picture);
            PictureImage.Source = bitmap;
            old?.Dispose();
            kept.Bitmap = bitmap;
            PictureCaption.Text = KeptCaption(kept);
            UpdateKeptButtons();
        }
        AppLog.Info("Receive", $"Analog SSTV {kept.Picture.Mode.Name} from {kept.Picture.Ended:HH:mm} drawn again: weak signals {DrawingNames[(int)drawing].ToLowerInvariant()}.");
        await SaveRedrawn(kept, picture);
    }

    /// <summary>The picture drawn again replaces its copy in the Gallery, and its website copy if that's still
    /// waiting to be uploaded.</summary>
    private async Task SaveRedrawn(KeptAnalog kept, DecodedPicture picture)
    {
        if (kept.GalleryPath is { } path)
        {
            try
            {
                var png = await Task.Run(() => Pictures.EncodePng(picture));
                await Task.Run(() => File.WriteAllBytes(path, png));
                SendSupport.RaiseGalleryChanged();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PictureException)
            {
                ShowError($"Drew the picture again, but couldn't replace \"{Path.GetFileName(path)}\" in the Gallery: {ex.Message}");
            }
        }
        if (kept.WebsiteCopy is { } making && await making is { } copy)
        {
            var when = kept.Picture.Ended;
            bool replaced = await Task.Run(() => RFpix.Upload.UploadService.ReplaceCopy(copy, picture, when));
            AppLog.Info("Receive", replaced ? $"Its website copy {Path.GetFileName(copy)} replaced before uploading."
                                            : "Its website copy was uploaded already: the website keeps it as it was.");
        }
    }
}
