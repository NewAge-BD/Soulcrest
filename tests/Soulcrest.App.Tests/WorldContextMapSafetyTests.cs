using System.Drawing;
using System.Reflection;
using OpenCvSharp;
using Soulcrest.App.Capture;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Instances;
using Soulcrest.Ocr.MapTracking;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class WorldContextMapSafetyTests : IDisposable
{
    private readonly Dictionary<string, byte[]?> _backups = new[] { AppPaths.SettingsFile, AppPaths.TargetsFile }
        .ToDictionary(path => path, path => File.Exists(path) ? File.ReadAllBytes(path) : null);
    private static WorldContextService Context() => new(InstanceCatalog.FromJson("""
        {"schemaVersion":1,"maps":[
        {"id":100,"kind":"openWorld","soulcrestMapId":"synthetic-world","sourceRefs":["synthetic"]},
        {"id":200,"kind":"instance","sourceRefs":["synthetic"]},
        {"id":300,"kind":"instance","category":"Abyss","soulcrestMapId":"synthetic-abyss","sourceRefs":["synthetic"]}],"dungeons":[
        {"id":3000,"mapIds":[300],"category":"Abyss","mode":"Normal","instanceLayer":false,"sourceRefs":["synthetic"]}]}
        """));

    private static SettingsService NetworkSettings()
    {
        var settings = new SettingsService(); settings.Current.LootTrackingEnabled = true; return settings;
    }

    [Fact]
    public void WithoutLocationFactsNetworkTrackingKeepsTheVisualMapTracker()
    {
        // Public builds ship without the private instance catalog (user decision 2026-10-21).
        var context = new WorldContextService(InstanceCatalog.Empty);
        using var capture = new GameCaptureService();
        using var tracking = new MapTrackingService(NetworkSettings(), new ProgressService(), capture, context);
        Assert.False(context.HasLocationFacts);
        Assert.Equal(MapTrackingMode.VisualFallback, tracking.Mode);
        context.ObserveOwn(100, 1, DateTimeOffset.UtcNow);
        Assert.Equal(WorldContextStatus.Confirmed, context.Snapshot.Status);
        Assert.Equal(MapTrackingMode.VisualFallback, tracking.Mode);
        Assert.False(tracking.NetworkSuspended);
        Assert.Null(tracking.NetworkInInstance);
        Assert.True(tracking.IsCurrentVisualContext(tracking.ContextGeneration));
        Assert.True(Context().HasLocationFacts);
    }

    [Fact]
    public void TransitionInvalidatesCoordinatesFramesAndCallbacksWhileRetainingTargets()
    {
        var context = Context();
        using var capture = new GameCaptureService();
        using var tracking = new MapTrackingService(NetworkSettings(), new ProgressService(), capture, context);
        context.ObserveOwn(100, 1, DateTimeOffset.UtcNow);
        Publish(tracking, 300);
        Assert.True(tracking.CanSelectProgression("synthetic-world"));
        Assert.False(tracking.CanSelectProgression("browsed-other-map"));
        Assert.False(tracking.IsCurrentProgressionContext(null));
        var generation = tracking.ContextGeneration;
        var player = tracking.Position;
        context.Invalidate(WorldContextStatus.Transitioning, 1, "synthetic-load");
        Assert.True(tracking.ContextGeneration > generation);
        Assert.False(tracking.Found);
        Assert.Null(tracking.Placement);
        Assert.Null(tracking.DetectedMap);
        Assert.Same(player, tracking.Position); // Last-known grey marker only.
        Assert.False(tracking.CanUsePlayerPosition);
        Assert.False(tracking.CanSelectProgression("synthetic-world"));
        Assert.False(tracking.IsCurrentProgressionContext(generation));
        Assert.Equal(0, capture.Diagnostics.Grabs);
    }

    [Fact]
    public void PublishedSourceRevisionBlocksOldEvidenceBeforeItsMapCallbackCanAcquireTheMapLock()
    {
        var context = Context();
        using var capture = new GameCaptureService();
        using var tracking = new MapTrackingService(NetworkSettings(), new ProgressService(), capture, context);
        using var targets = new MapTargetsService();
        context.ObserveOwn(100, 1, DateTimeOffset.UtcNow); Publish(tracking, 100);
        var oldGeneration = tracking.ContextGeneration;
        var oldPosition = tracking.Position!;
        var flowGate = typeof(MapTrackingService).GetField("_flowGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tracking)!;
        Task? publication = null;
        Monitor.Enter(flowGate);
        try
        {
            publication = Task.Run(() => context.Invalidate(WorldContextStatus.Stale, 2, "synthetic-gap"));
            Assert.True(SpinWait.SpinUntil(() => context.Snapshot.Status == WorldContextStatus.Stale, TimeSpan.FromSeconds(5)));
            Assert.False(publication.IsCompleted); // Its Changed callback is deliberately waiting for us.
            Assert.Equal(oldGeneration, tracking.ContextGeneration);
            Assert.False(tracking.IsCurrentVisualContext(oldGeneration));
            Assert.False(tracking.CanUsePlayerPosition);
            Assert.False(tracking.CanSelectProgression("synthetic-world"));
            Assert.False(tracking.IsCurrentProgressionContext(oldGeneration));
            Assert.False(tracking.TryApplyFlowShift(new FrameShift(1, 0, 3, 0, 1, 4), 124, oldGeneration));
            Assert.False(tracking.TryArrive(targets, "not-present", oldPosition));
            Assert.False(tracking.TrySetProgression(targets,
                new("progression", "synthetic-world", 100, 100, "Synthetic", "Locations", null), "synthetic-world", oldGeneration));
            Assert.Null(targets.Progression);
        }
        finally
        {
            Monitor.Exit(flowGate);
            Assert.True(publication?.Wait(TimeSpan.FromSeconds(5)) ?? false);
        }
        Assert.True(tracking.ContextGeneration > oldGeneration);
        Publish(tracking, 100);
        Assert.False(tracking.CanUsePlayerPosition); // Enabled network tracking never silently falls back.
        context.ObserveOwn(100, 3, DateTimeOffset.UtcNow);
        Publish(tracking, 100);
        Assert.True(tracking.CanUsePlayerPosition);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ArrivalNotifiesOutsideSourceMapAndArrivalLocksAndStopsTheOldBatchAfterReentrantStopOrOwn(bool newOwn)
    {
        var context = Context();
        var settings = NetworkSettings(); settings.Current.TargetArrivalRadius = 40;
        var progress = new ProgressService();
        using var capture = new GameCaptureService();
        using var tracking = new MapTrackingService(settings, progress, capture, context);
        using var targets = new MapTargetsService();
        using var network = new NetworkLootService(progress, worldContext: context);
        using var arrival = new NavigationArrivalService(tracking, targets, settings);
        var list = (List<MapTarget>)typeof(MapTargetsService).GetField("_targets", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(targets)!;
        list.Clear();
        list.Add(new("first", "synthetic-world", 100, 100, "First", "Locations", null));
        list.Add(new("second", "synthetic-world", 100, 100, "Second", "Locations", null, After: "first"));
        network.ObserveIdentity("synthetic-world-stream", new(101, 501, 100, "SyntheticAlpha"), DateTimeOffset.UtcNow, 100);
        PublishAndCheck(tracking, arrival, 300);
        var notifications = 0;
        targets.Changed += () =>
        {
            notifications++;
            foreach (var (owner, field) in new (object, string)[] { (context, "_gate"), (tracking, "_flowGate"), (arrival, "_gate") })
                Assert.False(Monitor.IsEntered(owner.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!));
            var reset = Task.Run(() =>
            {
                network.Stop();
                if (newOwn) network.ObserveIdentity("synthetic-world-stream-next", new(102, 501, 101, "SyntheticBeta"), DateTimeOffset.UtcNow, 100);
            });
            Assert.True(reset.Wait(TimeSpan.FromSeconds(5))); // Cross-thread callbacks cannot invert the source locks.
        };
        PublishAndCheck(tracking, arrival, 100);
        Assert.Equal(1, notifications);
        Assert.Equal("second", Assert.Single(targets.Targets).Id);
        Assert.False(tracking.CanUsePlayerPosition);
    }

    [Fact]
    public void ArrivalRejectsAReplacedPositionEvenWithinTheSameVisualEpoch()
    {
        var context = Context();
        using var capture = new GameCaptureService();
        using var tracking = new MapTrackingService(NetworkSettings(), new ProgressService(), capture, context);
        using var targets = new MapTargetsService();
        var list = (List<MapTarget>)typeof(MapTargetsService).GetField("_targets", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(targets)!;
        list.Clear(); list.Add(new("synthetic-target", "synthetic-world", 100, 100, "Synthetic", "Locations", null));
        context.ObserveOwn(100, 1, DateTimeOffset.UtcNow); Publish(tracking, 100);
        var oldPosition = tracking.Position!;
        Publish(tracking, 300);
        Assert.False(tracking.TryArrive(targets, "synthetic-target", oldPosition));
        Assert.Single(targets.Targets);
    }

    [Fact]
    public void StaleRequiresExplicitNetworkDisableAndNewVisualEvidenceWhileHealthyOwnDoesNotExpire()
    {
        var context = Context();
        var settings = NetworkSettings();
        using var capture = new GameCaptureService();
        using var tracking = new MapTrackingService(settings, new ProgressService(), capture, context);
        context.ObserveOwn(100, 1, DateTimeOffset.UtcNow.AddDays(-1));
        Publish(tracking, 300);
        Assert.True(tracking.CanUsePlayerPosition); // Healthy Own has no TTL.
        context.Invalidate(WorldContextStatus.Stale, 1, "synthetic-gap");
        Assert.False(tracking.CanUsePlayerPosition);
        Assert.Null(tracking.NetworkInInstance);
        Publish(tracking, 300);
        Assert.False(tracking.CanUsePlayerPosition);
        settings.Update(s => s.LootTrackingEnabled = false);
        Assert.False(tracking.CanUsePlayerPosition);
        Publish(tracking, 300);
        Assert.True(tracking.CanUsePlayerPosition);
        Assert.True(tracking.CanSelectProgression("synthetic-world"));
        settings.Update(s => s.LootTrackingEnabled = true);
        context.ObserveOwn(999, 2, DateTimeOffset.UtcNow);
        Publish(tracking, 300);
        Assert.False(tracking.CanUsePlayerPosition); // Unmapped confirmed identity cannot borrow a map.
        context.ObserveOwn(200, 3, DateTimeOffset.UtcNow);
        Publish(tracking, 300);
        Assert.True(tracking.InInstance);
        Assert.False(tracking.CanUsePlayerPosition);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EntryDoesNotCompleteArmedOrLevelingTargetsAndFreshDepartureRestoresArrival(bool leveling)
    {
        var context = Context();
        var settings = NetworkSettings();
        settings.Current.TargetArrivalRadius = 40;
        using var capture = new GameCaptureService();
        using var tracking = new MapTrackingService(settings, new ProgressService(), capture, context);
        using var targets = new MapTargetsService();
        var list = (List<MapTarget>)typeof(MapTargetsService).GetField("_targets", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(targets)!;
        list.Clear();
        list.Add(new("synthetic-target", "synthetic-world", 100, 100, "Synthetic stop", "Locations", null,
            RouteId: leveling ? "synthetic-route" : null, Leveling: leveling, StopIndex: leveling ? 0 : null));
        using var arrival = new NavigationArrivalService(tracking, targets, settings);
        context.ObserveOwn(100, 1, DateTimeOffset.UtcNow);
        PublishAndCheck(tracking, arrival, 300);
        Assert.Single(targets.Targets);
        context.Invalidate(WorldContextStatus.Transitioning, 1, "synthetic-transition");
        Assert.Single(targets.Targets);
        context.ObserveOwn(100, 2, DateTimeOffset.UtcNow);
        PublishAndCheck(tracking, arrival, 100);
        PublishAndCheck(tracking, arrival, 100);
        Assert.Single(targets.Targets);
        PublishAndCheck(tracking, arrival, 300);
        PublishAndCheck(tracking, arrival, 100);
        Assert.Empty(targets.Targets);
        Assert.Equal(0, capture.Diagnostics.Grabs);
    }

    [Fact]
    public void EscEventRevokesPlacementBeforeAnySubscriberOrOldFrameCanUseIt()
    {
        var context = Context();
        using var capture = new GameCaptureService();
        using var tracking = new MapTrackingService(NetworkSettings(), new ProgressService(), capture, context);
        context.ObserveOwn(100, 1, DateTimeOffset.UtcNow); Publish(tracking, 100);
        var before = tracking.ContextGeneration;
        tracking.Changed += () => { Assert.False(tracking.Found); Assert.Null(tracking.Placement); };
        tracking.UpdateEscMenuContext(true);
        Assert.True(tracking.ContextGeneration > before);
        Assert.Null(tracking.Placement);
        Assert.False(tracking.CanUsePlayerPosition);
    }

    [Fact]
    public void DelayedOpticalFlowCannotMoveANewerWorldsPlacementOrRebaseItsDrift()
    {
        var context = Context();
        using var capture = new GameCaptureService();
        using var tracking = new MapTrackingService(NetworkSettings(), new ProgressService(), capture, context);
        context.ObserveOwn(100, 1, DateTimeOffset.UtcNow); Publish(tracking, 100);
        var oldGeneration = tracking.ContextGeneration;
        context.Invalidate(WorldContextStatus.Stale, 2, "synthetic-gap");
        context.ObserveOwn(100, 3, DateTimeOffset.UtcNow); Publish(tracking, 100);
        var placement = tracking.Placement;
        Assert.False(tracking.TryApplyFlowShift(new FrameShift(1, 0, 120, 0, 1, 70), 123, oldGeneration));
        Assert.Same(placement, tracking.Placement);
        Assert.True(tracking.TryApplyFlowShift(new FrameShift(1, 0, 3, 0, 1, 4), 124, tracking.ContextGeneration));
        Assert.Equal(placement!.Fix.Tx + 3, tracking.Placement!.Fix.Tx);
        Assert.Equal(placement.Fix.Ty + 4, tracking.Placement.Fix.Ty);
    }

    [Fact]
    public void EscKeepsTheSameWorldAndDoesNotInventANewEntryBlockForAValidLevelingVisit()
    {
        var context = Context();
        var settings = NetworkSettings(); settings.Current.TargetArrivalRadius = 40;
        using var capture = new GameCaptureService();
        using var tracking = new MapTrackingService(settings, new ProgressService(), capture, context);
        using var targets = new MapTargetsService();
        var list = (List<MapTarget>)typeof(MapTargetsService).GetField("_targets", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(targets)!;
        list.Clear(); list.Add(new("synthetic-target", "synthetic-world", 100, 100, "Synthetic stop", "Locations", null,
            RouteId: "synthetic-route", Leveling: true, StopIndex: 0));
        using var arrival = new NavigationArrivalService(tracking, targets, settings);
        context.ObserveOwn(100, 1, DateTimeOffset.UtcNow);
        PublishAndCheck(tracking, arrival, 300); // Positive departure clears the original world-entry block.
        tracking.UpdateEscMenuContext(true);
        tracking.UpdateEscMenuContext(false);
        PublishAndCheck(tracking, arrival, 100);
        Assert.Empty(targets.Targets);
    }

    [Fact]
    public void SameBindingAbyssRefinementDoesNotInventANewLevelingEntryBoundary()
    {
        var context = Context();
        var settings = NetworkSettings(); settings.Current.TargetArrivalRadius = 40;
        using var capture = new GameCaptureService();
        using var tracking = new MapTrackingService(settings, new ProgressService(), capture, context);
        using var targets = new MapTargetsService();
        var list = (List<MapTarget>)typeof(MapTargetsService).GetField("_targets", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(targets)!;
        list.Clear(); list.Add(new("synthetic-target", "synthetic-abyss", 100, 100, "Synthetic stop", "Locations", null,
            RouteId: "synthetic-route", Leveling: true, StopIndex: 0));
        using var arrival = new NavigationArrivalService(tracking, targets, settings);
        context.ObserveOwn(300, 1, DateTimeOffset.UtcNow);
        Publish(tracking, 300, "synthetic-abyss");
        typeof(NavigationArrivalService).GetMethod("Check", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(arrival, null);
        var revision = context.Snapshot.Generation;
        context.ObserveDungeon(new(3000, Soulcrest.Core.Network.DungeonRunState.Process, 0, 0), 1, DateTimeOffset.UtcNow);
        Assert.True(context.Snapshot.Generation > revision);
        Assert.Equal(1, context.Snapshot.BindingGeneration);
        Publish(tracking, 100, "synthetic-abyss");
        typeof(NavigationArrivalService).GetMethod("Check", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(arrival, null);
        Assert.Empty(targets.Targets);
    }

    private static void PublishAndCheck(MapTrackingService tracking, NavigationArrivalService arrival, double x)
    {
        Publish(tracking, x);
        typeof(NavigationArrivalService).GetMethod("Check", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(arrival, null);
    }

    [Fact]
    public void SupportedAbyssRequiresItsOwnFreshMapAndEpochWhileKeepingNativeInstanceKind()
    {
        var context = Context();
        using var capture = new GameCaptureService();
        using var tracking = new MapTrackingService(NetworkSettings(), new ProgressService(), capture, context);
        context.ObserveOwn(300, 1, DateTimeOffset.UtcNow);
        Publish(tracking, 100, "synthetic-world");
        Assert.False(tracking.CanUsePlayerPosition);
        Publish(tracking, 100, "synthetic-abyss");
        Assert.True(tracking.CanSelectProgression("synthetic-abyss"));
        Assert.False(tracking.InInstance); // Effective open-PvP HUD rules, not a native reclassification.
        Assert.Equal(WorldContextKind.Instance, context.Snapshot.Kind);
        var oldGeneration = tracking.ContextGeneration;
        context.Invalidate(WorldContextStatus.Transitioning, 1, "synthetic-load");
        context.ObserveOwn(300, 2, DateTimeOffset.UtcNow);
        Assert.False(tracking.CanUsePlayerPosition);
        Assert.False(tracking.IsCurrentProgressionContext(oldGeneration));
        Publish(tracking, 100, "synthetic-abyss");
        Assert.True(tracking.CanSelectProgression("synthetic-abyss"));
    }

    private static void Publish(MapTrackingService tracking, double x, string map = "synthetic-world")
    {
        var at = DateTimeOffset.UtcNow;
        Set(tracking, nameof(MapTrackingService.Running), true);
        Set(tracking, nameof(MapTrackingService.Found), true);
        Set(tracking, nameof(MapTrackingService.Position), new PlayerPosition(map, x, 100, at)
        { ContextGeneration = tracking.ContextGeneration });
        Set(tracking, nameof(MapTrackingService.DetectedMap), new DetectedMapContext(map, false, at)
        { ContextGeneration = tracking.ContextGeneration });
        Set(tracking, nameof(MapTrackingService.Placement), new MapPlacement(map,
            new MapFix(1, 0, 0, 0, 1, 0, 50, 60, 0, false), 1, new Rectangle(0, 0, 300, 200), new Point2d(150, 100))
        { ContextGeneration = tracking.ContextGeneration });
    }

    private static void Set(MapTrackingService tracking, string name, object value) =>
        typeof(MapTrackingService).GetProperty(name)!.SetValue(tracking, value);

    public void Dispose()
    {
        foreach (var (path, bytes) in _backups)
            if (bytes is null) File.Delete(path); else File.WriteAllBytes(path, bytes);
    }
}
