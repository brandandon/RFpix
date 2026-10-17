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

    public void Dispose()
    {
        SendTab.Dispose();      // stops a transmission in progress (the radio is released)
        ReceiveTab.Dispose();   // stops listening, keeps a recording in progress
    }
}
