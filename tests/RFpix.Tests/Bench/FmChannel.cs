// FmChannel.cs - for the test bench: makes a signal sound as if it had gone through FM radios, on
// simplex or through a repeater, the way RFpix's sound goes through the radio's own microphone/speaker
// audio (not its data port).
//
// What an FM radio does to the sound, and what's modelled here:
//   1. Sending: the radio passes only about 300 to 3000 Hz, boosts the treble ("pre-emphasis":
//      6 dB more for each doubling of frequency), and limits how far the signal can swing the
//      frequency ("deviation"): louder peaks are clipped flat. A filter after the clipper keeps the
//      signal inside its channel.
//   2. The air: the sound swings the radio's frequency (FM). Noise is added at a chosen
//      carrier-to-noise ratio (CNR, in the 16 kHz the receiver listens to). A strong signal is
//      "full quieting"; near 10 dB, FM's noise turns into crackle and clicks.
//   3. Receiving: the radio turns the frequency swings back into sound, cuts the treble back down
//      ("de-emphasis", undoing step 1's boost, which also cuts the hiss) and passes 300 to 3000 Hz.
//   4. Through a repeater: steps 2 and 3 happen twice. The repeater turns the input back into sound
//      (with the input's noise), passes 300 to 3000 Hz, limits it again with its own clipper, and sends
//      it on (usually strong: its own CNR).
//
//   5. A sub-audible tone (CTCSS, 67 to 254 Hz), when the repeater needs one: added to the signal at
//      about a tenth of full deviation, by the sending station and again by the repeater. Many radios
//      pass it out with the received sound (their data/USB audio isn't filtered below 300 Hz): Dan's
//      FT-991A did, through a repeater with a 123 Hz tone (Nov 17, 2026), about 10 dB below the picture.
//   7. A strong bass cut, as measured on the net's repeater (Nov 20, 2026, RFpix FM test recordings):
//      the lowest tones arrived 17 dB down at 350 Hz, 6 dB at 600 Hz and 1.5 dB at 1 kHz, against the
//      middle of the band, and with them their share of the noise. Modelled as a 2nd-order high-pass at
//      780 Hz on the sending side (before the noise), optional (BassCutHz).
//   6. Data mode: a sending radio in data mode sends the sound flat, without step 1's treble boost, while
//      the receiver in plain FM still cuts the treble (Dan's repeater recording, Nov 17, 2026). RFpix can
//      add the boost itself (PreEmphasis).
//
// Real radios and repeaters each differ a little; a recording through Dan's repeater checks this model.

using System.Numerics;

namespace RFpix.Tests.Bench;

/// <summary>One kind of FM path.</summary>
/// <param name="Name">For the bench's tables.</param>
/// <param name="DriveDb">How loud the sound goes in, as its average (RMS) level against the clipper's
/// limit, after the treble boost: about -12 dB keeps a HamDRM signal's peaks under the limit; -6 dB
/// is "hot", with its peaks clipped.</param>
/// <param name="Repeater">True for through a repeater; false for simplex.</param>
/// <param name="RepeaterCnrDb">The repeater's own signal, as heard by the receiving station.</param>
/// <param name="DeviationHz">How far full volume swings the frequency: 5 kHz (most repeaters), 2.5 kHz (narrow).</param>
/// <param name="ToneHz">The repeater's sub-audible tone (CTCSS), heard in the receiving radio's sound; 0 for none.</param>
/// <param name="DataMode">The sending radio is in data mode: it sends the sound flat, without the treble boost
/// (the receiving radio, in plain FM, still cuts the treble).</param>
/// <param name="RfpixBoost">RFpix adds the treble boost itself before sending (PreEmphasis).</param>
/// <param name="BassCutHz">A 2nd-order high-pass at this frequency on the sending side, before the noise (0 for none):
/// the strong bass cut measured on the net's repeater is about 780 Hz.</param>
public sealed record FmCondition(string Name, double DriveDb, bool Repeater, double RepeaterCnrDb = 25, double DeviationHz = 5000,
                                 double ToneHz = 0, bool DataMode = false, bool RfpixBoost = false, double BassCutHz = 0)
{
    public static FmCondition SimplexRight { get; } = new("Simplex, level right (-12 dB)", -12, Repeater: false);
    public static FmCondition SimplexHot { get; } = new("Simplex, level hot (-6 dB)", -6, Repeater: false);
    public static FmCondition RepeaterRight { get; } = new("Repeater, level right (-12 dB)", -12, Repeater: true);
    public static FmCondition RepeaterHot { get; } = new("Repeater, level hot (-6 dB)", -6, Repeater: true);
    public static FmCondition RepeaterNarrow { get; } = new("Repeater, narrow FM (2.5 kHz)", -12, Repeater: true, DeviationHz: 2500);
    public static FmCondition RepeaterTone { get; } = new("Repeater, 123 Hz tone in the sound", -12, Repeater: true, ToneHz: 123);
    public static FmCondition RepeaterDataMode { get; } = new("Repeater, sent in data mode (flat)", -12, Repeater: true, DataMode: true);
    public static FmCondition RepeaterDataModeBoost { get; } =
        new("Repeater, data mode + RFpix's boost", -12, Repeater: true, DataMode: true, RfpixBoost: true);

    public static FmCondition RepeaterNet { get; } =
        new("Repeater like the net's (bass cut, 127.3 Hz tone)", -12, Repeater: true, ToneHz: 127.3, BassCutHz: 780);

    /// <summary>All of them, simplest first.</summary>
    public static IReadOnlyList<FmCondition> All { get; } =
        new[] { SimplexRight, SimplexHot, RepeaterRight, RepeaterHot, RepeaterNarrow, RepeaterTone, RepeaterDataMode, RepeaterDataModeBoost, RepeaterNet };
}

