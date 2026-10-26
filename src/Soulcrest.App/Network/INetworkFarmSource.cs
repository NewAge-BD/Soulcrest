using Soulcrest.Core.Network;

namespace Soulcrest.App.Network;

/// <summary>Own passive monster income and canonical drops from the shared capture.</summary>
public interface INetworkFarmSource
{
    bool Running { get; }
    OwnCharacterContext? OwnCharacter { get; }
    NetworkDailyMonsterKinaSnapshot DailyMonsterKinaSnapshot { get; }
    event Action? Changed;
    event Action? OwnCharacterChanged;
    event Action<NetworkKinaGain>? MonsterKinaGained;
    event Action<NetworkFarmDrop>? FarmDropGained;
}

public enum NetworkFarmDropKind { Item, SoulCrystal, PetSoul, UniversalSoulCrystal }

/// <summary>DataId is an item ID, native crystal type, or pet number; universal crystals use only 930500001. Never an inventory/entity key.</summary>
public sealed record NetworkFarmDrop(DateTimeOffset At, long Sequence, OwnCharacterContext Character,
    int DataId, long Quantity, NetworkFarmDropKind Kind, bool InInstance)
{
    // Own-build Item row 9323: CurrencyCategory::SoulCrystalAll, not five genus balances.
    public const int UniversalSoulCrystalItemId = 930500001;
}
