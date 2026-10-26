using OpenCvSharp;

namespace Soulcrest.App.Services;

/// <summary>One qualified visual position and its local map/world authority revisions; no game run or entity identifiers.</summary>
public sealed record RecorderPositionSnapshot(PlayerPosition Position, long MapGeneration,
    long? WorldGeneration, long? BindingGeneration, DateTimeOffset DetectedAt, int Inliers, double? MatchError);

public sealed partial class MapTrackingService
{
    private readonly object _trackingDemandGate = new();
    private bool _manualTrackingRequested;
    private int _recorderTrackingRequests;
    private long _recorderLeaseGeneration;
    private readonly List<Task> _trackingLoops = [];
    private bool _trackingDisposed;

    // Tests replace only the shared loop, never game capture. Production always uses LoopAsync.
    internal Func<CancellationToken, Task>? TrackingLoopForTests { get; set; }
    internal Func<bool>? TrackingPrerequisitesForTests { get; set; }

    /// <summary>
    /// Requests the existing shared tracker without changing the saved manual tracking preference.
    /// Leases are idempotent and a manual Stop cannot revoke another active recorder's request.
    /// </summary>
    public IDisposable AcquireRecorderTracking()
    {
        RecorderTrackingLease lease;
        lock (_trackingDemandGate)
        {
            ObjectDisposedException.ThrowIf(_trackingDisposed, this);
            _recorderTrackingRequests++;
            lease = new(this, _recorderLeaseGeneration);
        }
        try { EnsureTrackingStarted(); }
        catch { lease.Dispose(); throw; }
        return lease;
    }

    private void ReleaseRecorderTracking(long generation)
    {
        var stopped = false;
        lock (_trackingDemandGate)
        {
            if (generation != _recorderLeaseGeneration || _recorderTrackingRequests == 0) return;
            _recorderTrackingRequests--;
            if (_recorderTrackingRequests == 0 && !_manualTrackingRequested)
            {
                StopTrackingLoop();
                stopped = true;
            }
        }
        if (stopped) SetStatus("Tracking gestoppt.");
    }

    private void EnsureTrackingStarted()
    {
        string? status = null;
        var error = false;
        lock (_trackingDemandGate)
        {
            if (_trackingDisposed || Running || !_manualTrackingRequested && _recorderTrackingRequests == 0) return;
            var testReady = TrackingPrerequisitesForTests?.Invoke();
            if (testReady != true && Region is null)
            {
                status = "Zuerst den Kartenbereich markieren.";
                error = true;
            }
            else if (testReady != true && progress.MapDataDirectory is null)
            {
                status = "Kein Kartendatenpaket gefunden.";
                error = true;
            }
            else
            {
                InvalidateMapContext();
                Running = true;
                // One shared loop and OpenCV pool, regardless of the number of recorder consumers.
                if (TrackingLoopForTests is null && OpenCvThreads > 0) Cv2.SetNumThreads(OpenCvThreads);
                _cancel = new CancellationTokenSource();
                var token = _cancel.Token;
                var loop = TrackingLoopForTests;
                _loop = Task.Run(() => loop is null ? LoopAsync(token) : loop(token), token);
                _trackingLoops.RemoveAll(task => task.IsCompleted);
                _trackingLoops.Add(_loop);
                status = "Tracking läuft – Karte wird gesucht …";
            }
        }
        if (status is not null) SetStatus(status, error);
    }

    /// <summary>
    /// Copies fresh position, quality and revisions under the existing World -> map lock order.
    /// World-map browsing, old image holds, loads, gaps and unsupported contexts supply no position.
    /// </summary>
    public bool TryGetRecorderPositionSnapshot(out RecorderPositionSnapshot snapshot)
    {
        RecorderPositionSnapshot? result = null;
        var source = _worldContext?.Snapshot;
        bool Capture()
        {
            lock (_flowGate)
            {
                if (!CanUsePlayerPosition || Position is not { } position || DetectedMap is not { } detected
                    || !double.IsFinite(position.X) || !double.IsFinite(position.Y) || position.X < 0 || position.Y < 0)
                    return false;
                var placement = Placement is { WorldMap: false } current && current.MapId == position.MapId
                    && current.ContextGeneration == position.ContextGeneration ? current : null;
                // Own authority is independent of the optional map-definition catalogue. Public
                // builds still have a verified own generation while their map fix is visual.
                var ownAuthority = settings.Current.LootTrackingEnabled && source?.Status == WorldContextStatus.Confirmed;
                result = new(position, ContextGeneration, ownAuthority ? source?.Generation : null,
                    ownAuthority ? source?.BindingGeneration : null, detected.At, placement?.Fix.Inliers ?? 0,
                    placement?.Fix.Error is { } matchError && double.IsFinite(matchError) ? matchError : null);
                return true;
            }
        }
        var accepted = source is null ? Capture() : _worldContext!.TryApplyWhileCurrent(source, Capture);
        snapshot = result!;
        return accepted;
    }

    private sealed class RecorderTrackingLease(MapTrackingService owner, long generation) : IDisposable
    {
        private MapTrackingService? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseRecorderTracking(generation);
    }
}
