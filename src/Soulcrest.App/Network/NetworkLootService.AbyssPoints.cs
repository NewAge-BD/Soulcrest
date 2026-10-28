using Soulcrest.App.Services;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Network;

public sealed partial class NetworkLootService
{
    private const int AbyssPointItemId = 930100003;
    private CurrencyInventoryItem? _abyssWallet;
    private OwnCharacterContext? _abyssCharacter;
    private DateTimeOffset? _abyssAt;
    private bool _abyssPointRejected;
    private bool _abyssPointModificationSeen, _abyssPointGainsQualified;
    private long _abyssPointRevision;

    private void BindAbyssBaseline(EntryCandidate? candidate)
    {
        if (candidate is not { KinaRejected: false, FarmInventoryRejected: false, KinaList: { } list } || _ownCharacter is null) return;
        var wallets = list.AllItems.Where(item => item.ItemId == AbyssPointItemId).ToArray();
        if (wallets is not [{ Container: CurrencyInventoryMessage.WalletContainer, InstanceKey: > 0, HasEquipmentData: false } wallet]) return;
        if (list.AllItems.Count(item => item.InstanceKey == wallet.InstanceKey) != 1) return;
        _abyssWallet = wallet;
        _abyssCharacter = _ownCharacter;
        _abyssAt = candidate.KinaAt;
        _abyssSequence++;
    }

    private void ProcessAbyssInventoryChanges(CurrencyInventoryChanges? changes, DateTimeOffset at)
    {
        if (!Running || _ownCharacter is not { } own || _identity is null || _identityStream is not { } stream
            || own.Generation != _progressGeneration || _abyssPointRejected) return;
        if (changes is null)
        {
            ResetAbyssWallet();
            SafeEvent.Raise(Changed, "Abyss-Punkte-Netzwerkstatus");
            return;
        }
        var records = changes.AllChanges.Where(change => change.Item.ItemId == AbyssPointItemId
            || _abyssWallet is { } wallet && change.Item.InstanceKey == wallet.InstanceKey).ToArray();
        if (records.Length == 0) return;
        // Only the observed own absolute Mod1/11/0/0 wallet variant is supported.
        // The first modification is a comparison base, including after a login wallet.
        if (records is not [{ ModificationType: 1, IncreaseReason: 11, DecreaseReason: 0, UpdateReason: 0,
                Item: { ItemId: AbyssPointItemId, Container: CurrencyInventoryMessage.WalletContainer,
                    InstanceKey: > 0, HasEquipmentData: false } } record]
            || changes.AllChanges.Count(change => change.Item.InstanceKey == record.Item.InstanceKey) != 1
            || own.ObservedAt is not { } ownAt || ownAt == default || at == default || at < ownAt
            || at > DateTimeOffset.UtcNow || _abyssAt is { } previousAt && at < previousAt
            || _abyssWallet is { } before && (!ReferenceEquals(_abyssCharacter, own)
                || before.InstanceKey != record.Item.InstanceKey || record.Item.Count < before.Count))
        {
            ResetAbyssWallet();
            SafeEvent.Raise(Changed, "Abyss-Punkte-Netzwerkstatus");
            return;
        }
        var priorModification = _abyssPointModificationSeen ? _abyssWallet : null;
        var amount = priorModification is null ? 0 : record.Item.Count - priorModification.Count;
        var captureGeneration = _generation;
        var revision = _abyssPointRevision;
        _abyssWallet = record.Item;
        _abyssCharacter = own;
        _abyssAt = at;
        _abyssPointTransport = AbyssPointTransport(stream);
        _abyssPointCarryStartedAt = null;
        _abyssPointGainsQualified = priorModification is not null;
        _abyssPointModificationSeen = true;
        var sequence = ++_abyssSequence;
        // Only a positive difference between two qualified own modifications is income.
        // This is independent of the unknown cumulative farm counter and any kill attribution.
        if (amount > 0)
        {
            var gain = new NetworkAbyssPointGain(at, sequence, own, amount);
            foreach (var subscriber in AbyssPointsGained?.GetInvocationList().Cast<Action<NetworkAbyssPointGain>>() ?? [])
            {
                if (!AbyssPointBindingMatches(own, stream, captureGeneration, revision)) break;
                try { subscriber(gain); }
                catch (Exception error) { LogFile.Error("Abyss-Punkte-Netzwerkereignis", error); }
            }
        }
        if (AbyssPointBindingMatches(own, stream, captureGeneration, revision))
            SafeEvent.Raise(Changed, "Abyss-Punkte-Netzwerkstatus");
    }

    private bool AbyssPointBindingMatches(OwnCharacterContext own, string stream, long captureGeneration, long revision) =>
        Running && _generation == captureGeneration && _progressGeneration == own.Generation
        && _abyssPointRevision == revision && !_abyssPointRejected && _identityStream == stream
        && ReferenceEquals(_ownCharacter, own) && ReferenceEquals(_abyssCharacter, own);

    private void ResetAbyssWallet()
    {
        RevokeAbyssPointContinuity();
        _abyssPointRejected = true;
        ClearAbyssWallet();
    }

    private void ResetAbyssPointContext()
    {
        _abyssPointRejected = false;
        ClearAbyssWallet();
    }

    private void ClearAbyssWallet()
    {
        _abyssWallet = null;
        _abyssCharacter = null;
        _abyssAt = null;
        _abyssPointTransport = null;
        _abyssPointCarryStartedAt = null;
        _abyssPointModificationSeen = _abyssPointGainsQualified = false;
        _abyssPointRevision++;
        _abyssSequence++;
    }
}
