using System.Collections.Immutable;
using System.Threading.Channels;
using Soulcrest.App.Network;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Services;

/// <summary>One own session, monotonic clock and journal for all qualified loot activities.</summary>
public sealed partial class UnifiedLootTrackerService : IDisposable, IAsyncDisposable
{
    private readonly SettingsService _settings;
    private readonly INetworkFarmSource _farm;
    private readonly INetworkKinaSource _kina;
    private readonly INetworkGatheringSource _gathering;
    private readonly INetworkAbyssSource _abyss;
    private readonly INetworkAbyssCombatSource? _combat;
    private readonly Func<OwnCharacterContext, LootTrackerCharacterSnapshot?> _bind;
    private readonly Func<WorldContextSnapshot> _world;
    private readonly Func<RecorderPositionSnapshot?> _position;
    private readonly Func<IDisposable>? _acquire;
    private readonly Func<string, string> _mapName;
    private readonly Func<int, string> _itemName;
    private readonly LifeskillGatherPointResolver _resolver;
    private readonly UnifiedLootSessionStore _store;
    private readonly DailyMonsterKinaStore _dailyStore;
    private readonly TimeProvider _time;
    private readonly ReplayPositionRecorder? _replayPositions;
    private readonly Func<LootTrackerSession, bool>? _deleteLegacy;
    private readonly Func<UnifiedLootLegacyRead>? _readLegacy;
    private readonly Func<RecorderPositionSnapshot, FarmLocationMatch?>? _locationName;
    private readonly List<Action> _detach = [];
    private readonly Channel<Command> _commands = Channel.CreateBounded<Command>(new BoundedChannelOptions(4096)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task _worker;
    private readonly ITimer _timer;
    private readonly List<LootTrackerSession> _history = [];
    private readonly Dictionary<GainChannel, (long Generation, long Sequence)> _sequences = [];
    private LootTrackerSnapshot _snapshot = LootTrackerSnapshot.Empty;
    private UnifiedLootOverlayState _overlayState = UnifiedLootOverlayState.Empty;
    private LootTrackerSession? _active, _durable;
    private OwnCharacterContext? _own;
    private OwnCharacterIdentity? _activeIdentity;
    private LootTrackerCharacterSnapshot? _character;
    private WorldContextSnapshot? _observedWorld;
    private NetworkAbyssSnapshot _absolute = NetworkAbyssSnapshot.Empty;
    private NetworkDailyMonsterKinaSnapshot _daily = new(null, null, null, 0);
    private SavedDailyMonsterKina? _cachedDaily;
    private long _dailyGeneration = -1, _dailySequence = -1;
    private volatile bool _dailyConfirmed, _available, _overlayCandidate, _statusError;
    private volatile string _status = "Loot-Verlauf wird geladen …";
    private bool _storageIssue, _cleanupPending;
    private long _gatheringWarningVersion;
    private string? _gatheringWarning;
    private long _startStamp, _lastActivityStamp, _lastActivityElapsedTicks, _lastPresenceSave;
    private string? _suppressedAbyssProfile, _announcedSession;
    private IDisposable? _lease;
    private int _disposed, _overflow, _fatal;

    public UnifiedLootTrackerService(SettingsService settings, INetworkFarmSource farm, INetworkKinaSource kina,
        INetworkGatheringSource gathering, INetworkAbyssSource abyss, ExplorationService exploration,
        WorldContextService world, MapTrackingService tracking, ProgressService progress,
        IEnumerable<LootTrackerSession>? legacyHistory = null, INetworkAbyssCombatSource? combat = null,
        Func<LootTrackerSession, bool>? deleteLegacy = null, Func<UnifiedLootLegacyRead>? readLegacyHistory = null)
        : this(settings, farm, kina, gathering, abyss, exploration.TryGetBoundCharacter, () => world.Snapshot,
            new(Path.Combine(AppPaths.DataDirectory, "loot-tracker", "unified-sessions")), TimeProvider.System,
            () => tracking.TryGetRecorderPositionSnapshot(out var value) ? value : null,
            tracking.AcquireRecorderTracking, id => progress.Maps.FirstOrDefault(map => map.Id == id)?.Label ?? id,
            id => LifeskillItemNameCatalog.Default.Name(id), CreateResolver(progress), legacyHistory, combat,
            new(Path.Combine(AppPaths.DataDirectory, "loot-tracker", "daily-monster-kina.json")), deleteLegacy, readLegacyHistory, replayTracking: tracking,
            locationName: CreateLocationResolver(settings, progress))
    { world.Changed += SourceChanged; _detach.Add(() => world.Changed -= SourceChanged); }

    internal UnifiedLootTrackerService(SettingsService settings, INetworkFarmSource farm, INetworkKinaSource kina,
        INetworkGatheringSource gathering, INetworkAbyssSource abyss,
        Func<OwnCharacterContext, LootTrackerCharacterSnapshot?> bind, Func<WorldContextSnapshot> world,
        UnifiedLootSessionStore store, TimeProvider time, Func<RecorderPositionSnapshot?>? position = null,
        Func<IDisposable>? acquireRecorder = null, Func<string, string>? mapName = null,
        Func<int, string>? itemName = null, LifeskillGatherPointResolver? pointResolver = null,
        IEnumerable<LootTrackerSession>? legacyHistory = null, INetworkAbyssCombatSource? combat = null,
        DailyMonsterKinaStore? dailyStore = null, Func<LootTrackerSession, bool>? deleteLegacy = null,
        Func<UnifiedLootLegacyRead>? readLegacyHistory = null,
        MapTrackingService? replayTracking = null, ReplayPositionRecorder? replayPositionRecorder = null,
        Func<RecorderPositionSnapshot, FarmLocationMatch?>? locationName = null)
    {
        _settings = settings; _farm = farm; _kina = kina; _gathering = gathering; _abyss = abyss;
        _combat = combat ?? farm as INetworkAbyssCombatSource; _bind = bind; _world = world; _store = store; _time = time;
        _position = position ?? (() => null); _acquire = acquireRecorder; _mapName = mapName ?? (id => id);
        _itemName = itemName ?? (id => LifeskillItemNameCatalog.Default.Name(id));
        _resolver = pointResolver ?? new(new ResourceNodeCatalog([]), _ => null, _ => null);
        _dailyStore = dailyStore ?? new(Path.Combine(AppPaths.DataDirectory, "loot-tracker", "daily-monster-kina.json"));
        _deleteLegacy = deleteLegacy; _readLegacy = readLegacyHistory; _locationName = locationName;
        _replayPositions = replayPositionRecorder ?? (replayTracking is not null ? new(replayTracking, _mapName) : null);
        if (legacyHistory is not null) _history.AddRange(legacyHistory.Where(session => session.UnifiedVersion == 0)
            .Select(InterruptLegacy));
        settings.Changed += SourceChanged; _detach.Add(() => settings.Changed -= SourceChanged);
        var attached = new HashSet<object>(ReferenceEqualityComparer.Instance);
        if (attached.Add(farm)) { farm.Changed += SourceChanged; farm.OwnCharacterChanged += SourceChanged;
            _detach.Add(() => { farm.Changed -= SourceChanged; farm.OwnCharacterChanged -= SourceChanged; }); }
        if (attached.Add(kina)) { kina.Changed += SourceChanged; kina.OwnCharacterChanged += SourceChanged;
            _detach.Add(() => { kina.Changed -= SourceChanged; kina.OwnCharacterChanged -= SourceChanged; }); }
        if (attached.Add(gathering)) { gathering.Changed += SourceChanged; gathering.OwnCharacterChanged += SourceChanged;
            _detach.Add(() => { gathering.Changed -= SourceChanged; gathering.OwnCharacterChanged -= SourceChanged; }); }
        if (attached.Add(abyss)) { abyss.Changed += SourceChanged; abyss.OwnCharacterChanged += SourceChanged;
            _detach.Add(() => { abyss.Changed -= SourceChanged; abyss.OwnCharacterChanged -= SourceChanged; }); }
        farm.MonsterKinaGained += MonsterKina; farm.FarmDropGained += FarmDrop;
        kina.KinaGained += AbyssKina; gathering.GatheringCompleted += Gathered;
        abyss.AbyssPointsGained += PointsGained; abyss.RankPointsChanged += RankChanged;
        if (_combat is not null) _combat.CombatEvent += CombatObserved;
        _worker = Task.Run(Run);
        SourceChanged();
        _timer = time.CreateTimer(_ => Enqueue(new Tick()), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public LootTrackerSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public UnifiedLootOverlayState OverlayState => Volatile.Read(ref _overlayState);
    public bool SourceAvailable => _available;
    public bool OverlayCandidate => _overlayCandidate;
    public string Status => _status;
    public bool StatusIsError => _statusError;
    public NetworkDailyMonsterKinaSnapshot DailyMonsterKinaSnapshot => Volatile.Read(ref _daily);
    public SavedDailyMonsterKina? CachedDailyMonsterKina => Volatile.Read(ref _cachedDaily);
    public bool DailyMonsterKinaConfirmed => _dailyConfirmed;
    public event Action? Changed;
    public event Action<string>? SessionStarted;
    public event Action<AcceptedFarmLootDrop>? DropAccepted;

    private abstract record Command;
    private sealed record Observation(OwnCharacterContext? Own, LootTrackerCharacterSnapshot? Character,
        WorldContextSnapshot World, bool Running, NetworkDailyMonsterKinaSnapshot Daily,
        NetworkAbyssSnapshot Absolute, DateTimeOffset At, long Stamp) : Command;
    private sealed record Tick : Command;
    private sealed record Barrier(TaskCompletionSource Completion) : Command;
    private sealed record Delete(string Id, TaskCompletionSource Completion) : Command;
    private enum GainChannel { MonsterKina, AbyssKina, Drop, Harvest, AbyssPoints, RankPoints, Combat }

    private OwnCharacterContext? CurrentOwn => _farm.OwnCharacter ?? _gathering.OwnCharacter ?? _abyss.OwnCharacter ?? _kina.OwnCharacter;
    private bool Running => _farm.Running || _gathering.Running || _abyss.Running || _kina.Running;
    private Observation Capture() { var own = CurrentOwn; return new(own, own is null ? null : _bind(own),
        _world(), Running, _farm.DailyMonsterKinaSnapshot, _abyss.AbyssSnapshot, _time.GetUtcNow(), _time.GetTimestamp()); }
    private void SourceChanged() => Enqueue(Capture());
    private void Enqueue(Command command)
    {
        if (Volatile.Read(ref _disposed) == 0 && !_commands.Writer.TryWrite(command)) Interlocked.Exchange(ref _overflow, 1);
    }

    private async Task Run()
    {
        try
        {
            try { var saved = _store.Read(); _history.AddRange(saved.Sessions); _storageIssue = saved.Issues.Count > 0; _cleanupPending = saved.CleanupPending; }
            catch (Exception error) when (StorageFailure(error)) { _storageIssue = true; }
            try
            {
                if (_readLegacy?.Invoke() is { } legacy)
                {
                    _history.AddRange(legacy.Sessions.Where(session => session.UnifiedVersion == 0).Select(InterruptLegacy));
                    _storageIssue |= legacy.Incomplete;
                }
            }
            catch (Exception error) when (StorageFailure(error)) { _storageIssue = true; }
            await foreach (var command in _commands.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (Interlocked.Exchange(ref _overflow, 0) != 0) Fail();
                try
                {
                    switch (command)
                    {
                        case Observation observation: Observe(observation); break;
                        case Tick: Observe(Capture()); break;
                        case Activity activity: Accept(activity); break;
                        case Delete delete: await DeleteSession(delete).ConfigureAwait(false); break;
                    }
                }
                catch (Exception error) { Stop(error, command); }
                try { Publish(); }
                catch (Exception error) { Stop(error, null); }
                if (command is Barrier barrier) barrier.Completion.TrySetResult();
            }
        }
        finally
        {
            try { Finish(LootTrackerSessionStatus.Interrupted); }
            catch (Exception error) when (StorageFailure(error)) { Fail(); }
            _lease?.Dispose(); _lease = null;
            if (_replayPositions is not null) await _replayPositions.DisposeAsync().ConfigureAwait(false);
            Publish();
        }
    }

    // A damaged or unqualified gathering stream must not join positions across the faulty stretch.
    private void ObserveGatheringWarning()
    {
        var version = _gathering.GatheringWarningVersion;
        var warning = _settings.Current.LifeskillTrackingEnabled ? _gathering.GatheringWarning : null;
        if (version != _gatheringWarningVersion)
        {
            _gatheringWarningVersion = version;
            if (warning is not null) BreakRoute();
        }
        _gatheringWarning = warning;
    }

    private void Observe(Observation observation)
    {
        var character = ValidCharacter(observation.Character);
        var transition = observation.World.Status == WorldContextStatus.Transitioning;
        if (_active is not null && character is not null && (_active.Character?.ProfileId != character.ProfileId
            || _activeIdentity is not null && observation.Own is not null && !SameCharacter(_activeIdentity, observation.Own.Identity)))
            Finish(LootTrackerSessionStatus.Completed);
        if (!ReferenceEquals(_own, observation.Own)) BreakRoute();
        ObserveGatheringWarning();
        UpdateDaily(observation, character);
        _own = observation.Own;
        _replayPositions?.UpdateContext(observation.Own?.Generation, observation.World.Generation);
        _character = character ?? (transition ? _active?.Character : null);
        _absolute = QualifiedAbsolute(observation.Absolute, observation.Own, observation.At)
            ? observation.Absolute : NetworkAbyssSnapshot.Empty;
        if (_observedWorld is null || observation.World.Generation >= _observedWorld.Generation)
            _observedWorld = observation.World;
        if (!_settings.Current.LootTrackingEnabled || !observation.Running || Volatile.Read(ref _fatal) != 0)
        { Finish(LootTrackerSessionStatus.Interrupted); return; }
        if (_active?.Kind == LootTrackerSessionKind.Abyss && !_settings.Current.AbyssTrackingEnabled
            || _active?.Kind == LootTrackerSessionKind.Mixed && !_settings.Current.MonsterLootTrackingEnabled && !_settings.Current.LifeskillTrackingEnabled)
        { Finish(LootTrackerSessionStatus.Interrupted); return; }
        if (transition)
        {
            BreakRoute();
            if (_active?.Kind == LootTrackerSessionKind.Abyss && _settings.Current.AbyssTrackingEnabled)
                Presence(observation.At, observation.Stamp);
            else Expire(observation.At, observation.Stamp);
            return;
        }
        if (character is null)
        { Finish(LootTrackerSessionStatus.Interrupted); return; }
        if (!QualifiedWorld(observation.World, observation.Own, observation.At))
        {
            var confirmedExit = observation.Own is not null && observation.World.Status == WorldContextStatus.Confirmed
                && observation.World.BindingGeneration == observation.Own.Generation && observation.World.ObservedAt <= observation.At;
            if (confirmedExit) _suppressedAbyssProfile = null;
            Finish(confirmedExit ? LootTrackerSessionStatus.Completed : LootTrackerSessionStatus.Interrupted); return;
        }
        // A queued old positive observation must never roll a newer world/own binding backwards.
        if (!ReferenceEquals(CurrentOwn, observation.Own) || _world().Generation != observation.World.Generation) return;
        var abyss = IsAbyss(observation.World);
        if (!abyss) _suppressedAbyssProfile = null;
        if (_active is not null && (_active.Kind == LootTrackerSessionKind.Abyss) != abyss)
            Finish(LootTrackerSessionStatus.Completed);
        if (abyss)
        {
            if (!_settings.Current.AbyssTrackingEnabled) { Finish(LootTrackerSessionStatus.Interrupted); return; }
            if (_character is not null && _suppressedAbyssProfile != _character.ProfileId)
            {
                if (_active is null) { Start(_character, observation.Own!, observation.World, observation.At, observation.Stamp); Save(_active!); }
                Presence(observation.At, observation.Stamp);
                UpdateAbyssObservation(observation.At, observation.Stamp);
                RecordPosition(_position(), observation.World, observation.Own!, observation.At, observation.Stamp, false);
            }
        }
        else
        {
            if (!_settings.Current.MonsterLootTrackingEnabled && !_settings.Current.LifeskillTrackingEnabled)
                Finish(LootTrackerSessionStatus.Interrupted);
            Expire(observation.At, observation.Stamp);
            UpdateDailyMode(observation);
            if (_active is not null) RecordPosition(_position(), observation.World, observation.Own!, observation.At, observation.Stamp, false);
        }
    }

    private void Start(LootTrackerCharacterSnapshot character, OwnCharacterContext own, WorldContextSnapshot world,
        DateTimeOffset at, long stamp)
    {
        _startStamp = _lastActivityStamp = _lastPresenceSave = stamp; _lastActivityElapsedTicks = 0;
        _activeIdentity = own.Identity; _durable = null; _announcedSession = null; BreakRoute(); _gatherSection = 0;
        _locationResolved = false;
        _active = new()
        {
            Id = Guid.NewGuid().ToString("N"), Kind = IsAbyss(world) ? LootTrackerSessionKind.Abyss : LootTrackerSessionKind.Mixed,
            UnifiedVersion = 1, TimelineVersion = 1, Status = LootTrackerSessionStatus.Live,
            StartedAt = at, LastLootAt = IsAbyss(world) ? null : at, LastPresenceAt = IsAbyss(world) ? at : null,
            FarmDay = LootFarmDay.Get(at), Character = character,
            Location = (_settings.Current.LootNameLanguage == "de" ? world.NameDe ?? world.NameEn : world.NameEn ?? world.NameDe) ?? "",
            ItemCount = 0, SoulCount = 0, CoverageLimited = true
        };
        _active = Availability(_active, at, TimeSpan.Zero);
    }

    private void Expire(DateTimeOffset at, long stamp)
    {
        if (_active is not { Kind: LootTrackerSessionKind.Mixed } active) return;
        if (active.FarmDay != LootFarmDay.Get(at) || _time.GetElapsedTime(_lastActivityStamp, stamp) >=
            TimeSpan.FromMinutes(LootTrackerPresentation.NormalizeIdleTimeoutMinutes(_settings.Current.LootTrackerIdleTimeoutMinutes)))
            Finish(LootTrackerSessionStatus.Completed);
    }

    private void Presence(DateTimeOffset at, long stamp)
    {
        if (_active is not { Kind: LootTrackerSessionKind.Abyss } active) return;
        _active = active with { LastPresenceAt = Later(active.LastPresenceAt, at), Elapsed = MaxElapsed(active.Elapsed, Elapsed(stamp)) };
        if (_time.GetElapsedTime(_lastPresenceSave, stamp) >= TimeSpan.FromSeconds(10))
        { Save(_active); _lastPresenceSave = stamp; }
    }

    private void Save(LootTrackerSession next)
    {
        _store.Append(next); _active = _durable = next;
        if (_announcedSession != next.Id)
        {
            _replayPositions?.Start(next.Id, _startStamp, _own?.Generation, _observedWorld?.Generation);
            _announcedSession = next.Id;
            // Session callbacks may enable the HUD and accept the first receipt before
            // the regular command publication. They must already see this committed session.
            Publish(notify: false);
            SafeEvent.Raise(SessionStarted, next.Id, "Neue Loot-Session");
        }
    }

    private void Finish(LootTrackerSessionStatus status)
    {
        if (_active is not { } active) return;
        var abyss = active.Kind == LootTrackerSessionKind.Abyss;
        var end = (abyss ? active.LastPresenceAt : active.LastLootAt) ?? active.StartedAt;
        var elapsed = abyss ? active.Elapsed : TimeSpan.FromTicks(_lastActivityElapsedTicks);
        var finished = active with { Status = status, EndedAt = end, Elapsed = elapsed,
            RouteSegments = UnifiedLootSessionStore.TrimRoute(active, end, elapsed) };
        if (_durable is not null && Volatile.Read(ref _fatal) == 0) _store.Append(finished);
        if (_durable is not null) _history.Insert(0, finished);
        if (_replayPositions is not null) _ = _replayPositions.SealAsync(elapsed);
        _active = _durable = null; _activeIdentity = null; BreakRoute(); ResetRankObservation();
    }

    // The single worker must survive every command: a dead reader would silently stop recording
    // and leave pending deletes and flushes waiting forever.
    private void Stop(Exception error, Command? command)
    {
        if (!StorageFailure(error) && error is not OverflowException) LogFile.Error("Loot-Aufzeichnung", error);
        try { Fail(); }
        catch (Exception failure) when (StorageFailure(failure)) { }
        if (command is Delete delete) delete.Completion.TrySetException(error);
    }

    private void Fail()
    {
        Interlocked.Exchange(ref _fatal, 1); _storageIssue = true;
        if (_durable is { } saved)
        {
            _active = saved;
            Finish(LootTrackerSessionStatus.Interrupted);
        }
        else _active = null;
    }

    private void Publish(bool notify = true)
    {
        var now = _time.GetUtcNow(); var world = _world(); var own = CurrentOwn;
        _available = Volatile.Read(ref _fatal) == 0 && _settings.Current.LootTrackingEnabled && Running
            && ValidCharacter(own is null ? null : _bind(own)) is not null && QualifiedWorld(world, own, now)
            && (IsAbyss(world) ? _settings.Current.AbyssTrackingEnabled && _abyss.Running
                : _settings.Current.MonsterLootTrackingEnabled && _farm.Running
                    || _settings.Current.LifeskillTrackingEnabled && _gathering.Running && _gathering.GatheringVerified);
        _overlayCandidate = _available && _active is not null && ReferenceEquals(own, _own)
            && (_active.Kind == LootTrackerSessionKind.Abyss) == IsAbyss(world);
        if (_available) _lease ??= _acquire?.Invoke(); else { _lease?.Dispose(); _lease = null; }
        var sessions = _history.ToImmutableArray();
        if (_active is { } active) sessions = sessions.Insert(0, active with { Elapsed = Elapsed(_time.GetTimestamp()) });
        Volatile.Write(ref _snapshot, new(sessions, now));
        _dailyConfirmed = _daily.Character is not null && ReferenceEquals(_daily.Character, own) && _daily.At is { } at
            && _daily.Total is >= 0 && at <= now && LootFarmDay.Get(at) == LootFarmDay.Get(now)
            && _settings.Current.LootTrackingEnabled && _farm.Running;
        var currentProfile = own is null ? null : ValidCharacter(_bind(own))?.ProfileId;
        var overlayDaily = own is not null && ReferenceEquals(_daily.Character, own) ? _daily : new(null, null, null, 0);
        Volatile.Write(ref _overlayState, new(_overlayCandidate ? _active! with { Elapsed = Elapsed(_time.GetTimestamp()) } : null,
            _available, overlayDaily, _cachedDaily?.ProfileId == currentProfile ? _cachedDaily : null, _dailyConfirmed, now));
        _statusError = _storageIssue || Volatile.Read(ref _fatal) != 0 || _gatheringWarning is not null;
        _status = Volatile.Read(ref _fatal) != 0 ? "Loot-Aufzeichnung wegen eines Speicherfehlers unterbrochen."
            : _storageIssue ? "Loot-Verlauf ist unvollständig."
            : !_settings.Current.LootTrackingEnabled ? "Loot-Aufzeichnung ist ausgeschaltet."
            : !_available ? "Warte auf einen bestätigten eigenen Weltkontext."
            : _cleanupPending ? "Session gelöscht; Dateibereinigung ausstehend."
            : _gatheringWarning is { } warning ? warning
            : _active is null ? "Loot-Aufzeichnung bereit." : "Loot-Session läuft.";
        if (notify) SafeEvent.Raise(Changed, "Loot-Verlauf");
    }

    private async Task DeleteSession(Delete command)
    {
        try
        {
            var session = _active?.Id == command.Id ? _active : _history.FirstOrDefault(row => row.Id == command.Id);
            if (session is null) { command.Completion.TrySetResult(); return; }
            if (_replayPositions is not null && session.UnifiedVersion != 0)
                await _replayPositions.DeleteAsync(command.Id).ConfigureAwait(false);
            var pending = session.UnifiedVersion == 0
                ? (_deleteLegacy ?? throw new InvalidOperationException("Legacy history is read-only."))(session)
                : _store.Delete(command.Id);
            _cleanupPending |= pending;
            if (_active?.Id == command.Id)
            {
                if (_active.Kind == LootTrackerSessionKind.Abyss) _suppressedAbyssProfile = _active.Character?.ProfileId;
                _active = _durable = null; _activeIdentity = null; BreakRoute(); ResetRankObservation();
            }
            _history.RemoveAll(row => row.Id == command.Id); command.Completion.TrySetResult();
        }
        catch (Exception error) when (StorageFailure(error) || error is InvalidOperationException or KeyNotFoundException)
        { _storageIssue = true; command.Completion.TrySetException(error); }
    }

    public async Task DeleteSessionAsync(string sessionId)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _commands.Writer.WriteAsync(new Delete(sessionId, completion)).ConfigureAwait(false);
        await completion.Task.ConfigureAwait(false);
    }
    public async Task FlushAsync()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _commands.Writer.WriteAsync(new Barrier(completion)).ConfigureAwait(false); await completion.Task.ConfigureAwait(false);
    }
    internal Task DrainAsync() => FlushAsync();
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _timer.Dispose(); foreach (var detach in _detach) detach();
        _farm.MonsterKinaGained -= MonsterKina; _farm.FarmDropGained -= FarmDrop;
        _kina.KinaGained -= AbyssKina; _gathering.GatheringCompleted -= Gathered;
        _abyss.AbyssPointsGained -= PointsGained; _abyss.RankPointsChanged -= RankChanged;
        if (_combat is not null) _combat.CombatEvent -= CombatObserved;
        _commands.Writer.TryComplete(); await _worker.ConfigureAwait(false);
    }

