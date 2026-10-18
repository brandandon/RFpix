// SendPage.axaml.cs - the code half of the Send tab (sending your own pictures).
//
// What happens here:
//   1. Open a picture (button, or drag it onto the page). It's opened in the background:
//      turned the right way up, see-through parts made white (Pictures.Load).
//   2. The optimizer (PictureFitter) fits it to the transmit time, in the background, each
//      time something changes: the picture, the send settings, the target time, the
//      format, or (in Manual) the size or quality. A newer change cancels an older fit,
//      and the sliders wait a moment after the last move before fitting.
//   3. The fitted file is read back and shown beside the original, so you see exactly
//      what the receiver will get, with the time it takes in green (fits) or red (over).
//   4. The send settings can be saved as named profiles. Everything is remembered.
//   5. Send, and Save as sound file, work as on the Alpha Testing page (SendSupport).

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using RFpix.Audio;
using RFpix.HamDrm;
using RFpix.Imaging;
using RFpix.Radio;
using RFpix.Send;
using RFpix.Settings;

namespace RFpix.Views;

public partial class SendPage : UserControl, IDisposable
{
    // The drop-downs' choices, in order.
    private static readonly HamDrmMode[] Modes = { HamDrmMode.A, HamDrmMode.B, HamDrmMode.E };
    private static readonly HamDrmBandwidth[] Bandwidths = { HamDrmBandwidth.Narrow, HamDrmBandwidth.Wide };
    private static readonly int[] Qams = { 4, 16, 64 };
    private static readonly HamDrmInterleave[] Interleaves = { HamDrmInterleave.Long, HamDrmInterleave.Short };
    private static readonly HamDrmProtection[] Protections = { HamDrmProtection.High, HamDrmProtection.Low };
    private static readonly int[] TargetTimes = { 30, 45, 60, 90, 120, 150, 180, 240 };
    private static readonly int[] PresetSides = { 320, 640, 800, 1024, 1280, 1920 };

    /// <summary>The largest longer side Manual offers, in pixels.</summary>
    private const int MaxManualSide = 1920;

