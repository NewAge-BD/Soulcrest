using System.Collections.Immutable;
using Soulcrest.App.Network;

namespace Soulcrest.App.Services;

/// <summary>
/// Transient current-own tracker presentation. Ingress only enqueues immutable observations;
/// a native presentation timer calls Tick. No timer, activity or animation is written to disk.
/// </summary>
public sealed class AchievementTrackerService : IDisposable
{
    public static readonly TimeSpan ProgressDuration = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan CompletionDuration = TimeSpan.FromSeconds(60);
    private const int QueueCapacity = 256;
    private readonly IAchievementTrackerSource _source;
    private readonly AchievementFavoriteService _favorites;
    private readonly TimeProvider _time;
    private readonly AchievementTrackerStageResolver _stages;
    private readonly AchievementTitleTrackingResolver _titles;
    private readonly Func<CharacterCombatSnapshot>? _character;
    private readonly Func<int> _readDisplaySeconds;
    private readonly object _ingressGate = new(), _stateGate = new();
    private readonly Queue<AchievementChangeBatch> _queue = new();
    private readonly Dictionary<int, Recent> _recent = new();
    private readonly Dictionary<int, Celebration> _celebrations = new();
    private readonly Dictionary<int, Pending> _pending = new();
    private readonly HashSet<int> _claimSuppressed = new();
    private readonly HashSet<int> _expiredNonFavorites = new();
    private readonly HashSet<string> _seenCompletions = new(StringComparer.Ordinal), _seenClaims = new(StringComparer.Ordinal);
    private AchievementOwnSnapshot _own = AchievementOwnSnapshot.Empty;
    private AchievementFavoriteSnapshot _favoriteSnapshot = new(null, []);
    private AchievementTrackerSnapshot _snapshot = AchievementTrackerSnapshot.Empty;
    private Guid _sourceInstance;
    private long _sequence, _generation, _activityOrder;
    private int _favoritesDirty = 1;
    private bool _initialized, _disposed;

    public AchievementTrackerService(IAchievementTrackerSource source, AchievementFavoriteService favorites,
        TimeProvider? time = null, CharacterCombatStateService? character = null, SettingsService? settings = null)
        : this(source, favorites, new(), time, character: character is null ? null : () => character.Snapshot,
            readDisplaySeconds: settings is null ? null : () => settings.Current.AchievementOverlayDisplaySeconds) { }

    internal AchievementTrackerService(IAchievementTrackerSource source, AchievementFavoriteService favorites,
        AchievementTrackerStageResolver stages, TimeProvider? time = null, AchievementTitleTrackingResolver? titles = null,
        Func<CharacterCombatSnapshot>? character = null, Func<int>? readDisplaySeconds = null)
    {
        _source = source; _favorites = favorites; _stages = stages; _time = time ?? TimeProvider.System;
        _titles = titles ?? new(); _character = character;
        _readDisplaySeconds = readDisplaySeconds ?? (() => (int)ProgressDuration.TotalSeconds);
        source.AchievementObserved += Observe;
        favorites.Changed += FavoritesChanged;
        var current = source.CurrentOwnSnapshot;
        _own = current;
        _sourceInstance = current.SourceInstanceId;
        _sequence = current.State.Sequence;
        _generation = current.State.Generation;
        _initialized = current.Binding is not null;
        SeedPending(current);
        Tick();
    }

    public event Action? Changed;
    public AchievementTrackerSnapshot Snapshot { get { lock (_stateGate) return _snapshot; } }

    internal bool MatchesSourceInstance(Guid expected)
    {
        lock (_stateGate) return expected != Guid.Empty && _sourceInstance == expected;
    }

    private void Observe(AchievementChangeBatch batch)
    {
        lock (_ingressGate)
        {
            if (_disposed) return;
            if (_queue.Count >= QueueCapacity)
            {
                _queue.Clear();
                batch = batch with { Kind = NetworkAchievementObservationKind.RecoveryBaseline,
                    Activities = [], Milestones = [], HistoryGap = true };
            }
            _queue.Enqueue(batch);
        }
    }

