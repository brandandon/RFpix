// AudioSystem.cs - picks the right audio backend for the computer RFpix is running on.

using RFpix.Audio.CoreAudio;
using RFpix.Audio.Pulse;
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
        if (OperatingSystem.IsMacOS())
        {
            return new CoreAudioInput();
        }
        if (OperatingSystem.IsLinux())
        {
            return new PulseAudioInput();
        }
        return new UnsupportedAudioInput();
    }

    public static IAudioOutput CreateOutput()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WasapiAudioOutput();
        }
        if (OperatingSystem.IsMacOS())
        {
            return new CoreAudioOutput();
        }
        if (OperatingSystem.IsLinux())
        {
            return new PulseAudioOutput();
        }
        return new UnsupportedAudioOutput();
    }
}

/// <summary>Stand-in for any other system (RFpix has sound on Windows, the Mac and Linux).</summary>
internal sealed class UnsupportedAudioInput : IAudioInput
{
    private const string Message = "Sound input works on Windows, the Mac and Linux only.";

    public bool IsRunning => false;
    public string FormatDescription => "";
    public event EventHandler<AudioSamplesEventArgs>? SamplesAvailable { add { } remove { } }
    public event EventHandler<string>? Failed { add { } remove { } }

    public IReadOnlyList<AudioDeviceInfo> GetDevices() => throw new AudioException(Message);
    public void Start(string deviceId) => throw new AudioException(Message);
    public void Stop() { }
    public void Dispose() { }
}
