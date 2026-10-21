using System.Net;
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
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class MapNetworkLocationTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 21, 10, 0, 0, TimeSpan.Zero);

    [RequiresTheory(TestRequirement.PrivateInstanceCatalog)]
    [InlineData("de", "Kraohöhle", "Expeditionen · Eroberung", "Bestätigt")]
    [InlineData("en", "Krao Cave", "Expedition · Conquest", "Confirmed")]
    public async Task NativeLocationAndModeRenderInsideTheNetworkTrackingTile(string language, string name, string detail, string status)
    {
        await using var f = new Fixture(language);
        f.ConfirmKrao();
        await f.RenderMap();
        var markup = await f.Html();
        var tile = NetworkTile(WebUtility.HtmlDecode(markup));
        Assert.Contains(UiText.T("Netzwerk-Tracking"), tile);
        Assert.Contains("data-world-status=\"confirmed\"", tile);
        Assert.Contains("role=\"status\" aria-label=\"" + UiText.T("Aufenthalt") + "\"", tile);
        Assert.Matches("(?s)world-location-heading.*?world-location-name.*?" + Regex.Escape(name)
            + ".*?" + Regex.Escape(status) + ".*?</div>.*?world-location-detail.*?" + Regex.Escape(detail), tile);
        Assert.Single(Regex.Matches(markup, "data-world-status="));
        Assert.DoesNotContain("600001", tile);
        Assert.False(f.Tracking.Running);
        Assert.Equal(0, f.Capture.Diagnostics.Grabs);
        await f.Export(markup);
    }

    [RequiresTheory(TestRequirement.PrivateInstanceCatalog)]
    [InlineData("de")]
    [InlineData("en")]
    public async Task ConfirmedClientMapSelectsMapTabBeforePlayerAcquisitionWithoutFollowingOrProgression(string language)
    {
        await using var f = new Fixture(language);
        f.Settings.Update(s => { s.FollowPlayer = false; s.ProgressionEnabled = false; });
        f.World.ObserveOwn(1010, 1, At);
        await f.RenderMap();
        var markup = await f.Html();
        var tile = NetworkTile(WebUtility.HtmlDecode(markup));
        Assert.Contains("<option value=\"verteron\" selected>", markup);
        Assert.Equal("verteron", f.Settings.Current.LastMap);
        Assert.False(f.Settings.Current.FollowPlayer);
        Assert.False(f.Settings.Current.ProgressionEnabled);
        Assert.Contains("Verteron", tile);
        Assert.Contains("data-world-status=\"confirmed\"", tile);
        Assert.Null(f.Tracking.Position);
        Assert.False(f.Tracking.Running);
        Assert.Equal(0, f.Capture.Diagnostics.Grabs);
        await f.Export(markup, "client-map-");
    }

    [RequiresTheory(TestRequirement.PrivateInstanceCatalog)]
    [InlineData("de")]
    [InlineData("en")]
    public async Task OwnTransitionAndNextOwnRefreshImmediatelyWhileMapTrackingIsStopped(string language)
    {
        await using var f = new Fixture(language);
        var view = await f.RenderMap();
        // Suppress the independent loot refresh path: location updates must use WorldContext.Changed.
        typeof(MapView).GetField("_lastLootRender", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, DateTime.MaxValue);
        Assert.Contains("data-world-status=\"unknown\"", await f.Html());
        f.World.ObserveOwn(600001, 1, At);
        f.World.ObserveDungeon(new(600001, DungeonRunState.Process, 0, 0), 1, At);
        var tile = NetworkTile(WebUtility.HtmlDecode(await f.Html()));
        Assert.Contains(language == "de" ? "Kraohöhle" : "Krao Cave", tile);
        Assert.Contains(language == "de" ? "Eroberung" : "Conquest", tile);

        f.World.Invalidate(WorldContextStatus.Transitioning, 2, "synthetic-entry", At.AddSeconds(1));
        tile = NetworkTile(WebUtility.HtmlDecode(await f.Html()));
        Assert.Contains("data-world-status=\"transitioning\"", tile);
        Assert.DoesNotContain("Krao", tile, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("world-location-detail", tile);
        f.World.ObserveOwn(1110, 3, At.AddSeconds(2));
        tile = NetworkTile(WebUtility.HtmlDecode(await f.Html()));
        Assert.Contains("data-world-status=\"confirmed\"", tile);
        Assert.Contains("Altgard", tile);
        Assert.DoesNotContain("Krao", tile, StringComparison.OrdinalIgnoreCase);
        Assert.False(f.Tracking.Running);
        Assert.Equal(0, f.Capture.Diagnostics.Grabs);
    }

    [RequiresTheory(TestRequirement.PrivateInstanceCatalog)]
    [InlineData("de")]
    [InlineData("en")]
    public async Task DisabledNetworkPreferenceHidesPreviouslyConfirmedDetails(string language)
    {
        await using var f = new Fixture(language);
        f.ConfirmKrao();
        await f.RenderMap();
        f.Settings.Update(settings => settings.LootTrackingEnabled = false);
        var tile = NetworkTile(WebUtility.HtmlDecode(await f.Html()));
        Assert.Contains("data-world-status=\"off\"", tile);
        Assert.Contains(UiText.T("Netzwerk-Tracking ausgeschaltet"), tile);
        Assert.DoesNotContain("Krao", tile, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("world-location-detail", tile);
        Assert.Equal(WorldContextStatus.Confirmed, f.World.Snapshot.Status);
        Assert.Equal(0, f.Capture.Diagnostics.Grabs);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task PetProgressPanelDoesNotRepeatTheCurrentLocation(string language)
    {
        await using var f = new Fixture(language);
        f.ConfirmKrao();
        var html = await f.RenderPetStatus();
        Assert.Contains("data-progress-source=\"network\"", html);
        Assert.Contains(UiText.T("Pet-Baseline"), html);
        Assert.DoesNotContain("world-location", html);
        Assert.DoesNotContain("data-world-status", html);
        Assert.DoesNotContain("Krao", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DisposingMapViewRemovesItsDirectWorldContextSubscription()
    {
        await using var f = new Fixture("en");
        var before = SubscriberCount(f.World);
        await f.RenderMap();
        Assert.Equal(before + 1, SubscriberCount(f.World));
        await f.DisposeRenderer();
        Assert.Equal(before, SubscriberCount(f.World));
        f.World.ObserveOwn(600001, 1, At);
        Assert.False(f.Tracking.Running);
        Assert.Equal(0, f.Capture.Diagnostics.Grabs);
    }

    private static int SubscriberCount(WorldContextService source) =>
        (typeof(WorldContextService).GetField("Changed", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(source) as Delegate)?.GetInvocationList().Length ?? 0;

    private static string NetworkTile(string html)
    {
        var marker = html.IndexOf("class=\"tile-part map-network-tracking\"", StringComparison.Ordinal);
        Assert.True(marker >= 0, "The production network tracking tile must be present.");
        var start = html.LastIndexOf("<div", marker, StringComparison.Ordinal);
        var depth = 0;
        foreach (Match tag in Regex.Matches(html[start..], "</?div\\b[^>]*>"))
        {
            depth += tag.Value.StartsWith("</", StringComparison.Ordinal) ? -1 : 1;
            if (depth == 0) return html.Substring(start, tag.Index + tag.Length);
        }
        throw new InvalidOperationException("Unclosed network tracking tile.");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-map-location-" + Guid.NewGuid().ToString("N"));
        private readonly string? _previousMapData = Environment.GetEnvironmentVariable("SOULCREST_MAPDATA");
        private readonly string _previousLanguage = UiText.Language;
        private readonly Dictionary<string, byte[]?> _backups = [];
        private readonly ServiceProvider _provider;
        private readonly HtmlRenderer _renderer;
        private readonly string _language;
        private Func<string>? _html;
        private bool _rendererDisposed;
        public SettingsService Settings { get; }
        public NetworkLootService Network { get; }
        public WorldContextService World => Network.WorldContext;
        public MapTrackingService Tracking { get; }
        public GameCaptureService Capture { get; }

        public Fixture(string language)
        {
            _language = language;
            Directory.CreateDirectory(_folder);
            File.WriteAllText(Path.Combine(_folder, "manifest.json"), """{"maps":[{"id":"altgard","label":"Altgard","group":"Asmodian"},{"id":"verteron","label":"Verteron","group":"Elyos"}]}""");
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _folder);
            foreach (var path in new[] { AppPaths.SettingsFile, AppPaths.TargetsFile, AppPaths.RoutesFile, AppPaths.LegacyRoutesFile,
                Path.Combine(AppPaths.DataDirectory, "routes.seeded.json"), Path.Combine(AppPaths.DataDirectory, "leveling-progress.json") })
            {
                _backups.Add(path, File.Exists(path) ? File.ReadAllBytes(path) : null);
                File.Delete(path);
            }
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<SettingsService>();
            services.AddSingleton<GameCaptureService>();
            services.AddSingleton(_ => new ProgressService(Path.Combine(_folder, "progress.json")));
            services.AddSingleton(new ExplorationService(Path.Combine(_folder, "exploration.json"), []));
            services.AddSingleton<NetworkLootService>();
            services.AddSingleton(provider => new TrackerService(provider.GetRequiredService<SettingsService>(), new OfflineNetworkSource()));
            // No WorldContext connection here: a tracking event must not mask a missing MapView subscription.
            services.AddSingleton(provider => new MapTrackingService(provider.GetRequiredService<SettingsService>(),
                provider.GetRequiredService<ProgressService>(), provider.GetRequiredService<GameCaptureService>()));
            services.AddSingleton<MapTargetsService>();
            services.AddSingleton(_ => new AlertsService(Path.Combine(_folder, "alerts.json"), runTimer: false));
            services.AddSingleton<UiState>();
            services.AddSingleton<IJSRuntime>(new OfflineJs());
            _provider = services.BuildServiceProvider();
            Settings = _provider.GetRequiredService<SettingsService>();
            Settings.Update(settings =>
            {
                settings.UiLanguage = settings.NameLanguage = language;
                settings.LastMap = "altgard";
                settings.LootTrackingEnabled = true;
            });
            Network = _provider.GetRequiredService<NetworkLootService>();
            // The visible tracker uses only the fake source; the real network service is never started.
            _provider.GetRequiredService<TrackerService>().StartIfEnabled();
            Tracking = _provider.GetRequiredService<MapTrackingService>();
            Capture = _provider.GetRequiredService<GameCaptureService>();
            _renderer = new HtmlRenderer(_provider, _provider.GetRequiredService<ILoggerFactory>());
        }

        public void ConfirmKrao()
        {
            Network.ObserveIdentity("synthetic-location", new(101, 501, 100, "SyntheticMapLocation"), At, 600001);
            World.ObserveDungeon(new(600001, DungeonRunState.Process, 0, 0), World.Snapshot.BindingGeneration!.Value, At);
        }

        public async Task<MapView> RenderMap()
        {
            MapView? view = null;
            await _renderer.Dispatcher.InvokeAsync(async () =>
            {
                var root = await _renderer.RenderComponentAsync<CaptureMapView>(ParameterView.FromDictionary(new Dictionary<string, object?>
                { [nameof(CaptureMapView.Capture)] = (Action<MapView>)(value => view = value) }));
                _html = () => root.ToHtmlString();
            });
            return Assert.IsType<MapView>(view);
        }

        public Task<string> Html() => _renderer.Dispatcher.InvokeAsync(() => _html!());

        public Task<string> RenderPetStatus() => _renderer.Dispatcher.InvokeAsync(async () =>
            WebUtility.HtmlDecode((await _renderer.RenderComponentAsync<NetworkProgressStatus>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(NetworkProgressStatus.Scope)] = "pet" }))).ToHtmlString()));

        public async Task Export(string markup, string state = "")
        {
            if (Environment.GetEnvironmentVariable("SOULCREST_MAP_LOCATION_PREVIEW") is not { Length: > 0 } output) return;
            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, $"map-network-location-{state}{_language}.html"),
                "<!doctype html><html lang=\"" + _language + "\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">"
                + "<link rel=\"stylesheet\" href=\"/css/app.css\"><link rel=\"stylesheet\" href=\"/css/map-legend.css\">"
                + "<style>html,body{height:100%;margin:0}.mapview{height:100vh}#map{background:linear-gradient(135deg,#1b302c,#253c32)}</style></head><body>"
                + markup + "</body></html>");
        }

        public async Task DisposeRenderer()
        {
            if (_rendererDisposed) return;
            _rendererDisposed = true;
            await _renderer.DisposeAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await DisposeRenderer();
            await _provider.DisposeAsync();
            foreach (var (path, bytes) in _backups)
            {
                if (bytes is null) File.Delete(path); else File.WriteAllBytes(path, bytes);
            }
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _previousMapData);
            UiText.Language = _previousLanguage;
            Directory.Delete(_folder, recursive: true);
        }
    }

    private sealed class CaptureMapView : ComponentBase
    {
        [Parameter] public Action<MapView> Capture { get; set; } = default!;
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<MapView>(0);
            builder.AddComponentReferenceCapture(1, value => Capture((MapView)value));
            builder.CloseComponent();
        }
    }

    private sealed class OfflineJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<TValue>(identifier, args);
    }

    private sealed class OfflineNetworkSource : INetworkLootSource
    {
        public bool Running { get; private set; } = true;
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
