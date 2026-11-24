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

    private static SstvPicture? Receive(float[] audio, out SstvModeInfo? started, bool autoSlant = true)
    {
        var rx = new SstvReceiver { AutoSlant = autoSlant };
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
    public void With_auto_slant_off_a_fast_sound_card_slants_the_picture_as_in_MMSSTV()
    {
        var m = SstvModes.Get(SstvMode.Martin2);
        var picture = SstvEncoder.Fit(Pattern(400, 300), m);
        var audio = RFpix.Audio.ClockCorrector.Resample(SstvEncoder.Audio(picture, m), 1 / (1 + 400e-6));
        var straight = Receive(audio, out _, autoSlant: true)!.Picture;
        var slanted = Receive(audio, out _, autoSlant: false)!.Picture;
        // Off, the lines drift across by 0.09 ms a line: 23 ms (about 100 pixels) by the bottom of Martin 2.
        Assert.True(Psnr(picture, straight) > 30);
        Assert.True(Psnr(picture, slanted) < Psnr(picture, straight) - 6, $"off {Psnr(picture, slanted):0.0} dB, on {Psnr(picture, straight):0.0} dB");
        // A steady sound card: the same either way.
        var steady = SstvEncoder.Audio(picture, m);
        Assert.True(Psnr(picture, Receive(steady, out _, autoSlant: false)!.Picture) > 30);
    }

    // ---- weak and fading signals (measured on the HF bench, Nov 2026) ----

    /// <summary>The picture's sound with a second of silence either side and noise at
    /// <paramref name="snrDb"/> (in 2.5 kHz, as the HF bench).</summary>
    private static float[] Noisy(float[] audio, double snrDb, int seed = 3)
    {
        var padded = new float[audio.Length + 96000];
        audio.CopyTo(padded, 48000);
        return TestAudio.AddNoise(padded, snrDb, seed);
    }

    [Fact]
    public void A_VIS_code_in_heavy_noise_still_starts_the_picture_from_its_top()
    {
        // At 8 dB the frequency of each moment flickers wildly; the VIS code's tones still stand out.
        // (Before Nov 2026 RFpix missed it below about 16 dB and found the picture late, from its syncs.)
        var m = SstvModes.Get(SstvMode.PD50);
        var picture = SstvEncoder.Fit(Pattern(320, 256), m);
        var got = Receive(Noisy(SstvEncoder.Audio(picture, m), 8), out var started);
        Assert.Equal(SstvMode.PD50, started!.Mode);
        Assert.NotNull(got);
        Assert.False(got!.JoinedLate);
        Assert.True(got.Complete);
    }

    [Fact]
    public void A_short_dropout_in_the_VIS_code_doesnt_lose_it()
    {
        // A fade that takes out 12 ms of the stop bit and of two data bits.
        var m = SstvModes.Get(SstvMode.Martin2);
        var audio = SstvEncoder.Audio(SstvEncoder.Fit(Pattern(320, 256), m), m);
        double visEndMs = SstvModes.VisTime.TotalMilliseconds;
        foreach (double at in new[] { visEndMs - 20, visEndMs - 100, visEndMs - 190 })
            for (int i = (int)(at * 48); i < (int)((at + 12) * 48); i++) audio[i] = 0;
        var got = Receive(Noisy(audio, 25), out var started);
        Assert.Equal(SstvMode.Martin2, started!.Mode);
        Assert.False(got!.JoinedLate);
        Assert.True(got.Complete);
    }

    [Fact]
    public void Lines_lost_in_a_deep_fade_are_drawn_from_the_lines_around_them_not_as_random_colour()
    {
        // A plain picture, its sound faded 30 dB for two seconds in the middle, in noise at 20 dB.
        var m = SstvModes.Get(SstvMode.Martin2);
        var picture = Flat(m.Width, m.Height, 150, 110, 70);
        var audio = SstvEncoder.Audio(picture, m);
        int from = audio.Length / 2, to = from + 2 * 48000;
        for (int i = from; i < to; i++) audio[i] *= 0.03f;
        var got = Receive(Noisy(audio, 20), out _)!.Picture;
        // The faded lines (about 9 of them) stay close to the picture's colour.
        double lineMs = m.LineMs, visMs = SstvModes.VisTime.TotalMilliseconds;
        int first = (int)((from / 48.0 - visMs) / lineMs) + 1, last = (int)((to / 48.0 - visMs) / lineMs) - 1;
        double worst = 0;
        for (int y = first; y <= last; y++)
        {
            double sum = 0;
            for (int x = 20; x < m.Width - 20; x++)
            {
                int i = 4 * (y * m.Width + x);
                sum += Math.Abs(got.Bgra[i + 2] - 150) + Math.Abs(got.Bgra[i + 1] - 110) + Math.Abs(got.Bgra[i] - 70);
            }
            worst = Math.Max(worst, sum / (3 * (m.Width - 40)));
        }
        Assert.True(worst < 20, $"faded lines off by {worst:0} on average");
    }

    [Fact]
    public void A_weak_fading_signal_gives_grain_not_streaks_down_the_picture()
    {
        // A plain picture, weak and fading all the way through (as on a poor band): what noise is left should
        // be grain, not copied down the picture line after line (which shows as vertical streaks). Seen on
        // air, 23 November 2026: each weak line drawn mostly from the one before streaked whole pictures.
        // (Drawn that way, this one's noise was 0.15 alike 3 lines apart, 0.14 to 0.21 with other noise; now
        // it's 0.02, 0.01 to 0.06.)
        var m = SstvModes.Get(SstvMode.Martin2);
        var audio = SstvEncoder.Audio(Flat(m.Width, m.Height, 128, 128, 128), m);
        var padded = new float[audio.Length + 96000];
        audio.CopyTo(padded, 48000);
        var faded = RFpix.Tests.Bench.HfChannel.Apply(padded, RFpix.Tests.Bench.HfCondition.Moderate, seed: 1);
        var got = Receive(TestAudio.AddNoise(faded, 8, seed: 1), out _)!.Picture;
        // How alike the noise is from one line to the line 3 below, on the green (middle of each line).
        int w = m.Width, h = m.Height;
        double[,] g = new double[h, w];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) g[y, x] = got.Bgra[4 * (y * w + x) + 1];
        double sxy = 0, sxx = 0, syy = 0, mean = 0; int n = 0;
        for (int y = 20; y < h - 20; y++) for (int x = 20; x < w - 20; x++) { mean += g[y, x]; n++; }
        mean /= n;
        for (int y = 20; y < h - 23; y++)
            for (int x = 20; x < w - 20; x++)
            {
                double a = g[y, x] - mean, b = g[y + 3, x] - mean;
                sxy += a * b; sxx += a * a; syy += b * b;
            }
        double down3 = sxy / Math.Sqrt(sxx * syy);
        Assert.True(down3 < 0.09, $"noise 3 lines apart alike by {down3:0.00}");
    }

    // A picture heard as on the HF bench: a second of quiet either side, the fading path, then noise (the
    // same seeds the bench uses for that path, signal level and try).
    private static float[] OverHf(float[] audio, RFpix.Tests.Bench.HfCondition path, int pathIndex, double snrDb, int seed, int cutAt = -1)
    {
        var padded = new float[audio.Length + 96000];
        audio.CopyTo(padded, 48000);
        var heard = path.SpreadHz > 0 ? RFpix.Tests.Bench.HfChannel.Apply(padded, path, seed * 7919 + pathIndex) : padded;
        var noisy = TestAudio.AddNoise(heard, snrDb, seed * 104729 + (int)(snrDb * 31) + pathIndex);
        if (cutAt >= 0) for (int i = 48000 + cutAt; i < noisy.Length; i++) noisy[i] -= heard[i];   // the sender stops; the noise carries on
        return noisy;
    }

    [Fact]
    public void A_weak_picture_whose_syncs_are_lost_is_still_drawn_to_the_end()
    {
        // Seen on air, 23 November 2026: a weak Scottie 1 stopped at line 92 of 256 while MMSSTV drew it all.
        // RFpix lost the syncs and waited for them; they never came back clearly, so it kept only what came
        // before. On the bench, this one stopped at line 49; now it's drawn to the end on its timing.
        var m = SstvModes.Get(SstvMode.Scottie1);
        var audio = SstvEncoder.Audio(SstvEncoder.Fit(Pattern(320, 256), m), m);
        var got = Receive(OverHf(audio, RFpix.Tests.Bench.HfCondition.NoiseOnly, 0, 2, seed: 1), out _);
        Assert.NotNull(got);
        Assert.Equal(256, got!.Lines);
        Assert.True(got.Complete);
    }

    [Fact]
    public void A_sender_who_stops_part_way_gives_the_part_sent_not_noise_after_it()
    {
        // Drawn on its timing after the syncs go, a picture would otherwise fill up with noise to the end:
        // it ends where the sync tone could last be heard on its beat.
        var m = SstvModes.Get(SstvMode.Scottie1);
        var audio = SstvEncoder.Audio(SstvEncoder.Fit(Pattern(320, 256), m), m);
        int cutAt = (int)((SstvModes.VisTime.TotalMilliseconds + 0.4 * m.SoundLines * m.LineMs) * 48);   // at line 102
        var got = Receive(OverHf(audio, RFpix.Tests.Bench.HfCondition.Moderate, 2, 4, seed: 2, cutAt), out _);
        Assert.NotNull(got);
        Assert.False(got!.Complete);
        Assert.InRange(got.Lines, 95, 125);
    }

    [Fact]
    public void A_picture_whose_start_the_VIS_code_put_late_finds_its_syncs()
    {
        // In noise and echo the VIS code's end can come out 10-30 ms late, beyond the sync search: no sync was
        // ever found, the picture stopped at line 20, and a "joined partway" picture took over. The first lines'
        // syncs now put the start right. (This one's VIS code put it 15 ms late.)
        var m = SstvModes.Get(SstvMode.Scottie1);
        var audio = SstvEncoder.Audio(SstvEncoder.Fit(Pattern(320, 256), m), m);
        var heard = OverHf(audio, RFpix.Tests.Bench.HfCondition.Poor, 3, 8, seed: 2);
        var rx = new SstvReceiver();
        var pictures = new List<SstvPicture>();
        int started = 0;
        rx.Started += _ => started++;
        rx.Finished += p => pictures.Add(p);
        for (int at = 0; at < heard.Length; at += 4800) rx.AddSamples(heard.AsSpan(at, Math.Min(4800, heard.Length - at)));
        rx.End();
        Assert.Equal(1, started);
        var got = Assert.Single(pictures);
        Assert.True(got.Complete);
        Assert.False(got.JoinedLate);
    }

    [Fact]
    public void A_VIS_code_followed_by_noise_alone_gives_no_picture()
    {
        // Seen on air, 23 November 2026: the start of an RFpix FM transmission through a repeater was taken
        // for a VIS code (Robot 72, PD 180), and a "picture" of noise took over the screen and was saved. A
        // picture whose sync tone never stood out on its beat wasn't one: nothing is saved.
        var m = SstvModes.Get(SstvMode.Robot72);
        var audio = SstvEncoder.Audio(SstvEncoder.Fit(Pattern(320, 240), m), m);
        int visEnd = (int)(SstvModes.VisTime.TotalMilliseconds * 48);
        var got = Receive(OverHf(audio, RFpix.Tests.Bench.HfCondition.NoiseOnly, 0, 20, seed: 1, cutAt: visEnd), out var started);
        Assert.Equal(SstvMode.Robot72, started?.Mode);   // the VIS code was read...
        Assert.Null(got);                                // ...but no picture followed
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

    // ---- pictures whose VIS code was missed ----

    /// <summary>Quality of rows [from, to) of b against rows [from + shift, ...) of a.</summary>
    private static double PsnrRows(DecodedPicture a, DecodedPicture b, int shift, int from, int to)
    {
        double mse = 0;
        int n = 0;
        for (int y = from; y < to; y++)
            for (int x = 0; x < a.Width; x++)
                for (int c = 0; c < 3; c++)
                {
                    double d = a.Bgra[4 * ((y + shift) * a.Width + x) + c] - b.Bgra[4 * (y * b.Width + x) + c];
                    mse += d * d;
                    n++;
                }
        return 10 * Math.Log10(255 * 255 / (mse / n));
    }

    [Theory]
    [InlineData(SstvMode.Martin1, 40)]
    [InlineData(SstvMode.Martin2, 100)]
    [InlineData(SstvMode.Scottie1, 60)]
    [InlineData(SstvMode.Robot36, 50)]
    [InlineData(SstvMode.Robot72, 50)]
    [InlineData(SstvMode.PD90, 30)]
    [InlineData(SstvMode.PD120, 30)]
    public void A_picture_whose_VIS_code_was_missed_is_found_from_its_syncs(SstvMode mode, int joinLine)
    {
        var m = SstvModes.Get(mode);
        var picture = SstvEncoder.Fit(Pattern(400, 300), m);
        var audio = SstvEncoder.Audio(picture, m);
        bool scottie = mode is SstvMode.Scottie1 or SstvMode.Scottie2 or SstvMode.ScottieDX;
        // Listening starts 30% of the way into line joinLine.
        double cutMs = SstvModes.VisTime.TotalMilliseconds + (scottie ? m.SyncMs : 0) + (joinLine + 0.3) * m.LineMs;
        var late = audio.AsSpan((int)(cutMs * 48)).ToArray();

        var rx = new SstvReceiver();
        var started = new List<SstvModeInfo>();
        SstvPicture? got = null;
        rx.Started += started.Add;
        rx.Finished += p => got = p;
        var padded = new float[late.Length + 48000 * 12];   // silence after it, for the end to be noticed
        late.CopyTo(padded, 0);
        for (int at = 0; at < padded.Length; at += 4800) rx.AddSamples(padded.AsSpan(at, Math.Min(4800, padded.Length - at)));
        rx.End();

        Assert.Equal(new[] { mode }, started.Select(s => s.Mode));
        Assert.NotNull(got);
        Assert.True(got!.JoinedLate);
        Assert.False(got.Complete);
        // It starts at the top with the first whole line heard (for Scottie, the line whose sync came
        // first: its green and blue were cut, so it's skipped in the check).
        int firstLine = scottie ? joinLine : joinLine + 1;
        int rowsPerLine = m.Color == SstvColor.PD ? 2 : 1;
        int shift = firstLine * rowsPerLine;
        int rows = Math.Min(got.Lines, m.Height - shift);
        Assert.True(Math.Abs(rows - (m.Height - shift)) <= 2 * rowsPerLine, $"{rows} rows of {m.Height - shift}");
        double psnr = PsnrRows(picture, got.Picture, shift, 2, rows - 2);
        Assert.True(psnr > 24, $"quality {psnr:0.0} dB");
    }

    [Fact]
    public void A_picture_with_its_VIS_code_is_started_once_only()
    {
        foreach (var mode in new[] { SstvMode.Martin2, SstvMode.Robot36, SstvMode.Scottie2, SstvMode.PD50 })
        {
            var m = SstvModes.Get(mode);
            var audio = SstvEncoder.Audio(SstvEncoder.Fit(Pattern(320, 256), m), m, preamble: true, fskId: "N0CALL");
            var rx = new SstvReceiver();
            int starts = 0;
            rx.Started += _ => starts++;
            var padded = new float[audio.Length + 48000 * 10];
            audio.CopyTo(padded, 24000);
            for (int at = 0; at < padded.Length; at += 4800) rx.AddSamples(padded.AsSpan(at, Math.Min(4800, padded.Length - at)));
            rx.End();
            Assert.Equal(1, starts);
        }
    }

    [Fact]
    public void Noise_and_odd_tones_dont_start_a_picture()
    {
        var rnd = new Random(7);
        var noise = new float[48000 * 20];
        for (int i = 0; i < noise.Length; i++) noise[i] = (float)(0.2 * (rnd.NextDouble() * 2 - 1));
        // and 1200 Hz pulses at uneven spacing
        var tones = new List<(double, double)>();
        for (int k = 0; k < 30; k++) { tones.Add((1200, 9)); tones.Add((1700, 150 + 37 * (k % 5))); }
        foreach (var audio in new[] { noise, Tones(tones) })
        {
            var rx = new SstvReceiver();
            int starts = 0;
            rx.Started += _ => starts++;
            for (int at = 0; at < audio.Length; at += 4800) rx.AddSamples(audio.AsSpan(at, Math.Min(4800, audio.Length - at)));
            Assert.Equal(0, starts);
        }
    }

    [Fact]
    public void A_late_picture_is_found_in_noise_from_a_sound_card_that_runs_slow()
    {
        var m = SstvModes.Get(SstvMode.Martin2);
        var picture = SstvEncoder.Fit(Pattern(400, 300), m);
        var audio = SstvEncoder.Audio(picture, m);
        var late = audio.AsSpan((int)((SstvModes.VisTime.TotalMilliseconds + 60.3 * m.LineMs) * 48)).ToArray();
        late = RFpix.Audio.ClockCorrector.Resample(late, 1 + 300e-6);
        var rnd = new Random(3);
        for (int i = 0; i < late.Length; i++) late[i] += (float)(0.08 * (rnd.NextDouble() * 2 - 1));
        var rx = new SstvReceiver();
        SstvPicture? got = null;
        rx.Finished += p => got = p;
        var padded = new float[late.Length + 48000 * 8];
        late.CopyTo(padded, 0);
        for (int at = 0; at < padded.Length; at += 4800) rx.AddSamples(padded.AsSpan(at, Math.Min(4800, padded.Length - at)));
        rx.End();
        Assert.NotNull(got);
        Assert.Equal(SstvMode.Martin2, got!.Mode.Mode);
        double psnr = PsnrRows(picture, got.Picture, 61, 2, Math.Min(got.Lines, m.Height - 61) - 2);
        Assert.True(psnr > 22, $"quality {psnr:0.0} dB");
    }

    // ---- a fade: the signal goes for a while, then comes back ----

    private static void Feed(SstvReceiver rx, float[] audio)
    {
        for (int at = 0; at < audio.Length; at += 4800) rx.AddSamples(audio.AsSpan(at, Math.Min(4800, audio.Length - at)));
    }

    /// <summary>Where line <paramref name="line"/> of a picture starts in its sound, in samples.</summary>
    private static int LineSample(SstvModeInfo m, double line) =>
        (int)((SstvModes.VisTime.TotalMilliseconds + (m.IsScottie ? m.SyncMs : 0) + line * m.LineMs) * 48);

    [Theory]
    [InlineData(SstvMode.Scottie2, 70, 30)]
    [InlineData(SstvMode.Martin2, 60, 40)]
    [InlineData(SstvMode.Robot36, 80, 60)]
    [InlineData(SstvMode.PD90, 40, 25)]
    public void A_picture_that_fades_and_comes_back_carries_on_as_one_picture(SstvMode mode, int fadeLine, int fadeLines)
    {
        var m = SstvModes.Get(mode);
        var picture = SstvEncoder.Fit(Pattern(400, 300), m);
        var audio = SstvEncoder.Audio(picture, m);
        // The signal fades out completely for fadeLines lines (well over the 20 that end a picture now).
        Array.Clear(audio, LineSample(m, fadeLine + 0.4), LineSample(m, fadeLine + fadeLines + 0.4) - LineSample(m, fadeLine + 0.4));

        var rx = new SstvReceiver();
        var started = new List<SstvMode>();
        var finished = new List<SstvPicture>();
        int faded = 0, resumedAt = -1;
        rx.Started += s => started.Add(s.Mode);
        rx.Finished += finished.Add;
        rx.Faded += _ => faded++;
        rx.Resumed += line => resumedAt = line;
        var padded = new float[audio.Length + 48000 * 6];
        audio.CopyTo(padded, 24000);
        Feed(rx, padded);
        rx.End();

        Assert.Equal(new[] { mode }, started);            // started once: no second "joined partway" picture
        Assert.Equal(1, faded);
        Assert.InRange(resumedAt, fadeLine + fadeLines, fadeLine + fadeLines + 8);
        var got = Assert.Single(finished);                 // and saved once
        Assert.False(got.JoinedLate);
        Assert.True(got.Complete);
        // Before the fade, and after it, the picture is in its right place.
        int rows = m.Color == SstvColor.PD ? 2 : 1;
        double before = PsnrRows(picture, got.Picture, 0, 2 * rows, (fadeLine - 1) * rows);
        double after = PsnrRows(picture, got.Picture, 0, (fadeLine + fadeLines + 10) * rows, m.Height - 2 * rows);
        Assert.True(before > 24, $"before the fade: {before:0.0} dB");
        Assert.True(after > 24, $"after the fade: {after:0.0} dB");
    }

    [Fact]
    public void A_picture_whose_signal_never_comes_back_is_saved_at_its_planned_end()
    {
        var m = SstvModes.Get(SstvMode.Robot36);   // 36 seconds
        var audio = SstvEncoder.Audio(SstvEncoder.Fit(Pattern(320, 240), m), m);
        var rx = new SstvReceiver();
        SstvPicture? got = null;
        rx.Finished += p => got = p;
        Feed(rx, audio.AsSpan(0, LineSample(m, 100)).ToArray());
        Feed(rx, new float[48000 * 8]);            // 8 s of nothing: past the 20 lines (3 s), not yet its end
        Assert.Null(got);
        Assert.True(rx.Waiting);
        Assert.Equal(SstvMode.Robot36, rx.Receiving!.Mode);
        Feed(rx, new float[48000 * 25]);           // past where it would have ended
        Assert.NotNull(got);
        Assert.False(rx.Waiting);
        Assert.Null(rx.Receiving);
        Assert.False(got!.Complete);
        Assert.InRange(got.Lines, 100, 125);
    }

    [Fact]
    public void A_new_picture_while_one_waits_saves_the_waiting_one_first()
    {
        var robot = SstvModes.Get(SstvMode.Robot36);
        var martin = SstvModes.Get(SstvMode.Martin2);
        var rx = new SstvReceiver();
        var finished = new List<SstvMode>();
        rx.Finished += p => finished.Add(p.Mode.Mode);
        Feed(rx, SstvEncoder.Audio(SstvEncoder.Fit(Pattern(320, 240), robot), robot).AsSpan(0, LineSample(robot, 100)).ToArray());
        Feed(rx, new float[48000 * 5]);
        Assert.True(rx.Waiting);
        Feed(rx, SstvEncoder.Audio(SstvEncoder.Fit(Pattern(320, 256), martin), martin));   // with its VIS code
        Feed(rx, new float[48000 * 3]);
        rx.End();
        Assert.Equal(new[] { SstvMode.Robot36, SstvMode.Martin2 }, finished);
    }

    [Fact]
    public void The_same_mode_coming_back_off_the_beat_is_a_new_picture()
    {
        // Another station's Martin 2, joined partway, during the wait: its lines don't fall on the
        // waiting picture's beat, so it isn't drawn into it.
        var m = SstvModes.Get(SstvMode.Martin2);
        var first = SstvEncoder.Audio(SstvEncoder.Fit(Pattern(320, 256), m), m);
        var other = SstvEncoder.Audio(SstvEncoder.Fit(Flat(320, 256, 200, 40, 40), m), m);
        var rx = new SstvReceiver();
        var started = new List<bool>();
        var finished = new List<SstvPicture>();
        int resumed = 0;
        rx.Started += _ => started.Add(rx.ReceivingLate);
        rx.Finished += finished.Add;
        rx.Resumed += _ => resumed++;
        Feed(rx, first.AsSpan(0, LineSample(m, 80)).ToArray());
        // Half a line off the waiting picture's beat (plus 30 lines of quiet), then the other picture from its line 150.
        Feed(rx, new float[(int)((30.5 * m.LineMs) * 48)]);
        Feed(rx, other.AsSpan(LineSample(m, 150.3)).ToArray());
        Feed(rx, new float[48000 * 6]);
        rx.End();
        Assert.Equal(0, resumed);
        Assert.Equal(new[] { false, true }, started);   // the first with its VIS code, then a new one joined partway
        Assert.Equal(2, finished.Count);
        Assert.False(finished[0].Complete);
        Assert.True(finished[1].JoinedLate);
    }

    // ---- no new picture while a digital one arrives ----

    [Fact]
    public void While_held_no_picture_starts_but_one_already_arriving_carries_on()
    {
        // (Set by the Receive tab while a digital transmission arrives, and for 10 s after: its sound
        // once looked like a Robot 72 picture, Nov 20, 2026.)
        var m = SstvModes.Get(SstvMode.Robot36);
        var audio = SstvEncoder.Audio(Flat(320, 240, 200, 30, 90), m);
        var quiet = new float[48000];

        var held = new SstvReceiver { HoldNewPictures = true };
        int starts = 0;
        held.Started += _ => starts++;
        Feed(held, quiet.Concat(audio).Concat(quiet).ToArray());
        Assert.Equal(0, starts);   // neither by its VIS code nor by its syncs

        var rx = new SstvReceiver();
        SstvPicture? got = null;
        rx.Finished += p => got = p;
        int half = audio.Length / 2;
        Feed(rx, quiet.Concat(audio[..half]).ToArray());
        Assert.NotNull(rx.Receiving);
        rx.HoldNewPictures = true;   // a digital signal heard part-way: the picture already arriving goes on
        Feed(rx, audio[half..].Concat(quiet).ToArray());
        Assert.NotNull(got);
        Assert.True(got!.Complete);
    }
}
