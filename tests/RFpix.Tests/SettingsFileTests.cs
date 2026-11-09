// SettingsFileTests.cs - the settings file is written safely: a new file takes the old one's place
// in one step, the old one kept as .bak; a damaged file falls back to the last good copy.

using RFpix.Settings;
using Xunit;

namespace RFpix.Tests;

public sealed class SettingsFileTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rfpix-settings-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_folder, "settings.json");

    public void Dispose() { if (Directory.Exists(_folder)) Directory.Delete(_folder, true); }

    private static string Json(string call) =>
        System.Text.Json.JsonSerializer.Serialize(new AppSettings { Callsign = call });

    [Fact]
    public void Each_save_replaces_the_file_and_keeps_the_last_one_as_a_backup()
    {
        AppSettings.WriteFile(FilePath, Json("N0AAA"));
        Assert.False(File.Exists(FilePath + ".bak"));   // nothing to keep the first time
        AppSettings.WriteFile(FilePath, Json("N0BBB"));
        Assert.Equal("N0BBB", AppSettings.ReadFile(FilePath).Settings!.Callsign);
        Assert.Contains("N0AAA", File.ReadAllText(FilePath + ".bak"));
        // No half-written files left behind.
        Assert.Empty(Directory.GetFiles(_folder, "*.tmp"));
    }

    [Fact]
    public void A_damaged_file_falls_back_to_the_last_good_copy()
    {
        AppSettings.WriteFile(FilePath, Json("N0AAA"));
        AppSettings.WriteFile(FilePath, Json("N0BBB"));
        File.WriteAllText(FilePath, "{ \"Callsign\": \"N0BB");   // cut short, as a power cut might
        var (settings, problem) = AppSettings.ReadFile(FilePath);
        Assert.Equal("N0AAA", settings!.Callsign);
        Assert.Contains("last good copy", problem);
    }

    [Fact]
    public void No_file_yet_is_not_a_problem_but_two_damaged_ones_are()
    {
        var (none, noProblem) = AppSettings.ReadFile(FilePath);
        Assert.Null(none);
        Assert.Null(noProblem);
        Directory.CreateDirectory(_folder);
        File.WriteAllText(FilePath, "not json");
        File.WriteAllText(FilePath + ".bak", "");
        var (damaged, problem) = AppSettings.ReadFile(FilePath);
        Assert.Null(damaged);
        Assert.Contains("usual settings", problem);
    }
}
