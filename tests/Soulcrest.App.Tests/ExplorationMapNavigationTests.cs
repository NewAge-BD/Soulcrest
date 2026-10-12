using System.Reflection;
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

[Collection("Settings file")]
public sealed class ExplorationMapNavigationTests
{
    [Fact]
    public void ExplorationFocusIsOneShotAndIndependentOfBossAndPetRequests()
    {
        var ui = new UiState();
        var place = Place;
        var boss = new MapTarget("boss", "altgard", 10, 20, "Boss", "Boss", null);
        ui.ShowBossOnMap(boss);
        ui.ShowPetOnMap("pet-id");
        var changes = 0;
        ui.Changed += () => changes++;
        ui.ShowExplorationOnMap(place);
        Assert.Equal("map", ui.Tab);
        Assert.Equal(1, changes);
        Assert.Equal(place, ui.TakePendingExplorationFocus());
        Assert.Null(ui.TakePendingExplorationFocus());
        Assert.Equal(boss, ui.TakePendingBossFocus());
        Assert.Equal("pet-id", ui.TakePendingPetFocus());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FocusingAPlaceSwitchesMapBeforeItsCatalogueMarkerAndLeavesTrackingAndBossModeAlone(bool followPlayer)
    {
        await using var scenario = new Scenario();
        var settings = scenario.Provider.GetRequiredService<SettingsService>();
        var tracking = scenario.Provider.GetRequiredService<MapTrackingService>();
        var targets = scenario.Provider.GetRequiredService<MapTargetsService>();
        var exploration = scenario.Provider.GetRequiredService<ExplorationService>();
        settings.Current.LastMap = "altgard";
        settings.Current.FollowPlayer = followPlayer;
        settings.Current.BossRushEnabled = true;
        var boss = new MapTarget("boss-preview", "altgard", 10, 20, "Boss", "Boss", null);
        targets.SetBossPreview(boss);
        targets.SetBossRush(boss);
        var done = exploration.Completed;
        if (followPlayer)
        {
            // A manually requested place on another map must survive the first player push.
            Property(tracking, nameof(MapTrackingService.Position), new PlayerPosition("altgard", 100, 200, DateTimeOffset.UtcNow));
            Property(tracking, nameof(MapTrackingService.Found), true);
        }
        var view = await scenario.Render();
        await scenario.Renderer.Dispatcher.InvokeAsync(() => view.FocusExploration(Place));

        var show = scenario.Js.Calls.FindIndex(c => c.Name == "soulcrestMap.show");
        var fly = scenario.Js.Calls.FindIndex(c => c.Name == "soulcrestMap.flyToMarker");
        Assert.True(show >= 0 && fly > show);
        Assert.Equal("verteron", scenario.Js.Calls[show].Arguments[0]);
        Assert.Equal(42, scenario.Js.Calls[fly].Arguments[0]);
        Assert.Equal("verteron", settings.Current.LastMap);
        Assert.Equal(followPlayer, settings.Current.FollowPlayer);
        Assert.True(settings.Current.BossRushEnabled);
        Assert.Same(boss, targets.BossPreview);
        Assert.Same(boss, targets.BossRush);
        Assert.False(tracking.Running);
        Assert.Equal(done.Order(), exploration.Completed.Order());
        Assert.DoesNotContain(scenario.Js.Calls, call => call.Name == "soulcrestMap.flyToTarget");
    }

    [Fact]
    public async Task UnknownPlaceCannotFocusAnUnrelatedMarkerIndex()
    {
        await using var scenario = new Scenario();
        var view = await scenario.Render();
        await scenario.Renderer.Dispatcher.InvokeAsync(() => view.FocusExploration(Place with { Id = "unknown" }));
        Assert.DoesNotContain(scenario.Js.Calls, call => call.Name is "soulcrestMap.show" or "soulcrestMap.flyToMarker");
    }

    [Fact]
    public async Task ProgressionFollowsARealAltgardToAbyssMoveWithPlayerFollowingDisabledAndRejectsOldCallbacks()
    {
        await using var scenario = new Scenario();
        var settings = scenario.Provider.GetRequiredService<SettingsService>();
        var tracking = scenario.Provider.GetRequiredService<MapTrackingService>();
        var targets = scenario.Provider.GetRequiredService<MapTargetsService>();
        settings.Current.LastMap = "altgard";
        settings.Current.FollowPlayer = false;
        settings.Current.ProgressionEnabled = true;
        var view = await scenario.Render();
        SetTracked(tracking, "altgard");
        await scenario.Renderer.Dispatcher.InvokeAsync(() => PushPlayer(view));
        var previous = new MapTarget("progression", "altgard", 10, 20, "Old target", "Pet", null, PetId: "old-pet");
        targets.SetProgression(previous);
        scenario.Js.Calls.Clear();
        SetTracked(tracking, AbyssPlace.Map);

        await scenario.Renderer.Dispatcher.InvokeAsync(() => PushPlayer(view));
        Assert.Equal(AbyssPlace.Map, settings.Current.LastMap);
        Assert.Null(targets.Progression); // discarded before JS can report the destination's next place
        Assert.Contains(scenario.Js.Calls, call => call.Name == "soulcrestMap.show" && Equals(call.Arguments[0], AbyssPlace.Map));
        Assert.Contains(scenario.Js.Calls, call => call.Name == "soulcrestMap.setProgression" && Equals(call.Arguments[0], true));
        Assert.False(settings.Current.FollowPlayer);
        await scenario.Renderer.Dispatcher.InvokeAsync(() =>
            view.OnProgressionTarget(AbyssPlace.Map, AbyssPlace.Id, AbyssPlace.X, AbyssPlace.Y, AbyssPlace.En));
        var next = Assert.IsType<MapTarget>(targets.Progression);
        Assert.Equal(AbyssPlace.Map, next.MapId);
        await scenario.Renderer.Dispatcher.InvokeAsync(() =>
        {
            view.OnProgressionTarget("altgard", "old-pet", 10, 20, "Old target");
            view.OnProgressionTarget("altgard", null, 0, 0, null);
        });
        Assert.Same(next, targets.Progression);
        Assert.Empty(scenario.Provider.GetRequiredService<ExplorationService>().Completed);
        Assert.Equal(0, scenario.Provider.GetRequiredService<GameCaptureService>().Diagnostics.Grabs);
    }

    [Fact]
    public async Task EnablingProgressionAlignsWithAnAlreadyObservedPlayerMapWithoutPlayerFollowing()
    {
        await using var scenario = new Scenario();
        var settings = scenario.Provider.GetRequiredService<SettingsService>();
        var tracking = scenario.Provider.GetRequiredService<MapTrackingService>();
        var targets = scenario.Provider.GetRequiredService<MapTargetsService>();
        settings.Current.LastMap = "altgard";
        settings.Current.FollowPlayer = false;
        settings.Current.ProgressionEnabled = false;
        var view = await scenario.Render();
        SetTracked(tracking, AbyssPlace.Map);
        Field(view, "_trackedMap", AbyssPlace.Map); // no new tracking transition remains to notice
        targets.SetProgression(new MapTarget("progression", "altgard", 10, 20, "Old target", "Pet", null, PetId: "old-pet"));
        await scenario.Renderer.Dispatcher.InvokeAsync(() => (Task)typeof(MapView)
            .GetMethod("ToggleProgression", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(view, [new ChangeEventArgs { Value = true }])!);
        Assert.True(settings.Current.ProgressionEnabled);
        Assert.False(settings.Current.FollowPlayer);
        Assert.Equal(AbyssPlace.Map, settings.Current.LastMap);
        Assert.Null(targets.Progression);
        Assert.Contains(scenario.Js.Calls, call => call.Name == "soulcrestMap.show" && Equals(call.Arguments[0], AbyssPlace.Map));
        Assert.Equal(0, scenario.Provider.GetRequiredService<GameCaptureService>().Diagnostics.Grabs);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WorldMapBrowsingOrExpiredEvidenceCannotRedirectProgression(bool worldMap)
    {
        await using var scenario = new Scenario();
        var settings = scenario.Provider.GetRequiredService<SettingsService>();
        var tracking = scenario.Provider.GetRequiredService<MapTrackingService>();
        var targets = scenario.Provider.GetRequiredService<MapTargetsService>();
        settings.Current.LastMap = "altgard";
        settings.Current.FollowPlayer = false;
        settings.Current.ProgressionEnabled = true;
        var view = await scenario.Render();
        SetTracked(tracking, "altgard");
        await scenario.Renderer.Dispatcher.InvokeAsync(() => PushPlayer(view));
        var previous = new MapTarget("progression", "altgard", 10, 20, "Current target", "Pet", null, PetId: "current-pet");
        targets.SetProgression(previous);
        scenario.Js.Calls.Clear();
        if (worldMap)
        {
            Field(tracking, "_worldMapOpen", true);
            Property(tracking, nameof(MapTrackingService.DetectedMap), new DetectedMapContext(AbyssPlace.Map, true, DateTimeOffset.UtcNow));
        }
        else SetTracked(tracking, AbyssPlace.Map, DateTimeOffset.UtcNow.AddSeconds(-20));
        await scenario.Renderer.Dispatcher.InvokeAsync(() => PushPlayer(view));
        Assert.Equal("altgard", settings.Current.LastMap);
        Assert.Same(previous, targets.Progression);
        Assert.DoesNotContain(scenario.Js.Calls, call => call.Name == "soulcrestMap.show");
    }

    [Theory]
    [InlineData("lost")]
    [InlineData("expired")]
    [InlineData("world-map")]
    [InlineData("instance")]
    [InlineData("stopped")]
    public async Task HistoricalAbyssPositionCannotCenterOrAdvertiseAValidPlayer(string state)
    {
        await using var scenario = new Scenario();
        var settings = scenario.Provider.GetRequiredService<SettingsService>();
        var tracking = scenario.Provider.GetRequiredService<MapTrackingService>();
        settings.Current.LastMap = "altgard";
        settings.Current.FollowPlayer = true;
        settings.Current.ProgressionEnabled = true;
        var view = await scenario.Render();
        Field(view, "_trackedMap", AbyssPlace.Map);
        SetTracked(tracking, AbyssPlace.Map, state == "expired" ? DateTimeOffset.UtcNow.AddSeconds(-20) : null);
        if (state == "lost") Property(tracking, nameof(MapTrackingService.Found), false);
        if (state == "stopped") Property(tracking, nameof(MapTrackingService.Running), false);
        if (state == "instance") Field(tracking, "_inInstance", true);
        if (state == "world-map")
        {
            Field(tracking, "_worldMapOpen", true);
            Property(tracking, nameof(MapTrackingService.DetectedMap),
                new DetectedMapContext("altgard", true, DateTimeOffset.UtcNow));
        }

        await scenario.Renderer.Dispatcher.InvokeAsync(() => (Task)typeof(MapView)
            .GetMethod("CenterOnPlayer", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null)!);
        await scenario.Renderer.Dispatcher.InvokeAsync(() => PushPlayer(view));

        Assert.Equal("altgard", settings.Current.LastMap);
        Assert.DoesNotContain(scenario.Js.Calls, call => call.Name is "soulcrestMap.show" or "soulcrestMap.centerOnPlayer");
        var marker = Assert.Single(scenario.Js.Calls, call => call.Name == "soulcrestMap.setPlayer");
        Assert.Equal(false, marker.Arguments[3]); // no following the frozen/expired player
        Assert.Equal(false, marker.Arguments[4]); // last-known marker is grey
        Assert.Equal(0, scenario.Provider.GetRequiredService<GameCaptureService>().Diagnostics.Grabs);
    }

    [Fact]
    public async Task ReturningFromAbyssUsesFreshAltgardEvidenceAndIgnoresTheOldAbyssPosition()
    {
        await using var scenario = new Scenario();
        var settings = scenario.Provider.GetRequiredService<SettingsService>();
        var tracking = scenario.Provider.GetRequiredService<MapTrackingService>();
        settings.Current.LastMap = AbyssPlace.Map;
        settings.Current.FollowPlayer = true;
        settings.Current.ProgressionEnabled = true;
        var view = await scenario.Render();
        SetTracked(tracking, AbyssPlace.Map);
        await scenario.Renderer.Dispatcher.InvokeAsync(() => PushPlayer(view));
        scenario.Js.Calls.Clear();

        SetTracked(tracking, "altgard");
        await scenario.Renderer.Dispatcher.InvokeAsync(() => PushPlayer(view));
        Assert.Equal("altgard", settings.Current.LastMap);
        Assert.Contains(scenario.Js.Calls, call => call.Name == "soulcrestMap.show" && Equals(call.Arguments[0], "altgard"));
        scenario.Js.Calls.Clear();

        SetTracked(tracking, AbyssPlace.Map, DateTimeOffset.UtcNow.AddSeconds(-20));
        await scenario.Renderer.Dispatcher.InvokeAsync(() => PushPlayer(view));
        await scenario.Renderer.Dispatcher.InvokeAsync(() => (Task)typeof(MapView)
            .GetMethod("CenterOnPlayer", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null)!);
        Assert.Equal("altgard", settings.Current.LastMap);
        Assert.DoesNotContain(scenario.Js.Calls, call => call.Name is "soulcrestMap.show" or "soulcrestMap.centerOnPlayer");
    }

    [Fact]
    public async Task MapStatusCallbacksCannotClearDungeonsAndOnlyCorrectKibelisksForTheRenderedProfile()
    {
        await using var scenario = new Scenario();
        var settings = scenario.Provider.GetRequiredService<SettingsService>();
        settings.Current.LastMap = "altgard";
        var exploration = scenario.Provider.GetRequiredService<ExplorationService>();
        var character = exploration.ActiveId;
        var view = await scenario.Render();
        await scenario.Renderer.Dispatcher.InvokeAsync(() =>
        {
            view.OnKibeliskBound("altgard", character, AltgardDungeon.Id, true);
            view.OnKibeliskBound("verteron", character, KibeliskPlace.Id, true);
            view.OnKibeliskBound("altgard", "other-profile", KibeliskPlace.Id, true);
        });
        Assert.Empty(exploration.Completed);
        await scenario.Renderer.Dispatcher.InvokeAsync(() => view.OnKibeliskBound("altgard", character, KibeliskPlace.Id, true));
        Assert.Equal(new[] { KibeliskPlace.Id }, exploration.Completed);
        exploration.Add("Synthetic second profile");
        await scenario.Renderer.Dispatcher.InvokeAsync(() => view.OnKibeliskBound("altgard", character, KibeliskPlace.Id, false));
        Assert.Empty(exploration.Completed);
        exploration.Select(character);
        Assert.Equal(new[] { KibeliskPlace.Id }, exploration.Completed);
        await scenario.Renderer.Dispatcher.InvokeAsync(() => view.OnKibeliskBound("altgard", character, KibeliskPlace.Id, false));
        Assert.Empty(exploration.Completed);
    }

    private static ExplorationPlace KibeliskPlace => new("altgard|kibelisk|Fixture Kibelisk", "altgard", "kibelisk", 3, "Fixture Kibelisk", null, 100, 200);
    private static ExplorationPlace AltgardDungeon => new("altgard|dungeon|Fixture Dungeon", "altgard", "dungeon", 4, "Fixture Dungeon", null, 200, 200);
    private static ExplorationPlace Place => new("verteron|dungeon|Test Entrance", "verteron", "dungeon", 42, "Test Entrance", null, 400, 500);
    private static ExplorationPlace AbyssPlace => new("abyss-reshanta-a|stronghold|Fixture Stronghold", "abyss-reshanta-a", "stronghold", 17, "Fixture Stronghold", null, 1200, 1300);
    private static void Property(object target, string name, object value) =>
        target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)!.SetValue(target, value);
    private static void Field(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static Task PushPlayer(MapView view) => (Task)typeof(MapView)
        .GetMethod("PushPlayer", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [true])!;
    private static void SetTracked(MapTrackingService tracking, string map, DateTimeOffset? at = null)
    {
        var timestamp = at ?? DateTimeOffset.UtcNow;
        Property(tracking, nameof(MapTrackingService.Running), true);
        Property(tracking, nameof(MapTrackingService.Found), true);
        Property(tracking, nameof(MapTrackingService.Position), new PlayerPosition(map, 100, 200, timestamp));
        Property(tracking, nameof(MapTrackingService.DetectedMap), new DetectedMapContext(map, false, timestamp));
    }

    private sealed class Scenario : IAsyncDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-map-navigation-" + Guid.NewGuid());
        private readonly string? _previousMapData = Environment.GetEnvironmentVariable("SOULCREST_MAPDATA");
        private readonly Dictionary<string, byte[]?> _backups = [];
        private readonly string _previousLanguage = UiText.Language;
        public ServiceProvider Provider { get; }
        public HtmlRenderer Renderer { get; }
        public RecordingJs Js { get; } = new();

        public Scenario()
        {
            Directory.CreateDirectory(_folder);
            File.WriteAllText(Path.Combine(_folder, "manifest.json"), """{"maps":[{"id":"altgard","label":"Altgard","group":"Asmodian"},{"id":"verteron","label":"Verteron","group":"Elyos"},{"id":"abyss-reshanta-a","label":"Abyss","group":"Abyss"}]}""");
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _folder);
            foreach (var path in new[] { AppPaths.SettingsFile, AppPaths.TargetsFile, AppPaths.RoutesFile,
                Path.Combine(AppPaths.DataDirectory, "routes.seeded.json"), Path.Combine(AppPaths.DataDirectory, "leveling-progress.json") })
                _backups.Add(path, File.Exists(path) ? File.ReadAllBytes(path) : null);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<SettingsService>();
            services.AddSingleton<GameCaptureService>();
            services.AddSingleton<ProgressService>();
            services.AddSingleton<NetworkLootService>();
            services.AddSingleton<TrackerService>();
            services.AddSingleton<MapTrackingService>();
            services.AddSingleton<MapTargetsService>();
            // This fixture owns synthetic navigation targets; no live boss reducer should replace
            // them on a settings change. The real boss/map integration is covered by BossRushViewTests.
            services.AddSingleton(sp => new AlertsService(Path.Combine(_folder, "alerts.json"), runTimer: false));
            services.AddSingleton(new ExplorationService(Path.Combine(_folder, "exploration.json"), [Place, AbyssPlace, KibeliskPlace, AltgardDungeon]));
            services.AddSingleton<UiState>();
            services.AddSingleton<UpdateService>();
            services.AddSingleton<IJSRuntime>(Js);
            Provider = services.BuildServiceProvider();
            Renderer = new HtmlRenderer(Provider, Provider.GetRequiredService<ILoggerFactory>());
        }

        public async Task<MapView> Render()
        {
            MapView? view = null;
            await Renderer.Dispatcher.InvokeAsync(async () =>
                await Renderer.RenderComponentAsync<CaptureMapView>(ParameterView.FromDictionary(
                    new Dictionary<string, object?> { [nameof(CaptureMapView.Capture)] = (Action<MapView>)(v => view = v) })));
            return Assert.IsType<MapView>(view);
        }

        public async ValueTask DisposeAsync()
        {
            await Renderer.DisposeAsync();
            await Provider.DisposeAsync();
            foreach (var (path, bytes) in _backups)
            {
                if (bytes is null) File.Delete(path);
                else File.WriteAllBytes(path, bytes);
            }
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _previousMapData);
            UiText.Language = _previousLanguage;
            Directory.Delete(_folder, true);
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

    private sealed class RecordingJs : IJSRuntime
    {
        public List<(string Name, object?[] Arguments)> Calls { get; } = [];
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            Calls.Add((identifier, args ?? []));
            if (typeof(TValue) == typeof(string))
            {
                var result = identifier == "soulcrestMap.show" ? """{"groups":[],"regions":0,"markers":100}""" : "[]";
                return ValueTask.FromResult((TValue)(object)result);
            }
            return ValueTask.FromResult(default(TValue)!);
        }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<TValue>(identifier, args);
    }
}
