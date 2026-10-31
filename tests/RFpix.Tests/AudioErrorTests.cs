// AudioErrorTests.cs - Windows reports sound problems as numbers; RFpix says them in plain words
// with what to do (AudioErrors.cs). Checked here on every computer: the explanations are just text.
// (Code review, Oct 2026: this wording had no test.)

using System.Runtime.InteropServices;
using RFpix.Audio;
using Xunit;

namespace RFpix.Tests;

public class AudioErrorTests
{
    [Theory]
    [InlineData(0x80070005u, "Microphone access")]               // blocked by Windows' privacy settings
    [InlineData(0x8889000Au, "Another program has taken over")]  // in use by another program
    [InlineData(0x88890004u, "disappeared")]                     // unplugged or switched off
    [InlineData(0x80070490u, "isn't connected right now")]       // not there
    [InlineData(0x88890008u, "16 bit, 48000 Hz")]                // a format Windows refuses
    public void Windows_sound_errors_are_explained_with_what_to_do(uint code, string advice)
    {
        string text = AudioErrors.Explain(new COMException("(Windows' own wording)", unchecked((int)code)));
        Assert.Contains(advice, text);
        Assert.DoesNotContain("Windows' own wording", text);
    }

    [Fact]
    public void Other_errors_keep_Windows_message_and_code_and_plain_ones_pass_through()
    {
        Assert.Equal("Could not use the sound device: Something odd (Windows code 0x88890099)",
                     AudioErrors.Explain(new COMException("Something odd", unchecked((int)0x88890099))));
        Assert.Equal("Already in plain words.", AudioErrors.Explain(new AudioException("Already in plain words.")));
    }
}
