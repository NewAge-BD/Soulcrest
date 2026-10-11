using System.Drawing;
using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Components;
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
public sealed class ExplorationScanViewTests
{
    [Theory]
    [InlineData("de", "Karte und Liste öffnen", "Kurz stillhalten", "Langsam weiter", "Scan starten")]
    [InlineData("en", "Open the map and list", "Keep the list still", "Continue slowly", "Start scan")]
    public async Task ReadyViewExplainsThreeStepsAndKeepsManualCorrection(string language, string first, string second, string third, string start)
    {
        await using var view = new TestView(language);
        var html = await view.Render();
        Assert.Contains("data-scan-state=\"idle\"", html);
        Assert.Contains("<ol class=\"exploration-scan-steps\"", html);
        Assert.Contains(first, html);
        Assert.Contains(second, html);
        Assert.Contains(third, html);
        Assert.Contains(start, html);
        Assert.Contains(language == "de" ? "Karte und geöffnete Liste werden automatisch erkannt." : "The map and open list are detected automatically.", html);
        Assert.Contains(language == "de" ? "auf der erkannten Karte" : "on the detected map", html);
        Assert.DoesNotContain(language == "de" ? "dieselbe Karte" : "the same map", html);
        Assert.Contains(language == "de" ? "Namen allein setzen keinen Abschluss" : "names alone do not mark completion", html);
        Assert.DoesNotContain("Scan-Vorschau und unklare Einträge", html);
        Assert.DoesNotContain("Scan preview and unclear entries", html);
        await view.Export(html, "ready");
    }

    [Theory]
    [InlineData("de", "Zeilen geprüft", "Einträge übernommen", "Scan stoppen", "Versiegelte Dungeons")]
    [InlineData("en", "Rows checked", "Entries applied", "Stop scan", "Sealed Dungeons")]
    public async Task RunningViewSeparatesReadRowsImportedEntriesAndSavedCompletions(string language, string read, string imported, string stop, string category)
    {
        await using var view = new TestView(language);
        view.SetScan("Status", "Liste wird gelesen – kurz stillhalten.");
        view.SetScan("ScanMap", "altgard");
        view.SetScan("ScanKind", "all");
        view.SetScan("DetectedKind", "dungeon");
        view.SetScan("Recognized", 7);
        view.SetScan("Feedback", new ExplorationScanFeedback(new Rectangle(0, 0, 2560, 1080), null, "Read", 2, 3, [], false));
        view.SetRunning(true);
        var html = await view.Render();
        Assert.Contains("data-scan-state=\"running\"", html);
        Assert.Contains("role=\"status\" aria-live=\"polite\"", html);
        Assert.Contains($"<dt>{read}</dt><dd>2 / 3</dd>", html);
        Assert.Contains($"<dt>{imported}</dt><dd>7</dd>", html);
        Assert.Contains(stop, html);
        Assert.Contains(category, html);
        Assert.DoesNotContain("<progress value=\"7\"", html);
        Assert.DoesNotContain("class=\"btn primary\"", html);
        Assert.Equal(new[] { "done" }, view.Exploration.Completed);
        await view.Export(html, "running");
    }

    [Theory]
    [InlineData("de", "Scan-Vorschau und unklare Einträge", "Halte die Liste kurz still.")]
    [InlineData("en", "Scan preview and unclear entries", "Keep the list still for a moment.")]
    public async Task PreviewAndUncertainNamesStayInCollapsedInspectionWithoutChangingProgress(string language, string title, string instruction)
    {
        await using var view = new TestView(language);
        view.SetScan("Preview", "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jBzYAAAAASUVORK5CYII=");
        view.SetScan("Unmatched", new[] { "Uncertain dungeon", "<uncertain text>" });
        view.SetScan("Feedback", new ExplorationScanFeedback(new Rectangle(0, 0, 2560, 1080), null, "Uncertain", 0, 2, [], false));
        var html = await view.Render();
        Assert.Contains("<details class=\"exploration-scan-inspection\"><summary>", html);
        Assert.Contains(title, html);
        Assert.Contains(instruction, html);
        Assert.Contains("<ul><li>Uncertain dungeon</li><li><uncertain text></li></ul>", html);
        Assert.Contains("class=\"exploration-scan-unclear-count\">2</span>", html);
        Assert.DoesNotContain("exploration-scan-inspection\" open", html);
        Assert.Equal(new[] { "done" }, view.Exploration.Completed);
        var encoded = await view.Render(decode: false);
        Assert.Contains("&lt;uncertain text&gt;", encoded);
        await view.Export(html, "unclear");
    }

    [Theory]
    [InlineData("de", "Scan starten")]
    [InlineData("en", "Start scan")]
    public async Task AMapWithoutPlacesDoesNotBlockAutomaticScanningWhenAnotherMapHasPlaces(string language, string start)
    {
        await using var view = new TestView(language);
        var html = await view.Render(map: "empty-map", hasPlaces: false, kind: "stronghold");
        var button = System.Text.RegularExpressions.Regex.Matches(html, "<button[^>]*>[^<]*</button>")
            .Cast<System.Text.RegularExpressions.Match>().Single(match => match.Value.Contains(start, StringComparison.Ordinal)).Value;
        Assert.DoesNotContain("disabled", button);
        Assert.Contains(language == "de" ? "Karte und geöffnete Liste werden automatisch erkannt." : "The map and open list are detected automatically.", html);
        Assert.Equal(new[] { "done" }, view.Exploration.Completed);
    }

