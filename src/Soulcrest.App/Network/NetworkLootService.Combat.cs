using System.Collections.Immutable;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Network;

public sealed partial class NetworkLootService
{
    private const int MaximumCombatEntryBytes = 2 * 1024 * 1024;
    // Load the small immutable transport policy once at construction, outside packet dispatch.
    private readonly CombatAchievementRewardScopes _achievementRewardScopes = CombatAchievementRewardScopes.Default;
    private NetworkCharacterCombatSnapshot _combatSnapshot = NetworkCharacterCombatSnapshot.Empty;
    private (long CharacterId, int ServerId)? _combatLastCharacter;
    private bool _combatOwnQualified;
    // User correction 2026-11-06: titles belong to the character. A RAM-only continuity witness may
    // survive a same-character entry, but never a character, capture or source-stream boundary.
    private string? _titleOwnershipStream;
    private (long CharacterId, int ServerId)? _titleOwnershipCharacter;
    private NetworkCombatDomain<ImmutableHashSet<int>>? _titleOwnershipWitness;
    private long _titleOwnershipCaptureGeneration = -1;
    private DateTimeOffset? _titleOwnershipTransitionStartedAt;
    private EntryCandidate? _titleOwnershipTransitionCandidate;
    private string? _titleOwnershipTransitionStream;
    private readonly record struct TitleOwnershipEntry(string Stream, long CharacterId, int? ServerId);
    public NetworkCharacterCombatSnapshot CombatSnapshot { get { lock (_gate) return _combatSnapshot; } }

    private static NetworkCombatDomain<T> Stale<T>(NetworkCombatDomain<T> value) where T : class =>
        value.Value is null ? value : value with { Status = NetworkCombatSourceStatus.Stale, IsFresh = false };
    private static NetworkCombatDomain<T> Conflict<T>(NetworkCombatDomain<T> value) where T : class =>
        value with { Status = NetworkCombatSourceStatus.Conflict, IsFresh = false };
    private static NetworkCombatDomain<T> Observed<T>(T value, DateTimeOffset at, long sequence, string source,
        NetworkCombatSourceStatus status = NetworkCombatSourceStatus.Complete) where T : class =>
        new(value, status, true, at, sequence, source);

    private NetworkCombatDomain<T> BindPending<T>(NetworkCombatDomain<T> pending, NetworkCombatDomain<T> fallback,
        DateTimeOffset entered) where T : class => pending.Status == NetworkCombatSourceStatus.Unknown ? fallback :
        pending.ObservedAt is { } at && at > entered ? Conflict(fallback) : pending with { Sequence = ++_progressSequence };

    private void ResetCombatContext(bool softEntry = false, DateTimeOffset? at = null, TitleOwnershipEntry? entry = null)
    {
        ResetPantheonContext();
        var previousAchievementOwner = _combatSnapshot.Character ?? _achievementSnapshot?.Character;
        _combatOwnQualified = false;
        _titleOwnershipTransitionCandidate = softEntry ? _entryCandidate : null;
        _titleOwnershipTransitionStream = softEntry ? entry?.Stream ?? _entryCandidate?.Stream ?? _identityStream : null;
        var preserveTitleOwnership = softEntry && entry is { } target && at is { } entered
            && _titleOwnershipCharacter is { } owner && owner.CharacterId == target.CharacterId
            && (target.ServerId is null || target.ServerId == owner.ServerId)
            && _titleOwnershipStream == target.Stream && _titleOwnershipCaptureGeneration == _generation
            && _titleOwnershipWitness is { IsFresh: true, ObservedAt: { } observed } && entered >= observed
            && (_titleOwnershipTransitionStartedAt is not { } started
                || entered >= started && entered - started <= EntryCandidateLifetime);
        if (!preserveTitleOwnership) ForgetTitleOwnershipWitness();
        else _titleOwnershipTransitionStartedAt ??= at;
        _combatSnapshot = _combatSnapshot with
        {
            Character = null, CurrentCombatScore = null, CharacterLevel = null, CombatLevel = null, ConquerorLevel = null,
            Generation = _progressGeneration, Stats = Stale(_combatSnapshot.Stats),
            CraftingProficiencies = Stale(_combatSnapshot.CraftingProficiencies),
            DaevanionBoards = Stale(_combatSnapshot.DaevanionBoards), Titles = Stale(_combatSnapshot.Titles),
            // A prefix never publishes fresh character-owned data. The full Own must first
            // confirm the private witness's native identity and transport continuity.
            TitleOwnership = Stale(_combatSnapshot.TitleOwnership),
            WingOwnership = Stale(_combatSnapshot.WingOwnership), EquippedWing = Stale(_combatSnapshot.EquippedWing),
            Achievements = Stale(_combatSnapshot.Achievements), Equipment = Stale(_combatSnapshot.Equipment),
            Pets = Stale(_combatSnapshot.Pets), DaevanionPoints = Stale(_combatSnapshot.DaevanionPoints),
            Abnormals = Stale(_combatSnapshot.Abnormals),
            HpMaximum = Stale(_combatSnapshot.HpMaximum), MpMaximum = Stale(_combatSnapshot.MpMaximum)
        };
        PublishAchievementObservation(previousAchievementOwner, _combatSnapshot.Achievements,
            NetworkAchievementObservationKind.Invalidated, at ?? DateTimeOffset.UtcNow);
    }

