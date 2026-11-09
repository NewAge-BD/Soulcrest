using System.Text.Json;
using System.Text.Json.Nodes;
using Soulcrest.App.OverlayEditorKit;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class OverlayLockWidgetModelTests
{
    [Fact]
    public void TheRequiredControlIsIndependentOfAllTwentyFourOptionalContentSlots()
    {
        var content = Enumerable.Range(0, OverlayCatalog.MaximumWidgets).Select(_ => OverlayCatalog.CreateWidget("clock")).ToArray();
        var settings = OverlayLayout.Normalize(new() { Widgets = content });
        var control = OverlayLockWidget.Layout(settings);
        Assert.Equal(content.Select(widget => widget.Id), settings.Widgets.Select(widget => widget.Id));
        Assert.Equal(OverlayCatalog.MaximumWidgets, settings.Widgets.Count);
        Assert.Equal(OverlayLockWidget.Id, control.Id);
        Assert.Equal(OverlayLockWidget.Kind, control.Kind);
        Assert.Equal((28, 28), (control.Width, control.Height));
        Assert.False(control.ShowLabel);
        Assert.True(control.ShowIcon);
        Assert.Equal((324, 8), (control.X, control.Y));
        Assert.DoesNotContain(OverlayCatalog.Widgets, item => item.Kind == OverlayLockWidget.Kind);
        Assert.Same(OverlayLockWidget.Definition, OverlayCatalog.Find(OverlayLockWidget.Kind));
        Assert.Throws<ArgumentException>(() => OverlayCatalog.CreateWidget(OverlayLockWidget.Kind));
        Assert.Empty(new OverlaySettings().Widgets);
        OverlaySettingsStore.ValidateWidgets(settings);
    }

    [Fact]
    public void AnIllicitOptionalLockCannotDuplicateTheControlOrConsumeAContentSlot()
    {
        var content = Enumerable.Range(0, OverlayCatalog.MaximumWidgets).Select(_ => OverlayCatalog.CreateWidget("duration")).ToArray();
        var illicit = OverlayLockWidget.Layout(new()) with { Id = Guid.NewGuid().ToString("N") };
        var invalid = new OverlaySettings { Widgets = new[] { illicit }.Concat(content).ToArray() };
        Assert.Throws<InvalidDataException>(() => OverlaySettingsStore.ValidateWidgets(invalid));
        var normalized = OverlayLayout.Normalize(invalid);
        Assert.Equal(content.Select(widget => widget.Id), normalized.Widgets.Select(widget => widget.Id));
        Assert.DoesNotContain(normalized.Widgets, widget => widget.Kind == OverlayLockWidget.Kind);
    }

    [Theory]
    [InlineData(null, null, 360, 260, 324, 8)]
    [InlineData(-10d, 500d, 360, 260, 0, 232)]
    [InlineData(500d, -10d, 160, 64, 132, 0)]
    [InlineData(double.NaN, double.PositiveInfinity, 360, 260, 324, 8)]
    [InlineData(double.NegativeInfinity, double.NaN, double.NaN, double.PositiveInfinity, 324, 8)]
    [InlineData(15.25, 22.75, 360, 260, 15.25, 22.75)]
    public void ControlPositionsAreFiniteAndStayInsideTheActualCanvas(double? x, double? y,
        double width, double height, double expectedX, double expectedY)
    {
        var settings = OverlayLayout.Normalize(new() { Width = width, Height = height, LockWidget = new() { X = x, Y = y } });
        var position = OverlayLockWidget.Position(settings);
        var bounds = OverlayLockWidget.Bounds(settings);
        Assert.Equal(expectedX, position.X);
        Assert.Equal(expectedY, position.Y);
        Assert.Equal((float)expectedX, bounds.X);
        Assert.Equal((float)expectedY, bounds.Y);
        Assert.True(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= settings.Width && bounds.Bottom <= settings.Height);
    }

    [Fact]
    public void ShrinkingTheViewportKeepsTheControlReachableAndPreservesClippedContent()
    {
        var content = OverlayCatalog.CreateWidget("clock", 800, 600);
        var original = OverlayLayout.Normalize(new() { Width = 1000, Height = 800, Widgets = [content],
            LockWidget = new() { X = 950, Y = 750 } });
        var resized = OverlayLayout.ResizeCanvas(original, 160, 64);
        Assert.Equal(OverlayLockWidget.Normalize(new() { X = 950, Y = 750 }, 160, 64), resized.LockWidget);
        Assert.Equal((132, 36), (OverlayLockWidget.Layout(resized).X, OverlayLockWidget.Layout(resized).Y));
        Assert.Equal(content, Assert.Single(resized.Widgets));
    }

    [Fact]
    public async Task AFileWithoutTheNewFieldLoadsTheControlWithoutRewritingTheStoredLayout()
    {
        using var fixture = new Fixture();
        var collection = new OverlayCollectionSettings { Overlays = [new() { Settings = new() { Width = 480, Height = 192 } }] };
        var document = JsonSerializer.SerializeToNode(collection)!;
        document["Overlays"]![0]!["Settings"]!.AsObject().Remove("LockWidget");
        Directory.CreateDirectory(fixture.Directory);
        await File.WriteAllTextAsync(fixture.LayoutPath, document.ToJsonString());
        var bytes = await File.ReadAllBytesAsync(fixture.LayoutPath);
        using var service = fixture.Create();
        Assert.Null(service.LoadError);
        Assert.Equal((444, 8), (OverlayLockWidget.Layout(service.Settings).X, OverlayLockWidget.Layout(service.Settings).Y));
        Assert.Empty(service.Settings.Widgets);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(fixture.LayoutPath));
        Assert.True((await service.ReloadAsync()).Succeeded);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(fixture.LayoutPath));
    }

    [Fact]
    public void OpeningTheStandardWorkspaceDoesNotCreateAnyFiles()
    {
        using var fixture = new Fixture();
        using var service = new OverlayService(fixture.Directory);
        Assert.All(service.Overlays, window => Assert.NotNull(window.Settings.LockWidget));
        Assert.All(service.Overlays, window => Assert.Equal(8, window.Settings.Widgets.Count));
        Assert.False(Directory.Exists(fixture.Directory));
    }

    [Fact]
    public async Task DuplicationAndTemplatesKeepThePositionWhileAdditionalEmptyWindowsGetTheirOwnControl()
    {
        using var fixture = new Fixture();
        using var service = fixture.Create();
        var first = service.SelectedOverlayId;
        Assert.True((await service.SaveAsync(OverlayLockWidget.WithPosition(service.Settings, 12.5, 44.25))).Succeeded);
        Assert.True((await service.SaveTemplateAsync("Control layout", service.Settings)).Succeeded);
        Assert.True((await service.CreateOverlayAsync("Copy", first)).Succeeded);
        Assert.Equal(new OverlayLockWidgetPosition { X = 12.5, Y = 44.25 }, service.Settings.LockWidget);
        Assert.True((await service.CreateOverlayAsync("Empty")).Succeeded);
        Assert.Empty(service.Settings.Widgets);
        Assert.Equal((324, 8), (OverlayLockWidget.Layout(service.Settings).X, OverlayLockWidget.Layout(service.Settings).Y));
        using var restarted = fixture.Create();
        var template = Assert.Single(restarted.Templates);
        Assert.Equal(new OverlayLockWidgetPosition { X = 12.5, Y = 44.25 }, template.Layout.LockWidget);
        var target = restarted.Settings with { PositionX = .6, PositionY = .4, PositionMonitorId = "target",
            Enabled = true, Visibility = "always", TriggerRules = new() { WorldMap = OverlayTriggerRequirement.Required } };
        var applied = template.ApplyTo(target);
        Assert.Equal(template.Layout.LockWidget, applied.LockWidget);
        Assert.Equal((target.PositionX, target.PositionY, target.PositionMonitorId, target.Enabled, target.Visibility),
            (applied.PositionX, applied.PositionY, applied.PositionMonitorId, applied.Enabled, applied.Visibility));
        Assert.True(OverlayTriggerRules.Equivalent(target.TriggerRules, applied.TriggerRules));
    }

    [Fact]
    public async Task StaleContentSavesAndOlderCallersCannotOverwriteANewerControlPosition()
    {
        using var fixture = new Fixture();
        using var editor = fixture.Create();
        Assert.True((await editor.SaveAsync(editor.Settings with { Widgets = [OverlayCatalog.CreateWidget("clock")] })).Succeeded);
        using var control = fixture.Create();
        var stale = editor.Settings with { Width = 640 };
        Assert.True((await control.SaveAsync(OverlayLockWidget.WithPosition(control.Settings, 40, 52))).Succeeded);
        Assert.True((await editor.SaveAsync(stale)).Succeeded);
        Assert.Equal(new OverlayLockWidgetPosition { X = 40, Y = 52 }, editor.Settings.LockWidget);
        Assert.Equal(640, editor.Settings.Width);

        Assert.True((await control.SaveAsync(OverlayLockWidget.WithPosition(control.Settings, 88, 96))).Succeeded);
        Assert.True((await editor.SaveAsync(new() { Width = 720, Widgets = editor.Settings.Widgets })).Succeeded);
        Assert.Equal(new OverlayLockWidgetPosition { X = 88, Y = 96 }, editor.Settings.LockWidget);
        Assert.Single(editor.Settings.Widgets);
    }

    [Fact]
    public async Task AnExplicitControlMovePreservesNewerPlacementAndSurvivesRestart()
    {
        using var fixture = new Fixture();
        using var editor = fixture.Create();
        Assert.True((await editor.SaveAsync(editor.Settings)).Succeeded);
        using var placement = fixture.Create();
        var moved = OverlayLockWidget.WithPosition(editor.Settings, 48, 24);
        Assert.True((await placement.SavePlacementAsync(placement.SelectedOverlayId, "new-monitor", .7, .8, 1.6)).Succeeded);
        Assert.True((await editor.SaveAsync(moved)).Succeeded);
        using var restarted = fixture.Create();
        Assert.Equal(new OverlayLockWidgetPosition { X = 48, Y = 24 }, restarted.Settings.LockWidget);
        Assert.Equal(("new-monitor", .7, .8, 1.6), (restarted.Settings.PositionMonitorId,
            restarted.Settings.PositionX, restarted.Settings.PositionY, restarted.Settings.Scale));
        var json = await File.ReadAllTextAsync(fixture.LayoutPath);
        Assert.DoesNotContain("Unlocked", json);
        Assert.DoesNotContain("Locked", json);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "Soulcrest-lock-widget-model-" + Guid.NewGuid().ToString("N"));
        internal string Directory => Path.Combine(_root, "editor");
        internal string LayoutPath => Path.Combine(Directory, "overlay.json");
        internal OverlayService Create() => new(Directory, new OverlayCollectionSettings());
        public void Dispose() { if (System.IO.Directory.Exists(_root)) System.IO.Directory.Delete(_root, recursive: true); }
    }
}
