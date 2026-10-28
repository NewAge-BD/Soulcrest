using System.Collections.Immutable;
using Soulcrest.App.Network;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Services;

public sealed partial class UnifiedLootTrackerService
{
    // Only the own binding, static resource template and anonymous result metadata enter the queue.
    // Gathering target/object keys and opponent identities are deliberately not copied.
    private sealed record Activity(GainChannel Channel, OwnCharacterContext Own, LootTrackerCharacterSnapshot? Character,
        WorldContextSnapshot World, DateTimeOffset At, long Stamp, long Sequence, bool Enabled,
        RecorderPositionSnapshot? Position, long Amount = 0, NetworkFarmDrop? Drop = null,
        ImmutableArray<NetworkGatheringItem> Items = default, NetworkGatheringOutcome Outcome = NetworkGatheringOutcome.Harvested,
        int? GatherableId = null, bool InInstance = false, LootTrackerCombatKind? CombatKind = null,
        string? OpponentClassId = null, int? OpponentAbyssRankGrade = null) : Command
    { public override string ToString() => nameof(Activity); }

    private bool EventTime(OwnCharacterContext own, DateTimeOffset at, long sequence) => sequence > 0 && at != default
        && at <= _time.GetUtcNow() && own.ObservedAt is { } entered && entered != default && entered <= at;

    private bool ChannelEnabled(GainChannel channel, WorldContextSnapshot world) => _settings.Current.LootTrackingEnabled
        && (IsAbyss(world) ? _settings.Current.AbyssTrackingEnabled : channel == GainChannel.Harvest
            ? _settings.Current.LifeskillTrackingEnabled : _settings.Current.MonsterLootTrackingEnabled);

    private void QueueActivity(GainChannel channel, OwnCharacterContext own, DateTimeOffset at, long sequence,
        long amount = 0, NetworkFarmDrop? drop = null, ImmutableArray<NetworkGatheringItem> items = default,
        NetworkGatheringOutcome outcome = NetworkGatheringOutcome.Harvested, int? gatherableId = null,
        bool inInstance = false, LootTrackerCombatKind? combatKind = null, string? classId = null, int? grade = null)
    {
        if (!EventTime(own, at, sequence) || !ReferenceEquals(CurrentOwn, own)) return;
        var world = _world();
        var stamp = _time.GetTimestamp();
        // Freeze the visual fix at completion, before a delayed profile lookup can observe a later frame.
        // The position getter releases its map lock before the binding is read.
        var position = _position();
        Enqueue(new Activity(channel, own, _bind(own), world, at, stamp, sequence,
            ChannelEnabled(channel, world), position, amount, drop, items, outcome, gatherableId,
            inInstance, combatKind, classId, grade));
    }

    private void MonsterKina(NetworkKinaGain gain)
    {
        if (_farm.Running && ReferenceEquals(_farm.OwnCharacter, gain.Character) && !gain.InInstance
            && !IsAbyss(_world()) && gain.UnboundKina > 0)
            QueueActivity(GainChannel.MonsterKina, gain.Character, gain.At, gain.Sequence, gain.UnboundKina);
    }

    private void AbyssKina(NetworkKinaGain gain)
    {
        var snapshot = _abyss.AbyssSnapshot;
        if (_kina.Running && _abyss.Running && ReferenceEquals(_kina.OwnCharacter, gain.Character)
            && IsAbyss(_world()) && gain.UnboundKina > 0 && snapshot.KinaGainsQualified
            && QualifiedGain(snapshot, gain.Character, gain.At, snapshot.KinaGainsObservedAt))
            QueueActivity(GainChannel.AbyssKina, gain.Character, gain.At, gain.Sequence, gain.UnboundKina, inInstance: gain.InInstance);
    }

