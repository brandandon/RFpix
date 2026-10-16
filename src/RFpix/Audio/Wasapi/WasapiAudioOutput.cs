// WasapiAudioOutput.cs - plays sound to the radio on Windows, through WASAPI (via NAudio).
//
// Windows mixes every program's sound in one format per device (its "mix format",
// usually 48 kHz, 2 channels). RFpix hands Windows exactly that format, so Windows
// changes nothing: the HamDRM sound is resampled here if the device runs at another
// rate, and the one channel is copied to every channel the device has.
//
// Shared mode, like receiving. Pick the radio's own sound device (not your speakers):
// then other programs' sounds don't go out on the air unless they're sent there.

using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace RFpix.Audio.Wasapi;

[SupportedOSPlatform("windows")]
public sealed class WasapiAudioOutput : IAudioOutput
{
    private readonly MMDeviceEnumerator _enumerator = new();

    public IReadOnlyList<AudioDeviceInfo> GetDevices()
    {
        var devices = new List<AudioDeviceInfo>();
        foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            devices.Add(new AudioDeviceInfo(device.ID, device.FriendlyName));
        return devices;
    }

    public async Task PlayAsync(string deviceId, float[] samples, int sampleRate, IProgress<double>? progress, CancellationToken cancel)
    {
        WasapiOut output;
        var source = new ArraySource(samples, sampleRate);
        try
        {
            var device = _enumerator.GetDevice(deviceId);
            var mix = device.AudioClient.MixFormat;
            ISampleProvider sound = source;
            if (mix.SampleRate != sampleRate) sound = new WdlResamplingSampleProvider(sound, mix.SampleRate);
            sound = new EveryChannel(sound, mix.Channels);
            output = new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
            output.Init(new SampleToWaveProvider(sound));
        }
        catch (Exception ex)
        {
            throw new AudioException(AudioErrors.Explain(ex), ex);
        }

        using (output)
        {
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            output.PlaybackStopped += (_, e) =>
            {
                if (e.Exception is not null) finished.TrySetException(new AudioException(AudioErrors.Explain(e.Exception), e.Exception));
                else finished.TrySetResult();
            };
            output.Play();
            using (cancel.Register(() => output.Stop()))
            {
                while (!finished.Task.IsCompleted)
                {
                    progress?.Report(source.Fraction);
                    await Task.WhenAny(finished.Task, Task.Delay(250));
                }
            }
            await finished.Task;
            cancel.ThrowIfCancellationRequested();
            progress?.Report(1);
        }
    }

    public void Dispose() => _enumerator.Dispose();

    /// <summary>One channel of samples, read from start to end.</summary>
    private sealed class ArraySource(float[] samples, int sampleRate) : ISampleProvider
    {
        private int _position;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
        public double Fraction => samples.Length == 0 ? 1 : (double)_position / samples.Length;

        public int Read(float[] buffer, int offset, int count)
        {
            int n = Math.Min(count, samples.Length - _position);
            Array.Copy(samples, _position, buffer, offset, n);
            _position += n;
            return n;
        }
    }

    /// <summary>Copies one channel to every channel of the device.</summary>
    private sealed class EveryChannel(ISampleProvider source, int channels) : ISampleProvider
    {
        private float[] _mono = Array.Empty<float>();
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, channels);

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / channels;
            if (_mono.Length < frames) _mono = new float[frames];
            int read = source.Read(_mono, 0, frames);
            for (int i = 0; i < read; i++)
                for (int c = 0; c < channels; c++) buffer[offset + i * channels + c] = _mono[i];
            return read * channels;
        }
    }
}
