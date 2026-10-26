using System.Collections.Immutable;
using System.Threading.Channels;
using Soulcrest.App.Network;
using Soulcrest.App.Overlay;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Services;

/// <summary>
/// The shared passive source supplies absolute daily totals and already deduplicated gains.
/// One background reader owns session mutation and disk I/O. Native identities never reach a file.
/// </summary>
public sealed class FarmLootTrackerService : ILootOverlaySource, IDisposable, IAsyncDisposable
{
    private readonly SettingsService _settings;
    private readonly INetworkFarmSource _source;
    private readonly Func<OwnCharacterContext, LootTrackerCharacterSnapshot?> _bind;
    private readonly Func<WorldContextSnapshot> _world;
    private readonly Func<RecorderPositionSnapshot?> _position;
    private readonly Func<IDisposable>? _acquireRecorder;
    private readonly Func<string, string> _mapName;
    private readonly Func<RecorderPositionSnapshot, FarmLocationMatch?>? _locationName;
    private bool _locationResolved;
    private IDisposable? _recorderLease;
    private (string Map, long MapGeneration, long? WorldGeneration, long? BindingGeneration)? _routeContext;
    private bool _routeBroken = true;
    private long? _lastPositionStamp;
    private DateTimeOffset? _lastPositionEvidenceAt, _lastPositionRecordedAt;
    private readonly Action? _detachWorld;
    private readonly FarmLootSessionStore _sessions;
    private readonly DailyMonsterKinaStore _dailyStore;
    private readonly TimeProvider _time;
    private readonly Channel<Command> _commands = Channel.CreateBounded<Command>(new BoundedChannelOptions(4096)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task _worker;
    private readonly ITimer _timer;
    private readonly List<LootTrackerSession> _history = [];
    private LootTrackerSnapshot _sessionSnapshot = LootTrackerSnapshot.Empty;
    private LootOverlaySnapshot _overlay = LootOverlaySnapshot.Empty;
    private LootTrackerSession? _active;
    private LootTrackerCharacterSnapshot? _character;
    private OwnCharacterContext? _own;
    private SavedDailyMonsterKina? _daily;
    private bool _dailyConfirmed, _enabled, _networkEnabled, _running, _storageError, _cleanupPending;
    private long _startStamp, _lastLootStamp;
    private long _dailyGeneration = -1, _dailySequence = -1;
    private long _incomeGeneration = -1, _incomeSequence = -1;
    private long _dropGeneration = -1, _dropSequence = -1;
    private int _disposed, _overflow, _fatal;
    private volatile bool _available, _statusError;
    private volatile string _status = "Monsterloot-Verlauf wird geladen …";

    public FarmLootTrackerService(SettingsService settings, INetworkFarmSource source,
        ExplorationService exploration, WorldContextService world, MapTrackingService tracking, ProgressService progress)
        : this(settings, source, exploration.TryGetBoundCharacter, () => world.Snapshot,
            new(Path.Combine(AppPaths.DataDirectory, "loot-tracker", "monster-sessions")),
            new(Path.Combine(AppPaths.DataDirectory, "loot-tracker", "daily-monster-kina.json")), TimeProvider.System,
            () => tracking.TryGetRecorderPositionSnapshot(out var position) ? position : null,
            tracking.AcquireRecorderTracking, id => progress.Maps.FirstOrDefault(map => map.Id == id)?.Label ?? id,
            CreateLocationResolver(settings, progress))
    {
        world.Changed += SourceChanged;
        _detachWorld = () => world.Changed -= SourceChanged;
    }

    internal FarmLootTrackerService(SettingsService settings, INetworkFarmSource source,
        Func<OwnCharacterContext, LootTrackerCharacterSnapshot?> bind, Func<WorldContextSnapshot> world,
        FarmLootSessionStore sessions, DailyMonsterKinaStore dailyStore, TimeProvider time,
        Func<RecorderPositionSnapshot?>? position = null, Func<IDisposable>? acquireRecorder = null,
        Func<string, string>? mapName = null, Func<RecorderPositionSnapshot, FarmLocationMatch?>? locationName = null)
    {
        _settings = settings; _source = source; _bind = bind; _world = world;
        _sessions = sessions; _dailyStore = dailyStore; _time = time;
        _position = position ?? (() => null); _acquireRecorder = acquireRecorder; _mapName = mapName ?? (id => id);
        _locationName = locationName;
        settings.Changed += SettingsChanged;
        source.Changed += SourceChanged;
        source.OwnCharacterChanged += SourceChanged;
        source.MonsterKinaGained += KinaGained;
        source.FarmDropGained += DropGained;
        _worker = Task.Run(Run);
        SettingsChanged(); SourceChanged();
        _timer = time.CreateTimer(_ => Enqueue(new Tick(time.GetTimestamp())), null,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public LootOverlaySnapshot Snapshot => Volatile.Read(ref _overlay);
    public LootTrackerSnapshot SessionSnapshot => Volatile.Read(ref _sessionSnapshot);
    public bool SourceAvailable => _available;
    public string Status => _status;
    public bool StatusIsError => _statusError;
    public event Action? Changed;
    public event Action<AcceptedFarmLootDrop>? DropAccepted;
    public event Action<string>? SessionStarted;

    private abstract record Command;
    private sealed record Options(bool Enabled, bool NetworkEnabled) : Command;
    private sealed record Observation(bool Running, OwnCharacterContext? Own,
        LootTrackerCharacterSnapshot? Character, NetworkDailyMonsterKinaSnapshot Daily) : Command;
    private sealed record Income(NetworkKinaGain Gain, LootTrackerCharacterSnapshot? Character,
        WorldContextSnapshot World, NetworkDailyMonsterKinaSnapshot Daily, RecorderPositionSnapshot? Position, long Stamp) : Command;
    private sealed record Drop(NetworkFarmDrop Gain, LootTrackerCharacterSnapshot? Character,
        WorldContextSnapshot World, NetworkDailyMonsterKinaSnapshot Daily, RecorderPositionSnapshot? Position, long Stamp) : Command;
    private sealed record Tick(long Stamp) : Command;
    private sealed record Delete(string Id, TaskCompletionSource Completion) : Command;
    private sealed record Barrier(TaskCompletionSource Completion) : Command;

    private void SettingsChanged() => Enqueue(new Options(_settings.Current.MonsterLootTrackingEnabled, _settings.Current.LootTrackingEnabled));
    private void SourceChanged()
    {
        // The binding copy is qualified against this exact source context, never the selected UI profile.
        var daily = _source.DailyMonsterKinaSnapshot;
        var own = _source.OwnCharacter;
        var character = own is null ? null : _bind(own);
        if (daily.Character is not null && !ReferenceEquals(daily.Character, own))
            daily = new(null, null, null, 0);
        Enqueue(new Observation(_source.Running, own, character, daily));
    }
    private void KinaGained(NetworkKinaGain gain)
    {
        if (!_settings.Current.MonsterLootTrackingEnabled || !_settings.Current.LootTrackingEnabled || !_source.Running
            || !ReferenceEquals(_source.OwnCharacter, gain.Character) || gain.InInstance) return;
        Enqueue(new Income(gain, _bind(gain.Character), _world(), _source.DailyMonsterKinaSnapshot, _position(), _time.GetTimestamp()));
    }
    private void DropGained(NetworkFarmDrop gain)
    {
        if (!_settings.Current.MonsterLootTrackingEnabled || !_settings.Current.LootTrackingEnabled || !_source.Running
            || !ReferenceEquals(_source.OwnCharacter, gain.Character) || gain.InInstance) return;
        Enqueue(new Drop(gain, _bind(gain.Character), _world(), _source.DailyMonsterKinaSnapshot, _position(), _time.GetTimestamp()));
    }
    private void Enqueue(Command command)
    {
        if (Volatile.Read(ref _disposed) == 0 && !_commands.Writer.TryWrite(command))
            Interlocked.Exchange(ref _overflow, 1);
    }

    private async Task Run()
    {
        try
        {
            try
            {
                var saved = _sessions.Read(); _history.AddRange(saved.Sessions);
                _storageError = saved.Issues.Count > 0; _cleanupPending = saved.CleanupPending;
            }
            catch (Exception error) when (StorageFailure(error)) { _storageError = true; }
            await foreach (var command in _commands.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (Interlocked.Exchange(ref _overflow, 0) != 0) Fail();
                try
                {
                    switch (command)
                    {
                        case Options options:
                            _enabled = options.Enabled;
                            _networkEnabled = options.NetworkEnabled;
                            if (!_networkEnabled) _dailyConfirmed = false;
                            if (!_enabled || !_networkEnabled) Finish(LootTrackerSessionStatus.Completed);
                            break;
                        case Observation observation: Observe(observation); break;
                        case Income income: AcceptIncome(income); break;
                        case Drop drop: AcceptDrop(drop); break;
                        case Tick tick:
                            Expire(tick.Stamp);
                            // A fresh absolute baseline may confirm the cap without a gain after a load.
                            // Settle on the timer, after the source's related gain callbacks, so the
                            // final positive difference is still committed to the normal session.
                            if (_active is { Mode: LootTrackerSessionMode.Normal } active && _dailyConfirmed
                                && _daily is { Total: >= LootOverlaySnapshot.DailyLimit } daily
                                && active.Character?.ProfileId == daily.ProfileId)
                                Finish(LootTrackerSessionStatus.Completed);
                            if (_active is not null)
                                RecordPosition(_position(), _world(), _own, _time.GetUtcNow(), _time.GetTimestamp(), false);
                            break;
                        case Delete delete: DeleteSession(delete); continue;
                        case Barrier barrier: Publish(); barrier.Completion.TrySetResult(); continue;
                    }
                }
                catch (Exception error) when (StorageFailure(error) || error is OverflowException) { Fail(); }
                Publish();
            }
        }
        finally
        {
            try { Finish(LootTrackerSessionStatus.Interrupted); }
            catch (Exception error) when (StorageFailure(error)) { _storageError = true; }
            _running = false; Publish();
            while (_commands.Reader.TryRead(out var command))
            {
                if (command is Delete delete) delete.Completion.TrySetException(new ObjectDisposedException(nameof(FarmLootTrackerService)));
                if (command is Barrier barrier) barrier.Completion.TrySetException(new ObjectDisposedException(nameof(FarmLootTrackerService)));
            }
        }
    }

    private void Observe(Observation observation)
    {
        _running = observation.Running;
        var profile = ValidCharacter(observation.Character);
        if (_active is not null && profile is not null && _active.Character?.ProfileId != profile.ProfileId)
            Finish(LootTrackerSessionStatus.Completed);
        if (_active is not null && (!observation.Running || !ReferenceEquals(_own, observation.Own)))
        {
            BreakRoute();
            Save(_active with { CoverageLimited = true });
        }
        if (_character?.ProfileId != profile?.ProfileId)
        {
            _daily = null; _dailyConfirmed = false;
            if (profile is not null)
            {
                try { _daily = _dailyStore.Read(profile.ProfileId!); }
                catch (Exception error) when (StorageFailure(error)) { _storageError = true; }
            }
        }
        _own = observation.Own; _character = profile;
        var daily = observation.Daily;
        if (!_networkEnabled || !observation.Running || observation.Own is null || profile is null
            || !ReferenceEquals(daily.Character, observation.Own) || daily.Total is not { } total || daily.At is not { } at)
        { _dailyConfirmed = false; return; }
        if (_dailyGeneration == observation.Own.Generation && daily.Sequence <= _dailySequence)
        {
            if (daily.Sequence == _dailySequence && _daily is { } saved && saved.Total == total && saved.ObservedAt == at)
                _dailyConfirmed = LootFarmDay.Get(at) == LootFarmDay.Get(_time.GetUtcNow());
            return;
        }
        var record = new SavedDailyMonsterKina(profile.ProfileId!, total, at);
        // Replace absolute totals, including lower server values. Never add the wallet/event gain here.
        _daily = record; _dailyConfirmed = LootFarmDay.Get(at) == LootFarmDay.Get(_time.GetUtcNow());
        try
        {
            _dailyStore.Save(record);
            _dailyGeneration = observation.Own.Generation; _dailySequence = daily.Sequence;
        }
        catch (Exception error) when (StorageFailure(error)) { _storageError = true; }
    }

    private bool Prepare(LootTrackerCharacterSnapshot? character, OwnCharacterContext own,
        WorldContextSnapshot world, DateTimeOffset at, long stamp, LootTrackerSessionMode mode)
    {
        if (!_enabled || !_networkEnabled || Volatile.Read(ref _fatal) != 0 || ValidCharacter(character) is not { } valid) return false;
        // A positive own open-world observation is required. Unknown worlds or stale observations
        // cannot distinguish a dungeon from farming and must not create a falsely classified session.
        if (!IsOpenWorld(world, own, at)) return false;
        Expire(stamp);
        if (_active is not null && _active.Character?.ProfileId != valid.ProfileId) Finish(LootTrackerSessionStatus.Completed);
        if (_active is not null && (_active.FarmDay != LootFarmDay.Get(at) || _active.Mode != mode))
            Finish(LootTrackerSessionStatus.Completed);
        if (_active is null)
        {
            _startStamp = stamp;
            BreakRoute(); _lastPositionStamp = null; _locationResolved = false;
            var confirmedWorld = world.Status == WorldContextStatus.Confirmed && world.BindingGeneration == own.Generation;
            _active = new()
            {
                Id = Guid.NewGuid().ToString("N"), Kind = LootTrackerSessionKind.Grind,
                Status = LootTrackerSessionStatus.Live, StartedAt = at, Character = valid,
                FarmDay = LootFarmDay.Get(at), Mode = mode,
                Location = confirmedWorld ? (_settings.Current.LootNameLanguage == "de" ? world.NameDe ?? world.NameEn : world.NameEn ?? world.NameDe) ?? "" : "",
                KinaUnbound = 0, ItemCount = 0, SoulCount = 0,
                CoverageLimited = true // Own-mob joins and positive bound rewards remain unqualified.
            };
        }
        return true;
    }

    private void AcceptIncome(Income command)
    {
        var gain = command.Gain;
        if (gain.UnboundKina <= 0 || gain.InInstance || gain.Sequence <= 0
            || _incomeGeneration == gain.Character.Generation && gain.Sequence <= _incomeSequence) return;
        var mode = ModeFor(command.Daily, gain.Character, gain.At);
        var reachesLimit = false;
        if (QualifiedDaily(command.Daily, gain.Character, gain.At)
            && command.Daily.Sequence == gain.Sequence && command.Daily.Total is { } total)
        {
            // The absolute update can arrive before OR after its gain callback. The correlated
            // positive difference still belongs to the pre-cap session, never to a new Limited one.
            var before = total - gain.UnboundKina;
            reachesLimit = before < LootOverlaySnapshot.DailyLimit && total >= LootOverlaySnapshot.DailyLimit;
            if (reachesLimit) mode = LootTrackerSessionMode.Normal;
        }
        if (!Prepare(command.Character, gain.Character, command.World, gain.At, command.Stamp, mode)) return;
        RecordPosition(command.Position, command.World, gain.Character, gain.At, command.Stamp, true);
        var active = _active!;
        var amount = checked((active.KinaUnbound ?? 0) + gain.UnboundKina);
        var elapsed = _time.GetElapsedTime(_startStamp, command.Stamp);
        var minute = (int)Math.Min(int.MaxValue, Math.Max(0, Math.Floor(elapsed.TotalMinutes)));
        var bins = active.MinuteIncome.ToBuilder();
        var index = -1;
        for (var i = 0; i < bins.Count; i++) if (bins[i].Minute == minute) { index = i; break; }
        if (index < 0) bins.Add(new(minute, null, gain.UnboundKina));
        else bins[index] = bins[index] with { KinaUnbound = checked((bins[index].KinaUnbound ?? 0) + gain.UnboundKina) };
        Save(active with { KinaUnbound = amount, LastLootAt = gain.At, Elapsed = elapsed, MinuteIncome = bins.ToImmutable() });
        _lastLootStamp = command.Stamp; _incomeGeneration = gain.Character.Generation; _incomeSequence = gain.Sequence;
        if (reachesLimit) Finish(LootTrackerSessionStatus.Completed);
    }

    private void AcceptDrop(Drop command)
    {
        var gain = command.Gain;
        if (gain.Quantity <= 0 || gain.DataId <= 0 || gain.Sequence <= 0 || gain.InInstance || !Enum.IsDefined(gain.Kind)
            || gain.Kind == NetworkFarmDropKind.UniversalSoulCrystal && gain.DataId != NetworkFarmDrop.UniversalSoulCrystalItemId
            || _dropGeneration == gain.Character.Generation && gain.Sequence <= _dropSequence) return;
        if (!Prepare(command.Character, gain.Character, command.World, gain.At, command.Stamp,
            ModeFor(command.Daily, gain.Character, gain.At))) return;
        RecordPosition(command.Position, command.World, gain.Character, gain.At, command.Stamp, true);
        var active = _active!;
        var key = $"{gain.Kind}:{gain.DataId}";
        var loot = active.Loot.ToBuilder();
        var index = -1;
        for (var i = 0; i < loot.Count; i++) if (loot[i].Id == key) { index = i; break; }
        if (index < 0) loot.Add(new(key, DropName(gain.Kind, gain.DataId),
            gain.Kind == NetworkFarmDropKind.Item ? LootTrackerLootKind.Item : LootTrackerLootKind.Soul, gain.Quantity));
        else loot[index] = loot[index] with { Quantity = checked(loot[index].Quantity + gain.Quantity) };
        Save(active with
        {
            LastLootAt = gain.At, Elapsed = _time.GetElapsedTime(_startStamp, command.Stamp), Loot = loot.ToImmutable(),
            ItemCount = checked((active.ItemCount ?? 0) + (gain.Kind == NetworkFarmDropKind.Item ? gain.Quantity : 0)),
            SoulCount = checked((active.SoulCount ?? 0) + (gain.Kind != NetworkFarmDropKind.Item ? gain.Quantity : 0))
        });
        _lastLootStamp = command.Stamp; _dropGeneration = gain.Character.Generation; _dropSequence = gain.Sequence;
        SafeEvent.Raise(DropAccepted, new AcceptedFarmLootDrop(active.Id, active.Character!.ProfileId!, gain.At,
            key, loot[index < 0 ? loot.Count - 1 : index].Name, gain.Quantity), "Neuer Monsterloot");
    }

    private static bool QualifiedDaily(NetworkDailyMonsterKinaSnapshot daily, OwnCharacterContext own, DateTimeOffset at) =>
        ReferenceEquals(daily.Character, own) && daily.Total is >= 0 && daily.At is { } observed
        && LootFarmDay.Get(observed) == LootFarmDay.Get(at);

    private static LootTrackerSessionMode ModeFor(NetworkDailyMonsterKinaSnapshot daily, OwnCharacterContext own, DateTimeOffset at) =>
        QualifiedDaily(daily, own, at) && daily.Total >= LootOverlaySnapshot.DailyLimit
            ? LootTrackerSessionMode.LimitedKina : LootTrackerSessionMode.Normal;

    private static bool IsOpenWorld(WorldContextSnapshot world, OwnCharacterContext? own, DateTimeOffset at) =>
        own is not null && world.Status == WorldContextStatus.Confirmed && world.Kind == WorldContextKind.OpenWorld
        && world.BindingGeneration == own.Generation && world.ObservedAt is { } observed && observed <= at;

    private void RecordPosition(RecorderPositionSnapshot? snapshot, WorldContextSnapshot world,
        OwnCharacterContext? own, DateTimeOffset at, long stamp, bool lootPosition)
    {
        if (_active is null) return;
        if (!_enabled || !_networkEnabled || !_running || _character?.ProfileId != _active.Character?.ProfileId
            || !IsOpenWorld(world, own, at) || snapshot is not { } captured || captured.Position is not { } position
            || string.IsNullOrWhiteSpace(position.MapId) || position.ContextGeneration != captured.MapGeneration
            || captured.BindingGeneration != own!.Generation || captured.WorldGeneration != world.Generation
            || world.SoulcrestMapId is { Length: > 0 } mapId && mapId != position.MapId
            || position.At > at || at - position.At > TimeSpan.FromSeconds(2)
            || captured.DetectedAt > at || at - captured.DetectedAt > TimeSpan.FromSeconds(5)
            || !double.IsFinite(position.X) || !double.IsFinite(position.Y) || position.X < 0 || position.Y < 0)
        { BreakRoute(); return; }
        if (lootPosition && !_locationResolved && _locationName?.Invoke(captured) is { Name: { } location } match
            && !string.IsNullOrWhiteSpace(location) && location.Length <= 1024 && !location.Any(char.IsControl))
        { _active = _active with { Location = location.Trim(), LocationApproximate = match.Approximate }; _locationResolved = true; }
        var context = (position.MapId, captured.MapGeneration, captured.WorldGeneration, captured.BindingGeneration);
        if (_routeContext is { } previous && previous != context) BreakRoute();
        if (_lastPositionRecordedAt is { } recorded && at - recorded > TimeSpan.FromSeconds(2)) BreakRoute();
        _routeContext = context;
        // A copied old placement never becomes another observation merely because a timer fired.
        if (!_routeBroken && _lastPositionEvidenceAt is { } evidence && position.At <= evidence) return;
        if (!lootPosition && !_routeBroken && _lastPositionStamp is { } lastStamp
            && _time.GetElapsedTime(lastStamp, stamp) < TimeSpan.FromSeconds(1)) return;
        var elapsed = _time.GetElapsedTime(_startStamp, stamp);
        var point = new LootTrackerRoutePoint(position.X, position.Y, at, elapsed);
        var segments = _active.RouteSegments.ToBuilder();
        if (_routeBroken || segments.Count == 0)
            segments.Add(new(position.MapId, _mapName(position.MapId), [point]));
        else
            segments[^1] = segments[^1] with { Points = segments[^1].Points.Add(point) };
        // Provisional movement after the latest loot stays in memory. The next loot journals the
        // entire newly qualified part; finish/recovery trim the unproductive tail instead of
        // rewriting a growing full journal snapshot every second.
        _active = _active with { RouteSegments = segments.ToImmutable() };
        _routeBroken = false; _lastPositionStamp = stamp;
        _lastPositionEvidenceAt = position.At; _lastPositionRecordedAt = at;
    }

    private void BreakRoute()
    {
        _routeBroken = true; _routeContext = null;
        _lastPositionEvidenceAt = null; _lastPositionRecordedAt = null;
    }

    private static Func<RecorderPositionSnapshot, FarmLocationMatch?> CreateLocationResolver(SettingsService settings, ProgressService progress)
    {
        var catalog = FarmLocationCatalog.Load(progress.MapDataDirectory);
        return position => catalog.Resolve(position, settings.Current.LootNameLanguage);
    }

    private void ReconcileRecorder(bool available)
    {
        if (_active is not null && available)
        { if (_recorderLease is null && _acquireRecorder is not null) _recorderLease = _acquireRecorder(); }
        else
        {
            _recorderLease?.Dispose(); _recorderLease = null;
            BreakRoute();
        }
    }

    private string DropName(NetworkFarmDropKind kind, int id)
    {
        if (kind == NetworkFarmDropKind.Item) return MonsterLootItemCatalog.Default.Name(id, _settings.Current.LootNameLanguage);
        if (kind == NetworkFarmDropKind.UniversalSoulCrystal)
            return MonsterLootItemCatalog.Default.Name(NetworkFarmDrop.UniversalSoulCrystalItemId, _settings.Current.LootNameLanguage);
        if (kind == NetworkFarmDropKind.PetSoul)
        {
            var pet = PetIdTable.Find(id);
            return pet?.En ?? $"Pet #{id}";
        }
        // Explicit native crystal enum, distinct from the creature genus enum.
        var itemId = id switch { 1 => 531900005, 2 => 531900004, 3 => 531900006, 4 => 531900007, 5 => 531900008, _ => 0 };
        return MonsterLootItemCatalog.Default.Name(itemId, _settings.Current.LootNameLanguage);
    }

    private void Expire(long stamp)
    {
        var day = LootFarmDay.Get(_time.GetUtcNow());
        if (_daily is { } daily && LootFarmDay.Get(daily.ObservedAt) != day) _dailyConfirmed = false;
        if (_active is { } active && (active.FarmDay ?? LootFarmDay.Get(active.StartedAt)) != day)
            Finish(LootTrackerSessionStatus.Completed);
        if (_active?.LastLootAt is not null && _time.GetElapsedTime(_lastLootStamp, stamp)
            >= TimeSpan.FromMinutes(LootTrackerPresentation.NormalizeIdleTimeoutMinutes(_settings.Current.LootTrackerIdleTimeoutMinutes)))
            Finish(LootTrackerSessionStatus.Completed);
    }
    private void Save(LootTrackerSession next)
    {
        var started = _active?.LastLootAt is null && next.LastLootAt is not null;
        _sessions.Append(next); _active = next;
        if (started) SafeEvent.Raise(SessionStarted, next.Id, "Neue PvE-Session");
    }
    private void Finish(LootTrackerSessionStatus status)
    {
        if (_active is not { LastLootAt: not null } active) { _active = null; return; }
        var finished = active with { Status = status, EndedAt = active.LastLootAt,
            RouteSegments = FarmLootSessionStore.TrimRoute(active, active.LastLootAt.Value, active.Elapsed) };
        if (Volatile.Read(ref _fatal) == 0) _sessions.Append(finished);
        else finished = finished with { Status = LootTrackerSessionStatus.Interrupted };
        _history.Insert(0, finished); _active = null;
    }
    private void Fail()
    {
        Interlocked.Exchange(ref _fatal, 1); _storageError = true;
        Finish(LootTrackerSessionStatus.Interrupted);
    }
    private void Publish()
    {
        var openWorld = IsOpenWorld(_world(), _own, _time.GetUtcNow());
        _available = _enabled && _networkEnabled && _running && _character is not null && openWorld && Volatile.Read(ref _fatal) == 0;
        ReconcileRecorder(_available);
        _statusError = _storageError;
        _status = _storageError ? "Open-World-PvE: Speicher- oder Empfangsfehler. Verlauf prüfen."
            : !_enabled ? "Open-World-PvE-Aufzeichnung ist ausgeschaltet."
            : !_running || !_networkEnabled ? "Netzwerk-Tracking ist ausgeschaltet."
            : _character is null ? "Warte auf die eigene Charaktererkennung. Bitte einen Ladebildschirm auslösen."
            : !openWorld ? "Warte auf bestätigten Open-World-Aufenthalt."
            : _cleanupPending ? "Session gelöscht; Dateibereinigung ausstehend."
            : _active is null ? "Open-World-PvE-Aufzeichnung bereit." : "Open-World-PvE-Session läuft.";
        // Live rates use observed running time. The durable state retains the last-loot duration,
        // so timeout/recovery never include the inactivity tail as completed farming time.
        var live = _active?.LastLootAt is not null ? _active with
        { Elapsed = _time.GetElapsedTime(_startStamp, _time.GetTimestamp()) } : null;
        var sessions = live is not null ? _history.Prepend(live).ToImmutableArray() : _history.ToImmutableArray();
        Volatile.Write(ref _sessionSnapshot, new(sessions, _time.GetUtcNow()));
        var current = _active?.Character?.ProfileId == _character?.ProfileId ? _active : null;
        var completionScope = _character is null ? null
            : $"{_character.ProfileId}:{LootFarmDay.Get(_time.GetUtcNow()):yyyy-MM-dd}";
        var elapsed = current is null ? TimeSpan.Zero : _time.GetElapsedTime(_startStamp, _time.GetTimestamp());
        Volatile.Write(ref _overlay, new(current?.Id, current is not null, current?.KinaUnbound ?? 0,
            _daily?.Total, LootOverlayDropPresentation.GroupCommonGear(current?.Loot.Select(item => new LootOverlayDrop(item.Id, item.Name, item.Quantity,
                item.Kind == LootTrackerLootKind.Soul ? LootOverlayDropKind.SoulCrystal : LootOverlayDropKind.Item)
                { IconPath = MonsterLootIconCatalog.Default.DropAsset(item.Id),
                    Rarity = MonsterLootItemCatalog.Default.DropRarity(item.Id),
                }).ToImmutableArray() ?? []),
            _available, completionScope)
            { DailyConfirmed = _dailyConfirmed, KinaIconPath = MonsterLootIconCatalog.Default.ItemAsset(930100031),
                AnimationsEnabled = _settings.Current.LootDropAnimationsEnabled,
                LimitedKinaSession = current?.Mode == LootTrackerSessionMode.LimitedKina,
                SessionElapsed = elapsed,
                SessionKinaPerHour = LootTrackerPresentation.PerHour(current?.KinaUnbound, elapsed),
            });
        SafeEvent.Raise(Changed, "Monsterloot-Aufzeichnung");
    }

    public async Task DeleteSessionAsync(string sessionId)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!Guid.TryParseExact(sessionId, "N", out _)) throw new InvalidDataException("Invalid local session ID.");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try { await _commands.Writer.WriteAsync(new Delete(sessionId, completion)).ConfigureAwait(false); }
        catch (ChannelClosedException) { throw new ObjectDisposedException(nameof(FarmLootTrackerService)); }
        await completion.Task.ConfigureAwait(false);
    }
    private void DeleteSession(Delete command)
    {
        try
        {
            if (_active?.Id != command.Id && !_history.Any(session => session.Id == command.Id)) throw new KeyNotFoundException("Local session was not found.");
            _cleanupPending |= _sessions.Delete(command.Id);
            if (_active?.Id == command.Id) _active = null;
            _history.RemoveAll(session => session.Id == command.Id);
            Publish(); command.Completion.TrySetResult();
        }
        catch (Exception error) when (StorageFailure(error) || error is KeyNotFoundException)
        { Publish(); command.Completion.TrySetException(error); }
    }
    private static LootTrackerCharacterSnapshot? ValidCharacter(LootTrackerCharacterSnapshot? character) =>
        character is not null && Guid.TryParse(character.ProfileId, out var id) && id != Guid.Empty && !string.IsNullOrWhiteSpace(character.Name)
            ? character with { ProfileId = id.ToString("N") } : null;
    private static bool StorageFailure(Exception error) => error is IOException or UnauthorizedAccessException or InvalidDataException;
    internal async Task DrainAsync()
    {
        if (Volatile.Read(ref _disposed) != 0) { await _worker.ConfigureAwait(false); return; }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _commands.Writer.WriteAsync(new Barrier(completion)).ConfigureAwait(false);
        await completion.Task.ConfigureAwait(false);
    }
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _settings.Changed -= SettingsChanged; _source.Changed -= SourceChanged; _source.OwnCharacterChanged -= SourceChanged;
            _source.MonsterKinaGained -= KinaGained; _source.FarmDropGained -= DropGained;
            _detachWorld?.Invoke();
            _timer.Dispose(); _commands.Writer.TryComplete();
        }
        await _worker.ConfigureAwait(false);
    }
}
