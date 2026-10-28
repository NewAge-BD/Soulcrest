using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components;
using Soulcrest.App.Services;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Actual Razor output, isolated local tile placeholders, no app or capture service.</summary>
[Collection("Settings file")]
public sealed class LootTrackerRouteMapViewTests
{
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task ProductionComponentRendersLocalTileImagesAtUnchangedMapCoordinates(string language)
    {
        var directory = TemporaryDirectory();
        var previous = UiText.Language;
        try
        {
            UiText.Language = language;
            var session = new LootTrackerSession
            {
                RouteSegments = [new("sample-map", "Sample map", [new(1024, 1024), new(1280, 1152)])],
                ResourceNodes = [new("sample-node", "Example resource", "sample-map", "Sample map", 1200, 1100, 0, 1)],
            };
            var map = new LootTrackerRouteMapPreview.MapDefinition("sample-map", "Sample map", "sample-map/tiles/{z}_{x}_{y}.webp", 4096, 4, 512, 1, 6);
            File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(new { maps = new[]
            {
                new { id = map.Id, label = map.Label, tiles = map.Tiles, size = map.Size, refZoom = map.RefZoom,
                    tileSize = map.TileSize, minNativeZoom = map.MinNativeZoom, maxNativeZoom = map.MaxNativeZoom },
            } }));
            var preview = LootTrackerRouteMapPreview.Build(session, map.Id, map, _ => true);
            foreach (var tile in preview.Tiles)
            {
                var path = Path.Combine(directory, tile.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "isolated tile placeholder; HtmlRenderer does not decode images");
            }
            await using var provider = new ServiceCollection().AddLogging().BuildServiceProvider();
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            var html = await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var root = await renderer.RenderComponentAsync<LootTrackerRouteMap>(ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(LootTrackerRouteMap.Session)] = session,
                    [nameof(LootTrackerRouteMap.MapId)] = map.Id,
                    [nameof(LootTrackerRouteMap.MapDataDirectory)] = directory,
                }));
                return root.ToHtmlString();
            });
            Assert.Contains("data-map-background=\"offline\"", html);
            // Tiles keep their map coordinates and overlap their neighbours by one screen pixel against seams.
            Assert.Matches("href=\"https://mapdata.soulcrest/sample-map/tiles/5_4_4.webp\" x=\"1024\" y=\"1024\" width=\"256\\.\\d+\" height=\"256\\.\\d+\"", html);
            Assert.Contains("points=\"1024,1024 1280,1152\"", html);
            Assert.Contains("data-node-order=\"1\"", html);
            Assert.Contains("cx=\"1200\" cy=\"1100\"", html);
            Assert.Contains("aion2.gaming.tools", html);
            Assert.Contains(UiText.T("Kartenansicht · Unterbrechungen bleiben getrennt"), WebUtility.HtmlDecode(html));
            Assert.DoesNotContain(UiText.T("Offlinekarte nicht verfügbar · schematische Route"), WebUtility.HtmlDecode(html));
        }
        finally { UiText.Language = previous; Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task UnknownMapHasExplicitSchematicFallbackAndNoBackgroundImage(string language)
    {
        var previous = UiText.Language;
        try
        {
            UiText.Language = language;
            await using var provider = new ServiceCollection().AddLogging().BuildServiceProvider();
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            var session = new LootTrackerSession
            {
                RouteSegments = [new("unknown-map", "Unknown map", [new(100, 100), new(200, 200)])],
            };
            var html = await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var root = await renderer.RenderComponentAsync<LootTrackerRouteMap>(ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(LootTrackerRouteMap.Session)] = session,
                    [nameof(LootTrackerRouteMap.MapId)] = "unknown-map",
                }));
                return root.ToHtmlString();
            });
            Assert.Contains("data-map-background=\"schematic\"", html);
            Assert.Contains(UiText.T("Offlinekarte nicht verfügbar · schematische Route"), WebUtility.HtmlDecode(html));
            Assert.Contains("<polyline", html);
            Assert.DoesNotContain("<image", html);
            Assert.DoesNotContain("https://mapdata.soulcrest/", html);
        }
        finally { UiText.Language = previous; }
    }

    [Fact]
    public async Task AFirstFailedAttemptCanShowASingleFarmStopWithoutALine()
    {
        var previous = UiText.Language;
        try
        {
            UiText.Language = "en";
            await using var provider = new ServiceCollection().AddLogging().BuildServiceProvider();
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            var session = new LootTrackerSession
            {
                Kind = LootTrackerSessionKind.Lifeskill, FarmRouteVersion = 1, HarvestCount = 0, FailedHarvestCount = 1,
                FarmRoutePoints = [new("first-stop", 0, "sample-map", "Sample map", 100, 100, "Sample resource", 1, 0, 1)],
            };
            var html = await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var root = await renderer.RenderComponentAsync<LootTrackerRouteMap>(ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(LootTrackerRouteMap.Session)] = session,
                    [nameof(LootTrackerRouteMap.MapId)] = "sample-map",
                }));
                return root.ToHtmlString();
            });
            Assert.Contains("data-node-order=\"1\"", html);
            Assert.Contains("Farm route between gathering stops", html);
            Assert.DoesNotContain("lt-visual-empty", html);
            Assert.Contains("1 gathering stops", html);
            Assert.DoesNotContain("<polyline", html);
        }
        finally { UiText.Language = previous; }
    }

    [Fact]
    public async Task ReplayComponentShowsTheSynchronousPlayerAndHidesFutureMarkersAndStaleDot()
    {
        var directory = TemporaryDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "manifest.json"),
                "{\"maps\":[{\"id\":\"sample-map\",\"tiles\":\"sample-map/{z}_{x}_{y}.webp\",\"size\":4096,\"refZoom\":4,\"tileSize\":512,\"minNativeZoom\":1,\"maxNativeZoom\":6}]}");
            var at = new DateTimeOffset(2026, 10, 27, 10, 0, 0, TimeSpan.Zero);
            var session = new LootTrackerSession { Kind = LootTrackerSessionKind.Abyss,
                StartedAt = at, Elapsed = TimeSpan.FromSeconds(20),
                RouteSegments = [new("sample-map", "Sample map", [new(1000, 1000, at, TimeSpan.Zero),
                    new(1200, 1300, at.AddSeconds(10), TimeSpan.FromSeconds(10))])],
                CombatEvents = [new("future", LootTrackerCombatKind.Kill, at.AddSeconds(15), TimeSpan.FromSeconds(15),
                    new("sample-map", "Sample map", 1500, 1500, at.AddSeconds(15)))] };
            await using var provider = new ServiceCollection().AddLogging().BuildServiceProvider();
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            async Task<string> Render(int second) => await renderer.Dispatcher.InvokeAsync(async () =>
                (await renderer.RenderComponentAsync<LootTrackerRouteMap>(ParameterView.FromDictionary(new Dictionary<string, object?>
                { [nameof(LootTrackerRouteMap.Session)] = session,
                    [nameof(LootTrackerRouteMap.Replay)] = LootTrackerSessionReplay.Build(session, TimeSpan.FromSeconds(second)),
                    [nameof(LootTrackerRouteMap.MapDataDirectory)] = directory }))).ToHtmlString());
            var fresh = await Render(10);
            Assert.Contains("data-testid=\"replay-player\"", fresh);
            Assert.Contains("cx=\"1200\" cy=\"1300\"", fresh);
            Assert.Contains("data-map-id=\"sample-map\"", fresh);
            Assert.DoesNotContain("data-marker-kind=\"kill\"", fresh);
            Assert.DoesNotContain("data-testid=\"replay-player\"", await Render(13));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task ExportWholeProductionViewWithRealOfflineMapOnlyWhenRequested(string language)
    {
        if (Environment.GetEnvironmentVariable("SOULCREST_LOOT_UI_PREVIEW") is not { Length: > 0 } output) return;
        var directory = TemporaryDirectory();
        var previous = UiText.Language;
        var settingsBackup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        try
        {
            // Map files alone are read from the product cache. All metadata and progress paths are isolated/synthetic.
            var mapDirectory = AppPaths.FindMapData();
            Assert.NotNull(mapDirectory);
            var maps = LootTrackerRouteMapPreview.Load(mapDirectory);
            Assert.True(maps.ContainsKey("altgard"), "The explicitly requested preview needs the active Altgard offline package.");
            var progress = new ProgressService(Path.Combine(directory, "progress.json"), new PetCatalog([]));
            await using var provider = new ServiceCollection().AddLogging().AddSingleton<SettingsService>().AddSingleton(progress)
                .AddSingleton(new AlertsService(Path.Combine(directory, "alerts.json"), runTimer: false))
                .AddSingleton<AlertSoundPlayer>()
                .AddSingleton(new RareDropAlertService(() => new(), () => throw new InvalidOperationException("Rendering cannot play audio."), TimeProvider.System))
                .BuildServiceProvider();
            var settings = provider.GetRequiredService<SettingsService>();
            settings.Current.UiLanguage = settings.Current.NameLanguage = language;
            UiText.Language = language;
            var session = LootTrackerViewTests.ExampleSnapshot().Sessions.Single(session => session.Kind == LootTrackerSessionKind.Lifeskill) with
            {
                FarmRouteVersion = 1, RouteSegments = [], ResourceNodes = [],
                FarmRoutePoints =
                [
                    new("sample-stop-1", 0, "altgard", "Altgard", 4210, 3695, "Sapphire", 2, 1, 1, "sample-node-1", 11),
                    new("sample-stop-2", 0, "altgard", "Altgard", 4330, 3765, "Orichalcum", 2, 2, 0),
                    new("sample-stop-3", 0, "altgard", "Altgard", 4470, 3845, "Odyle", 1, 1, 0, "sample-node-3", 13),
                    new("sample-stop-4", 0, "altgard", "Altgard", 4455, 3685, "Sapphire", 1, 0, 1),
                ],
            };
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            var html = await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var root = await renderer.RenderComponentAsync<LootTrackerView>(ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(LootTrackerView.Snapshot)] = new LootTrackerSnapshot([session, session with
                    {
                        Id = "sample-lifeskill-earlier", StartedAt = session.StartedAt.AddHours(-2),
                        EndedAt = session.EndedAt?.AddHours(-2), Location = "Verteron",
                        FarmRoutePoints = [],
                    }], session.EndedAt!.Value),
                    [nameof(LootTrackerView.IsExample)] = true,
                }));
                return root.ToHtmlString();
            });
            Assert.Contains("data-map-background=\"offline\"", html);
            Assert.Contains("https://mapdata.soulcrest/altgard/", html);
            Assert.Contains("data-farm-stop-id=\"sample-stop-4\"", html);
            Assert.Contains("data-delete-session-id=", html);
            Directory.CreateDirectory(output);
            var cssDirectory = Path.Combine(TestPrerequisites.RepositoryRoot, "src", "Soulcrest.App", "wwwroot", "css");
            var css = await File.ReadAllTextAsync(Path.Combine(cssDirectory, "app.css")) + "\n" +
                await File.ReadAllTextAsync(Path.Combine(cssDirectory, "loot-tracker.css"));
            var document = "<!doctype html><html lang=\"" + language + "\"><head><meta charset=\"utf-8\">" +
                "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><style>" + css +
                "</style></head><body>" + html + "</body></html>";
            await File.WriteAllTextAsync(Path.Combine(output, $"loot-tracker-offline-map-{language}.html"), document);
            var pve = LootTrackerViewTests.ExampleSnapshot().Sessions[0] with
            {
                Location = language == "de" ? "Insel der Unsterblichkeit" : "Immortal Isle",
                LocationApproximate = true, KinaBound = null, KinaUnbound = 184_720,
                Mobs = [], MonsterCount = null,
                RouteSegments = [new("altgard", "Altgard", [
                    new(1910, 6405), new(1965, 6370), new(2030, 6410), new(2070, 6490),
                    new(2040, 6550), new(1960, 6570), new(1870, 6520), new(1845, 6460), new(1910, 6405)])],
            };
            var pveHtml = await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var root = await renderer.RenderComponentAsync<LootTrackerView>(ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(LootTrackerView.Snapshot)] = new LootTrackerSnapshot([pve], pve.LastLootAt),
                    [nameof(LootTrackerView.IsExample)] = true,
                }));
                return root.ToHtmlString();
            });
            Assert.Contains("data-map-background=\"offline\"", pveHtml);
            Assert.Contains("id=\"loot-show-route-on-map\"", pveHtml);
            await File.WriteAllTextAsync(Path.Combine(output, $"loot-tracker-pve-map-{language}.html"), document.Replace(html, pveHtml, StringComparison.Ordinal));
        }
        finally
        {
            if (settingsBackup is null) File.Delete(AppPaths.SettingsFile);
            else File.WriteAllBytes(AppPaths.SettingsFile, settingsBackup);
            UiText.Language = previous;
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string TemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "soulcrest-route-map-view-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
