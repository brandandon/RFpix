// AudioSystem.cs - picks the right audio backend for the computer RFpix is running on.

using RFpix.Audio.Wasapi;

namespace RFpix.Audio;

public static class AudioSystem
{
    public static IAudioInput CreateInput()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WasapiAudioInput();
        }
        return new UnsupportedAudioInput();
    }

    public static IAudioOutput CreateOutput()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WasapiAudioOutput();
        }
        return new UnsupportedAudioOutput();
    }
}

/// <summary>Stand-in used on Linux and Mac until their audio backend is written.</summary>
internal sealed class UnsupportedAudioInput : IAudioInput
{
    private const string Message = "Sound input is Windows-only for now. Linux and Mac support is planned.";

    public bool IsRunning => false;
    public string FormatDescription => "";
    public event EventHandler<AudioSamplesEventArgs>? SamplesAvailable { add { } remove { } }
    public event EventHandler<string>? Failed { add { } remove { } }

    public IReadOnlyList<AudioDeviceInfo> GetDevices() => throw new AudioException(Message);
    public void Start(string deviceId) => throw new AudioException(Message);
    public void Stop() { }
    public void Dispose() { }
}
