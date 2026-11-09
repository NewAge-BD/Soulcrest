using System.Text.Json;
using Soulcrest.App.OverlayEditorKit;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class OverlayKitServiceTests
{
    [Theory]
    [InlineData("de", "Beispielwert", "1.234", "Beispieldaten")]
    [InlineData("en", "Example value", "1,234", "Sample data")]
    public void CatalogContainsApprovedModulesAndKeepsNeutralExampleLayoutsCompatible(string language, string label, string value, string detail)
    {
        Assert.Equal(22, OverlayCatalog.Widgets.Count);
        Assert.Equal(22, OverlayCatalog.Widgets.Select(module => module.Kind).Distinct().Count());
        Assert.DoesNotContain(OverlayCatalog.Widgets, module => module.Kind == "preview-metric");
        Assert.DoesNotContain(OverlayCatalog.Widgets, module => module.Kind is "silver" or "trash" or "experience" or "spot" or "controls");
        var legacy = OverlayCatalog.Find("preview-metric");
        Assert.NotNull(legacy);
        Assert.Equal(168, legacy.Width);
        Assert.Equal(72, legacy.Height);
        var snapshot = OverlaySnapshot.DemoFor(language);
        var metric = snapshot.Metrics["preview-metric"];
        Assert.Equal(OverlayEditorThemes.DefaultId, snapshot.ThemeId);
        Assert.Equal(label, metric.Label);
        Assert.Equal(value, metric.Value);
        Assert.Equal(detail, metric.Detail);
        Assert.Empty(new OverlaySettings().Widgets);
        Assert.Single(OverlayCatalog.Preset("demo").Widgets);
        Assert.Empty(OverlayCatalog.Preset("empty").Widgets);
        Assert.Throws<ArgumentException>(() => OverlayCatalog.CreateWidget("silver"));
    }

    [Fact]
    public void NormalizeRetainsKitLimitsAndClippedModuleContentReferences()
    {
        var widgets = Enumerable.Range(0, 30).Select(_ => OverlayCatalog.CreateWidget("preview-metric", 900, 800)).ToArray();
        var settings = OverlayLayout.Normalize(new()
        {
            Width = 1, Height = 5000, Scale = double.NaN, BackgroundOpacity = 7,
            PositionX = -7, PositionY = 2, Interaction = "unknown", Visibility = "unknown", Widgets = widgets,
        });
        Assert.Equal(160, settings.Width);
        Assert.Equal(1200, settings.Height);
        Assert.Equal(1, settings.Scale);
        Assert.Equal(1, settings.BackgroundOpacity);
        Assert.Equal(0, settings.PositionX);
        Assert.Equal(1, settings.PositionY);
        Assert.Equal("move", settings.Interaction);
        Assert.Equal("game", settings.Visibility);
        Assert.Equal(24, settings.Widgets.Count);
        var resized = OverlayLayout.ResizeWidget(settings.Widgets[0], 336, 144);
        Assert.Equal(168, resized.ContentWidth);
        Assert.Equal(72, resized.ContentHeight);
        var twice = OverlayLayout.ResizeWidget(resized, 84, 36);
        Assert.Equal(168, twice.ContentWidth);
        Assert.Equal(72, twice.ContentHeight);
        var before = OverlayLayout.Normalize(new() { Width = 1200, Height = 1000, Widgets = [resized] });
        var small = OverlayLayout.ResizeCanvas(before, 160, 64);
        Assert.Equal(before.Widgets, small.Widgets);
        Assert.Equal(before.Widgets, OverlayLayout.ResizeCanvas(small, 1200, 1000).Widgets);
    }

    [Theory]
    [InlineData(84, 36, 1)]
    [InlineData(168, 72, 2)]
    [InlineData(336, 144, .7)]
    public void ContentLayoutFitsUniformlyWithoutDistortingTheReference(double width, double height, double fontScale)
    {
        var widget = OverlayLayout.ResizeWidget(OverlayCatalog.CreateWidget("preview-metric"), width, height) with { FontScale = fontScale };
        var layout = OverlayContentLayout.Create(widget, OverlaySnapshot.Demo);
        Assert.Equal(168, layout.ReferenceWidth);
        Assert.Equal(72, layout.ReferenceHeight);
        Assert.Equal(width, layout.LayoutWidget.Width * layout.Scale, 8);
        Assert.Equal(height, layout.LayoutWidget.Height * layout.Scale, 8);
        Assert.True(double.IsFinite(layout.Scale) && layout.Scale > 0);
        Assert.Equal(fontScale, layout.LayoutWidget.FontScale);
        Assert.True(layout.LayoutWidget.Height >= layout.MinimumHeight);
        Assert.False(OverlayContentLayout.Reflows(widget));
        Assert.False(OverlayWindowChrome.For("grindcrest", true).HasTitleBar);
    }

    [Fact]
    public async Task OpeningAndPreviewingDoNotCreateFilesOrChangeExistingSettings()
    {
        using var fixture = new Fixture();
        var oldSettings = Path.Combine(fixture.Root, "settings.json");
        var oldWorkspace = Path.Combine(fixture.Root, "overlay-workspace.json");
        File.WriteAllText(oldSettings, "legacy settings remain byte-identical");
        File.WriteAllText(oldWorkspace, "discarded editor is not imported");
        var settingsBefore = File.ReadAllBytes(oldSettings);
        using var service = fixture.Create();
        var changed = 0;
        service.Changed += () => changed++;
        var initial = JsonSerializer.Serialize(service.Overlays);
        await service.SetPreviewAsync(true);
        Assert.True(service.State.Previewing);
        await service.SetPreviewAsync(false);
        Assert.False(service.State.Previewing);
        Assert.False(Directory.Exists(fixture.Directory));
        Assert.Equal(initial, JsonSerializer.Serialize(service.Overlays));
        Assert.Equal(settingsBefore, File.ReadAllBytes(oldSettings));
        Assert.Equal("discarded editor is not imported", File.ReadAllText(oldWorkspace));
        Assert.Equal(2, changed);
    }

    [Fact]
    public async Task WindowCrudPersistsIndependentIdsAndKeepsTheLastWindow()
    {
        using var fixture = new Fixture();
        using var service = fixture.Create();
        Assert.Empty(service.Settings.Widgets);
        Assert.True((await service.SaveAsync(service.Settings with
        {
            Widgets = [OverlayCatalog.CreateWidget("preview-metric")],
        })).Succeeded);
        var initialId = Assert.Single(service.Settings.Widgets).Id;
        Assert.True((await service.RenameOverlayAsync("default", "  Demo  ")).Succeeded);
        Assert.Equal(initialId, Assert.Single(service.Settings.Widgets).Id);
        Assert.True((await service.CreateOverlayAsync("Kopie", "default")).Succeeded);
        var copyId = service.SelectedOverlayId;
        Assert.NotEqual("default", copyId);
        Assert.NotEqual(initialId, Assert.Single(service.Settings.Widgets).Id);
        Assert.False(service.Settings.Enabled);
        Assert.True((await service.SaveAsync(copyId, service.Settings with { Width = 512 })).Succeeded);
        Assert.Equal(360, service.Overlays.Single(overlay => overlay.Id == "default").Settings.Width);
        Assert.False((await service.RenameOverlayAsync(copyId, "demo")).Succeeded);
        Assert.Null(service.LoadError);
        Assert.True((await service.CreateOverlayAsync("Leer")).Succeeded);
        Assert.Empty(service.Settings.Widgets);
        Assert.True((await service.DeleteOverlayAsync(service.SelectedOverlayId)).Succeeded);
        Assert.Equal("default", service.SelectedOverlayId);
        Assert.True((await service.DeleteOverlayAsync(copyId)).Succeeded);
        Assert.False((await service.DeleteOverlayAsync("default")).Succeeded);
        using var reopened = fixture.Create();
        Assert.Equal("Demo", Assert.Single(reopened.Overlays).Name);
        Assert.Null(reopened.LoadError);
        using var document = JsonDocument.Parse(File.ReadAllBytes(fixture.LayoutPath));
        Assert.Equal(2, document.RootElement.GetProperty("Version").GetInt32());
        Assert.Empty(Directory.GetFiles(fixture.Directory, "*.tmp"));
    }

    [Fact]
    public async Task ConcurrentServiceInstancesPreserveOtherWindowsAndTheCurrentPositionAndBehavior()
    {
        using var fixture = new Fixture();
        using var first = fixture.Create();
        using var second = fixture.Create();
        var stale = first.Settings;
        Assert.True((await second.SaveAsync(second.Settings with { Enabled = true, Interaction = "passthrough" })).Succeeded);
        Assert.True((await second.SavePositionAsync(.7, .8)).Succeeded);
        Assert.True((await first.SaveAsync(stale with { Width = 500 })).Succeeded);
        Assert.True(first.Settings.Enabled);
        Assert.Equal("passthrough", first.Settings.Interaction);
        Assert.Equal(.7, first.Settings.PositionX);
        Assert.Equal(.8, first.Settings.PositionY);
        Assert.Equal(500, first.Settings.Width);
        var results = await Task.WhenAll(first.CreateOverlayAsync("Fenster A"), second.CreateOverlayAsync("Fenster B"));
        Assert.All(results, result => Assert.True(result.Succeeded, result.Error));
        Assert.True((await first.ReloadAsync()).Succeeded);
        Assert.Equal(3, first.Overlays.Count);
        Assert.Contains(first.Overlays, overlay => overlay.Name == "Fenster A");
        Assert.Contains(first.Overlays, overlay => overlay.Name == "Fenster B");
        Assert.True((await first.SelectOverlayAsync("default")).Succeeded);
        Assert.True((await first.SaveAsync(first.Settings with { Enabled = false, Interaction = "locked" })).Succeeded);
        Assert.False(first.Settings.Enabled);
        Assert.Equal("locked", first.Settings.Interaction);
    }

    [Fact]
    public async Task TemplatesPersistOnlyAppearanceAndApplyingThemPreservesWindowBehavior()
    {
        using var fixture = new Fixture();
        using var service = fixture.Create();
        var layout = new OverlaySettings
        {
            Width = 600, Height = 320, Scale = 1.5, BackgroundOpacity = .3, ShowBorder = false,
            SnapToGrid = false, AutoAlign = false, Enabled = true, PositionX = .8, PositionY = .9,
            Interaction = "locked", Visibility = "always", CaptureExcluded = false,
            Widgets = [OverlayLayout.ResizeWidget(OverlayCatalog.CreateWidget("preview-metric", 300, 100), 336, 144)],
        };
        Assert.True((await service.SaveTemplateAsync("  Meine Vorlage  ", layout)).Succeeded);
        var template = Assert.Single(service.Templates);
        var current = new OverlaySettings { Enabled = true, PositionX = .1, PositionY = .2, Interaction = "passthrough", Visibility = "session", CaptureExcluded = false };
        var applied = template.ApplyTo(current);
        Assert.Equal(600, applied.Width);
        Assert.Equal(1.5, applied.Scale);
        Assert.Equal(.3, applied.BackgroundOpacity);
        Assert.False(applied.ShowBorder);
        Assert.False(applied.SnapToGrid);
        Assert.False(applied.AutoAlign);
        Assert.Equal(current.PositionX, applied.PositionX);
        Assert.Equal(current.PositionY, applied.PositionY);
        Assert.Equal(current.Enabled, applied.Enabled);
        Assert.Equal(current.Interaction, applied.Interaction);
        Assert.Equal(current.Visibility, applied.Visibility);
        Assert.Equal(current.CaptureExcluded, applied.CaptureExcluded);
        using var document = JsonDocument.Parse(File.ReadAllBytes(fixture.TemplatePath));
        Assert.Equal(2, document.RootElement.GetProperty("Version").GetInt32());
        var storedLayout = document.RootElement.GetProperty("Templates")[0].GetProperty("Layout");
        Assert.False(storedLayout.TryGetProperty("Enabled", out _));
        Assert.False(storedLayout.TryGetProperty("PositionX", out _));
        Assert.False(storedLayout.TryGetProperty("Interaction", out _));
        Assert.False(storedLayout.TryGetProperty("CaptureExcluded", out _));
        using var reopened = fixture.Create();
        Assert.Equal(JsonSerializer.Serialize(template), JsonSerializer.Serialize(Assert.Single(reopened.Templates)));
        Assert.False((await service.SaveTemplateAsync("meine vorlage", layout)).Succeeded);
        Assert.Null(service.TemplateError);
        Assert.True((await service.SaveTemplateAsync("Geändert", layout with { Width = 700 }, template.Id)).Succeeded);
        Assert.Equal(template.Id, Assert.Single(service.Templates).Id);
        Assert.Equal(700, Assert.Single(service.Templates).Layout.Width);
        Assert.True((await service.DeleteTemplateAsync(template.Id)).Succeeded);
        Assert.Empty(service.Templates);
        Assert.False(File.Exists(fixture.LayoutPath));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"Version\":3,\"Overlays\":[]}")]
    [InlineData("{\"Version\":2}")]
    [InlineData("{\"Version\":2,\"SelectedOverlayId\":\"default\",\"Overlays\":[]}")]
    [InlineData("{\"Version\":2,\"SelectedOverlayId\":\"default\",\"Overlays\":[{\"Id\":\"default\",\"Name\":\"Overlay 1\",\"Settings\":{\"Widgets\":[{\"Id\":\"11111111111111111111111111111111\"}]}}]}")]
    [InlineData("{\"Version\":2,\"SelectedOverlayId\":\"default\",\"Overlays\":[{\"Id\":\"default\",\"Name\":\"Overlay 1\",\"Settings\":{\"Widgets\":[{\"Id\":\"11111111111111111111111111111111\",\"Kind\":\"unknown\"}]}}]}")]
    public async Task DamagedOrUnsupportedLayoutsRemainUntouchedAndLockedUntilExplicitReload(string content)
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Directory);
        File.WriteAllText(fixture.LayoutPath, content);
        var before = File.ReadAllBytes(fixture.LayoutPath);
        using var service = fixture.Create();
        Assert.NotNull(service.LoadError);
        await service.SetPreviewAsync(true);
        Assert.False((await service.SaveAsync(service.Settings with { Width = 640 })).Succeeded);
        Assert.Equal(before, File.ReadAllBytes(fixture.LayoutPath));
        File.Delete(fixture.LayoutPath);
        Assert.False((await service.CreateOverlayAsync("Kein stilles Entsperren")).Succeeded);
        Assert.True((await service.ReloadAsync()).Succeeded);
        Assert.False(File.Exists(fixture.LayoutPath));
        Assert.True((await service.RenameOverlayAsync("default", "Wieder geladen")).Succeeded);
        Assert.Null(service.LoadError);
    }

    [Fact]
    public async Task FailedAtomicSavePreservesCommittedSnapshotAndBlocksFurtherWritesUntilReload()
    {
        using var fixture = new Fixture();
        using var service = fixture.Create();
        Assert.True((await service.RenameOverlayAsync("default", "Gespeichert")).Succeeded);
        var before = File.ReadAllBytes(fixture.LayoutPath);
        using (var held = new FileStream(fixture.LayoutPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False((await service.RenameOverlayAsync("default", "Nicht gespeichert")).Succeeded);
            Assert.Equal("Gespeichert", Assert.Single(service.Overlays).Name);
            Assert.NotNull(service.LoadError);
            Assert.Equal(before, File.ReadAllBytes(fixture.LayoutPath));
            Assert.Empty(Directory.GetFiles(fixture.Directory, "*.tmp"));
        }
        Assert.False((await service.RenameOverlayAsync("default", "Weiterhin gesperrt")).Succeeded);
        Assert.Equal(before, File.ReadAllBytes(fixture.LayoutPath));
        Assert.True((await service.ReloadAsync()).Succeeded);
        Assert.True((await service.RenameOverlayAsync("default", "Danach gespeichert")).Succeeded);
    }

    [Fact]
    public async Task ExternalCorruptionIsDetectedBeforeAQueuedEditAndKeepsTheLastGoodSnapshot()
    {
        using var fixture = new Fixture();
        using var service = fixture.Create();
        Assert.True((await service.RenameOverlayAsync("default", "Gut")).Succeeded);
        File.WriteAllText(fixture.LayoutPath, "broken external change");
        Assert.False((await service.SaveAsync(service.Settings with { Width = 900 })).Succeeded);
        Assert.Equal(360, service.Settings.Width);
        Assert.Equal("Gut", Assert.Single(service.Overlays).Name);
        Assert.Equal("broken external change", File.ReadAllText(fixture.LayoutPath));
    }

    [Fact]
    public async Task BrokenTemplateFileDoesNotBlockIndependentLayoutsOrGetOverwritten()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Directory);
        File.WriteAllText(fixture.TemplatePath, "broken templates");
        using var service = fixture.Create();
        Assert.NotNull(service.TemplateError);
        Assert.Null(service.LoadError);
        Assert.True((await service.RenameOverlayAsync("default", "Unabhängig")).Succeeded);
        Assert.False((await service.SaveTemplateAsync("Neu", service.Settings)).Succeeded);
        Assert.Equal("broken templates", File.ReadAllText(fixture.TemplatePath));
        File.Delete(fixture.TemplatePath);
        Assert.True((await service.ReloadAsync()).Succeeded);
        Assert.True((await service.SaveTemplateAsync("Neu", service.Settings)).Succeeded);
    }

    [Fact]
    public async Task OversizedStoreIsReadOnlyAndTemplatesHaveTheOriginalThirtyEntryLimit()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Directory);
        using (var file = File.Create(fixture.LayoutPath)) file.SetLength(OverlayKitJson.MaximumFileBytes + 1);
        using var service = fixture.Create();
        Assert.NotNull(service.LoadError);
        Assert.False((await service.SaveAsync(service.Settings)).Succeeded);
        Assert.Equal(OverlayKitJson.MaximumFileBytes + 1, new FileInfo(fixture.LayoutPath).Length);
        for (var index = 0; index < 30; index++)
            Assert.True((await service.SaveTemplateAsync("Vorlage " + index, service.Settings)).Succeeded);
        Assert.False((await service.SaveTemplateAsync("Vorlage 31", service.Settings)).Succeeded);
        Assert.Equal(30, service.Templates.Count);
        Assert.Null(service.TemplateError);
    }

    [Theory]
    [InlineData("ItemView", "\"invalid\"")]
    [InlineData("ItemFilter", "\"trash\"")]
    [InlineData("ItemSort", "\"silver\"")]
    [InlineData("ItemLimit", "0")]
    [InlineData("ItemLimit", "25")]
    [InlineData("KillFeedRows", "0")]
    [InlineData("KillFeedRows", "11")]
    [InlineData("ItemSize", "31")]
    [InlineData("ItemSize", "113")]
    [InlineData("ItemKeys", "[null]")]
    [InlineData("ItemKeys", "[\"Item:123\",\"item:123\"]")]
    [InlineData("ItemKeys", "[\"bad\\nkey\"]")]
    public async Task CorruptContentOptionsAreReadOnlyAndNeverSilentlyRewritten(string property, string value)
    {
        using var fixture = new Fixture();
        var document = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new OverlayCollectionSettings
        {
            Overlays = [new OverlayInstance { Settings = new() { Widgets = [OverlayCatalog.CreateWidget("drop-grid")] } }],
        }))!;
        document["Overlays"]![0]!["Settings"]!["Widgets"]![0]![property] = System.Text.Json.Nodes.JsonNode.Parse(value);
        Directory.CreateDirectory(fixture.Directory);
        File.WriteAllText(fixture.LayoutPath, document.ToJsonString());
        var before = File.ReadAllBytes(fixture.LayoutPath);
        using var service = fixture.Create();
        Assert.NotNull(service.LoadError);
        Assert.False((await service.SaveAsync(new OverlaySettings())).Succeeded);
        Assert.Equal(before, File.ReadAllBytes(fixture.LayoutPath));
    }

    [Fact]
    public async Task DropOptionsAndStableSelectionSurviveTemplatesDuplicationAndRestartWithoutLiveFacts()
    {
        using var fixture = new Fixture();
        using var service = fixture.Create();
        var widget = OverlayCatalog.CreateWidget("drop-list") with
        {
            ItemView = "list", ItemFilter = "selected", ItemSort = "quantity", ItemSize = 84, ItemLimit = 12,
            ItemKeys = Array.AsReadOnly(new[] { "Item:930000123", "Item:930000456" }),
        };
        Assert.True((await service.SaveAsync(service.Settings with { Widgets = [widget] })).Succeeded);
        Assert.True((await service.SaveTemplateAsync("Auswahl", service.Settings)).Succeeded);
        Assert.True((await service.CreateOverlayAsync("Kopie", service.SelectedOverlayId)).Succeeded);
        using var restarted = fixture.Create();
        Assert.Null(restarted.LoadError);
        var saved = Assert.Single(restarted.Settings.Widgets);
        Assert.NotEqual(widget.Id, saved.Id);
        Assert.Equal(widget.ItemKeys, saved.ItemKeys);
        Assert.Equal(widget.ItemView, saved.ItemView);
        Assert.Equal(widget.ItemFilter, saved.ItemFilter);
        Assert.Equal(widget.ItemSort, saved.ItemSort);
        Assert.Equal(widget.ItemSize, saved.ItemSize);
        Assert.Equal(widget.ItemLimit, saved.ItemLimit);
        var template = Assert.Single(restarted.Templates);
        Assert.Equal(widget.ItemKeys, Assert.Single(template.Layout.Widgets).ItemKeys);
        var json = File.ReadAllText(fixture.LayoutPath);
        Assert.DoesNotContain("Quantity", json);
        Assert.DoesNotContain("SessionId", json);
        Assert.DoesNotContain("CapturedAt", json);
        Assert.DoesNotContain("NetKina", json);
    }

    [Fact]
    public async Task AbyssModuleOptionsSurviveDuplicationTemplatesAndRestartWithoutFeedFacts()
    {
        using var fixture = new Fixture();
        using var service = fixture.Create();
        var widget = OverlayCatalog.CreateWidget("abyss-killfeed") with { KillFeedRows = 8 };
        Assert.True((await service.SaveAsync(service.Settings with { Widgets = [widget, OverlayCatalog.CreateWidget("abyss-rank-progress")] })).Succeeded);
        Assert.True((await service.SaveTemplateAsync("Abyss", service.Settings)).Succeeded);
        Assert.True((await service.CreateOverlayAsync("Copy", service.SelectedOverlayId)).Succeeded);
        using var restarted = fixture.Create();
        Assert.Null(restarted.LoadError);
        Assert.Equal(8, restarted.Settings.Widgets.Single(item => item.Kind == "abyss-killfeed").KillFeedRows);
        Assert.Equal(8, restarted.Templates.Single().Layout.Widgets.Single(item => item.Kind == "abyss-killfeed").KillFeedRows);
        var json = File.ReadAllText(fixture.LayoutPath);
        Assert.DoesNotContain("Opponent", json);
        Assert.DoesNotContain("RankPoints", json);
        Assert.DoesNotContain("Generation", json);
        Assert.Equal(5, new OverlayWidget().KillFeedRows);
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "Soulcrest-overlay-kit-tests-" + Guid.NewGuid().ToString("N"));
        internal string Directory => Path.Combine(Root, "overlay-editor");
        internal string LayoutPath => Path.Combine(Directory, "overlay.json");
        internal string TemplatePath => Path.Combine(Directory, "overlay-templates.json");
        internal Fixture() => System.IO.Directory.CreateDirectory(Root);
        internal OverlayService Create() => new(Directory, new OverlayCollectionSettings());
        public void Dispose() => System.IO.Directory.Delete(Root, recursive: true);
    }
}
