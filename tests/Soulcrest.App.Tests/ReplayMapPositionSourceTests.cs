using System.Diagnostics;
using System.Reflection;
using OpenCvSharp;
using Soulcrest.App.Capture;
using Soulcrest.App.Services;
using Soulcrest.Ocr.MapTracking;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class ReplayMapPositionSourceTests
{
    [Fact]
    public void DelayedFixKeepsItsOwnPictureTimestampAndDoesNotChangeTheExistingPlayerPosition()
    {
        using var f = new Fixture(); var player = f.Tracking.Position;
        var stamp = Stopwatch.GetTimestamp() - Stopwatch.Frequency / 4;
        var at = DateTimeOffset.UtcNow.AddMilliseconds(-250);
        f.Fix(stamp, 1, at);
        var sample = Assert.Single(f.Samples);
        Assert.Equal(stamp, sample.CapturedAt); Assert.Equal(at, sample.OriginalAt);
        Assert.Equal(150, sample.X); Assert.Equal(100, sample.Y); Assert.Same(player, f.Tracking.Position);
        Assert.Equal(0, f.Capture.Diagnostics.Grabs);
    }

    [Fact]
    public void CapturedMetadataCanCrossAWorkerBoundaryWithoutUsingThatThreadsCaptureState()
    {
        using var f = new Fixture(); var priorStamp = GameCaptureService.GrabbedFrameAt; var priorTiming = GameCaptureService.GrabbedTiming;
        try
        {
            GameCaptureService.GrabbedFrameAt = Stopwatch.GetTimestamp() - Stopwatch.Frequency / 4;
            GameCaptureService.GrabbedTiming = default(CaptureTiming) with { SourceFrameId = 17, CaptureSessionId = 23 };
            var captured = MapTrackingService.ReplayCaptureFrame.Current();
            var worker = new Thread(() =>
            {
                // A fresh detection worker has no capture metadata for the image acquired by its caller.
                if (GameCaptureService.GrabbedFrameAt == 0) f.Fix(captured);
            });
            worker.Start(); Assert.True(worker.Join(TimeSpan.FromSeconds(3)));
            var sample = Assert.Single(f.Samples);
            Assert.Equal(captured.CapturedAt, sample.CapturedAt); Assert.Equal(captured.OriginalAt, sample.OriginalAt);
            Assert.Equal(17, sample.FrameId); Assert.Equal(23, sample.CaptureSessionId);
        }
        finally { GameCaptureService.GrabbedFrameAt = priorStamp; GameCaptureService.GrabbedTiming = priorTiming; }
    }

    [Fact]
    public void FlowIsOnlyPublishedAfterAFollowingFixAndRepeatedFrameIdsAreDeduplicated()
    {
        using var f = new Fixture(); var stamp = Stopwatch.GetTimestamp() - Stopwatch.Frequency;
        f.Fix(stamp, 1); f.Flow(stamp + Stopwatch.Frequency / 10, 2);
        Assert.Single(f.Samples);
        f.Fix(stamp + Stopwatch.Frequency / 5, 3);
        Assert.Equal(new[] { ReplayPositionProvenance.Matched, ReplayPositionProvenance.Flow, ReplayPositionProvenance.Matched }, f.Samples.Select(row => row.Provenance));
        f.Fix(stamp + Stopwatch.Frequency / 4, 3); Assert.Equal(3, f.Samples.Count);
        Assert.False(f.CallbackUnderMapLock); Assert.Equal(147, f.Samples[1].X);
    }

    [Theory]
    [InlineData("world-map")]
    [InlineData("esc")]
    [InlineData("generation")]
    [InlineData("flow-broken")]
    public void AnInvalidatedVisualContextDiscardsProvisionalFlow(string cause)
    {
        using var f = new Fixture(); var stamp = Stopwatch.GetTimestamp() - Stopwatch.Frequency;
        f.Fix(stamp, 1); f.Flow(stamp + Stopwatch.Frequency / 10, 2);
        switch (cause)
        {
            case "world-map": f.Set("_worldMapOpen", true); break;
            case "esc": f.Tracking.UpdateEscMenuContext(true); break;
            case "generation": f.Set("_mapContextGeneration", f.Tracking.ContextGeneration + 1); break;
            default: f.Set("_flowBroken", true); break;
        }
        f.Flow(stamp + Stopwatch.Frequency / 5, 3);
        Assert.Single(f.Samples); Assert.NotEmpty(f.Gaps); Assert.False(f.CallbackUnderMapLock);
    }

    [Fact]
    public void RegularTwoSecondIdleDetectionPlusProcessingCanStillConfirmTheProvisionalFlow()
    {
        using var f = new Fixture(); var start = Stopwatch.GetTimestamp(); var now = start;
        f.Tracking.ReplayTimestampForTests = () => now;
        f.Fix(start, 1);
        now = start + (long)(Stopwatch.Frequency * 2.2);
        f.Flow(start + (long)(Stopwatch.Frequency * 2.1), 2);
        Assert.Single(f.Samples);
        f.Fix(start + (long)(Stopwatch.Frequency * 2.15), 3);
        Assert.Equal(3, f.Samples.Count); Assert.Equal(ReplayPositionProvenance.Flow, f.Samples[1].Provenance);
        Assert.Empty(f.Gaps);
    }

    [Fact]
    public void MoreThanFiveSecondsWithoutAFixDiscardsTheUnconfirmedFlowTail()
    {
        using var f = new Fixture(); var start = Stopwatch.GetTimestamp(); var now = start;
        f.Tracking.ReplayTimestampForTests = () => now;
        f.Fix(start, 1); now = start + Stopwatch.Frequency; f.Flow(now, 2);
        now = start + Stopwatch.Frequency * 6; f.Flow(now, 3);
        Assert.Single(f.Samples); Assert.NotEmpty(f.Gaps);
        f.Fix(now, 4); Assert.Equal(2, f.Samples.Count);
        Assert.All(f.Samples, row => Assert.Equal(ReplayPositionProvenance.Matched, row.Provenance));
    }

    [Fact]
    public void AFollowingFixAfterLongCaptureSilenceCannotPromoteTheOldFlowTail()
    {
        using var f = new Fixture(); var start = Stopwatch.GetTimestamp(); var now = start;
        f.Tracking.ReplayTimestampForTests = () => now;
        f.Fix(start, 1); now = start + Stopwatch.Frequency; f.Flow(now, 2);
        now = start + Stopwatch.Frequency * 6; f.Fix(now, 3);
        Assert.Equal(2, f.Samples.Count); Assert.NotEmpty(f.Gaps);
        Assert.All(f.Samples, row => Assert.Equal(ReplayPositionProvenance.Matched, row.Provenance));
        Assert.NotEqual(f.Samples[0].SegmentId, f.Samples[1].SegmentId);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly byte[]? _backup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        public GameCaptureService Capture { get; } = new();
        public MapTrackingService Tracking { get; }
        public List<ReplayMapPositionSample> Samples { get; } = [];
        public List<ReplayPositionGapReason> Gaps { get; } = [];
        public bool CallbackUnderMapLock;
        private readonly object _mapGate;
        public Fixture()
        {
            var settings = new SettingsService(); settings.Current.LootTrackingEnabled = false;
            Tracking = new(settings, new ProgressService(), Capture);
            _mapGate = typeof(MapTrackingService).GetField("_flowGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Tracking)!;
            Set(nameof(MapTrackingService.Running), true); Set(nameof(MapTrackingService.Found), true); Set("_flowBroken", false);
            var now = DateTimeOffset.UtcNow;
            Set(nameof(MapTrackingService.Position), new PlayerPosition("sample", 999, 888, now) { ContextGeneration = Tracking.ContextGeneration });
            Set(nameof(MapTrackingService.DetectedMap), new DetectedMapContext("sample", false, now) { ContextGeneration = Tracking.ContextGeneration });
            Set(nameof(MapTrackingService.Placement), Placement());
            Tracking.ReplayPositionsConfirmed += rows => { CallbackUnderMapLock |= Monitor.IsEntered(_mapGate); Samples.AddRange(rows); };
            Tracking.ReplayPositionInterrupted += reason => { CallbackUnderMapLock |= Monitor.IsEntered(_mapGate); Gaps.Add(reason); };
        }
        private MapPlacement Placement() => new("sample", new MapFix(1, 0, 0, 0, 1, 0, 50, 60, .25, false),
            1, new System.Drawing.Rectangle(0, 0, 300, 200), new Point2d(150, 100)) { ContextGeneration = Tracking.ContextGeneration };
        public void Fix(long stamp, long frame, DateTimeOffset? at = null) => Fix(new(stamp, frame, 1,
            at ?? DateTimeOffset.UtcNow - Stopwatch.GetElapsedTime(stamp)));
        public void Fix(MapTrackingService.ReplayCaptureFrame frame) => Tracking.ObserveReplayFix("sample", Placement().Fix,
            1, new Point2d(150, 100), frame, Tracking.ContextGeneration);
        public void Flow(long stamp, long frame)
        {
            Tracking.TryApplyFlowShift(new FrameShift(1, 0, 3, 0, 1, 0), stamp, Tracking.ContextGeneration);
            Tracking.ObserveReplayFlow(new(stamp, frame, 1,
                DateTimeOffset.UtcNow - Stopwatch.GetElapsedTime(stamp)), Tracking.ContextGeneration);
        }
        public void Set(string name, object value)
        {
            if (name.StartsWith('_')) typeof(MapTrackingService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Tracking, value);
            else typeof(MapTrackingService).GetProperty(name)!.SetValue(Tracking, value);
        }
        public void Dispose()
        {
            Tracking.Dispose(); Capture.Dispose();
            if (_backup is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, _backup);
        }
    }
}