public static class FmChannel
{
    private const int Rate = 48000;
    private const double ListenBandwidth = 16000;   // the receiver's IF filter, for a 5 kHz deviation signal

    /// <summary>The audio as heard at the other end, with the station (or the repeater's input) heard at
    /// <paramref name="cnrDb"/>. The same seed always gives the same noise.</summary>
    public static float[] Apply(float[] audio, FmCondition condition, double cnrDb, int seed)
    {
        var random = new Random(seed);
        double bandwidth = ListenBandwidth * Math.Min(1, (condition.DeviationHz + 3000) / 8000.0);   // Carson's rule

        // The sending radio: its level, set so the average sits at DriveDb against the clipper's limit.
        if (condition.RfpixBoost) audio = RFpix.Dsp.PreEmphasis.Apply(audio, Rate);
        var sent = AddTone(Transmit(audio, condition.DriveDb, setLevel: true, emphasis: !condition.DataMode, condition.BassCutHz), condition.ToneHz);
        var heard = Air(sent, condition.DeviationHz, cnrDb, bandwidth, random);
        if (condition.Repeater)
        {
            // The repeater: its input turned back into sound, filtered and limited again (at the same
            // level it came in at), and sent on. (Its de-emphasis and its own pre-emphasis cancel out.)
            // Its audio filter removes the input's tone; it adds its own.
            var relayed = AddTone(Transmit(heard, condition.DriveDb, setLevel: false, emphasis: false), condition.ToneHz);
            heard = Air(relayed, condition.DeviationHz, condition.RepeaterCnrDb, bandwidth, random);
        }
        // The receiving radio. (With a tone, it's one that passes the tone out with the sound: no cut below 300 Hz.)
        var output = Filter(DeEmphasis(), heard);
        output = Filter(condition.ToneHz > 0 ? Lowpass(3000) : AudioBand(), output);
        return output.Select(s => (float)(0.25 * s)).ToArray();
    }

    // ---- sending ----

    private static double[] Transmit(IReadOnlyList<float> audio, double driveDb, bool setLevel, bool emphasis = true, double bassCutHz = 0) =>
        Transmit(audio.Select(s => (double)s).ToArray(), driveDb, setLevel, emphasis, bassCutHz);

    private static double[] Transmit(double[] audio, double driveDb, bool setLevel, bool emphasis = true, double bassCutHz = 0)
    {
        var x = Filter(AudioBand(), audio);
        if (bassCutHz > 0) x = Filter(new Chain(new[] { Biquad(bassCutHz, 0.7071, low: false) }), x);
        if (emphasis) x = Filter(PreEmphasis(), x);
        if (setLevel)
        {
            // The level is set on the signal itself (where it's louder than a tenth of its peak).
            double peak = x.Max(Math.Abs);
            var loud = x.Where(s => Math.Abs(s) > 0.1 * peak).ToArray();
            double rms = loud.Length > 0 ? Math.Sqrt(loud.Average(s => s * s)) : 1;
            double gain = Math.Pow(10, driveDb / 20) / rms;
            for (int i = 0; i < x.Length; i++) x[i] *= gain;
        }
        for (int i = 0; i < x.Length; i++) x[i] = Math.Clamp(x[i], -1, 1);   // the deviation limiter
        return Filter(Lowpass(3000), x);                                       // the "splatter" filter
    }

    /// <summary>Adds a sub-audible tone at a tenth of full deviation (none if <paramref name="hz"/> is 0).</summary>
    private static double[] AddTone(double[] sound, double hz)
    {
        if (hz <= 0) return sound;
        for (int i = 0; i < sound.Length; i++) sound[i] += 0.12 * Math.Sin(2 * Math.PI * hz * i / Rate);
        return sound;
    }

    // ---- the air ----

