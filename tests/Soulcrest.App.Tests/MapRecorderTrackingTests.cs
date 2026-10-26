using System.Drawing;
using System.Reflection;
using OpenCvSharp;
using Soulcrest.App.Capture;
using Soulcrest.App.Services;
using Soulcrest.Core.Instances;
using Soulcrest.Ocr.MapTracking;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class MapRecorderTrackingTests : IDisposable
{
    private readonly byte[]? _settings = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;

    private static SettingsService Settings(bool manual = false)
    {
        var settings = new SettingsService();
        settings.Current.MapTrackingActive = manual;
        settings.Current.LootTrackingEnabled = false;
        return settings;
    }

    private static void FakeLoop(MapTrackingService tracking, Action? started = null)
    {
        tracking.TrackingPrerequisitesForTests = () => true;
        tracking.TrackingLoopForTests = token =>
        {
            started?.Invoke();
            return Task.Delay(Timeout.Infinite, token);
        };
    }

    [Fact]
    public void RecorderLeasesShareOneLoopWithoutChangingSavedManualPreference()
    {
        var settings = Settings();
        using var capture = new GameCaptureService();
        using var tracking = new MapTrackingService(settings, new ProgressService(), capture);
        var starts = 0;
        FakeLoop(tracking, () => Interlocked.Increment(ref starts));
        var before = File.Exists(AppPaths.SettingsFile) ? File.ReadAllText(AppPaths.SettingsFile) : null;
        using var first = tracking.AcquireRecorderTracking();
        using var second = tracking.AcquireRecorderTracking();
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref starts) == 1, TimeSpan.FromSeconds(3)));
        Assert.True(tracking.Running);
        Assert.False(settings.Current.MapTrackingActive);
        Assert.Equal(before, File.Exists(AppPaths.SettingsFile) ? File.ReadAllText(AppPaths.SettingsFile) : null);
        first.Dispose();
        first.Dispose();
        Assert.True(tracking.Running);
        second.Dispose();
        Assert.False(tracking.Running);
        Assert.Equal(1, starts);
        Assert.Equal(0, capture.Diagnostics.Grabs);
    }

    [Fact]
    public void ManualStartAndStopRemainIndependentOfAnActiveRecorderRequest()
    {
        var settings = Settings();
        using var capture = new GameCaptureService();
        using var tracking = new MapTrackingService(settings, new ProgressService(), capture);
        FakeLoop(tracking);
        using var recorder = tracking.AcquireRecorderTracking();
        tracking.Start();
        Assert.True(settings.Current.MapTrackingActive);
        recorder.Dispose();
        Assert.True(tracking.Running);
        using var another = tracking.AcquireRecorderTracking();
        tracking.Stop();
        Assert.False(settings.Current.MapTrackingActive);
        Assert.True(tracking.Running);
        another.Dispose();
        Assert.False(tracking.Running);
        Assert.Equal(0, capture.Diagnostics.Grabs);
    }

    [Fact]
    public void ShutdownRevokesOldLeasesWithoutLettingTheirDisposalStopANewRecorder()
    {
        var settings = Settings();
        using var capture = new GameCaptureService();
        using var tracking = new MapTrackingService(settings, new ProgressService(), capture);
        FakeLoop(tracking);
        using var old = tracking.AcquireRecorderTracking();
        tracking.Shutdown();
        Assert.False(tracking.Running);
        using var current = tracking.AcquireRecorderTracking();
        old.Dispose();
        Assert.True(tracking.Running);
        current.Dispose();
        Assert.False(tracking.Running);
        Assert.False(settings.Current.MapTrackingActive);
    }

    [Fact]
    public void MissingSetupSuppliesNoRecorderPositionAndStartsNoCapture()
    {
        var settings = Settings();
        settings.Current.MapRegion = null;
        using var capture = new GameCaptureService();
        using var tracking = new MapTrackingService(settings, new ProgressService(), capture);
        using var recorder = tracking.AcquireRecorderTracking();
        Assert.False(tracking.Running);
        Assert.False(tracking.TryGetRecorderPositionSnapshot(out _));
        Assert.False(settings.Current.MapTrackingActive);
        Assert.True(tracking.StatusIsError);
        Assert.Equal(0, capture.Diagnostics.Grabs);
    }

    [Fact]
    public void AtomicSnapshotContainsMatchingQualityAndGenerationsAndLoadRevokesIt()
    {
        var settings = Settings();
        settings.Current.LootTrackingEnabled = true;
        var world = Context();
        using var capture = new GameCaptureService();
        using var tracking = new MapTrackingService(settings, new ProgressService(), capture, world);
        world.ObserveOwn(100, 7, DateTimeOffset.UtcNow);
        Publish(tracking, DateTimeOffset.UtcNow);
        Assert.True(tracking.TryGetRecorderPositionSnapshot(out var snapshot));
        Assert.Same(tracking.Position, snapshot.Position);
        Assert.Equal(tracking.ContextGeneration, snapshot.MapGeneration);
        Assert.Equal(world.Snapshot.Generation, snapshot.WorldGeneration);
        Assert.Equal(7, snapshot.BindingGeneration);
        Assert.Equal(50, snapshot.Inliers);
        Assert.Equal(0.25, snapshot.MatchError);
        world.Invalidate(WorldContextStatus.Transitioning, 8, "synthetic-load");
        Assert.False(tracking.TryGetRecorderPositionSnapshot(out _));
        Assert.Equal(100, snapshot.Position.X);
        Assert.Equal(7, snapshot.BindingGeneration);
        Assert.Equal(0, capture.Diagnostics.Grabs);
    }

    [Fact]
    public void PublicPackageWithoutLocationFactsKeepsOwnAuthorityOnAFreshVisualPosition()
    {
        var settings = Settings(); settings.Current.LootTrackingEnabled = true;
        var world = new WorldContextService(InstanceCatalog.Empty);
        using var capture = new GameCaptureService();
        using var tracking = new MapTrackingService(settings, new ProgressService(), capture, world);
        world.ObserveOwn(100, 7, DateTimeOffset.UtcNow);
        Publish(tracking, DateTimeOffset.UtcNow);
        Assert.False(world.HasLocationFacts);
        Assert.Equal(MapTrackingMode.VisualFallback, tracking.Mode);
        Assert.True(tracking.TryGetRecorderPositionSnapshot(out var snapshot));
        Assert.Equal(7, snapshot.BindingGeneration);
        Assert.Equal(world.Snapshot.Generation, snapshot.WorldGeneration);
        world.Invalidate(WorldContextStatus.Stale, 8, "synthetic-gap");
        Assert.False(tracking.TryGetRecorderPositionSnapshot(out _));
        Assert.Equal(0, capture.Diagnostics.Grabs);
    }

    [Fact]
    public void ExplicitVisualFallbackCannotReuseAnOldNetworkBinding()
    {
        var world = new WorldContextService(InstanceCatalog.Empty);
        using var capture = new GameCaptureService();
        using var tracking = new MapTrackingService(Settings(), new ProgressService(), capture, world);
        world.ObserveOwn(100, 7, DateTimeOffset.UtcNow);
        Publish(tracking, DateTimeOffset.UtcNow);
        Assert.True(tracking.TryGetRecorderPositionSnapshot(out var snapshot));
        Assert.Null(snapshot.BindingGeneration);
        Assert.Null(snapshot.WorldGeneration);
        Assert.Equal(0, capture.Diagnostics.Grabs);
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("future")]
    [InlineData("world-map")]
    [InlineData("wrong-map")]
    [InlineData("generation")]
    [InlineData("negative")]
    [InlineData("nan")]
    public void UnqualifiedPositionsNeverEnterTheRecorder(string invalid)
    {
        using var capture = new GameCaptureService();
        using var tracking = new MapTrackingService(Settings(), new ProgressService(), capture);
        var at = DateTimeOffset.UtcNow;
        Publish(tracking, invalid == "stale" ? at.AddSeconds(-3) : invalid == "future" ? at.AddSeconds(3) : at);
        if (invalid == "world-map") Set(tracking, "_worldMapOpen", true);
        if (invalid == "wrong-map") Set(tracking, nameof(MapTrackingService.DetectedMap), tracking.DetectedMap! with { MapId = "other" });
        if (invalid == "generation") Set(tracking, nameof(MapTrackingService.Position), tracking.Position! with { ContextGeneration = -1 });
        if (invalid == "negative") Set(tracking, nameof(MapTrackingService.Position), tracking.Position! with { X = -1 });
        if (invalid == "nan") Set(tracking, nameof(MapTrackingService.Position), tracking.Position! with { Y = double.NaN });
        Assert.False(tracking.TryGetRecorderPositionSnapshot(out _));
        Assert.Equal(0, capture.Diagnostics.Grabs);
    }

    [Fact]
    public void PublishedWorldRevisionRejectsOldPositionBeforeItsMapCallbackRuns()
    {
        var settings = Settings(); settings.Current.LootTrackingEnabled = true;
        var world = Context();
        using var capture = new GameCaptureService();
        using var tracking = new MapTrackingService(settings, new ProgressService(), capture, world);
        world.ObserveOwn(100, 1, DateTimeOffset.UtcNow);
        Publish(tracking, DateTimeOffset.UtcNow);
        var gate = typeof(MapTrackingService).GetField("_flowGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tracking)!;
        Task? publication = null;
        Monitor.Enter(gate);
        try
        {
            publication = Task.Run(() => world.Invalidate(WorldContextStatus.Stale, 2, "synthetic-gap"));
            Assert.True(SpinWait.SpinUntil(() => world.Snapshot.Status == WorldContextStatus.Stale, TimeSpan.FromSeconds(3)));
            Assert.False(tracking.TryGetRecorderPositionSnapshot(out _));
        }
        finally
        {
            Monitor.Exit(gate);
            Assert.True(publication?.Wait(TimeSpan.FromSeconds(3)) ?? false);
        }
    }

    private static WorldContextService Context() => new(InstanceCatalog.FromJson("""
        {"schemaVersion":1,"maps":[{"id":100,"kind":"openWorld","soulcrestMapId":"synthetic",
        "sourceRefs":["synthetic"]}],"dungeons":[]}
        """));

    private static void Publish(MapTrackingService tracking, DateTimeOffset at)
    {
        Set(tracking, nameof(MapTrackingService.Running), true);
        Set(tracking, nameof(MapTrackingService.Found), true);
        Set(tracking, nameof(MapTrackingService.Position), new PlayerPosition("synthetic", 100, 200, at)
        { ContextGeneration = tracking.ContextGeneration });
        Set(tracking, nameof(MapTrackingService.DetectedMap), new DetectedMapContext("synthetic", false, at)
        { ContextGeneration = tracking.ContextGeneration });
        Set(tracking, nameof(MapTrackingService.Placement), new MapPlacement("synthetic",
            new MapFix(1, 0, 0, 0, 1, 0, 50, 60, 0.25, false), 1, new Rectangle(0, 0, 300, 200), new Point2d(150, 100))
        { ContextGeneration = tracking.ContextGeneration });
    }

    private static void Set(MapTrackingService tracking, string name, object value)
    {
        if (name.StartsWith('_')) typeof(MapTrackingService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(tracking, value);
        else typeof(MapTrackingService).GetProperty(name)!.SetValue(tracking, value);
    }

    public void Dispose()
    {
        if (_settings is null) File.Delete(AppPaths.SettingsFile);
        else File.WriteAllBytes(AppPaths.SettingsFile, _settings);
    }
}