    private TimeSpan Elapsed(long stamp) => _time.GetElapsedTime(_startStamp, stamp) is var elapsed && elapsed >= TimeSpan.Zero ? elapsed : TimeSpan.Zero;
    private static TimeSpan MaxElapsed(TimeSpan first, TimeSpan second) => first >= second ? first : second;
    private static DateTimeOffset Later(DateTimeOffset? previous, DateTimeOffset next) => previous > next ? previous.Value : next;
    private static bool StorageFailure(Exception error) => error is IOException or UnauthorizedAccessException or InvalidDataException;
    private static bool SameCharacter(OwnCharacterIdentity a, OwnCharacterIdentity b) => a.CharacterId == b.CharacterId && a.ServerId == b.ServerId;
    private static LootTrackerCharacterSnapshot? ValidCharacter(LootTrackerCharacterSnapshot? value) => value is not null
        && Guid.TryParse(value.ProfileId, out var id) && id != Guid.Empty && !string.IsNullOrWhiteSpace(value.Name)
        ? value with { ProfileId = id.ToString("N") } : null;
    private static LootTrackerSession InterruptLegacy(LootTrackerSession value) => value.Status != LootTrackerSessionStatus.Live ? value
        : value with { Status = LootTrackerSessionStatus.Interrupted, EndedAt = value.LastPresenceAt ?? value.LastLootAt ?? value.StartedAt };
}
