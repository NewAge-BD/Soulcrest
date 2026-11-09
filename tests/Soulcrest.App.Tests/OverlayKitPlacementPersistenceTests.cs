using System.Text.Json;
using Soulcrest.App.OverlayEditorKit;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class OverlayKitPlacementPersistenceTests
{
    [Fact]
    public async Task PlacementCommitsOnlyScreenGeometryAndSurvivesRestart()
    {
        using var fixture = new Fixture();
        using var service = new OverlayService(fixture.Directory);
        var rules = new OverlayTriggerRules { WorldMap = OverlayTriggerRequirement.Excluded };
        var content = service.Settings with
        {
            Width = 640, Height = 384, Enabled = false, TriggerRules = rules,
            Widgets = [OverlayCatalog.CreateWidget("daily-kina", 16, 24)],
        };
        Assert.True((await service.SaveAsync(content)).Succeeded);
        var first = service.SelectedOverlayId;
        Assert.True((await service.CreateOverlayAsync("Second window")).Succeeded);
        var selected = service.SelectedOverlayId;
        Assert.True((await service.SavePlacementAsync(first, "left-monitor", .75, .25, 1.65)).Succeeded);
        Assert.Equal(selected, service.SelectedOverlayId);
        using var restarted = new OverlayService(fixture.Directory);
        var placed = restarted.Overlays.Single(window => window.Id == first).Settings;
        Assert.Equal("left-monitor", placed.PositionMonitorId);
        Assert.Equal(.75, placed.PositionX);
        Assert.Equal(.25, placed.PositionY);
        Assert.Equal(1.65, placed.Scale);
        Assert.Equal(640, placed.Width);
        Assert.Equal(384, placed.Height);
        Assert.False(placed.Enabled);
        Assert.True(OverlayTriggerRules.Equivalent(rules, placed.TriggerRules));
        Assert.Equal(JsonSerializer.Serialize(content.Widgets), JsonSerializer.Serialize(placed.Widgets));
    }

    [Fact]
    public async Task StaleLayoutSaveKeepsNewerPlacementAndPlacementKeepsNewerContent()
    {
        using var fixture = new Fixture();
        using var screen = new OverlayService(fixture.Directory);
        Assert.True((await screen.SaveAsync(screen.Settings with { Widgets = [OverlayCatalog.CreateWidget("clock")] })).Succeeded);
        using var editor = new OverlayService(fixture.Directory);
        var draft = editor.Settings with { Width = 720 };
        Assert.True((await screen.SavePlacementAsync(screen.SelectedOverlayId, "negative-origin-monitor", .9, .8, 1.8)).Succeeded);
        Assert.True((await editor.SaveAsync(draft)).Succeeded);
        Assert.Equal(1.8, editor.Settings.Scale);
        Assert.Equal("negative-origin-monitor", editor.Settings.PositionMonitorId);
        Assert.True((await screen.SavePlacementAsync(screen.SelectedOverlayId, "other-monitor", .2, .3, .7)).Succeeded);
        Assert.Equal(720, screen.Settings.Width);
        Assert.Single(screen.Settings.Widgets);
        Assert.True((await editor.ReloadAsync()).Succeeded);
        Assert.True((await editor.SaveAsync(editor.Settings with { Scale = 1.25 })).Succeeded);
        Assert.Equal(1.25, editor.Settings.Scale);
        Assert.Equal("other-monitor", editor.Settings.PositionMonitorId);
    }

    [Theory]
    [InlineData(double.NaN, 0, 1, "monitor")]
    [InlineData(0, double.PositiveInfinity, 1, "monitor")]
    [InlineData(0, 0, double.NaN, "monitor")]
    [InlineData(0, 0, 1, "bad\nmonitor")]
    public async Task InvalidPlacementDoesNotWrite(double x, double y, double scale, string monitor)
    {
        using var fixture = new Fixture();
        using var service = new OverlayService(fixture.Directory);
        Assert.False((await service.SavePlacementAsync(service.SelectedOverlayId, monitor, x, y, scale)).Succeeded);
        Assert.False(System.IO.Directory.Exists(fixture.Directory));
    }

    [Fact]
    public async Task CorruptMonitorBindingIsPreservedAndBlocksFurtherWrites()
    {
        using var fixture = new Fixture();
        using (var original = new OverlayService(fixture.Directory))
            Assert.True((await original.SavePlacementAsync(original.SelectedOverlayId, "monitor", .1, .2, 1)).Succeeded);
        var file = Path.Combine(fixture.Directory, "overlay.json");
        var document = File.ReadAllText(file).Replace("\"monitor\"", "\"bad\\nmonitor\"", StringComparison.Ordinal);
        File.WriteAllText(file, document);
        var bytes = File.ReadAllBytes(file);
        using var service = new OverlayService(fixture.Directory);
        Assert.NotNull(service.LoadError);
        Assert.False((await service.SavePlacementAsync(service.SelectedOverlayId, "monitor", .3, .4, 1)).Succeeded);
        Assert.Equal(bytes, File.ReadAllBytes(file));
    }

    [Fact]
    public void TemplatesDoNotCopyMonitorBindingAndPreserveTheTargetBinding()
    {
        var source = new OverlaySettings { PositionMonitorId = "source", PositionX = .7, PositionY = .8 };
        var template = OverlayTemplate.Create("Appearance", source);
        Assert.Null(template.Layout.PositionMonitorId);
        var target = new OverlaySettings { PositionMonitorId = "target", PositionX = .2, PositionY = .4 };
        var applied = template.ApplyTo(target);
        Assert.Equal(target.PositionMonitorId, applied.PositionMonitorId);
        Assert.Equal(target.PositionX, applied.PositionX);
        Assert.Equal(target.PositionY, applied.PositionY);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "soulcrest-kit-placement-" + Guid.NewGuid().ToString("N"));
        public string Directory => Path.Combine(_root, "editor");
        public void Dispose() { if (System.IO.Directory.Exists(_root)) System.IO.Directory.Delete(_root, true); }
    }
}
