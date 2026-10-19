// SendSupport.cs - what the Send tab and the Alpha Testing page share about sending:
// which sound output and keying are in use, the checks before keying the radio, and
// the send itself (connect to the radio if needed, key, play, un-key).

using RFpix.Audio;
using RFpix.HamDrm;
using RFpix.Radio;
using RFpix.Send;
using RFpix.Settings;

namespace RFpix.Views;

internal static class SendSupport
{
    /// <summary>The radio's sound output chosen on the Settings page, if it's plugged in.</summary>
    public static AudioDeviceInfo? ChosenOutput(IAudioOutput output)
    {
        var id = AppSettings.Current.OutputDeviceId;
        if (id is null) return null;
        try { return output.GetDevices().FirstOrDefault(d => d.Id == id); }
        catch (AudioException) { return null; }
    }

    /// <summary>The operator's callsign, set in Settings (Misc tab); "" if not set yet.</summary>
    public static string Callsign => (AppSettings.Current.Callsign ?? "").Trim().ToUpperInvariant();

    /// <summary>The line beside Send: who's sending, where the sound goes and how the radio is keyed.</summary>
    public static string Route(IAudioOutput output)
    {
        var radio = AppSettings.Current.Radio;
        string keying = radio.Ptt switch
        {
            PttMethod.Vox => "VOX",
            PttMethod.Rts => "RTS",
            PttMethod.Dtr => "DTR",
            _ => RadioChoices.Find(radio.Choice).Connection == "none" ? "nothing (no radio chosen)" : "CAT",
        };
        string device = ChosenOutput(output)?.Name
            ?? (AppSettings.Current.OutputDeviceId is null ? "no sound output chosen yet" : "a sound output that isn't connected");
        string call = Callsign.Length > 0 ? $"as {Callsign} " : "(no callsign set) ";
        return $"Sending {call}to {device}, keyed by {keying}  ·  change in Settings";
    }

    /// <summary>Why sending can't start, or null if it can.</summary>
    public static string? Problem(IAudioOutput output, string callsign)
    {
        if (callsign.Length == 0) return "Set your callsign in Settings (Misc tab) first: it's sent with every transmission.";
        if (ChosenOutput(output) is null)
            return AppSettings.Current.OutputDeviceId is null
                ? "Pick the radio's sound output in Settings (Audio tab) first."
                : "The sound output chosen in Settings (Audio tab) isn't connected. Switch on or plug in the radio, or pick another there.";
        var radio = AppSettings.Current.Radio;
        if (radio.Ptt != PttMethod.Vox && RadioChoices.Find(radio.Choice).Connection == "none")
            return "Keying is set to CAT, RTS or DTR but no radio is chosen. Pick your radio in Settings (Radio tab), or choose VOX there.";
        return null;
    }

    /// <summary>Makes a HamDRM transmission's sound and sends it (check <see cref="Problem"/> first).</summary>
    /// <param name="what">For the messages, e.g. "test 2" or "tower-sunset.jp2".</param>
    /// <param name="level">The level as it plays (the slider, read again and again).</param>
    /// <param name="status">Shows a message as it goes.</param>
    public static Task SendAsync(Func<HamDrmTransmitter> transmitter, string what, IAudioOutput output,
                                 Func<double> level, Action<string> status, IProgress<double> progress,
                                 CancellationToken cancel) =>
        SendSoundAsync(() => transmitter().Audio(), what, output, level, status, progress, cancel);

    /// <summary>Makes any sound (48 kHz, -1 to 1: a picture with its waterfall text, Morse code, ...)
    /// and sends it: key, play, un-key (check <see cref="Problem"/> first).</summary>
    public static async Task SendSoundAsync(Func<float[]> makeSound, string what, IAudioOutput output,
                                            Func<double> level, Action<string> status, IProgress<double> progress,
                                            CancellationToken cancel)
    {
        var device = ChosenOutput(output) ?? throw new AudioException("The sound output chosen in Settings (Audio tab) isn't connected.");
        var radioSettings = AppSettings.Current.Radio;
        bool hasControl = RadioChoices.Find(radioSettings.Choice).Connection != "none";
        bool keyed = radioSettings.Ptt != PttMethod.Vox;

        status("Preparing the transmission…");
        var (sound, seconds) = await Task.Run(() =>
        {
            var signal = makeSound();
            var prepared = Transmission.Prepare(signal, 1, keyed);
            // Sound-card calibration: re-time the sound to the output's true clock.
            double ppm = AppSettings.Correction(AppSettings.Current.OutputClock, device.Id);
            if (ppm != 0) prepared = ClockCorrector.Resample(prepared, ClockCorrector.OutputRatio(ppm));
            return (prepared, (double)signal.Length / Transmission.SampleRate);
        }, cancel);

        // Connect if keying or data mode needs the radio and Settings hasn't connected.
        var radio = RadioService.Current;
        if (radio is null && hasControl)
        {
            status("Connecting to the radio…");
            radio = await RadioService.ConnectAsync(radioSettings);
        }

        // The IC-9700's data-mode switch, if it's on, is shown with the progress ("IC-9700 in FM-D").
        string mode = "";
        void OnDataMode(string note) => mode = $"  ·  {note}";
        if (radio is not null) radio.DataModeSwitched += OnDataMode;
        try
        {
            status($"Transmitting {what} ({seconds:0} s)…");
            var shown = new Progress<double>(f =>
            {
                progress.Report(f);
                status($"Transmitting {what}: {f * seconds:0} of {seconds:0} s{mode}");
            });
            await Transmission.SendAsync(sound, output, device.Id, radio, level, shown, cancel);
        }
        finally
        {
            if (radio is not null) radio.DataModeSwitched -= OnDataMode;
        }
    }

    /// <summary>Writes a HamDRM transmission to a WAV file instead of sending it.</summary>
    public static Task SaveAsync(Func<HamDrmTransmitter> transmitter, string path, double level) =>
        SaveSoundAsync(() => transmitter().Audio(), path, level);

    /// <summary>Writes any sound to a WAV file instead of sending it.</summary>
    public static Task SaveSoundAsync(Func<float[]> makeSound, string path, double level) => Task.Run(() =>
    {
        var sound = Transmission.Prepare(makeSound(), level, keyed: false);
        using var wav = new WavWriter(path, Transmission.SampleRate);
        wav.Write(sound);
    });

    /// <summary>A picture's sound with the waterfall text from Settings (Misc tab) before and after it.</summary>
    public static float[] WithWaterfallText(float[] picture) =>
        WaterfallText.Around(picture, AppSettings.Current.WaterfallTextBefore, AppSettings.Current.WaterfallTextAfter);

    /// <summary>The level slider's text, e.g. "−30 dB".</summary>
    public static string LevelLabel(double db) => db == 0 ? "0 dB" : $"{db:0} dB".Replace('-', '−');

    /// <summary>The level as a multiplier: 0 dB = 1, -20 dB = 0.1.</summary>
    public static double Gain(double db) => Math.Pow(10, db / 20);
}
