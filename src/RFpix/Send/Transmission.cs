// Transmission.cs - puts a prepared HamDRM sound on the air: key, play, un-key.
//
// The steps, and the safety around them:
//   1. Add the silences around the signal. (The level is applied as it plays, so the
//      Send page's slider works during a transmission.)
//   2. Key the radio (unless it's VOX). The key has a time limit: the sound's length
//      plus 15 seconds. If anything goes wrong, the radio un-keys itself then.
//   3. Play a short silence first (keyed radios take a moment to switch to transmit, and
//      a repeater to open: the silence stops the start of the signal being cut off; the
//      send profile can make it longer), then the signal, then a short silence so the end
//      isn't cut off either.
//   4. Un-key, always: when the sound ends, when Stop is clicked, or if the sound
//      device fails.

using RFpix.Audio;
using RFpix.Radio;

namespace RFpix.Send;

public static class Transmission
{
    public const int SampleRate = 48000;
    public static readonly TimeSpan LeadIn = TimeSpan.FromMilliseconds(300);
    public static readonly TimeSpan Tail = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan ExtraKeyTime = TimeSpan.FromSeconds(15);

    /// <summary>The sound as it goes to the radio: lead-in silence (when keyed: by default 0.3 s, or
    /// <paramref name="leadIn"/>), the signal scaled by <paramref name="level"/> (0 to 1, where 1 is
    /// QSSTV's level), then the tail.</summary>
    public static float[] Prepare(float[] signal, double level, bool keyed, TimeSpan? leadIn = null)
    {
        int lead = keyed ? (int)((leadIn ?? LeadIn).TotalSeconds * SampleRate) : 0;
        int tail = (int)(Tail.TotalSeconds * SampleRate);
        var sound = new float[lead + signal.Length + tail];
        float gain = (float)Math.Clamp(level, 0, 1);
        for (int i = 0; i < signal.Length; i++) sound[lead + i] = signal[i] * gain;
        return sound;
    }

    /// <summary>Sends a prepared sound, at the level <paramref name="level"/> says as it plays (null: as it is).</summary>
    /// <param name="radio">The radio connection, or null with VOX and no radio control. With VOX
    /// and a connection, nothing is keyed but data mode is still switched, if that's ticked.</param>
    public static async Task SendAsync(float[] sound, IAudioOutput output, string deviceId, RadioConnection? radio,
                                       Func<double>? level, IProgress<double>? progress, CancellationToken cancel)
    {
        var limit = TimeSpan.FromSeconds(sound.Length / (double)SampleRate) + ExtraKeyTime;
        if (radio is not null) await Task.Run(() => radio.Key(limit), cancel);
        try
        {
            await output.PlayAsync(deviceId, sound, SampleRate, level, progress, cancel);
        }
        finally
        {
            if (radio is not null) await Task.Run(radio.Unkey, CancellationToken.None);
        }
    }
}