    private void BindCombatBaseline(EntryCandidate? candidate, OwnWorldObservation? world)
    {
        if (_ownCharacter is not { } own || world is null)
        {
            ForgetTitleOwnershipWitness();
            _combatSnapshot = NetworkCharacterCombatSnapshot.Empty;
            return;
        }
        var same = _combatLastCharacter is { } last && last.CharacterId == own.Identity.CharacterId && last.ServerId == own.Identity.ServerId;
        var previousOwnership = _combatSnapshot.TitleOwnership;
        var snapshot = same ? _combatSnapshot : NetworkCharacterCombatSnapshot.Empty;
        if (same && _titleOwnershipWitness is { } witness && _titleOwnershipCharacter is { } owner
            && owner.CharacterId == own.Identity.CharacterId && owner.ServerId == own.Identity.ServerId
            && _titleOwnershipStream == _identityStream && _titleOwnershipCaptureGeneration == _generation)
            snapshot = snapshot with { TitleOwnership = witness };
        else ForgetTitleOwnershipWitness();
        _combatLastCharacter = (own.Identity.CharacterId, own.Identity.ServerId);
        _combatOwnQualified = true;
        var sequence = ++_progressSequence;
        snapshot = snapshot with
        {
            Character = own, ClassId = own.Identity.ClassId, CharacterLevel = world.CharacterLevel,
            CombatLevel = world.CombatLevel, ConquerorLevel = world.ConquerorLevel,
            CurrentCombatScore = world.CombatScore, Generation = own.Generation, Sequence = sequence,
            ObservedAt = own.ObservedAt,
            CraftingProficiencies = world.CraftingProficiencies is { } crafting
                ? Observed(crafting, own.ObservedAt!.Value, sequence, "own-entry-crafting-proficiencies")
                : NetworkCombatDomain<ImmutableDictionary<byte, CharacterCraftingProficiency>>.Unknown,
            HpMaximum = world.ResourceMaximums.MaxHp is { } hp
                ? Observed(new NetworkCharacterResourceMaximum(hp), own.ObservedAt!.Value, sequence, "own-entry-resource-maxima")
                : NetworkCombatDomain<NetworkCharacterResourceMaximum>.Unknown,
            MpMaximum = world.ResourceMaximums.MaxMp is { } mp
                ? Observed(new NetworkCharacterResourceMaximum(mp), own.ObservedAt!.Value, sequence, "own-entry-resource-maxima")
                : NetworkCombatDomain<NetworkCharacterResourceMaximum>.Unknown,
            Stats = Observed(world.StatSubset, own.ObservedAt!.Value, sequence, "own-entry-stat-subset", NetworkCombatSourceStatus.Partial),
            EquippedWing = world.EquippedWing is { } wing && (wing.WingId is not null || wing.SkinId is not null || wing.EnchantLevel is not null)
                ? Observed(wing, own.ObservedAt.Value, sequence, "own-entry-wing-state",
                    wing.WingId is not null ? NetworkCombatSourceStatus.Complete : NetworkCombatSourceStatus.Partial)
                : NetworkCombatDomain<CharacterWingEquipment>.Unknown,
            Abnormals = world.Abnormals is { } abnormals
                ? Observed(abnormals, own.ObservedAt.Value, sequence, "own-entry-abnormal-list")
                : NetworkCombatDomain<CharacterAbnormalState>.Unknown,
            DaevanionPoints = world.DaevanionPoints is { } points
                ? Observed(points, own.ObservedAt.Value, sequence, "own-entry-used-bonus-points")
                : NetworkCombatDomain<CharacterDaevanionPointState>.Unknown
        };
        // A prepared transaction is confirmed by matching native character+entity+transport in the
        // shared Own binder. No prefix observation supplies authority by itself.
        if (candidate is { Combat: { } pending })
        {
            snapshot = snapshot with
            {
                Stats = BindPending(pending.Stats, snapshot.Stats, own.ObservedAt.Value),
                DaevanionBoards = BindPending(pending.DaevanionBoards, snapshot.DaevanionBoards, own.ObservedAt.Value),
                Titles = BindPending(pending.Titles, snapshot.Titles, own.ObservedAt.Value),
                TitleOwnership = BindPending(pending.TitleOwnership, snapshot.TitleOwnership, own.ObservedAt.Value),
                WingOwnership = BindPending(pending.WingOwnership, snapshot.WingOwnership, own.ObservedAt.Value),
                Achievements = BindPending(pending.Achievements, snapshot.Achievements, own.ObservedAt.Value),
                Equipment = BindPending(pending.Equipment, snapshot.Equipment, own.ObservedAt.Value),
                Pets = BindPending(pending.Pets, snapshot.Pets, own.ObservedAt.Value)
            };
        }
        RememberTitleOwnership(previousOwnership, snapshot.TitleOwnership, _identityStream!);
        _titleOwnershipTransitionStartedAt = null;
        _titleOwnershipTransitionCandidate = null;
        _titleOwnershipTransitionStream = null;
        _combatSnapshot = snapshot with { Sequence = _progressSequence };
        BindPantheonBaseline(candidate, own);
        ObserveAchievementBaseline(own.ObservedAt.Value);
    }

