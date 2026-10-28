namespace Soulcrest.App.Services;

/// <summary>Binds shared visual batches to the original session, with no loot-queue traffic or capture work.</summary>
public sealed class ReplayPositionRecorder : IDisposable, IAsyncDisposable
{
    private sealed record Binding(string SessionId, long StartStamp, long? Generation, long? WorldGeneration);
    private readonly object _gate = new();
    private readonly string _root;
    private readonly Func<string, string> _mapName;
    private readonly MapTrackingService? _source;
    private readonly TimeProvider _time;
    private readonly List<Task> _sealing = [];
    private Binding? _binding;
    private ReplayPositionWriter? _writer;
    private long _lastCapture, _lastFrameId, _lastCaptureSession;
    private bool _disposed;
    public ReplayPositionRecorder(MapTrackingService? source, Func<string, string>? mapName = null, string? root = null,
        TimeProvider? time = null)
    {
        _source = source; _mapName = mapName ?? (id => id); _root = root ?? ReplayPositionStore.DefaultRoot; _time = time ?? TimeProvider.System;
        if (source is not null) { source.ReplayPositionsConfirmed += Observe; source.ReplayPositionInterrupted += Gap; }
    }
    public void Start(string sessionId, long startStamp, long? bindingGeneration, long? worldGeneration)
    {
        lock (_gate)
        {
            if (_disposed || _binding?.SessionId == sessionId) return;
            if (_writer is not null) _sealing.Add(_writer.SealAsync(TimeSpan.Zero));
            _binding = null; _writer = null;
            try
            {
                _writer = ReplayPositionStore.Create(_root, sessionId);
                _binding = new(sessionId, startStamp, bindingGeneration, worldGeneration);
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
                _writer?.Gap(Elapsed(binding, _time.GetTimestamp()), ReplayPositionGapReason.SourceInvalidated);
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
                if (sample.CapturedAt < binding.StartStamp || sample.CapturedAt <= _lastCapture
                    || sample.BindingGeneration != binding.Generation || sample.WorldGeneration != binding.WorldGeneration
                    || !double.IsFinite(sample.X) || !double.IsFinite(sample.Y) || sample.X < 0 || sample.Y < 0
                    || sample.OriginalAt == default || string.IsNullOrWhiteSpace(sample.MapId)
                    || sample.FrameId > 0 && sample.FrameId == _lastFrameId && sample.CaptureSessionId == _lastCaptureSession) continue;
                _writer.Append(new(Elapsed(binding, sample.CapturedAt), sample.OriginalAt, sample.MapId,
                    _mapName(sample.MapId), sample.X, sample.Y, sample.Provenance, sample.SegmentId, sample.FrameId));
                _lastCapture = sample.CapturedAt; _lastFrameId = sample.FrameId; _lastCaptureSession = sample.CaptureSessionId;
            }
        }
    }
    private TimeSpan Elapsed(Binding binding, long stamp) => stamp <= binding.StartStamp ? TimeSpan.Zero
        : _time.GetElapsedTime(binding.StartStamp, stamp);
    public void Gap(ReplayPositionGapReason reason)
    { lock (_gate) if (_binding is { } binding) _writer?.Gap(Elapsed(binding, _time.GetTimestamp()), reason); }
    public Task SealAsync(TimeSpan finalElapsed)
    {
        lock (_gate)
        {
            var writer = _writer; _binding = null; _writer = null;
            if (writer is null) return Task.CompletedTask;
            var task = writer.SealAsync(finalElapsed); _sealing.Add(task); return task;
        }
    }
    public async Task DeleteAsync(string sessionId)
    {
        ReplayPositionWriter? active = null;
        lock (_gate)
        {
            if (_binding?.SessionId == sessionId) { active = _writer; _writer = null; _binding = null; }
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
            if (_writer is { } writer) { _sealing.Add(writer.SealAsync(TimeSpan.MaxValue)); _writer = null; _binding = null; }
            pending = _sealing.ToArray();
        }
        await Task.WhenAll(pending).ConfigureAwait(false);
    }
}
