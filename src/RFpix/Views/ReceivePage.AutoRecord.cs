// ReceivePage.AutoRecord.cs - part of ReceivePage (ReceivePage.axaml.cs): "Record each picture" (asked by Dan,
// Nov 28, 2026). While ticked, each picture of the kind RFpix is listening for is recorded by itself, from 10 seconds
// before it was found to 5 seconds after it ends (AutoRecordPolicy), into Documents\RFpix\Recordings\Automatic.
// Old ones are cleared out as set on Settings > Misc (AutoRecordCleanup).

using Avalonia.Interactivity;
using Avalonia.Media;
using RFpix.Diagnostics;
using RFpix.Receive;
using RFpix.Settings;

namespace RFpix.Views;

public partial class ReceivePage
{
    private readonly AutoRecordPolicy _autoPolicy = new();
    private bool _autoRecording;     // the recording running was started by "Record each picture"
    private string? _autoLabel;      // what it is recording, for its name ("SSTV Martin 2", "RFpix FM"...)

    /// <summary>Sets up the tick box and clears out old automatic recordings (from the constructor).</summary>
    private void SetUpAutoRecord()
    {
        AutoRecordBox.IsChecked = AppSettings.Current.AutoRecord;
        CleanAutoRecordings(null);
    }

    private void OnAutoRecordClick(object? sender, RoutedEventArgs e)
    {
        bool on = AutoRecordBox.IsChecked == true;
        AppSettings.Current.AutoRecord = on;
        AppSettings.Current.Save();
        AppLog.Info("Receive", on ? $"Record each picture: on (into {AutoRecordCleanup.Folder})." : "Record each picture: off.");
        if (!on && _autoRecording) StopRecording();   // keep what it has so far
    }

    /// <summary>Whether a picture of the kind being listened for is arriving, and what it is (for the file's name).</summary>
    private (bool Busy, string? Label) PictureArriving()
    {
        switch (_listening)
        {
            case ListenFor.Analog:
                return _sstv.Receiving is { } mode ? (true, "SSTV " + mode.Name) : (false, null);
            default:
                if (_fm.Heard is not null) return (true, "RFpix FM");
                return _receiver.Heard is { Fac: not null } ? (true, "HamDRM") : (false, null);
        }
    }

    /// <summary>Every moment while listening to the radio (from UpdateMeter): start or stop the automatic recording.</summary>
    private void UpdateAutoRecord()
    {
        if (!AppSettings.Current.AutoRecord || !_input.IsRunning) return;
        var (busy, label) = PictureArriving();
        if (_autoRecording && label is not null) _autoLabel ??= label;
        switch (_autoPolicy.Update(busy, DateTime.Now, _autoRecording, otherRecording: _recorder.IsRecording && !_autoRecording))
        {
            case AutoRecordStep.Start:
                StartAutoRecording(label);
                break;
            case AutoRecordStep.Stop:
                StopRecording();
                break;
        }
    }

    private void StartAutoRecording(string? label)
    {
        try
        {
            _recordingCallsigns.Clear();
            string path = _recorder.Start(AutoRecordCleanup.Folder, DateTime.Now, AutoRecordPolicy.Lead);
            _autoRecording = true;
            _autoLabel = label;
            RecordText.Text = $"Recording this picture to \"{Path.GetFileName(path)}\"…";
            RecordText.Foreground = Brushes.OrangeRed;
            RecordText.IsVisible = true;
            AppLog.Info("Receive", $"Record each picture: {label ?? "a picture"} found, recording to \"{Path.GetFileName(path)}\".");
        }
        catch (InvalidOperationException)
        {
            _autoPolicy.Hold();   // no sound yet: try again with the next picture
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _autoPolicy.Hold();
            ShowError($"Couldn't record this picture in {AutoRecordCleanup.Folder}: {ex.Message}");
        }
        UpdateStartButton();
    }

    /// <summary>The automatic recording has stopped (from StopRecording, or by itself): note it, don't start another
    /// for the same picture, and clear out old ones.</summary>
    private void AutoRecordingStopped(string? path)
    {
        _autoRecording = false;
        _autoLabel = null;
        _autoPolicy.Hold();   // ends once the picture is over (no effect if it already is)
        if (path is not null) AppLog.Info("Receive", $"Record each picture: saved \"{Path.GetFileName(path)}\".");
        CleanAutoRecordings(path);
    }

    /// <summary>Deletes automatic recordings older than the days set, then the oldest beyond the MB set (Settings >
    /// Misc), off the screen's thread. <paramref name="justSaved"/> is never deleted.</summary>
    private static void CleanAutoRecordings(string? justSaved)
    {
        int days = AppSettings.Current.AutoRecordDays, maxMb = AppSettings.Current.AutoRecordMaxMb;
        Task.Run(() =>
        {
            var deleted = AutoRecordCleanup.Clean(AutoRecordCleanup.Folder, days, maxMb, DateTime.Now, justSaved);
            if (deleted.Count > 0)
                AppLog.Info("Receive", $"Record each picture: deleted {deleted.Count} old automatic recording(s) " +
                                       $"(kept {days} day(s), at most {maxMb} MB): {string.Join(", ", deleted)}.");
        });
    }
}
