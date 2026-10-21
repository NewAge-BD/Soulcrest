using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Soulcrest.App.Capture;
using Soulcrest.App.Components;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Instances;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class MapFallbackOptionTests
{
    [Theory]
    [InlineData("de", true, false)]
    [InlineData("en", true, false)]
    [InlineData("de", true, true)]
    [InlineData("en", true, true)]
    [InlineData("de", false, false)]
    [InlineData("en", false, false)]
    [InlineData("de", false, true)]
    [InlineData("en", false, true)]
    public async Task PauseOptionFollowsAvailableLocationDetectionAndPreservesItsPreference(
        string language, bool networkEnabled, bool hasLocationFacts)
    {
        await using var f = new Fixture(language, networkEnabled, hasLocationFacts);
        await f.Render();
        var disabled = networkEnabled && hasLocationFacts;
        foreach (var pause in new[] { false, true })
        {
            await f.Update(settings => settings.PauseTrackingInInstances = pause);
            var html = await f.Html();
            AssertOption(html, disabled, pause);
            Assert.Contains(UiText.T("Bei visueller Erkennung in Instanzen ruhen"), WebUtility.HtmlDecode(html));
            Assert.Equal(pause, new SettingsService().Current.PauseTrackingInInstances);
        }

        await f.Update(settings => settings.LootTrackingEnabled = !networkEnabled);
        AssertOption(await f.Html(), !networkEnabled && hasLocationFacts, true);
        Assert.True(f.Settings.Current.PauseTrackingInInstances);
        await f.Update(settings => settings.LootTrackingEnabled = networkEnabled);
        AssertOption(await f.Html(), disabled, true);
        Assert.Equal(0, f.Capture.Diagnostics.Grabs);
        Assert.False(f.Tracking.Running);

        if (networkEnabled && !hasLocationFacts)
            await f.Export(await f.Html(), language);
    }

    private static void AssertOption(string markup, bool disabled, bool pause)
    {
        var input = Regex.Match(markup, "<input\\b[^>]*\\bid=\"pause-tracking-in-instances\"[^>]*>").Value;
        Assert.NotEmpty(input);
        Assert.Equal(disabled, Regex.IsMatch(input, "\\sdisabled(?:[\\s=>])"));
        Assert.Equal(pause, Regex.IsMatch(input, "\\schecked(?:[\\s=>])"));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-fallback-option-" + Guid.NewGuid().ToString("N"));
        private readonly string? _oldMapData = Environment.GetEnvironmentVariable("SOULCREST_MAPDATA");
        private readonly string _oldLanguage = UiText.Language;
        private readonly Dictionary<string, byte[]?> _backups = [];
        private readonly ServiceProvider _provider;
        private readonly HtmlRenderer _renderer;
        private Func<string>? _html;
        public SettingsService Settings => _provider.GetRequiredService<SettingsService>();
        public GameCaptureService Capture => _provider.GetRequiredService<GameCaptureService>();
        public MapTrackingService Tracking => _provider.GetRequiredService<MapTrackingService>();

        public Fixture(string language, bool networkEnabled, bool hasLocationFacts)
        {
            var world = new WorldContextService(hasLocationFacts ? InstanceCatalog.FromJson("""
                {"schemaVersion":1,"maps":[{"id":100,"kind":"openWorld","soulcrestMapId":"altgard","sourceRefs":["synthetic"]}],"dungeons":[]}
                """) : InstanceCatalog.Empty);
            Directory.CreateDirectory(_folder);
            File.WriteAllText(Path.Combine(_folder, "manifest.json"), """
                {"maps":[{"id":"altgard","label":"Altgard","group":"Asmodian"}]}
                """);
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _folder);
            foreach (var path in new[] { AppPaths.SettingsFile, AppPaths.TargetsFile, AppPaths.RoutesFile,
                AppPaths.LegacyRoutesFile, Path.Combine(AppPaths.DataDirectory, "routes.seeded.json"),
                Path.Combine(AppPaths.DataDirectory, "leveling-progress.json") })
            {
                _backups.Add(path, File.Exists(path) ? File.ReadAllBytes(path) : null);
                File.Delete(path);
            }
            JsonFile.Save(AppPaths.SettingsFile, new AppSettings
            {
                LootTrackingEnabled = networkEnabled, UiLanguage = language, NameLanguage = language,
                LastMap = "altgard", CheckUpdatesOnStart = false
            });
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<SettingsService>();
            services.AddSingleton<GameCaptureService>();
            services.AddSingleton(_ => new ProgressService(Path.Combine(_folder, "progress.json")));
            services.AddSingleton(new ExplorationService(Path.Combine(_folder, "exploration.json"), []));
            services.AddSingleton(world);
            services.AddSingleton<NetworkLootService>();
            services.AddSingleton(provider => new TrackerService(provider.GetRequiredService<SettingsService>(), new OfflineNetworkSource()));
            services.AddSingleton<MapTrackingService>();
            services.AddSingleton<MapTargetsService>();
            services.AddSingleton(_ => new AlertsService(Path.Combine(_folder, "alerts.json"), runTimer: false));
            services.AddSingleton<UiState>();
            services.AddSingleton<IJSRuntime, OfflineJs>();
            _provider = services.BuildServiceProvider();
            _provider.GetRequiredService<TrackerService>().StartIfEnabled();
            UiText.Language = language;
            _renderer = new HtmlRenderer(_provider, _provider.GetRequiredService<ILoggerFactory>());
        }

        public Task Render() => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await _renderer.RenderComponentAsync<MapView>();
            _html = () => root.ToHtmlString();
        });

        public async Task Update(Action<AppSettings> update)
        {
            await _renderer.Dispatcher.InvokeAsync(() => Settings.Update(update));
            await _renderer.Dispatcher.InvokeAsync(() => { });
        }

        public Task<string> Html() => _renderer.Dispatcher.InvokeAsync(() => _html!());

        public async Task Export(string markup, string language)
        {
            if (Environment.GetEnvironmentVariable("SOULCREST_FALLBACK_PREVIEW") is not { Length: > 0 } output) return;
            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, $"map-fallback-{language}.html"),
                "<!doctype html><html lang=\"" + language + "\"><head><meta charset=\"utf-8\">"
                + "<link rel=\"stylesheet\" href=\"/css/app.css\"><link rel=\"stylesheet\" href=\"/css/map-legend.css\">"
                + "<style>html,body{height:100%;margin:0}.mapview{height:100vh}</style></head><body>"
                + markup + "</body></html>");
        }

        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync();
            await _provider.DisposeAsync();
            foreach (var (path, bytes) in _backups)
                if (bytes is null) File.Delete(path); else File.WriteAllBytes(path, bytes);
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _oldMapData);
            UiText.Language = _oldLanguage;
            Directory.Delete(_folder, recursive: true);
        }
    }

    private sealed class OfflineJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<TValue>(identifier, args);
    }

    private sealed class OfflineNetworkSource : INetworkLootSource
    {
        public bool Running { get; private set; }
        public string Status => "Netzwerk bereit.";
        public bool StatusIsError => false;
        public string? Connection => null;
        public long Packets => 0;
        public long Messages => 0;
        public IReadOnlyList<int> UnknownPets => [];
        public event Action? Changed { add { } remove { } }
        public event Action<NetworkSoul>? SoulGained { add { } remove { } }
        public void Start() => Running = true;
        public void Stop() => Running = false;
    }
}
