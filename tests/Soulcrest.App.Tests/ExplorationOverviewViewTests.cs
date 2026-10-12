using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Capture;
using Soulcrest.App.Components;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class ExplorationOverviewViewTests
{
    [Theory]
    [InlineData("de", "Gesamtübersicht", "Erkundungs-Checkliste", "Manuell fixiert", "Spielkarte folgen")]
    [InlineData("en", "Overview", "Exploration checklist", "Manually pinned", "Follow game map")]
    public async Task PinnedOverviewShowsPassiveStatusAboveSavedCompletionAndTheLockedChecklist(string language, string overview, string checklist, string source, string follow)
    {
        await using var fixture = new Fixture(language);
        fixture.Settings.Current.ExplorationPinnedMap = "altgard";
        var html = await fixture.Render();
        Assert.Contains(overview, html);
        Assert.Contains(checklist, html);
        Assert.Contains(source, html);
        Assert.Contains(follow, html);
        Assert.Contains("<progress value=\"1\" max=\"3\"", html);
        Assert.Contains("data-place=\"rift\"", html);
        Assert.Contains(language == "de" ? "Bestätigt, nicht erledigt: 0" : "Confirmed, incomplete: 0", html);
        Assert.Contains(language == "de" ? "Unbestätigt: 4" : "Unconfirmed: 4", html);
        Assert.Contains("exploration-place locked", html);
        Assert.Contains("Twisted Pit Entrance", html);
        Assert.Contains("class=\"exploration-rows\" tabindex=\"0\"", html);
        AssertNetworkAboveOverview(html);
        Assert.DoesNotContain(language == "de" ? "Scan starten" : "Start scan", html);
        Assert.False(fixture.Tracking.Running);
        Assert.Equal(new[] { "lost" }, fixture.Exploration.Completed);
        await fixture.Export(html, "pinned");
    }

    [Theory]
    [InlineData("de", "Automatisch erkannt · Spielkarte", "Auf Karte anzeigen")]
    [InlineData("en", "Detected automatically · player map", "Show on map")]
    public async Task FreshDetectionChangesTheOverviewAndShowsTheNearestOpenPlace(string language, string source, string focus)
    {
        await using var fixture = new Fixture(language);
        fixture.Settings.Current.ExplorationPinnedMap = null;
        fixture.Detect("verteron");
        var html = await fixture.Render();
        Assert.Contains(source, html);
        Assert.Contains("Verteron open place", html);
        Assert.Contains("data-place=\"verteron-open\"", html);
        Assert.DoesNotContain("data-place=\"rift\"", html);
        Assert.Contains(focus, html);
        Assert.Contains("class=\"btn primary\"", html);
        Assert.Equal(new[] { "lost" }, fixture.Exploration.Completed);
        await fixture.Export(html, "detected");
    }

    [Theory]
    [InlineData("de", "Unentdeckt", "Entdeckt · offen", "Entdeckt · erledigt", "Unbestätigt")]
    [InlineData("en", "Undiscovered", "Discovered · incomplete", "Discovered · completed", "Unconfirmed")]
    public async Task ThreeExplorationStatesRemainSeparateFromUnconfirmedData(string language, string undiscovered, string incomplete, string complete, string unknown)
    {
        await using var fixture = new Fixture(language);
        fixture.Settings.Current.ExplorationPinnedMap = "altgard";
        fixture.Exploration.SetState(fixture.Exploration.ActiveId, ["twisted"], ExplorationProgressState.DiscoveredIncomplete);
        fixture.Exploration.SetState(fixture.Exploration.ActiveId, ["rift"], ExplorationProgressState.Undiscovered);
        var html = await fixture.Render();
        Assert.Contains(undiscovered, html);
        Assert.Contains(incomplete, html);
        Assert.Contains(complete, html);
        Assert.Contains(unknown, html);
        Assert.Contains("value=\"DiscoveredIncomplete\"", html);
        Assert.Contains("value=\"Undiscovered\"", html);
        AssertNetworkAboveOverview(html);
        Assert.DoesNotContain("data-scan-state", html);
        Assert.False(fixture.Tracking.Running);
        Assert.Equal(new[] { "lost" }, fixture.Exploration.Completed);
    }

    [Theory]
    [InlineData("de", "Manuell", "Gespeicherter Fortschritt", "Unbestätigt")]
    [InlineData("en", "Manual", "Saved progress", "Unconfirmed")]
    public async Task CompactWorkspaceKeepsOneEditableRowPerPlaceAndDistinctSavedAndUnknownStates(
        string language, string manual, string saved, string unknown)
    {
        await using var fixture = new Fixture(language);
        fixture.Settings.Current.ExplorationPinnedMap = "altgard";
        fixture.Exploration.SetState(fixture.Exploration.ActiveId, ["twisted"], ExplorationProgressState.DiscoveredIncomplete);
        fixture.Exploration.SetState(fixture.Exploration.ActiveId, ["rift"], ExplorationProgressState.Undiscovered);
        fixture.Detect("altgard");
        var html = await fixture.Render();
        Assert.Contains("class=\"exploration-workspace\"", html);
        Assert.Contains("class=\"exploration-sidebar\"", html);
        Assert.Contains("<details class=\"exploration-status-details\">", html); // Details start collapsed.
        AssertNetworkAboveOverview(html);
        foreach (var id in new[] { "lost", "twisted", "rift", "stronghold", "kibelisk" })
            Assert.Single(Regex.Matches(html, "data-place=\"" + id + "\""));
        var incomplete = Regex.Match(html, "<li[^>]*data-place=\"twisted\".*?</li>", RegexOptions.Singleline).Value;
        Assert.Contains("exploration-state-badge discovered", incomplete);
        Assert.Contains(manual, incomplete);
        Assert.Contains(saved, incomplete);
        Assert.Contains("value=\"DiscoveredIncomplete\"", incomplete);
        var unconfirmed = Regex.Match(html, "<li[^>]*data-place=\"stronghold\".*?</li>", RegexOptions.Singleline).Value;
        Assert.Contains("exploration-state-badge unknown", unconfirmed);
        Assert.Contains(unknown, unconfirmed);
        Assert.DoesNotContain("exploration-state-badge undiscovered", unconfirmed);
        Assert.Contains("class=\"exploration-kibelisk-toggle\"", html);
        Assert.Equal(new[] { "lost" }, fixture.Exploration.Completed);
        await fixture.Export(html, "workspace");
    }

    private static void AssertNetworkAboveOverview(string html)
    {
        Assert.Contains("class=\"exploration-network-setup\"", html);
        Assert.Single(Regex.Matches(html, "data-progress-source="));
        var context = html.IndexOf("class=\"card exploration-context\"", StringComparison.Ordinal);
        var network = html.IndexOf("data-progress-source=", StringComparison.Ordinal);
        var overview = html.IndexOf("class=\"exploration-summary\"", StringComparison.Ordinal);
        var checklist = html.IndexOf("class=\"card exploration-checklist\"", StringComparison.Ordinal);
        Assert.True(context < network && network < overview && overview < checklist);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly string _language;
        private readonly string _profile = Path.Combine(AppPaths.DataDirectory, "exploration-overview-view-" + Guid.NewGuid().ToString("N") + ".json");
        private readonly (string Ui, string Names, string Map, string? Pin) _previous;
        private readonly string _previousLanguage;
        private readonly string? _previousMapData = Environment.GetEnvironmentVariable("SOULCREST_MAPDATA");
        private readonly string _mapData = Path.Combine(AppPaths.DataDirectory, "overview-mapdata-" + Guid.NewGuid().ToString("N"));
        public SettingsService Settings { get; }
        public ExplorationService Exploration { get; }
        public MapTrackingService Tracking => _provider.GetRequiredService<MapTrackingService>();

        public Fixture(string language)
        {
            _language = language;
            _previousLanguage = UiText.Language;
            Directory.CreateDirectory(_mapData);
            File.WriteAllText(Path.Combine(_mapData, "manifest.json"), """{"maps":[{"id":"altgard","label":"Altgard","group":"Asmodian"},{"id":"verteron","label":"Verteron","group":"Elyos"}]}""");
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _mapData);
            Exploration = new ExplorationService(_profile,
            [new("lost", "altgard", "dungeon", 0, "Lost Ruin Entrance", null, 100, 100),
             new("twisted", "altgard", "dungeon", 1, "Twisted Pit Entrance", null, 200, 100),
             new("rift", "altgard", "dungeon", 2, "Rift Fissure Entrance", null, 300, 100),
             new("stronghold", "altgard", "stronghold", 3, "Synthetic stronghold", null, 400, 100),
             new("kibelisk", "altgard", "kibelisk", 4, "Synthetic Kibelisk", null, 500, 100),
             new("verteron-open", "verteron", "stronghold", 0, "Verteron open place", null, 110, 100)]);
            Exploration.SetDone(Exploration.ActiveId, ["lost"]);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<SettingsService>();
            services.AddSingleton<GameCaptureService>();
            services.AddSingleton<ProgressService>();
            services.AddSingleton<MapTrackingService>();
            services.AddSingleton<NetworkLootService>();
            services.AddSingleton(Exploration);
            services.AddSingleton<CharacterDetectionService>();
            services.AddSingleton<UiState>();
            _provider = services.BuildServiceProvider();
            Settings = _provider.GetRequiredService<SettingsService>();
            _previous = (Settings.Current.UiLanguage, Settings.Current.NameLanguage, Settings.Current.LastMap, Settings.Current.ExplorationPinnedMap);
            Settings.Current.UiLanguage = Settings.Current.NameLanguage = UiText.Language = language;
            Settings.Current.LastMap = "altgard";
        }
        public void Detect(string map)
        {
            typeof(MapTrackingService).GetProperty(nameof(MapTrackingService.Running))!.SetValue(Tracking, true);
            typeof(MapTrackingService).GetProperty(nameof(MapTrackingService.Found))!.SetValue(Tracking, true);
            var at = DateTimeOffset.UtcNow;
            typeof(MapTrackingService).GetProperty(nameof(MapTrackingService.Position))!.SetValue(Tracking, new PlayerPosition(map, 100, 100, at));
            typeof(MapTrackingService).GetProperty(nameof(MapTrackingService.DetectedMap))!.SetValue(Tracking, new DetectedMapContext(map, false, at));
        }
        public async Task<string> Render()
        {
            await using var renderer = new HtmlRenderer(_provider, _provider.GetRequiredService<ILoggerFactory>());
            return WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<ExplorationView>()).ToHtmlString()));
        }
        public async Task Export(string html, string state)
        {
            var output = Environment.GetEnvironmentVariable("SOULCREST_EXPLORATION_UI_PREVIEW")
                ?? Environment.GetEnvironmentVariable("SOULCREST_SCAN_UI_PREVIEW");
            if (string.IsNullOrWhiteSpace(output)) return;
            Directory.CreateDirectory(output);
            foreach (var (width, height) in new[] { (1600, 900), (1280, 720) })
            {
                var name = $"exploration-{state}-{_language}-{width}x{height}.html";
                var document = "<!doctype html><html lang=\"" + _language + "\"><head><meta charset=\"utf-8\">"
                    + "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
                    + "<link rel=\"stylesheet\" href=\"/css/app.css\"><link rel=\"stylesheet\" href=\"/css/exploration.css\">"
                    + "</head><body data-preview-width=\"" + width + "\" data-preview-height=\"" + height + "\">"
                    + "<div class=\"shell\"><header class=\"topbar\"><div class=\"brand\">Soulcrest</div><nav class=\"tabs\">"
                    + "<button>" + UiText.T("Karte") + "</button><button>Pets</button><button class=\"active\">"
                    + UiText.T("Erkundungsfortschritt") + "</button><button>Boss Rush</button><button>Alerts</button></nav></header>"
                    + "<main class=\"content\">" + html + "</main></div></body></html>";
                await File.WriteAllTextAsync(Path.Combine(output, name), document);
            }
        }
        public async ValueTask DisposeAsync()
        {
            typeof(MapTrackingService).GetProperty(nameof(MapTrackingService.Running))!.SetValue(Tracking, false);
            Settings.Current.UiLanguage = _previous.Ui;
            Settings.Current.NameLanguage = _previous.Names;
            Settings.Current.LastMap = _previous.Map;
            Settings.Current.ExplorationPinnedMap = _previous.Pin;
            UiText.Language = _previousLanguage;
            await _provider.DisposeAsync();
            File.Delete(_profile);
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _previousMapData);
            File.Delete(Path.Combine(_mapData, "manifest.json"));
            Directory.Delete(_mapData);
        }
    }
}
