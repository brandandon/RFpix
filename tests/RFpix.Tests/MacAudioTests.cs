// MacAudioTests.cs - the Mac's sound system, for real (on GitHub's Macs; elsewhere they do nothing).
// Text goes to macOS and back unchanged, and every sound device listed can be found again by its
// ID: RFpix 0.5.2 to 0.6.0 garbled the IDs, so the Mac couldn't find the device (error -66680).

using RFpix.Audio.CoreAudio;
using Xunit;

namespace RFpix.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("macos")]   // (each test does nothing elsewhere)
public class MacAudioTests
{
    [Theory]
    [InlineData("BuiltInMicrophoneDevice")]
    [InlineData("AppleUSBAudioEngine:Burr-Brown from TI:USB Audio CODEC:14200000:1")]
    [InlineData("Radio ünïcødé ✓")]
    public void Text_goes_to_macOS_and_back_unchanged(string text)
    {
        if (!OperatingSystem.IsMacOS()) return;
        IntPtr cf = CoreAudioNative.CFStringCreateWithCharacters(IntPtr.Zero, text, text.Length);
        try { Assert.Equal(text, CoreAudioNative.FromCFString(cf)); }
        finally { CoreAudioNative.CFRelease(cf); }
    }

    [Fact]
    public void Every_device_listed_can_be_found_by_its_ID()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var devices = CoreAudioNative.ListDevices(input: true).Concat(CoreAudioNative.ListDevices(input: false)).ToList();
        foreach (var device in devices)
        {
            Assert.DoesNotContain("\0", device.Id);
            Assert.DoesNotContain("\0", device.Name);
            Assert.True(CoreAudioNative.DeviceFromUid(device.Id) != 0, $"\"{device.Name}\" ({device.Id}) can't be found by its ID");
        }
    }
}
