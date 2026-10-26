namespace Soulcrest.Core.Network;

/// <summary>Only the own world-entry message supplies this identity; never a nearby player's nameplate.</summary>
public sealed record OwnCharacterIdentity(long CharacterId, int ServerId, int EntityKey, string Name)
{
    /// <summary>Native board class key; unknown/future PC types do not invalidate the own identity.</summary>
    public string? ClassId { get; init; }
}

/// <summary>Own map metadata returned only after complete MyCharAppear validation; no raw map key or coordinates.</summary>
public sealed record OwnWorldObservation(OwnCharacterIdentity Identity, int? MapDataId)
{
    public long? CombatScore { get; init; }
    public int? GearScore { get; init; }
}

/// <summary>Complete preparation shape, provisional map metadata only.</summary>
public sealed record WorldEntryPreparation(int? MapDataId);

/// <summary>Current own identity and its transient network generation; never a persisted character choice.</summary>
public sealed record OwnCharacterContext(OwnCharacterIdentity Identity, long Generation)
{
    /// <summary>Own-entry scores, refreshed by any complete load-screen entry; never part of identity equality.</summary>
    public long? CombatScore { get; init; }
    public int? GearScore { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }
}

/// <summary>Untrusted login-batch candidate. Only a later full own message can confirm both fields and supply the server.</summary>
public sealed record OwnEntryCandidate(long CharacterId, int EntityKey);

/// <summary>Transient capture provenance. Sequence ordering belongs to one generation and own identity.</summary>
public sealed record NetworkProgressContext(OwnCharacterIdentity Identity, long Generation, long Sequence, DateTimeOffset At)
{
    public long CharacterId => Identity.CharacterId;
    public int ServerId => Identity.ServerId;
    public string Name => Identity.Name;
}

/// <summary>
/// One completely decoded ride-init packet. Both dictionaries use the inner pet number, not the
/// acquired-map's storage key. Souls are the available, unspent balance, separate from the level.
/// Absent keys mean zero only in this complete snapshot, never in a delta or a rejected packet.
/// </summary>
public sealed record PetProgressSnapshot(IReadOnlyDictionary<int, int> Levels, IReadOnlyDictionary<int, int> AvailableSouls);

/// <summary>Filter EntityKey against the current own identity before applying this event.</summary>
public sealed record PetAcquisition(int EntityKey, int PetNumber, int Level);

/// <summary>Explicit upgrade result without an entity field; only an own-bound stream may apply a successful result.</summary>
public sealed record PetUpgradeResult(ushort Result, int PetNumber, int Level);

public sealed record TeleportProgressSnapshot(IReadOnlySet<int> TeleportIds);

/// <summary>Raw quest fields. Only exact catalogue joins and own-bound fresh observations may supply positive completion evidence.</summary>
public sealed record QuestProgressRecord(int QuestId, byte Status, byte Stage, IReadOnlyList<int> Counters, long Ordinal);

/// <summary>Complete consumption of the observed packet variant, not a guarantee of all quests' applicability.</summary>
public sealed record QuestProgressSnapshot(IReadOnlyList<QuestProgressRecord> Quests);
