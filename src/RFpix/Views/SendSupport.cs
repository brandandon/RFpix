// SendSupport.cs - what the Send tab and the Beta Testing page share about sending:
// the sound output in use, the checks before keying the radio, and the send itself
// (connect to the radio if needed, key, play, un-key).

using RFpix.Analog;
using RFpix.Audio;
using RFpix.Gallery;
using RFpix.Imaging;
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

    /// <summary>Why sending can't start, or null if it can.</summary>
    public static string? Problem(IAudioOutput output, string callsign)
    {
        if (callsign.Length == 0) return "Set your callsign in Settings (Misc tab) first: it's sent with every transmission.";
        if (ChosenOutput(output) is null)
            return AppSettings.Current.OutputDeviceId is null
                ? "Pick the radio's sound output in Settings (Radio tab) first."
                : "The sound output chosen in Settings (Radio tab) isn't connected. Switch on or plug in the radio, or pick another there.";
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
    /// <param name="leadIn">Silence after keying before the sound (null: the usual 0.3 s).</param>
    public static async Task SendSoundAsync(Func<float[]> makeSound, string what, IAudioOutput output,
                                            Func<double> level, Action<string> status, IProgress<double> progress,
                                            CancellationToken cancel, TimeSpan? leadIn = null)
    {
        // One transmission at a time, whichever page starts it (a picture, a BSR request, a fix).
        if (Interlocked.Exchange(ref _sending, 1) == 1)
            throw new AudioException("RFpix is already transmitting. Wait for it to finish, or stop it first.");
        try
        {
            await SendSoundCoreAsync(makeSound, what, output, level, status, progress, cancel, leadIn);
        }
        finally
        {
            Volatile.Write(ref _sending, 0);
        }
    }

    private static int _sending;

    /// <summary>True while a transmission is going out.</summary>
    public static bool IsSending => Volatile.Read(ref _sending) == 1;

    private static async Task SendSoundCoreAsync(Func<float[]> makeSound, string what, IAudioOutput output,
                                                 Func<double> level, Action<string> status, IProgress<double> progress,
                                                 CancellationToken cancel, TimeSpan? leadIn)
    {
        var device = ChosenOutput(output) ?? throw new AudioException("The sound output chosen in Settings (Radio tab) isn't connected.");
        var radioSettings = AppSettings.Current.Radio;
        bool hasControl = RadioChoices.Find(radioSettings.Choice).Connection != "none";
        bool keyed = radioSettings.Keying() != PttMethod.Vox;

        status("Preparing the transmission…");
        var (sound, seconds) = await Task.Run(() =>
        {
            var signal = makeSound();
            var prepared = Transmission.Prepare(signal, 1, keyed, leadIn);
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

    /// <summary>The transmit level as set on the Send tab (for sending from other pages).</summary>
    public static double SavedGain() => Gain(AppSettings.Current.TransmitLevelDb);

    /// <summary>The lead-in of the send settings in use (for a BSR request).</summary>
    public static TimeSpan SavedLeadIn => TimeSpan.FromSeconds(AppSettings.Current.SendSettings?.LeadInSeconds ?? Transmission.LeadIn.TotalSeconds);

    /// <summary>The Sent folder (Documents\RFpix\Sent).</summary>
    public static string SentFolder => GalleryIndex.DefaultSentFolder;

    /// <summary>Raised (on the screen's thread) when a picture is added to the Received or Sent folder.</summary>
    public static event Action? GalleryChanged;
    public static void RaiseGalleryChanged() => GalleryChanged?.Invoke();

    /// <summary>Notes a picture about to go out: in the sent log (so missing pieces can be sent
    /// again if asked), and in the Sent folder for the Gallery. Returns a problem, or null.</summary>
    public static string? RecordSent(FittedPicture fitted, string callsign)
    {
        var profile = fitted.Profile;
        SentLog.Current.Add(fitted.SendName, fitted.FileName, fitted.SendData, profile.Settings, profile.LeadInSeconds);
        try
        {
            var when = DateTime.Now;
            string path = GalleryIndex.SaveSent(SentFolder, fitted.FileName, fitted.File, when);
            GalleryIndex.Add(SentFolder, new GalleryEntry(Path.GetFileName(path), when, fitted.FileName, callsign,
                profile.Settings.Description, profile.ReedSolomon));
            RaiseGalleryChanged();
            var picture = fitted.File;
            _ = Task.Run(() => RFpix.Upload.UploadService.SaveCopy(picture, when, analog: false, sent: true));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Couldn't save a copy in {SentFolder}: {ex.Message}";
        }
    }

    /// <summary>Notes an analog SSTV picture about to go out: saved (as a JPEG of exactly what's sent)
    /// in the Sent folder for the Gallery, and copied for the website if that's set. Returns a problem, or null.</summary>
    public static string? RecordSentAnalog(DecodedPicture picture, string name, SstvModeInfo mode, string callsign)
    {
        try
        {
            var when = DateTime.Now;
            string path = GalleryIndex.SaveSent(SentFolder, name, Pictures.EncodeJpeg(picture, 92), when);
            GalleryIndex.Add(SentFolder, new GalleryEntry(Path.GetFileName(path), when, name, callsign, $"Analog SSTV  ·  {mode.Name}"));
            RaiseGalleryChanged();
            _ = Task.Run(() => RFpix.Upload.UploadService.SaveCopy(picture, when, analog: true, sent: true));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PictureException)
        {
            return $"Couldn't save a copy in {SentFolder}: {ex.Message}";
        }
    }

    /// <summary>The level slider's text, e.g. "−30 dB".</summary>
    public static string LevelLabel(double db) => db == 0 ? "0 dB" : $"{db:0} dB".Replace('-', '−');

    /// <summary>The level as a multiplier: 0 dB = 1, -20 dB = 0.1.</summary>
    public static double Gain(double db) => Math.Pow(10, db / 20);
}
