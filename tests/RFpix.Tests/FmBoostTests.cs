// FmBoostTests.cs - when RFpix adds FM's treble boost (pre-emphasis): only for a picture sent in data mode,
// which radios send flat. In plain FM the radio adds the boost itself, and RFpix's on top would boost the treble
// twice (N9UE's FT-991A, Nov 27, 2026).

using RFpix.Radio;
using RFpix.Views;
using Xunit;

namespace RFpix.Tests;

public class FmBoostTests
{
    [Fact]
    public void The_boost_goes_on_only_in_FM_data_mode()
    {
        Assert.Equal(SendSupport.FmKind.Fm, SendSupport.FmKindOf(HamlibNative.ModeFm));
        Assert.Equal(SendSupport.FmKind.DataFm, SendSupport.FmKindOf(HamlibNative.ModePktFm));
        Assert.Equal(SendSupport.FmKind.NotFm, SendSupport.FmKindOf(HamlibNative.ModeUsb));
        Assert.Equal(SendSupport.FmKind.NotFm, SendSupport.FmKindOf(HamlibNative.ModePktUsb));

        Assert.True(SendSupport.Boosts(SendSupport.FmKind.DataFm, switchingToDataMode: false));   // already in DATA-FM
        Assert.True(SendSupport.Boosts(SendSupport.FmKind.Fm, switchingToDataMode: true));        // RFpix switches it to FM-D
        Assert.False(SendSupport.Boosts(SendSupport.FmKind.Fm, switchingToDataMode: false));      // plain FM: the radio boosts
        Assert.False(SendSupport.Boosts(SendSupport.FmKind.NotFm, switchingToDataMode: true));    // SSB: never
    }
}
