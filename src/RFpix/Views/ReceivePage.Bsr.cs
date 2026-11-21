// ReceivePage.Bsr.cs - part of ReceivePage (ReceivePage.axaml.cs): missing pieces (BSR): the pictures still
// missing pieces, and asking their sender for them.

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
using RFpix.FmSignal;
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
    // ---- missing pieces (BSR) ----

    /// <summary>A picture still missing pieces: one from HamDRM, or one from RFpix's FM signal.</summary>
    private sealed record MissingPicture(IncompleteFile? HamDrm, FmUnfinishedFile? Fm)
    {
        public int Id => HamDrm?.File.TransportId ?? Fm!.FileNumber;
        public DateTime Heard => HamDrm?.Heard ?? Fm!.LastHeard;
        public string? Name => HamDrm is not null ? HamDrm.File.Name : Fm!.Name;
        public string? Callsign => HamDrm is not null ? HamDrm.Callsign : Fm!.Callsign;
        public int? StillNeeded => HamDrm is not null ? HamDrm.StillNeeded : Math.Max(1, Fm!.StillNeeded);
        public int Asking => HamDrm?.Asking.Count ?? Math.Max(1, Fm!.StillNeeded);

        /// <summary>The request's sound, from <paramref name="callsign"/>.</summary>
        public float[] Audio(string callsign) => HamDrm is not null
            ? HamDrm.Transmitter(callsign).Audio()
            : FmSignalTransmitter.RequestAudio(new FmRequest(Fm!.FileNumber, Fm.SourcePieces, Fm.PieceBytes, Math.Max(1, Fm.StillNeeded), callsign));

        /// <summary>How long the request is on the air, in seconds.</summary>
        public double Seconds => HamDrm is not null ? HamDrm.Transmitter("X", transportId: 0).Duration.TotalSeconds
                                                    : FmSignalLayout.FrameSeconds;
    }

    private IReadOnlyList<MissingPicture> _incomplete = Array.Empty<MissingPicture>();

    /// <summary>Lists the pictures still missing pieces (the latest first), keeping the choice.</summary>
    private void ShowIncomplete()
    {
        var chosen = MissingBox.SelectedIndex >= 0 && MissingBox.SelectedIndex < _incomplete.Count
            ? _incomplete[MissingBox.SelectedIndex].Id : (int?)null;
        _incomplete = _receiver.Incomplete.Select(i => new MissingPicture(i, null))
            // (not the one arriving now: it may yet complete)
            .Concat(_fm.Unfinished.Where(f => _fm.Heard?.File?.FileNumber != f.FileNumber).Select(f => new MissingPicture(null, f)))
            .OrderByDescending(m => m.Heard).ToList();
        MissingBox.ItemsSource = _incomplete.Select(Describe).ToList();
        int index = chosen is int id ? _incomplete.ToList().FindIndex(i => i.Id == id) : -1;
        MissingBox.SelectedIndex = _incomplete.Count == 0 ? -1 : Math.Max(0, index);
        MissingPanel.IsVisible = _incomplete.Count > 0 || _asking is not null;
        ShowAskHint();
    }

    private static string Describe(MissingPicture m) =>
        $"{m.Name ?? "(name not received)"}" + (string.IsNullOrEmpty(m.Callsign) ? "" : $" from {m.Callsign}") +
        (m.StillNeeded is int n ? $"  ·  needs {n} more piece{(n == 1 ? "" : "s")}" : $"  ·  {m.HamDrm!.File.Missing.Count} of {m.HamDrm.File.Pieces} missing") +
        (m.Fm is not null ? "  ·  RFpix FM" : "") + $"  ·  {m.Heard:HH:mm}";

    private void OnMissingChanged(object? sender, SelectionChangedEventArgs e) => ShowAskHint();

    private void ShowAskHint()
    {
        if (_asking is not null) return;   // the request's progress is showing
        if (MissingBox.SelectedIndex < 0 || MissingBox.SelectedIndex >= _incomplete.Count) { AskText.Text = ""; return; }
        var i = _incomplete[MissingBox.SelectedIndex];
        var seconds = i.Seconds;
        string who = string.IsNullOrEmpty(i.Callsign) ? "The sender's" : $"{i.Callsign}'s";
        AskText.Text = i.Fm is not null
            ? $"Sends a short request ({seconds:0} s, in RFpix's FM signal) for {i.Asking} more piece{(i.Asking == 1 ? "" : "s")}. " +
              $"{who} RFpix answers with new repair pieces: any {i.Asking} of them complete it here. Keep listening for them."
            : i.StillNeeded is int n
            ? $"Sends a short request ({seconds:0} s) in the settings the picture came with, for {n} more piece{(n == 1 ? "" : "s")}. " +
              $"It came with the fountain code, so {(string.IsNullOrEmpty(i.Callsign) ? "the sender's" : who)} RFpix answers with new repair pieces: any {n} of them complete it here. Keep listening for them."
            : $"Sends a short request ({seconds:0} s) in the settings the picture came with. {who} RFpix, EasyPal or QSSTV " +
              "can then send just those pieces, and the picture completes here. Keep listening for them.";
    }

    private async void OnAskClick(object? sender, RoutedEventArgs e)
    {
        if (_asking is not null) { _asking.Cancel(); return; }   // the button says Stop
        if (MissingBox.SelectedIndex < 0 || MissingBox.SelectedIndex >= _incomplete.Count) return;
        var picture = _incomplete[MissingBox.SelectedIndex];
        string call = SendSupport.Callsign;
        if (SendSupport.Problem(_output, call) is { } problem) { ShowAskStatus(problem, problem: true); return; }
        if (SendSupport.IsSending) { ShowAskStatus("RFpix is already transmitting. Wait for it to finish.", problem: true); return; }

        _asking = new CancellationTokenSource();
        AskButton.Content = "Stop";
        MissingBox.IsEnabled = false;
        AskProgress.Value = 0;
        AskProgress.IsVisible = true;
        string what = $"a request for {picture.Asking} missing pieces";
        try
        {
            await SendSupport.SendSoundAsync(() => picture.Audio(call), what, _output,
                SendSupport.SavedGain, m => ShowAskStatus(m, problem: false),
                new Progress<double>(f => AskProgress.Value = f), _asking.Token, SendSupport.SavedLeadIn, hamDrm: true);
            _askedFor.Add(picture.Id);
            ShowAskStatus($"Asked for {picture.Asking} missing pieces of {picture.Name ?? "the picture"}. " +
                          "If the sender answers, the picture completes here and is saved.", problem: false);
        }
        catch (OperationCanceledException)
        {
            ShowAskStatus("Stopped. The transmitter is released.", problem: false);
        }
        catch (Exception ex) when (ex is AudioException or RadioException)
        {
            ShowAskStatus(ex.Message, problem: true);
        }
        finally
        {
            _asking.Dispose();
            _asking = null;
            AskButton.Content = "Ask for them (BSR)";
            MissingBox.IsEnabled = true;
            AskProgress.IsVisible = false;
            MissingPanel.IsVisible = _incomplete.Count > 0 || AskText.Text?.Length > 0;
        }
    }

    private void ShowAskStatus(string message, bool problem) => StatusLine.Show(AskText, message, problem, "Receive");
}
