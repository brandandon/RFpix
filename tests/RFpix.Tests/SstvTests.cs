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
}
