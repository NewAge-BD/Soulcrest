using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Soulcrest.App.Capture;
using Soulcrest.App.Components;
using Soulcrest.App.Components.OverlayKit;
using Soulcrest.App.Network;
using Soulcrest.App.OverlayEditorKit;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Offline production Razor export; does not start an app, capture, clock or online source.</summary>
[Collection("Settings file")]
public sealed class AppThemePreviewTests
{
    [DiagnosticTheory]
    [Trait("Category", "Diagnostic")]
    [InlineData("de")]
    [InlineData("en")]
    public async Task ExportProductionViewsForBothAppThemes(string language)
    {
        var folder = Path.GetFullPath(TestPrerequisites.RequiredValue("SOULCREST_APP_THEME_EXPORT"));
        var repository = TestPrerequisites.RepositoryRoot;
        Assert.StartsWith(Path.GetFullPath(Path.Combine(repository, "output", "social-media")) + Path.DirectorySeparatorChar,
            folder, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SOULCREST_DATA")));
        var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Soulcrest");
        Assert.False(string.Equals(Path.GetFullPath(userData), Path.GetFullPath(AppPaths.DataDirectory), StringComparison.OrdinalIgnoreCase));
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "theme-palettes.json"), JsonSerializer.Serialize(
            AppThemeDefinition.All.Select(theme => new { theme.Id, theme.CssVariables })));
        foreach (var theme in AppThemeDefinition.All)
        {
            await using var fixture = new Fixture(language, theme.Id);
            await Export("settings", await fixture.Render<OptionsView>());
            await Export("pets", await fixture.Render<PetsView>());
            await Export("loot", await fixture.Render<LootTrackerView>(new()
            {
                [nameof(LootTrackerView.Snapshot)] = LootTrackerViewTests.ExampleSnapshot(),
                [nameof(LootTrackerView.IsExample)] = true,
                [nameof(LootTrackerView.SourceAvailable)] = true
            }));
            await Export("overlay-editor", await fixture.Render<OriginalKitOverlayEditor>(new()
            {
                [nameof(OriginalKitOverlayEditor.Section)] = "layout"
            }));
            if (fixture.Progress.MapDataDirectory is { } mapData)
            {
                await Export("map", await fixture.Render<MapView>(),
                    "<script src=\"lib/leaflet/leaflet.js\"></script><script src=\"js/soulcrest-cube-groups.js\"></script>"
                    + "<script src=\"js/soulcrest-canvas-icons.js\"></script><script src=\"js/soulcrest-map.js\"></script><script>"
                    + "(async()=>{const manifest=" + JsonSerializer.Serialize(File.ReadAllText(Path.Combine(mapData, "manifest.json")))
                    + ";soulcrestMap.init('map',manifest,'/imports/generated/mapdata/',{invokeMethodAsync:async()=>{}});"
                    + "await soulcrestMap.show('altgard'," + JsonSerializer.Serialize(language) + ");window.previewMapReady=true;})()"
                    + ".catch(error=>{window.previewMapError=String(error);console.error(error);});</script>");
            }

            async Task Export(string view, string markup, string scripts = "") =>
                await File.WriteAllTextAsync(Path.Combine(folder, $"{view}-{theme.Id}-{language}.html"),
                    Document(repository, language, theme, markup, scripts));
        }

