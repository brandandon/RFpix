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

    private IReadOnlyList<IncompleteFile> _incomplete = Array.Empty<IncompleteFile>();

    /// <summary>Lists the pictures still missing pieces (the latest first), keeping the choice.</summary>
    private void ShowIncomplete()
    {
        var chosen = MissingBox.SelectedIndex >= 0 && MissingBox.SelectedIndex < _incomplete.Count
            ? _incomplete[MissingBox.SelectedIndex].File.TransportId : (int?)null;
        _incomplete = _receiver.Incomplete;
        MissingBox.ItemsSource = _incomplete.Select(Describe).ToList();
        int index = chosen is int id ? _incomplete.ToList().FindIndex(i => i.File.TransportId == id) : -1;
        MissingBox.SelectedIndex = _incomplete.Count == 0 ? -1 : Math.Max(0, index);
        MissingPanel.IsVisible = _incomplete.Count > 0 || _asking is not null;
        ShowAskHint();
    }

    private static string Describe(IncompleteFile i) =>
        $"{i.File.Name ?? "(name not received)"}" + (string.IsNullOrEmpty(i.Callsign) ? "" : $" from {i.Callsign}") +
        $"  ·  {i.File.Missing.Count} of {i.File.Pieces} missing  ·  {i.Heard:HH:mm}";

    private void OnMissingChanged(object? sender, SelectionChangedEventArgs e) => ShowAskHint();

    private void ShowAskHint()
    {
        if (_asking is not null) return;   // the request's progress is showing
        if (MissingBox.SelectedIndex < 0 || MissingBox.SelectedIndex >= _incomplete.Count) { AskText.Text = ""; return; }
        var i = _incomplete[MissingBox.SelectedIndex];
        var seconds = i.Transmitter("X", transportId: 0).Duration.TotalSeconds;
        string who = string.IsNullOrEmpty(i.Callsign) ? "The sender's" : $"{i.Callsign}'s";
        AskText.Text = $"Sends a short request ({seconds:0} s) in the settings the picture came with. {who} RFpix, EasyPal or QSSTV " +
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
        string what = $"a request for {picture.File.Missing.Count} missing pieces";
        try
        {
            await SendSupport.SendSoundAsync(() => picture.Transmitter(call).Audio(), what, _output,
                SendSupport.SavedGain, m => ShowAskStatus(m, problem: false),
                new Progress<double>(f => AskProgress.Value = f), _asking.Token, SendSupport.SavedLeadIn);
            _askedFor.Add(picture.File.TransportId);
            ShowAskStatus($"Asked for {picture.File.Missing.Count} missing pieces of {picture.File.Name ?? "the picture"}. " +
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