    private void ForgetTitleOwnershipWitness()
    {
        _titleOwnershipStream = null;
        _titleOwnershipCharacter = null;
        _titleOwnershipWitness = null;
        _titleOwnershipCaptureGeneration = -1;
        _titleOwnershipTransitionStartedAt = null;
    }

    private void RememberTitleOwnership(NetworkCombatDomain<ImmutableHashSet<int>> previous,
        NetworkCombatDomain<ImmutableHashSet<int>> current, string stream)
    {
        if (ReferenceEquals(previous, current)) return;
        if (current is { IsFresh: true, Status: NetworkCombatSourceStatus.Complete, Value: not null, ObservedAt: not null }
            && current.Source == "own-title-ownership-list" && _ownCharacter is { } own)
        {
            _titleOwnershipStream = stream;
            _titleOwnershipCharacter = (own.Identity.CharacterId, own.Identity.ServerId);
            _titleOwnershipWitness = current;
            _titleOwnershipCaptureGeneration = _generation;
        }
        else ForgetTitleOwnershipWitness();
    }

    /// <summary>Also revokes a provisional title proof held by a reentrant soft-entry reset.</summary>
    private bool InvalidateTitleOwnershipForGap(string stream)
    {
        var sourceAffected = stream == "*" || _titleOwnershipStream == stream;
        var transitionAffected = stream == "*" || _titleOwnershipTransitionStream == stream;
        if (!sourceAffected && !transitionAffected) return false;
        var previous = _combatSnapshot.TitleOwnership;
        _combatSnapshot = _combatSnapshot with { TitleOwnership = Stale(previous) };
        if (transitionAffected && _titleOwnershipTransitionCandidate is { } candidate)
            candidate.Combat = candidate.Combat with
            {
                TitleOwnership = Stale(candidate.Combat.TitleOwnership),
                Titles = Stale(candidate.Combat.Titles)
            };
        ForgetTitleOwnershipWitness();
        _titleOwnershipTransitionCandidate = null;
        _titleOwnershipTransitionStream = null;
        return !ReferenceEquals(previous, _combatSnapshot.TitleOwnership);
    }