    private void FavoritesChanged() => Interlocked.Exchange(ref _favoritesDirty, 1);

    public void Tick()
    {
        AchievementChangeBatch[] batches;
        lock (_ingressGate)
        {
            if (_disposed) return;
            batches = _queue.ToArray();
            _queue.Clear();
        }
        var changed = false;
        lock (_stateGate)
        {
            if (_disposed) return;
            foreach (var batch in batches) Apply(batch);
            if (Interlocked.Exchange(ref _favoritesDirty, 0) != 0 || _favoriteSnapshot.Binding != _own.Binding)
            {
                _favoriteSnapshot = _favorites.Get(_own.Binding);
                if (_own.Binding is not null && _favoriteSnapshot.Binding is null)
                {
                    // The assignment was deleted or retired while its source notification was queued.
                    _own = AchievementOwnSnapshot.Empty;
                    ClearTransient();
                }
            }
            var now = _time.GetTimestamp();
            var displayDuration = TimeSpan.FromSeconds(SettingsService.NormalizeAchievementOverlayDisplaySeconds(_readDisplaySeconds()));
            // Keep the most recent event anchor, even after hiding. Dropping it would allow an
            // unclaimed baseline row to be reseeded by unrelated updates or a longer setting.
            foreach (var family in _celebrations.Where(pair => Expired(pair.Value.At, now, CompletionDuration)).Select(pair => pair.Key).ToArray())
                _celebrations.Remove(family);
            var next = Build(now, displayDuration);
            if (_snapshot.Binding != next.Binding || _snapshot.IsLive != next.IsLive || !_snapshot.Rows.SequenceEqual(next.Rows))
            {
                _snapshot = next;
                changed = true;
            }
        }
        if (changed) SafeEvent.Raise(Changed, "Achievement-Tracker");
    }

