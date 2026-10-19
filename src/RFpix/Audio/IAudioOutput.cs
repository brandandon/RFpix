// IAudioOutput.cs - RFpix's audio layer for sound going OUT to the radio.
//
// Like IAudioInput, the rest of RFpix only talks to this interface. On Windows the
// backend is WasapiAudioOutput; Linux and Mac get theirs later.

namespace RFpix.Audio;

public interface IAudioOutput : IDisposable
{
    /// <summary>The playback devices that are plugged in and switched on right now.</summary>
    IReadOnlyList<AudioDeviceInfo> GetDevices();

    /// <summary>Plays one channel of sound (numbers between -1 and +1) on a device, on all its
    /// channels, and finishes once the last of it has been played. Cancelling stops at once.
    /// Throws <see cref="AudioException"/> if the device can't be used.</summary>
    /// <param name="level">Read as it plays: the level wanted now (a multiplier, 0 to 1), so it can be
    /// changed during playing. Null plays the samples as they are.</param>
    /// <param name="progress">Told the fraction played so far, a few times a second.</param>
    Task PlayAsync(string deviceId, float[] samples, int sampleRate, Func<double>? level, IProgress<double>? progress,
                   CancellationToken cancel);

    /// <summary>For sound-card calibration: plays silence on a device for <paramref name="duration"/>,
    /// telling <paramref name="reading"/> again and again how many samples the device has played
    /// (by its own count) at that moment (seconds, on the computer's clock), and at what nominal rate.</summary>
    Task MeasureClockAsync(string deviceId, TimeSpan duration, Action<double, long, int> reading, CancellationToken cancel) =>
        throw new AudioException("Measuring the sound card's clock isn't possible on this computer.");
}

/// <summary>Stand-in used on Linux and Mac until their audio backend is written.</summary>
internal sealed class UnsupportedAudioOutput : IAudioOutput
{
    private const string Message = "Sound output is Windows-only for now. Linux and Mac support is planned.";

    public IReadOnlyList<AudioDeviceInfo> GetDevices() => throw new AudioException(Message);
    public Task PlayAsync(string deviceId, float[] samples, int sampleRate, Func<double>? level, IProgress<double>? progress,
                          CancellationToken cancel) =>
        throw new AudioException(Message);
    public void Dispose() { }
}