    private static readonly IBrush FitsBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32));
    private static readonly IBrush OverBrush = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));
    private static readonly IBrush WaitingBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80));

    private readonly IAudioOutput _output = AudioSystem.CreateOutput();
    private readonly PictureFitter _fitter = new(Pictures.Encode);
    private readonly DispatcherTimer _refitTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };

    private DecodedPicture? _original;
    private string _baseName = "picture";
    private FittedPicture? _fitted;
    private CancellationTokenSource? _fitting;   // the fit in progress; cancelled by a newer one
    private int _fitNumber;                      // counts fits, so an older one can't replace a newer
    private CancellationTokenSource? _sending;   // set while sending; cancelling it = Stop
    private bool _updating;                      // true while code (not the operator) sets the controls
    private double _gain;                        // the level slider as a multiplier, read as it plays

    public SendPage()
    {
        InitializeComponent();
        _updating = true;
        ModeBox.ItemsSource = Modes.Select(m => m.ToString()).ToList();
        BandwidthBox.ItemsSource = new[] { "2.2 kHz", "2.5 kHz" };
        QamBox.ItemsSource = Qams.Select(q => q.ToString()).ToList();
        InterleaveBox.ItemsSource = new[] { "Long", "Short" };
        ProtectionBox.ItemsSource = new[] { "High", "Low" };
        ReedSolomonBox.ItemsSource = new[] { "None", "RS1", "RS2", "RS3", "RS4" };
        TargetBox.ItemsSource = TargetTimes.Select(Clock).ToList();

        var settings = AppSettings.Current;
        ShowSendSettings(settings.SendSettings ?? SendProfile.NetRepeater);
        FillProfiles();
        (settings.SendFormat switch
        {
            PictureFormat.Jpeg => JpegButton,
            PictureFormat.Avif => AvifButton,
            _ => Jpeg2000Button,
        }).IsChecked = true;
        (settings.SendAutoFit ? AutoButton : ManualButton).IsChecked = true;
        SizeSlider.Value = 640;
        QualitySlider.Value = 50;
        ShowSliderTexts();
        ShowManualControls();
        _updating = false;

        _refitTimer.Tick += (_, _) => { _refitTimer.Stop(); Refit(); };
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        // Callsign, level, sound output and keying are shared with other pages: show them
        // as they are whenever this tab appears.
        AttachedToVisualTree += (_, _) => ShowShared();
        AppSettings.SoundDevicesChanged += ShowRoute;
        ShowShared();
    }

    private static string Clock(int seconds) => $"{seconds / 60}:{seconds % 60:00}";

    // ---- the send settings and profiles ----

    /// <summary>The send settings the drop-downs show, as a profile (named after the chosen one, if any).</summary>
    private SendProfile CurrentSettings() => new(
        (ProfileBox.SelectedItem as string) ?? "",
        Modes[Math.Max(0, ModeBox.SelectedIndex)],
        Bandwidths[Math.Max(0, BandwidthBox.SelectedIndex)],
        Qams[Math.Max(0, QamBox.SelectedIndex)],
        Interleaves[Math.Max(0, InterleaveBox.SelectedIndex)],
        Protections[Math.Max(0, ProtectionBox.SelectedIndex)],
        Math.Max(0, ReedSolomonBox.SelectedIndex),
        TargetTimes[Math.Max(0, TargetBox.SelectedIndex)]);

    private void ShowSendSettings(SendProfile p)
    {
        ModeBox.SelectedIndex = Array.IndexOf(Modes, p.Mode);
        BandwidthBox.SelectedIndex = Array.IndexOf(Bandwidths, p.Bandwidth);
        QamBox.SelectedIndex = Math.Max(0, Array.IndexOf(Qams, p.Qam));
        InterleaveBox.SelectedIndex = Array.IndexOf(Interleaves, p.Interleave);
        ProtectionBox.SelectedIndex = Array.IndexOf(Protections, p.Protection);
        ReedSolomonBox.SelectedIndex = Math.Clamp(p.ReedSolomon, 0, 4);
        int target = Array.IndexOf(TargetTimes, p.TargetSeconds);
        TargetBox.SelectedIndex = target >= 0 ? target : Array.IndexOf(TargetTimes, 120);
    }

    private static IEnumerable<SendProfile> AllProfiles => SendProfile.BuiltIn.Concat(AppSettings.Current.SendProfiles);

    /// <summary>Lists the profiles, and picks the one the drop-downs match (if any).</summary>
    private void FillProfiles()
    {
        bool was = _updating;
        _updating = true;
        ProfileBox.ItemsSource = AllProfiles.Select(p => p.Name).ToList();
        var current = CurrentSettings();
        string? wanted = AppSettings.Current.SendSettings?.Name;
        var match = AllProfiles.FirstOrDefault(p => p.Name == wanted && p.SameAs(current))
                    ?? AllProfiles.FirstOrDefault(p => p.SameAs(current));
        ProfileBox.SelectedItem = match?.Name;
        DeleteProfileButton.IsEnabled = match is not null && !SendProfile.BuiltIn.Contains(match);
        _updating = was;
    }

    private void OnProfileChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        var profile = AllProfiles.FirstOrDefault(p => p.Name == ProfileBox.SelectedItem as string);
        DeleteProfileButton.IsEnabled = profile is not null && !SendProfile.BuiltIn.Contains(profile);
        if (profile is null) return;
        _updating = true;
        ShowSendSettings(profile);
        _updating = false;
        SaveSendSettings();
        Refit();
    }

    private void OnSendSettingChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        FillProfiles();   // the chosen profile no longer matches, perhaps (or another now does)
        SaveSendSettings();
        Refit();
    }

    private void SaveSendSettings()
    {
        AppSettings.Current.SendSettings = CurrentSettings();
        AppSettings.Current.Save();
    }

    private void OnSaveProfileClick(object? sender, RoutedEventArgs e)
    {
        string name = (ProfileNameBox.Text ?? "").Trim();
        if (name.Length == 0) { ProfileNameProblem.Text = "Type a name first."; return; }
        if (SendProfile.BuiltIn.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            ProfileNameProblem.Text = "That name comes with RFpix: pick another.";
            return;
        }
        var saved = AppSettings.Current.SendProfiles;
        saved.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));   // saving again replaces it
        var profile = CurrentSettings() with { Name = name };
        saved.Add(profile);
        AppSettings.Current.SendSettings = profile;
        AppSettings.Current.Save();
        FillProfiles();
        ProfileNameProblem.Text = "";
        ProfileNameBox.Text = "";
        SaveProfileButton.Flyout?.Hide();
        ShowStatus($"Saved the profile \"{name}\".", problem: false);
    }

    private void OnDeleteProfileClick(object? sender, RoutedEventArgs e)
    {
        if (ProfileBox.SelectedItem is not string name) return;
        if (AppSettings.Current.SendProfiles.RemoveAll(p => p.Name == name) == 0) return;
        AppSettings.Current.SendSettings = CurrentSettings() with { Name = "" };
        AppSettings.Current.Save();
        FillProfiles();
        ShowStatus($"Deleted the profile \"{name}\". The settings it had are still in use.", problem: false);
    }

    // ---- format, Auto or Manual, size and quality ----

    private PictureFormat CurrentFormat =>
        JpegButton.IsChecked == true ? PictureFormat.Jpeg : AvifButton.IsChecked == true ? PictureFormat.Avif : PictureFormat.Jpeg2000;

    private bool IsAuto => AutoButton.IsChecked == true;

    private void OnFormatChanged(object? sender, RoutedEventArgs e)
    {
        if (_updating || sender is RadioButton { IsChecked: not true }) return;   // only the one switched on
        AppSettings.Current.SendFormat = CurrentFormat;
        AppSettings.Current.Save();
        Refit();
    }

    private void OnAutoChanged(object? sender, RoutedEventArgs e)
    {
        ShowManualControls();
        if (_updating) return;
        AppSettings.Current.SendAutoFit = IsAuto;
        AppSettings.Current.Save();
        // Switching to Manual starts from what Auto picked, so nothing changes until a slider moves.
        if (IsAuto) Refit();
    }

    /// <summary>Size and quality can be moved only in Manual; in Auto they show what Auto picked.</summary>
    private void ShowManualControls()
    {
        bool manual = !IsAuto && _original is not null;
        SizeSlider.IsEnabled = QualitySlider.IsEnabled = manual;
        foreach (var preset in PresetPanel.Children) preset.IsEnabled = manual;
    }

    private void OnManualSliderChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (SizeText is null || QualityText is null) return;   // still being built
        ShowSliderTexts();
        if (_updating || IsAuto) return;
        _refitTimer.Stop();   // wait until the slider has stopped moving
        _refitTimer.Start();
    }

    private void ShowSliderTexts()
    {
        int side = (int)Math.Round(SizeSlider.Value);
        SizeText.Text = _original is null ? $"{side} px" : SizeLabel(side);
        QualityText.Text = $"{QualitySlider.Value:0}";
    }

    /// <summary>"640 × 480": the picture's size with this longer side.</summary>
    private string SizeLabel(int longSide)
    {
        if (_original is null) return "";
        var (w, h) = PictureScaler.FitLongSide(_original.Width, _original.Height, longSide);
        return $"{w} × {h}";
    }

    /// <summary>Size buttons for the sizes this picture can be made (never larger than it is).</summary>
    private void FillPresets()
    {
        PresetPanel.Children.Clear();
        if (_original is null) return;
        int longest = Math.Max(_original.Width, _original.Height);
        foreach (int side in PresetSides.Where(s => s < longest).Append(Math.Min(longest, MaxManualSide)).Distinct())
        {
            var button = new Button
            {
                Content = SizeLabel(side), FontSize = 12, Padding = new Thickness(6, 2), Margin = new Thickness(0, 0, 4, 4),
                Tag = side,
            };
            button.Click += (_, _) => SizeSlider.Value = side;
            PresetPanel.Children.Add(button);
        }
    }

    // ---- opening a picture ----

    private async void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        IStorageFolder? start = null;
        if (AppSettings.Current.LastPictureFolder is { } folder)
        {
            try { start = await top.StorageProvider.TryGetFolderFromPathAsync(folder); } catch { }
        }
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a picture to send",
            AllowMultiple = false,
            SuggestedStartLocation = start,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Pictures")
                {
                    Patterns = new[] { "*.jpg", "*.jpeg", "*.png", "*.bmp", "*.gif", "*.tif", "*.tiff", "*.webp",
                                       "*.heic", "*.heif", "*.avif", "*.jp2", "*.j2k" },
                },
                FilePickerFileTypes.All,
            },
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) await OpenAsync(path);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files) && _sending is null ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (_sending is not null) return;
        var path = e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).FirstOrDefault(p => p is not null);
        if (path is not null) await OpenAsync(path);
    }

    private async Task OpenAsync(string path)
    {
        ShowStatus($"Opening {Path.GetFileName(path)}…", problem: false);
        DecodedPicture picture;
        try
        {
            picture = await Task.Run(() => Pictures.Load(path));
        }
        catch (Exception ex) when (ex is PictureException or IOException or UnauthorizedAccessException)
        {
            ShowStatus(ex is PictureException ? $"{Path.GetFileName(path)}: {ex.Message}"
                                              : $"Couldn't open {Path.GetFileName(path)}: {ex.Message}", problem: true);
            return;
        }

        _original = picture;
        _baseName = PictureFitter.BaseName(path);
        AppSettings.Current.LastPictureFolder = Path.GetDirectoryName(path);
        AppSettings.Current.Save();

        long bytes = 0;
        try { bytes = new FileInfo(path).Length; } catch (IOException) { }
        FileText.Text = $"{Path.GetFileName(path)}  ·  {picture.Width} × {picture.Height}  ·  {Kilobytes(bytes)}";
        DropHint.Text = "or drag another picture onto the page";
        var old = OriginalImage.Source as IDisposable;
        OriginalImage.Source = OriginalZoomImage.Source = Bitmaps.From(picture);
        old?.Dispose();

        _updating = true;
        SizeSlider.Minimum = Math.Min(PictureFitter.MinLongSide, Math.Max(picture.Width, picture.Height));
        SizeSlider.Maximum = Math.Min(Math.Max(picture.Width, picture.Height), MaxManualSide);
        FillPresets();
        _updating = false;
        ShowSliderTexts();
        ShowManualControls();
        ZoomBox.IsEnabled = true;
        ShowStatus("", problem: false);
        Refit();
    }

    private static string Kilobytes(long bytes) => bytes >= 1_000_000 ? $"{bytes / 1e6:0.0} MB" : $"{bytes / 1e3:0.0} kB";

    // ---- fitting ----

    /// <summary>Fits the picture again (in the background), and shows the result.</summary>
    private async void Refit()
    {
        _refitTimer.Stop();
        if (_original is not { } original) return;
        _fitting?.Cancel();
        var cancel = new CancellationTokenSource();
        _fitting = cancel;
        int number = ++_fitNumber;

        var profile = CurrentSettings();
        var format = CurrentFormat;
        bool auto = IsAuto;
        int side = (int)Math.Round(SizeSlider.Value), quality = (int)Math.Round(QualitySlider.Value);
        string name = _baseName;
        SendButton.IsEnabled = _sending is not null;   // Stop stays usable while sending
        SaveButton.IsEnabled = false;
        EstimateBorder.Background = WaitingBrush;
        EstimateText.ClearValue(TextBlock.ForegroundProperty);
        EstimateDetail.ClearValue(TextBlock.ForegroundProperty);
        EstimateText.Text = "Fitting…";
        EstimateDetail.Text = auto ? "Finding the best size and quality" : "Making the picture";

        try
        {
            var (fitted, preview) = await Task.Run(() =>
            {
                var f = auto ? _fitter.Auto(original, name, format, profile, cancel.Token)
                             : _fitter.Manual(original, name, format, profile, side, quality);
                cancel.Token.ThrowIfCancellationRequested();
                return (f, Pictures.Decode(f.File));   // what the receiver will see
            }, cancel.Token);
            if (number != _fitNumber) return;   // something changed meanwhile: a newer fit is coming
            ShowFitted(fitted, preview);
        }
        catch (OperationCanceledException)
        {
            // A newer fit replaced this one.
        }
        catch (Exception ex)
        {
            if (number != _fitNumber) return;
            EstimateText.Text = "Couldn't fit";
            EstimateDetail.Text = ex is PictureException ? ex.Message : $"Something went wrong making the picture: {ex.Message}";
        }
        finally
        {
            if (_fitting == cancel) _fitting = null;
            cancel.Dispose();
        }
    }

    private void ShowFitted(FittedPicture fitted, DecodedPicture preview)
    {
        _fitted = fitted;
        var old = SentImage.Source as IDisposable;
        SentImage.Source = SentZoomImage.Source = Bitmaps.From(preview);
        old?.Dispose();
        NoPictureText.IsVisible = false;
        SentLabel.Text = $"As sent: {fitted.Picture.Width} × {fitted.Picture.Height}  ·  {Kilobytes(fitted.File.Length)}  ·  {fitted.Format.DisplayName()}";
        ApplyZoom();

        if (IsAuto)
        {
            // Show what Auto picked (and start Manual from there).
            _updating = true;
            SizeSlider.Value = Math.Clamp(Math.Max(fitted.Picture.Width, fitted.Picture.Height), SizeSlider.Minimum, SizeSlider.Maximum);
            QualitySlider.Value = fitted.Quality;
            _updating = false;
            ShowSliderTexts();
        }

        int seconds = (int)Math.Ceiling(fitted.Duration.TotalSeconds);
        EstimateText.Text = $"{Clock(seconds)} on the air";
        EstimateText.Foreground = EstimateDetail.Foreground = Brushes.White;
        string pieces = $"{Kilobytes(fitted.SendData.Length)} sent";
        if (fitted.FitsTarget)
        {
            EstimateBorder.Background = FitsBrush;
            EstimateDetail.Text = $"Fits the {Clock(fitted.Profile.TargetSeconds)} target  ·  {pieces}";
        }
        else
        {
            EstimateBorder.Background = OverBrush;
            EstimateDetail.Text = $"Over the {Clock(fitted.Profile.TargetSeconds)} target  ·  {pieces}" +
                                  (IsAuto ? "  ·  even the smallest version is too long for these settings" : "  ·  make it smaller or lower the quality");
        }
        if (fitted.Format.RFpixOnly())
            EstimateDetail.Text += "\nOnly RFpix shows AVIF: QSSTV and EasyPal stations get a file they can't open.";
        SendButton.IsEnabled = true;
        SaveButton.IsEnabled = _sending is null;
    }

    // ---- square boxes and zoom ----

    /// <summary>Keeps the two picture boxes square: as tall as they are wide.</summary>
    private void OnPreviewGridSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        double side = Math.Max(100, Math.Floor((e.NewSize.Width - PreviewGrid.ColumnSpacing) / 2));
        if (Math.Abs(OriginalBox.Height - side) < 0.5) return;
        OriginalBox.Height = SentBox.Height = side;
    }

    private void OnZoomChanged(object? sender, RoutedEventArgs e) => ApplyZoom();

    /// <summary>Not zoomed, each picture fits its box. Zoomed, the boxes show scrolling copies
    /// instead: the sent picture at its own pixel size, the original scaled to the same size
    /// so the two line up, and they move together.</summary>
    private void ApplyZoom()
    {
        bool zoom = ZoomBox.IsChecked == true && _fitted is not null;
        OriginalImage.IsVisible = SentImage.IsVisible = !zoom;
        OriginalScroll.IsVisible = SentScroll.IsVisible = ZoomHint.IsVisible = zoom;
        if (!zoom) return;
        OriginalZoomImage.Width = _fitted!.Picture.Width;
        OriginalZoomImage.Height = _fitted.Picture.Height;
        // Start in the middle of the picture.
        Dispatcher.UIThread.Post(() =>
        {
            var middle = new Vector(Math.Max(0, (SentScroll.Extent.Width - SentScroll.Viewport.Width) / 2),
                                    Math.Max(0, (SentScroll.Extent.Height - SentScroll.Viewport.Height) / 2));
            SentScroll.Offset = OriginalScroll.Offset = middle;
        }, DispatcherPriority.Loaded);
    }

    private bool _syncingScroll;

    private void OnPreviewScrolled(object? sender, ScrollChangedEventArgs e)
    {
        if (_syncingScroll || sender is not ScrollViewer moved) return;
        _syncingScroll = true;
        var other = moved == OriginalScroll ? SentScroll : OriginalScroll;
        other.Offset = moved.Offset;
        _syncingScroll = false;
    }

    // Dragging a zoomed picture moves it under the pointer, like a map.
    private Point? _dragFrom;
    private Vector _dragStartOffset;

    private void OnPreviewPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ScrollViewer scroll || !e.GetCurrentPoint(scroll).Properties.IsLeftButtonPressed) return;
        _dragFrom = e.GetPosition(scroll);
        _dragStartOffset = scroll.Offset;
        e.Pointer.Capture(scroll);
        e.Handled = true;
    }

    private void OnPreviewDragged(object? sender, PointerEventArgs e)
    {
        if (_dragFrom is not { } from || sender is not ScrollViewer scroll) return;
        var moved = e.GetPosition(scroll) - from;
        double x = Math.Clamp(_dragStartOffset.X - moved.X, 0, Math.Max(0, scroll.Extent.Width - scroll.Viewport.Width));
        double y = Math.Clamp(_dragStartOffset.Y - moved.Y, 0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height));
        scroll.Offset = new Vector(x, y);   // the other box follows (OnPreviewScrolled)
        e.Handled = true;
    }

    private void OnPreviewReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragFrom is null) return;
        _dragFrom = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    // ---- callsign and level (shared with the Alpha Testing page) ----

    private void ShowShared()
    {
        _updating = true;
        CallsignBox.Text = AppSettings.Current.Callsign ?? "";
        LevelSlider.Value = Math.Clamp(Math.Round(AppSettings.Current.TransmitLevelDb), LevelSlider.Minimum, 0);
        LevelText.Text = SendSupport.LevelLabel(LevelSlider.Value);
        _gain = SendSupport.Gain(LevelSlider.Value);
        _updating = false;
        ShowRoute();
    }

    private void ShowRoute() => RouteText.Text = SendSupport.Route(_output);

    private void OnLevelChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (LevelText is null) return;   // still being built
        LevelText.Text = SendSupport.LevelLabel(LevelSlider.Value);
        Volatile.Write(ref _gain, SendSupport.Gain(LevelSlider.Value));   // a transmission in progress follows at once
        if (_updating) return;
        AppSettings.Current.TransmitLevelDb = LevelSlider.Value;
        AppSettings.Current.Save();
    }

    private void OnCallsignLostFocus(object? sender, RoutedEventArgs e) => SaveCallsign();

    private string SaveCallsign()
    {
        string call = (CallsignBox.Text ?? "").Trim().ToUpperInvariant();
        CallsignBox.Text = call;
        if (call != (AppSettings.Current.Callsign ?? ""))
        {
            AppSettings.Current.Callsign = call;
            AppSettings.Current.Save();
        }
        return call;
    }

    // ---- sending ----

    private async void OnSendClick(object? sender, RoutedEventArgs e)
    {
        if (_sending is not null)   // the button says Stop
        {
            _sending.Cancel();
            ShowStatus("Stopping…", problem: false);
            return;
        }
        if (_fitted is not { } fitted) return;
        string call = SaveCallsign();
        if (SendSupport.Problem(_output, call) is { } problem) { ShowStatus(problem, problem: true); return; }

        _sending = new CancellationTokenSource();
        var cancel = _sending.Token;
        SendButton.Content = "Stop";
        SaveButton.IsEnabled = OpenButton.IsEnabled = false;
        Progress.Value = 0;
        Progress.IsVisible = true;
        try
        {
            await SendSupport.SendAsync(() => fitted.Transmitter(call), fitted.FileName, _output,
                () => Volatile.Read(ref _gain), m => ShowStatus(m, problem: false),
                new Progress<double>(f => Progress.Value = f), cancel);
            ShowStatus($"Sent {fitted.FileName} as {call}.", problem: false);
        }
        catch (OperationCanceledException)
        {
            ShowStatus("Stopped. The transmitter is released.", problem: false);
        }
        catch (Exception ex) when (ex is AudioException or RadioException)
        {
            ShowStatus(ex.Message, problem: true);
        }
        finally
        {
            _sending.Dispose();
            _sending = null;
            SendButton.Content = "Send";
            SendButton.IsEnabled = _fitted is not null && _fitting is null;
            SaveButton.IsEnabled = SendButton.IsEnabled;
            OpenButton.IsEnabled = true;
            Progress.IsVisible = false;
        }
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (_fitted is not { } fitted || TopLevel.GetTopLevel(this) is not { } top) return;
        string call = SaveCallsign();
        if (call.Length == 0) { ShowStatus("Type your callsign first: it's sent with the transmission.", problem: true); return; }

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save the transmission as a sound file",
            SuggestedFileName = $"{_baseName}.wav",
            DefaultExtension = "wav",
            FileTypeChoices = new[] { new FilePickerFileType("WAV sound file") { Patterns = new[] { "*.wav" } } },
        });
        if (file?.TryGetLocalPath() is not { } path) return;

        try
        {
            ShowStatus("Making the sound file…", problem: false);
            await SendSupport.SaveAsync(() => fitted.Transmitter(call), path, SendSupport.Gain(LevelSlider.Value));
            ShowStatus($"Saved {Path.GetFileName(path)}.", problem: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowStatus($"Couldn't save the sound file: {ex.Message}", problem: true);
        }
    }

    private void ShowStatus(string message, bool problem)
    {
        StatusText.Text = message;
        if (problem) StatusText.Foreground = Brushes.OrangeRed;
        else StatusText.ClearValue(TextBlock.ForegroundProperty);
    }

    public void Dispose()
    {
        AppSettings.SoundDevicesChanged -= ShowRoute;
        _refitTimer.Stop();
        _fitting?.Cancel();
        _sending?.Cancel();   // closing RFpix mid-send: stop (Transmission un-keys)
        _output.Dispose();
    }
}
