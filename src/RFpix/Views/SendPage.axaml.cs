// SendPage.axaml.cs - the code half of the Send tab (sending your own pictures).
//
// What happens here:
//   1. Open a picture (button, or drag it onto the page). It's opened in the background:
//      turned the right way up, see-through parts made white (Pictures.Load).
//   2. The optimizer (PictureFitter) fits it to the transmit time automatically, in the
//      background: when it opens, and again when the send settings, target time or format
//      change. The size and quality sliders then fine-tune it: moving one makes the picture
//      at exactly that size and quality (waiting a moment after the last move). A newer
//      change cancels an older fit.
//   3. The fitted file is read back and shown beside the original, so you see exactly
//      what the receiver will get, with the time it takes: green (fits), yellow (up to 7 s
//      over) or red. The automatic fit may use 3 s more than the target and still be green.
//   4. The send settings can be saved as named profiles. Everything is remembered.
//   5. Send, and Save as sound file, work as on the Beta Testing page (SendSupport), with the
//      waterfall text from Settings (Misc tab) before and after the picture.
//   6. CW sends the callsign in Morse code; ID writes it on the waterfall.
//   8. An overlay template (Templates/) can be picked: it's drawn on the picture at the size
//      that goes out, with its codes (%to, %rst...) filled from the boxes beside it.
//   7. "Send as Analog SSTV": the picture is fitted to the chosen mode's frame instead (each
//      mode has a fixed size and time) and sent as analog SSTV (Analog/SstvEncoder.cs), without
//      waterfall text (ID, WF TXT and WF PIC are hidden then; they come back in Digital).

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using RFpix.Analog;
using RFpix.Audio;
using RFpix.Gallery;
using RFpix.HamDrm;
using RFpix.Imaging;
using RFpix.Radio;
using RFpix.Send;
using RFpix.Settings;
using RFpix.Templates;

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
    private static readonly int[] Timeouts = { 0, 60, 90, 120, 150, 180, 210, 240, 300, 600 };   // 0: no repeater
    private static readonly double[] LeadIns = { 0.3, 0.5, 0.75, 1.0, 1.25, 1.5 };
    private static readonly int[] PresetSides = { 320, 640, 800, 1024, 1280, 1920 };

    /// <summary>The largest longer side Manual offers, in pixels.</summary>
    private const int MaxManualSide = 1920;

    /// <summary>The longest side of the copy of the original shown on screen (zoom never needs more:
    /// it shows the original at the sent picture's size, at most 1920).</summary>
    private const int OnScreenSide = 2048;

    private static readonly IBrush FitsBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32));
    private static readonly IBrush SlightlyOverBrush = new SolidColorBrush(Color.FromRgb(0xF2, 0xC2, 0x00));
    private static readonly IBrush OverBrush = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));
    private static readonly IBrush DarkText = new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1B));
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
    private bool _analog;                        // sending analog SSTV, not HamDRM
    private DecodedPicture? _analogPicture;      // the picture fitted to the analog mode's frame
    private OverlayTemplate? _template;          // drawn on the picture as it goes out (null: none)
    private List<GalleryEntry> _receivedChoices = new();   // for a template's received-picture box, newest first
    private string? _receivedPicked;             // the one picked by hand (null: the newest, whatever it is)
    private string? _receivedShown;              // the one loaded into _received
    private DecodedPicture? _received;
    private string? _autoTo;                     // the To callsign filled in from the picture chosen
    private readonly DispatcherTimer _fieldsTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };

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
        TimeoutBox.ItemsSource = Timeouts.Select(t => t == 0 ? "None" : Clock(t)).ToList();
        LeadInBox.ItemsSource = LeadIns.Select(l => $"{l:0.0#} s").ToList();

        var settings = AppSettings.Current;
        ShowSendSettings(settings.SendSettings ?? SendProfile.NetRepeater);
        FillProfiles();
        (settings.SendFormat switch
        {
            PictureFormat.Jpeg => JpegButton,
            PictureFormat.Avif => AvifButton,
            _ => Jpeg2000Button,
        }).IsChecked = true;
        SizeSlider.Value = 640;
        QualitySlider.Value = 50;
        ShowSliderTexts();
        ShowManualControls();
        AnalogModeBox.ItemsSource = SstvModes.All.Select(m => $"{m.Name}  ({m.Width} × {m.Height}, {Clock((int)Math.Ceiling(SstvEncoder.Duration(m).TotalSeconds))})").ToList();
        AnalogModeBox.SelectedIndex = Math.Max(0, SstvModes.All.ToList().FindIndex(m => m.Mode == settings.AnalogMode));
        (settings.AnalogWhole ? WholeButton : FillButton).IsChecked = true;
        AnalogCwBox.IsChecked = settings.AnalogCw;
        _analog = settings.SendAnalog;
        (_analog ? AnalogButton : DigitalButton).IsChecked = true;
        ShowSendAs();
        _updating = false;
        // Each radio button reports being switched on and off; only the one switched on counts
        // (the other is still on for a moment while the switch happens).
        _template = TemplateStore.Find(settings.TemplateName);
        ToBox.Text = settings.TemplateTo;
        NameBox.Text = settings.TemplateToName;
        RstBox.Text = settings.TemplateRst;
        ShowTemplate();
        foreach (var box in new[] { ToBox, NameBox, RstBox })
            box.TextChanged += (_, _) => { if (!_updating) { _fieldsTimer.Stop(); _fieldsTimer.Start(); } };
        ReceivedBox.SelectionChanged += (_, _) => OnReceivedPicked();
        SendSupport.GalleryChanged += () => Dispatcher.UIThread.Post(() => { if (ReceivedField.IsVisible) FillReceivedChoices(); });
        _fieldsTimer.Tick += (_, _) => { _fieldsTimer.Stop(); OnFieldsChanged(); };
        DigitalButton.IsCheckedChanged += (_, _) => { if (DigitalButton.IsChecked == true) OnSendAsChanged(analog: false); };
        AnalogButton.IsCheckedChanged += (_, _) => { if (AnalogButton.IsChecked == true) OnSendAsChanged(analog: true); };
        AnalogModeBox.SelectionChanged += (_, _) => OnAnalogChanged();
        // Fill and Whole: each acts only when it's the one switched on (as for Digital and Analog).
        FillButton.IsCheckedChanged += (_, _) => { if (FillButton.IsChecked == true) OnAnalogChanged(); };
        WholeButton.IsCheckedChanged += (_, _) => { if (WholeButton.IsChecked == true) OnAnalogChanged(); };
        AnalogCwBox.IsCheckedChanged += (_, _) => OnAnalogChanged();

        _refitTimer.Tick += (_, _) => { _refitTimer.Stop(); Refit(auto: false); };
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        // Callsign, level, sound output and keying are set elsewhere too: show them
        // as they are whenever this tab appears.
        AppSettings.TransmitLevelChanged += ShowShared;   // the level moved on the other page
        ShowShared();
    }

    private static string Clock(int seconds) => $"{seconds / 60}:{seconds % 60:00}";

    // ---- the overlay template ----

    private TemplateFields Fields() => new(SendSupport.Callsign, (ToBox.Text ?? "").Trim().ToUpperInvariant(),
                                           (NameBox.Text ?? "").Trim(), (RstBox.Text ?? "").Trim(), DateTime.Now,
                                           AppSettings.Current.GridSquare ?? "", _template?.UsesReceivedPicture == true ? _received : null);

    /// <summary>Draws the template on a picture (null if there's no template).</summary>
    private Func<DecodedPicture, DecodedPicture>? Decorator()
    {
        if (_template is not { } t) return null;
        var fields = Fields();
        return p => OverlayRenderer.Draw(p, t, fields);
    }

    private void ShowTemplate()
    {
        TemplateText.Text = _template is null ? "No template" : $"Template: {_template.Name}";
        var codes = _template?.Codes() ?? new HashSet<string>();
        ToField.IsVisible = codes.Contains("%to");
        NameField.IsVisible = codes.Contains("%name");
        RstField.IsVisible = codes.Contains("%rst");
        ReceivedField.IsVisible = _template?.UsesReceivedPicture == true;
        if (ReceivedField.IsVisible) FillReceivedChoices();
    }

    // ---- a template's received picture ----

    /// <summary>Lists the newest received pictures (keeping the one picked by hand, if it's still there).</summary>
    private void FillReceivedChoices()
    {
        var received = PictureFolders.Received;
        try
        {
            _receivedChoices = GalleryIndex.List(new[] { received, Path.Combine(received, RFpix.Receive.ReceivedFileSaver.DigitalSubfolder),
                                                         Path.Combine(received, RFpix.Receive.ReceivedFileSaver.AnalogSubfolder) })
                                           .Take(40).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _receivedChoices = new(); }
        if (_receivedPicked is not null && !_receivedChoices.Any(e => e.FullPath == _receivedPicked)) _receivedPicked = null;
        _updating = true;
        ReceivedBox.ItemsSource = _receivedChoices.Count == 0
            ? new[] { "None received yet" }
            : _receivedChoices.Select(e => $"{e.When:MMM d HH:mm}  {e.Callsign ?? "(no callsign)"}  ·  {(e.IsAnalog ? "analog" : "digital")}").ToArray();
        ReceivedBox.SelectedIndex = _receivedChoices.Count == 0 ? 0 : Math.Max(0, _receivedChoices.FindIndex(e => e.FullPath == _receivedPicked));
        ReceivedBox.IsEnabled = _receivedChoices.Count > 0;
        _updating = false;
        _ = ShowReceivedAsync(byHand: false);
    }

    private void OnReceivedPicked()
    {
        if (_updating) return;
        int i = ReceivedBox.SelectedIndex;
        if (i < 0 || i >= _receivedChoices.Count) return;
        _receivedPicked = i == 0 ? null : _receivedChoices[i].FullPath;   // the first is "the newest"
        _ = ShowReceivedAsync(byHand: true);
    }

    /// <summary>Loads the chosen received picture for the template, and fills in the To callsign.</summary>
    private async Task ShowReceivedAsync(bool byHand)
    {
        int i = Math.Max(0, ReceivedBox.SelectedIndex);
        var entry = i < _receivedChoices.Count ? _receivedChoices[i] : null;
        if (entry?.FullPath == _receivedShown && !byHand) return;
        _receivedShown = entry?.FullPath;
        DecodedPicture? picture = null;
        if (entry is not null)
        {
            try { picture = await Task.Run(() => Pictures.Decode(File.ReadAllBytes(entry.FullPath))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PictureException) { }
        }
        if (_receivedShown != entry?.FullPath) return;   // another was chosen meanwhile
        _received = picture;
        // Their callsign in the To box: always when picked by hand; otherwise only if the box is
        // empty or still holds the callsign filled in last time (so a typed one isn't lost).
        string to = (ToBox.Text ?? "").Trim().ToUpperInvariant();
        if (entry?.Callsign is { Length: > 0 } call && (byHand || to.Length == 0 || to == _autoTo))
        {
            _autoTo = call.ToUpperInvariant();
            if (to != _autoTo) ToBox.Text = _autoTo;   // (the To box's own change fits the picture again)
        }
        if (_template is not null) Refit(auto: true);
    }

    private async void OnTemplateClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var picker = new TemplatePickerWindow(_original, _template?.Name, Fields());
        var chosen = await picker.ShowDialog<string?>(owner);
        // Cancelled: the same template, read again (it may have been changed in the editor, or deleted).
        chosen ??= _template?.Name ?? "";
        _template = chosen.Length == 0 ? null : TemplateStore.Find(chosen);
        AppSettings.Current.TemplateName = _template?.Name;
        AppSettings.Current.Save();
        ShowTemplate();
        Refit(auto: true);
    }

    // The To, Name or RST box changed (and typing has paused): keep it, and fit again.
    private void OnFieldsChanged()
    {
        AppSettings.Current.TemplateTo = (ToBox.Text ?? "").Trim().ToUpperInvariant();
        AppSettings.Current.TemplateToName = (NameBox.Text ?? "").Trim();
        AppSettings.Current.TemplateRst = (RstBox.Text ?? "").Trim();
        AppSettings.Current.Save();
        if (_template is not null) Refit(auto: true);
    }

    // ---- digital or analog ----

    private SstvModeInfo AnalogMode => SstvModes.All[Math.Clamp(AnalogModeBox.SelectedIndex, 0, SstvModes.All.Count - 1)];

    private void OnSendAsChanged(bool analog)
    {
        if (_updating || analog == _analog) return;
        _analog = analog;
        AppSettings.Current.SendAnalog = _analog;
        AppSettings.Current.Save();
        ShowSendAs();
        Refit(auto: true);
    }

    /// <summary>Shows the controls for HamDRM or for analog SSTV. The repeater time-out and lead-in
    /// (the last two settings) count for both. Analog SSTV goes out without waterfall text, so ID,
    /// WF TXT and WF PIC are hidden then (their settings stay, for Digital).</summary>
    private void ShowSendAs()
    {
        IdButton.IsVisible = WfTextButton.IsVisible = WfPicButton.IsVisible = !_analog;
        ProfileWrap.IsVisible = !_analog;
        var settings = SettingsWrap.Children;
        for (int i = 0; i < settings.Count - 2; i++) settings[i].IsVisible = !_analog;
        FitPanel.IsVisible = AnalogPanel.IsVisible = _analog;
        TargetRow.IsVisible = SizePanel.IsVisible = QualityPanel.IsVisible = FormatPanel.IsVisible = !_analog;
        if (_analog) ZoomBox.IsChecked = false;
        ZoomBox.IsEnabled = !_analog && _original is not null;
    }

    private void OnAnalogChanged()
    {
        if (_updating) return;
        AppSettings.Current.AnalogMode = AnalogMode.Mode;
        AppSettings.Current.AnalogWhole = WholeButton.IsChecked == true;
        AppSettings.Current.AnalogCw = AnalogCwBox.IsChecked == true;
        AppSettings.Current.Save();
        if (_analog) ShowAnalog();
    }

    /// <summary>Analog SSTV: the picture fitted to the mode's frame, and its time on the air.</summary>
    private void ShowAnalog()
    {
        _fitting?.Cancel();
        ++_fitNumber;   // a HamDRM fit still running is no longer wanted
        if (_original is not { } original)
        {
            SendButton.IsEnabled = _sending is not null;
            return;
        }
        var mode = AnalogMode;
        _analogPicture = SstvEncoder.Fit(original, mode, whole: WholeButton.IsChecked == true);
        if (Decorator() is { } decorate) _analogPicture = decorate(_analogPicture);
        var old = SentImage.Source as IDisposable;
        SentImage.Source = SentZoomImage.Source = Bitmaps.From(_analogPicture);
        old?.Dispose();
        NoPictureText.IsVisible = false;
        SentLabel.Text = $"As sent: {mode.Width} × {mode.Height}  ·  {mode.Name}";
        ApplyZoom();

        var profile = CurrentSettings();
        var onAir = AnalogOnAir(mode);
        int seconds = (int)Math.Ceiling(AnalogTime(mode).TotalSeconds);
        EstimateText.Text = $"{Clock(seconds)} on the air";
        bool fits = profile.FitsTimeout(onAir);
        EstimateBorder.Background = fits ? FitsBrush : OverBrush;
        EstimateText.Foreground = EstimateDetail.Foreground = Brushes.White;
        EstimateDetail.Text = profile.TimeoutSeconds <= 0 ? "No repeater time-out set"
            : fits ? $"Inside the repeater's {Clock(profile.TimeoutSeconds)} time-out"
            : $"Over the repeater's {Clock(profile.TimeoutSeconds)} time-out ({Clock((int)Math.Ceiling(onAir.TotalSeconds))} in all): pick a shorter mode.";
        SendButton.IsEnabled = true;
        SaveButton.IsEnabled = _sending is null;
    }

    /// <summary>How long an analog picture keeps the transmitter on: lead-in, VIS and picture, tail.</summary>
    private TimeSpan AnalogOnAir(SstvModeInfo mode) => AnalogTime(mode) + CurrentSettings().Overhead(TimeSpan.Zero);

    /// <summary>How long the analog sound lasts: preamble, VIS, picture, FSK ID, and CW if ticked.</summary>
    private TimeSpan AnalogTime(SstvModeInfo mode)
    {
        string call = SendSupport.Callsign;
        var time = SstvEncoder.Duration(mode, preamble: true, fskId: call);
        if (AnalogCwBox.IsChecked == true && call.Length > 0) time += CwGap + MorseCode.Duration(call);
        return time;
    }

    /// <summary>The pause between the end of the picture (its FSK ID) and the CW callsign.</summary>
    private static readonly TimeSpan CwGap = TimeSpan.FromSeconds(0.5);

    /// <summary>The analog picture's sound, as MMSSTV sends it: a short preamble, the VIS code, the
    /// picture, and the callsign as an FSK ID; then, if ticked, half a second's pause and the callsign
    /// in CW (the CW button's tone and speed). No waterfall text: that's for Digital only.</summary>
    private Func<float[]> AnalogSound(DecodedPicture picture, SstvModeInfo mode)
    {
        string call = SendSupport.Callsign;
        bool cw = AnalogCwBox.IsChecked == true && call.Length > 0;
        return () =>
        {
            var sound = SstvEncoder.Audio(picture, mode, preamble: true, fskId: call);
            if (!cw) return sound;
            var morse = MorseCode.Audio(call);
            int gap = (int)(CwGap.TotalSeconds * SstvEncoder.SampleRate);
            var all = new float[sound.Length + gap + morse.Length];
            sound.CopyTo(all, 0);
            morse.CopyTo(all, sound.Length + gap);
            return all;
        };
    }

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
        TargetTimes[Math.Max(0, TargetBox.SelectedIndex)],
        Timeouts[Math.Max(0, TimeoutBox.SelectedIndex)],
        LeadIns[Math.Max(0, LeadInBox.SelectedIndex)]);

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
        int timeout = Array.IndexOf(Timeouts, p.TimeoutSeconds);
        TimeoutBox.SelectedIndex = timeout >= 0 ? timeout : 0;
        int lead = Array.FindIndex(LeadIns, l => Math.Abs(l - p.LeadInSeconds) < 0.01);
        LeadInBox.SelectedIndex = lead >= 0 ? lead : 0;
    }

    /// <summary>Keeps the transmit time within the repeater's time-out (the longer choices can't be picked).</summary>
    private bool KeepTargetInsideTimeout()
    {
        int timeout = Timeouts[Math.Max(0, TimeoutBox.SelectedIndex)];
        int target = TargetTimes[Math.Max(0, TargetBox.SelectedIndex)];
        if (timeout <= 0 || target <= timeout) return false;
        int best = Array.FindLastIndex(TargetTimes, t => t <= timeout);
        bool was = _updating;
        _updating = true;
        TargetBox.SelectedIndex = Math.Max(0, best);
        _updating = was;
        return true;
    }

    /// <summary>The waterfall text's time (before and after the picture), from Settings > Misc.</summary>
    private static TimeSpan TextTime =>
        WaterfallText.Extra(AppSettings.Current.WaterfallTextBefore, AppSettings.Current.WaterfallTextAfter);

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
        Refit(auto: true);
    }

    private void OnSendSettingChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        if (KeepTargetInsideTimeout())
            ShowStatus("The transmit time can't be longer than the repeater's time-out.", problem: false);
        FillProfiles();   // the chosen profile no longer matches, perhaps (or another now does)
        SaveSendSettings();
        Refit(auto: true);
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

    // ---- format, size and quality ----

    private PictureFormat CurrentFormat =>
        JpegButton.IsChecked == true ? PictureFormat.Jpeg : AvifButton.IsChecked == true ? PictureFormat.Avif : PictureFormat.Jpeg2000;


    private void OnFormatChanged(object? sender, RoutedEventArgs e)
    {
        if (_updating || sender is RadioButton { IsChecked: not true }) return;   // only the one switched on
        AppSettings.Current.SendFormat = CurrentFormat;
        AppSettings.Current.Save();
        Refit(auto: true);
    }

    /// <summary>Size and quality can be moved once there's a picture.</summary>
    private void ShowManualControls()
    {
        bool picture = _original is not null;
        SizeSlider.IsEnabled = QualitySlider.IsEnabled = picture;
        foreach (var preset in PresetPanel.Children) preset.IsEnabled = picture;
    }

    private void OnManualSliderChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (SizeText is null || QualityText is null) return;   // still being built
        ShowSliderTexts();
        if (_updating || _original is null) return;
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

    private const string AlreadySending = "RFpix is already transmitting (a request or a fix for missing pieces). Wait for it to finish.";

    /// <summary>Opens a picture, ready to fit and send (the Gallery's "Send this picture").</summary>
    public Task OpenPictureAsync(string path) => OpenAsync(path);

    private async Task OpenAsync(string path)
    {
        ShowStatus($"Opening {Path.GetFileName(path)}…", problem: false);
        DecodedPicture picture, onScreen;
        try
        {
            // The full picture is kept for fitting; the Original box shows a copy no bigger than
            // the screen needs (a 4096-pixel photo is slow to redraw in a small box).
            (picture, onScreen) = await Task.Run(() =>
            {
                var full = Pictures.Load(path);
                var (w, h) = PictureScaler.FitLongSide(full.Width, full.Height, OnScreenSide);
                return (full, PictureScaler.Resize(full, w, h));
            });
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
        OriginalImage.Source = OriginalZoomImage.Source = Bitmaps.From(onScreen);
        old?.Dispose();

        _updating = true;
        SizeSlider.Minimum = Math.Min(PictureFitter.MinLongSide, Math.Max(picture.Width, picture.Height));
        SizeSlider.Maximum = Math.Min(Math.Max(picture.Width, picture.Height), MaxManualSide);
        FillPresets();
        _updating = false;
        ShowSliderTexts();
        ShowManualControls();
        ZoomBox.IsEnabled = !_analog;
        ShowStatus("", problem: false);
        Refit(auto: true);
    }

    private static string Kilobytes(long bytes) => bytes >= 1_000_000 ? $"{bytes / 1e6:0.0} MB" : $"{bytes / 1e3:0.0} kB";

    // ---- fitting ----

    /// <summary>Fits the picture again (in the background), and shows the result: automatically,
    /// or (<paramref name="auto"/> false) at the size and quality the sliders show.</summary>
    private async void Refit(bool auto)
    {
        _refitTimer.Stop();
        if (_analog) { ShowAnalog(); return; }
        if (_original is not { } original) return;
        _fitting?.Cancel();
        var cancel = new CancellationTokenSource();
        _fitting = cancel;
        int number = ++_fitNumber;

        var chosen = CurrentSettings();
        // Fit to the chosen time, shortened if the whole transmission must stay inside a repeater's time-out.
        var profile = chosen with { TargetSeconds = chosen.FittingSeconds(TextTime) };
        var format = CurrentFormat;
        int side = (int)Math.Round(SizeSlider.Value), quality = (int)Math.Round(QualitySlider.Value);
        string name = _baseName;
        var decorate = Decorator();
        SendButton.IsEnabled = _sending is not null;   // Stop stays usable while sending
        SaveButton.IsEnabled = false;
        EstimateBorder.Background = WaitingBrush;
        EstimateText.ClearValue(TextBlock.ForegroundProperty);
        EstimateDetail.ClearValue(TextBlock.ForegroundProperty);
        EstimateText.Text = "Fitting…";
        EstimateDetail.Text = auto ? "Finding the best size and quality" : "Making the picture";

        try
        {
            // A newer change (a slider moved again, say) cancels this fit part-way. That's
            // caught inside the background work and comes back as "nothing", so the
            // cancelling never leaves it as an error (the Visual Studio debugger stops on
            // one that does, even though it's expected).
            var result = await Task.Run(() =>
            {
                try
                {
                    var f = auto ? _fitter.Auto(original, name, format, profile, cancel.Token, decorate)
                                 : _fitter.Manual(original, name, format, profile, side, quality, decorate);
                    if (cancel.IsCancellationRequested) return null;
                    return ((FittedPicture, DecodedPicture)?)(f, Pictures.Decode(f.File));   // what the receiver will see
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
            });
            // Cancelled, or something changed meanwhile: a newer fit is coming.
            if (result is not { } done || number != _fitNumber) return;
            ShowFitted(done.Item1, done.Item2);
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

        if (fitted.Automatic)
        {
            // Show what Auto picked (and start Manual from there).
            _updating = true;
            SizeSlider.Value = Math.Clamp(Math.Max(fitted.Picture.Width, fitted.Picture.Height), SizeSlider.Minimum, SizeSlider.Maximum);
            QualitySlider.Value = fitted.Quality;
            _updating = false;
            ShowSliderTexts();
        }

        int seconds = (int)Math.Ceiling(fitted.Duration.TotalSeconds);
        string target = Clock(fitted.Profile.TargetSeconds);
        string sent = $"{Kilobytes(fitted.SendData.Length)} sent";
        EstimateText.Text = $"{Clock(seconds)} on the air";
        switch (fitted.Fit)
        {
            case TimeFit.Fits:
                EstimateBorder.Background = FitsBrush;
                EstimateText.Foreground = EstimateDetail.Foreground = Brushes.White;
                EstimateDetail.Text = $"{(fitted.Automatic ? "Fitted to" : "Fits")} the {target} target  ·  {sent}";
                break;
            case TimeFit.SlightlyOver:
                EstimateBorder.Background = SlightlyOverBrush;
                EstimateText.Foreground = EstimateDetail.Foreground = DarkText;
                EstimateDetail.Text = $"A few seconds over the {target} target  ·  {sent}";
                break;
            default:
                EstimateBorder.Background = OverBrush;
                EstimateText.Foreground = EstimateDetail.Foreground = Brushes.White;
                EstimateDetail.Text = $"Over the {target} target  ·  {sent}" +
                    (fitted.Automatic ? "  ·  even the smallest version is too long for these settings"
                                      : "  ·  make it smaller or lower the quality");
                break;
        }
        var extra = TextTime;
        if (extra > TimeSpan.Zero)
            EstimateDetail.Text += $"\nPlus {extra.TotalSeconds:0} s of waterfall text.";

        // A repeater's time-out: the whole transmission must stay inside it.
        var p = fitted.Profile;
        if (p.TimeoutSeconds > 0)
        {
            var onAir = OnAir(fitted);
            int chosen = TargetTimes[Math.Max(0, TargetBox.SelectedIndex)];
            if (!p.FitsTimeout(onAir))
            {
                EstimateBorder.Background = OverBrush;
                EstimateText.Foreground = EstimateDetail.Foreground = Brushes.White;
                EstimateDetail.Text += $"\nOver the repeater's {Clock(p.TimeoutSeconds)} time-out ({Clock((int)Math.Ceiling(onAir.TotalSeconds))} in all): " +
                                       "it would be cut off, so it won't be sent.";
            }
            else if (p.TargetSeconds < chosen)
                EstimateDetail.Text += $"\nFitted to {Clock(p.TargetSeconds)} so the whole transmission stays inside the {Clock(p.TimeoutSeconds)} time-out.";
        }
        SendButton.IsEnabled = true;
        SaveButton.IsEnabled = _sending is null;
    }

    // ---- zoom ----

    private void OnZoomChanged(object? sender, RoutedEventArgs e) => ApplyZoom();

    /// <summary>Not zoomed, each picture fits its box. Zoomed, the boxes show scrolling copies
    /// instead: the sent picture at its own pixel size, the original scaled to the same size
    /// so the two line up, and they move together.</summary>
    private void ApplyZoom()
    {
        bool zoom = ZoomBox.IsChecked == true && _fitted is not null && !_analog;
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

    // ---- level (shared with the Beta Testing page) ----

    private void ShowShared()
    {
        _updating = true;
        LevelSlider.Value = Math.Clamp(Math.Round(AppSettings.Current.TransmitLevelDb), LevelSlider.Minimum, 0);
        LevelText.Text = SendSupport.LevelLabel(LevelSlider.Value);
        _gain = SendSupport.Gain(LevelSlider.Value);
        _updating = false;
    }

    private void OnLevelChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (LevelText is null) return;   // still being built
        LevelText.Text = SendSupport.LevelLabel(LevelSlider.Value);
        Volatile.Write(ref _gain, SendSupport.Gain(LevelSlider.Value));   // a transmission in progress follows at once
        if (_updating) return;
        AppSettings.Current.TransmitLevelDb = LevelSlider.Value;
        AppSettings.Current.Save();
        AppSettings.RaiseTransmitLevelChanged();
    }

    // ---- sending ----

    private void OnSendClick(object? sender, RoutedEventArgs e)
    {
        if (_sending is not null)   // the button says Stop
        {
            _sending.Cancel();
            ShowStatus("Stopping…", problem: false);
            return;
        }
        if (_analog)
        {
            SendAnalog();
            return;
        }
        if (_fitted is not { } fitted) return;
        var profile = fitted.Profile;
        if (!profile.FitsTimeout(OnAir(fitted)))
        {
            ShowStatus($"This would be on the air {Clock((int)Math.Ceiling(OnAir(fitted).TotalSeconds))}, longer than the repeater's " +
                       $"{Clock(profile.TimeoutSeconds)} time-out, so the repeater would cut it off. Make the picture smaller, " +
                       "pick a shorter transmit time, or shorten the waterfall text.", problem: true);
            return;
        }
        string call = SendSupport.Callsign;
        if (_sending is not null) return;
        if (SendSupport.Problem(_output, call) is { } problem) { ShowStatus(problem, problem: true); return; }
        if (SendSupport.IsSending) { ShowStatus(AlreadySending, problem: true); return; }
        // Kept so missing pieces can be sent again if a station asks (BSR), and saved for the Gallery.
        string? notSaved = SendSupport.RecordSent(fitted, call);
        Transmit(() => SendSupport.WithWaterfallText(fitted.Transmitter(call).Audio()), fitted.FileName,
                 $"Sent {fitted.FileName} as {call}." + (notSaved is null ? "" : $" ({notSaved})"),
                 TimeSpan.FromSeconds(profile.LeadInSeconds));
    }

    private void SendAnalog()
    {
        ShowAnalog();   // drawn again, so a template's date and time are the sending time's
        if (_analogPicture is not { } picture) return;
        var mode = AnalogMode;
        var profile = CurrentSettings();
        if (!profile.FitsTimeout(AnalogOnAir(mode)))
        {
            ShowStatus($"{mode.Name} would be on the air {Clock((int)Math.Ceiling(AnalogOnAir(mode).TotalSeconds))}, longer than the repeater's " +
                       $"{Clock(profile.TimeoutSeconds)} time-out, so the repeater would cut it off. Pick a shorter mode.", problem: true);
            return;
        }
        string call = SendSupport.Callsign;
        if (SendSupport.Problem(_output, call) is { } problem) { ShowStatus(problem, problem: true); return; }
        if (SendSupport.IsSending) { ShowStatus(AlreadySending, problem: true); return; }
        string? notSaved = SendSupport.RecordSentAnalog(picture, $"{_baseName}.jpg", mode, call);
        Transmit(AnalogSound(picture, mode), $"{_baseName} in {mode.Name}",
                 $"Sent {_baseName} in {mode.Name}." + (notSaved is null ? "" : $" ({notSaved})"),
                 TimeSpan.FromSeconds(profile.LeadInSeconds));
    }

    // CW: the callsign in Morse code, 750 Hz at 15 words per minute.
    private void OnCwClick(object? sender, RoutedEventArgs e)
    {
        string call = SendSupport.Callsign;
        Transmit(() => MorseCode.Audio(call), "your callsign in CW", $"Sent {call} in CW.");
    }

    // ID: the callsign written on the waterfall.
    private void OnIdClick(object? sender, RoutedEventArgs e)
    {
        string call = SendSupport.Callsign;
        Transmit(() => WaterfallText.Audio(call), "your callsign on the waterfall", $"Sent {call} on the waterfall.");
    }

    // WF TXT: a text of your own on the waterfall (typed in a small window).
    private async void OnWfTextClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var text = await new WaterfallTextWindow().ShowDialog<string?>(owner);
        if (string.IsNullOrWhiteSpace(text)) return;
        Transmit(() => WaterfallText.Audio(text), "your text on the waterfall", $"Sent \"{text}\" on the waterfall.");
    }

    // WF PIC: a small picture on the waterfall (chosen and previewed in a small window).
    private async void OnWfPicClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var levels = await new WaterfallPictureWindow().ShowDialog<double[][]?>(owner);
        if (levels is null || levels.Length == 0) return;
        Transmit(() => WaterfallPicture.Audio(levels), "a picture on the waterfall", "Sent the picture on the waterfall.");
    }

    /// <summary>Sends a sound (a picture, CW or ID). Send becomes Stop meanwhile, and the
    /// buttons that would start another sending are off.</summary>
    /// <summary>How long a picture keeps the transmitter on: lead-in, waterfall text, picture and tail.</summary>
    private static TimeSpan OnAir(FittedPicture fitted) => fitted.Duration + fitted.Profile.Overhead(TextTime);

    private async void Transmit(Func<float[]> makeSound, string what, string done, TimeSpan? leadIn = null)
    {
        if (_sending is not null) return;
        if (SendSupport.Problem(_output, SendSupport.Callsign) is { } problem) { ShowStatus(problem, problem: true); return; }
        if (SendSupport.IsSending) { ShowStatus(AlreadySending, problem: true); return; }

        _sending = new CancellationTokenSource();
        var cancel = _sending.Token;
        SendButton.Content = "Stop";
        SendButton.IsEnabled = true;
        SaveButton.IsEnabled = OpenButton.IsEnabled = CwButton.IsEnabled = IdButton.IsEnabled = false;
        DigitalButton.IsEnabled = AnalogButton.IsEnabled = false;   // not while sending
        WfTextButton.IsEnabled = WfPicButton.IsEnabled = false;
        Progress.Value = 0;
        Progress.IsVisible = true;
        try
        {
            await SendSupport.SendSoundAsync(makeSound, what, _output,
                () => Volatile.Read(ref _gain), m => ShowStatus(m, problem: false),
                new Progress<double>(f => Progress.Value = f), cancel, leadIn);
            ShowStatus(done, problem: false);
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
            SendButton.IsEnabled = _analog ? _analogPicture is not null : _fitted is not null && _fitting is null;
            SaveButton.IsEnabled = SendButton.IsEnabled;
            OpenButton.IsEnabled = CwButton.IsEnabled = IdButton.IsEnabled = true;
            DigitalButton.IsEnabled = AnalogButton.IsEnabled = true;
            WfTextButton.IsEnabled = WfPicButton.IsEnabled = true;
            Progress.IsVisible = false;
        }
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        if (!_analog && _fitted is null || _analog && _analogPicture is null) return;
        var fitted = _fitted;
        var analogPicture = _analogPicture;
        var analogMode = AnalogMode;
        bool analog = _analog;
        string call = SendSupport.Callsign;
        if (call.Length == 0) { ShowStatus("Set your callsign in Settings (Misc tab) first: it's sent with the transmission.", problem: true); return; }

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
            await SendSupport.SaveSoundAsync(analog ? AnalogSound(analogPicture!, analogMode)
                                                    : () => SendSupport.WithWaterfallText(fitted!.Transmitter(call).Audio()),
                                             path, SendSupport.Gain(LevelSlider.Value));
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
        AppSettings.TransmitLevelChanged -= ShowShared;
        _refitTimer.Stop();
        _fieldsTimer.Stop();
        _fitting?.Cancel();
        _sending?.Cancel();   // closing RFpix mid-send: stop (Transmission un-keys)
        _output.Dispose();
    }
}
