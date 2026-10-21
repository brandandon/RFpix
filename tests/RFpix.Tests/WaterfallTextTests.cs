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

    // ---- waterfall pictures ----

    private static RFpix.Imaging.DecodedPicture Gradient(int width, int height)
    {
        // Black at the left to white at the right.
        var bgra = new byte[4 * width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                byte v = (byte)(255 * x / (width - 1));
                int i = 4 * (y * width + x);
                bgra[i] = bgra[i + 1] = bgra[i + 2] = v;
                bgra[i + 3] = 255;
            }
        return new RFpix.Imaging.DecodedPicture(width, height, bgra);
    }

    [Fact]
    public void A_picture_is_fitted_into_a_square_108_across()
    {
        var wide = WaterfallPicture.Levels(Gradient(400, 100), invert: false);
        Assert.Equal(27, wide.Length);                      // 108 across, so 108 x 27 rows
        Assert.All(wide, r => Assert.Equal(108, r.Length));
        var square = WaterfallPicture.Levels(Gradient(300, 300), invert: false);
        Assert.Equal(108, square.Length);                   // fills the square
        Assert.True(square[0][107] > 0);
        var tall = WaterfallPicture.Levels(Gradient(100, 400), invert: false);
        Assert.Equal(108, tall.Length);                     // 108 rows, so 27 across, centred
        Assert.Equal(0, tall[0][30]);                       // outside the picture: silent
        Assert.True(tall[0][66] > 0);
        Assert.Equal(13.8, WaterfallPicture.Duration(108).TotalSeconds, 1);
    }

    [Fact]
    public void Shades_are_spread_over_30_dB_and_black_is_silent()
    {
        Assert.Equal(1, WaterfallPicture.Level(1));
        Assert.Equal(0, WaterfallPicture.Level(0.02));
        Assert.Equal(-15, 20 * Math.Log10(WaterfallPicture.Level(0.5)), 6);
        Assert.Equal(0.5, WaterfallPicture.Shade(WaterfallPicture.Level(0.5)), 6);

        var levels = WaterfallPicture.Levels(Gradient(108, 108), invert: false);
        Assert.Equal(0, levels[0][0]);                      // black at the left
        Assert.Equal(1, levels[0][107], 2);                 // white at the right
        var inverted = WaterfallPicture.Levels(Gradient(108, 108), invert: true);
        Assert.Equal(1, inverted[0][0], 2);
        Assert.Equal(0, inverted[0][107]);
    }

    [Fact]
    public void A_waterfall_picture_sounds_its_shades_bottom_row_first_from_250_Hz()
    {
        var levels = new double[2][];
        levels[0] = new double[108];   // top row: one bright pixel at column 0 (250 Hz)
        levels[1] = new double[108];   // bottom row: one faint pixel at column 107 (top of the band)
        levels[0][0] = 1;
        levels[1][107] = WaterfallPicture.Level(0.5);
        var audio = WaterfallPicture.Audio(levels);
        Assert.Equal(2 * 6144, audio.Length);
        int first = (int)Math.Round(250 / WaterfallText.PixelHz);
        Assert.True((first + 107) * WaterfallText.PixelHz < 2800);
        double faint = Strength(audio, 2048, 2048, (first + 107) * WaterfallText.PixelHz);    // first 128 ms: the bottom row
        double bright = Strength(audio, 6144 + 2048, 2048, first * WaterfallText.PixelHz);   // then the top row
        Assert.True(Strength(audio, 2048, 2048, first * WaterfallText.PixelHz) < 0.0005);
        Assert.Equal(-15, 20 * Math.Log10(faint / bright), 0);
        Assert.Equal(108, WaterfallPicture.Preview(levels).Width);
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
