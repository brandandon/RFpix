// UpdateCheck.cs - is there a newer RFpix?
//
// When RFpix starts (and "Check for new versions" is ticked in Settings > Misc), it asks GitHub
// for the list of RFpix releases, once. If one is newer than the version running and has an
// installer for this computer (Windows, or this kind of Mac), the sidebar says so, with a
// Download button that opens it in the web browser. Installing it replaces the old RFpix (your
// settings and pictures stay).
//
// Versions are compared number by number (0.5.10 is newer than 0.5.9); a test version ("beta")
// comes before the finished one of the same number (0.5.2 beta, then 0.5.2). While you're
// running a test version you're told about newer test versions too; once on a finished one,
// only about finished ones.

using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RFpix.Update;

/// <summary>A version number such as 0.5.2 or 0.5.2 beta (Label "beta").</summary>
public sealed record AppVersion(int Major, int Minor, int Patch, string Label = "", int LabelNumber = 0) : IComparable<AppVersion>
{
    private static readonly Regex Pattern = new(@"(\d+)\.(\d+)(?:\.(\d+))?(?:[\s\-_.]*([A-Za-z]+)[\s\-_.]*(\d+)?)?", RegexOptions.CultureInvariant);

    public bool IsTest => Label.Length > 0;

    /// <summary>Reads "v0.5.2-beta", "0.5.2 beta", "V0.5.3", "0.6.0-rc.1"... Null if there's no version in it.</summary>
    public static AppVersion? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Pattern.Match(text);
        if (!m.Success) return null;
        string label = m.Groups[4].Success ? m.Groups[4].Value.ToLowerInvariant() : "";
        // Words after the number that aren't test labels ("for mac os") don't count.
        if (label.Length > 0 && Rank(label) < 0) label = "";
        return new AppVersion(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
                              m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0,
                              label, label.Length > 0 && m.Groups[5].Success ? int.Parse(m.Groups[5].Value) : 0);
    }

    // The test labels, earliest first.
    private static int Rank(string label) => label switch
    {
        "dev" => 0, "alpha" or "a" => 1, "beta" or "b" => 2, "pre" or "preview" => 3, "rc" => 4, _ => -1,
    };

    public int CompareTo(AppVersion? other)
    {
        if (other is null) return 1;
        int c = Major.CompareTo(other.Major);
        if (c == 0) c = Minor.CompareTo(other.Minor);
        if (c == 0) c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        if (IsTest != other.IsTest) return IsTest ? -1 : 1;   // the finished version comes after its tests
        c = Rank(Label).CompareTo(Rank(other.Label));
        return c != 0 ? c : LabelNumber.CompareTo(other.LabelNumber);
    }

    public static bool operator >(AppVersion a, AppVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(AppVersion a, AppVersion b) => a.CompareTo(b) < 0;

    public override string ToString() =>
        $"{Major}.{Minor}.{Patch}" + (IsTest ? " " + Label + (LabelNumber > 0 ? " " + LabelNumber : "") : "");
}

/// <summary>A newer RFpix: its version, its page on GitHub, and the installer for this computer.</summary>
public sealed record UpdateInfo(AppVersion Version, string Title, string PageUrl, string DownloadUrl);

public static class UpdateCheck
{
    public const string ReleasesApi = "https://api.github.com/repos/brandandon/RFpix/releases?per_page=30";

    /// <summary>The name of the download for this computer, with * for the version (null: none,
    /// as when RFpix runs from its source code). On Linux it's the same kind as the one running:
    /// <paramref name="linuxKind"/> "appimage" or "deb" (see <see cref="LinuxKind"/>).</summary>
    public static string? InstallerPattern(bool windows, bool mac, Architecture architecture, string? linuxKind = null) =>
        windows ? "RFpix-Setup-*.exe"
        : mac ? (architecture == Architecture.Arm64 ? "RFpix-*-mac-apple-silicon.pkg" : "RFpix-*-mac-intel.pkg")
        : linuxKind == "appimage" ? (architecture == Architecture.Arm64 ? "RFpix-*-linux-arm64.AppImage" : "RFpix-*-linux-x64.AppImage")
        : linuxKind == "deb" ? (architecture == Architecture.Arm64 ? "rfpix_*_arm64.deb" : "rfpix_*_amd64.deb")
        : null;

    /// <summary>On Linux: "appimage" when running as an AppImage, "deb" when installed from the
    /// .deb (in /opt/rfpix), otherwise null.</summary>
    public static string? LinuxKind()
    {
        if (!OperatingSystem.IsLinux()) return null;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APPIMAGE"))) return "appimage";
        return AppContext.BaseDirectory.StartsWith("/opt/rfpix/", StringComparison.Ordinal) ? "deb" : null;
    }

    /// <summary>Whether a file's name fits the pattern (* stands for anything; capitals don't matter).</summary>
    public static bool Fits(string name, string pattern) =>
        Regex.IsMatch(name, "^" + Regex.Escape(pattern).Replace(@"\*", ".*") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>From GitHub's list of releases (its JSON), the newest one newer than <paramref name="running"/>
    /// with an installer fitting <paramref name="installerPattern"/>. Null if there isn't one.</summary>
    public static UpdateInfo? Newest(string releasesJson, AppVersion running, string installerPattern)
    {
        using var doc = JsonDocument.Parse(releasesJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
        UpdateInfo? best = null;
        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (Bool(release, "draft")) continue;
            string tag = Text(release, "tag_name"), name = Text(release, "name");
            var version = AppVersion.Parse(tag) ?? AppVersion.Parse(name);
            if (version is null || !(version > running)) continue;
            if (version.IsTest && !running.IsTest) continue;   // on a finished version: finished ones only
            if (best is not null && !(version > best.Version)) continue;
            string? download = null;
            if (release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var asset in assets.EnumerateArray())
                    if (Fits(Text(asset, "name"), installerPattern))
                        download = Text(asset, "browser_download_url");
            if (string.IsNullOrEmpty(download)) continue;   // no installer for this computer (yet)
            best = new UpdateInfo(version, name.Length > 0 ? name : "RFpix " + tag, Text(release, "html_url"), download);
        }
        return best;
    }

    /// <summary>Asks GitHub. Null if there's nothing newer, or GitHub couldn't be reached (no internet: no fuss).</summary>
    public static async Task<UpdateInfo?> CheckAsync(AppVersion running, string installerPattern, CancellationToken cancel = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RFpix", running.ToString().Replace(' ', '-')));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            string json = await http.GetStringAsync(ReleasesApi, cancel);
            return Newest(json, running, installerPattern);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
