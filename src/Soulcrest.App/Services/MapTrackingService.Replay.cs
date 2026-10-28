using System.Diagnostics;
using Soulcrest.App.Capture;
using Soulcrest.Ocr.MapTracking;

namespace Soulcrest.App.Services;

public sealed partial class MapTrackingService
{
    internal readonly record struct ReplayCaptureFrame(long CapturedAt, long FrameId, long CaptureSessionId, DateTimeOffset OriginalAt)
    {
        internal static ReplayCaptureFrame Current()
        {
            var stamp = GameCaptureService.GrabbedFrameAt; var now = Stopwatch.GetTimestamp();
            return new(stamp, GameCaptureService.GrabbedTiming.SourceFrameId, GameCaptureService.GrabbedTiming.CaptureSessionId,
                stamp > 0 && stamp <= now ? DateTimeOffset.UtcNow - Stopwatch.GetElapsedTime(stamp, now) : default);
        }
    }
    public event Action<IReadOnlyList<ReplayMapPositionSample>>? ReplayPositionsConfirmed;
    public event Action<ReplayPositionGapReason>? ReplayPositionInterrupted;
    private sealed record ReplayNotification(ReplayPositionGapReason? Gap, ReplayMapPositionSample[]? Samples);
    private readonly Queue<ReplayNotification> _replayNotifications = [];
    private readonly object _replayNotificationGate = new();
    private readonly List<ReplayMapPositionSample> _replayFlow = [];
    internal Func<long>? ReplayTimestampForTests { get; set; }
    private long ReplayNow => ReplayTimestampForTests?.Invoke() ?? Stopwatch.GetTimestamp();
    internal static readonly TimeSpan ReplayIdentityHoldLimit = TimeSpan.FromSeconds(5);
    private long _replaySegment, _replayLastCommitted, _replayLastFrame, _replayFixCapturedAt;
    private string? _replayMap;
    private long _replayCaptureSession, _replayCommittedFrameId;
    private long? _replayWorldGeneration, _replayBindingGeneration;

    // Called under _flowGate. Notifications are drained only after the visual lock is released.
    private void InvalidateReplayPosition(ReplayPositionGapReason reason)
    {
        var had = _replayMap is not null || _replayFlow.Count > 0;
        _replayFlow.Clear(); _replayMap = null; _replayFixCapturedAt = 0; _replayLastFrame = 0;
        _replaySegment++;
        if (had) _replayNotifications.Enqueue(new(reason, null));
    }

    private bool ReplayContext(long generation, ReplayCaptureFrame frame)
    {
        var now = ReplayNow;
        return frame.CapturedAt > 0 && frame.CapturedAt <= now && frame.OriginalAt != default
            && Stopwatch.GetElapsedTime(frame.CapturedAt, now) <= MaximumFrameAge
            && IsCurrentVisualContext(generation) && Running && Found && !Paused && !WorldMapOpen && !EscMenuOpen && !InInstance
            && DetectedMap is { WorldMap: false } detected && detected.ContextGeneration == generation
            && Position is { } position && position.ContextGeneration == generation && position.MapId == detected.MapId
            && EffectiveFeaturePolicy.AllowsLiveMap(NetworkContextEnabled, _worldContext?.Snapshot, position.MapId);
    }

    private void PublishReplayNotifications()
    {
        if (Monitor.IsEntered(_flowGate)) return;
        lock (_replayNotificationGate)
        {
            while (true)
            {
                ReplayNotification notification;
                lock (_flowGate)
                {
                    if (_replayNotifications.Count == 0) return;
                    notification = _replayNotifications.Dequeue();
                }
                if (notification.Gap is { } reason) SafeEvent.Raise(ReplayPositionInterrupted, reason, "Replayposition unterbrochen");
                else if (notification.Samples is { } samples) SafeEvent.Raise(ReplayPositionsConfirmed,
                    (IReadOnlyList<ReplayMapPositionSample>)samples, "Bestätigte Replaypositionen");
            }
        }
    }

