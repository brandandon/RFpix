// FmLevelCheckTests.cs - the level check for RFpix's FM signal (FmLevelCheck.cs, new in 0.7.4): six frames 3 dB
// apart; every listening RFpix measures each step's quality from the known data and shows the best step to read
// out to the sender (decided by Dan, Nov 23, 2026).

using RFpix.FmSignal;
using RFpix.Tests.Bench;
using Xunit;

namespace RFpix.Tests;

public class FmLevelCheckTests
{
    [Fact]
    public void The_steps_go_3_dB_apart_up_to_6_dB_above_the_transmit_level_but_never_past_0_dB()
    {
        Assert.Equal(new[] { -9, -6, -3, 0, 3, 6 }, FmLevelCheck.Offsets(-20));
        Assert.Equal(new[] { -13, -10, -7, -4, -1, 2 }, FmLevelCheck.Offsets(-2));
        Assert.Equal(new[] { -15, -12, -9, -6, -3, 0 }, FmLevelCheck.Offsets(0));
        foreach (int step in Enumerable.Range(0, 6))
            foreach (int offset in new[] { -15, -9, 0, 6 })
                Assert.Equal(new FmLevelStep(step, offset), FmLevelCheck.FromHeader(FmLevelCheck.Header(new FmLevelStep(step, offset))));
        Assert.Null(FmLevelCheck.FromHeader(2 << 8 | 7));   // a picture's frame (FM 3)
    }

    private static FmLevelResult? Heard(float[] heard)
    {
        var rx = new FmSignalLiveReceiver();
        var results = new List<FmLevelResult>();
        bool other = false;
        rx.LevelCheckHeard += results.Add;
        rx.FileReceived += (_, _) => other = true;
        rx.RequestHeard += _ => other = true;
        for (int i = 0; i < heard.Length; i += 4800) rx.AddSamples(heard[i..Math.Min(heard.Length, i + 4800)], Math.Min(4800, heard.Length - i), 48000);
        rx.EndOfAudio();
        Assert.False(other);
        Assert.True(results.Count <= 1, $"{results.Count} level checks heard");
        return results.FirstOrDefault();
    }

    private static float[] Burst(double transmitDb)
    {
        var burst = FmLevelCheck.Audio(FmLevelCheck.Offsets(transmitDb));
        var padded = new float[burst.Length + 96000];
        burst.CopyTo(padded, 48000);
        return padded;
    }

    [Fact]
    public void Driven_into_the_limiter_the_best_step_is_below_the_loudest_and_the_steps_above_it_clip()
    {
        // Through a repeater, a strong signal, with the burst's middle driven hot into the limiters.
        var result = Heard(FmChannel.Apply(Burst(-20), new FmCondition("hot", -3, Repeater: true), 30, seed: 1))!;
        Assert.Equal(6, result.Steps.Count);
        var best = result.Best;
        Assert.InRange(best.Step.Step, 1, 3);            // not the loudest: clipped above it
        Assert.True(best.QualityDb > 24, $"best {best.QualityDb:0.0} dB");
        Assert.True(result.Steps[^1].QualityDb < best.QualityDb - 8, "the loudest step clips");
        Assert.Contains($"Read out \"step {best.Step.Step + 1}\"", result.Text);
    }

    [Fact]
    public void On_a_weak_signal_below_the_limiter_quality_keeps_rising_and_says_to_go_louder()
    {
        var result = Heard(FmChannel.Apply(Burst(-20), FmCondition.RepeaterRight, 15, seed: 2))!;
        Assert.Equal(6, result.Steps.Count);
        for (int i = 1; i < 6; i++) Assert.True(result.Steps[i].QualityDb > result.Steps[i - 1].QualityDb + 1.5);
        Assert.Equal(5, result.Best.Step.Step);
        Assert.Contains("can go louder", result.Text);
    }

    [Fact]
    public void A_strong_listener_wants_a_little_less_level_than_a_weak_one_through_the_same_repeater()
    {
        // Clipping is the same for every listener; noise isn't: the weak one gains from more level (Nov 23 notes).
        var hot = new FmCondition("hot", -3, Repeater: true);
        var strong = Heard(FmChannel.Apply(Burst(-20), hot, 30, seed: 3))!;
        var weak = Heard(FmChannel.Apply(Burst(-20), hot, 15, seed: 3))!;
        Assert.True(weak.Best.Step.Step >= strong.Best.Step.Step, $"weak {weak.Best.Step}, strong {strong.Best.Step}");
    }

    [Fact]
    public void A_receiver_that_doesnt_know_level_checks_finds_no_picture_in_one()
    {
        var reception = FmSignalReceiver.Decode(Burst(-20));
        Assert.Empty(reception.Files);
        Assert.All(reception.Frames, f => Assert.Null(f.Mode));
    }
}
