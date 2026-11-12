using System.Collections.Immutable;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Network;

public interface INetworkCharacterCombatSource
{
    bool Running { get; }
    NetworkCharacterCombatSnapshot CombatSnapshot { get; }
    event Action? Changed;
    event Action? OwnCharacterChanged;
}

public enum NetworkCombatSourceStatus { Unknown, Partial, Complete, Stale, Conflict }

/// <summary>One directly observed own resource maximum; independent of raw stat components.</summary>
public sealed record NetworkCharacterResourceMaximum(long Value);

/// <summary>Completeness covers only the explicitly named domain and scope. Missing keys have no implied zero.</summary>
public sealed record NetworkCombatDomain<T>(T? Value, NetworkCombatSourceStatus Status, bool IsFresh,
    DateTimeOffset? ObservedAt, long Sequence, string Source) where T : class
{
    public static NetworkCombatDomain<T> Unknown { get; } = new(null, NetworkCombatSourceStatus.Unknown, false, null, 0, "unknown");
}

/// <summary>Immutable, transient own evidence; native identity remains inside Character and must never be persisted.</summary>
public sealed record NetworkCharacterCombatSnapshot
{
    public NetworkCombatDomain<ImmutableDictionary<byte, CharacterCraftingProficiency>> CraftingProficiencies { get; init; } =
        NetworkCombatDomain<ImmutableDictionary<byte, CharacterCraftingProficiency>>.Unknown;
    public OwnCharacterContext? Character { get; init; }
    public string? ClassId { get; init; }
    public int? CharacterLevel { get; init; }
    public int? CombatLevel { get; init; }
    public int? ConquerorLevel { get; init; }
    public long? CurrentCombatScore { get; init; }
    public long Generation { get; init; }
    public long Sequence { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }
    public NetworkCombatDomain<ImmutableDictionary<ushort, int>> Stats { get; init; } = NetworkCombatDomain<ImmutableDictionary<ushort, int>>.Unknown;
    public NetworkCombatDomain<NetworkCharacterResourceMaximum> HpMaximum { get; init; } = NetworkCombatDomain<NetworkCharacterResourceMaximum>.Unknown;
    public NetworkCombatDomain<NetworkCharacterResourceMaximum> MpMaximum { get; init; } = NetworkCombatDomain<NetworkCharacterResourceMaximum>.Unknown;
    public NetworkCombatDomain<CharacterDaevanionState> DaevanionBoards { get; init; } = NetworkCombatDomain<CharacterDaevanionState>.Unknown;
    /// <summary>Ownership bound to Character in this capture; equipped slots stay in Titles.</summary>
    public NetworkCombatDomain<ImmutableHashSet<int>> TitleOwnership { get; init; } = NetworkCombatDomain<ImmutableHashSet<int>>.Unknown;
    public NetworkCombatDomain<CharacterTitleState> Titles { get; init; } = NetworkCombatDomain<CharacterTitleState>.Unknown;
    public NetworkCombatDomain<CharacterWingOwnership> WingOwnership { get; init; } = NetworkCombatDomain<CharacterWingOwnership>.Unknown;
    public NetworkCombatDomain<CharacterWingEquipment> EquippedWing { get; init; } = NetworkCombatDomain<CharacterWingEquipment>.Unknown;
    public NetworkCombatDomain<CharacterAchievementState> Achievements { get; init; } = NetworkCombatDomain<CharacterAchievementState>.Unknown;
    public NetworkCombatDomain<CharacterEquipmentState> Equipment { get; init; } = NetworkCombatDomain<CharacterEquipmentState>.Unknown;
    public NetworkCombatDomain<PetProgressSnapshot> Pets { get; init; } = NetworkCombatDomain<PetProgressSnapshot>.Unknown;
    public NetworkCombatDomain<CharacterDaevanionPointState> DaevanionPoints { get; init; } = NetworkCombatDomain<CharacterDaevanionPointState>.Unknown;
    public NetworkCombatDomain<CharacterAbnormalState> Abnormals { get; init; } = NetworkCombatDomain<CharacterAbnormalState>.Unknown;
    public static NetworkCharacterCombatSnapshot Empty { get; } = new();
    public override string ToString() => $"NetworkCharacterCombatSnapshot {{ ClassId = {ClassId}, Generation = {Generation}, Sequence = {Sequence} }}";
}