    [Theory]
    [InlineData("de", "Scan starten")]
    [InlineData("en", "Start scan")]
    public async Task NoExplorationPlacesKeepsAutomaticScanDisabled(string language, string start)
    {
        await using var view = new TestView(language, hasPlaces: false);
        var html = await view.Render(hasPlaces: true);
        var button = System.Text.RegularExpressions.Regex.Matches(html, "<button[^>]*>[^<]*</button>")
            .Cast<System.Text.RegularExpressions.Match>().Single(match => match.Value.Contains(start, StringComparison.Ordinal)).Value;
        Assert.Contains("disabled", button);
        Assert.Empty(view.Exploration.Completed);
    }

    private sealed class TestView : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly SettingsService _settings;
        private readonly (string Ui, string Names, string Map) _previous;
        private readonly string _previousLanguage = UiText.Language;
        private readonly string _profile = Path.Combine(AppPaths.DataDirectory, "exploration-scan-view-" + Guid.NewGuid().ToString("N") + ".json");
        private readonly string _language;
        public ExplorationService Exploration { get; }
        private ExplorationScanService Scan => _provider.GetRequiredService<ExplorationScanService>();

        public TestView(string language, bool hasPlaces = true)
        {
            _language = language;
            Exploration = new ExplorationService(_profile,
            hasPlaces ? [new("done", "altgard", "dungeon", 1, "Manual completed dungeon", null),
             new("open", "altgard", "dungeon", 2, "Manual open dungeon", null),
             new("unknown", "altgard", "dungeon", 3, "Manual unknown dungeon", null),
             new("stronghold", "altgard", "stronghold", 4, "Manual stronghold", null),
             new("kibelisk", "altgard", "kibelisk", 5, "Manual Kibelisk", null)] : []);
            if (hasPlaces) Exploration.SetDone(Exploration.ActiveId, ["done"]);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<SettingsService>();
            services.AddSingleton<GameCaptureService>();
            services.AddSingleton<ProgressService>();
            services.AddSingleton<NetworkLootService>();
            services.AddSingleton(Exploration);
            services.AddSingleton<ExplorationScanService>();
            services.AddSingleton<CharacterDetectionService>();
            services.AddSingleton<OcrLanguageInstaller>();
            _provider = services.BuildServiceProvider();
            _settings = _provider.GetRequiredService<SettingsService>();
            _previous = (_settings.Current.UiLanguage, _settings.Current.NameLanguage, _settings.Current.LastMap);
            _settings.Current.UiLanguage = _settings.Current.NameLanguage = UiText.Language = language;
            _settings.Current.LastMap = "altgard";
            typeof(OcrLanguageInstaller).GetProperty(nameof(OcrLanguageInstaller.CheckedAt))!
                .SetValue(_provider.GetRequiredService<OcrLanguageInstaller>(), DateTime.UtcNow);
        }

        public void SetScan(string name, object value) => typeof(ExplorationScanService).GetProperty(name)!.SetValue(Scan, value);
        public void SetRunning(bool running) => typeof(ExplorationScanService).GetField("_running", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Scan, running ? 1 : 0);
        public async Task<string> Render(bool decode = true, string map = "altgard", bool hasPlaces = true, string kind = "all")
        {
            await using var renderer = new HtmlRenderer(_provider, _provider.GetRequiredService<ILoggerFactory>());
            var html = await renderer.Dispatcher.InvokeAsync(async () =>
                (await renderer.RenderComponentAsync<ExplorationScanPanel>(ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(ExplorationScanPanel.Map)] = map, [nameof(ExplorationScanPanel.HasPlaces)] = hasPlaces, [nameof(ExplorationScanPanel.Kind)] = kind }))).ToHtmlString());
            return decode ? WebUtility.HtmlDecode(html) : html;
        }
        public async Task Export(string html, string state)
        {
            if (Environment.GetEnvironmentVariable("SOULCREST_SCAN_UI_PREVIEW") is not { Length: > 0 } output) return;
            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, "exploration-" + state + "-" + _language + ".html"),
                "<!doctype html><html><head><meta charset=\"utf-8\"><link rel=\"stylesheet\" href=\"/css/app.css\"></head><body>" + html + "</body></html>");
        }
        public async ValueTask DisposeAsync()
        {
            SetRunning(false);
            _settings.Current.UiLanguage = _previous.Ui;
            _settings.Current.NameLanguage = _previous.Names;
            _settings.Current.LastMap = _previous.Map;
            UiText.Language = _previousLanguage;
            await _provider.DisposeAsync();
            File.Delete(_profile);
        }
    }
}
