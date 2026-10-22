// SstvTests.cs - checks analog SSTV sending: the modes' timing, the VIS code and the tones.
// (Every mode was also checked by decoding RFpix's sound with an independent SSTV decoder,
// which recognised each mode from its VIS code and decoded the picture: Oct 20, 2026.)

using RFpix.Analog;
using RFpix.Imaging;
using Xunit;

namespace RFpix.Tests;

public class SstvTests
{
    private static DecodedPicture Flat(int w, int h, byte r, byte g, byte b)
    {
        var bgra = new byte[4 * w * h];
        for (int i = 0; i < w * h; i++) { bgra[4 * i] = b; bgra[4 * i + 1] = g; bgra[4 * i + 2] = r; bgra[4 * i + 3] = 255; }
        return new DecodedPicture(w, h, bgra);
    }

    /// <summary>The tone's frequency over a stretch, from its zero crossings.</summary>
    private static double FrequencyAt(float[] audio, double fromMs, double ms)
    {
        int from = (int)(fromMs * 48), n = (int)(ms * 48);
        int crossings = 0;
        double first = -1, last = -1;
        for (int i = from + 1; i < from + n; i++)
            if (audio[i - 1] < 0 && audio[i] >= 0)
            {
                double t = i - 1 + audio[i - 1] / (audio[i - 1] - audio[i]);
                if (first < 0) first = t; else crossings++;
                last = t;
            }
        return crossings * 48000.0 / (last - first);
    }

    private static void Near(double expected, double actual, double within) =>
        Assert.True(Math.Abs(expected - actual) <= within, $"expected {expected}, got {actual}");

    [Theory]
    [InlineData(SstvMode.Martin1, 114.3)]
    [InlineData(SstvMode.Martin2, 58.1)]
    [InlineData(SstvMode.Scottie1, 109.6)]
    [InlineData(SstvMode.Scottie2, 71.1)]
    [InlineData(SstvMode.ScottieDX, 268.9)]
    [InlineData(SstvMode.Robot36, 36.0)]
    [InlineData(SstvMode.Robot72, 72.0)]
    [InlineData(SstvMode.PD50, 49.7)]
    [InlineData(SstvMode.PD90, 90.0)]
    [InlineData(SstvMode.PD120, 126.1)]
    [InlineData(SstvMode.PD160, 160.9)]
    [InlineData(SstvMode.PD180, 187.1)]
    [InlineData(SstvMode.PD240, 248.0)]
    [InlineData(SstvMode.PD290, 288.7)]
    public void Each_mode_takes_its_standard_time(SstvMode mode, double seconds)
    {
        var m = SstvModes.Get(mode);
        Assert.Equal(seconds, m.PictureTime.TotalSeconds, 0);   // to the second: the standard figures
        Assert.True(Math.Abs(m.PictureTime.TotalSeconds - seconds) < 0.1);
        // The line's parts add up to the line time.
        double parts = m.Color switch
        {
            SstvColor.Gbr when m.Name.StartsWith("Martin") => m.SyncMs + m.PorchMs + 3 * (m.ScanMs + m.GapMs),
            SstvColor.Gbr => m.SyncMs + 3 * (m.GapMs + m.ScanMs),
            SstvColor.Robot when mode == SstvMode.Robot36 => m.SyncMs + m.PorchMs + m.ScanMs + m.GapMs + 1.5 + m.ScanMs / 2,
            SstvColor.Robot => m.SyncMs + m.PorchMs + m.ScanMs + 2 * (m.GapMs + 1.5 + m.ScanMs / 2),
            _ => m.SyncMs + m.PorchMs + 4 * m.ScanMs,
        };
        Assert.Equal(m.LineMs, parts, 2);
    }

    [Fact]
    public void The_sound_lasts_exactly_as_long_as_the_mode_says()
    {
        var m = SstvModes.Get(SstvMode.Robot36);
        var audio = SstvEncoder.Audio(Flat(320, 240, 128, 128, 128), m);
        Assert.Equal(SstvEncoder.Duration(m).TotalSeconds * 48000, audio.Length, 0);
        Assert.Equal(0.910, SstvModes.VisTime.TotalSeconds, 3);
    }

