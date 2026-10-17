// IAudioInput.cs - RFpix's own "audio layer" for sound coming IN from the radio.
//
// The rest of RFpix (level meter, waterfall, decoder) only ever talks to this
// interface, never to Windows directly. Today there is one backend behind it
// (WasapiAudioInput, for Windows). Linux and Mac get their own backend later,
// and nothing else in the app has to change.

namespace RFpix.Audio;

/// <summary>One recording device, e.g. a radio's "USB Audio CODEC".</summary>
/// <param name="Id">Permanent ID from the operating system. Stays the same even if
/// the device is renamed or plugged into another USB port, so we save this one.</param>
/// <param name="Name">The name people see, e.g. "Microphone (USB Audio CODEC)".</param>
public sealed record AudioDeviceInfo(string Id, string Name)
{
    // Lists and drop-downs show this text.
    public override string ToString() => Name;
}

/// <summary>A chunk of audio, already converted to one channel of numbers between -1 and +1.</summary>
public sealed class AudioSamplesEventArgs : EventArgs
{
    public AudioSamplesEventArgs(float[] samples, int count, int sampleRate)
    {
        Samples = samples;
        Count = count;
        SampleRate = sampleRate;
    }

    /// <summary>The samples. Only the first <see cref="Count"/> are valid.
    /// The array is reused for the next chunk, so copy anything you want to keep.</summary>
    public float[] Samples { get; }
    public int Count { get; }
    /// <summary>Samples per second, usually 48000 for radio USB sound devices.</summary>
    public int SampleRate { get; }
}

/// <summary>Something went wrong with audio. The message is written for the operator to read.</summary>
public sealed class AudioException : Exception
{
    public AudioException(string message, Exception? inner = null) : base(message, inner) { }
}

public interface IAudioInput : IDisposable
{
    /// <summary>The recording devices that are plugged in and switched on right now.</summary>
    IReadOnlyList<AudioDeviceInfo> GetDevices();

    /// <summary>Start listening to a device. Throws <see cref="AudioException"/> if it can't.</summary>
    void Start(string deviceId);

    /// <summary>Stop listening. Safe to call even if not listening.</summary>
    void Stop();

    bool IsRunning { get; }

    /// <summary>A short description such as "48000 Hz, 32-bit, 2 channel(s)".</summary>
    string FormatDescription { get; }

    /// <summary>How many times sound went missing on its way in since listening started (Windows
    /// lost some). Each gap is filled with silence of the same length, so the timing is kept.</summary>
    int Gaps => 0;

    /// <summary>Raised many times a second while listening (on a background thread).</summary>
    event EventHandler<AudioSamplesEventArgs>? SamplesAvailable;

    /// <summary>Raised if listening stops by itself, e.g. the radio was unplugged.
    /// The text explains what happened in plain words.</summary>
    event EventHandler<string>? Failed;
}
