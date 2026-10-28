using System.Threading.Channels;
using Soulcrest.App.Network;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Services;

/// <summary>
/// Maintains authoritative absolute Abyss values and rank-observation continuity.
/// This auxiliary cache never records sessions, income, movement or combat events.
/// </summary>
public sealed class AbyssProgressService : IDisposable, IAsyncDisposable
{
    private readonly SettingsService _settings;
    private readonly INetworkAbyssSource _source;
    private readonly Func<OwnCharacterContext, LootTrackerCharacterSnapshot?> _bind;
    private readonly Func<WorldContextSnapshot> _world;
    private readonly AbyssProgressStore _progressStore;
    private readonly TimeProvider _time;
    private readonly Channel<Command> _commands = Channel.CreateBounded<Command>(new BoundedChannelOptions(4096)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task _worker;
    private readonly ITimer _timer;
    private readonly Action? _detachWorld;
    private AbyssProgressSnapshot _progressSnapshot = AbyssProgressSnapshot.Empty;
    private LootTrackerCharacterSnapshot? _character;
    private OwnCharacterContext? _own;
    private SavedAbyssProgress? _cached;
    private NetworkAbyssSnapshot _absolute = NetworkAbyssSnapshot.Empty;
    private NetworkAbyssSnapshot _acceptedAbsolute = NetworkAbyssSnapshot.Empty;
    private WorldContextSnapshot? _observedWorld;
    private long _absoluteGeneration = -1, _absoluteSequence = -1;
    private long _rankObservationSequence, _rankContinuityRevision;
    private OwnCharacterContext? _rankMotionOwn;
    private string? _rankMotionProfile;
    private bool _rankMotionEligible;
    private bool _abyssEnabled, _monsterEnabled, _networkEnabled, _running, _storageError;
    private int _disposed, _overflow, _fatal;
    private volatile bool _available, _statusError;
    private volatile string _status = "Abyss-Verlauf wird geladen …";

    public AbyssProgressService(SettingsService settings, INetworkAbyssSource source,
        ExplorationService exploration, WorldContextService world)
        : this(settings, source, exploration.TryGetBoundCharacter, () => world.Snapshot,
            new(Path.Combine(AppPaths.DataDirectory, "loot-tracker", "abyss-progress.json")), TimeProvider.System)
    {
        world.Changed += SourceChanged; _detachWorld = () => world.Changed -= SourceChanged;
    }

    internal AbyssProgressService(SettingsService settings, INetworkAbyssSource source,
        Func<OwnCharacterContext, LootTrackerCharacterSnapshot?> bind, Func<WorldContextSnapshot> world,
        AbyssProgressStore progressStore, TimeProvider time)
    {
        _settings = settings; _source = source; _bind = bind; _world = world;
        _progressStore = progressStore; _time = time;
        settings.Changed += SettingsChanged; source.Changed += SourceChanged; source.OwnCharacterChanged += SourceChanged;
        _worker = Task.Run(Run); SettingsChanged(); SourceChanged();
        _timer = time.CreateTimer(_ => QueueTick(), null,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public AbyssProgressSnapshot ProgressSnapshot => Volatile.Read(ref _progressSnapshot);
    public bool SourceAvailable => _available;
    public string Status => _status;
    public bool StatusIsError => _statusError;
    public event Action? Changed;

    private abstract record Command;
    private sealed record Options(bool AbyssEnabled, bool MonsterEnabled, bool NetworkEnabled) : Command;
    private sealed record Observation(bool Running, OwnCharacterContext? Own, LootTrackerCharacterSnapshot? Character,
        WorldContextSnapshot World, NetworkAbyssSnapshot Absolute, DateTimeOffset At) : Command;
    private sealed record Tick(WorldContextSnapshot World, DateTimeOffset At) : Command;
    private sealed record Barrier(TaskCompletionSource Completion) : Command;

    private void SettingsChanged() => Enqueue(new Options(_settings.Current.AbyssTrackingEnabled,
        _settings.Current.MonsterLootTrackingEnabled, _settings.Current.LootTrackingEnabled));
    private void SourceChanged()
    {
        var own = _source.OwnCharacter;
        Enqueue(new Observation(_source.Running, own, own is null ? null : _bind(own), _world(),
            _source.AbyssSnapshot, _time.GetUtcNow()));
    }
    private void Enqueue(Command command)
    {
        if (Volatile.Read(ref _disposed) == 0 && !_commands.Writer.TryWrite(command)) Interlocked.Exchange(ref _overflow, 1);
    }
    private void QueueTick()
    {
        var world = _world(); // Capture the world before its observation time, never read a newer world for an old tick.
        Enqueue(new Tick(world, _time.GetUtcNow()));
    }

    private async Task Run()
    {
        try
        {
            await foreach (var command in _commands.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (Interlocked.Exchange(ref _overflow, 0) != 0) Fail();
                try
                {
                    switch (command)
                    {
                        case Options options:
                            _abyssEnabled = options.AbyssEnabled; _monsterEnabled = options.MonsterEnabled; _networkEnabled = options.NetworkEnabled;
                            break;
                        case Observation observation: Observe(observation); break;
                        case Tick tick:
                            if ((_observedWorld is null || tick.World.Generation >= _observedWorld.Generation)
                                && (tick.World.Status != WorldContextStatus.Confirmed || tick.World.BindingGeneration == _own?.Generation))
                                _observedWorld = tick.World;
                            break;
                        case Barrier barrier: Publish(); barrier.Completion.TrySetResult(); continue;
                    }
                }
                catch (Exception error) when (StorageFailure(error) || error is OverflowException) { Fail(); }
                Publish();
            }
        }
        finally
        {
            _running = false; Publish();
            while (_commands.Reader.TryRead(out var command))
                if (command is Barrier barrier) barrier.Completion.TrySetException(new ObjectDisposedException(nameof(AbyssProgressService)));
        }
    }

    private void Observe(Observation observation)
    {
        var character = ValidCharacter(observation.Character);
        if (character?.ProfileId != _character?.ProfileId)
        {
            _cached = null;
            if (character is not null)
            {
                try { _cached = _progressStore.Read(character.ProfileId!); }
                catch (Exception error) when (StorageFailure(error)) { _storageError = true; }
            }
        }
        _character = character;
        _own = observation.Own; _running = observation.Running; _observedWorld = observation.World;
        _absolute = QualifiedAbsolute(observation.Absolute, observation.Own, observation.At)
            && observation.Running && _networkEnabled ? observation.Absolute : NetworkAbyssSnapshot.Empty;
        if (_absolute.Character is { } absoluteOwn && _absoluteGeneration == absoluteOwn.Generation
            && (_absolute.Sequence < _absoluteSequence || _absolute.Sequence == _absoluteSequence && _absolute != _acceptedAbsolute))
            _absolute = NetworkAbyssSnapshot.Empty;
        if (_absolute.Character is { } own && _character is { } profile
            && (_absoluteGeneration != own.Generation || _absolute.Sequence > _absoluteSequence))
        {
            var at = _absolute.At!.Value;
            var next = new SavedAbyssProgress(profile.ProfileId!,
                Value(_absolute.AbyssPointBalance, _absolute.AbyssPointBalanceObservedAt ?? at, _cached?.AbyssPointBalance),
                Value(_absolute.FarmedAbyssPoints, _absolute.FarmedAbyssPointsObservedAt ?? at, _cached?.FarmedAbyssPoints),
                Value(_absolute.RankPoints, _absolute.RankPointsObservedAt ?? at, _cached?.RankPoints));
            _cached = next;
            // The cache is auxiliary: a failed write keeps the confirmed values and must not stop rank tracking.
            try { _progressStore.Save(next); }
            catch (Exception error) when (StorageFailure(error)) { _storageError = true; }
            // Preserve the source's accepted fact sequence, not this service's publication count.
            // AP, farm, session and UI updates may share a newer envelope with the same rank fact.
            if (ConfirmedFact(next.RankPoints, _absolute.RankPoints, _absolute.RankPointsObservedAt, _absolute.At)
                && (!ReferenceEquals(_acceptedAbsolute.Character, own)
                    || _acceptedAbsolute.RankPoints != _absolute.RankPoints
                    || (_acceptedAbsolute.RankPointsObservedAt ?? _acceptedAbsolute.At)
                        != (_absolute.RankPointsObservedAt ?? _absolute.At)))
                _rankObservationSequence = _absolute.Sequence;
            _absoluteGeneration = own.Generation; _absoluteSequence = _absolute.Sequence;
            _acceptedAbsolute = _absolute;
        }
    }

    private static SavedAbyssValue? Value(long? value, DateTimeOffset at, SavedAbyssValue? previous) =>
        value is >= 0 && (previous is null || at >= previous.ObservedAt) ? new(value.Value, at) : previous;

    private static bool QualifiedFact(long? value, DateTimeOffset? observedAt, DateTimeOffset at) =>
        value is null || value >= 0 && (observedAt ?? at) != default && (observedAt ?? at) <= at;

    private static bool ConfirmedFact(SavedAbyssValue? cached, long? value, DateTimeOffset? observedAt, DateTimeOffset? at) =>
        cached is not null && value is >= 0 && cached.Value == value && cached.ObservedAt == (observedAt ?? at);

    private static bool QualifiedAbsolute(NetworkAbyssSnapshot snapshot, OwnCharacterContext? own, DateTimeOffset at) =>
        own is not null && ReferenceEquals(snapshot.Character, own) && !snapshot.FormatRejected && snapshot.Sequence > 0
        && snapshot.At is { } observed && observed != default && observed <= at
        && QualifiedFact(snapshot.AbyssPointBalance, snapshot.AbyssPointBalanceObservedAt, observed)
        && QualifiedFact(snapshot.FarmedAbyssPoints, snapshot.FarmedAbyssPointsObservedAt, observed)
        && QualifiedFact(snapshot.RankPoints, snapshot.RankPointsObservedAt, observed)
        && (snapshot.KinaGainsObservedAt is not { } kinaAt || kinaAt != default && kinaAt <= observed);

    private static bool CurrentWorld(WorldContextSnapshot? world, OwnCharacterContext? own, DateTimeOffset at) =>
        own is not null && world is { Status: WorldContextStatus.Confirmed }
        && world.BindingGeneration == own.Generation && world.ObservedAt is { } observed && observed <= at;
    private static bool OpenWorld(WorldContextSnapshot? world) =>
        world is { Kind: WorldContextKind.OpenWorld, InstanceLayer: not true };
    private static bool AbyssWorld(WorldContextSnapshot? world) =>
        world is { Kind: WorldContextKind.Instance, Category: "Abyss" };

    private void Fail() { Interlocked.Exchange(ref _fatal, 1); _storageError = true; }

    private void Publish()
    {
        var now = _time.GetUtcNow();
        var openWorld = OpenWorld(_observedWorld);
        var contextEnabled = openWorld ? _monsterEnabled : _abyssEnabled;
        _available = contextEnabled && _networkEnabled && _running && _character is not null
            && (openWorld || AbyssWorld(_observedWorld)) && CurrentWorld(_observedWorld, _own, now)
            && Volatile.Read(ref _fatal) == 0;
        _statusError = _storageError;
        _status = _storageError ? "Abyss: Speicher- oder Empfangsfehler. Verlauf prüfen."
            : !contextEnabled ? openWorld ? "Monsterloot-Aufzeichnung ist ausgeschaltet." : "Abyss-Aufzeichnung ist ausgeschaltet."
            : !_networkEnabled || !_running ? "Netzwerk-Tracking ist ausgeschaltet."
            : _character is null ? "Warte auf die eigene Charaktererkennung."
            : openWorld ? "Warte auf einen bestätigten eigenen Weltkontext." : "Warte auf bestätigten Abyss-Aufenthalt.";
        var confirmed = _networkEnabled && _running && _character is not null && QualifiedAbsolute(_absolute, _own, now);
        var rankConfirmed = confirmed && ConfirmedFact(_cached?.RankPoints,
            _absolute.RankPoints, _absolute.RankPointsObservedAt, _absolute.At);
        var rankTrackingActive = _available && _observedWorld?.ObservedAt is { } worldAt && worldAt != default;
        var rankEligible = rankTrackingActive && rankConfirmed && _rankObservationSequence > 0;
        // Every command publishes separately. The epoch makes a short loss of authority visible
        // even when the overlay later reads only the recovered snapshot in one BeginInvoke.
        if (!ReferenceEquals(_rankMotionOwn, _own) || _rankMotionProfile != _character?.ProfileId
            || _rankMotionEligible && !rankEligible)
            _rankContinuityRevision++;
        _rankMotionOwn = _own;
        _rankMotionProfile = _character?.ProfileId;
        _rankMotionEligible = rankEligible;
        var observedAt = new[] { _cached?.AbyssPointBalance?.ObservedAt, _cached?.FarmedAbyssPoints?.ObservedAt, _cached?.RankPoints?.ObservedAt }
            .Where(value => value is not null).DefaultIfEmpty().Max();
        Volatile.Write(ref _progressSnapshot, new(_character?.ProfileId,
            _cached?.AbyssPointBalance?.Value, _cached?.FarmedAbyssPoints?.Value, _cached?.RankPoints?.Value,
            AbyssCapSchedule.CapAt(now), observedAt, confirmed)
        { BindingGeneration = _own?.Generation,
            AbyssPointBalanceConfirmed = confirmed && ConfirmedFact(_cached?.AbyssPointBalance,
                _absolute.AbyssPointBalance, _absolute.AbyssPointBalanceObservedAt, _absolute.At),
            FarmedAbyssPointsConfirmed = confirmed && ConfirmedFact(_cached?.FarmedAbyssPoints,
                _absolute.FarmedAbyssPoints, _absolute.FarmedAbyssPointsObservedAt, _absolute.At),
            RankPointsConfirmed = rankConfirmed,
            RankObservationSequence = rankConfirmed ? _rankObservationSequence : 0,
            RankTrackingActive = rankTrackingActive,
            RankContinuityRevision = _rankContinuityRevision,
            RankPointsObservedAt = _cached?.RankPoints?.ObservedAt, NextResetAt = AbyssCapSchedule.NextReset(now) });
        SafeEvent.Raise(Changed, "Abyss-Aufzeichnung");
    }

    private static LootTrackerCharacterSnapshot? ValidCharacter(LootTrackerCharacterSnapshot? character) =>
        character is not null && Guid.TryParse(character.ProfileId, out var id) && id != Guid.Empty && !string.IsNullOrWhiteSpace(character.Name)
            ? character with { ProfileId = id.ToString("N") } : null;
    private static bool StorageFailure(Exception error) => error is IOException or UnauthorizedAccessException or InvalidDataException;
    internal async Task DrainAsync()
    {
        if (Volatile.Read(ref _disposed) != 0) { await _worker.ConfigureAwait(false); return; }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _commands.Writer.WriteAsync(new Barrier(completion)).ConfigureAwait(false); await completion.Task.ConfigureAwait(false);
    }
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _settings.Changed -= SettingsChanged; _source.Changed -= SourceChanged; _source.OwnCharacterChanged -= SourceChanged;
            _detachWorld?.Invoke(); _timer.Dispose(); _commands.Writer.TryComplete();
        }
        await _worker.ConfigureAwait(false);
    }
}
