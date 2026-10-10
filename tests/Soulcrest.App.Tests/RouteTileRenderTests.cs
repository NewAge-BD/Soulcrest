using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Soulcrest.App.Capture;
using Soulcrest.App.Components;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>The route workspace keeps character progress, the visible itinerary and the route library distinct.</summary>
[Collection("Settings file")] // writes targets.json, routes.soulroute and settings.json of the test data folder
public sealed class RouteTileRenderTests
{
    [Fact]
    public async Task RouteTileShowsSaveButtonAndSavedRoutes()
    {
        File.Delete(AppPaths.TargetsFile);
        File.Delete(AppPaths.RoutesFile);
        var progressFile = Path.Combine(AppPaths.DataDirectory, "leveling-progress.json");
        var previousProgress = File.Exists(progressFile) ? File.ReadAllBytes(progressFile) : null;
        File.Delete(progressFile);
        var characterFile = Path.Combine(AppPaths.DataDirectory, "route-tile-characters.json");
        File.Delete(characterFile);
        RouteFile.Save(AppPaths.RoutesFile, new[] { new SavedRoute("leveling", "Asmodian · Episode 2 · Level 10–16",
            Enumerable.Range(1, 10).Select(i => new RouteStop("altgard", i * 100, 200,
                i == 5 ? "Finding Nemon" : i == 6 ? "" : $"Quest {i}",
                i == 4 ? "Kibelisk" : i == 6 ? "Sealed Dungeon" : i == 7 ? "NPCs · Regional Quest" : "NPCs · Hero Quest", null)).ToList(),
            Category: LevelingRouteStyle.Category),
            new SavedRoute("elyos", "Elyos · Episode 2 · Level 10–16",
                [new("verteron", 100, 100, "Quest A", "NPCs · Hero Quest", null),
                 new("verteron", 200, 200, "Quest B", "NPCs · Hero Quest", null)], Category: LevelingRouteStyle.Category),
            new SavedRoute("unassigned", "Custom leveling practice",
                [new("altgard", 50, 50, "Practice", "Locations", null)], Category: LevelingRouteStyle.Category) });
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<GameCaptureService>();
        services.AddSingleton<ProgressService>();
        services.AddSingleton<NetworkLootService>();
        services.AddSingleton<TrackerService>();
        services.AddSingleton<MapTrackingService>();
        services.AddSingleton<MapTargetsService>();
        services.AddSingleton<BossRushService>();
        services.AddSingleton(new ExplorationService(characterFile, []));
        services.AddSingleton<ExplorationScanService>();
        services.AddSingleton<UiState>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<IJSRuntime, NoJs>();
        await using var provider = services.BuildServiceProvider();
        var settings = provider.GetRequiredService<SettingsService>();
        var previous = (settings.Current.UiLanguage, settings.Current.LastMap);
        settings.Update(s => { s.UiLanguage = "de"; s.LastMap = "altgard"; });
        UiText.Language = "de";
        var targets = provider.GetRequiredService<MapTargetsService>();
        var exploration = provider.GetRequiredService<ExplorationService>();
        exploration.Rename("Route Tester A");
        var firstCharacter = exploration.ActiveId;
        try
        {
            targets.Toggle("altgard", 100, 100, "A", "Locations", null);
            targets.Toggle("altgard", 200, 200, "B", "Locations", null, chain: true);
            var before = await Render(provider);
            Assert.Contains("Folge speichern (2)", before);

            var route = targets.SaveRoute("altgard", "Runde")!;
            targets.SetRouteRepeat(route.Id, true);
            var after = await Render(provider);
            Assert.Contains("Runde", after);
            Assert.Contains("2 Ziele", after);
            Assert.Contains("läuft", after);
            Assert.Contains("route-repeat on", after);
            Assert.Contains("route-character-profile", after);
            Assert.Contains("Routen suchen", after);
            Assert.Contains("route-card", after);
            Assert.Equal(2, Regex.Matches(after, "class=\"route-faction-group\"").Count);
            Assert.DoesNotContain("open", FactionTag(after, "Asmodian"));
            Assert.DoesNotContain("open", FactionTag(after, "Elyos"));
            Assert.Contains("Weitere Leveling-Routen", after);
            Assert.Contains("Custom leveling practice", after);

            targets.StartRoute("leveling");
            for (var i = 0; i < 4; i++) targets.AdvanceRoute("leveling");
            var leveling = await Render(provider);
            Assert.Contains("Leveling Routes", leveling);
            Assert.Contains("Eigene Routen", leveling);
            Assert.Contains("objective-colors", leveling);
            Assert.Contains("Nächstes Ziel", leveling);
            Assert.Contains("Vorheriges Ziel", leveling);
            Assert.Contains("Station 5 / 10", leveling);
            Assert.Contains("leveling-navigation", leveling);
            Assert.Contains("Finding Nemon", leveling);
            Assert.Contains("Station 6", leveling); // unnamed map waypoints keep a useful list position
            Assert.Contains("aria-valuenow=\"4\"", leveling);
            Assert.Contains("aria-current=\"step\"", leveling);
            Assert.Contains("Fortschritt für diesen Charakter gespeichert", leveling);
            Assert.Contains("class=\"btn route-cancel\"", leveling);
            Assert.Contains("Route abbrechen", leveling);
            Assert.Contains("Route beenden und Fortschritt behalten", leveling);
            Assert.Contains("Leveling-Overlay", leveling);
            Assert.Contains("Größe des Leveling-Overlays", leveling);
            Assert.Contains("open", FactionTag(leveling, "Asmodian"));
            Assert.DoesNotContain("open", FactionTag(leveling, "Elyos"));
            Assert.DoesNotContain("name=", FactionTag(leveling, "Asmodian")); // Native details remain independent.
            Assert.DoesNotContain("name=", FactionTag(leveling, "Elyos"));
            Assert.Contains("<span class=\"route-faction-percent\">40 %</span>", leveling);
            Assert.Contains("Am Ende startet die nächste Episode automatisch.", leveling);
            Assert.DoesNotContain("class=\"route-repeat-option\"", leveling);
            Assert.Equal(2, Regex.Matches(leveling, "class=\"btn small route-repeat ").Count); // Ordinary and custom leveling routes retain Repeat.
            Assert.Contains("Erneut klicken, um Klicks durchzulassen.", leveling);
            Assert.DoesNotContain("Auf der Weltkarte im Spiel: Alt + Rechtsklick", leveling);
            Assert.Contains("Schloss im Overlay anklicken", leveling);
            Assert.DoesNotContain("Strg+Alt+L", leveling);
            Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(leveling, "class=\"route-stop completed ").Count);
            Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(leveling, "class=\"route-stop upcoming ").Count);
            Assert.Contains("A <small>", leveling); // manually marked targets are retained beside the itinerary
            Assert.Contains("B <small>", leveling);
            WritePreview("routes.html", leveling);
            var matchingFaction = await Render(provider, "Elyos");
            Assert.Contains("open", FactionTag(matchingFaction, "Elyos"));
            Assert.DoesNotContain("data-faction=\"Asmodian\"", matchingFaction);
            Assert.DoesNotContain("Weitere Leveling-Routen", matchingFaction);
            var matchingBoth = await Render(provider, "Level");
            Assert.Contains("open", FactionTag(matchingBoth, "Asmodian"));
            Assert.Contains("open", FactionTag(matchingBoth, "Elyos"));
            WritePreview("routes-search.html", matchingBoth);
            UiText.Language = "en";
            var english = await Render(provider);
            Assert.Contains("Progress saved for this character", english);
            Assert.Contains("Stop 5 / 10", english);
            Assert.Contains("Back", english);
            Assert.Contains("Cancel route", english);
            Assert.Contains("Leveling overlay", english);
            Assert.Contains("Click the overlay lock to interact and move it.", english);
            Assert.DoesNotContain("On the in-game world map: Alt + right-click", english);
            Assert.Contains("Other leveling routes", english);
            Assert.Contains("The next episode starts automatically at the end.", english);
            Assert.Equal(2, Regex.Matches(english, "class=\"route-faction-group\"").Count);
            WritePreview("routes-en.html", english);
            UiText.Language = "de";

