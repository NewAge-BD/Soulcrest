using System.Diagnostics;

namespace Soulcrest.App.Services;

/// <summary>Fresh results of the existing visual detectors; null is not an observed absence.</summary>
public sealed record MapVisualContextSnapshot(bool? WorldMap = null, bool? EscMenu = null, bool? MinimapMissing = null);

public sealed partial class MapTrackingService
{
    // Visual detection deliberately idles for MaxIdleIntervalMs. Keep an accepted
    // observation through that next detection's existing processing budget; this
    // does not relax MaximumFrameAge when accepting a new input frame.
    internal static TimeSpan OverlayVisualObservationLifetime =>
        TimeSpan.FromMilliseconds(MaxIdleIntervalMs) + MaximumFrameAge;

    internal sealed record OverlayVisualSignal(bool Value, long CapturedAt, long Generation);
    private OverlayVisualSignal? _overlayWorldMap, _overlayMenu, _overlayMinimapMissing;

    public MapVisualContextSnapshot OverlayVisualContext
    {
        get
        {
            lock (_flowGate)
            {
                var now = Stopwatch.GetTimestamp();
                bool? Read(OverlayVisualSignal? signal) => ReadOverlayVisualSignal(signal, ContextGeneration, Running, NetworkSuspended, now);
                return new(Read(_overlayWorldMap), Read(_overlayMenu), Read(_overlayMinimapMissing));
            }
        }
    }

    internal static bool? ReadOverlayVisualSignal(OverlayVisualSignal? signal, long generation, bool running, bool suspended, long now) =>
        running && !suspended && signal is { CapturedAt: > 0 } && signal.Generation == generation
        && signal.CapturedAt <= now && Stopwatch.GetElapsedTime(signal.CapturedAt, now) <= OverlayVisualObservationLifetime ? signal.Value : null;

    private void ObserveOverlayMenu(bool open, long generation, ReplayCaptureFrame? frame)
    {
        lock (_flowGate)
        {
            if (!IsCurrentVisualContext(generation)) return;
            if (_overlayMenu?.Value != open) _overlayMinimapMissing = null;
            _overlayMenu = frame is { CapturedAt: > 0 } observed ? new(open, observed.CapturedAt, generation) : null;
            if (open)
            {
                _overlayWorldMap = null;
                _overlayMinimapMissing = _overlayMenu;
            }
        }
    }

    private void ObserveOverlayMap(bool worldMap, bool? found, long generation, ReplayCaptureFrame? frame)
    {
        lock (_flowGate)
        {
            if (!IsCurrentVisualContext(generation)) return;
            _overlayWorldMap = frame is { CapturedAt: > 0 } observed ? new(worldMap, observed.CapturedAt, generation) : null;
            _overlayMinimapMissing = frame is { CapturedAt: > 0 } known && found is { } hasMap
                ? new(!hasMap && !worldMap, known.CapturedAt, generation) : null;
        }
    }

    private void ClearOverlayVisualObservations()
    {
        lock (_flowGate) { _overlayWorldMap = null; _overlayMenu = null; _overlayMinimapMissing = null; }
    }
}
