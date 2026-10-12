namespace Soulcrest.Core.Network;

/// <summary>Only the own world-entry message supplies this identity; never a nearby player's nameplate.</summary>
public sealed record OwnCharacterIdentity(long CharacterId, int ServerId, int EntityKey, string Name);

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

public sealed record TeleportProgressSnapshot(IReadOnlySet<int> TeleportIds);

/// <summary>Raw quest status; 2/4 have not yet been tied to the three exploration states by game UI evidence.</summary>
public sealed record QuestProgressRecord(int QuestId, byte Status, byte Stage, IReadOnlyList<int> Counters, long Ordinal);

/// <summary>Complete consumption of the observed packet variant, not a guarantee of all quests' applicability.</summary>
public sealed record QuestProgressSnapshot(IReadOnlyList<QuestProgressRecord> Quests);
