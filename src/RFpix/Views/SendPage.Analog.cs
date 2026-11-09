// SendPage.Analog.cs - part of SendPage (SendPage.axaml.cs): digital or analog, and the analog SSTV picture:
// the mode, fitting the picture into it, and the preview.

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

public partial class SendPage
{
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
}
