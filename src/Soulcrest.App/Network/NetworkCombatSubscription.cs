using System.Diagnostics;

namespace Soulcrest.App.Network;

/// <summary>
/// Bounded, asynchronous consumer mailbox. The capture path only copies/enqueues transport input;
/// decoders, persistence and UI run on the consumer's worker. A global overflow barrier replaces a
/// discarded backlog and is delivered before any later retained message, even when this mailbox was full.
/// </summary>
public sealed class NetworkCombatSubscription : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<CombatCaptureEnvelope> _queue = new();
    private readonly Action<NetworkCombatSubscription> _unsubscribe;
    private readonly int _capacity, _maxBytes;
    private TaskCompletionSource _available = NewSignal();
    private int _bytes;
    private bool _completed;

    internal NetworkCombatSubscription(int capacity, int maxBytes, Action<NetworkCombatSubscription> unsubscribe)
    {
        if (capacity < 2) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (maxBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        _capacity = capacity;
        _maxBytes = maxBytes;
        _unsubscribe = unsubscribe;
    }

    public bool TryRead(out CombatCaptureEnvelope? envelope)
    {
        lock (_gate)
        {
            if (!_queue.TryDequeue(out envelope)) return false;
            _bytes -= envelope.Message.Length;
            if (_queue.Count == 0 && !_completed) _available = NewSignal();
            return true;
        }
    }

    /// <summary>Returns null after disposal. Cancellation does not dispose or lose queued input.</summary>
    public async ValueTask<CombatCaptureEnvelope?> ReadAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task available;
            lock (_gate)
            {
                if (TryRead(out var envelope)) return envelope;
                if (_completed) return null;
                available = _available.Task;
            }
            await available.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal bool WouldOverflow(int length)
    {
        lock (_gate) return !_completed && (_queue.Count >= _capacity || length > _maxBytes - _bytes);
    }

    internal void Enqueue(CombatCaptureEnvelope envelope, CombatCaptureEnvelope? overflow)
    {
        lock (_gate)
        {
            if (_completed) return;
            if (overflow is not null)
            {
                var dropped = _queue.Sum(item => item.Kind == CombatCaptureKind.QueueOverflow ? item.DroppedRecords : 1L);
                _queue.Clear();
                _bytes = 0;
                if (envelope.Message.Length > _maxBytes) dropped++;
                _queue.Enqueue(overflow with { DroppedRecords = dropped });
            }
            if (envelope.Message.Length <= _maxBytes)
            {
                _queue.Enqueue(envelope);
                _bytes += envelope.Message.Length;
            }
            _available.TrySetResult();
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_completed) return;
            _completed = true;
            _queue.Clear();
            _bytes = 0;
            _available.TrySetResult();
        }
        _unsubscribe(this);
    }
}

/// <summary>Only opaque stream tokens escape this process-local endpoint-key map.</summary>
internal sealed class NetworkCombatFeed : INetworkCombatSource, IDisposable
{
    private sealed record StreamContext(string Token, long Generation);
    private readonly object _gate = new();
    private readonly Dictionary<string, StreamContext> _streams = new(StringComparer.Ordinal);
    private readonly HashSet<NetworkCombatSubscription> _subscriptions = [];
    private readonly long _startedAt = Stopwatch.GetTimestamp();
    private long _sequence, _epoch = 1, _lastElapsedTicks;
    private bool _disposed;
    internal Func<long>? ElapsedTicks { get; set; }

    public NetworkCombatSubscription SubscribeCombat(int capacity = 2048, int maxBytes = 8 * 1024 * 1024)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var subscription = new NetworkCombatSubscription(capacity, maxBytes, Unsubscribe);
            _subscriptions.Add(subscription);
            subscription.Enqueue(new(++_sequence, ObserveTime(), null, null, _epoch,
                CombatCaptureKind.SessionReset, ReadOnlyMemory<byte>.Empty, "subscriber-attached"), null);
            return subscription;
        }
    }

    internal void Message(string stream, ReadOnlyMemory<byte> message, DateTimeOffset capturedAtUtc)
    {
        lock (_gate)
        {
            if (_subscriptions.Count == 0) return;
            var context = Context(stream);
            Publish(context.Token, context.Generation, CombatCaptureKind.Message, message.ToArray(), capturedAtUtc, null);
        }
    }

    internal void Gap(string stream, string reason, DateTimeOffset? capturedAtUtc = null)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (stream == "*")
            {
                Publish(null, _epoch, CombatCaptureKind.Gap, ReadOnlyMemory<byte>.Empty, capturedAtUtc, reason);
                _streams.Clear();
                _epoch++;
                return;
            }
            var context = Context(stream);
            Publish(context.Token, context.Generation, CombatCaptureKind.Gap, ReadOnlyMemory<byte>.Empty, capturedAtUtc, reason);
            // The old generation's boundary always precedes the first input of the new generation.
            _streams[stream] = context with { Generation = context.Generation + 1 };
            if (reason is "tcp-finished" or "tcp-reset" or "ownership-removed" or "capture-handle-error") _streams.Remove(stream);
        }
    }

    internal void Reset(string reason, DateTimeOffset? capturedAtUtc = null)
    {
        lock (_gate)
        {
            if (_disposed) return;
            Publish(null, _epoch, CombatCaptureKind.SessionReset, ReadOnlyMemory<byte>.Empty, capturedAtUtc, reason);
            _streams.Clear();
            _epoch++;
        }
    }

    private StreamContext Context(string stream)
    {
        if (_streams.TryGetValue(stream, out var context)) return context;
        // The transport is bounded to 64 streams; synthetic/direct gap callers are bounded here too.
        if (_streams.Count >= 64) Reset("combat-stream-limit");
        _streams[stream] = context = new(Guid.NewGuid().ToString("N"), 1);
        return context;
    }

    private long ObserveTime() => _lastElapsedTicks = Math.Max(_lastElapsedTicks,
        ElapsedTicks?.Invoke() ?? Stopwatch.GetElapsedTime(_startedAt).Ticks);

    private void Publish(string? token, long generation, CombatCaptureKind kind, ReadOnlyMemory<byte> message,
        DateTimeOffset? capturedAtUtc, string? reason)
    {
        var elapsed = ObserveTime();
        var overflowing = _subscriptions.Where(subscription => subscription.WouldOverflow(message.Length)).ToHashSet();
        var overflow = overflowing.Count == 0 ? null : new CombatCaptureEnvelope(++_sequence, elapsed,
            capturedAtUtc, null, _epoch, CombatCaptureKind.QueueOverflow, ReadOnlyMemory<byte>.Empty, "consumer-overflow");
        var envelope = new CombatCaptureEnvelope(++_sequence, elapsed, capturedAtUtc, token, generation, kind, message, reason);
        foreach (var subscription in _subscriptions)
            subscription.Enqueue(envelope, overflowing.Contains(subscription) ? overflow : null);
    }

    private void Unsubscribe(NetworkCombatSubscription subscription) { lock (_gate) _subscriptions.Remove(subscription); }

    public void Dispose()
    {
        NetworkCombatSubscription[] subscriptions;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            subscriptions = _subscriptions.ToArray();
            _subscriptions.Clear();
            _streams.Clear();
        }
        // A subscription releases its queue lock before calling Unsubscribe. Keep this two-stage
        // teardown outside the feed lock as well, so disposal never reverses feed -> queue locking.
        foreach (var subscription in subscriptions) subscription.Dispose();
    }
}
