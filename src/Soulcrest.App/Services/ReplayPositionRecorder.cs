namespace Soulcrest.App.Services;

/// <summary>Binds shared visual batches to the original session, with no loot-queue traffic or capture work.</summary>
public sealed class ReplayPositionRecorder : IDisposable, IAsyncDisposable
{
    private sealed record Binding(string SessionId, long StartStamp, long? Generation, long? WorldGeneration,
        bool ProductiveClock, long OffsetTicks, long CommittedStamp, TimeSpan CommittedElapsed,
        long MinimumCaptureStamp, bool Paused = false, TimeSpan? ResumeBoundary = null);
    private sealed record Pending(long CapturedAt, ReplayPositionSample Sample);
    internal const int MaximumPendingSamples = 4096;
    private readonly object _gate = new();
    private readonly string _root;
    private readonly Func<string, string> _mapName;
    private readonly MapTrackingService? _source;
    private readonly TimeProvider _time;
    private readonly List<Task> _sealing = [];
    private readonly List<Pending> _pending = [];
    private Binding? _binding;
    private ReplayPositionWriter? _writer;
    private long _lastCapture, _lastFrameId, _lastCaptureSession;
    private bool _disposed;
    private long? _pendingGapStamp;
    internal int PendingSampleCount { get { lock (_gate) return _pending.Count; } }
    public ReplayPositionRecorder(MapTrackingService? source, Func<string, string>? mapName = null, string? root = null,
        TimeProvider? time = null)
    {
        _source = source; _mapName = mapName ?? (id => id); _root = root ?? ReplayPositionStore.DefaultRoot; _time = time ?? TimeProvider.System;
        if (source is not null) { source.ReplayPositionsConfirmed += Observe; source.ReplayPositionInterrupted += Gap; }
    }
    public void Start(string sessionId, long startStamp, long? bindingGeneration, long? worldGeneration,
        bool productiveClock = false)
    {
        lock (_gate)
        {
            if (_disposed || _binding?.SessionId == sessionId) return;
            if (_writer is not null) _sealing.Add(_writer.SealAsync(TimeSpan.Zero));
            _binding = null; _writer = null; _pending.Clear(); _pendingGapStamp = null;
            try
            {
                _writer = ReplayPositionStore.Create(_root, sessionId);
                _binding = new(sessionId, startStamp, bindingGeneration, worldGeneration, productiveClock,
                    0, startStamp, TimeSpan.Zero, startStamp);
                _lastCapture = _lastFrameId = _lastCaptureSession = 0;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or InvalidOperationException)
            { if (Guid.TryParseExact(sessionId, "N", out _)) ReplayPositionStore.ReportFailure(Path.Combine(Path.GetFullPath(_root), sessionId, "positions-v1.bin")); }
        }
    }
    public void UpdateContext(long? bindingGeneration, long? worldGeneration)
    {
        lock (_gate)
        {
            if (_binding is not { } binding) return;
            if (binding.Generation != bindingGeneration || binding.WorldGeneration != worldGeneration)
            {
                _pending.Clear(); _pendingGapStamp = null;
                _writer?.Gap(binding.ProductiveClock ? binding.CommittedElapsed : Elapsed(binding, _time.GetTimestamp()),
                    ReplayPositionGapReason.SourceInvalidated);
                _binding = binding with { Generation = bindingGeneration, WorldGeneration = worldGeneration };
            }
        }
    }
    public void Observe(IReadOnlyList<ReplayMapPositionSample> observations)
    {
        lock (_gate)
        {
            if (_disposed || _binding is not { } binding || _writer is null) return;
            foreach (var sample in observations)
            {
                if (binding.Paused || sample.CapturedAt < binding.MinimumCaptureStamp
                    || binding.ResumeBoundary is not null && sample.CapturedAt == binding.MinimumCaptureStamp
                    || sample.CapturedAt <= _lastCapture
                    || sample.BindingGeneration != binding.Generation || sample.WorldGeneration != binding.WorldGeneration
                    || !double.IsFinite(sample.X) || !double.IsFinite(sample.Y) || sample.X < 0 || sample.Y < 0
                    || sample.OriginalAt == default || string.IsNullOrWhiteSpace(sample.MapId)
                    || sample.FrameId > 0 && sample.FrameId == _lastFrameId && sample.CaptureSessionId == _lastCaptureSession) continue;
                var position = new ReplayPositionSample(Elapsed(binding, sample.CapturedAt), sample.OriginalAt, sample.MapId,
                    _mapName(sample.MapId), sample.X, sample.Y, sample.Provenance, sample.SegmentId, sample.FrameId);
                // The old anchor owns its exact elapsed boundary. A resumed sample must be later,
                // including after timestamp-to-TimeSpan rounding; no synthetic epsilon is invented.
                if (binding.ResumeBoundary is { } boundary && position.Elapsed <= boundary) continue;
                if (!binding.ProductiveClock || sample.CapturedAt <= binding.CommittedStamp)
                    _writer.Append(position);
                else if (_time.GetElapsedTime(Math.Max(binding.CommittedStamp, binding.MinimumCaptureStamp), sample.CapturedAt) <=
                    TimeSpan.FromSeconds(LootTrackerPresentation.IdlePauseSeconds))
                {
                    // Outside the Abyss, movement is provisional until a successfully saved loot receipt.
                    // Neither a pause nor a failed write can commit its unproductive tail to the replay.
                    if (_pending.Count == MaximumPendingSamples) { _pending.Clear(); _pendingGapStamp = sample.CapturedAt; }
                    _pending.Add(new(sample.CapturedAt, position));
                }
                else _pendingGapStamp ??= sample.CapturedAt;
                _lastCapture = sample.CapturedAt; _lastFrameId = sample.FrameId; _lastCaptureSession = sample.CaptureSessionId;
            }
        }
    }
    internal void Commit(long stamp, TimeSpan elapsed)
    {
        lock (_gate)
        {
            if (_binding is not { ProductiveClock: true, Paused: false } binding || _writer is null
                || stamp < binding.CommittedStamp || elapsed < binding.CommittedElapsed) return;
            var committed = _pending.TakeWhile(row => row.CapturedAt <= stamp).ToArray();
            foreach (var row in committed)
            {
                if (row.Sample.Elapsed > elapsed) continue;
                // Overflow loses samples before the retained tail; an expired provisional sample
                // can instead follow earlier retained samples. Keep that actual capture boundary.
                if (_pendingGapStamp is { } loss && row.CapturedAt >= loss)
                { _writer.Gap(row.Sample.Elapsed, ReplayPositionGapReason.QueueOverflow); _pendingGapStamp = null; }
                _writer.Append(row.Sample);
            }
            _pending.RemoveRange(0, committed.Length);
            if (_pendingGapStamp is { } boundary && boundary <= stamp)
            { _writer.Gap(elapsed, ReplayPositionGapReason.QueueOverflow); _pendingGapStamp = null; }
            _binding = binding with { CommittedStamp = stamp, CommittedElapsed = elapsed };
        }
    }
    internal void Pause()
    {
        lock (_gate)
        {
            if (_binding is not { ProductiveClock: true, Paused: false } binding) return;
            _pending.Clear(); _pendingGapStamp = null;
            _writer?.Gap(binding.CommittedElapsed, ReplayPositionGapReason.SessionPaused);
            _binding = binding with { Paused = true };
        }
    }
    internal void Resume(long stamp, TimeSpan skipped)
    {
        lock (_gate)
        {
            if (_binding is not { ProductiveClock: true, Paused: true } binding || skipped < TimeSpan.Zero) return;
            _pending.Clear(); _pendingGapStamp = null;
            _binding = binding with { Paused = false, OffsetTicks = checked(binding.OffsetTicks + skipped.Ticks),
                MinimumCaptureStamp = stamp, ResumeBoundary = binding.CommittedElapsed };
        }
    }
    private TimeSpan Elapsed(Binding binding, long stamp)
    {
        var raw = stamp <= binding.StartStamp ? TimeSpan.Zero : _time.GetElapsedTime(binding.StartStamp, stamp);
        return TimeSpan.FromTicks(Math.Max(0, raw.Ticks - binding.OffsetTicks));
    }
    public void Gap(ReplayPositionGapReason reason)
    {
        lock (_gate) if (_binding is { } binding)
        {
            _pending.Clear(); _pendingGapStamp = null;
            _writer?.Gap(binding.ProductiveClock ? binding.CommittedElapsed : Elapsed(binding, _time.GetTimestamp()), reason);
        }
    }
    public Task SealAsync(TimeSpan finalElapsed)
    {
        lock (_gate)
        {
            var writer = _writer; _binding = null; _writer = null; _pending.Clear(); _pendingGapStamp = null;
            if (writer is null) return Task.CompletedTask;
            var task = writer.SealAsync(finalElapsed); _sealing.Add(task); return task;
        }
    }
    public async Task DeleteAsync(string sessionId)
    {
        ReplayPositionWriter? active = null;
        lock (_gate)
        {
            if (_binding?.SessionId == sessionId)
            { active = _writer; _writer = null; _binding = null; _pending.Clear(); _pendingGapStamp = null; }
        }
        if (active is not null) await active.StopForDeleteAsync().ConfigureAwait(false);
        await ReplayPositionStore.FlushAsync(_root, sessionId).ConfigureAwait(false);
        ReplayPositionStore.Forget(_root, sessionId);
        // UnifiedLootSessionStore owns deletion of the complete original session directory.
    }
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    public async ValueTask DisposeAsync()
    {
        Task[] pending;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_source is not null) { _source.ReplayPositionsConfirmed -= Observe; _source.ReplayPositionInterrupted -= Gap; }
            if (_writer is { } writer)
            {
                _sealing.Add(writer.SealAsync(_binding is { ProductiveClock: true } binding ? binding.CommittedElapsed : TimeSpan.MaxValue));
                _writer = null; _binding = null; _pending.Clear(); _pendingGapStamp = null;
            }
            pending = _sealing.ToArray();
        }
        await Task.WhenAll(pending).ConfigureAwait(false);
    }
}