    private static bool IsCombatOpcode(ushort opcode) => opcode is
        0x3649 or 0x364A or 0x3656 or 0x8D00 or 0xE226 or 0xE22E or 0x5672 or 0x5676 or 0x5679 or 0x5683 or 0x5684 or 0x5686 or 0x5611 or 0x561B or 0x9000 or 0x9018 or 0x901A;

    /// <summary>Called under the shared dispatch gate; this method has no callbacks or capture of its own.</summary>
    private bool ProcessOwnCombatMessage(string stream, ushort opcode, byte[] message, DateTimeOffset at)
    {
        if (!IsCombatOpcode(opcode)) return false;
        var own = _combatOwnQualified && _identityStream == stream ? _ownCharacter : null;
        // A spent-point update never grants its own authority or supplies a provisional login
        // baseline. Only the complete current Own establishes the matching bonus map and epoch.
        if (opcode is 0xE22E or 0x8D00 or 0x5686 or 0x5676 or 0x9018 or 0x901A && own is null) return false;
        // Observed login order: Entry -> titles/achievements/boards/inventory -> Prepare -> pets -> Own.
        // These prefixes remain provisional, bounded and transport-bound until a matching full Own.
        var pending = own is null && _entryCandidate is { } candidate && candidate.Stream == stream
            && at >= candidate.StartedAt && (candidate.Prepared && candidate.PreparedAt is { } prepared && at >= prepared
                || !candidate.Prepared && opcode is 0x5672 or 0x5679 or 0x5683 or 0xE226 or 0x5611 or 0x5684 or 0x561B)
            ? candidate : null;
        if (own is null && pending is null) return false;
        if (own is not null && own.ObservedAt is { } entered && at < entered) return false;
        var snapshot = pending?.Combat ?? _combatSnapshot;
        if (pending is not null)
        {
            pending.CombatMessages++;
            pending.CombatBytes += message.Length;
            if (pending.CombatMessages > 64 || pending.CombatBytes > MaximumCombatEntryBytes)
            {
                pending.Combat = RejectAllCombat(snapshot); return true;
            }
            // A change alone cannot complete a domain. Achievement and inventory changes are
            // absolute, fully read records and may refine their already observed provisional list.
            // Stat updates carry an entity and do not acquire authority before the full Own.
            if (opcode is 0x364A or 0x3656)
            {
                pending.Combat = snapshot.Stats.Value is not null ? RejectCombatDomain(snapshot, opcode) : snapshot;
                return true;
            }
            if (opcode is not (0x5684 or 0x561B) && !pending.CombatSources.Add(opcode))
            {
                pending.Combat = RejectCombatDomain(snapshot, opcode); return true;
            }
        }
        var sequence = ++_progressSequence;
        var next = ApplyCombatMessage(snapshot, own, opcode, message, at, sequence);
        if (ReferenceEquals(next, snapshot)) return false;
        if (pending is not null) pending.Combat = next;
        else
        {
            RememberTitleOwnership(snapshot.TitleOwnership, next.TitleOwnership, stream);
            _combatSnapshot = next with { Sequence = sequence, ObservedAt = snapshot.ObservedAt is { } previous && previous > at ? previous : at };
            ObserveAchievementChange(snapshot.Achievements, opcode, at);
        }
        return true;
    }

    private static bool IsOlder<T>(NetworkCombatDomain<T> domain, DateTimeOffset at) where T : class =>
        domain.ObservedAt is { } previous && at < previous;

