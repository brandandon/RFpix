// ReceivePage.LevelCheck.cs - part of ReceivePage (ReceivePage.axaml.cs): a level check heard (FmLevelCheck.cs,
// new in 0.7.4). Each step's quality shows as it arrives; at the end, the best step, to read out to the sender
// over the air (decided by Dan, Nov 23, 2026: no reply frame), with the quality at every step. The result is
// also passed to the TX/RX page, so a station that hears its own check back sees it on its Send tab.

using Avalonia.Threading;
using RFpix.Diagnostics;
using RFpix.FmSignal;

namespace RFpix.Views;

public partial class ReceivePage
{
    /// <summary>A level check was heard to the end (on the screen's thread).</summary>
    public event Action<FmLevelResult>? LevelCheckHeard;

    private void WireLevelCheck()
    {
        _fm.LevelStepHeard += (step, quality) => Dispatcher.UIThread.Post(() =>
        {
            LevelCheckText.Text = $"Level check arriving: {step} of {FmLevelCheck.Steps}, quality {quality:0} dB…";
            LevelCheckText.IsVisible = true;
        });
        _fm.LevelCheckHeard += result =>
        {
            AppLog.Info("Receive", result.Text);
            Dispatcher.UIThread.Post(() =>
            {
                LevelCheckText.Text = result.Text;
                LevelCheckText.IsVisible = true;
                LevelCheckHeard?.Invoke(result);
            });
        };
    }
}
