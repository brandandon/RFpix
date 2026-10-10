// AppSettings.cs - remembers your choices between runs.
//
// Settings are saved as a small text file (JSON) at
//   C:\Users\<you>\AppData\Roaming\RFpix\settings.json
// If the file is missing or damaged, RFpix just starts with the defaults.

using System.Text.Json;

namespace RFpix.Settings;

public sealed class AppSettings
{
    /// <summary>Permanent Windows ID of the radio's recording device.</summary>
    public string? InputDeviceId { get; set; }

    /// <summary>"System" (follow Windows), "Light" or "Dark".</summary>
    public string Theme { get; set; } = "System";

    /// <summary>Folder the "Play recording…" button opened last time.</summary>
    public string? LastRecordingFolder { get; set; }

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