    /// <summary>Frequency-modulates the sound, adds noise, and turns it back into sound (a discriminator).</summary>
    private static double[] Air(double[] sound, double deviationHz, double cnrDb, double bandwidth, Random random)
    {
        int n = sound.Length;
        var re = new double[n];
        var im = new double[n];
        double phase = 0;
        // Noise for the CNR in the receiver's bandwidth: the carrier's power is 1.
        double sigma = Math.Sqrt(Rate / (Math.Pow(10, cnrDb / 10) * bandwidth) / 2);
        for (int i = 0; i < n; i++)
        {
            phase += 2 * Math.PI * deviationHz * sound[i] / Rate;
            re[i] = Math.Cos(phase) + sigma * Gaussian(random);
            im[i] = Math.Sin(phase) + sigma * Gaussian(random);
        }
        // The receiver's IF filter, on both halves of the signal.
        var filter = Lowpass(bandwidth / 2);
        re = Filter(filter, re);
        im = Filter(filter, im);

        // The discriminator: how far the phase turned from one sample to the next.
        var output = new double[n];
        var previous = new Complex(1, 0);
        for (int i = 0; i < n; i++)
        {
            var now = new Complex(re[i], im[i]);
            double turned = (now * Complex.Conjugate(previous)).Phase;
            output[i] = turned * Rate / (2 * Math.PI * deviationHz);
            previous = now;
        }
        return output;
    }

    private static double Gaussian(Random random)
    {
        double u1 = 1 - random.NextDouble(), u2 = random.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }

    // ---- filters ----

    /// <summary>A filter as a chain of sections, each b0 b1 b2 a1 a2 (a0 = 1).</summary>
    private sealed record Chain(double[][] Sections);

    private static double[] Filter(Chain chain, double[] x)
    {
        var y = (double[])x.Clone();
        foreach (var s in chain.Sections)
        {
            double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
            for (int i = 0; i < y.Length; i++)
            {
                double input = y[i];
                double output = s[0] * input + s[1] * x1 + s[2] * x2 - s[3] * y1 - s[4] * y2;
                x2 = x1; x1 = input; y2 = y1; y1 = output;
                y[i] = output;
            }
        }
        return y;
    }

    /// <summary>A 4th-order Butterworth low-pass (two sections).</summary>
    private static Chain Lowpass(double hz) => new(new[] { Biquad(hz, 0.5412, low: true), Biquad(hz, 1.3066, low: true) });

    /// <summary>The radio's audio band, 300 to 3000 Hz (4th-order each side).</summary>
    private static Chain AudioBand() => new(new[]
    {
        Biquad(300, 0.5412, low: false), Biquad(300, 1.3066, low: false),
        Biquad(3000, 0.5412, low: true), Biquad(3000, 1.3066, low: true),
    });

    /// <summary>One section of a Butterworth filter (the RBJ "cookbook" formulas).</summary>
    private static double[] Biquad(double hz, double q, bool low)
    {
        double w = 2 * Math.PI * hz / Rate, cos = Math.Cos(w), alpha = Math.Sin(w) / (2 * q);
        double a0 = 1 + alpha;
        double b0 = low ? (1 - cos) / 2 : (1 + cos) / 2;
        double b1 = low ? 1 - cos : -(1 + cos);
        return new[] { b0 / a0, b1 / a0, b0 / a0, -2 * cos / a0, (1 - alpha) / a0 };
    }

    // The treble boost: 6 dB more for each doubling of frequency above 212 Hz (750 microseconds),
    // levelling off far above the audio band; and the matching cut in the receiver.
    private const double EmphasisTime = 750e-6, EmphasisTop = 1 / (2 * Math.PI * 12000);

    private static Chain PreEmphasis() => FirstOrder(1, EmphasisTime, 1, EmphasisTop, gainAt1k: true);
    private static Chain DeEmphasis() => FirstOrder(1, EmphasisTop, 1, EmphasisTime, gainAt1k: true);

    /// <summary>(b0 + b1 s) / (a0 + a1 s), made digital (bilinear), scaled to no change at 1 kHz.</summary>
    private static Chain FirstOrder(double b0, double b1, double a0, double a1, bool gainAt1k)
    {
        double k = 2.0 * Rate;
        double nb0 = b0 + b1 * k, nb1 = b0 - b1 * k, na0 = a0 + a1 * k, na1 = a0 - a1 * k;
        var s = new[] { nb0 / na0, nb1 / na0, 0, na1 / na0, 0 };
        if (gainAt1k)
        {
            var z = Complex.Exp(new Complex(0, -2 * Math.PI * 1000 / Rate));
            double g = ((s[0] + s[1] * z) / (1 + s[3] * z)).Magnitude;
            s[0] /= g; s[1] /= g;
        }
        return new(new[] { s });
    }
}
