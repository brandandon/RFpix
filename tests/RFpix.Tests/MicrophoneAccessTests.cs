// MicrophoneAccessTests.cs - the Mac asks once whether RFpix may listen (the radio's sound input
// counts as a microphone). While it asks, starting to listen says so, marked as asking; the answer
// then reaches the Receive tab, which starts listening by itself after Allow (code review, Oct 2026).
// The question itself can only be answered by a person at a Mac, so this checks the plumbing.

using RFpix.Audio;
using Xunit;

namespace RFpix.Tests;

public class MicrophoneAccessTests
{
    [Fact]
    public void The_answer_reaches_whoever_is_waiting_for_it()
    {
        var answers = new List<bool>();
        void Heard(bool allowed) { lock (answers) answers.Add(allowed); }
        MicrophoneAccess.Answered += Heard;
        try
        {
            MicrophoneAccess.RaiseAnswered(true);
            MicrophoneAccess.RaiseAnswered(false);
        }
        finally
        {
            MicrophoneAccess.Answered -= Heard;
        }
        Assert.Equal(new[] { true, false }, answers);
    }

    [Fact]
    public void Only_the_question_is_marked_as_asking()
    {
        Assert.False(new AudioException("The sound device disappeared.").AskingForPermission);
        Assert.True(new AudioException("The Mac is asking...") { AskingForPermission = true }.AskingForPermission);
    }
}
