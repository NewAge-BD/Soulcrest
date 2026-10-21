using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
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
using Soulcrest.Core.Instances;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class MapNetworkNavigationTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task ConfirmedOwnSelectsTheMapBeforeAnyVisualPositionRegardlessOfFollowingOrProgression(
        bool following, bool progression)
    {
        await using var f = new Scenario();
        f.Settings.Current.FollowPlayer = following;
        f.Settings.Current.ProgressionEnabled = progression;
        await f.RenderReady();
        f.Js.Calls.Clear();

        await f.Own(1010, 1);

        Assert.Equal("verteron", f.Settings.Current.LastMap);
        Assert.Equal("verteron", Assert.Single(f.Js.Shows).Arguments[0]);
        Assert.Equal(following, f.Settings.Current.FollowPlayer);
        Assert.Equal(progression, f.Settings.Current.ProgressionEnabled);
        Assert.Null(f.Tracking.Position);
        Assert.False(f.Tracking.Running);
        Assert.False(f.Tracking.Found);
        Assert.False(f.View.Active); // The next map is selected even while another tab is active.
        Assert.False(f.Tracking.CanSelectProgression("verteron"));
        await f.OnUi(() => f.View.OnProgressionTarget("verteron", "unconfirmed-pet", 10, 20,
            "Synthetic target", f.Tracking.ContextGeneration));
        Assert.Null(f.Targets.Progression);
        Assert.Empty(f.Provider.GetRequiredService<ExplorationService>().Completed);
        Assert.Equal(0, f.Capture.Diagnostics.Grabs);
    }

    [Fact]
    public async Task ConfirmedContextBeforeRenderChoosesTheInitialMapWithoutPrematureJavascript()
    {
        await using var f = new Scenario();
        f.Settings.Current.FollowPlayer = false;
        f.Settings.Current.ProgressionEnabled = false;
        f.World.ObserveOwn(1010, 1, At);
        await f.Render();

        Assert.Equal("verteron", f.ShownMap);
        Assert.Empty(f.Js.Calls);
        await f.Ready();
        Assert.Equal("verteron", Assert.Single(f.Js.Shows).Arguments[0]);
        Assert.Equal("verteron", f.Settings.Current.LastMap);
        Assert.Equal(0, f.Capture.Diagnostics.Grabs);
    }

    [Fact]
    public async Task OwnReceivedBeforeJavascriptIsReadyUsesTheNewestSelectionOnFirstShow()
    {
        await using var f = new Scenario();
        f.Settings.Current.FollowPlayer = false;
        f.Settings.Current.ProgressionEnabled = false;
        await f.Render();
        await f.Own(1110, 1);
        await f.Own(1010, 2);
        Assert.Empty(f.Js.Calls);

        await f.Ready();

        Assert.Equal("verteron", f.ShownMap);
        Assert.Equal("verteron", Assert.Single(f.Js.Shows).Arguments[0]);
    }

    [Fact]
    public async Task RepeatedOwnAndSameMapDungeonRefinementDoNotUndoManualBrowsing()
    {
        await using var f = new Scenario();
        f.Settings.Current.FollowPlayer = false;
        f.Settings.Current.ProgressionEnabled = false;
        f.World.ObserveOwn(1110, 1, At);
        await f.RenderReady();
        await f.Switch("verteron");
        f.Js.Calls.Clear();

        await f.Own(1110, 2);
        await f.OnUi(() => f.World.ObserveDungeon(new(77, DungeonRunState.Process, 0, 0), 2, At.AddSeconds(3)));
        Assert.Equal(77, f.World.Snapshot.DungeonId); // A real same-map publication, not an ignored message.
        await f.Own(1110, 3);

        Assert.Equal("verteron", f.ShownMap);
        Assert.Equal("verteron", f.Settings.Current.LastMap);
        Assert.Empty(f.Js.Shows);
        Assert.Equal(0, f.Capture.Diagnostics.Grabs);
    }

    [Fact]
    public async Task UnsupportedVisitDoesNotGuessAMapButItsConfirmedReturnEndsManualBrowsing()
    {
        await using var f = new Scenario();
        f.Settings.Current.FollowPlayer = false;
        f.Settings.Current.ProgressionEnabled = false;
        f.World.ObserveOwn(1110, 1, At);
        await f.RenderReady();
        await f.Switch("verteron");
        f.Js.Calls.Clear();

        await f.Own(600001, 2);
        Assert.Equal("verteron", f.ShownMap);
        Assert.Empty(f.Js.Shows);
        await f.Own(1110, 3);

        Assert.Equal("altgard", f.ShownMap);
        Assert.Equal("altgard", Assert.Single(f.Js.Shows).Arguments[0]);
        Assert.Equal(0, f.Capture.Diagnostics.Grabs);
    }

    [Fact]
    public async Task NetworkMapSwitchPreservesTheSavedViewportWhenFollowingAndProgressionAreDisabled()
    {
        await using var f = new Scenario();
        f.Settings.Current.FollowPlayer = false;
        f.Settings.Current.ProgressionEnabled = false;
        var viewport = new MapViewport(123, 456, -2.25);
        f.Settings.Current.MapViewports["verteron"] = viewport;
        _ = f.Tracking; // Subscribe before the synthetic position and MapView receive the same Own.
        var supplied = false;
        void SupplyFreshPosition()
        {
            if (f.World.Snapshot.MapDataId != 1010) return;
            f.SetFreshPosition("verteron");
            Assert.True(f.Tracking.CanUsePlayerPosition);
            supplied = true;
        }
        f.World.Changed += SupplyFreshPosition;
        try
        {
            await f.RenderReady();
            f.Js.Calls.Clear();

            await f.Own(1010, 1);

            Assert.True(supplied);
            var show = Assert.Single(f.Js.Shows);
            Assert.Equal("verteron", show.Arguments[0]);
            Assert.Null(show.Arguments[2]);
            var options = JsonSerializer.SerializeToElement(show.Arguments[3]);
            Assert.Equal(viewport, options.GetProperty("savedView").Deserialize<MapViewport>());
            Assert.Equal(0, f.Capture.Diagnostics.Grabs);
        }
        finally { f.World.Changed -= SupplyFreshPosition; }
    }

    [Theory]
    [InlineData("transition")]
    [InlineData("gap")]
    [InlineData("unknown")]
    [InlineData("unsupported")]
    [InlineData("network-off")]
    public async Task UnconfirmedUnsupportedOrDisabledNetworkCannotReplaceTheBrowsedMap(string state)
    {
        await using var f = new Scenario();
        f.World.ObserveOwn(1110, 1, At);
        await f.RenderReady();
        await f.Switch("verteron");
        f.Js.Calls.Clear();

        await f.OnUi(() =>
        {
            switch (state)
            {
                case "transition": f.World.Invalidate(WorldContextStatus.Transitioning, 2, "synthetic-transition", At); break;
                case "gap": f.World.Invalidate(WorldContextStatus.Stale, 2, "synthetic-gap", At); break;
                case "unknown": f.World.ObserveOwn(999, 2, At); break;
                case "unsupported": f.World.ObserveOwn(600001, 2, At); break;
                case "network-off":
                    f.Settings.Update(s => s.LootTrackingEnabled = false);
                    f.World.ObserveOwn(20, 2, At);
                    break;
            }
        });

        Assert.Equal("verteron", f.ShownMap);
        Assert.Equal("verteron", f.Settings.Current.LastMap);
        Assert.Empty(f.Js.Shows);
        Assert.Equal(0, f.Capture.Diagnostics.Grabs);
    }

    [Theory]
    [InlineData("follow")]
    [InlineData("network")]
    [InlineData("follow-with-progression")]
    [InlineData("progression-with-follow")]
    public async Task ReenablingFollowingOrNetworkSynchronizesAnAlreadyConfirmedLocation(string enable)
    {
        await using var f = new Scenario();
        f.Settings.Current.FollowPlayer = enable is not ("follow" or "follow-with-progression");
        f.Settings.Current.ProgressionEnabled = enable == "follow-with-progression";
        f.Settings.Current.LootTrackingEnabled = enable != "network";
        f.World.ObserveOwn(1110, 1, At);
        await f.RenderReady();
        await f.Switch("verteron");
        f.Js.Calls.Clear();

        await f.OnUi(() => f.Settings.Update(s =>
        {
            if (enable is "follow" or "follow-with-progression") s.FollowPlayer = true;
            else if (enable == "progression-with-follow") s.ProgressionEnabled = true;
            else s.LootTrackingEnabled = true;
        }));

        Assert.Equal("altgard", f.ShownMap);
        Assert.Equal("altgard", Assert.Single(f.Js.Shows).Arguments[0]);
        Assert.Null(f.Tracking.Position);
        Assert.Equal(0, f.Capture.Diagnostics.Grabs);
    }

    [Fact]
    public async Task DelayedManualShowCannotOverwriteANewerConfirmedOwnMap()
    {
        await using var f = new Scenario();
        f.World.ObserveOwn(1110, 1, At);
        await f.RenderReady();
        f.Js.Calls.Clear();
        var blocked = f.Js.BlockNextShow("verteron");
        var oldShow = f.Switch("verteron");
        await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await f.Own(20, 2);
        Assert.Equal("abyss-reshanta-a", f.ShownMap);
        blocked.Release.TrySetResult();
        await oldShow.WaitAsync(TimeSpan.FromSeconds(5));
        await f.Flush();

        Assert.Equal("abyss-reshanta-a", f.ShownMap);
        Assert.Equal("abyss-reshanta-a", f.Settings.Current.LastMap);
        Assert.Equal(33, f.MarkerCount); // The old Verteron reply contains 22 markers.
        Assert.Equal(0, f.Capture.Diagnostics.Grabs);
    }

    [Fact]
    public async Task RepeatedOwnCannotCancelAnInFlightManualMapShow()
    {
        await using var f = new Scenario();
        f.World.ObserveOwn(1110, 1, At);
        await f.RenderReady();
        var blocked = f.Js.BlockNextShow("verteron");
        var manual = f.Switch("verteron");
        await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await f.Own(1110, 2);
        blocked.Release.TrySetResult();
        await manual.WaitAsync(TimeSpan.FromSeconds(5));
        await f.Flush();

        Assert.Equal("verteron", f.ShownMap);
        Assert.Equal(22, f.MarkerCount);
    }

    [Fact]
    public async Task DelayedExplorationPushCannotSendOldGuidanceAfterTheNewMapHasFinished()
    {
        await using var f = new Scenario();
        f.Settings.Current.ProgressionEnabled = true;
        f.World.ObserveOwn(1110, 1, At);
        await f.RenderReady();
        f.Js.Calls.Clear();
        var blocked = f.Js.BlockNextExploration();
        var oldShow = f.Switch("verteron");
        await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await f.Own(20, 2);
        Assert.Equal("abyss-reshanta-a", f.ShownMap);
        Assert.Equal(33, f.MarkerCount);
        var currentGuidanceCalls = f.Js.Calls.Count(call => call.Name == "soulcrestMap.setProgression");
        Assert.True(currentGuidanceCalls > 0);
        blocked.Release.TrySetResult();
        await oldShow.WaitAsync(TimeSpan.FromSeconds(5));
        await f.Flush();

        Assert.Equal(currentGuidanceCalls, f.Js.Calls.Count(call => call.Name == "soulcrestMap.setProgression"));
        Assert.Equal("abyss-reshanta-a", f.Settings.Current.LastMap);
        Assert.Equal(0, f.Capture.Diagnostics.Grabs);
    }

    [Fact]
    public async Task DelayedCategoryRestoreCannotHideCategoriesOfTheNewMap()
    {
        await using var f = new Scenario();
        f.World.ObserveOwn(1110, 1, At);
        await f.RenderReady();
        f.Settings.Current.MapCategories["verteron"] = [];
        f.Js.ShowReplies["verteron"] = """
            {"groups":[{"name":"Locations","categories":[
             {"index":41,"name":"First","count":1,"hidden":false,"color":"#fff","icon":null},
             {"index":42,"name":"Second","count":1,"hidden":false,"color":"#fff","icon":null}]}],"regions":0,"markers":22}
            """;
        f.Js.ShowReplies["abyss-reshanta-a"] = """
            {"groups":[{"name":"Locations","categories":[
             {"index":42,"name":"Abyss location","count":1,"hidden":false,"color":"#fff","icon":null}]}],"regions":0,"markers":33}
            """;
        f.Js.Calls.Clear();
        var blocked = f.Js.BlockNextCategory();
        var oldShow = f.Switch("verteron");
        await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await f.Own(20, 2);
        Assert.Equal("abyss-reshanta-a", f.ShownMap);
        Assert.Contains(42, f.VisibleCategories);
        var categoryCalls = f.Js.Calls.Count(call => call.Name == "soulcrestMap.setCategoryVisible");
        Assert.Equal(1, categoryCalls);
        blocked.Release.TrySetResult();
        await oldShow.WaitAsync(TimeSpan.FromSeconds(5));
        await f.Flush();

        Assert.Equal(categoryCalls, f.Js.Calls.Count(call => call.Name == "soulcrestMap.setCategoryVisible"));
        Assert.Contains(42, f.VisibleCategories);
        Assert.Equal("abyss-reshanta-a", f.Settings.Current.LastMap);
    }

    [Fact]
    public async Task AuthorizedViewportIsSavedOnceAndPassedBackOnTheNextVisit()
    {
        await using var f = new Scenario();
        await f.RenderReady();
        var revision = Revision(Assert.Single(f.Js.Shows));
        var changes = 0;
        f.Settings.Changed += CountChange;
        void CountChange() => changes++;

        await f.OnUi(() =>
        {
            f.View.MapViewChanged("altgard", 123, 456, -2.25, revision);
            f.View.MapViewChanged("altgard", 123, 456, -2.25, revision);
        });

        Assert.Equal(new MapViewport(123, 456, -2.25), f.Settings.Current.MapViewports["altgard"]);
        Assert.Equal(1, changes);
        f.Settings.Changed -= CountChange;
        await f.Switch("verteron");
        await f.Switch("altgard");
        var options = JsonSerializer.SerializeToElement(f.Js.Shows.Last().Arguments[3]);
        Assert.Equal(new MapViewport(123, 456, -2.25), options.GetProperty("savedView").Deserialize<MapViewport>());
    }

    [Fact]
    public async Task OutgoingMapCanSaveItsLastRevisionButNotOverwriteANewerVisit()
    {
        await using var f = new Scenario();
        await f.RenderReady();
        var oldAltgard = Revision(Assert.Single(f.Js.Shows));
        await f.Switch("verteron");
        var verteron = Revision(f.Js.Shows.Last());
        await f.OnUi(() => f.View.MapViewChanged("altgard", 100, 200, 1, oldAltgard));
        Assert.Equal("verteron", f.Settings.Current.LastMap);
        Assert.Equal(new MapViewport(100, 200, 1), f.Settings.Current.MapViewports["altgard"]);

        await f.Switch("altgard");
        var newAltgard = Revision(f.Js.Shows.Last());
        Assert.True(newAltgard > oldAltgard);
        await f.OnUi(() =>
        {
            f.View.MapViewChanged("altgard", 900, 900, 9, oldAltgard);
            f.View.MapViewChanged("verteron", 300, 400, 2, verteron);
        });
        Assert.Equal(new MapViewport(100, 200, 1), f.Settings.Current.MapViewports["altgard"]);
        Assert.Equal(new MapViewport(300, 400, 2), f.Settings.Current.MapViewports["verteron"]);
        await f.OnUi(() => f.View.MapViewChanged("altgard", 500, 600, 3, newAltgard));
        Assert.Equal(new MapViewport(500, 600, 3), f.Settings.Current.MapViewports["altgard"]);
    }

    [Fact]
    public async Task UnknownUnshownOrNonfiniteViewportCallbacksCannotWriteSettings()
    {
        await using var f = new Scenario();
        await f.RenderReady();
        var revision = Revision(Assert.Single(f.Js.Shows));
        var before = File.ReadAllBytes(AppPaths.SettingsFile);
        var changes = 0;
        f.Settings.Changed += () => changes++;

        await f.OnUi(() =>
        {
            f.View.MapViewChanged("unknown", 10, 20, 1, revision);
            f.View.MapViewChanged("verteron", 10, 20, 1, revision);
            f.View.MapViewChanged("altgard", 10, 20, 1, revision + 100);
            f.View.MapViewChanged("altgard", double.NaN, 20, 1, revision);
            f.View.MapViewChanged("altgard", 10, double.PositiveInfinity, 1, revision);
            f.View.MapViewChanged("altgard", 10, 20, double.NegativeInfinity, revision);
        });

        Assert.Empty(f.Settings.Current.MapViewports);
        Assert.Equal(0, changes);
        Assert.Equal(before, File.ReadAllBytes(AppPaths.SettingsFile));
    }

    private static readonly DateTimeOffset At = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static long Revision(JsCall show) => JsonSerializer.SerializeToElement(show.Arguments[3]).GetProperty("revision").GetInt64();

    private sealed class Scenario : IAsyncDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-network-map-view-" + Guid.NewGuid().ToString("N"));
        private readonly string? _oldMapData = Environment.GetEnvironmentVariable("SOULCREST_MAPDATA");
        private readonly string _oldLanguage = UiText.Language;
        private readonly Dictionary<string, byte[]?> _backups = [];
        private HtmlRenderer Renderer { get; }
        public ServiceProvider Provider { get; }
        public SettingsService Settings => Provider.GetRequiredService<SettingsService>();
        public MapTrackingService Tracking => Provider.GetRequiredService<MapTrackingService>();
        public MapTargetsService Targets => Provider.GetRequiredService<MapTargetsService>();
        public GameCaptureService Capture => Provider.GetRequiredService<GameCaptureService>();
        public WorldContextService World { get; } = new(InstanceCatalog.FromJson("""
            {"schemaVersion":1,"maps":[
             {"id":1110,"names":{"en":"Synthetic Altgard"},"kind":"openWorld","soulcrestMapId":"altgard","sourceRefs":["synthetic-map"]},
             {"id":1010,"names":{"en":"Synthetic Verteron"},"kind":"openWorld","soulcrestMapId":"verteron","sourceRefs":["synthetic-map"]},
             {"id":20,"names":{"en":"Synthetic Abyss"},"kind":"instance","category":"Abyss","soulcrestMapId":"abyss-reshanta-a","sourceRefs":["synthetic-map"]},
             {"id":600001,"names":{"en":"Synthetic unsupported dungeon"},"kind":"instance","category":"Party","sourceRefs":["synthetic-map"]},
             {"id":999,"names":{"en":null},"kind":"unknown","sourceRefs":["synthetic-map"]}],
             "dungeons":[{"id":77,"mapIds":[1110],"names":{"en":"Synthetic layer"},"category":"InstanceLayer","mode":"Normal","instanceLayer":true,"sourceRefs":["synthetic-layer"]}]}
            """));
        public RecordingJs Js { get; } = new();
        public MapView View { get; private set; } = null!;
        public string ShownMap => (string)Field("_mapId")!;
        public int MarkerCount => (int)Field("_markerCount")!;
        public IReadOnlySet<int> VisibleCategories => (HashSet<int>)Field("_visible")!;

        public Scenario()
        {
            Directory.CreateDirectory(_folder);
            File.WriteAllText(Path.Combine(_folder, "manifest.json"), """
                {"maps":[{"id":"altgard","label":"Altgard","group":"Asmodian"},
                 {"id":"verteron","label":"Verteron","group":"Elyos"},
                 {"id":"abyss-reshanta-a","label":"Abyss","group":"Abyss"}]}
                """);
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _folder);
            foreach (var path in new[] { AppPaths.SettingsFile, AppPaths.TargetsFile, AppPaths.RoutesFile,
                AppPaths.ProgressFile, AppPaths.LearnedPetsFile, AppPaths.LearnedNamesFile,
                Path.Combine(AppPaths.DataDirectory, "routes.seeded.json"), Path.Combine(AppPaths.DataDirectory, "leveling-progress.json") })
            {
                _backups.Add(path, File.Exists(path) ? File.ReadAllBytes(path) : null);
                if (File.Exists(path)) File.Delete(path);
            }
            JsonFile.Save(AppPaths.SettingsFile, new AppSettings
            {
                LootTrackingEnabled = true, FollowPlayer = true, ProgressionEnabled = false,
                LastMap = "altgard", CheckUpdatesOnStart = false, UiLanguage = "en"
            });
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<SettingsService>();
            services.AddSingleton<GameCaptureService>();
            services.AddSingleton<ProgressService>();
            services.AddSingleton(World);
            services.AddSingleton<NetworkLootService>();
            services.AddSingleton<TrackerService>();
            services.AddSingleton<MapTrackingService>();
            services.AddSingleton<MapTargetsService>();
            services.AddSingleton(sp => new AlertsService(Path.Combine(_folder, "alerts.json"), runTimer: false));
            services.AddSingleton(new ExplorationService(Path.Combine(_folder, "exploration.json"), []));
            services.AddSingleton<UiState>();
            services.AddSingleton<UpdateService>();
            services.AddSingleton<IJSRuntime>(Js);
            Provider = services.BuildServiceProvider();
            Renderer = new HtmlRenderer(Provider, Provider.GetRequiredService<ILoggerFactory>());
        }

        public async Task Render()
        {
            await Renderer.Dispatcher.InvokeAsync(async () => await Renderer.RenderComponentAsync<CaptureView>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                { [nameof(CaptureView.Capture)] = (Action<MapView>)(view => View = view) })));
        }
        public async Task RenderReady() { await Render(); await Ready(); }
        public Task Ready() => Renderer.Dispatcher.InvokeAsync(() => (Task)typeof(MapView)
            .GetMethod("OnAfterRenderAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(View, [true])!);
        public Task Switch(string map) => Renderer.Dispatcher.InvokeAsync(() => (Task)typeof(MapView)
            .GetMethod("SwitchMap", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(View, [map, null])!);
        public Task Own(int map, long generation) => OnUi(() => World.ObserveOwn(map, generation, At.AddSeconds(generation)));
        public void SetFreshPosition(string map)
        {
            var now = DateTimeOffset.UtcNow;
            var generation = Tracking.ContextGeneration;
            void Set(string name, object value) => typeof(MapTrackingService).GetProperty(name)!.SetValue(Tracking, value);
            Set(nameof(MapTrackingService.Running), true);
            Set(nameof(MapTrackingService.Found), true);
            Set(nameof(MapTrackingService.Position), new PlayerPosition(map, 987, 654, now) { ContextGeneration = generation });
            Set(nameof(MapTrackingService.DetectedMap), new DetectedMapContext(map, false, now) { ContextGeneration = generation });
        }
        public async Task OnUi(Action action) { await Renderer.Dispatcher.InvokeAsync(action); await Flush(); }
        public Task Flush() => Renderer.Dispatcher.InvokeAsync(() => { });
        private object? Field(string name) => typeof(MapView).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(View);

        public async ValueTask DisposeAsync()
        {
            Js.ReleaseAll();
            await Renderer.DisposeAsync();
            await Provider.DisposeAsync();
            foreach (var (path, bytes) in _backups)
                if (bytes is null)
                {
                    if (File.Exists(path)) File.Delete(path);
                }
                else File.WriteAllBytes(path, bytes);
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _oldMapData);
            UiText.Language = _oldLanguage;
            Directory.Delete(_folder, true);
        }
    }

    private sealed class CaptureView : ComponentBase
    {
        [Parameter] public Action<MapView> Capture { get; set; } = default!;
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<MapView>(0);
            builder.AddComponentReferenceCapture(1, view => Capture((MapView)view));
            builder.CloseComponent();
        }
    }

    private sealed record JsCall(string Name, object?[] Arguments);
    private sealed class PendingCall(string identifier, string? map = null)
    {
        public string Identifier { get; } = identifier;
        public string? Map { get; } = map;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Used;
    }
    private sealed class RecordingJs : IJSRuntime
    {
        private readonly List<PendingCall> _blocks = [];
        public Dictionary<string, string> ShowReplies { get; } = [];
        public ConcurrentQueue<JsCall> Calls { get; } = new();
        public JsCall[] Shows => Calls.Where(call => call.Name == "soulcrestMap.show").ToArray();
        public PendingCall BlockNextShow(string map) => Block("soulcrestMap.show", map);
        public PendingCall BlockNextExploration() => Block("soulcrestMap.setExploration");
        public PendingCall BlockNextCategory() => Block("soulcrestMap.setCategoryVisible");
        private PendingCall Block(string identifier, string? map = null)
        {
            var pending = new PendingCall(identifier, map);
            _blocks.Add(pending);
            return pending;
        }
        public void ReleaseAll() { foreach (var pending in _blocks) pending.Release.TrySetResult(); }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            Calls.Enqueue(new(identifier, args ?? []));
            var block = _blocks.FirstOrDefault(item => item.Identifier == identifier
                && (item.Map is null || Equals(item.Map, args?.FirstOrDefault()))
                && Interlocked.CompareExchange(ref item.Used, 1, 0) == 0);
            if (typeof(TValue) != typeof(string))
            {
                if (block is null) return ValueTask.FromResult(default(TValue)!);
                block.Entered.TrySetResult();
                return new(Complete<TValue>(block, default(TValue)!));
            }
            if (identifier != "soulcrestMap.show") return ValueTask.FromResult((TValue)(object)"[]");
            var map = (string)args![0]!;
            var markers = map switch { "altgard" => 11, "verteron" => 22, _ => 33 };
            var json = ShowReplies.GetValueOrDefault(map)
                ?? JsonSerializer.Serialize(new { groups = Array.Empty<object>(), regions = 0, markers });
            if (block is null) return ValueTask.FromResult((TValue)(object)json);
            block.Entered.TrySetResult();
            return new(Complete(block, (TValue)(object)json));
        }
        private static async Task<TValue> Complete<TValue>(PendingCall pending, TValue result)
        {
            await pending.Release.Task;
            return result;
        }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<TValue>(identifier, args);
    }
}
