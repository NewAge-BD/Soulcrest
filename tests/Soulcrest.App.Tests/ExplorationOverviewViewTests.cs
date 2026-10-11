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
    public async Task PinnedOverviewShowsSavedCompletionAndLockedChecklistBeforeTheOptionalScan(string language, string overview, string checklist, string source, string follow)
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
        Assert.Contains("exploration-place locked", html);
        Assert.Contains("Twisted Pit Entrance", html);
        Assert.Contains("class=\"exploration-rows\" tabindex=\"0\"", html);
        Assert.Contains("<details class=\"exploration-scan-drawer\"><summary>", html);
        Assert.True(html.IndexOf("exploration-checklist\"", StringComparison.Ordinal) < html.IndexOf("exploration-scan-drawer\"", StringComparison.Ordinal));
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
    [InlineData("de", "Karte des laufenden Scans", "Manuelle Änderungen sind während des Scans gesperrt.")]
    [InlineData("en", "Map locked by the running scan", "Manual changes are locked during a scan.")]
    public async Task RunningScanKeepsItsMapLocksCorrectionsAndExpandsTheScanDrawer(string language, string source, string locked)
    {
        await using var fixture = new Fixture(language);
        fixture.Settings.Current.ExplorationPinnedMap = null;
        fixture.Detect("verteron");
        fixture.SetScan("ScanMap", "altgard");
        fixture.SetScan("ScanKind", "all");
        fixture.SetScan("Status", "Liste wird gelesen – kurz stillhalten.");
        fixture.SetRunning(true);
        var html = await fixture.Render();
        Assert.Contains(source, html);
        Assert.Contains(locked, html);
        Assert.Contains("data-place=\"rift\"", html);
        Assert.DoesNotContain("data-place=\"verteron-open\"", html);
        Assert.Matches("<details class=\"exploration-scan-drawer\" open(?:>|[ =])", html);
        Assert.Contains("data-scan-state=\"running\"", html);
        var checkboxes = Regex.Matches(html, "<input type=\"checkbox\"[^>]*>").Select(match => match.Value).ToArray();
        Assert.NotEmpty(checkboxes);
        Assert.All(checkboxes, checkbox => Assert.Contains("disabled", checkbox));
        Assert.Equal(new[] { "lost" }, fixture.Exploration.Completed);
        await fixture.Export(html, "scanning");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly string _language;
        private readonly string _profile = Path.Combine(AppPaths.DataDirectory, "exploration-overview-view-" + Guid.NewGuid().ToString("N") + ".json");
        private readonly (string Ui, string Names, string Map, string? Pin) _previous;
        private readonly string _previousLanguage;
        public SettingsService Settings { get; }
        public ExplorationService Exploration { get; }
        public MapTrackingService Tracking => _provider.GetRequiredService<MapTrackingService>();
        private ExplorationScanService Scan => _provider.GetRequiredService<ExplorationScanService>();

        public Fixture(string language)
        {
            _language = language;
            _previousLanguage = UiText.Language;
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
            services.AddSingleton<ExplorationScanService>();
            services.AddSingleton<CharacterDetectionService>();
            services.AddSingleton<OcrLanguageInstaller>();
            services.AddSingleton<UiState>();
            _provider = services.BuildServiceProvider();
            Settings = _provider.GetRequiredService<SettingsService>();
            _previous = (Settings.Current.UiLanguage, Settings.Current.NameLanguage, Settings.Current.LastMap, Settings.Current.ExplorationPinnedMap);
            Settings.Current.UiLanguage = Settings.Current.NameLanguage = UiText.Language = language;
            Settings.Current.LastMap = "altgard";
            typeof(OcrLanguageInstaller).GetProperty(nameof(OcrLanguageInstaller.CheckedAt))!.SetValue(_provider.GetRequiredService<OcrLanguageInstaller>(), DateTime.UtcNow);
        }
        public void Detect(string map)
        {
            typeof(MapTrackingService).GetProperty(nameof(MapTrackingService.Running))!.SetValue(Tracking, true);
            typeof(MapTrackingService).GetProperty(nameof(MapTrackingService.Found))!.SetValue(Tracking, true);
            var at = DateTimeOffset.UtcNow;
            typeof(MapTrackingService).GetProperty(nameof(MapTrackingService.Position))!.SetValue(Tracking, new PlayerPosition(map, 100, 100, at));
            typeof(MapTrackingService).GetProperty(nameof(MapTrackingService.DetectedMap))!.SetValue(Tracking, new DetectedMapContext(map, false, at));
        }
        public void SetScan(string name, object value) => typeof(ExplorationScanService).GetProperty(name)!.SetValue(Scan, value);
        public void SetRunning(bool running) => typeof(ExplorationScanService).GetField("_running", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Scan, running ? 1 : 0);
        public async Task<string> Render()
        {
            await using var renderer = new HtmlRenderer(_provider, _provider.GetRequiredService<ILoggerFactory>());
            return WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<ExplorationView>()).ToHtmlString()));
        }
        public async Task Export(string html, string state)
        {
            if (Environment.GetEnvironmentVariable("SOULCREST_SCAN_UI_PREVIEW") is not { Length: > 0 } output) return;
            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, "exploration-overview-" + state + "-" + _language + ".html"),
                "<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><link rel=\"stylesheet\" href=\"/css/app.css\"></head><body>" + html + "</body></html>");
        }
        public async ValueTask DisposeAsync()
        {
            SetRunning(false);
            typeof(MapTrackingService).GetProperty(nameof(MapTrackingService.Running))!.SetValue(Tracking, false);
            Settings.Current.UiLanguage = _previous.Ui;
            Settings.Current.NameLanguage = _previous.Names;
            Settings.Current.LastMap = _previous.Map;
            Settings.Current.ExplorationPinnedMap = _previous.Pin;
            UiText.Language = _previousLanguage;
            await _provider.DisposeAsync();
            File.Delete(_profile);
        }
    }
}