    [Fact]
    public void The_VIS_code_says_which_mode_follows()
    {
        // Martin 1 is VIS 44 = 0101100: bits (lowest first) 0,0,1,1,0,1,0, three ones so parity 1.
        var audio = SstvEncoder.Audio(Flat(320, 256, 0, 0, 0), SstvModes.Get(SstvMode.Martin1));
        Assert.Equal(1900, FrequencyAt(audio, 20, 250), 0);    // leader
        Assert.Equal(1900, FrequencyAt(audio, 330, 250), 0);   // leader again, after the 1200 Hz break
        Near(1200, FrequencyAt(audio, 615, 20), 20);   // start bit
        int[] bits = { 0, 0, 1, 1, 0, 1, 0, 1 };               // 7 data bits, then parity
        for (int i = 0; i < bits.Length; i++)
            Near(bits[i] == 1 ? 1100 : 1300, FrequencyAt(audio, 640 + 30 * i + 3, 24), 20);
        Near(1200, FrequencyAt(audio, 885, 20), 20);   // stop bit
        Assert.Equal(SstvMode.Martin1, SstvModes.FromVis(44)!.Mode);
        Assert.Null(SstvModes.FromVis(1));
    }

    [Fact]
    public void Shades_become_frequencies_from_1500_to_2300_Hz()
    {
        Assert.Equal(1500, SstvModes.Frequency(0));
        Assert.Equal(2300, SstvModes.Frequency(255));
        Assert.Equal(1900, SstvModes.Frequency(127.5));

        // A Martin 1 line of pure green: green scan white, blue and red black.
        var m = SstvModes.Get(SstvMode.Martin1);
        var audio = SstvEncoder.Audio(Flat(320, 256, 0, 255, 0), m, vis: false);
        double line = 5 * m.LineMs;   // somewhere in the picture
        Near(1200, FrequencyAt(audio, line + 0.5, 4), 20);                                   // sync
        Assert.Equal(2300, FrequencyAt(audio, line + 10, 130), 0);                                   // green
        Assert.Equal(1500, FrequencyAt(audio, line + 5.434 + 146.432 + 0.572 + 10, 130), 0);         // blue
    }

    [Fact]
    public void The_tone_never_jumps()
    {
        // From sample to sample the wave moves smoothly (no clicks where the frequency changes).
        var audio = SstvEncoder.Audio(Flat(320, 240, 200, 30, 90), SstvModes.Get(SstvMode.Robot36));
        double most = 0;
        for (int i = 1; i < audio.Length; i++) most = Math.Max(most, Math.Abs(audio[i] - audio[i - 1]));
        // At 2300 Hz the steepest step is 2 pi f / fs x amplitude = 0.075.
        Assert.True(most < 0.08, $"largest step {most}");
        Assert.True(audio.Max(Math.Abs) <= SstvEncoder.Amplitude + 1e-6);
    }

    [Fact]
    public void A_picture_is_fitted_by_filling_the_mode_and_cutting_the_edges()
    {
        var wide = Flat(800, 400, 10, 20, 30);
        var fitted = SstvEncoder.Fit(wide, SstvModes.Get(SstvMode.Martin1));
        Assert.Equal(320, fitted.Width);
        Assert.Equal(256, fitted.Height);
        Assert.Equal(30, fitted.Bgra[0]);   // the same colour (blue first in BGRA)
        var small = SstvEncoder.Fit(Flat(100, 75, 1, 2, 3), SstvModes.Get(SstvMode.PD120));
        Assert.Equal(640, small.Width);
        Assert.Equal(496, small.Height);
        Assert.Throws<ArgumentException>(() => SstvEncoder.Audio(wide, SstvModes.Get(SstvMode.Martin1)));
    }

    // ---- receiving ----

    private static DecodedPicture Pattern(int w, int h)
    {
        var bgra = new byte[4 * w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = 4 * (y * w + x);
                double fx = (double)x / w, fy = (double)y / h;
                bgra[i] = (byte)(127 + 120 * Math.Sin(fx * 9 + fy * 3));
                bgra[i + 1] = (byte)(255 * fx);
                bgra[i + 2] = (byte)(255 * fy);
                bgra[i + 3] = 255;
            }
        return new DecodedPicture(w, h, bgra);
    }

    private static double Psnr(DecodedPicture a, DecodedPicture b)
    {
        double mse = 0;
        for (int i = 0; i < a.Bgra.Length; i++) if (i % 4 != 3) { double d = a.Bgra[i] - b.Bgra[i]; mse += d * d; }
        mse /= a.Bgra.Length * 3 / 4.0;
        return 10 * Math.Log10(255 * 255 / mse);
    }

