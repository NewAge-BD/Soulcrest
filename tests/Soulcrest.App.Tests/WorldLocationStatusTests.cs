using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components;
using Soulcrest.App.Services;
using Soulcrest.Core.Instances;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class WorldLocationStatusTests
{
    private static WorldContextSnapshot Confirmed => new(WorldContextStatus.Confirmed, WorldContextKind.Instance,
        600001, 600001, "Party", "Normal", "Kraohöhle", "Krao Cave", 7,
        DateTimeOffset.Parse("2026-10-21T10:00:00Z"), "own-world")
        { CategoryNameDe = "Expeditionen", CategoryNameEn = "Expedition",
          ModeNameDe = "Eroberung", ModeNameEn = "Conquest" };

    [Theory]
    [InlineData("de", "Kraohöhle", "Expeditionen · Eroberung", "Bestätigt")]
    [InlineData("en", "Krao Cave", "Expedition · Conquest", "Confirmed")]
    public async Task ConfirmedLocationUsesOriginalNamesAndTranslatedContext(string language, string name, string detail, string status)
    {
        var previous = UiText.Language;
        try
        {
            UiText.Language = language;
            var markup = await Render(Confirmed);
            var html = WebUtility.HtmlDecode(markup);
            Assert.Contains(name, html);
            Assert.Contains(detail, html);
            Assert.Contains(status, html);
            Assert.Contains("data-world-status=\"confirmed\"", html);
            Assert.DoesNotContain("600001", html);
            await Export(markup, language, "confirmed");
        }
        finally { UiText.Language = previous; }
    }

    [Theory]
    [InlineData(WorldContextStatus.Unknown)]
    [InlineData(WorldContextStatus.Stale)]
    [InlineData(WorldContextStatus.Transitioning)]
    public async Task UnconfirmedStatesNeverDisplayPreviousLocationAsCurrent(WorldContextStatus state)
    {
        var markup = await Render(Confirmed with { Status = state });
        Assert.DoesNotContain("Krao", markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data-world-status=\"confirmed\"", markup);
        Assert.DoesNotContain("world-location-detail", markup);
    }

    [Fact]
    public async Task DisabledSourceCannotDisplayConfirmedNetworkLocation()
    {
        var markup = await Render(Confirmed, false);
        Assert.Contains("data-world-status=\"off\"", markup);
        Assert.DoesNotContain("Krao", markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("world-location-detail", markup);
    }

    [Fact]
    public void MissingOriginalTranslationFallsBackWithoutInventingNameOrMode()
    {
        var previous = UiText.Language;
        try
        {
            UiText.Language = "de";
            var display = WorldLocationPresentation.Build(Confirmed with
                { NameDe = null, Mode = "future-unknown-mode", ModeNameDe = null, ModeNameEn = null }, true);
            Assert.Equal("Krao Cave", display.Name);
            Assert.Equal("Expeditionen", display.Detail);
            Assert.DoesNotContain("future-unknown-mode", display.Detail);
        }
        finally { UiText.Language = previous; }
    }

    [Theory]
    [InlineData("de", "Chaotisches Äußeres Reshanta")]
    [InlineData("en", "Chaotic Outer Reshanta")]
    public void CurrentExactAbyssMapLabelSupersedesHistoricalName(string language, string currentName)
    {
        var previous = UiText.Language;
        try
        {
            UiText.Language = language;
            var snapshot = Confirmed with
            {
                MapDataId = 23, DungeonId = 24, Category = "Abyss", Mode = null,
                SoulcrestMapId = "chaotic-upper-reshanta", InstanceLayer = false,
                NameDe = "Chaotisches Oberes Reshanta", NameEn = "Chaotic Upper Reshanta"
            };
            var display = WorldLocationPresentation.Build(snapshot, true,
                id => id == "chaotic-upper-reshanta" ? currentName : null);
            Assert.Equal(currentName, display.Name);
            Assert.Equal("confirmed", display.State);
            Assert.Equal("Chaotic Upper Reshanta", snapshot.NameEn);
        }
        finally { UiText.Language = previous; }
    }

    [Fact]
    public void CurrentOpenWorldMapLabelUsesOnlyTheExactMappedIdentity()
    {
        var snapshot = Confirmed with
        {
            Kind = WorldContextKind.OpenWorld, DungeonId = null, Category = null, Mode = null,
            SoulcrestMapId = "synthetic-world", NameDe = "Historical world", NameEn = "Historical world"
        };
        Assert.Equal("Current world", WorldLocationPresentation.Build(snapshot, true,
            id => id == "synthetic-world" ? "Current world" : null).Name);
        Assert.Equal("Historical world", WorldLocationPresentation.Build(snapshot with { SoulcrestMapId = "other-world" }, true,
            id => id == "synthetic-world" ? "Current world" : null).Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void MissingCurrentMapLabelPreservesHistoricalAbyssName(string? packageName)
    {
        var snapshot = Confirmed with
        {
            Category = "Abyss", Mode = null, SoulcrestMapId = "synthetic-abyss",
            NameDe = "Historical Abyss", NameEn = "Historical Abyss"
        };
        Assert.Equal("Historical Abyss", WorldLocationPresentation.Build(snapshot, true, _ => packageName).Name);
    }

    [Theory]
    [InlineData("Party", false, null)]
    [InlineData("Abyss", true, null)]
    [InlineData("Abyss", null, null)]
    [InlineData("Abyss", false, "Normal")]
    public void CurrentBaseMapLabelCannotReplaceConcreteDungeonLayerOrMode(string category, bool? layer, string? mode)
    {
        var snapshot = Confirmed with
        {
            Category = category, Mode = mode, InstanceLayer = layer, SoulcrestMapId = "synthetic-world",
            NameDe = "Concrete dungeon", NameEn = "Concrete dungeon"
        };
        Assert.Equal("Concrete dungeon", WorldLocationPresentation.Build(snapshot, true, _ => "Base map").Name);
    }

    [Fact]
    public async Task NativeTextIsHtmlEncoded()
    {
        var markup = await Render(Confirmed with { NameDe = "<script>example</script>", NameEn = "<script>example</script>" });
        Assert.DoesNotContain("<script>", markup);
        Assert.Contains("&lt;script&gt;", markup);
    }

    [RequiresTheory(TestRequirement.PrivateInstanceCatalog)]
    [InlineData("de")]
    [InlineData("en")]
    public async Task BundledCatalogueRendersOriginalLocationAndCategory(string language)
    {
        var previous = UiText.Language;
        try
        {
            UiText.Language = language;
            var map = InstanceCatalog.Default.FindMap(600001);
            Assert.NotNull(map);
            Assert.Equal(InstanceMapKind.Instance, map.Kind);
            var world = new WorldContextService(InstanceCatalog.Default);
            var at = DateTimeOffset.Parse("2026-10-21T10:00:00Z");
            world.ObserveOwn(map.Id, 1, at);
            world.ObserveDungeon(new(600001, DungeonRunState.Process, 0, 0), 1, at);
            var markup = await Render(world.Snapshot);
            var html = WebUtility.HtmlDecode(markup);
            Assert.Contains(language == "de" ? "Kraohöhle" : "Krao Cave", html);
            Assert.Contains(language == "de" ? "Expeditionen" : "Expedition", html);
            Assert.Contains(language == "de" ? "Eroberung" : "Conquest", html);
            Assert.Contains("aria-label=\"" + UiText.T("Aufenthalt") + "\"", html);
            await Export(markup, language, "client-catalog");
        }
        finally { UiText.Language = previous; }
    }

    [Fact]
    public void ConfirmedUnmappedIdentifierRemainsUnclassified()
    {
        var previous = UiText.Language;
        try
        {
            UiText.Language = "en";
            var display = WorldLocationPresentation.Build(Confirmed with
            {
                Kind = WorldContextKind.Unknown, NameDe = null, NameEn = null,
                Category = null, Mode = null, DungeonId = null,
                CategoryNameDe = null, CategoryNameEn = null, ModeNameDe = null, ModeNameEn = null
            }, true);
            Assert.Equal("Area not identified yet", display.Name);
            Assert.Equal("Area type not identified yet", display.Detail);
            Assert.Equal("Confirmed", display.Status);
            Assert.DoesNotContain("Open world", display.Detail);
        }
        finally { UiText.Language = previous; }
    }

    private static async Task<string> Render(WorldContextSnapshot snapshot, bool enabled = true)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<WorldLocationStatus>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(WorldLocationStatus.Snapshot)] = snapshot,
                [nameof(WorldLocationStatus.Enabled)] = enabled
            }))).ToHtmlString());
    }

    private static async Task Export(string markup, string language, string state)
    {
        if (Environment.GetEnvironmentVariable("SOULCREST_WORLD_UI_PREVIEW") is not { Length: > 0 } folder) return;
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, $"world-location-{state}-{language}.html"),
            "<!doctype html><html lang=\"" + language + "\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><link rel=\"stylesheet\" href=\"/css/app.css\"></head><body><main style=\"width:520px;padding:20px\"><section class=\"card network-progress\">" + markup + "</section></main></body></html>");
    }
}