            targets.StopRoute("leveling");
            var inactive = await Render(provider);
            Assert.DoesNotContain("class=\"route-active\"", inactive);
            Assert.DoesNotContain("class=\"btn route-cancel\"", inactive);
            Assert.Contains("A <small>", inactive);
            Assert.Contains("4 / 10 erledigt", inactive);
            Assert.Contains("Fortsetzen", inactive);
            WritePreview("routes-library.html", inactive);
            targets.StartRoute("leveling");
            Assert.Contains("Station 5 / 10", await Render(provider));

            exploration.Add("Route Tester B");
            targets.StartRoute("leveling");
            var otherCharacter = await Render(provider);
            Assert.Contains("Station 1 / 10", otherCharacter);
            Assert.Contains("aria-valuenow=\"0\"", otherCharacter);
            exploration.Select(firstCharacter);
            Assert.Contains("Station 5 / 10", await Render(provider));
            targets.RewindRoute("leveling");
            var rewound = await Render(provider);
            Assert.Contains("Station 4 / 10", rewound);
            for (var i = 0; i < 10; i++) targets.AdvanceRoute("leveling");
            var finished = await Render(provider);
            Assert.Contains("Route abgeschlossen", finished);
            Assert.Contains("Vorheriges Ziel", finished);
        }
        finally
        {
            settings.Update(s => { s.UiLanguage = previous.UiLanguage; s.LastMap = previous.LastMap; });
            UiText.Language = previous.UiLanguage;
            File.Delete(AppPaths.TargetsFile);
            File.Delete(AppPaths.RoutesFile);
            File.Delete(characterFile);
            if (previousProgress is null) File.Delete(progressFile);
            else File.WriteAllBytes(progressFile, previousProgress);
        }
    }

    private static void WritePreview(string filename, string markup)
    {
        if (Environment.GetEnvironmentVariable("SOULCREST_ROUTE_UI_PREVIEW") is not { Length: > 0 } output) return;
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, filename), "<!doctype html><html><head><meta charset=\"utf-8\"><link rel=\"stylesheet\" href=\"/css/app.css\"></head><body>" + markup + "</body></html>");
    }

    private static string FactionTag(string html, string faction)
    {
        var match = Regex.Match(html, "<details\\b[^>]*class=\"route-faction-group\"[^>]*data-faction=\"" + faction + "\"[^>]*>");
        Assert.True(match.Success, $"Missing faction group {faction}.");
        return match.Value;
    }

    private static async Task<string> Render(ServiceProvider provider, string? query = null)
    {
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            if (query is null) return (await renderer.RenderComponentAsync<MapView>()).ToHtmlString();
            MapView? view = null;
            var root = await renderer.RenderComponentAsync<RouteQueryHost>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(RouteQueryHost.Capture)] = (Action<MapView>)(value => view = value) }));
            Assert.NotNull(view);
            typeof(MapView).GetField("_routeQuery", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, query);
            await view!.SetParametersAsync(ParameterView.Empty);
            return root.ToHtmlString();
        });
        return System.Net.WebUtility.HtmlDecode(html);
    }

    private sealed class RouteQueryHost : ComponentBase
    {
        [Parameter] public Action<MapView> Capture { get; set; } = default!;
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<MapView>(0);
            builder.AddComponentReferenceCapture(1, value => Capture((MapView)value));
            builder.CloseComponent();
        }
    }

    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }
}
