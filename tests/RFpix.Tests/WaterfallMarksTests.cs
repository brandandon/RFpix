// WaterfallMarksTests.cs - the Receive tab's waterfall marks follow the kind of picture being
// received (analog SSTV or HamDRM), and go back to the Send tab's setting when it's changed.

using RFpix.Receive;
using Xunit;

namespace RFpix.Tests;

public class WaterfallMarksTests
{
    [Fact]
    public void They_start_by_matching_the_Send_tab()
    {
        Assert.True(new WaterfallMarks(sendAnalog: true).Analog);
        Assert.False(new WaterfallMarks(sendAnalog: false).Analog);
        // Nothing arriving: still as Send.
        Assert.True(new WaterfallMarks(true).Update(true, analogArriving: false, digitalArriving: false));
    }

    [Fact]
    public void They_switch_when_the_other_kind_arrives_and_stay_after_it_ends()
    {
        var marks = new WaterfallMarks(sendAnalog: true);
        Assert.False(marks.Update(true, false, digitalArriving: true));   // a HamDRM transmission arrives
        Assert.False(marks.Update(true, false, false));                   // it ends: still HamDRM's marks
        Assert.False(marks.Update(true, false, false));
        Assert.True(marks.Update(true, analogArriving: true, false));     // an analog picture arrives
        Assert.True(marks.Update(true, false, false));                    // and they stay analog after it
    }

    [Fact]
    public void Changing_the_Send_setting_puts_them_back_to_match_it()
    {
        var marks = new WaterfallMarks(sendAnalog: false);
        Assert.True(marks.Update(false, analogArriving: true, false));
        Assert.True(marks.Update(false, false, false));                   // the analog picture ended: stays analog
        Assert.True(marks.Update(sendAnalog: true, false, false));        // Send changed to analog: analog
        Assert.False(marks.Update(sendAnalog: false, false, false));      // and back to HamDRM: HamDRM's marks
        Assert.False(marks.Update(false, false, false));

        // While a picture is arriving, its own marks show, whatever Send says.
        Assert.True(marks.Update(sendAnalog: false, analogArriving: true, false));
        Assert.False(marks.Update(sendAnalog: true, false, digitalArriving: true));
        Assert.False(marks.Update(true, false, false));                   // Send unchanged since: stays HamDRM
    }
}