    private static SstvPicture? Receive(float[] audio, out SstvModeInfo? started)
    {
        var rx = new SstvReceiver();
        SstvPicture? got = null;
        SstvModeInfo? s = null;
        rx.Started += m => s = m;
        rx.Finished += p => got = p;
        var withSilence = new float[audio.Length + 96000];
        audio.CopyTo(withSilence, 48000);
        for (int at = 0; at < withSilence.Length; at += 4800)
            rx.AddSamples(withSilence.AsSpan(at, Math.Min(4800, withSilence.Length - at)));
        rx.End();
        started = s;
        return got;
    }

    [Theory]
    [InlineData(SstvMode.Robot36, 26)]
    [InlineData(SstvMode.Martin2, 30)]
    [InlineData(SstvMode.Scottie2, 30)]
    [InlineData(SstvMode.PD50, 30)]
    public void A_picture_sent_is_received_in_the_mode_its_VIS_code_names(SstvMode mode, double quality)
    {
        var m = SstvModes.Get(mode);
        var picture = SstvEncoder.Fit(Pattern(400, 300), m);
        var got = Receive(SstvEncoder.Audio(picture, m), out var started);
        Assert.Equal(mode, started!.Mode);
        Assert.NotNull(got);
        Assert.True(got!.Complete);
        Assert.Equal(m.Height, got.Lines);
        double psnr = Psnr(picture, got.Picture);
        Assert.True(psnr > quality, $"quality {psnr:0.0} dB");
    }

    [Fact]
    public void A_sender_whose_sound_card_runs_fast_doesnt_slant_the_picture()
    {
        var m = SstvModes.Get(SstvMode.Martin2);
        var picture = SstvEncoder.Fit(Pattern(400, 300), m);
        // 400 ppm fast: the sound plays 400 ppm shorter, so each line ends 0.09 ms early.
        var audio = RFpix.Audio.ClockCorrector.Resample(SstvEncoder.Audio(picture, m), 1 / (1 + 400e-6));
        var got = Receive(audio, out _);
        Assert.NotNull(got);
        Assert.True(Psnr(picture, got!.Picture) > 30);
    }

    [Fact]
    public void A_picture_cut_short_is_kept_as_far_as_it_got()
    {
        var m = SstvModes.Get(SstvMode.Robot36);
        var audio = SstvEncoder.Audio(SstvEncoder.Fit(Pattern(320, 240), m), m);
        var half = audio.AsSpan(0, (int)(SstvModes.VisTime.TotalSeconds * 48000 + audio.Length) / 2).ToArray();
        var got = Receive(half, out _);
        Assert.NotNull(got);
        Assert.False(got!.Complete);
        Assert.InRange(got.Lines, 100, 140);
        Assert.Null(Receive(new float[48000 * 3], out var nothing));   // silence: nothing
        Assert.Null(nothing);
    }

    [Theory]
    [InlineData(SstvMode.Robot36)]
    [InlineData(SstvMode.Robot72)]
    [InlineData(SstvMode.Martin1)]
    public void The_edges_of_the_picture_keep_their_colour(SstvMode mode)
    {
        // The sync after Robot's colour scan once turned the right-hand edge green.
        var m = SstvModes.Get(mode);
        var picture = Flat(m.Width, m.Height, 130, 120, 110);
        var got = Receive(SstvEncoder.Audio(picture, m), out _)!.Picture;
        int y = m.Height / 2;
        foreach (int x in new[] { 0, 1, m.Width - 2, m.Width - 1 })
        {
            int i = 4 * (y * m.Width + x);
            Assert.True(Math.Abs(got.Bgra[i + 2] - 130) <= 8 && Math.Abs(got.Bgra[i + 1] - 120) <= 8 && Math.Abs(got.Bgra[i] - 110) <= 8,
                        $"pixel {x}: ({got.Bgra[i + 2]}, {got.Bgra[i + 1]}, {got.Bgra[i]})");
        }
    }

    // ---- MMSSTV's preamble, and the FSK ID ----