    private void FarmDrop(NetworkFarmDrop drop)
    {
        if (_farm.Running && ReferenceEquals(_farm.OwnCharacter, drop.Character) && drop.Quantity > 0 && drop.DataId > 0
            && Enum.IsDefined(drop.Kind) && (drop.Kind != NetworkFarmDropKind.UniversalSoulCrystal
                || drop.DataId == NetworkFarmDrop.UniversalSoulCrystalItemId))
            QueueActivity(GainChannel.Drop, drop.Character, drop.At, drop.Sequence, drop.Quantity, drop, inInstance: drop.InInstance);
    }

    private void PointsGained(NetworkAbyssPointGain gain)
    {
        var snapshot = _abyss.AbyssSnapshot;
        if (_abyss.Running && ReferenceEquals(_abyss.OwnCharacter, gain.Character) && gain.Amount > 0
            && snapshot.AbyssPointGainsQualified && QualifiedGain(snapshot, gain.Character, gain.At, snapshot.AbyssPointBalanceObservedAt))
            QueueActivity(GainChannel.AbyssPoints, gain.Character, gain.At, gain.Sequence, gain.Amount);
    }

    private void RankChanged(NetworkAbyssRankChange gain)
    {
        var snapshot = _abyss.AbyssSnapshot;
        if (_abyss.Running && ReferenceEquals(_abyss.OwnCharacter, gain.Character) && gain.Delta != 0 && gain.Delta != long.MinValue
            && snapshot.RankPointChangesQualified && QualifiedGain(snapshot, gain.Character, gain.At, snapshot.RankPointsObservedAt))
            QueueActivity(GainChannel.RankPoints, gain.Character, gain.At, gain.Sequence, gain.Delta);
    }

    private void Gathered(NetworkGathering gathering)
    {
        if (!_gathering.Running || !_gathering.GatheringVerified || !ReferenceEquals(_gathering.OwnCharacter, gathering.Character)
            || !Enum.IsDefined(gathering.Outcome)
            || gathering.Outcome == NetworkGatheringOutcome.Harvested && gathering.Items.IsDefaultOrEmpty
            || gathering.Outcome == NetworkGatheringOutcome.Failed && !gathering.Items.IsDefaultOrEmpty
            || !gathering.Items.IsDefaultOrEmpty && gathering.Items.Any(item => item is null || item.ItemId <= 0 || item.Quantity <= 0)) return;
        QueueActivity(GainChannel.Harvest, gathering.Character, gathering.At, gathering.Sequence,
            items: gathering.Items.IsDefault ? [] : gathering.Items, outcome: gathering.Outcome,
            gatherableId: gathering.Object?.DataId is > 0 ? gathering.Object.DataId : null, inInstance: gathering.InInstance);
    }

    private void CombatObserved(NetworkAbyssCombatEvent combat)
    {
        if (_combat is not { Running: true } || !_combat.CombatCapabilities.Supports(combat.Kind)
            || !ReferenceEquals(_combat.OwnCharacter, combat.Character)
            || combat.Kind is not (NetworkAbyssCombatKind.Kill or NetworkAbyssCombatKind.Death)) return;
        QueueActivity(GainChannel.Combat, combat.Character, combat.At, combat.Sequence,
            combatKind: combat.Kind == NetworkAbyssCombatKind.Kill ? LootTrackerCombatKind.Kill : LootTrackerCombatKind.Death,
            classId: LootTrackerTimelineFacts.ClassId(combat.OpponentClassId),
            grade: combat.OpponentAbyssRankGrade is >= 1 and <= 19 ? combat.OpponentAbyssRankGrade : null);
    }

    private bool CurrentChannel(Activity activity) => activity.Channel switch
    {
        GainChannel.Harvest => _gathering.Running && _gathering.GatheringVerified && ReferenceEquals(_gathering.OwnCharacter, activity.Own),
        GainChannel.Drop => _farm.Running && ReferenceEquals(_farm.OwnCharacter, activity.Own),
        GainChannel.Combat => _combat is { Running: true } && ReferenceEquals(_combat.OwnCharacter, activity.Own)
            && _combat.CombatCapabilities.Supports(activity.CombatKind == LootTrackerCombatKind.Kill ? NetworkAbyssCombatKind.Kill : NetworkAbyssCombatKind.Death),
        GainChannel.AbyssPoints or GainChannel.RankPoints => _abyss.Running && ReferenceEquals(_abyss.OwnCharacter, activity.Own),
        _ => IsAbyss(activity.World) ? _kina.Running && ReferenceEquals(_kina.OwnCharacter, activity.Own)
            : _farm.Running && ReferenceEquals(_farm.OwnCharacter, activity.Own)
    };

