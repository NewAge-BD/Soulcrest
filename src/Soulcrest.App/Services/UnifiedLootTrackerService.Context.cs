using System.Collections.Immutable;
using Soulcrest.App.Network;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Services;

public sealed partial class UnifiedLootTrackerService
{
    private string? _dailyProfile;
    private OwnCharacterContext? _rankOwn;
    private long? _rankPoints;
    private DateTimeOffset? _rankAt;
    private long _rankSequence;

    private static bool IsAbyss(WorldContextSnapshot world) => world.Kind == WorldContextKind.Instance && world.Category == "Abyss";
    private static bool QualifiedWorld(WorldContextSnapshot world, OwnCharacterContext? own, DateTimeOffset at) => own is not null
        && world.Status == WorldContextStatus.Confirmed && world.BindingGeneration == own.Generation
        && world.ObservedAt is { } observed && observed != default && observed <= at
        && (world.Kind == WorldContextKind.OpenWorld && world.InstanceLayer != true || IsAbyss(world));

    private static bool QualifiedFact(long? value, DateTimeOffset? observedAt, DateTimeOffset at) =>
        value is null || value >= 0 && (observedAt ?? at) != default && (observedAt ?? at) <= at;
    private static bool QualifiedAbsolute(NetworkAbyssSnapshot snapshot, OwnCharacterContext? own, DateTimeOffset at) =>
        own is not null && ReferenceEquals(snapshot.Character, own) && !snapshot.FormatRejected && snapshot.Sequence > 0
        && snapshot.At is { } observed && observed != default && observed <= at
        && QualifiedFact(snapshot.AbyssPointBalance, snapshot.AbyssPointBalanceObservedAt, observed)
        && QualifiedFact(snapshot.FarmedAbyssPoints, snapshot.FarmedAbyssPointsObservedAt, observed)
        && QualifiedFact(snapshot.RankPoints, snapshot.RankPointsObservedAt, observed)
        && (snapshot.KinaGainsObservedAt is not { } kinaAt || kinaAt != default && kinaAt <= observed);
    private bool QualifiedGain(NetworkAbyssSnapshot snapshot, OwnCharacterContext own, DateTimeOffset at, DateTimeOffset? factAt) =>
        QualifiedAbsolute(snapshot, own, _time.GetUtcNow()) && (factAt ?? snapshot.At) is { } observed && observed != default && observed <= at;
    private static bool QualifiedDaily(NetworkDailyMonsterKinaSnapshot snapshot, OwnCharacterContext own, DateTimeOffset at) =>
        ReferenceEquals(snapshot.Character, own) && snapshot.Total is >= 0 && snapshot.Sequence > 0
        && snapshot.At is { } observed && observed != default && observed <= at && LootFarmDay.Get(observed) == LootFarmDay.Get(at);

    private void UpdateDaily(Observation observation, LootTrackerCharacterSnapshot? character)
    {
        if (_dailyProfile != character?.ProfileId)
        {
            _dailyProfile = character?.ProfileId; _cachedDaily = null;
            if (_dailyProfile is not null)
                try { _cachedDaily = _dailyStore.Read(_dailyProfile); }
                catch (Exception error) when (StorageFailure(error)) { _storageIssue = true; }
        }
        var daily = observation.Daily;
        _daily = new(null, null, null, 0);
        if (!_settings.Current.LootTrackingEnabled || !_farm.Running || observation.Own is null || character is null
            || !ReferenceEquals(daily.Character, observation.Own) || daily.Sequence <= 0 || daily.Total is not (>= 0)
            || daily.At is not { } at || at == default || at > observation.At) return;
        if (_dailyGeneration == observation.Own.Generation && daily.Sequence <= _dailySequence)
        {
            if (daily.Sequence == _dailySequence && _cachedDaily is { } previous && previous.Total == daily.Total && previous.ObservedAt == at)
                _daily = daily;
            return;
        }
        // A later server total can be lower. Neither a local reset nor income arithmetic invents this absolute fact.
        var record = new SavedDailyMonsterKina(character.ProfileId!, daily.Total.Value, at);
        _cachedDaily = record; _daily = daily;
        // The cache is auxiliary: a failed write keeps the confirmed fact, retries with the next
        // observation and must never stop the session writer.
        try
        {
            _dailyStore.Save(record);
            _dailyGeneration = observation.Own.Generation; _dailySequence = daily.Sequence;
        }
        catch (Exception error) when (StorageFailure(error)) { _storageIssue = true; }
    }

    private void UpdateDailyMode(Observation observation)
    {
        if (_active is not { Kind: LootTrackerSessionKind.Mixed, Mode: LootTrackerSessionMode.Normal } active
            || _durable is not { } durable || durable.Id != active.Id || observation.Own is null
            || !QualifiedDaily(_daily, observation.Own, observation.At)
            || _daily.Total is not { } total || total < Overlay.LootOverlaySnapshot.DailyLimit) return;
        // A pure cap fact must not commit provisional walking or advance the activity clock.
        var marked = durable with { Mode = LootTrackerSessionMode.LimitedKina };
        _store.Append(marked); _durable = marked;
        _active = active with { Mode = LootTrackerSessionMode.LimitedKina };
    }

