using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Soulcrest.App.Components;
using Soulcrest.App.Network;
using Soulcrest.Core.Combat;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Services;

/// <summary>Ordered own facts and character history. Never starts a capture or sends a game request.</summary>
public sealed class AchievementStateService : IDisposable, IAchievementTrackerSource
{
    private const int QueueCapacity = 512;
    private readonly INetworkAchievementSource _source;
    private readonly ExplorationService? _profiles;
    private readonly Func<OwnCharacterContext, AchievementProfileBinding?> _resolve;
    private readonly Func<AchievementProfileBinding?> _selected;
    private readonly AchievementStateStore _store;
    private readonly TimeProvider _time;
    private AchievementOwnSnapshot _currentOwn = AchievementOwnSnapshot.Empty;
    private Guid _trackerSourceInstance;
    private long _trackerSequence;
    private readonly AchievementRuleEvaluator _rules;
    private readonly CombatAchievementDisplayCatalog _catalog;
    private readonly object _gate = new();
    private readonly Queue<Work> _queue = new();
    private readonly ConcurrentDictionary<AchievementProfileBinding, State> _states = new();
    private readonly HashSet<string> _deleted = new(StringComparer.Ordinal);
    private Task _worker = Task.CompletedTask;
    private bool _running, _disposed, _selectionRefresh;
    private Guid _sourceInstance;
    private long _sequence;
    private AchievementProfileBinding? _liveBinding, _selectedBinding;
    private AchievementViewState _view = AchievementViewState.Empty, _live = AchievementViewState.Empty, _saved = AchievementViewState.Empty;

    public event Action? Changed;
    public event Action<AchievementChangeBatch>? AchievementObserved;
    public AchievementOwnSnapshot CurrentOwnSnapshot { get { lock (_gate) return _currentOwn; } }
    public AchievementViewState View { get { lock (_gate) return _view; } }
    public AchievementViewState Snapshot { get { lock (_gate) return _live; } }
    public AchievementViewState SavedSnapshot { get { lock (_gate) return _saved; } }

    public AchievementStateService(INetworkAchievementSource source, ExplorationService profiles, AchievementStateStore store)
        : this(source, store, profiles.GetAchievementBinding, () => profiles.TryGetAchievementBinding(), profiles) { }

    internal AchievementStateService(INetworkAchievementSource source, AchievementStateStore store,
        Func<OwnCharacterContext, AchievementProfileBinding?> resolve, Func<AchievementProfileBinding?> selected,
        ExplorationService? profiles = null, AchievementRuleEvaluator? rules = null, TimeProvider? time = null)
    {
        _source = source; _time = time ?? TimeProvider.System; _store = store; _resolve = resolve; _selected = selected; _profiles = profiles;
        _rules = rules ?? new(); _catalog = CombatAchievementDisplayCatalog.Default;
        source.AchievementObserved += Observe;
        if (profiles is not null) { profiles.Changed += ProfileChanged; profiles.CharacterRemoved += ProfileRemoved; }
        ProfileChanged();
        Observe(source.AchievementSnapshot);
    }

    private void Observe(NetworkAchievementObservation observation)
    {
        // Freeze assignment while the accepted observation still has its native owner. No delayed active-profile lookup.
        var binding = observation.Character is { } own ? _resolve(own) : null;
        lock (_gate)
        {
            if (_disposed || observation.SourceInstanceId == _sourceInstance && observation.Sequence <= _sequence) return;
            _sourceInstance = observation.SourceInstanceId; _sequence = observation.Sequence;
            if (binding is not null && _deleted.Contains(binding.ProfileId)) return;
            if (_queue.Count >= QueueCapacity)
            {
                // The producer's immutable RAM snapshot is authoritative. Lost transitions are not reconstructed as live events.
                var deletions = _queue.Where(work => work.DeletedProfile is not null).ToArray();
                var affected = _queue.Where(work => work.Binding is not null).Select(work => work.Binding!).Distinct().ToArray();
                _queue.Clear();
                foreach (var deletion in deletions) _queue.Enqueue(deletion);
                foreach (var owner in affected) _queue.Enqueue(new(owner, null, GapOnly: true));
                observation = observation with { Kind = NetworkAchievementObservationKind.RecoveryBaseline, HistoryGap = true };
            }
            _queue.Enqueue(new(binding, observation, ReceivedTimestamp: _time.GetTimestamp()));
            StartWorker();
        }
    }

