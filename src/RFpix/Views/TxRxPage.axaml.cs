// TxRxPage.axaml.cs - the code half of the TX/RX page. The two tabs do most of the work
// (ReceivePage and SendPage). This page also answers requests for missing pieces (BSR):
//   1. The Receive tab hears a request (BsrHeard).
//   2. If it's for one of the last 6 pictures sent from here (SentLog), the bar above the
//      tabs says who asked, for which picture and how long the answer takes; nothing is
//      sent until "Send the pieces" is clicked. Otherwise a short note says it was for
//      someone else.
//   3. "Send the pieces" sends only those pieces (a "fix"), with the picture's own settings.

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using RFpix.Audio;
using RFpix.HamDrm;
using RFpix.Radio;
using RFpix.Send;

namespace RFpix.Views;

public partial class TxRxPage : UserControl, IDisposable
{
    private readonly IAudioOutput _output = AudioSystem.CreateOutput();
    private readonly DispatcherTimer _noteTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private FixPlan? _plan;                  // the request on show
    private CancellationTokenSource? _fixing;

    public TxRxPage()
    {
        InitializeComponent();
        ReceiveTab.BsrHeard += OnBsrHeard;
        _noteTimer.Tick += (_, _) => { _noteTimer.Stop(); NoteBar.IsVisible = false; };
    }

    // Show the chosen tab and hide the other (both keep running).
    private void OnTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ReceiveTab is null || SendTab is null) return;   // still being built
        ReceiveTab.IsVisible = Tabs.SelectedIndex == 0;
        SendTab.IsVisible = Tabs.SelectedIndex == 1;
    }

    /// <summary>Shows the Send tab with this picture opened (the Gallery's "Send this picture").</summary>
    public async void ShowSendTab(string picturePath)
    {
        Tabs.SelectedIndex = 1;
        await SendTab.OpenPictureAsync(picturePath);
    }

    // ---- requests for missing pieces ----

    private void OnBsrHeard(HeardBsr heard)
    {
        string who = string.IsNullOrEmpty(heard.Callsign) ? "A station" : heard.Callsign;
        var plan = SentLog.Current.Answer(heard.Request);
        if (plan is null)
        {
            NoteText.Text = $"{who} asked for missing pieces of a picture that wasn't sent from here.";
            NoteBar.IsVisible = true;
            _noteTimer.Stop();
            _noteTimer.Start();
            return;
        }
        if (_fixing is not null) return;   // already answering one: this one can be asked again

        _plan = plan;
        var seconds = plan.Transmitter("X").Duration.TotalSeconds + plan.File.LeadInSeconds;
        RequestTitle.Text = $"{who} asked for {plan.Pieces.Count} missing piece{(plan.Pieces.Count == 1 ? "" : "s")} of {plan.File.ShownName}";
        RequestText.Text = $"You sent it at {plan.File.Sent:HH:mm}. Sending the pieces takes about {(int)seconds / 60}:{(int)seconds % 60:00} " +
                           "on the air, with the picture's own settings.";
        RequestText.ClearValue(TextBlock.ForegroundProperty);
        SendPiecesButton.Content = "Send the pieces";
        SendPiecesButton.IsVisible = NotNowButton.IsVisible = true;
        NotNowButton.Content = "Not now";
        RequestBar.IsVisible = true;
    }

    private async void OnSendPiecesClick(object? sender, RoutedEventArgs e)
    {
        if (_fixing is not null) { _fixing.Cancel(); return; }   // the button says Stop
        if (_plan is not { } plan) return;
        string call = SendSupport.Callsign;
        if (SendSupport.Problem(_output, call) is { } problem) { ShowRequestStatus(problem, problem: true); return; }
        if (SendSupport.IsSending) { ShowRequestStatus("RFpix is already transmitting. Wait for it to finish, then click again.", problem: true); return; }

        _fixing = new CancellationTokenSource();
        SendPiecesButton.Content = "Stop";
        NotNowButton.IsVisible = false;
        RequestProgress.Value = 0;
        RequestProgress.IsVisible = true;
        bool done = false;
        try
        {
            await SendSupport.SendSoundAsync(() => plan.Transmitter(call).Audio(),
                $"{plan.Pieces.Count} pieces of {plan.File.ShownName}", _output, SendSupport.SavedGain,
                m => ShowRequestStatus(m, problem: false), new Progress<double>(f => RequestProgress.Value = f),
                _fixing.Token, TimeSpan.FromSeconds(plan.File.LeadInSeconds));
            done = true;
            ShowRequestStatus($"Sent the {plan.Pieces.Count} pieces of {plan.File.ShownName}.", problem: false);
        }
        catch (OperationCanceledException)
        {
            ShowRequestStatus("Stopped. The transmitter is released.", problem: false);
        }
        catch (Exception ex) when (ex is AudioException or RadioException)
        {
            ShowRequestStatus(ex.Message, problem: true);
        }
        finally
        {
            _fixing.Dispose();
            _fixing = null;
            RequestProgress.IsVisible = false;
            SendPiecesButton.Content = "Send the pieces";
            NotNowButton.IsVisible = true;
            if (done)
            {
                // Done: the bar stays, saying so, until Close.
                SendPiecesButton.IsVisible = false;
                NotNowButton.Content = "Close";
            }
        }
    }

    private void OnNotNowClick(object? sender, RoutedEventArgs e)
    {
        RequestBar.IsVisible = false;
        NotNowButton.Content = "Not now";
        _plan = null;
    }

    private void ShowRequestStatus(string message, bool problem)
    {
        RequestText.Text = message;
        if (problem) RequestText.Foreground = Avalonia.Media.Brushes.OrangeRed;
        else RequestText.ClearValue(TextBlock.ForegroundProperty);
    }

    public void Dispose()
    {
        _noteTimer.Stop();
        _fixing?.Cancel();      // closing RFpix mid-fix: stop (Transmission un-keys)
        _output.Dispose();
        SendTab.Dispose();      // stops a transmission in progress (the radio is released)
        ReceiveTab.Dispose();   // stops listening, keeps a recording in progress
    }
}
