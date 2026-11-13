using Soulcrest.App.Services;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Network;

public sealed partial class NetworkLootService
{
    private CurrencyInventoryItem? _abyssBoundKinaWallet;
    private OwnCharacterContext? _abyssBoundKinaCharacter;
    private DateTimeOffset? _abyssBoundKinaAt;
    private bool _abyssBoundKinaRejected, _abyssBoundKinaGainsQualified;

    // User-qualified 2026-11-13: complete Abyss Own, same-wallet Mod1/11/0/0 pair, +421.
    // Capture boundaries and the first unknown baseline never create catch-up income.
    private void ProcessAbyssBoundKinaInventoryChanges(CurrencyInventoryChanges? changes, DateTimeOffset at)
    {
        if (_ownCharacter is not { } own || _abyssBoundKinaRejected || _kinaFormatRejected) return;
        var world = WorldContext.Snapshot;
        if (world is not { Status: WorldContextStatus.Confirmed, Category: "Abyss", ObservedAt: { } worldAt }
            || world.BindingGeneration != own.Generation) return;
        var previous = _abyssBoundKinaWallet ?? (ReferenceEquals(_kinaCharacter, own) ? _boundKina : null);
        // A login wallet is already an identity binding, even before the first reward.
        // Reusing that key for another item must revoke this comparison, not leave a stale zero.
        var records = changes?.AllChanges.Where(change => change.Item.ItemId == CurrencyInventoryMessage.BoundKinaItemId
            || previous is { } wallet && change.Item.InstanceKey == wallet.InstanceKey).ToArray();
        if (records is { Length: 0 }) return;
        // A newly created wallet is only an absolute baseline. The controlled reward pair
        // is not permission to treat an unknown prior balance as zero.
        if (changes is null || records is not [{ ModificationType: 0 or 1,
                Item: { ItemId: CurrencyInventoryMessage.BoundKinaItemId, Container: CurrencyInventoryMessage.WalletContainer,
                    InstanceKey: > 0, HasEquipmentData: false } } record]
            || !(record is { IncreaseReason: 11, DecreaseReason: 0, UpdateReason: 0 }
                || IsAbyssBoundKinaWalletDecrease(record, previous))
            || changes.AllChanges.Count(change => change.Item.InstanceKey == record.Item.InstanceKey) != 1
            || record.ModificationType == 0 && previous is not null
            || own.ObservedAt is not { } ownAt || ownAt == default || at == default || at < ownAt || at < worldAt
            || at > DateTimeOffset.UtcNow || _abyssBoundKinaAt is { } previousAt && at < previousAt
            || _abyssBoundKinaWallet is not null && !ReferenceEquals(_abyssBoundKinaCharacter, own)
            || previous is not null && (previous.InstanceKey != record.Item.InstanceKey
                || record.Item.Count < previous.Count && !IsAbyssBoundKinaWalletDecrease(record, previous)))
        {
            ResetAbyssBoundKinaContext(rejected: true);
            SafeEvent.Raise(Changed, "Abyss-Kina-Netzwerkstatus");
            return;
        }
        _abyssBoundKinaWallet = record.Item;
        _abyssBoundKinaCharacter = own;
        _abyssBoundKinaAt = at;
        _abyssBoundKinaGainsQualified = previous is not null;
        _abyssSequence++;
        // Reuse the sole Kina publisher. Priming never publishes the baseline as income.
        if (!_kinaFormatRejected && _boundKina is null)
        {
            if (!ReferenceEquals(_kinaCharacter, own)) _unboundKina = null;
            _kinaCharacter = own;
            _boundKina = record.Item;
            _kinaBaselineAt = at;
        }
    }

    // The same own capture qualifies only this absolute lower-wallet variant. It changes
    // the comparison base, never session income or a farm budget; no cause is inferred.
    private bool IsAbyssBoundKinaWalletDecrease(CurrencyInventoryChange record, CurrencyInventoryItem? previous) =>
        _abyssBoundKinaGainsQualified && previous is not null
        && record is { ModificationType: 1, IncreaseReason: 0, DecreaseReason: 9, UpdateReason: 0 }
        && record.Item.Count <= previous.Count;

    private void ResetAbyssBoundKinaContext(bool rejected = false)
    {
        _abyssBoundKinaWallet = null;
        _abyssBoundKinaCharacter = null;
        _abyssBoundKinaAt = null;
        _abyssBoundKinaGainsQualified = false;
        _abyssBoundKinaRejected = rejected;
        _abyssSequence++;
    }
}
