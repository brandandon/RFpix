// DeviceIdRepairTests.cs - the Mac's sound device choices saved garbled by 0.5.2 to 0.6.0 are put right.

using RFpix.Audio;
using Xunit;

namespace RFpix.Tests;

public class DeviceIdRepairTests
{
    [Fact]
    public void A_garbled_choice_is_matched_to_the_device_it_meant()
    {
        // As 0.6.0 saved it on the Mac (seen with a stand-in for macOS's own function).
        string saved = "B\0u\0i\0l\0t\0I\0n\0M\0i\0c\0r\0o";
        Assert.True(DeviceIdRepair.LooksGarbled(saved));
        Assert.Equal(saved, DeviceIdRepair.Garbled("BuiltInMicrophoneDevice"));

        var devices = new[]
        {
            new AudioDeviceInfo("BuiltInSpeakerDevice", "MacBook Pro Speakers"),
            new AudioDeviceInfo("BuiltInMicrophoneDevice", "MacBook Pro Microphone"),
            new AudioDeviceInfo("AppleUSBAudioEngine:Burr-Brown from TI:USB Audio CODEC:14200000:1", "USB Audio CODEC"),
        };
        Assert.Equal("BuiltInMicrophoneDevice", DeviceIdRepair.Find(saved, devices)?.Id);
        Assert.Equal(devices[2], DeviceIdRepair.Find(DeviceIdRepair.Garbled(devices[2].Id), devices));
        Assert.Null(DeviceIdRepair.Find(DeviceIdRepair.Garbled("SomethingUnplugged"), devices));
    }

    [Fact]
    public void Proper_choices_are_left_alone()
    {
        Assert.False(DeviceIdRepair.LooksGarbled("BuiltInMicrophoneDevice"));
        Assert.False(DeviceIdRepair.LooksGarbled("{0.0.1.00000000}.{a1b2c3d4-0000-0000-0000-000000000000}"));   // Windows
        Assert.False(DeviceIdRepair.LooksGarbled(null));
    }
}
