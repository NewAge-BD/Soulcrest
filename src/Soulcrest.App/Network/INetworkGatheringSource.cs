using System.Collections.Immutable;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Network;

/// <summary>Verified own gathering outcomes only; sharing this source never starts another capture.</summary>
public interface INetworkGatheringSource
{
    bool Running { get; }
    OwnCharacterContext? OwnCharacter { get; }
    bool GatheringVerified { get; }
    string GatheringStatus { get; }
    string? GatheringWarning => null;
    long GatheringWarningVersion => 0;
    event Action? Changed;
    event Action? OwnCharacterChanged;
    event Action<NetworkGathering>? GatheringCompleted;
}

public sealed record NetworkGatheringItem(int ItemId, long Quantity);

public enum NetworkGatheringOutcome { Harvested, Failed }

/// <summary>Transient native environment facts. Not a stable map-node ID; never persist its keys.</summary>
public sealed record NetworkGatheringObject(int DataId, int OriginSpawnKey, int? SpawnUniqueKey);

/// <summary>
/// One verified own completed gathering attempt. Failed attempts carry no items. Sequence belongs
/// to the source, Character to the qualified own generation. Rewards are not inventory balances
/// and must never be added to their inventory echo.
/// TargetKey/Object are RAM-only join facts, not persisted session or catalogue identifiers.
/// </summary>
public sealed record NetworkGathering(DateTimeOffset At, long Sequence, OwnCharacterContext Character,
    int TargetKey, ImmutableArray<NetworkGatheringItem> Items)
{
    public NetworkGatheringOutcome Outcome { get; init; } = NetworkGatheringOutcome.Harvested;
    /// <summary>Confirmed own instance context at completion; kept for separate dungeon loot recording.</summary>
    public bool InInstance { get; init; }
    public NetworkGatheringObject? Object { get; init; }
}
