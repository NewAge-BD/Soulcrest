using System.Security.Cryptography;
using System.Text.Json;
using Soulcrest.Core.Combat;

namespace Soulcrest.App.Services;

public sealed record CombatHistoryWriterFault(string Code, Guid? RunId, Exception Cause);

public sealed class CombatHistoryWriterException(CombatHistoryWriterFault fault)
    : IOException($"Combat history writer failed: {fault.Code}.", fault.Cause)
{
    public CombatHistoryWriterFault Fault { get; } = fault;
}

/// <summary>
/// One background writer for complete run checkpoints. Accepted snapshots are owned immutable bytes;
/// a pending run may be superseded only by an append-compatible full snapshot. No capture callback
/// performs file I/O. A fault retains accepted unwritten snapshots and permanently stops this writer.
/// </summary>
public sealed class CombatHistoryWriter : IDisposable, IAsyncDisposable
{
    private const int MaximumTrackedRuns = 4096;
    private const int MaximumTrackedAttempts = 32768;
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 64 };
    private readonly object _gate = new();
    private readonly Action<CombatRunDocument> _write;
    private readonly int _maximumPendingRuns, _maximumPendingBytes, _maximumCheckpointBytes;
    private readonly Queue<Guid> _order = new();
    private readonly Dictionary<Guid, Pending> _pending = [];
    private readonly Dictionary<Guid, RunStamp> _stamps = [];
    private TaskCompletionSource _available = Signal();
    private TaskCompletionSource _progress = Signal();
    private Pending? _inFlight;
    private CombatHistoryWriterFault? _fault;
    private bool _completing;
    private long _revision;
    private int _pendingBytes, _trackedAttempts;

    public CombatHistoryWriter(CombatHistoryStore store, int maximumPendingRuns = 8,
        int maximumPendingBytes = 64 * 1024 * 1024, int maximumCheckpointBytes = 32 * 1024 * 1024)
        : this((store ?? throw new ArgumentNullException(nameof(store))).SaveRun,
            maximumPendingRuns, maximumPendingBytes, maximumCheckpointBytes) { }

    internal CombatHistoryWriter(Action<CombatRunDocument> write, int maximumPendingRuns = 8,
        int maximumPendingBytes = 64 * 1024 * 1024, int maximumCheckpointBytes = 32 * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(write);
        if (maximumPendingRuns < 1) throw new ArgumentOutOfRangeException(nameof(maximumPendingRuns));
        if (maximumPendingBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumPendingBytes));
        if (maximumCheckpointBytes < 1 || maximumCheckpointBytes > maximumPendingBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumCheckpointBytes));
        _write = write;
        _maximumPendingRuns = maximumPendingRuns;
        _maximumPendingBytes = maximumPendingBytes;
        _maximumCheckpointBytes = maximumCheckpointBytes;
        Completion = Task.Run(ConsumeAsync);
    }

    public event Action<CombatHistoryWriterFault>? Faulted;
    public Task Completion { get; }
    public CombatHistoryWriterFault? Fault { get { lock (_gate) return _fault; } }
    public int PendingRunCount { get { lock (_gate) return _pending.Count; } }
    public int PendingBytes { get { lock (_gate) return _pendingBytes; } }

    /// <summary>
    /// Accepts a full checkpoint, never an event delta. Returns false after Complete/fault or when this
    /// input causes an explicit limit/validation fault. A rejected input never replaces accepted data.
    /// The caller must not mutate its graph during this call; it may mutate it after this call returns.
    /// </summary>
    public bool TryEnqueue(CombatRunDocument checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        lock (_gate) if (_completing || _fault is not null) return false;
        byte[] bytes;
        CombatRunDocument snapshot;
        try
        {
            using var buffer = new LimitedMemoryStream(_maximumCheckpointBytes);
            JsonSerializer.Serialize(buffer, checkpoint, JsonOptions);
            bytes = buffer.ToArray();
            snapshot = Decode(bytes);
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or InvalidDataException
            or ArgumentException)
        {
            RecordFault("checkpoint-invalid-or-too-large", checkpoint.Id, error);
            return false;
        }

        CombatHistoryWriterFault? raised = null;
        lock (_gate)
        {
            if (_completing || _fault is not null) return false;
            try
            {
                var replacing = _pending.TryGetValue(snapshot.Id, out var previous);
                var size = (long)_pendingBytes - (previous?.Bytes.Length ?? 0) + bytes.Length;
                if ((!replacing && _pending.Count >= _maximumPendingRuns) || size > _maximumPendingBytes)
                    throw new WriterLimitException("pending-checkpoint-limit");
                _stamps.TryGetValue(snapshot.Id, out var oldStamp);
                if (oldStamp is null && _stamps.Count >= MaximumTrackedRuns)
                    throw new WriterLimitException("checkpoint-identity-limit");
                var stamp = ValidateCheckpoint(snapshot, oldStamp);
                var attempts = _trackedAttempts - (oldStamp?.Attempts.Count ?? 0) + stamp.Attempts.Count;
                if (attempts > MaximumTrackedAttempts) throw new WriterLimitException("checkpoint-identity-limit");
                var revision = ++_revision;
                _pending[snapshot.Id] = new(snapshot.Id, bytes, previous?.FirstRevision ?? revision, revision);
                if (!replacing) _order.Enqueue(snapshot.Id);
                _pendingBytes = (int)size;
                _stamps[snapshot.Id] = stamp;
                _trackedAttempts = attempts;
                _available.TrySetResult();
                return true;
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException)
            {
                raised = SetFaultLocked(error is WriterLimitException limit ? limit.Code : "checkpoint-incompatible",
                    snapshot.Id, error);
            }
        }
        RaiseFault(raised);
        return false;
    }

    /// <summary>
    /// Waits for every checkpoint accepted before this call, including a later coalesced replacement.
    /// Later independent runs do not extend this barrier. Cancellation cancels only this wait.
    /// </summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        long target;
        lock (_gate) target = _revision;
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (_fault is { } fault) throw new CombatHistoryWriterException(fault);
                if ((_inFlight is null || _inFlight.FirstRevision > target) &&
                    _pending.Values.All(item => item.FirstRevision > target)) return;
                changed = _progress.Task;
            }
            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Stops accepting input and drains accepted checkpoints. Never blocks its caller.</summary>
    public void Complete()
    {
        lock (_gate) { _completing = true; _available.TrySetResult(); }
    }

    /// <summary>
    /// Copies retained accepted snapshots after a fault for explicit user-directed recovery. This
    /// never retries a write. Returned graphs are independent of the writer's immutable byte buffers.
    /// </summary>
    public IReadOnlyList<CombatRunDocument> GetUnwrittenCheckpoints()
    {
        byte[][] snapshots;
        lock (_gate)
        {
            IEnumerable<Pending> inFlight = _inFlight is null ? [] : [_inFlight];
            snapshots = inFlight.Concat(_order.Select(id => _pending[id]))
                .GroupBy(item => item.RunId).Select(group => group.OrderByDescending(item => item.Revision).First().Bytes)
                .ToArray();
        }
        return snapshots.Select(Decode).ToArray();
    }

    private async Task ConsumeAsync()
    {
        while (true)
        {
            Pending? item = null;
            Task? available = null;
            lock (_gate)
            {
                if (_fault is { } fault) throw new CombatHistoryWriterException(fault);
                if (_order.TryDequeue(out var id))
                {
                    item = _pending[id];
                    _pending.Remove(id);
                    _pendingBytes -= item.Bytes.Length;
                    _inFlight = item;
                }
                else if (_completing) return;
                else available = _available.Task;
            }
            if (item is null)
            {
                await available!.ConfigureAwait(false);
                continue;
            }
            try { _write(Decode(item.Bytes)); }
            catch (Exception error)
            {
                RecordFault("checkpoint-write-failed", item.RunId, error);
                throw new CombatHistoryWriterException(Fault!);
            }
            lock (_gate)
            {
                _inFlight = null;
                PulseProgress();
                if (_order.Count == 0) _available = Signal();
                // An overflow/invalid input may have faulted while this write was already in progress.
                // A successful in-flight write is acknowledged; pending snapshots stay retained.
                if (_fault is { } fault) throw new CombatHistoryWriterException(fault);
            }
        }
    }

    private void RecordFault(string code, Guid? runId, Exception error)
    {
        CombatHistoryWriterFault? raised;
        lock (_gate) raised = SetFaultLocked(code, runId, error);
        RaiseFault(raised);
    }

    private CombatHistoryWriterFault? SetFaultLocked(string code, Guid? runId, Exception error)
    {
        if (_fault is not null) return null;
        _fault = new(code, runId, error);
        _available.TrySetResult();
        PulseProgress();
        return _fault;
    }

    private void RaiseFault(CombatHistoryWriterFault? fault)
    {
        if (fault is null) return;
        foreach (var handler in Faulted?.GetInvocationList().Cast<Action<CombatHistoryWriterFault>>() ?? [])
            try { handler(fault); } catch { /* Reporting cannot restart or stop the writer. */ }
    }

    private void PulseProgress() { _progress.TrySetResult(); _progress = Signal(); }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static CombatRunDocument Decode(byte[] bytes) => JsonSerializer.Deserialize<CombatRunDocument>(bytes, JsonOptions)
        ?? throw new InvalidDataException("Null combat checkpoint.");

    private static RunStamp ValidateCheckpoint(CombatRunDocument run, RunStamp? previous)
    {
        if (run.Id == Guid.Empty || run.Attempts is null || run.Attempts.Any(a => a is null || a.Events is null || a.BossTargets is null))
            throw new ArgumentException("Invalid full checkpoint.");
        var identity = Hash(new { run.SchemaVersion, run.Id, run.DungeonKey, run.InstanceKey, run.StartedAtUtc });
        if (previous is not null && (identity != previous.Identity ||
            previous.EndedAtUtc is not null && previous.EndedAtUtc != run.EndedAtUtc))
            throw new InvalidOperationException("A run identity or confirmed end cannot change.");
        Dictionary<Guid, AttemptStamp> attempts = [];
        foreach (var attempt in run.Attempts)
        {
            if (attempt.Id == Guid.Empty || attempts.ContainsKey(attempt.Id)) throw new ArgumentException("Duplicate attempt.");
            var identityHash = Hash(attempt with
            { Events = [], BossTargets = [], DurationTicks = null, EndReason = null, HasIncompleteJournal = false });
            var count = attempt.Events.Length;
            var eventHash = Hash(attempt.Events.AsEnumerable());
            AttemptStamp? old = null;
            if (previous is not null) previous.Attempts.TryGetValue(attempt.Id, out old);
            if (previous is not null && previous.EndedAtUtc is not null && old is null)
                throw new InvalidOperationException("A closed run cannot gain attempts.");
            if (old is not null)
            {
                if (identityHash != old.Identity || count < old.EventCount ||
                    Hash(attempt.Events.Take(old.EventCount)) != old.EventHash ||
                    old.Targets.Any(target => !attempt.BossTargets.Contains(target)) ||
                    old.Incomplete && !attempt.HasIncompleteJournal ||
                    old.EndReason is not null && (count != old.EventCount || attempt.EndReason != old.EndReason ||
                        attempt.DurationTicks != old.DurationTicks || !attempt.BossTargets.SequenceEqual(old.Targets)))
                    throw new InvalidOperationException("A full checkpoint rewrites or omits accepted attempt data.");
            }
            attempts.Add(attempt.Id, new(identityHash, count, eventHash, attempt.BossTargets.ToArray(),
                attempt.EndReason, attempt.DurationTicks, attempt.HasIncompleteJournal));
        }
        if (previous?.Attempts.Keys.Any(id => !attempts.ContainsKey(id)) == true)
            throw new InvalidOperationException("A full checkpoint cannot remove accepted attempts.");
        return new(identity, run.EndedAtUtc, attempts);
    }

    private static string Hash<T>(T value)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var sink = new HashStream(hash);
        JsonSerializer.Serialize(sink, value, JsonOptions);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public void Dispose() => Complete();
    public async ValueTask DisposeAsync() { Complete(); await Completion.ConfigureAwait(false); }

    private sealed record Pending(Guid RunId, byte[] Bytes, long FirstRevision, long Revision);
    private sealed record RunStamp(string Identity, DateTimeOffset? EndedAtUtc, Dictionary<Guid, AttemptStamp> Attempts);
    private sealed record AttemptStamp(string Identity, int EventCount, string EventHash, CombatEntityReference[] Targets,
        CombatEndReason? EndReason, long? DurationTicks, bool Incomplete);
    private sealed class WriterLimitException(string code) : InvalidOperationException(code) { public string Code { get; } = code; }

    private sealed class LimitedMemoryStream(int limit) : MemoryStream
    {
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Position + buffer.Length > limit) throw new InvalidDataException("Checkpoint byte limit exceeded.");
            base.Write(buffer);
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Position + count > limit) throw new InvalidDataException("Checkpoint byte limit exceeded.");
            base.Write(buffer, offset, count);
        }
    }

    private sealed class HashStream(IncrementalHash hash) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Write(ReadOnlySpan<byte> buffer) => hash.AppendData(buffer);
        public override void Write(byte[] buffer, int offset, int count) => hash.AppendData(buffer, offset, count);
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