    private void Apply(AchievementChangeBatch batch)
    {
        var previous = _own;
        var changedOwner = previous.Binding != batch.Snapshot.Binding;
        var changedSource = !_initialized || _sourceInstance != batch.SourceInstanceId || _generation != batch.Generation;
        var boundary = batch.Kind is NetworkAchievementObservationKind.Invalidated or NetworkAchievementObservationKind.Baseline
            or NetworkAchievementObservationKind.RecoveryBaseline || batch.HistoryGap || changedOwner || changedSource;

        // Profile removal and gap-only notifications can legitimately reuse the last source sequence.
        // Their invalidation always takes effect before ordinary replay suppression.
        if (batch.Kind == NetworkAchievementObservationKind.Invalidated || batch.HistoryGap)
        {
            ClearTransient();
            _own = batch.Snapshot;
        }
        if (_initialized && _sourceInstance == batch.SourceInstanceId && batch.Sequence <= _sequence) return;
        if (changedOwner || _sourceInstance != batch.SourceInstanceId)
        {
            _seenCompletions.Clear();
            _seenClaims.Clear();
        }
        _initialized = true;
        _sourceInstance = batch.SourceInstanceId; _sequence = batch.Sequence; _generation = batch.Generation;
        _own = batch.Snapshot;
        if (boundary || !_own.IsLive)
        {
            ClearTransient();
            SeedPending(_own);
            RememberHistoricalIds(_own);
            return;
        }
        var favorites = _favorites.Get(_own.Binding);
        var titleFamilies = _titles.Resolve(_own, favorites.TitleIds).Where(target => target.FamilyId is not null)
            .Select(target => target.FamilyId!.Value).ToHashSet();
        foreach (var activity in batch.Activities)
        {
            if (!_stages.IsEligible(activity.FamilyId, _own.State.MetadataBuild, favorites.Contains(activity.FamilyId) || titleFamilies.Contains(activity.FamilyId))
                || activity.PreviousValue == activity.CurrentValue) continue;
            _recent[activity.FamilyId] = new(activity, batch.ReceivedTimestamp, ++_activityOrder);
            _claimSuppressed.Remove(activity.FamilyId);
            _expiredNonFavorites.Remove(activity.FamilyId);
        }
        foreach (var milestone in batch.Milestones)
        {
            if (milestone.Kind != AchievementMilestoneKind.Completed || milestone.FamilyId is not { } family
                || milestone.StageId is not { } stage || !milestone.NotificationEligible || !_seenCompletions.Add(milestone.Id)
                || !_stages.IsEligible(family, _own.State.MetadataBuild, favorites.Contains(family) || titleFamilies.Contains(family))) continue;
            if (!_stages.IsCompletedUnclaimed(_own, family, stage)) continue;
            _expiredNonFavorites.Remove(family);
            if (!_celebrations.TryGetValue(family, out var active) || _stages.IsLaterStage(family, stage, active.StageId))
                _celebrations[family] = new(stage, batch.ReceivedTimestamp);
            _pending[family] = new(batch.ReceivedTimestamp, ++_activityOrder);
        }

        // Claims win over progress and completion in the same batch. A retained history milestone
        // need not be newly journalled, so also compare the qualified raw claim prefix directly.
        var claimed = batch.Milestones.Where(item => item.Kind == AchievementMilestoneKind.RewardClaimed
            && item.FamilyId is not null && _seenClaims.Add(item.Id) && item.NotificationEligible).Select(item => item.FamilyId!.Value).ToHashSet();
        if (_own.Raw is not null)
            foreach (var (family, currentRow) in _own.Raw.Achievements)
            {
                var oldRow = previous.Raw?.Achievements.GetValueOrDefault(family);
                if ((oldRow is null || oldRow.RewardedStep != currentRow.RewardedStep)
                    && _stages.HasNewQualifiedClaim(previous, _own, family)) claimed.Add(family);
            }
        foreach (var family in claimed)
        {
            _celebrations.Remove(family); _recent.Remove(family); _pending.Remove(family);
            _claimSuppressed.Add(family);
        }
        SeedPending(_own);
    }

    private void SeedPending(AchievementOwnSnapshot current)
    {
        var favorites = _favorites.Get(current.Binding);
        var titleFamilies = _titles.Resolve(current, favorites.TitleIds).Where(target => target.FamilyId is not null)
            .Select(target => target.FamilyId!.Value).ToHashSet();
        foreach (var family in _stages.PendingFamilies(current))
            if (!_claimSuppressed.Contains(family) && _stages.IsEligible(family, current.State.MetadataBuild,
                favorites.Contains(family) || titleFamilies.Contains(family)))
                _pending.TryAdd(family, new(current.ReceivedTimestamp, 0));
    }

    private void RememberHistoricalIds(AchievementOwnSnapshot current)
    {
        foreach (var milestone in current.State.Milestones)
        {
            if (milestone.Kind == AchievementMilestoneKind.Completed) _seenCompletions.Add(milestone.Id);
            else if (milestone.Kind == AchievementMilestoneKind.RewardClaimed) _seenClaims.Add(milestone.Id);
        }
    }