    private void Accept(Activity activity)
    {
        var world = _world(); var now = _time.GetUtcNow();
        if (Volatile.Read(ref _fatal) != 0 || !EventTime(activity.Own, activity.At, activity.Sequence)
            || !ReferenceEquals(_own, activity.Own) || !ReferenceEquals(CurrentOwn, activity.Own) || !CurrentChannel(activity)
            || ValidCharacter(activity.Character) is not { } character
            || !QualifiedWorld(activity.World, activity.Own, activity.At)
            || !QualifiedWorld(world, activity.Own, now) || world.Generation != activity.World.Generation
            || _observedWorld is null || _observedWorld.Generation > activity.World.Generation
            || !QualifiedWorld(_observedWorld, activity.Own, now)) return;
        if (_sequences.TryGetValue(activity.Channel, out var previous)
            && previous.Generation == activity.Own.Generation && previous.Sequence >= activity.Sequence) return;
        // Valid but switched-off observations still consume their own watermark; reenabling never replays an echo.
        _sequences[activity.Channel] = (activity.Own.Generation, activity.Sequence);
        if (!activity.Enabled || !ChannelEnabled(activity.Channel, world)) return;
        var abyss = IsAbyss(world);
        if (activity.Channel == GainChannel.Harvest && activity.InInstance != abyss) return;
        // A fact belongs to the farm day of its capture time, even when the reset passed while it was queued.
        // Only a session that already runs for a newer farm day cannot take an older fact.
        if (!abyss && (activity.InInstance || activity.Channel == GainChannel.Combat
                || _active is { Kind: LootTrackerSessionKind.Mixed, FarmDay: { } openDay } && openDay > LootFarmDay.Get(activity.At))) return;
        if (abyss && _active?.Kind != LootTrackerSessionKind.Abyss) return; // Own entry, never a late reward, opens residence.
        var rewards = Rewards(activity);
        Expire(activity.At, activity.Stamp);
        if (_active is not null && (_active.Character?.ProfileId != character.ProfileId || (_active.Kind == LootTrackerSessionKind.Abyss) != abyss))
            Finish(LootTrackerSessionStatus.Completed);
        if (_active is null) Start(character, activity.Own, world, activity.At, activity.Stamp);
        if (_active is not { } active || activity.At < active.StartedAt) return;
        var elapsed = Elapsed(activity.Stamp);
        if (abyss) Presence(activity.At, activity.Stamp);
        RecordPosition(activity.Position, world, activity.Own, activity.At, activity.Stamp, true);
        active = _active!;
        var origin = activity.Channel == GainChannel.Harvest ? LootTrackerActivityOrigin.Lifeskill
            : abyss ? LootTrackerActivityOrigin.Abyss : LootTrackerActivityOrigin.Pve;
        var next = active with { Elapsed = MaxElapsed(active.Elapsed, elapsed), LastLootAt = Later(active.LastLootAt, activity.At),
            LastPresenceAt = abyss ? Later(active.LastPresenceAt, activity.At) : null };
        next = Availability(next, activity.At, elapsed, activity.Channel);
        var accepted = new List<AcceptedFarmLootDrop>();
        switch (activity.Channel)
        {
            case GainChannel.Drop:
                next = AddRewards(next, rewards);
                var dropPosition = QualifiedPosition(activity.Position, activity.Own, world, activity.At);
                next = AddFact(next, LootTrackerTimelineEventKind.Drop, activity, elapsed, origin, loot: rewards,
                    position: dropPosition is null ? null : new(dropPosition.MapId, _mapName(dropPosition.MapId),
                        dropPosition.X, dropPosition.Y, dropPosition.At));
                break;
            case GainChannel.Harvest:
                var succeeded = activity.Outcome == NetworkGatheringOutcome.Harvested;
                var visit = GatherVisit(activity, succeeded);
                next = AddRewards(next, rewards) with
                {
                    HarvestCount = checked((next.HarvestCount ?? 0) + (succeeded ? 1 : 0)),
                    FailedHarvestCount = checked((next.FailedHarvestCount ?? 0) + (succeeded ? 0 : 1)),
                    GatherVisits = visit is null ? next.GatherVisits : next.GatherVisits.Add(visit)
                };
                next = AddFact(next, succeeded ? LootTrackerTimelineEventKind.HarvestSuccess : LootTrackerTimelineEventKind.HarvestFailure,
                    activity, elapsed, origin, loot: rewards, visit: visit);
                break;
            case GainChannel.Combat:
                var position = QualifiedPosition(activity.Position, activity.Own, world, activity.At);
                var placed = position is null ? null : new LootTrackerCombatPosition(position.MapId, _mapName(position.MapId), position.X, position.Y, position.At);
                next = next with { CombatEvents = next.CombatEvents.Add(new(Guid.NewGuid().ToString("N"), activity.CombatKind!.Value,
                    activity.At, elapsed, placed) { Origin = LootTrackerActivityOrigin.Abyss,
                    OpponentClassId = activity.OpponentClassId, OpponentAbyssRankGrade = activity.OpponentAbyssRankGrade }) };
                break;
            default:
                next = AddIncome(next, activity, elapsed, origin);
                break;
        }
        foreach (var reward in rewards) accepted.Add(new(next.Id, character.ProfileId!, activity.At, reward.Id, reward.Name, reward.Quantity));
        if (!abyss && QualifiedDaily(_daily, activity.Own, activity.At) && _daily.Total is { } total && total >= Overlay.LootOverlaySnapshot.DailyLimit)
            next = next with { Mode = LootTrackerSessionMode.LimitedKina };
        Save(next);
        _lastActivityStamp = Math.Max(_lastActivityStamp, activity.Stamp);
        _lastActivityElapsedTicks = Math.Max(_lastActivityElapsedTicks, elapsed.Ticks);
        if (!abyss) Expire(_time.GetUtcNow(), _time.GetTimestamp());
        foreach (var drop in accepted) SafeEvent.Raise(DropAccepted, drop, "Neue Loot-Beute");
    }