    internal void ObserveReplayFlow(ReplayCaptureFrame frame, long generation)
    {
        try
        {
        lock (_flowGate)
        {
            if (frame.CaptureSessionId > 0 && _replayCaptureSession > 0 && frame.CaptureSessionId != _replayCaptureSession)
                InvalidateReplayPosition(ReplayPositionGapReason.SourceInvalidated);
            if (_replayMap is null || !ReplayContext(generation, frame) || _flowBroken
                || _replayFixCapturedAt == 0 || Stopwatch.GetElapsedTime(_replayFixCapturedAt, frame.CapturedAt) > ReplayIdentityHoldLimit
                || Placement is not { WorldMap: false } placement || placement.MapId != _replayMap
                || placement.ContextGeneration != generation || frame.CapturedAt <= _replayLastFrame)
            {
                if (_replayMap is not null && _flowBroken) InvalidateReplayPosition(ReplayPositionGapReason.FlowBroken);
                else if (_replayMap is not null && (!ReplayContext(generation, frame)
                    || Stopwatch.GetElapsedTime(_replayFixCapturedAt, frame.CapturedAt) > ReplayIdentityHoldLimit))
                    InvalidateReplayPosition(ReplayPositionGapReason.SourceInvalidated);
                return;
            }
            if (frame.FrameId > 0 && frame.CaptureSessionId == _replayCaptureSession && frame.FrameId <= _replayCommittedFrameId) return;
            if (_replayFlow.Count >= 600) { InvalidateReplayPosition(ReplayPositionGapReason.QueueOverflow); return; }
            var coordinate = placement.Fix.FrameToReference(placement.Anchor);
            var x = coordinate.X * placement.WorldPerPixel; var y = coordinate.Y * placement.WorldPerPixel;
            if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0)
            { InvalidateReplayPosition(ReplayPositionGapReason.FlowBroken); return; }
            _replayFlow.Add(new(placement.MapId, x, y, frame.OriginalAt, frame.CapturedAt,
                Math.Max(0, frame.FrameId), Math.Max(0, frame.CaptureSessionId), generation,
                _replayWorldGeneration, _replayBindingGeneration, ReplayPositionProvenance.Flow, _replaySegment));
            _replayLastFrame = frame.CapturedAt;
        }
        }
        finally { PublishReplayNotifications(); }
    }

    internal void ObserveReplayFix(string mapId, MapFix fix, double worldPerPixel,
        OpenCvSharp.Point2d anchor, ReplayCaptureFrame frame, long generation)
    {
        try
        {
        lock (_flowGate)
        {
            if (!ReplayContext(generation, frame)) { InvalidateReplayPosition(ReplayPositionGapReason.SourceInvalidated); return; }
            var world = _worldContext?.Snapshot;
            var worldGeneration = settings.Current.LootTrackingEnabled && world?.Status == WorldContextStatus.Confirmed ? world.Generation : (long?)null;
            var binding = settings.Current.LootTrackingEnabled && world?.Status == WorldContextStatus.Confirmed ? world.BindingGeneration : null;
            if (_replayMap != mapId || _replayWorldGeneration != worldGeneration || _replayBindingGeneration != binding
                || _replayFixCapturedAt > 0 && Stopwatch.GetElapsedTime(_replayFixCapturedAt, frame.CapturedAt) > ReplayIdentityHoldLimit
                || frame.CaptureSessionId > 0 && _replayCaptureSession > 0 && frame.CaptureSessionId != _replayCaptureSession)
                InvalidateReplayPosition(ReplayPositionGapReason.SourceInvalidated);
            if (_replayCaptureSession != frame.CaptureSessionId) _replayCommittedFrameId = 0;
            if (frame.FrameId > 0 && _replayCaptureSession == frame.CaptureSessionId && frame.FrameId <= _replayCommittedFrameId) return;
            _replayCaptureSession = frame.CaptureSessionId;
            var coordinate = fix.FrameToReference(anchor);
            var x = coordinate.X * worldPerPixel; var y = coordinate.Y * worldPerPixel;
            if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0) return;
            var rows = new List<ReplayMapPositionSample>();
            foreach (var sample in _replayFlow)
                if (sample.CapturedAt > _replayLastCommitted && sample.CapturedAt < frame.CapturedAt
                    && !(sample.FrameId > 0 && sample.FrameId == frame.FrameId && sample.CaptureSessionId == frame.CaptureSessionId)) rows.Add(sample);
            if (frame.CapturedAt > _replayLastCommitted)
                rows.Add(new(mapId, x, y, frame.OriginalAt, frame.CapturedAt, Math.Max(0, frame.FrameId), Math.Max(0, frame.CaptureSessionId),
                    generation, worldGeneration, binding, ReplayPositionProvenance.Matched, _replaySegment));
            _replayFlow.RemoveAll(sample => sample.CapturedAt <= frame.CapturedAt);
            _replayMap = mapId; _replayWorldGeneration = worldGeneration; _replayBindingGeneration = binding;
            _replayFixCapturedAt = frame.CapturedAt;
            if (rows.Count == 0) return;
            rows.Sort((left, right) => left.CapturedAt.CompareTo(right.CapturedAt));
            _replayLastCommitted = rows[^1].CapturedAt;
            if (frame.FrameId > 0) _replayCommittedFrameId = Math.Max(_replayCommittedFrameId, frame.FrameId);
            _replayNotifications.Enqueue(new(null, rows.ToArray()));
        }
        }
        finally { PublishReplayNotifications(); }
    }
}
