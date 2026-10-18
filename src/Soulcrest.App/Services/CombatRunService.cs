using System.Text.Json;
using Soulcrest.Core.Combat;

namespace Soulcrest.App.Services;

public sealed record CombatRunServiceSnapshot(CombatRunControllerSnapshot Controller,
    CombatRunAdapterSnapshot Adapter, bool Stopped, string? FaultCode, int UnacceptedCheckpoints);

/// <summary>Connects the existing ordered decoder to the local controller and a separate serial disk worker.
/// Merely constructing this service requests no capture and opens no history files.</summary>
public sealed class CombatRunService : IDisposable
{
    private readonly object _gate = new();
    private readonly CombatNetworkService _network;
    private readonly CombatHistoryWriter _writer;
    private readonly CombatRunController _controller;
    private readonly CombatRunAdapter _adapter;
    private readonly System.Threading.Timer _timer;
    private readonly TimeProvider _time;
    private long _lastTicks, _lastSequence, _lastTimestamp;
    private CombatSourceContext? _lastContext;
    private DateTimeOffset _lastUtc;
    private bool _stopped;
    private string? _faultCode;
    private readonly Dictionary<Guid, CombatRunDocument> _unaccepted = [];

    public CombatRunService(CombatNetworkService network, CombatHistoryWriter writer)
        : this(network, writer, new(), GlobalEuCombatBossCatalog.Load()) { }

    public CombatRunService(CombatNetworkService network, CombatHistoryWriter writer, CombatRunController controller,
        CombatBossCatalog catalog, ICombatAmountClassifier? amounts = null, TimeProvider? timeProvider = null)
    {
        _network = network; _writer = writer; _controller = controller;
        _time = timeProvider ?? TimeProvider.System;
        _adapter = new(controller, catalog, network.SetConfirmedBossTargets, amounts);
        _network.ObservationReceived += Observe;
        _network.BoundaryReceived += Boundary;
        _timer = new(_ => Publish(), null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
    }

    public event Action? Changed;
    public Task Completion => _writer.Completion;
    public CombatRunServiceSnapshot Snapshot
    {
        get { lock (_gate) return new(_controller.Snapshot, _adapter.Snapshot, _stopped, _faultCode ?? (_writer.Fault is null ? null : "history-write-failed"), _unaccepted.Count); }
    }

    private void Observe(CombatNetworkObservation input)
    {
        lock (_gate)
        {
            if (_stopped) return;
            Remember(input.Sequence, input.ReceivedElapsedTicks, input.CapturedAtUtc, input.Context);
            try { _adapter.Observe(input, _network.GetConfirmedWorld(input.Context)); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or OverflowException)
            { Fail("controller-input-rejected"); }
        }
    }

    private void Boundary(CombatNetworkBoundary input)
    {
        lock (_gate)
        {
            if (_stopped) return;
            if (input.ReceivedElapsedTicks is { } ticks)
                Remember(input.Sequence, ticks, input.CapturedAtUtc ?? LogicalUtc(ticks), input.Context ?? _lastContext);
            try { _adapter.Boundary(input); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or OverflowException)
            { Fail("controller-boundary-rejected"); }
            Drain();
        }
    }

    private void Remember(long sequence, long ticks, DateTimeOffset at, CombatSourceContext? context)
    {
        _lastSequence = Math.Max(sequence, _lastSequence);
        _lastTicks = Math.Max(ticks, _lastTicks);
        _lastUtc = at;
        _lastContext = context;
        _lastTimestamp = _time.GetTimestamp();
    }

    private DateTimeOffset LogicalUtc(long ticks) => _lastUtc == default
        ? _time.GetUtcNow() : _lastUtc.AddTicks(Math.Max(0, ticks - _lastTicks));

    private void Publish()
    {
        Action? handlers;
        lock (_gate)
        {
            if (_stopped) return;
            // No source observation is manufactured to drive UI time. Advance is presentation-only.
            var elapsed = _lastContext is null ? 0 : _lastTicks + _time.GetElapsedTime(_lastTimestamp).Ticks;
            _controller.Advance(elapsed);
            Drain();
            handlers = Changed;
        }
        foreach (var handler in handlers?.GetInvocationList().Cast<Action>() ?? [])
        {
            try { handler(); } catch { /* A view cannot stop capture, measurement or local writes. */ }
        }
    }

    private void Drain()
    {
        var rejected = _unaccepted.Count != 0;
        foreach (var run in _controller.DrainCheckpoints())
        {
            if (!rejected && _writer.TryEnqueue(run)) continue;
            // The writer owns only accepted data. Keep every unaccepted run, including later entries.
            _unaccepted[run.Id] = run;
            rejected = true;
        }
        if (rejected)
        {
            if (!_stopped) Fail("history-checkpoint-rejected");
            foreach (var run in _controller.DrainCheckpoints()) _unaccepted[run.Id] = run;
        }
    }

    /// <summary>Defensive snapshots for explicit local recovery; never retries a possibly partial append.</summary>
    public IReadOnlyList<CombatRunDocument> GetUnwrittenCheckpoints()
    {
        lock (_gate)
        {
            var all = _writer.GetUnwrittenCheckpoints().ToDictionary(run => run.Id);
            foreach (var run in _unaccepted.Values) all[run.Id] = run;
            return all.Values.Select(run => JsonSerializer.Deserialize<CombatRunDocument>(JsonSerializer.SerializeToUtf8Bytes(run))!).ToArray();
        }
    }

    private void Fail(string code)
    {
        _faultCode ??= code;
        if (_lastContext is not null)
            _controller.Interrupt(new(_lastSequence, _lastTicks, _lastUtc, _lastContext)
                { IsCaptureTimestampObserved = false }, CombatGapReason.InterruptedJournal);
        _stopped = true;
    }

    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            Drain();
            if (_unaccepted.Count != 0)
                return Task.FromException(new InvalidOperationException("Unaccepted local combat checkpoints require explicit recovery."));
        }
        return _writer.FlushAsync(cancellationToken);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _network.ObservationReceived -= Observe;
            _network.BoundaryReceived -= Boundary;
            _timer.Dispose();
            if (!_stopped && _lastContext is not null)
            {
                var ticks = _lastTicks + _time.GetElapsedTime(_lastTimestamp).Ticks;
                _controller.Interrupt(new(_lastSequence + 1, ticks, LogicalUtc(ticks), _lastContext)
                    { IsCaptureTimestampObserved = false }, CombatGapReason.InterruptedJournal);
            }
            _stopped = true;
            Drain();
            _writer.Complete(); // Drain asynchronously; never block a capture callback on disk I/O.
        }
    }
}
