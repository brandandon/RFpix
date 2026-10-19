// AppSettings.cs - remembers your choices between runs.
//
// Settings are saved as a small text file (JSON) at
//   C:\Users\<you>\AppData\Roaming\RFpix\settings.json
// If the file is missing or damaged, RFpix just starts with the defaults.

using System.Text.Json;
using RFpix.Imaging;
using RFpix.Radio;
using RFpix.Send;

namespace RFpix.Settings;

public sealed class AppSettings
{
    /// <summary>Permanent Windows ID of the radio's recording device.</summary>
    public string? InputDeviceId { get; set; }

    /// <summary>"System" (follow Windows), "Light" or "Dark".</summary>
    public string Theme { get; set; } = "System";

    /// <summary>Folder the "Play recording…" button opened last time.</summary>
    public string? LastRecordingFolder { get; set; }

    /// <summary>Where received files are saved. Null means the default, Documents\RFpix\Received.</summary>
    public string? ReceivedFolder { get; set; }

    /// <summary>Permanent Windows ID of the radio's playback device (Send page).</summary>
    public string? OutputDeviceId { get; set; }

    /// <summary>Transmit level in dB below QSSTV's level (Send page): 0 is QSSTV's, -20 is a tenth of it.</summary>
    public double TransmitLevelDb { get; set; } = -20;

    /// <summary>The operator's callsign, sent with every transmission (Settings, Misc tab).</summary>
    public string? Callsign { get; set; }

    // ---- the Send tab ----

    /// <summary>The send settings in use (Send tab), whether or not they're a saved profile.</summary>
    public SendProfile? SendSettings { get; set; }

    /// <summary>Profiles the operator has saved (the built-in ones aren't stored).</summary>
    public List<SendProfile> SendProfiles { get; set; } = new();

    /// <summary>The picture format sent: JPEG 2000 unless changed.</summary>
    public PictureFormat SendFormat { get; set; } = PictureFormat.Jpeg2000;

    /// <summary>Folder the "Open picture…" button opened last time.</summary>
    public string? LastPictureFolder { get; set; }

    /// <summary>How RFpix reaches the radio (Radio page).</summary>
    public RadioSettings Radio { get; set; } = new();

    /// <summary>Raised (on the screen's thread) when the sound devices are changed on the Settings page,
    /// so the TX/RX page can switch over at once.</summary>
    public static event Action? SoundDevicesChanged;

    public static void RaiseSoundDevicesChanged() => SoundDevicesChanged?.Invoke();

    // ---- loading and saving ----

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RFpix", "settings.json");

    /// <summary>The settings in use. Loaded once when RFpix starts.</summary>
    public static AppSettings Current { get; } = Load();

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
            }
        }
        catch
        {
            // A damaged settings file shouldn't stop RFpix from starting.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // Not being able to save settings isn't worth crashing over.
        }
    }
}