        // Reuse the existing production calculator fixture, including its explicit example-price notice.
        var previous = Environment.GetEnvironmentVariable("SOULCREST_CRAFTING_UI_EXPORT");
        try
        {
            Environment.SetEnvironmentVariable("SOULCREST_CRAFTING_UI_EXPORT", folder);
            await new CraftingCalculatorViewTests().ExportProductionCraftingViewWithAnonymousManualPrices(language);
            var original = await File.ReadAllTextAsync(Path.Combine(folder, "crafting-" + language + ".html"));
            var body = Regex.Match(original, "<body>(.*)</body>", RegexOptions.Singleline).Groups[1].Value;
            Assert.Contains("data-crafting-calculator", body);
            foreach (var theme in AppThemeDefinition.All)
                await File.WriteAllTextAsync(Path.Combine(folder, $"crafting-{theme.Id}-{language}.html"),
                    Document(repository, language, theme, body));
        }
        finally { Environment.SetEnvironmentVariable("SOULCREST_CRAFTING_UI_EXPORT", previous); }
    }

    private static string Document(string repository, string language, AppThemeDefinition theme, string markup, string scripts = "")
    {
        var index = File.ReadAllText(Path.Combine(repository, "src", "Soulcrest.App", "wwwroot", "index.html"));
        var styles = string.Join("\n", Regex.Matches(index, "<link[^>]+rel=\"stylesheet\"[^>]*>")
            .Select(match => match.Value));
        return "<!doctype html><html lang=\"" + language + "\" data-app-theme=\"" + theme.Id + "\"><head><meta charset=\"utf-8\">"
            + "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><base href=\"/src/Soulcrest.App/wwwroot/\">"
            + styles + "<style>:root{" + theme.CssVariables + "}html,body{margin:0;height:100%;}</style></head><body>"
            + markup.Replace("https://mapdata.soulcrest/", "/imports/generated/mapdata/", StringComparison.Ordinal)
            + scripts + "</body></html>";
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _language = UiText.Language;
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "SoulcrestAppThemePreview", Guid.NewGuid().ToString("N"));
        private readonly ServiceProvider _provider;
        private readonly HtmlRenderer _renderer;
        public ProgressService Progress => _provider.GetRequiredService<ProgressService>();

        public Fixture(string language, string theme)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<SettingsService>();
            services.AddSingleton<OverlayHotkeyService>();
            services.AddSingleton<GameCaptureService>();
            services.AddSingleton(_ => new ProgressService(Path.Combine(_directory, "progress.json")));
            services.AddSingleton(_ => new ExplorationService(Path.Combine(_directory, "exploration.json"), []));
            services.AddSingleton<NetworkLootService>();
            services.AddSingleton<INetworkLootSource>(provider => provider.GetRequiredService<NetworkLootService>());
            services.AddSingleton<TrackerService>();
            services.AddSingleton<MapTrackingService>();
            services.AddSingleton<MapTargetsService>();
            services.AddSingleton(_ => new AlertsService(Path.Combine(_directory, "alerts.json"), runTimer: false));
            services.AddSingleton<UiState>();
            services.AddSingleton<IJSRuntime, InertJs>();
            services.AddSingleton<IOverlayService>(_ => new OverlayService(Path.Combine(_directory, "overlays")));
            services.AddSingleton<IOverlayEditorPresentationSource, ExamplePresentation>();
            _provider = services.BuildServiceProvider();
            var settings = _provider.GetRequiredService<SettingsService>();
            settings.Current.UiLanguage = settings.Current.NameLanguage = UiText.Language = language;
            settings.Current.AppTheme = theme;
            settings.Current.TutorialDone = true;
            settings.Current.LastMap = "altgard";
            _renderer = new(_provider, _provider.GetRequiredService<ILoggerFactory>());
        }

        public Task<string> Render<T>(Dictionary<string, object?>? parameters = null) where T : IComponent =>
            _renderer.Dispatcher.InvokeAsync(async () => (await _renderer.RenderComponentAsync<T>(
                ParameterView.FromDictionary(parameters ?? []))).ToHtmlString());

        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync();
            await _provider.DisposeAsync();
            UiText.Language = _language;
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class InertJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => new(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => new(default(TValue)!);
    }

    private sealed class ExamplePresentation : IOverlayEditorPresentationSource
    {
        public event Action? Changed { add { } remove { } }
        public OverlaySnapshot Capture(bool examples = false, string? language = null) => OverlaySnapshot.DemoFor(language);
        public IDisposable Acquire(bool examples = false, bool market = false) => new EmptyLease();
        public void Dispose() { }
        private sealed class EmptyLease : IDisposable { public void Dispose() { } }
    }
}
