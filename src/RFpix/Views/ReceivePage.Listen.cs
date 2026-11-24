// ReceivePage.Listen.cs - part of ReceivePage (ReceivePage.axaml.cs): what the Receive tab listens for follows
// the Send tab (decided by Dan, Nov 23, 2026). On Digital it decodes HamDRM and RFpix FM only; on Analog SSTV,
// analog pictures only. On a busy or noisy frequency (an FM repeater's noise
// between pictures, say) noise could pass for an analog picture's VIS code and take over the picture and the
// boxes while digital pictures were being sent; listening only for the kind in use stops that.

using RFpix.Settings;

namespace RFpix.Views;

public partial class ReceivePage
{
    internal enum ListenFor { Digital, Analog }

    internal static ListenFor Listening(AppSettings settings) =>
        settings.SendAnalog ? ListenFor.Analog : ListenFor.Digital;

    internal static string ListenText(ListenFor listen) => listen switch
    {
        ListenFor.Analog => "Listening for analog SSTV pictures, as picked on the Send tab",
        _ => "Listening for digital pictures (HamDRM and RFpix FM), as picked on the Send tab",
    };

    private ListenFor _listening = Listening(AppSettings.Current);

    /// <summary>On the pump's thread: what to listen for now. If the Send tab changed, whatever was arriving
    /// of the old kind is finished first (a picture part way through is shown and saved as far as it got).</summary>
    private ListenFor ListenNow()
    {
        var now = Listening(AppSettings.Current);
        if (now == _listening) return now;
        switch (_listening)
        {
            case ListenFor.Digital:
                _receiver.EndOfAudio(); _receiver.Reset();
                _fm.EndOfAudio(); _fm.Reset();
                break;
            case ListenFor.Analog:
                _sstv.End();
                break;
        }
        _listening = now;
        return now;
    }

    private string _listenShown = "";

    private void ShowListening()
    {
        string text = ListenText(Listening(AppSettings.Current));
        if (text == _listenShown) return;
        _listenShown = text;
        ListenTextBlock.Text = text;
    }
}