    private ImmutableArray<LootTrackerLoot> Rewards(Activity activity)
    {
        if (activity.Channel == GainChannel.Drop && activity.Drop is { } drop)
            return [new($"{drop.Kind}:{drop.DataId}", DropName(drop.Kind, drop.DataId),
                drop.Kind == NetworkFarmDropKind.Item ? LootTrackerLootKind.Item : LootTrackerLootKind.Soul, drop.Quantity)];
        if (activity.Channel != GainChannel.Harvest || activity.Items.IsDefaultOrEmpty) return [];
        var result = activity.Items.GroupBy(item => item.ItemId).Select(group => new LootTrackerLoot($"Item:{group.Key}",
            _itemName(group.Key), LootTrackerLootKind.Item, group.Aggregate(0L, (sum, item) => checked(sum + item.Quantity)))).ToImmutableArray();
        _ = result.Aggregate(0L, (sum, row) => checked(sum + row.Quantity));
        return result;
    }

    private static LootTrackerSession AddRewards(LootTrackerSession session, ImmutableArray<LootTrackerLoot> rewards)
    {
        var loot = session.Loot.ToBuilder(); var itemCount = session.ItemCount ?? 0; var soulCount = session.SoulCount ?? 0;
        foreach (var reward in rewards)
        {
            var index = -1;
            for (var i = 0; i < loot.Count; i++) if (loot[i].Id == reward.Id) { index = i; break; }
            if (index < 0) loot.Add(reward); else loot[index] = loot[index] with { Quantity = checked(loot[index].Quantity + reward.Quantity) };
            if (reward.Kind == LootTrackerLootKind.Item) itemCount = checked(itemCount + reward.Quantity);
            else soulCount = checked(soulCount + reward.Quantity);
        }
        return session with { Loot = loot.ToImmutable(), ItemCount = itemCount, SoulCount = soulCount };
    }