    private static float[] Tones(IEnumerable<(double Hz, double Ms)> tones, double noise = 0, int seed = 1)
    {
        var rnd = new Random(seed);
        var audio = new List<float>();
        double phase = 0;
        foreach (var (hz, ms) in tones)
            for (int i = 0; i < (int)Math.Round(ms * 48); i++)
            {
                phase += 2 * Math.PI * hz / 48000;
                audio.Add((float)(0.25 * Math.Sin(phase) + noise * (rnd.NextDouble() * 2 - 1)));
            }
        return audio.ToArray();
    }

    private static List<string> Ids(float[] audio)
    {
        var rx = new SstvReceiver();
        var ids = new List<string>();
        rx.IdReceived += ids.Add;
        var padded = new float[audio.Length + 48000];
        audio.CopyTo(padded, 24000);
        for (int at = 0; at < padded.Length; at += 4800)
            rx.AddSamples(padded.AsSpan(at, Math.Min(4800, padded.Length - at)));
        return ids;
    }

    [Fact]
    public void The_FSK_ID_is_coded_as_MMSSTV_and_QSSTV_send_it()
    {
        // Header 0x2A, the characters less 0x20, the end 0x01, then the characters XORed.
        Assert.Equal(new[] { 0x2A, 0x21, 0x22, 0x01, 0x21 ^ 0x22 }, FskId.Symbols("ab"));
        Assert.Equal("N0CALL/P", FskId.Clean(" n0call/p "));
        Assert.Empty(FskId.Tones(""));
        var tones = FskId.Tones("AB");
        Assert.Equal((1500.0, 300.0), tones[0]);
        Assert.Equal((2100.0, 100.0), tones[1]);
        Assert.Equal(3 + 5 * 6 + 1, tones.Count);
        Assert.Equal(300 + 100 + 22 + 30 * 22 + 100, FskId.Duration("AB").TotalMilliseconds, 3);
    }

    [Fact]
    public void A_picture_goes_out_with_the_preamble_first_and_the_FSK_ID_after()
    {
        var m = SstvModes.Get(SstvMode.Robot36);
        var picture = SstvEncoder.Fit(Pattern(320, 240), m);
        var audio = SstvEncoder.Audio(picture, m, preamble: true, fskId: "N0CALL");
        Assert.Equal(SstvEncoder.Duration(m, preamble: true, fskId: "N0CALL").TotalSeconds * 48000, audio.Length, 0);
        double[] expected = { 1900, 1500, 1900, 1500, 2300, 1500, 2300, 1500 };
        for (int i = 0; i < 8; i++) Near(expected[i], FrequencyAt(audio, 100 * i + 20, 60), 5);
        Near(1900, FrequencyAt(audio, 820, 250), 5);   // then the VIS leader

        var rx = new SstvReceiver();
        SstvPicture? got = null;
        var ids = new List<string>();
        rx.Finished += p => got = p;
        rx.IdReceived += ids.Add;
        var padded = new float[audio.Length + 96000];
        audio.CopyTo(padded, 48000);
        for (int at = 0; at < padded.Length; at += 4800) rx.AddSamples(padded.AsSpan(at, Math.Min(4800, padded.Length - at)));
        rx.End();
        Assert.NotNull(got);
        Assert.True(Psnr(picture, got!.Picture) > 26);
        Assert.Equal(new[] { "N0CALL" }, ids);
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.15, 0.0)]       // noisy
    [InlineData(0.3, 0.0)]        // very noisy (as loud as the signal)
    [InlineData(0.0, 500e-6)]     // the sender's sound card 500 ppm slow
    [InlineData(0.05, -500e-6)]
    public void The_FSK_ID_is_read(double noise, double ppm)
    {
        var audio = Tones(FskId.Tones("N0CALL/P"), noise);
        if (ppm != 0) audio = RFpix.Audio.ClockCorrector.Resample(audio, 1 + ppm);
        Assert.Equal(new[] { "N0CALL/P" }, Ids(audio));
    }

    [Fact]
    public void An_FSK_ID_with_the_wrong_check_is_ignored()
    {
        var tones = FskId.Tones("N0CALL").ToList();
        int lastCheckBit = tones.Count - 2;   // before the closing 100 ms of 1900 Hz
        tones[lastCheckBit] = (tones[lastCheckBit].Hz == FskId.OneHz ? FskId.ZeroHz : FskId.OneHz, FskId.BitMs);
        Assert.Empty(Ids(Tones(tones)));
        Assert.Empty(Ids(Tones(new[] { (1500.0, 2000.0), (2100.0, 500.0), (1900.0, 500.0) })));   // just tones
    }
}
