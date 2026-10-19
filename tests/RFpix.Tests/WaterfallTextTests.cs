// WaterfallTextTests.cs - checks the waterfall text and the Morse code (CW) sounds.

using RFpix.Dsp;
using RFpix.HamDrm;
using RFpix.Send;
using Xunit;

namespace RFpix.Tests;

public class WaterfallTextTests
{
    /// <summary>The strength of one frequency in a stretch of sound (a single-frequency Fourier sum).</summary>
    private static double Strength(float[] audio, int from, int length, double hz)
    {
        double re = 0, im = 0;
        for (int n = 0; n < length; n++)
        {
            double a = 2 * Math.PI * hz * n / 48000;
            re += audio[from + n] * Math.Cos(a);
            im += audio[from + n] * Math.Sin(a);
        }
        return Math.Sqrt(re * re + im * im) / length;
    }

    // ---- waterfall text ----

    [Fact]
    public void Text_is_drawn_in_pixels_that_fit_across()
    {
        Assert.Equal(98, WaterfallText.Columns);
        var rows = WaterfallText.Pixels("KD9DD");
        Assert.All(rows, r => Assert.Equal(98, r.Length));
        Assert.True(rows.First().Any(p => p) && rows.Last().Any(p => p));   // no empty rows above or below
        Assert.Empty(WaterfallText.Pixels("   "));
    }

    [Fact]
    public void A_short_text_is_drawn_bigger_and_a_long_one_wraps()
    {
        var call = WaterfallText.Pixels("K9X");
        var callsign = WaterfallText.Pixels("KD9DD");
        Assert.True(call.Length > callsign.Length);   // three letters: enlarged more
        var wrapped = WaterfallText.Pixels("CQ SSTV NET ON THE 70 CM REPEATER");
        int lit(bool[][] r) => r.Count(row => row.Any(p => p));
        Assert.True(wrapped.Length > callsign.Length);              // several lines
        Assert.Contains(wrapped, row => !row.Any(p => p));          // with blank rows between them
        Assert.True(lit(wrapped) > 0);
    }

    [Fact]
    public void Each_row_lasts_128_ms_and_the_bottom_row_goes_first()
    {
        var rows = WaterfallText.Pixels("KD9DD");
        var audio = WaterfallText.Audio("KD9DD");
        Assert.Equal(rows.Length * 6144, audio.Length);
        Assert.Equal(rows.Length * 0.128, WaterfallText.Duration("KD9DD").TotalSeconds, 3);

        // The first 128 ms are the bottom row: its lit pixels sound, its dark ones don't
        // (measured in the middle third, past the fade-in).
        var bottom = rows[^1];
        int first = (int)Math.Round(300 / WaterfallText.PixelHz);
        for (int c = 0; c < 98; c++)
        {
            double s = Strength(audio, 2048, 2048, (first + c) * WaterfallText.PixelHz);
            if (bottom[c]) Assert.True(s > 0.005, $"pixel {c} should sound ({s})");
            else Assert.True(s < 0.0005, $"pixel {c} should be silent ({s})");
        }
    }

    [Fact]
    public void Waterfall_text_stays_within_300_to_2600_Hz_and_isnt_too_loud()
    {
        var audio = WaterfallText.Audio("KD9DD");
        Assert.True(audio.Max(Math.Abs) < 0.5);
        Assert.True(Strength(audio, 0, 2048 * 3, 150) < 0.0005);
        Assert.True(Strength(audio, 0, 2048 * 3, 2800) < 0.0005);
    }

    // ---- Morse code ----

    [Fact]
    public void Morse_timing_follows_the_standard()
    {
        Assert.Equal(new[] { true }, MorseCode.Pattern("E"));
        Assert.Equal(new[] { true, true, true }, MorseCode.Pattern("t"));
        // A then N: .-  (3-dit gap)  -.
        Assert.Equal("1011100011101", string.Concat(MorseCode.Pattern("AN").Select(b => b ? '1' : '0')));
        Assert.Equal(1 + 7 + 1, MorseCode.Pattern("E E").Length);
        // "PARIS" plus the gap before the next word is 50 dits: 4 s at 15 words per minute.
        Assert.Equal(43, MorseCode.Pattern("PARIS").Length);
        Assert.Equal(43 * 0.08, MorseCode.Audio("PARIS").Length / 48000.0, 3);
        Assert.Equal("-..-.", MorseCode.Code('/'));
        Assert.Null(MorseCode.Code('#'));
    }

    [Fact]
    public void Morse_is_a_750_Hz_tone_that_starts_and_stops_gently()
    {
        var audio = MorseCode.Audio("E");   // one dit: 80 ms
        Assert.Equal(3840, audio.Length);
        Assert.True(Strength(audio, 480, 2400, 750) > 0.1);
        Assert.True(Strength(audio, 480, 2400, 900) < 0.01);
        Assert.True(Math.Abs(audio[0]) < 0.001 && Math.Abs(audio[^1]) < 0.01);   // no click at either end
        Assert.True(audio.Max(Math.Abs) <= MorseCode.Amplitude + 1e-6);
    }

    [Fact]
    public void A_picture_with_text_before_and_after_still_decodes()
    {
        var test = TestTransmissions.All.Single(t => t.Number == 4);   // the shortest test
        var tx = test.Transmitter("KD9DD");
        var signal = tx.Audio();
        var sound = WaterfallText.Around(signal, "CQ NET", "73 KD9DD");
        Assert.Equal(signal.Length / 48000.0 + WaterfallText.Extra("CQ NET", "73 KD9DD").TotalSeconds,
                     sound.Length / 48000.0, 2);
        Assert.Equal(signal, WaterfallText.Around(signal, "", null));   // no text: just the picture

        var (received, _, _) = TransmitterTests.ReceiveSound(sound);
        Assert.Equal(test.Picture(), received?.Data);
    }
}
