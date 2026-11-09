// ReceivePage.Analog.cs - part of ReceivePage (ReceivePage.axaml.cs): analog SSTV pictures arriving: shown as they
// fill in, saved (or not, by the Gallery setting) when they end, waiting through a fade, and the FSK ID.

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.Input;
using Avalonia;
using Avalonia.VisualTree;
using RFpix.Audio;
using RFpix.Diagnostics;
using RFpix.Dsp;
using RFpix.Gallery;
using RFpix.HamDrm;
using RFpix.Radio;
using RFpix.Imaging;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using RFpix.Receive;
using RFpix.Settings;

namespace RFpix.Views;

public partial class ReceivePage
{
    // ---- analog SSTV ----

    private void ShowAnalogStart(RFpix.Analog.SstvModeInfo mode)
    {
        ++_pictureCount;   // a HamDRM picture still being decoded won't replace this one
        _analogMode = mode;
        _analogFrom = "";
        _lastAnalog = null;
        _unkeptAnalog = null;
        PictureCaption.Text = $"Receiving analog SSTV: {mode.Name} ({mode.Width} × {mode.Height})" +
                              (_sstv.ReceivingLate ? ", joined partway (its VIS code was missed)" : "");
        AppLog.Info("Receive", $"Analog SSTV {mode.Name} arriving" + (_sstv.ReceivingLate ? ", found without its VIS code." : " (its VIS code heard)."));
    }

    /// <summary>The picture's signal has gone: it's kept, in case it comes back before the picture's end.</summary>
    private void ShowAnalogFaded(int lines)
    {
        if (_analogMode is not { } mode) return;
        PictureCaption.Text = $"Analog SSTV {mode.Name}: the signal faded at line {lines}. Waiting for it to come back…";
        AppLog.Info("Receive", $"Analog SSTV {mode.Name}: the signal faded at line {lines}; waiting for it to come back before saving.");
    }

    private void ShowAnalogResumed(int line)
    {
        if (_analogMode is not { } mode) return;
        PictureCaption.Text = $"Receiving analog SSTV: {mode.Name} ({mode.Width} × {mode.Height}): the signal came back, carrying on from line {line}";
        AppLog.Info("Receive", $"Analog SSTV {mode.Name}: the signal came back on the same picture's beat; carrying on from line {line}.");
    }

    private void ShowAnalogProgress(DecodedPicture picture, int lines)
    {
        var old = PictureImage.Source as IDisposable;
        PictureImage.Source = Bitmaps.From(picture);
        old?.Dispose();
        PicturePlaceholder.IsVisible = false;
    }

    private async void ShowAnalogPicture(RFpix.Analog.SstvPicture got)
    {
        ShowAnalogProgress(got.Picture, got.Lines);
        var when = got.Ended;
        // An FSK ID heard while it arrived, or while it waited after fading out (it often comes then):
        // the sender, saved with it.
        string? from = _analogFrom.Length > 0 ? _analogFrom : null;
        if (from is not null) LastPicture = new(from, got.Ended.ToUniversalTime(), Digital: false, got.Mode.Name);
        string status = got.JoinedLate ? "  ·  joined partway (no VIS code)"
                      : got.Complete ? "" : $"  ·  cut short: {got.Lines} of {got.Mode.Height} lines";
        _analogCaption = $"Analog SSTV {got.Mode.Name}  ·  {when:HH:mm}{status}" + (from is null ? "" : $"  ·  from {from}");

        // A picture found without its VIS goes to the Gallery only if enough of it arrived
        // (Settings > Misc), and to the website only by its own rule (Settings > FTP server).
        var settings = AppSettings.Current;
        if (settings.Upload.ShouldCopyAnalog(got.JoinedLate, got.Lines, got.Mode.Height))
        {
            var copy = got.Picture;
            _ = Task.Run(() => RFpix.Upload.UploadService.SaveCopy(copy, when, analog: true));
        }
        if (!settings.ShouldKeepAnalog(got.JoinedLate, got.Lines, got.Mode.Height))
        {
            string note = NotKeptNote(settings.KeepNoVis, got.Lines, got.Mode.Height);
            if (from is null) _unkeptAnalog = (note, DateTime.Now);
            PictureCaption.Text = $"{_analogCaption}\n{note}";
            AppLog.Info("Receive", $"Analog SSTV {got.Mode.Name}: {got.Lines} of {got.Mode.Height} lines, without its VIS code; not kept in the Gallery (Settings > Misc).");
            return;
        }
        try
        {
            var png = await Task.Run(() => Pictures.EncodePng(got.Picture));
            string name = $"SSTV {got.Mode.Name}.png";
            string savedAs = Path.GetFileName(ReceivedFileSaver.Save(new ReceivedFile(name, png, 0), AnalogFolder, null, when));
            var entry = new GalleryEntry(savedAs, when, name, from, $"Analog SSTV  ·  {got.Mode.Name}" + (got.JoinedLate ? "  ·  joined partway" : ""));
            GalleryIndex.Add(AnalogFolder, entry);
            if (from is null) _lastAnalog = (AnalogFolder, entry, DateTime.Now);   // for an FSK ID still to come
            SendSupport.RaiseGalleryChanged();
            PictureCaption.Text = $"{_analogCaption}\nSaved as \"{savedAs}\"";
            AppLog.Info("Receive", $"Analog SSTV {got.Mode.Name}: {got.Lines} of {got.Mode.Height} lines" +
                                   (got.JoinedLate ? ", without its VIS code" : "") + $", saved as \"{savedAs}\".");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PictureException)
        {
            ShowError($"Received an analog SSTV picture ({got.Mode.Name}), but couldn't save it to {AnalogFolder}: {ex.Message}");
        }
    }

    /// <summary>The note under a picture that isn't kept in the Gallery: "Not saved to the Gallery:
    /// only 30% received", or, with the setting off, that pictures without their VIS aren't kept.</summary>
    internal static string NotKeptNote(bool keepingSome, int lines, int height) =>
        keepingSome
            ? $"Not saved to the Gallery: only {(height > 0 ? lines * 100L / height : 0)}% received"
            : "Not saved to the Gallery: pictures without their VIS code aren't kept (Settings > Misc)";

    /// <summary>An FSK ID: the sender's callsign, shown in the From box and added to the picture just saved.</summary>
    private void ShowAnalogId(string call)
    {
        AppLog.Info("Receive", $"FSK ID heard: {call}");
        _analogFrom = call;
        // The FSK ID comes just after the picture: it's that picture's sender (for Log QSO).
        if (_analogMode is { } heardMode)
            LastPicture = new(call, (_lastAnalog?.Entry.When ?? DateTime.Now).ToUniversalTime(), Digital: false, heardMode.Name);
        if (_lastAnalog is null && _unkeptAnalog is { } unkept && DateTime.Now - unkept.At <= TimeSpan.FromSeconds(20))
        {
            _unkeptAnalog = null;
            PictureCaption.Text = $"{_analogCaption}  ·  from {call}\n{unkept.Note}";
            return;
        }
        if (_lastAnalog is not { } last || DateTime.Now - last.Saved > TimeSpan.FromSeconds(20)) return;
        var (folder, entry, _) = last;
        _lastAnalog = null;
        try
        {
            GalleryIndex.Add(folder, entry with { Callsign = call });
            SendSupport.RaiseGalleryChanged();
            PictureCaption.Text = $"{_analogCaption}  ·  from {call}\nSaved as \"{entry.File}\"";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The picture is saved already; only the callsign beside it is lost.
        }
    }
}
