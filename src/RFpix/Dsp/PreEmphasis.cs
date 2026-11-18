// PreEmphasis.cs - FM's treble boost, added by RFpix to its own sound before sending on FM.
//
// On FM, a radio sending voice boosts the treble ("pre-emphasis": 6 dB more for each doubling of
// frequency above about 212 Hz, the 750 microsecond standard), and every receiver cuts it back down
// ("de-emphasis"), which also cuts the hiss. But to send RFpix's sound the radio is usually in data
// mode, and in data mode radios send the sound flat, without the boost. The receiving radios, in
// plain FM, still cut the treble, so the top of the picture signal arrived about 12 dB weak (Dan's
// recording through a repeater, Nov 17, 2026). Adding the boost here, before the sound goes out,
// puts it right for every receiver in plain FM, whatever program it runs.
//
// The boosted sound is scaled to the same average strength (RMS) as before, so the Transmit level
// slider means the same.

namespace RFpix.Dsp;

public static class PreEmphasis
{
    /// <summary>The standard FM pre-emphasis time constant: the boost starts at about 212 Hz.</summary>
    public const double TimeConstant = 750e-6;
    private const double LevelsOffAt = 12000;   // far above the audio band, so the filter stays tame

    /// <summary>The sound with FM's treble boost added, at the same average strength.</summary>
    public static float[] Apply(float[] sound, int sampleRate)
    {
        // (1 + s*T) / (1 + s*t), with T = 750 us and t far shorter: made digital (bilinear transform).
        double k = 2.0 * sampleRate, top = 1 / (2 * Math.PI * LevelsOffAt);
        double b0 = 1 + TimeConstant * k, b1 = 1 - TimeConstant * k, a0 = 1 + top * k, a1 = 1 - top * k;
        b0 /= a0; b1 /= a0; a1 /= a0;

        var output = new float[sound.Length];
        double x1 = 0, y1 = 0, before = 0, after = 0;
        for (int i = 0; i < sound.Length; i++)
        {
            double x = sound[i];
            double y = b0 * x + b1 * x1 - a1 * y1;
            x1 = x; y1 = y;
            output[i] = (float)y;
            before += x * x;
            after += y * y;
        }
        if (after > 0)
        {
            float scale = (float)Math.Sqrt(before / after);
            for (int i = 0; i < output.Length; i++) output[i] *= scale;
        }
        return output;
    }
}
