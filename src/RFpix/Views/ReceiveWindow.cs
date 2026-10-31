// ReceiveWindow.cs - the Receive tab in a window of its own ("Pop out" on the TX/RX page),
// so receiving and sending can be watched side by side, or on two monitors.
//
// The Receive tab itself moves into this window: listening, recording and decoding carry on
// without a break. Closing the window puts it back on the TX/RX page. Its size and place are
// remembered (AppSettings.ReceiveWindow; see RememberedWindow).

using Avalonia;
using Avalonia.Controls;
using RFpix.Settings;

namespace RFpix.Views;

public sealed class ReceiveWindow : RememberedWindow
{
    public ReceiveWindow(Control receiveTab)
        : base(() => AppSettings.Current.ReceiveWindow, placement => AppSettings.Current.ReceiveWindow = placement)
    {
        Title = "RFpix – Receive";
        Width = 1000;
        Height = 760;
        MinWidth = 700;
        MinHeight = 500;
        Padding = new Thickness(24, 16);
        Content = new RFpix.Controls.FitToWindow(receiveTab);   // shrinks to fit a small window
        RestorePlacement();
    }

    /// <summary>Gives the Receive tab back (the window closes empty).</summary>
    public Control? TakeBack()
    {
        var tab = (Content as RFpix.Controls.FitToWindow)?.Release();
        Content = null;
        return tab;
    }
}
