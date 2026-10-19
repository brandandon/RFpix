// SoundCardClockTests.cs - checks measuring a sound card's clock, and correcting for it.

using RFpix.Audio;
using Xunit;

namespace RFpix.Tests;

public class SoundCardClockTests
{
    [Fact]
    public void The_true_rate_comes_from_readings_even_when_they_wobble()
    {
        // A card 37 ppm fast, read every 10 ms for 2 minutes, each reading up to 3 ms late.
        var m = new ClockMeasurement(48000);
        var random = new Random(4);
        double rate = 48000 * (1 + 37e-6);
        for (int i = 0; i < 12000; i++)
        {
            double t = i * 0.01;
            m.Add(t + random.NextDouble() * 0.003, (long)(t * rate));
        }
        Assert.InRange(m.Ppm!.Value, 35, 39);
        Assert.InRange(m.UncertaintyPpm!.Value, 0, 2);
        Assert.Equal(119.99, m.Seconds, 1);
    }

    [Fact]
    public void Too_few_readings_give_no_answer()
    {
        var m = new ClockMeasurement(48000);
        m.Add(0, 0);
        m.Add(1, 48000);
        Assert.Null(m.Ppm);
    }

    /// <summary>The frequency of a tone, from where it crosses zero going up.</summary>
    private static double Frequency(IReadOnlyList<float> s, int rate)
    {
        int first = -1, last = -1, crossings = 0;
        for (int i = 1; i < s.Count; i++)
            if (s[i - 1] < 0 && s[i] >= 0)
            {
                if (first < 0) first = i; else crossings++;
                last = i;
            }
        return crossings * (double)rate / (last - first);
    }

    [Fact]
    public void A_fast_cards_recording_is_retimed_to_true_frequency()
    {
        // A true 1000 Hz tone recorded by a card 500 ppm fast looks like 999.5 Hz.
        double ppm = 500;
        int n = 48000 * 4;
        var recorded = new float[n];
        for (int i = 0; i < n; i++) recorded[i] = (float)Math.Sin(2 * Math.PI * 1000 / (1 + ppm * 1e-6) * i / 48000);
        Assert.InRange(Frequency(recorded, 48000), 999.4, 999.6);

        var corrected = ClockCorrector.Resample(recorded, ClockCorrector.InputRatio(ppm));
        Assert.InRange(Frequency(corrected, 48000), 999.97, 1000.03);
        Assert.Equal((int)Math.Round(n / (1 + ppm * 1e-6)), corrected.Length);
    }

    [Fact]
    public void Retiming_in_chunks_gives_the_same_sound_as_all_at_once()
    {
        var sound = new float[10000];
        for (int i = 0; i < sound.Length; i++) sound[i] = (float)Math.Sin(i * 0.05) * 0.5f;
        double ratio = ClockCorrector.OutputRatio(-120);
        var whole = new List<float>();
        new ClockCorrector(ratio).Process(sound, whole);
        var chunked = new List<float>();
        var corrector = new ClockCorrector(ratio);
        for (int at = 0; at < sound.Length; at += 480) corrector.Process(sound.AsSpan(at, Math.Min(480, sound.Length - at)), chunked);
        Assert.Equal(whole.Count, chunked.Count);
        for (int i = 0; i < whole.Count; i++) Assert.Equal(whole[i], chunked[i], 6);
    }

    [Fact]
    public void Retiming_is_clean_enough_for_HamDRM()
    {
        // A 2.5 kHz tone, stretched by 300 ppm: the error from the curve is far below the signal.
        double ratio = ClockCorrector.OutputRatio(300);
        var sound = new float[48000];
        for (int i = 0; i < sound.Length; i++) sound[i] = (float)Math.Sin(2 * Math.PI * 2500 * i / 48000);
        var out_ = ClockCorrector.Resample(sound, ratio);
        double error = 0, power = 0;
        for (int k = 100; k < out_.Length - 100; k++)
        {
            double ideal = Math.Sin(2 * Math.PI * 2500 * (k / ratio) / 48000);
            error += (out_[k] - ideal) * (out_[k] - ideal);
            power += ideal * ideal;
        }
        Assert.True(10 * Math.Log10(power / error) > 50, $"{10 * Math.Log10(power / error):0} dB");
        Assert.Equal(sound[0], out_[0]);   // no delay
    }

    [Fact]
    public void A_64_QAM_picture_retimed_out_and_back_still_decodes_exactly()
    {
        // Sent through an output corrected for 200 ppm fast, received through an input
        // corrected for the same: the picture must come through untouched by the re-timing.
        var test = Send.TestTransmissions.All.Single(t => t.Number == 4);   // 64-QAM: the most demanding
        var sound = test.Transmitter("KD9DD").Audio();
        var sent = ClockCorrector.Resample(sound, ClockCorrector.OutputRatio(200));
        var received = ClockCorrector.Resample(sent, ClockCorrector.InputRatio(200));
        var (file, _, _) = TransmitterTests.ReceiveSound(received);
        Assert.Equal(test.Picture(), file?.Data);
    }
}
