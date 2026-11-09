// ReceivePage.Recording.cs - part of ReceivePage (ReceivePage.axaml.cs): recording what the radio hears, and playing a
// recording back into RFpix.

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
    // ---- recording what the radio hears ----

    private static string RecordingsFolder => AudioRecorder.DefaultFolder;

    private void OnRecordClick(object? sender, RoutedEventArgs e)
    {
        if (_recorder.IsRecording)
        {
            StopRecording();
            return;
        }
        try
        {
            _recordingCallsigns.Clear();
            string path = _recorder.Start(RecordingsFolder, DateTime.Now);
            RecordText.Text = $"Recording to \"{Path.GetFileName(path)}\"…";
            RecordText.Foreground = Brushes.OrangeRed;
            RecordText.IsVisible = true;
            HideError();
        }
        catch (InvalidOperationException)
        {
            ShowError("No sound has come from the radio yet. Wait a moment, then press Record again.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError($"Couldn't start a recording in {RecordingsFolder}: {ex.Message}");
        }
        UpdateStartButton();
    }

    private void StopRecording()
    {
        var (length, bytes) = _recorder.Progress;
        string? path = _recorder.Stop(_recordingCallsigns);
        ShowRecordingSaved(path, null, length, bytes);
        UpdateStartButton();
    }

    private void ShowRecordingSaved(string? path, string? why, TimeSpan length = default, long bytes = 0)
    {
        RecordText.ClearValue(TextBlock.ForegroundProperty);
        RecordText.IsVisible = true;
        if (path is null)
        {
            RecordText.Text = why ?? "";
            return;
        }
        // Next time "Play recording…" is pressed, start in the recordings folder.
        AppSettings.Current.LastRecordingFolder = Path.GetDirectoryName(path);
        AppSettings.Current.Save();
        string size = bytes > 0 ? $" ({length:h\\:mm\\:ss}, {bytes / 1e6:0.0} MB)" : "";
        RecordText.Text = (why is null ? "" : why + " ") + $"Saved \"{Path.GetFileName(path)}\"{size} in Documents\\RFpix\\Recordings";
    }

    // ---- playing a recording ----

    private async void OnPlayClick(object? sender, RoutedEventArgs e)
    {
        if (_player.IsRunning)
        {
            StopPlayback();
            return;
        }

        // Ask which file to play, starting in the folder used last time.
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null) return;

        IStorageFolder? startFolder = null;
        var lastFolder = AppSettings.Current.LastRecordingFolder;
        if (lastFolder is not null && Directory.Exists(lastFolder))
            startFolder = await storage.TryGetFolderFromPathAsync(lastFolder);

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Play a recording",
            AllowMultiple = false,
            SuggestedStartLocation = startFolder,
            FileTypeFilter = new[] { new FilePickerFileType("WAV sound files") { Patterns = new[] { "*.wav" } } },
        });

        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path is null) return;   // cancelled

        AppSettings.Current.LastRecordingFolder = Path.GetDirectoryName(path);
        AppSettings.Current.Save();

        StopListening();   // the radio and a recording can't both feed the waterfall
        try
        {
            _player.Start(path);
            AppLog.Info("Receive", $"Playing the recording \"{Path.GetFileName(path)}\".");
            HideError();
            _meterTimer.Start();
        }
        catch (AudioException ex)
        {
            ShowError(ex.Message);
        }
        UpdateStartButton();
    }

    private void StopPlayback()
    {
        _player.Stop();
        if (!_input.IsRunning) ResetMeter();
        UpdateStartButton();
    }
}
