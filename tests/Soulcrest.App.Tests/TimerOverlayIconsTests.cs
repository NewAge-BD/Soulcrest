using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class TimerOverlayIconsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("../outside.png")]
    [InlineData("icons/../outside.png")]
    [InlineData("icons/./boss.png")]
    [InlineData("icons//boss.png")]
    [InlineData("/icons/boss.png")]
    [InlineData("C:\\icons\\boss.png")]
    [InlineData("\\\\server\\share\\boss.png")]
    [InlineData("https://example.invalid/boss.png")]
    [InlineData("icons/boss.png:stream")]
    [InlineData("icons/%2e%2e/boss.png")]
    [InlineData("icons/.. /boss.png")]
    [InlineData("icons/trailing./boss.png")]
    public void UnsafeBossPathsBecomeTheBundledBossIcon(string? path) =>
        Assert.Equal("boss", TimerOverlayIcons.ForBoss(path));

    [Fact]
    public void RelativeBossPathsNormalizeSeparatorsAndEscapeEachBrowserSegment()
    {
        var id = TimerOverlayIcons.ForBoss("icons\\Boss portraits\\boss #1.png");
        Assert.Equal("map:icons/Boss portraits/boss #1.png", id);
        Assert.Equal("https://mapdata.soulcrest/icons/Boss%20portraits/boss%20%231.png", TimerOverlayIcons.BrowserSource(id));
        var root = Path.Combine(Path.GetTempPath(), "soulcrest-icon-root");
        Assert.Equal(Path.GetFullPath(Path.Combine(root, "icons", "Boss portraits", "boss #1.png")), TimerOverlayIcons.LocalPath(id, root));
    }

    [Theory]
    [InlineData("shugo", "shugo-festival.png")]
    [InlineData("rift", "spacetime-rift.png")]
    [InlineData("invasion", "dimensional-invasion.png")]
    [InlineData("abyss", "artifact-siege.png")]
    [InlineData("daily-reset", "daily-reset.png")]
    [InlineData("weekly-reset", "weekly-reset.png")]
    [InlineData("custom", "custom-timer.png")]
    [InlineData("boss", "boss-timer.png")]
    [InlineData("event:catalog:daily-reset", "daily-reset.png")]
    [InlineData("boss:1110:111001", "boss-timer.png")]
    [InlineData("map:icons/boss.png", "boss-timer.png")]
    [InlineData(null, "custom-timer.png")]
    [InlineData("unknown", "custom-timer.png")]
    public void EveryTimerTypeHasAShippedFallback(string? id, string file)
    {
        Assert.Equal(file, TimerOverlayIcons.BundledFile(id));
        if (id?.StartsWith("map:", StringComparison.Ordinal) != true)
        {
            Assert.Equal("icons/events/" + file, TimerOverlayIcons.BrowserSource(id));
            Assert.Equal(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "wwwroot", "icons", "events", file)),
                TimerOverlayIcons.LocalPath(id, null));
        }
    }

    [Theory]
    [InlineData("map:../outside.png")]
    [InlineData("map:icons/../../outside.png")]
    [InlineData("map:C:/outside.png")]
    [InlineData("map://server/share/boss.png")]
    [InlineData("map:icons/%2e%2e/outside.png")]
    public void ForgedMapIdsCannotEscapeIntoAFileOrRemoteBrowserSource(string id)
    {
        Assert.Equal("icons/events/boss-timer.png", TimerOverlayIcons.BrowserSource(id));
        Assert.Equal(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "wwwroot", "icons", "events", "boss-timer.png")),
            TimerOverlayIcons.LocalPath(id, Path.GetTempPath()));
    }

    [Fact]
    public void AValidMapIdNeedsAnExplicitRootAndPathResolutionDoesNotCreateOrLoadAnything()
    {
        var root = Path.Combine(Path.GetTempPath(), "soulcrest-icon-" + Guid.NewGuid().ToString("N"));
        var id = TimerOverlayIcons.ForBoss("icons/boss.png");
        Assert.Null(TimerOverlayIcons.LocalPath(id, null));
        Assert.Null(TimerOverlayIcons.LocalPath(id, ""));
        Assert.Null(TimerOverlayIcons.LocalPath(id, "\0"));
        var expected = Path.GetFullPath(Path.Combine(root, "icons", "boss.png"));
        Assert.Equal(expected, TimerOverlayIcons.LocalPath(id, root));
        Assert.False(Directory.Exists(root));
    }
}