    private NetworkCharacterCombatSnapshot ApplyCombatMessage(NetworkCharacterCombatSnapshot state,
        OwnCharacterContext? own, ushort opcode, byte[] message, DateTimeOffset at, long sequence)
    {
        switch (opcode)
        {
            case 0x3649:
            case 0x364A:
                if (IsOlder(state.Stats, at)) return state;
                var stats = CharacterCombatMessage.TryParseStats(message);
                if (stats is null) return state with { Stats = Conflict(state.Stats) };
                if (stats.EntityKey is { } entity && entity != own?.Identity.EntityKey) return state;
                if (stats.IsComplete) return state with { Stats = Observed(stats.Stats, at, sequence, "own-full-stat-list") };
                if (!state.Stats.IsFresh) return state with { Abnormals = Stale(state.Abnormals) };
                return state with { Stats = Observed((state.Stats.Value ?? ImmutableDictionary<ushort, int>.Empty).SetItems(stats.Stats),
                    at, sequence, "own-stat-update", state.Stats.Status), Abnormals = Stale(state.Abnormals) };
            case 0x8D00:
                // A readable prefix may exclude a clearly foreign entity, but never grants own authority.
                if (CharacterResourceMessage.TryReadEntityKeyPrefix(message) is { } resourceEntity
                    && resourceEntity != own?.Identity.EntityKey) return state;
                var resources = CharacterResourceMessage.TryParseUpdate(message);
                if (resources is null)
                {
                    var invalidHp = IsOlder(state.HpMaximum, at) ? state.HpMaximum : Conflict(state.HpMaximum);
                    var invalidMp = IsOlder(state.MpMaximum, at) ? state.MpMaximum : Conflict(state.MpMaximum);
                    return ReferenceEquals(invalidHp, state.HpMaximum) && ReferenceEquals(invalidMp, state.MpMaximum)
                        ? state : state with { HpMaximum = invalidHp, MpMaximum = invalidMp };
                }
                if (resources.EntityKey != own?.Identity.EntityKey) return state;
                // Each maximum keeps its own fact time. An HP-only update must not refresh MP.
                var hpMaximum = resources.Maximums.MaxHp is { } hp && !IsOlder(state.HpMaximum, at)
                    ? Observed(new NetworkCharacterResourceMaximum(hp), at, sequence, "own-resource-maxima-update") : state.HpMaximum;
                var mpMaximum = resources.Maximums.MaxMp is { } mp && !IsOlder(state.MpMaximum, at)
                    ? Observed(new NetworkCharacterResourceMaximum(mp), at, sequence, "own-resource-maxima-update") : state.MpMaximum;
                return ReferenceEquals(hpMaximum, state.HpMaximum) && ReferenceEquals(mpMaximum, state.MpMaximum)
                    ? state : state with { HpMaximum = hpMaximum, MpMaximum = mpMaximum };
            case 0x3656:
                if (state.ObservedAt is { } scoreAt && at < scoreAt) return state;
                return state with { CurrentCombatScore = CharacterCombatMessage.TryParseCombatScore(message) };
            case 0xE226:
                if (IsOlder(state.DaevanionBoards, at)) return state;
                var boards = CharacterCombatMessage.TryParseBoards(message);
                if (boards is null) return state with { DaevanionBoards = Conflict(state.DaevanionBoards) };
                // E226 proves each included board's node set; missing board IDs stay unknown.
                return state with { DaevanionBoards = Observed(boards, at, sequence, "own-daevanion-board-lists", NetworkCombatSourceStatus.Partial) };
            case 0xE22E:
                if (IsOlder(state.DaevanionPoints, at)) return state;
                var used = CharacterCombatMessage.TryParseUsedDaevanionPoints(message);
                if (used is null) return state with { DaevanionPoints = Conflict(state.DaevanionPoints) };
                if (!state.DaevanionPoints.IsFresh || state.DaevanionPoints.Value is not { } points) return state;
                // Same qualified own generation only. Preserve its independently observed bonus
                // values; spent values cannot establish Free, Total or a new bonus-point source.
                return state with { DaevanionPoints = Observed(points with { Used = used.Used }, at, sequence,
                    "own-used-daevanion-point-map", state.DaevanionPoints.Status) };
            case 0x5672:
                if (IsOlder(state.WingOwnership, at)) return state;
                var wings = CharacterWingMessage.TryParseList(message);
                return state with { WingOwnership = wings is null ? Conflict(state.WingOwnership)
                    : Observed(wings, at, sequence, "own-wing-ownership-list") };
            case 0x5676:
                if (own is null || IsOlder(state.EquippedWing, at)) return state;
                // A readable foreign prefix must not invalidate the own domain, including malformed tails.
                if (CharacterWingMessage.TryReadEntityKeyPrefix(message) is { } wingEntity
                    && wingEntity != own.Identity.EntityKey) return state;
                var wingChange = CharacterWingMessage.TryParseChange(message);
                if (wingChange is null) return state with { EquippedWing = Conflict(state.EquippedWing) };
                if (wingChange.EntityKey != own.Identity.EntityKey) return state;
                var currentWing = state.EquippedWing.IsFresh ? state.EquippedWing.Value : null;
                // A cosmetic change carries no evidence about the stat-bearing wing. A stat-wing
                // change carries no enhancement level; never attach the previous wing's level.
                var changedWing = wingChange.IsWingSkin
                    ? new CharacterWingEquipment(currentWing?.WingId, currentWing?.EnchantLevel, wingChange.WingId)
                    : new CharacterWingEquipment(wingChange.WingId,
                        currentWing?.WingId == wingChange.WingId ? currentWing.EnchantLevel : null, currentWing?.SkinId);
                return state with { EquippedWing = Observed(changedWing, at, sequence, "own-wing-change",
                    changedWing.WingId is not null ? NetworkCombatSourceStatus.Complete : NetworkCombatSourceStatus.Partial) };
            case 0x5679:
                if (IsOlder(state.Titles, at)) return state;
                var titles = CharacterCombatMessage.TryParseTitles(message);
                return state with
                {
                    Titles = titles is null ? Conflict(state.Titles) : Observed(titles, at, sequence, "own-title-list"),
                    TitleOwnership = IsOlder(state.TitleOwnership, at) ? state.TitleOwnership : titles is null
                        ? Conflict(state.TitleOwnership) : Observed(titles.OwnedTitleIds, at, sequence, "own-title-ownership-list")
                };
            case 0x5683:
            case 0x5684:
                if (IsOlder(state.Achievements, at)) return state;
                var achievements = CharacterCombatMessage.TryParseAchievements(message);
                if (achievements is null) return state with { Achievements = Conflict(state.Achievements) };
                if (opcode == 0x5683) return state with { Achievements = Observed(achievements, at, sequence, "own-achievement-list") };
                if (!state.Achievements.IsFresh || state.Achievements.Value is not { } existingAchievements)
                {
                    // A current fully bound Own gives absolute rows their own authority. Start a
                    // partial source from only this patch; stale rows and reward groups stay stale.
                    // Provisional login updates still need their already received full list.
                    if (own is null || achievements.Achievements.IsEmpty) return state;
                    return state with { Achievements = Observed(new CharacterAchievementState(
                        achievements.Achievements, ImmutableDictionary<int, ImmutableArray<bool>>.Empty),
                        at, sequence, "own-achievement-update", NetworkCombatSourceStatus.Partial) };
                }
                return state with { Achievements = Observed(existingAchievements with
                { Achievements = existingAchievements.Achievements.SetItems(achievements.Achievements) },
                    at, sequence, "own-achievement-update", state.Achievements.Status) };
            case 0x5686:
                // Controlled tier-II/full-list and independent level-stage observations qualify
                // a shared individual prefix ABI for these explicit no-reset chains only.
                // An ACK supplies no baseline, counter, time, group or availability fact.
                if (own is null || IsOlder(state.Achievements, at) || !state.Achievements.IsFresh
                    || state.Achievements.Status is not (NetworkCombatSourceStatus.Complete or NetworkCombatSourceStatus.Partial)
                    || state.Achievements.Value is not { } rewardedAchievements) return state;
                var reward = CharacterCombatMessage.TryParseAchievementReward(message);
                if (reward is null || !_achievementRewardScopes.Allows(reward.AchievementId, reward.RewardedStep)
                    || !rewardedAchievements.Achievements.TryGetValue(reward.AchievementId, out var rewardedRow)
                    || reward.RewardedStep <= rewardedRow.RewardedStep) return state;
                return state with { Achievements = Observed(rewardedAchievements with
                { Achievements = rewardedAchievements.Achievements.SetItem(reward.AchievementId,
                    rewardedRow with { RewardedStep = reward.RewardedStep }) },
                    at, sequence, "own-achievement-reward", state.Achievements.Status) };
            case 0x5611:
                if (IsOlder(state.Equipment, at)) return state;
                var inventory = CurrencyInventoryMessage.TryParseList(message);
                if (inventory is not null && inventory.AllItems.Where(item => item.Container == 11 && item.Equipment is not null)
                    .GroupBy(item => item.InstanceKey).Any(group => group.Count() > 1))
                    return state with { Equipment = Conflict(state.Equipment) };
                return state with { Equipment = inventory is null ? Conflict(state.Equipment) :
                    Observed(new CharacterEquipmentState(inventory.AllItems.Where(item => item.Container == 11 && item.Equipment is not null)
                        .Select(item => new CharacterEquipmentItem(item.ItemId, item.Equipment!) { InstanceKey = item.InstanceKey }).ToImmutableArray()),
                        at, sequence, "own-observed-equipped-inventory", NetworkCombatSourceStatus.Partial) };
            case 0x561B:
                if (IsOlder(state.Equipment, at)) return state;
                var changes = CurrencyInventoryMessage.TryParseChanges(message);
                if (changes is null) return state with { Equipment = Conflict(state.Equipment) };
                if (!state.Equipment.IsFresh || state.Equipment.Value is not { } currentGear) return state;
                var items = currentGear.Items.ToList();
                foreach (var change in changes.AllChanges)
                {
                    items.RemoveAll(item => item.InstanceKey == change.Item.InstanceKey);
                    if (change.ModificationType != 2 && change.Item is { Container: 11, Equipment: { } detail } item)
                        items.Add(new(item.ItemId, detail) { InstanceKey = item.InstanceKey });
                }
                return state with { Equipment = Observed(new CharacterEquipmentState(items.ToImmutableArray()),
                    at, sequence, "own-equipped-inventory-update", state.Equipment.Status) };
            case 0x9000:
                if (IsOlder(state.Pets, at)) return state;
                var pets = PetProgressMessage.TryParseSnapshot(message);
                return state with { Pets = pets is null ? Conflict(state.Pets) : Observed(pets, at, sequence, "own-pet-genus-list") };
            case 0x901A:
                // This reply has no actor key. It can refine only the fresh, complete
                // own-bound list on the current transport; it cannot restore a gap or entry.
                if (IsOlder(state.Pets, at) || state.Pets is not { IsFresh: true, Status: NetworkCombatSourceStatus.Complete,
                        Value: { } baseline } || state.Pets.Source is not ("own-pet-genus-list" or "own-pet-genus-lock-update" or "own-pet-genus-roll-update")) return state;
                var locked = CharacterGenusMessage.TryParseSlotLock(message);
                if (locked is null) return state with { Pets = Conflict(state.Pets) };
                var matchingBoards = baseline.GenusBoards.Where(pair => pair.Value.CreatureType == locked.CreatureType).Take(2).ToArray();
                if (matchingBoards.Length != 1) return state with { Pets = Conflict(state.Pets) };
                var board = matchingBoards[0].Value;
                if (!board.Panels.TryGetValue(locked.Panel, out var panel)
                    || panel.Count(slot => slot.Slot == locked.Slot) != 1)
                    return state with { Pets = Conflict(state.Pets) };
                var changed = panel.Select(slot => slot.Slot == locked.Slot ? slot with { IsLocked = locked.IsLocked } : slot).ToImmutableArray();
                var refined = baseline with { GenusBoards = baseline.GenusBoards.SetItem(matchingBoards[0].Key,
                    board with { Panels = board.Panels.SetItem(locked.Panel, changed) }) };
                return state with { Pets = Observed(refined, at, sequence, "own-pet-genus-lock-update") };
            case 0x9018:
                if (IsOlder(state.Pets, at) || state.Pets is not { IsFresh: true, Status: NetworkCombatSourceStatus.Complete,
                        Value: { } rollBaseline } || state.Pets.Source is not ("own-pet-genus-list" or "own-pet-genus-lock-update" or "own-pet-genus-roll-update")) return state;
                var roll = CharacterGenusMessage.TryParseRoll(message);
                if (roll is null) return state with { Pets = Conflict(state.Pets) };
                var matchingRollBoards = rollBaseline.GenusBoards.Where(pair => pair.Value.CreatureType == roll.CreatureType).Take(2).ToArray();
                if (matchingRollBoards.Length != 1) return state with { Pets = Conflict(state.Pets) };
                var rollBoard = matchingRollBoards[0].Value;
                if (!rollBoard.Panels.TryGetValue(roll.Panel, out var rolledPanel)
                    || roll.Slots.Any(change => rolledPanel.Count(slot => slot.Slot == change.Slot) != 1))
                    return state with { Pets = Conflict(state.Pets) };
                var replacements = roll.Slots.ToDictionary(slot => slot.Slot);
                var rolled = rolledPanel.Select(slot => replacements.GetValueOrDefault(slot.Slot, slot)).ToImmutableArray();
                // A reply for another panel proves that the old selected-panel fact cannot
                // safely stand for the currently applied bonuses. It does not prove the new
                // panel is equipped: preview, roll target and applied panel are distinct.
                var rolledState = rollBaseline with
                { SelectedGenusPanel = rollBaseline.SelectedGenusPanel == roll.Panel ? rollBaseline.SelectedGenusPanel : null,
                    GenusBoards = rollBaseline.GenusBoards.SetItem(matchingRollBoards[0].Key,
                    rollBoard with { Level = roll.Level, Experience = roll.Experience,
                        Panels = rollBoard.Panels.SetItem(roll.Panel, rolled) }) };
                return state with { Pets = Observed(rolledState, at, sequence, "own-pet-genus-roll-update") };
            default: return state;
        }
    }

