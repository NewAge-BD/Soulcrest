using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Update check against the GitHub releases (user request 2026-10-07).</summary>
public sealed class UpdateServiceTests
{
    private const string Releases = """
        [
          { "tag_name": "v0.1.31", "draft": false, "prerelease": false, "body": "## 0.1.31 – 2026-10-09\n\n- **Neu:** Zwei.",
            "html_url": "https://github.com/NewAge-BD/Soulcrest/releases/tag/v0.1.31",
            "assets": [
              { "name": "Soulcrest-0.1.31-Setup-win-x64.exe", "size": 423000000, "browser_download_url": "https://example.invalid/setup.exe" },
              { "name": "Soulcrest-0.1.31-Setup-win-x64.exe.sha256", "size": 102, "browser_download_url": "https://example.invalid/setup.exe.sha256" },
              { "name": "Soulcrest-0.1.31-Update-win-x64.exe", "size": 101000000, "browser_download_url": "https://example.invalid/update.exe" },
              { "name": "Soulcrest-0.1.31-Update-win-x64.exe.sha256", "size": 103, "browser_download_url": "https://example.invalid/update.exe.sha256" },
              { "name": "BUILD-INFO.json", "browser_download_url": "https://example.invalid/BUILD-INFO.json" } ] },
          { "tag_name": "mapdata", "draft": false, "prerelease": true, "body": "manifest-sha256: x", "assets": [] },
          { "tag_name": "v0.1.32", "draft": true, "prerelease": false, "body": "", "assets": [] },
          { "tag_name": "v0.1.30", "draft": false, "prerelease": false, "body": "## 0.1.30 – 2026-10-07\n\n- **Neu:** Eins.", "assets": [] },
          { "tag_name": "v0.1.29", "draft": false, "prerelease": false, "body": "", "assets": [] }
        ]
        """;

    [Fact]
    public void OnlyPublishedNewerVersionsNewestFirst()
    {
        var updates = UpdateService.Newer(Releases, new Version(0, 1, 29));
        Assert.Equal([new Version(0, 1, 31), new Version(0, 1, 30)], updates.Select(u => u.Version));
        Assert.Equal(new SetupAsset("https://example.invalid/setup.exe", "https://example.invalid/setup.exe.sha256", 423000000), updates[0].Full);
        Assert.Equal(new SetupAsset("https://example.invalid/update.exe", "https://example.invalid/update.exe.sha256", 101000000), updates[0].Update);
        Assert.Equal("https://example.invalid/BUILD-INFO.json", updates[0].BuildInfo);
        Assert.Null(updates[1].Full); // still being built
        Assert.Equal("Zwei.", PatchNotes.Parse(updates[0].Notes)[0].Entries[0].Text);
        Assert.Empty(UpdateService.Newer(Releases, new Version(0, 1, 31)));
    }

    [Fact]
    public void ChecksumFileOfTheInstallerBuild()
    {
        var hash = new string('a', 32) + new string('0', 32);
        Assert.Equal(hash, UpdateService.ChecksumOf($"{hash}  Soulcrest-0.1.31-Setup-win-x64.exe\r\n"));
        Assert.Null(UpdateService.ChecksumOf("not found"));
        Assert.Null(UpdateService.ChecksumOf(""));
    }

    [Fact]
    public void MapDataOfTheBuild()
    {
        Assert.Equal("550AE0", UpdateService.MapManifestOf("\uFEFF{ \"version\": \"0.1.31\", \"mapManifestSha256\": \"550AE0\" }"));
        Assert.Null(UpdateService.MapManifestOf("{ \"version\": \"0.1.31\" }"));
    }

    [Fact]
    public void VersionsCompareAsThreeNumbers()
    {
        Assert.Equal(new Version(0, 1, 30), UpdateService.ParseVersion("0.1.30"));
        Assert.Equal(new Version(0, 2, 0), UpdateService.ParseVersion("0.2"));
        Assert.True(UpdateService.ParseVersion("0.1.100") > UpdateService.ParseVersion("0.1.99"));
        Assert.Null(UpdateService.ParseVersion("dev"));
    }
}
