// TxRxPage.axaml.cs - the code half of the TX/RX page. The two tabs do all the work
// (ReceivePage and SendPage); this page only hands closing-down on to them.

using Avalonia.Controls;

namespace RFpix.Views;

public partial class TxRxPage : UserControl, IDisposable
{
    public TxRxPage()
    {
        InitializeComponent();
    }

    // Show the chosen tab and hide the other (both keep running).
    private void OnTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ReceiveTab is null || SendTab is null) return;   // still being built
        ReceiveTab.IsVisible = Tabs.SelectedIndex == 0;
        SendTab.IsVisible = Tabs.SelectedIndex == 1;
    }

    public void Dispose()
    {
        SendTab.Dispose();      // stops a transmission in progress (the radio is released)
        ReceiveTab.Dispose();   // stops listening, keeps a recording in progress
    }
}
