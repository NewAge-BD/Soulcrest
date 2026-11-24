using System.Diagnostics;

namespace Soulcrest.App.Overlay;

/// <summary>Monotonic deadlines, with missed frames dropped instead of a catch-up queue.</summary>
internal sealed class DaevanionAnimationPacing
{
    private readonly long _period;
    private readonly long _frequency;
    private long _next;
    internal DaevanionAnimationPacing(long frequency = 0)
    {
        _frequency = frequency > 0 ? frequency : Stopwatch.Frequency;
        _period = Math.Max(1, (long)Math.Round(_frequency / 30d));
    }

    internal int AfterFrame(long started, long finished)
    {
        if (_next == 0 || started < 0 || finished < started) _next = started;
        _next += _period;
        // At least 4ms for UI input; skip overdue phase slots without changing source freshness.
        var earliest = finished + Math.Max(1, _frequency / 250);
        if (_next < earliest) _next += ((earliest - _next + _period - 1) / _period) * _period;
        return Math.Clamp((int)Math.Ceiling((_next - finished) * 1000d / _frequency), 1, 67);
    }

    internal void Reset() => _next = 0;
}

/// <summary>Anonymous presentation work only. Does not sample images or extend a source deadline.</summary>
internal sealed record DaevanionAnimationHealth(long PresentedFrames, long Suppressions, int NativeBufferAllocations,
    double LastRasterMilliseconds, double MaximumRasterMilliseconds, double LastTransferMilliseconds,
    double MaximumTransferMilliseconds, double LastFrameIntervalMilliseconds, double MaximumFrameIntervalMilliseconds,
    double LastTotalMilliseconds, int NextFrameDelayMilliseconds)
{
    public long AnimationTicks { get; init; }
    public double LastTickMilliseconds { get; init; }
    public double MaximumTickMilliseconds { get; init; }
    public double LastInitialSourceValidationMilliseconds { get; init; }
    public double MaximumInitialSourceValidationMilliseconds { get; init; }
}

internal sealed class DaevanionAnimationTelemetry
{
    private readonly object _gate = new();
    private long _lastFrameAt;
    private DaevanionAnimationHealth _snapshot = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 33);
    internal DaevanionAnimationHealth Snapshot { get { lock (_gate) return _snapshot; } }

    internal void Presented(long at, double raster, double transfer, double total, int allocations)
    {
        lock (_gate)
        {
            var interval = _lastFrameAt > 0 ? Stopwatch.GetElapsedTime(_lastFrameAt, at).TotalMilliseconds : 0;
            _lastFrameAt = at;
            _snapshot = _snapshot with { PresentedFrames = _snapshot.PresentedFrames + 1,
                NativeBufferAllocations = allocations, LastRasterMilliseconds = raster,
                MaximumRasterMilliseconds = Math.Max(_snapshot.MaximumRasterMilliseconds, raster),
                LastTransferMilliseconds = transfer, MaximumTransferMilliseconds = Math.Max(_snapshot.MaximumTransferMilliseconds, transfer),
                LastFrameIntervalMilliseconds = interval,
                MaximumFrameIntervalMilliseconds = Math.Max(_snapshot.MaximumFrameIntervalMilliseconds, interval),
                LastTotalMilliseconds = total };
        }
    }

    internal void NextDelay(int milliseconds)
    { lock (_gate) _snapshot = _snapshot with { NextFrameDelayMilliseconds = milliseconds }; }

    internal void TickCompleted(double total, double initialSourceValidation)
    {
        lock (_gate)
            _snapshot = _snapshot with { AnimationTicks = _snapshot.AnimationTicks + 1,
                LastTickMilliseconds = total, MaximumTickMilliseconds = Math.Max(_snapshot.MaximumTickMilliseconds, total),
                LastInitialSourceValidationMilliseconds = initialSourceValidation,
                MaximumInitialSourceValidationMilliseconds = Math.Max(_snapshot.MaximumInitialSourceValidationMilliseconds, initialSourceValidation) };
    }

    internal void Suppressed()
    {
        lock (_gate)
        {
            _lastFrameAt = 0;
            _snapshot = _snapshot with { Suppressions = _snapshot.Suppressions + 1, LastFrameIntervalMilliseconds = 0 };
        }
    }
}