    private void ProfileChanged()
    {
        var binding = _selected();
        lock (_gate)
        {
            if (_disposed) return;
            _selectedBinding = binding;
            _selectionRefresh = true;
            StartWorker();
        }
        // A full Own may precede creation of its local profile. Revisit only the current immutable source.
        var latest = _source.AchievementSnapshot;
        if (latest.Character is not null)
        {
            var owner = _resolve(latest.Character);
            lock (_gate)
            {
                if (!_disposed && owner is not null && !_deleted.Contains(owner.ProfileId)
                    && (!_states.TryGetValue(owner, out var state) || state.LastObservation is not { } last
                        || last.SourceInstanceId != latest.SourceInstanceId || last.Sequence < latest.Sequence) && !_queue.Any(work => work.Binding == owner && work.Observation is not null))
                {
                    _queue.Enqueue(new(owner, latest with { Kind = NetworkAchievementObservationKind.Baseline }, ReceivedTimestamp: _time.GetTimestamp()));
                    StartWorker();
                }
            }
        }
    }

    private void ProfileRemoved(string profileId)
    {
        lock (_gate)
        {
            _deleted.Add(profileId);
            _queue.Enqueue(new(null, null, profileId));
            StartWorker();
        }
    }

    private void StartWorker()
    {
        if (_running) return;
        _running = true;
        _worker = Task.Run(Run);
    }