    private LootTrackerSession Availability(LootTrackerSession session, DateTimeOffset at, TimeSpan elapsed, GainChannel? activityChannel = null)
    {
        var abyss = session.Kind == LootTrackerSessionKind.Abyss;
        var own = _own;
        var current = own is not null && QualifiedWorld(_world(), own, _time.GetUtcNow());
        var absolute = _absolute;
        var kina = current && (abyss
            ? absolute.KinaGainsQualified && QualifiedGain(absolute, own!, at, absolute.KinaGainsObservedAt)
            : _farm.Running && ReferenceEquals(_farm.OwnCharacter, own) && QualifiedDaily(_farm.DailyMonsterKinaSnapshot, own!, at));
        Set(LootTrackerTimelineMetric.KinaUnbound, kina);
        Set(LootTrackerTimelineMetric.KinaBound, false);
        if (kina) session = session with { KinaUnbound = session.KinaUnbound ?? 0 };
        // Outside the Abyss this method runs only during an accepted activity booking.
        // Absolute observations alone cannot start/extend a session or append idle facts after LastLootAt.
        var pointFacts = abyss ? absolute : _abyss.AbyssSnapshot;
        var pointsCurrent = current && (abyss || _settings.Current.MonsterLootTrackingEnabled)
            && _abyss.Running && ReferenceEquals(_abyss.OwnCharacter, own);
        var ap = pointsCurrent && pointFacts.AbyssPointGainsQualified
            && QualifiedGain(pointFacts, own!, at, pointFacts.AbyssPointBalanceObservedAt);
        var rank = pointsCurrent && pointFacts.RankPointChangesQualified
            && QualifiedGain(pointFacts, own!, at, pointFacts.RankPointsObservedAt);
        // A globally qualified balance is no evidence that an outside session earned points.
        // Activate each channel independently from its first accepted delta, never a cache/baseline.
        if (abyss || session.AbyssPoints is not null || activityChannel == GainChannel.AbyssPoints)
        {
            Set(LootTrackerTimelineMetric.AbyssPoints, ap);
            if (ap) session = session with { AbyssPoints = session.AbyssPoints ?? 0 };
        }
        if (abyss || session.RankPointsGained is not null || session.RankPointsLost is not null || activityChannel == GainChannel.RankPoints)
        {
            Set(LootTrackerTimelineMetric.RankPoints, rank);
            if (rank) session = session with { RankPointsGained = session.RankPointsGained ?? 0, RankPointsLost = session.RankPointsLost ?? 0 };
        }
        var harvesting = current && _gathering.Running && _gathering.GatheringVerified && ReferenceEquals(_gathering.OwnCharacter, own)
            && (abyss ? _settings.Current.AbyssTrackingEnabled : _settings.Current.LifeskillTrackingEnabled);
        if (harvesting) session = session with { HarvestCount = session.HarvestCount ?? 0, FailedHarvestCount = session.FailedHarvestCount ?? 0 };
        return session;

        void Set(LootTrackerTimelineMetric metric, bool available)
        {
            var previous = session.TimelineEvents.LastOrDefault(row => row.Kind == LootTrackerTimelineEventKind.Availability && row.Metric == metric);
            if (previous?.Available == available) return;
            session = session with { TimelineEvents = session.TimelineEvents.Add(LootTrackerTimelineFacts.Create(
                LootTrackerTimelineEventKind.Availability, at, elapsed) with
                { Origin = abyss ? LootTrackerActivityOrigin.Abyss : LootTrackerActivityOrigin.Pve, Metric = metric, Available = available }) };
        }
    }

    private void UpdateAbyssObservation(DateTimeOffset at, long stamp)
    {
        if (_active is not { Kind: LootTrackerSessionKind.Abyss } active) { ResetRankObservation(); return; }
        var next = Availability(active, at, active.Elapsed);
        var rankAt = _absolute.RankPointsObservedAt ?? _absolute.At;
        if (_own is not null && QualifiedAbsolute(_absolute, _own, at) && _absolute.RankPoints is { } points && rankAt is { } factAt)
        {
            if (ReferenceEquals(_rankOwn, _own) && _rankPoints is { } previous && _rankAt <= factAt
                && _rankSequence < _absolute.Sequence && _absolute.RankPointChangesQualified && factAt >= active.StartedAt)
            {
                var from = AbyssRankCatalog.Default.FindStage(previous)!;
                var to = AbyssRankCatalog.Default.FindStage(points)!;
                if (to.Threshold > from.Threshold)
                    next = next with { TimelineEvents = next.TimelineEvents.Add(LootTrackerTimelineFacts.Create(
                        LootTrackerTimelineEventKind.PointsStageReached, factAt, Elapsed(stamp)) with
                        { Origin = LootTrackerActivityOrigin.Abyss, FromStageId = from.Id, ToStageId = to.Id }) };
            }
            _rankOwn = _own; _rankPoints = points; _rankAt = factAt; _rankSequence = _absolute.Sequence;
        }
        else ResetRankObservation();
        if (next != active) Save(next);
    }

    private void ResetRankObservation() { _rankOwn = null; _rankPoints = null; _rankAt = null; _rankSequence = 0; }
}
