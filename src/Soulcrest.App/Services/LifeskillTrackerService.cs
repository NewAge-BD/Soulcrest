using System.Collections.Immutable;
using System.Threading.Channels;
using Soulcrest.App.Network;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Services;

/// <summary>
/// Records only verified own harvesting. All mutation and session I/O run on one background reader;
/// source callbacks copy immutable evidence and never write a file or call map tracking.
/// </summary>
public sealed class LifeskillTrackerService : IDisposable, IAsyncDisposable
{
    private readonly SettingsService _settings;
    private readonly INetworkGatheringSource _source;
    private readonly Func<OwnCharacterContext, LootTrackerCharacterSnapshot?> _bind;
    private readonly Func<RecorderPositionSnapshot?> _position;
    private readonly Func<IDisposable> _acquire;
    private readonly Func<string, string> _mapName;
    private readonly Func<int, string> _itemName;
    private readonly LifeskillSessionStore _store;
    private readonly LifeskillGatherPointResolver _pointResolver;
    private readonly TimeProvider _time;
    private readonly Channel<Command> _commands = Channel.CreateBounded<Command>(new BoundedChannelOptions(4096)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task _worker;
    private readonly ITimer _timer;
    private readonly List<LootTrackerSession> _history = [];
    private LootTrackerSnapshot _snapshot = LootTrackerSnapshot.Empty;
    private LifeskillSessionState? _active;
    private IDisposable? _lease;
    private OwnCharacterIdentity? _activeIdentity;
    private OwnCharacterContext? _observedOwn;
    private (string Map, long MapGeneration, long? WorldGeneration, long? BindingGeneration)? _positionContext;
    private long _startedStamp, _lastHarvestStamp;
    private long _sequenceGeneration = -1, _lastSourceSequence = -1;
    private long _sourceWarningVersion;
    private bool _segmentBroken = true, _enabled;
    private volatile bool _sourceAvailable, _statusError;
    private volatile string _status = "Lifeskill-Verlauf wird geladen …";
    private int _disposed, _fatal, _overflow;
    private bool _recoveryIssue, _bindingMissing, _cleanupPending;

    public LifeskillTrackerService(SettingsService settings, INetworkGatheringSource source,
        MapTrackingService tracking, ExplorationService exploration, ProgressService progress)
        : this(settings, source, exploration.TryGetBoundCharacter,
            () => tracking.TryGetRecorderPositionSnapshot(out var value) ? value : null,
            tracking.AcquireRecorderTracking,
            id => progress.Maps.FirstOrDefault(map => map.Id == id)?.Label ?? id,
            LifeskillItemNameCatalog.Default.Name, new(Path.Combine(AppPaths.DataDirectory, "loot-tracker", "sessions")), TimeProvider.System,
            CreatePointResolver(progress)) { }

    internal LifeskillTrackerService(SettingsService settings, INetworkGatheringSource source,
        Func<OwnCharacterContext, LootTrackerCharacterSnapshot?> bind, Func<RecorderPositionSnapshot?> position,
        Func<IDisposable> acquire, Func<string, string> mapName, Func<int, string> itemName,
        LifeskillSessionStore store, TimeProvider time, LifeskillGatherPointResolver? pointResolver = null)
    {
        _settings = settings; _source = source; _bind = bind; _position = position;
        _acquire = acquire; _mapName = mapName; _itemName = itemName; _store = store; _time = time;
        _pointResolver = pointResolver ?? new(ResourceNodeCatalog.Empty, _ => null, _ => 8192);
        _worker = Task.Run(Run);
        settings.Changed += SettingsChanged;
        source.Changed += SourceChanged;
        source.OwnCharacterChanged += OwnChanged;
        source.GatheringCompleted += Gathered;
        Enqueue(new SettingsCommand(settings.Current.LifeskillTrackingEnabled));
        Enqueue(new OwnCommand(source.OwnCharacter));
        _timer = time.CreateTimer(_ => Enqueue(new TickCommand(time.GetTimestamp(), time.GetUtcNow())),
            null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public LootTrackerSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public bool SourceAvailable => _sourceAvailable;
    public string Status => _status;
    public bool StatusIsError => _statusError;
    public event Action? Changed;
    public event Action<AcceptedFarmLootDrop>? DropAccepted;

    private abstract record Command;
    private sealed record SettingsCommand(bool Enabled) : Command;
    private sealed record SourceCommand : Command;
    private sealed record OwnCommand(OwnCharacterContext? Own) : Command;
    private sealed record TickCommand(long Stamp, DateTimeOffset At) : Command;
    private sealed record GatherCommand(NetworkGathering Gathering, LootTrackerCharacterSnapshot? Character,
        RecorderPositionSnapshot? Position, long Stamp) : Command;
    private sealed record BarrierCommand(TaskCompletionSource Completion) : Command;
    private sealed record DeleteCommand(string Id, TaskCompletionSource Completion) : Command;

    private void SettingsChanged() => Enqueue(new SettingsCommand(_settings.Current.LifeskillTrackingEnabled));
    private void SourceChanged() => Enqueue(new SourceCommand());
    private void OwnChanged() => Enqueue(new OwnCommand(_source.OwnCharacter));
    private void Gathered(NetworkGathering gathering)
    {
        if (Volatile.Read(ref _disposed) != 0 || !_settings.Current.LifeskillTrackingEnabled
            || gathering.InInstance || !_source.Running || !_source.GatheringVerified || Volatile.Read(ref _fatal) != 0) return;
        var own = _source.OwnCharacter;
        if (own is null || !SameBinding(own, gathering.Character)) return;
        // Copy the bound profile before World -> map. Neither copy raises callbacks or performs I/O;
        // the established Network -> World -> map order has no reverse network acquisition.
        // Looking these up later could lose this event's position/binding during the next load.
        var character = _bind(gathering.Character);
        var position = _position();
        Enqueue(new GatherCommand(gathering, character, position, _time.GetTimestamp()));
    }

    private void Enqueue(Command command)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (!_commands.Writer.TryWrite(command)) Interlocked.Exchange(ref _overflow, 1);
    }

    private async Task Run()
    {
        try
        {
            try
            {
                var saved = _store.Read();
                _history.AddRange(saved.Sessions);
                _recoveryIssue = saved.Issues.Count > 0;
                _cleanupPending = saved.CleanupPending;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { _recoveryIssue = true; }
            await foreach (var command in _commands.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (Interlocked.Exchange(ref _overflow, 0) != 0)
                    Fail("Lifeskill-Aufzeichnung überlastet und angehalten.");
                switch (command)
                {
                    case SettingsCommand settings:
                        _enabled = settings.Enabled;
                        if (!_enabled && _active is not null) Finish(LifeskillEventKind.Completed);
                        Reconcile();
                        break;
                    case SourceCommand:
                        Reconcile();
                        break;
                    case OwnCommand own:
                        ObserveOwn(own.Own);
                        Reconcile();
                        break;
                    case TickCommand tick:
                        if (_active is not null && Volatile.Read(ref _fatal) == 0)
                            Expire(tick.Stamp, tick.At);
                        Reconcile();
                        break;
                    case GatherCommand gather:
                        Accept(gather);
                        break;
                    case DeleteCommand delete:
                        Delete(delete);
                        continue;
                    case BarrierCommand barrier:
                        barrier.Completion.TrySetResult();
                        break;
                }
                Publish();
            }
        }
        finally
        {
            if (_active is not null) Finish(LifeskillEventKind.Interrupted);
            _lease?.Dispose(); _lease = null; _sourceAvailable = false;
            while (_commands.Reader.TryRead(out var pending))
            {
                if (pending is DeleteCommand delete) delete.Completion.TrySetException(new ObjectDisposedException(nameof(LifeskillTrackerService)));
                if (pending is BarrierCommand barrier) barrier.Completion.TrySetException(new ObjectDisposedException(nameof(LifeskillTrackerService)));
            }
            Publish();
        }
    }

    private void Reconcile()
    {
        _sourceAvailable = _enabled && _source.Running && _source.GatheringVerified && Volatile.Read(ref _fatal) == 0;
        if (_sourceAvailable) _lease ??= _acquire();
        else
        {
            _lease?.Dispose(); _lease = null;
            if (_active is not null) BreakRoute();
        }
        if (Volatile.Read(ref _fatal) != 0) return;
        var warningVersion = _source.GatheringWarningVersion;
        var warning = _source.GatheringWarning;
        if (warningVersion != _sourceWarningVersion)
        {
            _sourceWarningVersion = warningVersion;
            if (_enabled && warning is not null) BreakRoute();
        }
        if (Volatile.Read(ref _fatal) != 0) return;
        _statusError = _recoveryIssue || _enabled && warning is not null;
        _status = _recoveryIssue ? "Lifeskill-Verlauf ist unvollständig."
            : !_enabled ? "Lifeskill-Aufzeichnung ist ausgeschaltet."
            : !_source.Running || !_source.GatheringVerified ? _source.GatheringStatus
            : warning is not null ? warning
            : _observedOwn is null ? "Warte auf die eigene Charaktererkennung."
            : _bindingMissing ? "Sammelbeleg ohne sichere Charakterzuordnung. Warte auf einen vollständigen Welteintritt, etwa nach einem Gebiets- oder Instanzwechsel."
            : _cleanupPending ? "Session gelöscht; Dateibereinigung ausstehend."
            : _active is null ? "Lifeskill-Aufzeichnung bereit." : "Lifeskill-Session läuft.";
    }

    private void ObserveOwn(OwnCharacterContext? own)
    {
        _bindingMissing = false;
        if (_active is not null)
        {
            if (own is not null && _activeIdentity is not null && !SameCharacter(own.Identity, _activeIdentity))
                Finish(LifeskillEventKind.Completed);
            else if (own is null || _observedOwn?.Generation != own.Generation) BreakRoute();
        }
        _observedOwn = own;
    }

    private void Accept(GatherCommand command)
    {
        var gathering = command.Gathering;
        if (!_enabled || gathering.InInstance || Volatile.Read(ref _fatal) != 0) return;
        if (command.Character is null) { _bindingMissing = true; Reconcile(); return; }
        var failed = gathering.Outcome == NetworkGatheringOutcome.Failed;
        if (!Enum.IsDefined(gathering.Outcome)) return;
        if (command.Character is not { } character
            || !Guid.TryParse(character.ProfileId, out _) || string.IsNullOrWhiteSpace(character.Name)
            || gathering.Sequence < 0
            || (failed ? !gathering.Items.IsDefaultOrEmpty : gathering.Items.IsDefaultOrEmpty)
            || !failed && gathering.Items.Any(item => item is null || item.ItemId <= 0 || item.Quantity <= 0)) return;
        if (_sequenceGeneration == gathering.Character.Generation && gathering.Sequence <= _lastSourceSequence) return;
        _bindingMissing = false;
        ImmutableArray<LootTrackerLoot> items;
        try
        {
            items = failed ? [] : gathering.Items.GroupBy(item => item.ItemId).Select(group => new LootTrackerLoot(
                $"item:{group.Key}", _itemName(group.Key), LootTrackerLootKind.Item,
                group.Aggregate(0L, (sum, item) => checked(sum + item.Quantity)))).ToImmutableArray();
            _ = items.Aggregate(0L, (sum, item) => checked(sum + item.Quantity));
        }
        catch (OverflowException) { Fail("Ungültiger Sammelbeleg wurde verworfen."); return; }
        Expire(command.Stamp, gathering.At);
        if (_active is not null && _activeIdentity is not null && !SameCharacter(_activeIdentity, gathering.Character.Identity))
            Finish(LifeskillEventKind.Completed);
        if (_active is null)
        {
            _active = new(Guid.NewGuid().ToString("N"));
            _activeIdentity = gathering.Character.Identity;
            _startedStamp = _lastHarvestStamp = command.Stamp;
            _segmentBroken = true; _positionContext = null;
            Commit(new() { SchemaVersion = 3, Kind = LifeskillEventKind.Started, At = gathering.At,
                FarmDay = LootFarmDay.Get(gathering.At), Character = character });
        }
        if (Volatile.Read(ref _fatal) != 0) return;
        var elapsed = _time.GetElapsedTime(_startedStamp, command.Stamp);
        var visit = GatherVisit(command.Position, gathering);
        if (!Commit(new() { Kind = failed ? LifeskillEventKind.Failed : LifeskillEventKind.Harvested,
            At = gathering.At, Elapsed = elapsed, Items = items, GatherVisit = visit })) return;
        _sequenceGeneration = gathering.Character.Generation; _lastSourceSequence = gathering.Sequence;
        if (!failed) _lastHarvestStamp = command.Stamp;
        if (visit is not null) _segmentBroken = false;
        Checkpoint();
        if (!failed && Volatile.Read(ref _fatal) == 0)
            foreach (var item in items)
                SafeEvent.Raise(DropAccepted, new AcceptedFarmLootDrop(_active.Id, character.ProfileId!,
                    gathering.At, item.Id, item.Name, item.Quantity), "Neue Sammelbeute");
        Reconcile();
    }

    private void Expire(long stamp, DateTimeOffset at)
    {
        if (_active is null) return;
        if (_active.FarmDay != LootFarmDay.Get(at))
        { Finish(LifeskillEventKind.Completed); return; }
        var timeout = TimeSpan.FromMinutes(LootTrackerPresentation.NormalizeIdleTimeoutMinutes(_settings.Current.LootTrackerIdleTimeoutMinutes));
        if (_time.GetElapsedTime(_lastHarvestStamp, stamp) >= timeout) Finish(LifeskillEventKind.Completed);
    }

    private LootTrackerGatherVisit? GatherVisit(RecorderPositionSnapshot? snapshot, NetworkGathering gathering)
    {
        if (_active is null || Volatile.Read(ref _fatal) != 0) return null;
        if (snapshot is not { } captured || captured.Position is not { } position
            || string.IsNullOrWhiteSpace(position.MapId) || position.ContextGeneration != captured.MapGeneration
            || captured.BindingGeneration != gathering.Character.Generation
            || position.At > gathering.At || gathering.At - position.At > TimeSpan.FromSeconds(2)
            || !double.IsFinite(position.X) || !double.IsFinite(position.Y) || position.X < 0 || position.Y < 0)
        { BreakRoute(); return null; }
        var context = (position.MapId, captured.MapGeneration, captured.WorldGeneration, captured.BindingGeneration);
        if (_positionContext is { } previous && previous != context) BreakRoute();
        _positionContext = context;
        var visit = _pointResolver.Resolve(Guid.NewGuid().ToString("N"), _active.RouteSection, position,
            gathering.Object?.DataId, gathering.At, gathering.Outcome == NetworkGatheringOutcome.Harvested,
            _mapName(position.MapId), "Sammelstelle");
        // A bad visual placement must not invalidate otherwise verified gathering loot.
        if (visit.OriginalX > visit.MapSize || visit.OriginalY > visit.MapSize
            || !double.IsFinite(visit.X) || !double.IsFinite(visit.Y)
            || visit.X < 0 || visit.Y < 0 || visit.X > visit.MapSize || visit.Y > visit.MapSize)
        { BreakRoute(); return null; }
        return visit;
    }

    private static LifeskillGatherPointResolver CreatePointResolver(ProgressService progress)
    {
        var maps = LootTrackerRouteMapPreview.Load(progress.MapDataDirectory);
        return new(ResourceNodeCatalog.Load(progress.MapDataDirectory),
            id => progress.Maps.FirstOrDefault(map => map.Id == id)?.MetersPerPixel,
            id => maps.TryGetValue(id, out var map) ? map.Size : null);
    }

    private void BreakRoute()
    {
        _positionContext = null;
        if (_active is null || _segmentBroken && _active.CoverageLimited || Volatile.Read(ref _fatal) != 0) return;
        _segmentBroken = true;
        Commit(new() { Kind = LifeskillEventKind.Gap, At = _time.GetUtcNow(), Elapsed = _active.LastAttemptElapsed });
    }

    private bool Commit(LifeskillSessionEvent entry)
    {
        if (_active is null) return false;
        var next = entry with { Sequence = _active.Sequence + 1,
            SchemaVersion = _active.Started ? _active.SchemaVersion : entry.SchemaVersion };
        if (!_active.CanApply(next)) { Fail("Ungültiger Sammelbeleg wurde verworfen."); return false; }
        try
        {
            _store.Append(_active.Id, next);
            // Only this worker owns the reducer; successful validation cannot become stale.
            if (!_active.TryApply(next)) throw new InvalidOperationException("Validated session event changed before apply.");
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        { Fail("Lifeskill-Aufzeichnung wegen Speicherfehler angehalten."); return false; }
    }

    private void Checkpoint()
    {
        if (_active is null || Volatile.Read(ref _fatal) != 0) return;
        try { _store.Checkpoint(_active); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Fail("Lifeskill-Aufzeichnung wegen Speicherfehler angehalten."); }
    }

    private void Finish(LifeskillEventKind kind)
    {
        if (_active is null) return;
        if (Volatile.Read(ref _fatal) == 0)
        {
            Commit(new() { Kind = kind, At = _active.LastAttemptAt ?? _time.GetUtcNow(), Elapsed = _active.LastAttemptElapsed });
            Checkpoint();
        }
        if (_active.HasAttempts) _history.Insert(0, _active.Snapshot(recovered: Volatile.Read(ref _fatal) != 0));
        _active = null; _activeIdentity = null; _positionContext = null; _segmentBroken = true;
    }

    /// <summary>Deletes on the same queue as gathering; the source duplicate guard is deliberately retained.</summary>
    public async Task DeleteSessionAsync(string sessionId)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!Guid.TryParseExact(sessionId, "N", out _)) throw new InvalidDataException("Invalid local session ID.");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try { await _commands.Writer.WriteAsync(new DeleteCommand(sessionId, completion)).ConfigureAwait(false); }
        catch (ChannelClosedException) { throw new ObjectDisposedException(nameof(LifeskillTrackerService)); }
        await completion.Task.ConfigureAwait(false);
    }

    private void Delete(DeleteCommand command)
    {
        try
        {
            if (_active?.Id != command.Id && !_history.Any(session => session.Id == command.Id))
                throw new KeyNotFoundException("Local session was not found.");
            // The directory rename is the commit point: before it no in-memory session changes,
            // afterwards a cleanup failure only leaves an ignored tombstone to retry at startup.
            var pending = _store.Delete(command.Id);
            _cleanupPending |= pending;
            if (_active?.Id == command.Id)
            { _active = null; _activeIdentity = null; _positionContext = null; _segmentBroken = true; }
            _history.RemoveAll(session => session.Id == command.Id);
            Reconcile();
            Publish();
            command.Completion.TrySetResult();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or KeyNotFoundException)
        { Publish(); command.Completion.TrySetException(error); }
    }

    private void Fail(string status)
    {
        Interlocked.Exchange(ref _fatal, 1);
        _sourceAvailable = false; _statusError = true; _status = status;
        _lease?.Dispose(); _lease = null;
    }

    private void Publish()
    {
        var sessions = _active is { HasAttempts: true } active
            ? _history.Prepend(active.Snapshot(_time.GetElapsedTime(_startedStamp, _time.GetTimestamp()),
                recovered: Volatile.Read(ref _fatal) != 0)).ToImmutableArray()
            : _history.ToImmutableArray();
        Volatile.Write(ref _snapshot, new(sessions, _time.GetUtcNow()));
        SafeEvent.Raise(Changed, "Lifeskill-Aufzeichnung");
    }

    private static bool SameCharacter(OwnCharacterIdentity first, OwnCharacterIdentity second) =>
        first.CharacterId == second.CharacterId && first.ServerId == second.ServerId;
    private static bool SameBinding(OwnCharacterContext first, OwnCharacterContext second) =>
        first.Generation == second.Generation && first.Identity == second.Identity;

    internal async Task DrainAsync()
    {
        if (Volatile.Read(ref _disposed) != 0) { await _worker.ConfigureAwait(false); return; }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _commands.Writer.WriteAsync(new BarrierCommand(completion)).ConfigureAwait(false);
        await completion.Task.ConfigureAwait(false);
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _settings.Changed -= SettingsChanged;
            _source.Changed -= SourceChanged;
            _source.OwnCharacterChanged -= OwnChanged;
            _source.GatheringCompleted -= Gathered;
            _timer.Dispose();
            _commands.Writer.TryComplete();
        }
        await _worker.ConfigureAwait(false);
    }
}