    private static LootTrackerSession AddFact(LootTrackerSession session, LootTrackerTimelineEventKind kind,
        Activity activity, TimeSpan elapsed, LootTrackerActivityOrigin origin, long? amount = null,
        ImmutableArray<LootTrackerLoot> loot = default, LootTrackerGatherVisit? visit = null, LootTrackerCombatPosition? position = null) =>
        session with { TimelineEvents = session.TimelineEvents.Add(LootTrackerTimelineFacts.Create(kind, activity.At, elapsed) with
        { Origin = origin, Amount = amount, Loot = loot.IsDefault ? [] : loot, GatherVisit = visit, Position = position,
            GatherableId = activity.Channel == GainChannel.Harvest ? activity.GatherableId : null }) };

    private static LootTrackerSession AddIncome(LootTrackerSession session, Activity activity, TimeSpan elapsed, LootTrackerActivityOrigin origin)
    {
        var minute = (int)Math.Min(int.MaxValue, Math.Max(0, Math.Floor(elapsed.TotalMinutes)));
        var bins = session.MinuteIncome.ToBuilder(); var index = -1;
        for (var i = 0; i < bins.Count; i++) if (bins[i].Minute == minute) { index = i; break; }
        var bin = index >= 0 ? bins[index] : new LootTrackerIncomeBin(minute, null, null);
        LootTrackerTimelineEventKind kind;
        switch (activity.Channel)
        {
            case GainChannel.MonsterKina:
            case GainChannel.AbyssKina:
                session = session with { KinaUnbound = checked((session.KinaUnbound ?? 0) + activity.Amount) };
                bin = bin with { KinaUnbound = checked((bin.KinaUnbound ?? 0) + activity.Amount) };
                kind = LootTrackerTimelineEventKind.KinaUnbound; break;
            case GainChannel.AbyssPoints:
                session = session with { AbyssPoints = checked((session.AbyssPoints ?? 0) + activity.Amount) };
                bin = bin with { AbyssPoints = checked((bin.AbyssPoints ?? 0) + activity.Amount) };
                kind = LootTrackerTimelineEventKind.AbyssPoints; break;
            default:
                if (activity.Amount > 0)
                {
                    session = session with { RankPointsGained = checked((session.RankPointsGained ?? 0) + activity.Amount) };
                    bin = bin with { RankPointsGained = checked((bin.RankPointsGained ?? 0) + activity.Amount) };
                }
                else
                {
                    session = session with { RankPointsLost = checked((session.RankPointsLost ?? 0) - activity.Amount) };
                    bin = bin with { RankPointsLost = checked((bin.RankPointsLost ?? 0) - activity.Amount) };
                }
                kind = LootTrackerTimelineEventKind.RankPoints; break;
        }
        if (index < 0) bins.Add(bin); else bins[index] = bin;
        return AddFact(session with { MinuteIncome = bins.ToImmutable() }, kind, activity, elapsed, origin, activity.Amount);
    }

    private string DropName(NetworkFarmDropKind kind, int id) => kind switch
    {
        NetworkFarmDropKind.Item or NetworkFarmDropKind.UniversalSoulCrystal => MonsterLootItemCatalog.Default.Name(id, _settings.Current.LootNameLanguage),
        NetworkFarmDropKind.PetSoul => PetIdTable.Find(id)?.En ?? $"Pet #{id}",
        _ => MonsterLootItemCatalog.Default.Name(id switch { 1 => 531900005, 2 => 531900004, 3 => 531900006, 4 => 531900007, 5 => 531900008, _ => 0 }, _settings.Current.LootNameLanguage)
    };
}