    private void Run()
    {
        while (true)
        {
            Work[] batch;
            lock (_gate)
            {
                if (_queue.Count == 0 && !_selectionRefresh) { _running = false; return; }
                batch = _selectionRefresh ? [.. _queue, new(_selectedBinding, null)] : _queue.ToArray();
                _queue.Clear(); _selectionRefresh = false;
            }
            var pending = new Dictionary<AchievementProfileBinding, AchievementSavedSnapshot>();
            foreach (var work in batch)
            {
                if (work.DeletedProfile is { } deleted)
                {
                    InvalidateTracker(deleted, historyGap: false);
                    foreach (var key in _states.Keys.Where(key => key.ProfileId == deleted).ToArray()) _states.TryRemove(key, out _);
                    try { _store.DeleteProfile(deleted); }
                    catch (Exception error) when (StorageError(error)) { /* No delayed job can pass the in-memory tombstone. */ }
                    continue;
                }
                if (work.Binding is not { } binding) continue;
                lock (_gate) if (_deleted.Contains(binding.ProfileId)) continue;
                if (!_states.TryGetValue(binding, out var state))
                {
                    AchievementStoreRead read;
                    try { read = _store.Load(binding); }
                    catch (Exception error) when (StorageError(error)) { read = new(null, [], ["history-read-failed"]); }
                    state = new() { Saved = read.Snapshot, Milestones = read.Milestones.Select(item => item with { NotificationEligible = false }).ToImmutableArray(), Issues = read.Issues, HistoryGap = read.Snapshot?.HistoryGap == true || !read.Issues.IsEmpty };
                    _states.TryAdd(binding, state);
                }
                if (work.GapOnly)
                {
                    InvalidateTracker(binding.ProfileId, historyGap: true);
                    state.HistoryGap = true; state.Live = null; state.Previous = null;
                    state.Activities = ImmutableDictionary<int, AchievementActivity>.Empty;
                    if (_liveBinding == binding) _liveBinding = null;
                    if (state.Saved is { } saved) pending[binding] = saved with { HistoryGap = true };
                    continue;
                }
                if (work.Observation is not { } observation) continue;
                if (state.LastObservation is { } lastObservation && lastObservation.SourceInstanceId == observation.SourceInstanceId
                    && lastObservation.Sequence >= observation.Sequence) continue;
                state.LastObservation = observation;
                if (observation.Kind == NetworkAchievementObservationKind.Invalidated || !Usable(observation))
                {
                    state.Live = null; state.Previous = null;
                    state.Activities = ImmutableDictionary<int, AchievementActivity>.Empty;
                    state.HistoryGap |= observation.HistoryGap;
                    if (observation.HistoryGap && (pending.GetValueOrDefault(binding) ?? state.Saved) is { } checkpoint)
                        pending[binding] = checkpoint with { HistoryGap = true };
                    if (_liveBinding == binding) _liveBinding = null;
                    var unready = MakeUnreadyOwn(binding, state, pending.GetValueOrDefault(binding) ?? state.Saved,
                        observation, work.ReceivedTimestamp);
                    PublishTracker(unready, observation, [], [], work.ReceivedTimestamp);
                    continue;
                }
                var raw = observation.Domain.Value!;
                var progress = _rules.Evaluate(raw, _catalog.MetadataBuild, observation.Generation, observation.Sequence,
                    KnownEarned(state, _catalog.MetadataBuild), KnownClaimed(state, _catalog.MetadataBuild));
                var continuous = observation.Kind is NetworkAchievementObservationKind.Update or NetworkAchievementObservationKind.Reward
                    && state.Previous is { } previous && previous.Generation == observation.Generation
                    && previous.SourceInstanceId == observation.SourceInstanceId && !observation.HistoryGap;
                var activities = ObserveActivity(state, observation, continuous);
                var discovered = Discover(binding, state, progress, raw, observation, continuous);
                state.HistoryGap |= observation.HistoryGap;
                state.Milestones = state.Milestones.AddRange(discovered);
                state.Previous = observation;
                state.Live = new()
                {
                    Binding = binding, Raw = raw, ObservedAt = observation.ObservedAt,
                    MetadataBuild = _catalog.MetadataBuild, RuleVersion = AchievementRuleEvaluator.Version,
                    Generation = observation.Generation, SourceSequence = observation.Sequence,
                    HistoryGap = state.HistoryGap, Coverage = observation.Domain.Status == NetworkCombatSourceStatus.Complete ? "received-list" : "received-partial",
                };
                _liveBinding = binding;
                var currentCheckpoint = AccumulateCheckpoint(state.Live, pending.GetValueOrDefault(binding) ?? state.Saved);
                var ownSnapshot = new AchievementOwnSnapshot(binding, MakeView(state, state.Live, AchievementViewSource.Live), raw, work.ReceivedTimestamp)
                {
                    SourceInstanceId = observation.SourceInstanceId,
                    History = observation.Domain.Status == NetworkCombatSourceStatus.Partial
                        ? MakeHistoricalOwn(binding, state, currentCheckpoint, observation, work.ReceivedTimestamp) : null,
                };
                PublishTracker(ownSnapshot, observation, activities, discovered, work.ReceivedTimestamp);
                pending[binding] = currentCheckpoint;
            }
            // Every transition above was evaluated. Counter-only checkpoints may now be coalesced safely.
            foreach (var (binding, checkpoint) in pending)
            {
                lock (_gate) if (_deleted.Contains(binding.ProfileId)) continue;
                var state = _states[binding];
                try
                {
                    state.Saved = _store.Save(checkpoint, state.Milestones);
                    state.Issues = state.Issues.Remove("history-write-failed");
                }
                catch (Exception error) when (StorageError(error))
                {
                    state.Issues = state.Issues.Add("history-write-failed").Distinct().ToImmutableArray();
                }
            }
            Publish();
        }
    }

    private static ImmutableArray<AchievementActivity> ObserveActivity(State state, NetworkAchievementObservation observation, bool continuous)
    {
        // User request 2026-11-07: identifying the updated achievement does not require
        // a qualified explanation, unit or target. Login/recovery imports are not gameplay updates.
        if (!continuous)
        {
            state.Activities = ImmutableDictionary<int, AchievementActivity>.Empty;
            return [];
        }
        if (observation.Kind != NetworkAchievementObservationKind.Update || state.Previous?.Domain.Value is not { } prior) return [];
        var activities = state.Activities.ToBuilder();
        var changed = ImmutableArray.CreateBuilder<AchievementActivity>();
        foreach (var (id, value) in observation.Domain.Value!.Achievements)
        {
            var hadPrevious = prior.Achievements.TryGetValue(id, out var old);
            if (hadPrevious && old!.Count == value.Count) continue;
            // A first update has no implied starting zero. Signed values remain exactly as received.
            var activity = new AchievementActivity(id, hadPrevious ? old!.Count : null, value.Count, observation.ObservedAt);
            activities[id] = activity; changed.Add(activity);
        }
        state.Activities = activities.ToImmutable();
        return changed.ToImmutable();
    }

