using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using OpenCvSharp;
using Soulcrest.App.Capture;
using Soulcrest.App.Services;
using Soulcrest.Core.Instances;
using Soulcrest.Core.Pets;
using Soulcrest.Ocr.MapTracking;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class NetworkMapWorkTests
{
    [Fact]
    public async Task SuspendedProductionLoopHasNoCaptureProbeOrReferencePreparationAcrossTheOldTwoSecondDeadline()
    {
        using var f = new Fixture();
        f.Context.ObserveOwn(200, 1, DateTimeOffset.UtcNow);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Tracking.NetworkWaitStartedForTests = () => waiting.TrySetResult();
        f.Tracking.Start();
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(f.Tracking.NetworkSuspended);
        await Task.Delay(TimeSpan.FromMilliseconds(2200));
        Assert.Equal(0, f.Readbacks);
        Assert.Empty(f.Searches);
        Assert.Null(Field<Task?>(f.Tracking, "_preparing"));
        Assert.Empty(Field<Dictionary<string, MapLocator>>(f.Tracking, "_coarse"));
        Assert.Equal(0, f.Capture.Diagnostics.Grabs);
        Assert.Equal(0, f.Capture.Diagnostics.FramesArrived);
        f.Tracking.Shutdown();
        await Field<Task>(f.Tracking, "_loop").WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("stale")]
    [InlineData("transition")]
    [InlineData("unmapped")]
    [InlineData("missing-service")]
    public async Task EnabledUnusableNetworkContextsWaitForEventsWithoutStartingVisualWork(string state)
    {
        using var f = new Fixture(withContext: state != "missing-service");
        if (state == "stale") f.Context.Invalidate(WorldContextStatus.Stale, 1, "synthetic-gap");
        if (state == "transition") f.Context.Invalidate(WorldContextStatus.Transitioning, 1, "synthetic-entry");
        if (state == "unmapped") f.Context.ObserveOwn(999, 1, DateTimeOffset.UtcNow);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Tracking.NetworkWaitStartedForTests = () => waiting.TrySetResult();
        f.Tracking.Start();
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(MapTrackingMode.NetworkSuspended, f.Tracking.Mode);
        Assert.Equal(0, f.Readbacks);
        Assert.Empty(f.Searches);
        Assert.False(f.Tracking.IsCurrentProgressionContext(null));
        Assert.False(f.Tracking.CanSelectProgression("synthetic-a"));
        using var frame = new Mat(300, 300, MatType.CV_8UC3, Scalar.All(70));
        f.Tracking.ProcessMiniMapFrame(new(0, 0, 300, 300), new(0, 0, 300, 300), frame, CancellationToken.None);
        f.Tracking.UpdateEscMenuContext(true);
        Assert.False(f.Tracking.EscMenuOpen);
        Assert.Null(f.Tracking.Position);
        Assert.Empty(f.Searches);
        f.Tracking.Shutdown();
        await Field<Task>(f.Tracking, "_loop").WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task QueuedDetectionCannotReadBackAfterAnInstanceEntryAndNewOwnWakesWithoutAProbeDelay()
    {
        using var f = new Fixture(references: true);
        f.Context.ObserveOwn(100, 1, DateTimeOffset.UtcNow);
        var queued = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Tracking.ScheduleDetectionForTests = (action, _) => queued.TrySetResult(action) ? done.Task : Task.Run(action);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Tracking.NetworkWaitStartedForTests = () => waiting.TrySetResult();
        f.Tracking.Start();
        var oldJob = await queued.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Context.ObserveOwn(200, 2, DateTimeOffset.UtcNow);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stoppedCount = f.Readbacks;
        Assert.True(stoppedCount > 0);
        oldJob();
        done.TrySetResult();
        Assert.Equal(stoppedCount, f.Readbacks);
        Assert.Empty(f.Searches);
        var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.AfterReadback = () => resumed.TrySetResult();
        f.Context.ObserveOwn(300, 3, DateTimeOffset.UtcNow);
        await resumed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(MapTrackingMode.NetworkMap, f.Tracking.Mode);
        Assert.True(f.Readbacks > stoppedCount);
        Assert.Equal(0, f.Capture.Diagnostics.Grabs);
        f.Tracking.Shutdown();
        await Field<Task>(f.Tracking, "_loop").WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void SupportedNetworkMinimapNeverSearchesOtherMapsOrInfersAnInstanceAfterRepeatedImageMisses()
    {
        using var f = new Fixture(references: true);
        f.Context.ObserveOwn(100, 1, DateTimeOffset.UtcNow);
        using var frame = new Mat(300, 300, MatType.CV_8UC3, Scalar.All(70));
        for (var i = 0; i < 9; i++)
            f.Tracking.ProcessMiniMapFrame(new(0, 0, 300, 300), new(0, 0, 300, 300), frame, CancellationToken.None);
        var minimapSearches = f.Searches.Where(s => !s.World).ToArray();
        Assert.Equal(9, minimapSearches.Length);
        Assert.All(minimapSearches, search => Assert.Equal("synthetic-a", search.Map));
        Assert.Equal("synthetic-a", Field<string>(f.Tracking, "_currentMap"));
        f.Tracking.UpdateInstanceContext(false, false, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
        Assert.False(f.Tracking.InInstance);
        Assert.False(f.Tracking.Paused);
        Assert.Equal(MapTrackingMode.NetworkMap, f.Tracking.Mode);
        Assert.Equal(0, f.Readbacks);
    }

    [Fact]
    public void ExplicitVisualFallbackIgnoresAnOldConfirmedNetworkInstanceAndRetainsTheOldLossThresholds()
    {
        using var f = new Fixture(networkEnabled: false);
        f.Context.ObserveOwn(200, 1, DateTimeOffset.UtcNow);
        Assert.True(f.Tracking.UsesVisualFallback);
        Assert.Null(f.Tracking.NetworkInInstance);
        f.Tracking.UpdateInstanceContext(false, false, TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(9));
        Assert.False(f.Tracking.InInstance);
        f.Tracking.UpdateInstanceContext(false, false, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(10));
        Assert.True(f.Tracking.InInstance);
        f.Tracking.UpdateInstanceContext(true, false, TimeSpan.Zero, null);
        f.Tracking.UpdateInstanceContext(false, false, TimeSpan.FromSeconds(29), null);
        Assert.False(f.Tracking.InInstance);
        f.Tracking.UpdateInstanceContext(false, false, TimeSpan.FromSeconds(30), null);
        Assert.True(f.Tracking.InInstance);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RapidSavedSwitchRoundTripRevokesOldFramesBeforeBlockedSettingsCallbacksCanRun(bool withContext)
    {
        using var f = new Fixture(networkEnabled: false, withContext: withContext);
        var generation = f.Tracking.ContextGeneration;
        var gate = Field<object>(f.Tracking, "_flowGate");
        Task? enabled = null, disabled = null;
        Monitor.Enter(gate);
        try
        {
            enabled = Task.Run(() => f.Settings.Update(s => s.LootTrackingEnabled = true));
            Assert.True(SpinWait.SpinUntil(() => f.Settings.NetworkTrackingRevision == 1, TimeSpan.FromSeconds(5)));
            disabled = Task.Run(() => f.Settings.Update(s => s.LootTrackingEnabled = false));
            Assert.True(SpinWait.SpinUntil(() => f.Settings.NetworkTrackingRevision == 2, TimeSpan.FromSeconds(5)));
            Assert.False(f.Settings.Current.LootTrackingEnabled);
            Assert.Equal(generation, f.Tracking.ContextGeneration);
            Assert.False(f.Tracking.IsCurrentVisualContext(generation));
            Assert.False(f.Tracking.IsCurrentProgressionContext(generation));
            Assert.False(f.Tracking.IsCurrentProgressionContext(null));
            Assert.False(f.Tracking.CanSelectProgression("synthetic-a"));
        }
        finally { Monitor.Exit(gate); }
        await Task.WhenAll(enabled!, disabled!).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(f.Tracking.ContextGeneration > generation);
        Assert.False(f.Tracking.IsCurrentVisualContext(generation));
        Assert.DoesNotContain("NetworkTrackingRevision", File.ReadAllText(AppPaths.SettingsFile));
    }

    [Fact]
    public void UnrelatedSettingsDoNotRestartVisualWorkOrAdvanceTheNetworkSwitchRevision()
    {
        using var f = new Fixture(networkEnabled: false);
        var generation = f.Tracking.ContextGeneration;
        f.Settings.Update(s => s.MiniMapZoom = .75);
        Assert.Equal(0, f.Settings.NetworkTrackingRevision);
        Assert.Equal(generation, f.Tracking.ContextGeneration);
        Assert.True(f.Tracking.IsCurrentVisualContext(generation));
    }

    private static T Field<T>(object value, string field) =>
        (T)value.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;

    private sealed class Fixture : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-network-map-work-" + Guid.NewGuid().ToString("N"));
        private readonly string? _oldMapData = Environment.GetEnvironmentVariable("SOULCREST_MAPDATA");
        private readonly byte[]? _settings = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        private int _readbacks;
        public int Readbacks => Volatile.Read(ref _readbacks);
        public List<(string Map, bool World)> Searches { get; } = [];
        public Action? AfterReadback { get; set; }
        public SettingsService Settings { get; }
        public GameCaptureService Capture { get; } = new();
        public WorldContextService Context { get; } = new(InstanceCatalog.FromJson("""
            {"schemaVersion":1,"maps":[
            {"id":100,"kind":"openWorld","soulcrestMapId":"synthetic-a","sourceRefs":["synthetic"]},
            {"id":200,"kind":"instance","category":"FieldEvent","sourceRefs":["synthetic"]},
            {"id":300,"kind":"openWorld","soulcrestMapId":"synthetic-b","sourceRefs":["synthetic"]}],"dungeons":[]}
            """));
        public MapTrackingService Tracking { get; }

        public Fixture(bool networkEnabled = true, bool withContext = true, bool references = false)
        {
            Directory.CreateDirectory(_folder);
            File.WriteAllText(Path.Combine(_folder, "manifest.json"), """
                {"version":"synthetic-network-work","maps":[
                {"id":"synthetic-a","label":"Synthetic A","tiles":"a/{z}/{x}/{y}.png","refZoom":0,"size":300,"maxNativeZoom":0},
                {"id":"synthetic-b","label":"Synthetic B","tiles":"b/{z}/{x}/{y}.png","refZoom":0,"size":300,"maxNativeZoom":0}]}
                """);
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _folder);
            Settings = new SettingsService();
            Settings.Current.LootTrackingEnabled = networkEnabled;
            Settings.Current.LastMap = "synthetic-b";
            Settings.Current.MapRegion = new Rectangle(0, 0, 300, 300);
            var progress = new ProgressService(Path.Combine(_folder, "progress.json"), new PetCatalog([]));
            Tracking = new(Settings, progress, Capture, withContext ? Context : null);
            Tracking.CaptureFrameForTests = (area, scale) =>
            {
                Interlocked.Increment(ref _readbacks);
                GameCaptureService.GrabbedFrameAt = Stopwatch.GetTimestamp();
                GameCaptureService.GrabbedGameWindowOnly = true;
                GameCaptureService.GrabbedTiming = default;
                AfterReadback?.Invoke();
                return new Mat(Math.Max(1, (int)(area.Height * scale)), Math.Max(1, (int)(area.Width * scale)), MatType.CV_8UC3, Scalar.All(70));
            };
            Tracking.SearchStartedForTests = (map, world) => Searches.Add((map, world));
            if (references)
            {
                var pixels = new byte[300 * 300]; new Random(7319).NextBytes(pixels);
                using var gray = Mat.FromPixelData(300, 300, MatType.CV_8UC1, pixels);
                foreach (var id in new[] { "synthetic-a", "synthetic-b" })
                foreach (var field in new[] { "_coarse", "_fine" })
                    Field<Dictionary<string, MapLocator>>(Tracking, field).Add(id, new(MapReference.Build(id, gray, 1)));
            }
        }

        public void Dispose()
        {
            Tracking.Shutdown();
            Tracking.Dispose();
            Capture.Dispose();
            Environment.SetEnvironmentVariable("SOULCREST_MAPDATA", _oldMapData);
            if (_settings is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, _settings);
            Directory.Delete(_folder, recursive: true);
        }
    }
}
