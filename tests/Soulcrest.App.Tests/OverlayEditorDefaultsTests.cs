using System.Text.Json;
using Soulcrest.App.OverlayEditorKit;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class OverlayEditorDefaultsTests
{
    [Fact]
    public async Task FirstOpenAndReloadOfferBothInactiveStandardsWithoutCreatingFiles()
    {
        using var fixture = new Fixture();
        using var service = new OverlayService(fixture.Directory);
        Assert.Null(service.LoadError);
        Assert.Equal(new[] { "Open World", "Abyss" }, service.Overlays.Select(window => window.Name));
        Assert.Equal(service.Overlays[1].Id, service.SelectedOverlayId);
        Assert.All(service.Overlays, window => Assert.False(window.Settings.Enabled));
        Assert.All(service.Overlays, window => Assert.Equal(8, window.Settings.Widgets.Count));
        Assert.False(Directory.Exists(fixture.Directory));
        Assert.True((await service.ReloadAsync()).Succeeded);
        Assert.Equal(2, service.Overlays.Count);
        Assert.Equal(service.Overlays[1].Id, service.SelectedOverlayId);
        Assert.False(Directory.Exists(fixture.Directory));
    }

    [Fact]
    public async Task FirstMutationPersistsTheSameWindowsAndWidgetsAndAdditionalWindowsStillStartEmpty()
    {
        using var fixture = new Fixture();
        using var service = new OverlayService(fixture.Directory);
        var before = service.Overlays.SelectMany(window => window.Settings.Widgets).Select(widget => widget.Id).ToArray();
        var selected = service.SelectedOverlayId;
        Assert.True((await service.SavePositionAsync(selected, .3, .4)).Succeeded);
        using var reopened = new OverlayService(fixture.Directory);
        Assert.Null(reopened.LoadError);
        Assert.Equal(selected, reopened.SelectedOverlayId);
        Assert.Equal(before, reopened.Overlays.SelectMany(window => window.Settings.Widgets).Select(widget => widget.Id));
        Assert.Equal((.3, .4), (reopened.Settings.PositionX, reopened.Settings.PositionY));
        Assert.True((await reopened.CreateOverlayAsync("Extra")).Succeeded);
        Assert.Empty(reopened.Settings.Widgets);
        Assert.False(reopened.Settings.Enabled);
        Assert.Equal(3, reopened.Overlays.Count);
    }

    [Fact]
    public async Task ExistingEmptyLayoutIsNeverReplacedByNewStandards()
    {
        using var fixture = new Fixture();
        var store = new OverlaySettingsStore(fixture.Directory);
        store.Save(new() { Overlays = [new() { Name = "Existing", Settings = new() { Width = 712, Height = 304 } }] });
        var original = await File.ReadAllBytesAsync(fixture.File);
        using var service = new OverlayService(fixture.Directory);
        Assert.Null(service.LoadError);
        Assert.Equal("Existing", Assert.Single(service.Overlays).Name);
        Assert.Empty(service.Settings.Widgets);
        Assert.Equal((712, 304), (service.Settings.Width, service.Settings.Height));
        Assert.True((await service.ReloadAsync()).Succeeded);
        Assert.Empty(service.Settings.Widgets);
        Assert.Equal(original, await File.ReadAllBytesAsync(fixture.File));
    }

    [Fact]
    public async Task BrokenLayoutRemainsProtectedAndIsNotSeededOver()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Directory);
        await File.WriteAllTextAsync(fixture.File, "{ broken layout");
        var original = await File.ReadAllBytesAsync(fixture.File);
        using var service = new OverlayService(fixture.Directory);
        Assert.NotNull(service.LoadError);
        Assert.False((await service.SaveAsync(OverlayEditorDefaults.CreateSettings(OverlayEditorDefaults.OpenWorld))).Succeeded);
        Assert.False((await service.ReloadAsync()).Succeeded);
        Assert.Equal(original, await File.ReadAllBytesAsync(fixture.File));
    }

    [Theory]
    [InlineData(OverlayEditorDefaults.OpenWorld, 336, 616, .85, true, 1)]
    [InlineData(OverlayEditorDefaults.Abyss, 360, 400, .85, true, .967741935483871)]
    public void PresetsKeepTheirApprovedVisualOptionsAndHavePortableInactivePlacement(string id,
        double width, double height, double opacity, bool border, double scale)
    {
        var settings = OverlayEditorDefaults.CreateSettings(id);
        Assert.Equal((width, height, opacity, border, scale),
            (settings.Width, settings.Height, settings.BackgroundOpacity, settings.ShowBorder, settings.Scale));
        Assert.False(settings.Enabled);
        Assert.Null(settings.PositionMonitorId);
        Assert.Equal((.02, .15), (settings.PositionX, settings.PositionY));
        Assert.Equal("move", settings.Interaction);
        Assert.Equal("game", settings.Visibility);
        Assert.True(settings.CaptureExcluded);
        Assert.True(settings.SnapToGrid);
        Assert.True(settings.AutoAlign);
        Assert.Equal(8, settings.Widgets.Count);
        Assert.All(settings.Widgets, widget =>
        {
            Assert.NotNull(OverlayCatalog.Find(widget.Kind));
            Assert.True(Guid.TryParse(widget.Id, out _));
            Assert.True(widget.ShowIcon);
            Assert.Empty(widget.ItemKeys);
            Assert.Equal((8, 56, "all", "default", 5),
                (widget.ItemLimit, widget.ItemSize, widget.ItemFilter, widget.ItemSort, widget.KillFeedRows));
        });
        OverlaySettingsStore.ValidateWidgets(settings);
    }

    [Theory]
    [InlineData(OverlayEditorDefaults.OpenWorld, OverlayTriggerArea.OpenWorld, OverlayTriggerRequirement.Excluded)]
    [InlineData(OverlayEditorDefaults.Abyss, OverlayTriggerArea.Abyss, OverlayTriggerRequirement.Any)]
    public void PresetsKeepContextGuardsWithoutStartingAnySource(string id, OverlayTriggerArea area,
        OverlayTriggerRequirement minimapMissing)
    {
        var rules = OverlayEditorDefaults.CreateSettings(id).TriggerRules!;
        Assert.Equal(area, Assert.Single(rules.Areas));
        Assert.Equal(OverlayTriggerRequirement.Required, rules.Foreground);
        Assert.Equal(OverlayTriggerRequirement.Required, rules.SessionActive);
        Assert.Equal(OverlayTriggerRequirement.Excluded, rules.WorldMap);
        Assert.Equal(OverlayTriggerRequirement.Excluded, rules.EscMenu);
        Assert.Equal(minimapMissing, rules.MinimapMissing);
        Assert.False(OverlayTriggerPolicy.Matches(rules, new()));
        Assert.True(OverlayTriggerPolicy.Matches(rules, new(area, true, true, false, false, false)));
        Assert.False(OverlayTriggerPolicy.Matches(rules, new(area, true, false, false, false, false)));
    }

    [Fact]
    public void WorldListKeepsIndependentTimeAndRateRowsAndItsFullListHeight()
    {
        var settings = OverlayEditorDefaults.CreateSettings(OverlayEditorDefaults.OpenWorld);
        Assert.Equal(new[] { "duration", "market-kina", "clock", "dropped-kina", "daily-kina",
            "dropped-kina-hour", "market-kina-hour", "drop-list" }, settings.Widgets.Select(widget => widget.Kind));
        var duration = settings.Widgets.Single(widget => widget.Kind == "duration");
        Assert.Equal((168, 176, 168, 48, 168d, 72d, 1.5),
            (duration.X, duration.Y, duration.Width, duration.Height, duration.ContentWidth, duration.ContentHeight, duration.FontScale));
        var list = settings.Widgets.Single(widget => widget.Kind == "drop-list");
        Assert.Equal((0, 216, 336, 400, 344d, 224d, "list"),
            (list.X, list.Y, list.Width, list.Height, list.ContentWidth, list.ContentHeight, list.ItemView));
        Assert.All(settings.Widgets, widget => Assert.False(widget.ShowLabel));
        Assert.All(settings.Widgets.Where(widget => widget.Kind.Contains("kina") && widget.Kind != "daily-kina"),
            widget => Assert.Equal(1.3, widget.FontScale));
    }

    [Fact]
    public void AbyssKeepsTheSeparateNetAndRateLabelsAndOriginalCanvasClipping()
    {
        var settings = OverlayEditorDefaults.CreateSettings(OverlayEditorDefaults.Abyss);
        Assert.Equal(new[] { "rank-net-hour", "rank-net", "abyss-killfeed" },
            settings.Widgets.Where(widget => widget.ShowLabel).Select(widget => widget.Kind));
        var killfeed = settings.Widgets.Single(widget => widget.Kind == "abyss-killfeed");
        Assert.Null(killfeed.ContentWidth);
        Assert.Null(killfeed.ContentHeight);
        Assert.Equal(408, killfeed.Y + killfeed.Height);
        Assert.Equal(400, OverlayLayout.Normalize(settings).Height);
        Assert.Equal(8, killfeed.X + killfeed.Width - settings.Width);
        Assert.Equal(killfeed, OverlayLayout.Normalize(settings).Widgets.Single(widget => widget.Kind == "abyss-killfeed"));
    }

    [Fact]
    public void FactoriesReturnIndependentIdentitiesAndReadOnlyCollections()
    {
        var first = OverlayEditorDefaults.CreateSettings(OverlayEditorDefaults.OpenWorld);
        var second = OverlayEditorDefaults.CreateSettings(OverlayEditorDefaults.OpenWorld);
        Assert.NotSame(first.Widgets, second.Widgets);
        Assert.Empty(first.Widgets.Select(widget => widget.Id).Intersect(second.Widgets.Select(widget => widget.Id)));
        var edited = first with { Widgets = first.Widgets.Select(widget => widget with { FontScale = 2 }).ToArray() };
        Assert.NotEqual(edited.Widgets[0].FontScale, second.Widgets[0].FontScale);
        Assert.True(((IList<OverlayWidget>)first.Widgets).IsReadOnly);
    }

    [Fact]
    public void CollectionUsesOnlyNeutralNamesAndIdsAndCanSelectTheAbyssWindow()
    {
        var collection = OverlayEditorDefaults.CreateCollection(
            [OverlayEditorDefaults.OpenWorld, OverlayEditorDefaults.Abyss], OverlayEditorDefaults.Abyss);
        Assert.Equal(new[] { "Open World", "Abyss" }, collection.Overlays.Select(window => window.Name));
        Assert.Equal(OverlayInstance.DefaultId, collection.Overlays[0].Id);
        Assert.Equal(collection.Overlays[1].Id, collection.SelectedOverlayId);
        Assert.All(collection.Overlays, window => Assert.False(window.Settings.Enabled));
        var json = JsonSerializer.Serialize(collection);
        Assert.DoesNotContain("DISPLAY", json);
        Assert.DoesNotContain("SessionId", json);
        Assert.DoesNotContain("Character", json);
        Assert.DoesNotContain("CapturedAt", json);
    }

    [Fact]
    public void GenericEmptyModelsAndAdditionalWindowsRemainEmpty()
    {
        Assert.Empty(new OverlaySettings().Widgets);
        Assert.Empty(new OverlayCollectionSettings().Overlays.Single().Settings.Widgets);
        Assert.Empty(OverlayCatalog.Preset("empty").Widgets);
        Assert.Single(OverlayCatalog.Preset("demo").Widgets);
    }

    [Fact]
    public void InvalidPresetSelectionsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => OverlayEditorDefaults.CreateSettings("unknown"));
        Assert.Throws<ArgumentNullException>(() => OverlayEditorDefaults.CreateCollection(null!));
        Assert.Throws<ArgumentException>(() => OverlayEditorDefaults.CreateCollection([]));
        Assert.Throws<ArgumentException>(() => OverlayEditorDefaults.CreateCollection([OverlayEditorDefaults.Abyss, OverlayEditorDefaults.Abyss]));
        Assert.Throws<ArgumentException>(() => OverlayEditorDefaults.CreateCollection(["unknown"]));
        Assert.Throws<ArgumentException>(() => OverlayEditorDefaults.CreateCollection([OverlayEditorDefaults.Abyss], OverlayEditorDefaults.OpenWorld));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "Soulcrest-overlay-defaults-" + Guid.NewGuid().ToString("N"));
        internal string Directory => Path.Combine(_root, "overlay-editor");
        internal string File => Path.Combine(Directory, "overlay.json");
        public void Dispose() { if (System.IO.Directory.Exists(_root)) System.IO.Directory.Delete(_root, recursive: true); }
    }
}