    private static AchievementSavedSnapshot AccumulateCheckpoint(AchievementSavedSnapshot current,
        AchievementSavedSnapshot? previous)
    {
        // A post-entry absolute update can establish only the rows it actually carries. Keep
        // other last-known rows for offline history, never in the fresh RAM/overlay source.
        if (current.Coverage != "received-partial" || previous is null
            || previous.Binding != current.Binding || previous.MetadataBuild != current.MetadataBuild) return current;
        if (previous.Raw.Achievements.Keys.All(current.Raw.Achievements.ContainsKey)
            && previous.Raw.RewardGroups.Keys.All(current.Raw.RewardGroups.ContainsKey)) return current;
        return current with
        {
            Raw = new(previous.Raw.Achievements.SetItems(current.Raw.Achievements),
                previous.Raw.RewardGroups.SetItems(current.Raw.RewardGroups)),
            Coverage = "accumulated-observations",
        };
    }

    private ImmutableArray<AchievementMilestone> Discover(AchievementProfileBinding binding, State state,
        CombatAchievementProgressSnapshot progress, CharacterAchievementState raw,
        NetworkAchievementObservation observation, bool continuous)
    {
        var known = state.Milestones.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var result = ImmutableArray.CreateBuilder<AchievementMilestone>();
        var priorRaw = continuous ? state.Previous?.Domain.Value : null;
        var priorProgress = priorRaw is null ? null : _rules.Evaluate(priorRaw, _catalog.MetadataBuild, observation.Generation, observation.Sequence,
            KnownEarned(state, _catalog.MetadataBuild), KnownClaimed(state, _catalog.MetadataBuild));
        foreach (var family in progress.Families.Values)
        {
            if (_rules.QualifiedPeriodId(family.Id) is not { } periodId) continue;
            var definition = _catalog.Find(family.Id);
            foreach (var stage in family.Stages.Values)
            foreach (var kind in new[] { AchievementMilestoneKind.Completed, AchievementMilestoneKind.RewardClaimed })
            {
                if (!(kind == AchievementMilestoneKind.Completed ? stage.Completed == true || _rules.ClaimFact(raw, family.Id, stage.StageId) == true : stage.Claimed == true)) continue;
                // The default qualified contracts are explicitly permanent. No calendar- or counter-based period inference.
                var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                    $"{binding.Revision}:{periodId}:{family.Id}:{stage.StageId}:{kind}")));
                if (!known.Add(id)) continue;
                var prior = priorProgress?.Find(family.Id)?.Find(stage.StageId);
                // A reported current state below its goal cannot prove "never completed".
                // Its continuous first false-to-true requirements transition can still confirm
                // a live completion; the durable milestone identity prevents repeat celebrations.
                var liveTransition = continuous && (kind == AchievementMilestoneKind.Completed ? family.Status != CombatKnowledgeStatus.Conflict
                    && (prior?.Completed == false || prior?.RequirementsMetNow == false && stage.RequirementsMetNow == true)
                    : prior?.Claimed == false || priorRaw is not null && _rules.ClaimFact(priorRaw, family.Id, stage.StageId) == false);
                var label = definition?.Stages.FirstOrDefault(item => item.Id == stage.StageId);
                result.Add(new(id, kind, family.Id, stage.StageId, observation.ObservedAt, null,
                    liveTransition ? "Live" : observation.Kind == NetworkAchievementObservationKind.RecoveryBaseline ? "Recovery" : "Baseline", _catalog.MetadataBuild, AchievementRuleEvaluator.Version,
                    periodId, new(raw.Achievements.Where(pair => pair.Key == family.Id).ToImmutableDictionary(),
                        ImmutableDictionary<int, ImmutableArray<bool>>.Empty), label?.NameDe ?? definition?.NameDe, label?.NameEn ?? definition?.NameEn)
                    { NotificationEligible = liveTransition });
            }
        }
        return result.ToImmutable();
    }

    private void Publish()
    {
        AchievementViewState saved = AchievementViewState.Empty, live = AchievementViewState.Empty;
        AchievementProfileBinding? selected;
        lock (_gate) selected = _selectedBinding;
        if (selected is not null && _states.TryGetValue(selected, out var state))
        {
            saved = MakeView(state, state.Saved, AchievementViewSource.Saved);
            if (_liveBinding == selected) live = MakeView(state, state.Live, AchievementViewSource.Live);
        }
        lock (_gate)
        {
            if (selected is not null && _deleted.Contains(selected.ProfileId)) saved = live = AchievementViewState.Empty;
            _saved = saved; _live = live; _view = live.IsLive ? live : saved;
        }
        SafeEvent.Raise(Changed, "Achievement-Stand aktualisiert");
    }

    private AchievementViewState MakeView(State state, AchievementSavedSnapshot? value, AchievementViewSource source) => value is null
        ? AchievementViewState.Empty with { Issues = state.Issues, HistoryGap = state.HistoryGap }
        : new()
        {
            ProfileId = value.Binding.ProfileId, Binding = value.Binding, Source = source, ObservedAt = value.ObservedAt,
            Generation = value.Generation, Sequence = value.SourceSequence, MetadataBuild = value.MetadataBuild,
            RuleVersion = value.RuleVersion, Progress = _rules.Evaluate(value.Raw, value.MetadataBuild, value.Generation, value.SourceSequence,
                KnownEarned(state, value.MetadataBuild), KnownClaimed(state, value.MetadataBuild)),
            ObservedFamilyIds = value.Raw.Achievements.Keys.ToImmutableHashSet(), Milestones = state.Milestones,
            ObservedValues = value.Raw.Achievements.ToImmutableDictionary(pair => pair.Key, pair => pair.Value.Count),
            Activities = source == AchievementViewSource.Live ? state.Activities : ImmutableDictionary<int, AchievementActivity>.Empty,
            Issues = state.Issues, HistoryGap = state.HistoryGap || value.HistoryGap,
        };

    private AchievementOwnSnapshot MakeUnreadyOwn(AchievementProfileBinding binding, State state,
        AchievementSavedSnapshot? saved, NetworkAchievementObservation observation, long timestamp)
    {
        // User request 2026-11-09: a newly confirmed own character may show its saved
        // achievement progress after world entry. A gap alone never confirms that owner.
        if (observation.Kind != NetworkAchievementObservationKind.Baseline || observation.Generation <= 0
            || observation.Character?.Generation != observation.Generation) return AchievementOwnSnapshot.Empty;
        if (MakeHistoricalOwn(binding, state, saved, observation, timestamp) is { } history) return history;
        return new(binding, new() { Binding = binding, ProfileId = binding.ProfileId, MetadataBuild = _catalog.MetadataBuild,
            Generation = observation.Generation, Sequence = observation.Sequence }, null, timestamp)
            { SourceInstanceId = observation.SourceInstanceId };
    }

    private AchievementOwnSnapshot? MakeHistoricalOwn(AchievementProfileBinding binding, State state,
        AchievementSavedSnapshot? saved, NetworkAchievementObservation observation, long timestamp)
    {
        if (saved is null || saved.Binding != binding || saved.MetadataBuild != _catalog.MetadataBuild) return null;
        return new(binding, MakeView(state, saved, AchievementViewSource.Saved) with
            {
                Generation = observation.Generation, Sequence = observation.Sequence,
                Milestones = state.Milestones.Select(item => item with { NotificationEligible = false }).ToImmutableArray(),
            }, saved.Raw, timestamp)
            { SourceInstanceId = observation.SourceInstanceId };
    }

    private void PublishTracker(AchievementOwnSnapshot snapshot, NetworkAchievementObservation observation,
        ImmutableArray<AchievementActivity> activities, ImmutableArray<AchievementMilestone> milestones, long timestamp)
    {
        lock (_gate)
        {
            // A profile-binding catch-up can precede older queued callbacks belonging to another
            // character. Such callbacks may invalidate their own history, never the newer own HUD.
            if (_trackerSourceInstance == observation.SourceInstanceId && observation.Sequence <= _trackerSequence) return;
            _trackerSourceInstance = observation.SourceInstanceId; _trackerSequence = observation.Sequence;
            _currentOwn = snapshot;
        }
        SafeEvent.Raise(AchievementObserved, new(snapshot, observation.Kind, activities, milestones,
            observation.HistoryGap, observation.SourceInstanceId, observation.Sequence, observation.Generation, timestamp), "Achievement-Overlayquelle");
    }

    private void InvalidateTracker(string profileId, bool historyGap)
    {
        AchievementOwnSnapshot previous;
        lock (_gate)
        {
            previous = _currentOwn;
            if (previous.Binding?.ProfileId != profileId) return;
            _currentOwn = AchievementOwnSnapshot.Empty;
        }
        SafeEvent.Raise(AchievementObserved, new(AchievementOwnSnapshot.Empty, NetworkAchievementObservationKind.Invalidated,
            [], [], historyGap, previous.SourceInstanceId, previous.State.Sequence, previous.State.Generation, _time.GetTimestamp()), "Achievement-Overlayquelle entwertet");
    }

    private HashSet<int> KnownEarned(State state, int metadataBuild) => state.Milestones
        .Where(item => item.Kind == AchievementMilestoneKind.Completed && item.MetadataBuild == metadataBuild
            && item.StageId is not null && item.FamilyId is { } family && _rules.QualifiedPeriodId(family) is { } period && period == item.PeriodId)
        .Select(item => item.StageId!.Value).ToHashSet();

    private HashSet<int> KnownClaimed(State state, int metadataBuild)
    {
        var definitions = AchievementDefinitionCatalog.Default;
        if (definitions.MetadataBuild != metadataBuild) return [];
        return state.Milestones.Where(item => item.Kind == AchievementMilestoneKind.RewardClaimed
            && item.MetadataBuild == metadataBuild && item.PeriodId == "permanent"
            && item.StageId is { } stage && item.FamilyId is { } family
            && definitions.Find(family) is { Area: AchievementArea.Normal, Role: AchievementRole.PlayerFacing } definition
            && definition.StageIds.Contains(stage) && _rules.QualifiedPeriodId(family) == "permanent")
            .Select(item => item.StageId!.Value).ToHashSet();
    }

    private static bool Usable(NetworkAchievementObservation item) => item.Character is not null && item.Generation > 0
        && item.Character.Generation == item.Generation && item.Domain is { IsFresh: true, Value: not null, ObservedAt: not null, Sequence: > 0 }
        && item.Domain.Status is NetworkCombatSourceStatus.Complete or NetworkCombatSourceStatus.Partial
        && item.Domain.Source is "own-achievement-list" or "own-achievement-update" or "own-achievement-reward";

    private static bool StorageError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException
        or System.Text.Json.JsonException or ArgumentException or InvalidOperationException or NotSupportedException;

    public async Task FlushAsync()
    {
        while (true)
        {
            Task worker;
            lock (_gate) worker = _worker;
            await worker.ConfigureAwait(false);
            lock (_gate) if (!_running && _queue.Count == 0 && !_selectionRefresh) return;
        }
    }

    public void Dispose()
    {
        _source.AchievementObserved -= Observe;
        if (_profiles is not null) { _profiles.Changed -= ProfileChanged; _profiles.CharacterRemoved -= ProfileRemoved; }
        lock (_gate) _disposed = true;
        FlushAsync().GetAwaiter().GetResult();
    }

    private sealed record Work(AchievementProfileBinding? Binding, NetworkAchievementObservation? Observation, string? DeletedProfile = null, bool GapOnly = false, long ReceivedTimestamp = 0);
    private sealed class State
    {
        public AchievementSavedSnapshot? Saved, Live;
        public NetworkAchievementObservation? Previous, LastObservation;
        public ImmutableArray<AchievementMilestone> Milestones = [];
        public ImmutableArray<string> Issues = [];
        public ImmutableDictionary<int, AchievementActivity> Activities = ImmutableDictionary<int, AchievementActivity>.Empty;
        public bool HistoryGap;
    }
}
