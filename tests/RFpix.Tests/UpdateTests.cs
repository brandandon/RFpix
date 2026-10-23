// UpdateTests.cs - telling whether a release on GitHub is newer, and finding its Mac installer.

using System.Runtime.InteropServices;
using RFpix.Update;
using Xunit;

namespace RFpix.Tests;

public class UpdateTests
{
    [Theory]
    [InlineData("v0.5.2-beta", "0.5.2 beta")]
    [InlineData("V0.5.2 beta for mac os", "0.5.2 beta")]
    [InlineData("0.5.3", "0.5.3")]
    [InlineData("RFpix v1.0 for Mac", "1.0.0")]
    [InlineData("v0.6.0-rc.2", "0.6.0 rc 2")]
    [InlineData("0.5.2-beta+abc123", "0.5.2 beta")]
    public void Version_numbers_are_read(string text, string expected) =>
        Assert.Equal(expected, AppVersion.Parse(text)!.ToString());

    [Fact]
    public void Text_without_a_version_is_ignored()
    {
        Assert.Null(AppVersion.Parse("Latest"));
        Assert.Null(AppVersion.Parse(""));
        Assert.Null(AppVersion.Parse(null));
    }

    [Theory]
    [InlineData("0.5.3 beta", "0.5.2 beta")]
    [InlineData("0.5.2", "0.5.2 beta")]        // the finished version comes after its tests
    [InlineData("0.5.10", "0.5.9")]            // number by number, not as text
    [InlineData("0.6.0 beta", "0.5.9")]
    [InlineData("0.6.0 rc", "0.6.0 beta")]
    [InlineData("0.6.0 beta 2", "0.6.0 beta")]
    [InlineData("1.0.0", "0.99.99")]
    public void Newer_versions_come_after(string newer, string older)
    {
        Assert.True(AppVersion.Parse(newer)! > AppVersion.Parse(older)!);
        Assert.True(AppVersion.Parse(older)! < AppVersion.Parse(newer)!);
    }

    [Fact]
    public void The_same_version_is_not_newer() =>
        Assert.False(AppVersion.Parse("v0.5.2-beta")! > AppVersion.Parse("0.5.2 beta")!);

    [Fact]
    public void Each_kind_of_Mac_gets_its_own_installer()
    {
        Assert.Equal("-mac-intel.pkg", UpdateCheck.InstallerEnding(true, Architecture.X64));
        Assert.Equal("-mac-apple-silicon.pkg", UpdateCheck.InstallerEnding(true, Architecture.Arm64));
        Assert.Null(UpdateCheck.InstallerEnding(false, Architecture.X64));   // Windows: not yet
    }

    private static string Release(string tag, bool draft = false, bool prerelease = true, params string[] files) =>
        $$"""
        { "tag_name": "{{tag}}", "name": "RFpix {{tag}}", "draft": {{(draft ? "true" : "false")}}, "prerelease": {{(prerelease ? "true" : "false")}},
          "html_url": "https://github.com/brandandon/RFpix/releases/tag/{{tag}}",
          "assets": [ {{string.Join(",", files.Select(f => $$"""{ "name": "{{f}}", "browser_download_url": "https://example.com/{{f}}" }"""))}} ] }
        """;

    private static string List(params string[] releases) => "[" + string.Join(",", releases) + "]";

    [Fact]
    public void The_newest_release_with_a_Mac_installer_is_offered()
    {
        var running = AppVersion.Parse("0.5.2-beta")!;
        string json = List(
            Release("v0.5.4-beta", draft: true, files: "RFpix-0.5.4-beta-mac-intel.pkg"),               // not published yet
            Release("v0.5.3-beta", files: new[] { "RFpix-Setup-0.5.3-beta.exe", "RFpix-0.5.3-beta-mac-intel.pkg",
                                                   "RFpix-0.5.3-beta-mac-apple-silicon.pkg" }),
            Release("v0.5.2-beta", files: "RFpix-0.5.2-beta-mac-intel.pkg"),
            Release("v0.5.1", prerelease: false, files: "RFpix-Setup-0.5.1.exe"));
        var update = UpdateCheck.Newest(json, running, "-mac-apple-silicon.pkg")!;
        Assert.Equal("0.5.3 beta", update.Version.ToString());
        Assert.Equal("https://example.com/RFpix-0.5.3-beta-mac-apple-silicon.pkg", update.DownloadUrl);
        Assert.EndsWith("/v0.5.3-beta", update.PageUrl);
    }

    [Fact]
    public void Nothing_is_offered_when_up_to_date_or_without_a_Mac_installer()
    {
        var running = AppVersion.Parse("0.5.2-beta")!;
        Assert.Null(UpdateCheck.Newest(List(Release("v0.5.2-beta", files: "RFpix-0.5.2-beta-mac-intel.pkg")), running, "-mac-intel.pkg"));
        Assert.Null(UpdateCheck.Newest(List(Release("v0.5.3-beta", files: "RFpix-Setup-0.5.3-beta.exe")), running, "-mac-intel.pkg"));
        Assert.Null(UpdateCheck.Newest("[]", running, "-mac-intel.pkg"));
        Assert.Null(UpdateCheck.Newest("""{ "message": "API rate limit exceeded" }""", running, "-mac-intel.pkg"));
    }

    [Fact]
    public void A_finished_version_is_only_told_about_finished_versions()
    {
        var running = AppVersion.Parse("1.0.0")!;
        string json = List(Release("v1.1.0-beta", files: "RFpix-1.1.0-beta-mac-intel.pkg"),
                           Release("v1.0.1", prerelease: false, files: "RFpix-1.0.1-mac-intel.pkg"));
        Assert.Equal("1.0.1", UpdateCheck.Newest(json, running, "-mac-intel.pkg")!.Version.ToString());
    }
}
