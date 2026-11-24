// ListenTests.cs - the Receive tab listens for the kind of picture picked on the Send tab (decided by Dan,
// Nov 23, 2026), so on a noisy repeater frequency noise can't pass for an analog picture while digital
// pictures are being sent (seen in his log that night: four analog "pictures" between RFpix FM pictures).

using RFpix.Settings;
using RFpix.Views;
using Xunit;

namespace RFpix.Tests;

public class ListenTests
{
    [Fact]
    public void Receive_listens_for_the_kind_picked_on_the_Send_tab()
    {
        Assert.Equal(ReceivePage.ListenFor.Digital, ReceivePage.Listening(new AppSettings()));
        Assert.Equal(ReceivePage.ListenFor.Analog, ReceivePage.Listening(new AppSettings { SendAnalog = true }));
        Assert.Contains("HamDRM and RFpix FM", ReceivePage.ListenText(ReceivePage.ListenFor.Digital));
        Assert.Contains("analog SSTV", ReceivePage.ListenText(ReceivePage.ListenFor.Analog));
    }

    [Fact]
    public void The_Send_tab_offers_all_six_FM_speeds()
    {
        Assert.Equal(6, RFpix.FmSignal.FmSignalMode.Offered.Count);
        Assert.Equal("FM 6", RFpix.FmSignal.FmSignalMode.Offered[^1].ShortName);
    }
}