    private AchievementTrackerSnapshot Build(long now, TimeSpan displayDuration)
    {
        if (_own.Binding is null) return AchievementTrackerSnapshot.Empty;
        var rows = ImmutableArray.CreateBuilder<AchievementTrackerRow>();
        var included = new HashSet<int>();
        var titleTargets = _titles.Resolve(_own, _favoriteSnapshot.TitleIds, _character?.Invoke());
        var familyTitles = titleTargets.Where(target => target.FamilyId is not null).GroupBy(target => target.FamilyId!.Value)
            .ToDictionary(group => group.Key, group => group.First().Title);
        foreach (var family in _favoriteSnapshot.FamilyIds)
            Add(family, favorite: true);
        foreach (var target in titleTargets)
            if (target.FamilyId is { } family) Add(family, favorite: true);
            else rows.Add(AchievementTitleTrackingResolver.Placeholder(target.Title));
        foreach (var family in _pending.OrderByDescending(pair => pair.Value.At).ThenByDescending(pair => pair.Value.Order)
            .ThenBy(pair => pair.Key).Select(pair => pair.Key))
            if (!_claimSuppressed.Contains(family)) Add(family, favorite: false);
        foreach (var family in _recent.OrderByDescending(pair => pair.Value.At).ThenByDescending(pair => pair.Value.Order).Select(pair => pair.Key))
            if (!_claimSuppressed.Contains(family)) Add(family, favorite: false);
        return new(_own.Binding, rows.ToImmutable()) { IsLive = _own.IsLive };

        void Add(int family, bool favorite)
        {
            if (included.Contains(family) || !_stages.IsEligible(family, _own.State.MetadataBuild, favorite)) return;
            if (!favorite && !VisibleNonFavorite(family, now, displayDuration)) return;
            var selected = _stages.Resolve(_own, family);
            if (selected.AllClaimed) return;
            var row = selected.Row;
            if (familyTitles.TryGetValue(family, out var title)) row = row with
                { TrackedTitleId = title.Id, TitleNameDe = title.NameDe, TitleNameEn = title.NameEn };
            var animationDuration = favorite || CompletionDuration <= displayDuration ? CompletionDuration : displayDuration;
            if (_celebrations.TryGetValue(family, out var oldCelebration) && Expired(oldCelebration.At, now, animationDuration))
                _celebrations.Remove(family);
            if (_celebrations.TryGetValue(family, out var celebration)) row = row with
            {
                State = AchievementTrackerRowState.Celebrating,
                CelebratedStageId = celebration.StageId,
                CompletionProgress = Math.Clamp(_time.GetElapsedTime(celebration.At, now).TotalSeconds / CompletionDuration.TotalSeconds, 0, 1),
            };
            else if (!favorite && !_recent.ContainsKey(family) && !selected.CompletedUnclaimed) return;
            else if (row.RawValue is null && _recent.TryGetValue(family, out var recent)) row = row with { RawValue = recent.Activity.CurrentValue };
            rows.Add(row with { IsFavorite = favorite });
            included.Add(family);
        }
    }

    private bool VisibleNonFavorite(int family, long now, TimeSpan duration)
    {
        if (_expiredNonFavorites.Contains(family)) return false;
        long? latest = null;
        if (_recent.TryGetValue(family, out var recent)) latest = recent.At;
        if (_pending.TryGetValue(family, out var pending) && (latest is null || pending.At > latest.Value)) latest = pending.At;
        if (_celebrations.TryGetValue(family, out var celebration) && (latest is null || celebration.At > latest.Value)) latest = celebration.At;
        if (latest is not null && !Expired(latest.Value, now, duration)) return true;
        _expiredNonFavorites.Add(family);
        _celebrations.Remove(family);
        return false;
    }

    private bool Expired(long started, long now, TimeSpan duration) => _time.GetElapsedTime(started, now) >= duration;
    private void ClearTransient()
    { _recent.Clear(); _celebrations.Clear(); _pending.Clear(); _claimSuppressed.Clear(); _expiredNonFavorites.Clear(); }

    public void Dispose()
    {
        _source.AchievementObserved -= Observe;
        _favorites.Changed -= FavoritesChanged;
        lock (_ingressGate) { _disposed = true; _queue.Clear(); }
        lock (_stateGate)
        {
            ClearTransient();
            _own = AchievementOwnSnapshot.Empty;
            _snapshot = AchievementTrackerSnapshot.Empty;
        }
    }

    private sealed record Recent(AchievementActivity Activity, long At, long Order);
    private sealed record Celebration(int StageId, long At);
    private sealed record Pending(long At, long Order);
}