    private static NetworkCharacterCombatSnapshot RejectCombatDomain(NetworkCharacterCombatSnapshot state, ushort opcode) => opcode switch
    {
        0x3649 or 0x364A => state with { Stats = Conflict(state.Stats) },
        0xE226 => state with { DaevanionBoards = Conflict(state.DaevanionBoards) },
        0x5672 => state with { WingOwnership = Conflict(state.WingOwnership) },
        0x5676 => state with { EquippedWing = Conflict(state.EquippedWing) },
        0x5679 => state with { Titles = Conflict(state.Titles), TitleOwnership = Conflict(state.TitleOwnership) },
        0x5683 or 0x5684 => state with { Achievements = Conflict(state.Achievements) },
        0x5611 or 0x561B => state with { Equipment = Conflict(state.Equipment) },
        0x9000 => state with { Pets = Conflict(state.Pets) },
        _ => state
    };

    private static NetworkCharacterCombatSnapshot RejectAllCombat(NetworkCharacterCombatSnapshot state) => state with
    {
        Stats = Conflict(state.Stats), DaevanionBoards = Conflict(state.DaevanionBoards), Titles = Conflict(state.Titles),
        TitleOwnership = Conflict(state.TitleOwnership), WingOwnership = Conflict(state.WingOwnership), EquippedWing = Conflict(state.EquippedWing),
        Achievements = Conflict(state.Achievements), Equipment = Conflict(state.Equipment), Pets = Conflict(state.Pets)
    };
}
