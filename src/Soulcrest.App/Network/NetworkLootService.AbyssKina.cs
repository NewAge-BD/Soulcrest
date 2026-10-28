using Soulcrest.App.Services;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Network;

public sealed partial class NetworkLootService
{
    private CurrencyInventoryItem? _abyssKinaWallet;
    private OwnCharacterContext? _abyssKinaCharacter;
    private DateTimeOffset? _abyssKinaAt;
    private bool _abyssKinaRejected, _abyssKinaGainsQualified;

    private void ProcessAbyssKinaInventoryChanges(CurrencyInventoryChanges? changes, DateTimeOffset at)
    {
        if (_ownCharacter is not { } own || _abyssKinaRejected || _kinaFormatRejected) return;
        var world = WorldContext.Snapshot;
        if (world is not { Status: WorldContextStatus.Confirmed, Category: "Abyss", ObservedAt: { } worldAt }
            || world.BindingGeneration != own.Generation) return;
        var records = changes?.AllChanges.Where(change => change.Item.ItemId == CurrencyInventoryMessage.UnboundKinaItemId
            || _abyssKinaWallet is { } wallet && change.Item.InstanceKey == wallet.InstanceKey).ToArray();
        if (records is { Length: 0 }) return;
        if (changes is null || records is not [{ ModificationType: 1, IncreaseReason: 11, DecreaseReason: 0, UpdateReason: 0,
                Item: { ItemId: CurrencyInventoryMessage.UnboundKinaItemId, Container: CurrencyInventoryMessage.WalletContainer,
                    InstanceKey: > 0, HasEquipmentData: false } } record]
            || changes.AllChanges.Count(change => change.Item.InstanceKey == record.Item.InstanceKey) != 1
            || own.ObservedAt is not { } ownAt || ownAt == default || at == default || at < ownAt || at < worldAt
            || at > DateTimeOffset.UtcNow || _abyssKinaAt is { } previousAt && at < previousAt
            || _abyssKinaWallet is { } before && (!ReferenceEquals(_abyssKinaCharacter, own)
                || before.InstanceKey != record.Item.InstanceKey || record.Item.Count < before.Count)
            || _unboundKina is { } genericBefore && (!ReferenceEquals(_kinaCharacter, own)
                || genericBefore.InstanceKey != record.Item.InstanceKey || record.Item.Count < genericBefore.Count))
        {
            ResetAbyssKinaContext(rejected: true);
            SafeEvent.Raise(Changed, "Abyss-Kina-Netzwerkstatus");
            return;
        }
        var hasPrior = _abyssKinaWallet is not null;
        _abyssKinaWallet = record.Item;
        _abyssKinaCharacter = own;
        _abyssKinaAt = at;
        _abyssKinaGainsQualified = hasPrior;
        _abyssSequence++;
        // The controlled same-own pair proves a usable comparison base after an internal load.
        // Prime the existing producer only once; it remains the sole publisher of Kina income.
        if (!hasPrior && !_kinaFormatRejected && _unboundKina is null)
        {
            if (!ReferenceEquals(_kinaCharacter, own)) _boundKina = null;
            _kinaCharacter = own;
            _unboundKina = record.Item;
            _kinaBaselineAt = at;
        }
    }

    private void ResetAbyssKinaContext(bool rejected = false)
    {
        _abyssKinaWallet = null;
        _abyssKinaCharacter = null;
        _abyssKinaAt = null;
        _abyssKinaGainsQualified = false;
        _abyssKinaRejected = rejected;
        _abyssSequence++;
    }
}
