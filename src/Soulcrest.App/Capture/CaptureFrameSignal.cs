using System.Diagnostics;

namespace Soulcrest.App.Capture;

/// <summary>
/// Broadcast notification that a capture session published a newer frame. A reader owns its cursor;
/// waiting never consumes a frame or another reader's notification. This class owns no capture data.
/// </summary>
internal sealed class CaptureFrameSignal
{
    private readonly object _gate = new();
    private long _publishedId;
    private bool _stopped;
    private TaskCompletionSource? _changed;

    /// <summary>
    /// Publish only after releasing the capture's frame lock. Older/repeated IDs and publications after
    /// Stop are ignored. Without readers this neither schedules work nor allocates a completion source.
    /// </summary>
    internal void Publish(long frameId)
    {
        lock (_gate)
        {
            if (_stopped || frameId <= _publishedId) return;
            _publishedId = frameId;
            var changed = _changed;
            _changed = null;
            // RunContinuationsAsynchronously keeps reader work off the WGC callback and this lock.
            changed?.TrySetResult();
        }
    }

    /// <summary>
    /// The current/new ID when it exceeds observedId; null on timeout or Stop. Cancellation throws
    /// without cancelling the shared notification. Session changes are handled by the capture service.
    /// </summary>
    internal ValueTask<long?> WaitAfterAsync(long observedId, TimeSpan timeout, CancellationToken token = default)
    {
        var started = Stopwatch.GetTimestamp();
        ArgumentOutOfRangeException.ThrowIfNegative(observedId);
        if (timeout != Timeout.InfiniteTimeSpan && (timeout < TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_stopped) return ValueTask.FromResult<long?>(null);
            if (_publishedId > observedId) return ValueTask.FromResult<long?>(_publishedId);
            if (timeout == TimeSpan.Zero) return ValueTask.FromResult<long?>(null);
            var changed = (_changed ??= NewCompletion()).Task;
            return new ValueTask<long?>(WaitCoreAsync(observedId, changed, timeout, started, token));
        }
    }

    /// <summary>Wake all pending readers and reject further publications. Idempotent.</summary>
    internal void Stop()
    {
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
            var changed = _changed;
            _changed = null;
            changed?.TrySetResult();
        }
    }

    private async Task<long?> WaitCoreAsync(long observedId, Task changed, TimeSpan timeout, long started, CancellationToken token)
    {
        while (true)
        {
            var remaining = timeout == Timeout.InfiniteTimeSpan ? timeout : timeout - Stopwatch.GetElapsedTime(started);
            if (remaining < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan) return null;
            try { await changed.WaitAsync(remaining, token).ConfigureAwait(false); }
            catch (TimeoutException) { return null; }
            lock (_gate)
            {
                if (_stopped) return null;
                if (_publishedId > observedId) return _publishedId;
                // A future cursor can outlive intermediate publications. Keep the original deadline.
                changed = (_changed ??= NewCompletion()).Task;
            }
        }
    }

    private static TaskCompletionSource NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
