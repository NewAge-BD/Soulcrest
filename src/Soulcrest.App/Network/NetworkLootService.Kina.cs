using Soulcrest.App.Services;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Network;

public sealed partial class NetworkLootService
{
    private const int MaximumPendingKinaGains = 64;
    private CurrencyInventoryItem? _unboundKina, _boundKina;
    private OwnCharacterContext? _kinaCharacter;
    private DateTimeOffset? _kinaBaselineAt;
    private bool _kinaFormatRejected, _dispatchingKina;
    private long _kinaSequence, _kinaMessageRevision;
    private readonly Queue<(string Stream, NetworkKinaGain Gain)> _kinaDispatch = [];

    public event Action<NetworkKinaGain>? KinaGained;

    public NetworkKinaSnapshot KinaSnapshot
    {
        get
        {
            lock (_gate)
            {
                var bound = Running && _kinaCharacter is not null && ReferenceEquals(_kinaCharacter, _ownCharacter);
                var unbound = bound ? _unboundKina?.Count : null;
                var status = !Running ? "Netzwerk-Tracking ist ausgeschaltet."
                    : _ownCharacter is null ? "Warte auf die eigene Charaktererkennung."
                    : _kinaFormatRejected ? "Kina-Netzwerkbeleg nicht unterstützt. Bitte erneut über die Charakterauswahl einloggen."
                    : unbound is null ? "Warte auf den Kina-Ausgangsstand. Bitte über die Charakterauswahl einloggen."
                    : "Kina-Ausgangsstand über das Netzwerk erfasst.";
                return new(unbound, bound ? _boundKina?.Count : null,
                    bound ? _kinaBaselineAt : null, _kinaFormatRejected, status);
            }
        }
    }

    // User-qualified 2026-10-26: only explicit records from the observed login-list variant.
    // Lists are tentative until Entry -> Preparation -> matching complete Own. No absent=zero,
    // no inventory-completeness/result inference, and no income from a login or a world entry.
    private void ProcessKinaMessage(string stream, ushort opcode, byte[] message, DateTimeOffset at)
    {
        if (!Running) return;
        _kinaMessageRevision++;
        if (_ownCharacter is null)
        {
            if (_entryCandidate is not { } candidate || candidate.Stream != stream) return;
            if (opcode == 0x5611)
            {
                var list = CurrencyInventoryMessage.TryParseList(message);
                if (candidate.Prepared || candidate.KinaSeen || list is null)
                {
                    candidate.KinaRejected = true;
                    candidate.KinaList = null;
                    return;
                }
                candidate.KinaSeen = true;
                candidate.KinaList = list;
                candidate.KinaAt = at;
            }
            else
            {
                var changes = CurrencyInventoryMessage.TryParseChanges(message);
                if (changes is null || changes.AllChanges.Count != 0) candidate.FarmInventoryRejected = true;
                // Do not apply a stale list after an intervening own-wallet change. Non-wallet
                // item traffic is validated but has no bearing on the pending Kina baseline.
                if (changes is null || changes.Items.Count != 0)
                {
                    candidate.KinaRejected = true;
                    candidate.KinaList = null;
                }
            }
            return;
        }
        if (_identityStream != stream) return;
        if (opcode == 0x5611)
        {
            // Unanchored or late lists are not a newly qualified login transaction.
            ResetKinaContext();
            ResetFarmDropContext();
            SafeEvent.Raise(Changed, "Kina-Netzwerkstatus");
            return;
        }
        var update = CurrencyInventoryMessage.TryParseChanges(message);
        var ownBeforeFarm = _ownCharacter;
        var revisionBeforeFarm = _kinaMessageRevision;
        ProcessFarmInventoryChanges(stream, update, at);
        if (!ReferenceEquals(ownBeforeFarm, _ownCharacter) || revisionBeforeFarm != _kinaMessageRevision) return;
        if (update is null)
        {
            ResetKinaContext();
            _kinaFormatRejected = true;
            SafeEvent.Raise(Changed, "Kina-Netzwerkstatus");
            return;
        }
        if (!ReferenceEquals(_kinaCharacter, _ownCharacter)) return;
        var character = _ownCharacter;
        var gains = new List<NetworkKinaGain>(1);
        foreach (var change in update.Items)
        {
            var isUnbound = change.Item.ItemId == CurrencyInventoryMessage.UnboundKinaItemId;
            var previous = isUnbound ? _unboundKina : _boundKina;
            if (change.Item.Container != CurrencyInventoryMessage.WalletContainer)
            {
                if (previous?.InstanceKey == change.Item.InstanceKey)
                {
                    if (isUnbound) _unboundKina = null;
                    else _boundKina = null;
                }
                continue;
            }
            // Wallet instance identifiers change on character login. A new/unexpected key is
            // not a gain and cannot reinitialize an unknown balance from an arbitrary update.
            if (previous is null) continue;
            var matches = previous.InstanceKey == change.Item.InstanceKey && previous.Container == change.Item.Container;
            var current = matches && change.ModificationType != 2 ? change.Item : null;
            if (isUnbound) _unboundKina = current;
            else _boundKina = current;
            // Other valid reasons, spending and transfers update the absolute comparison base
            // without producing farm income. Bound reward variants are not live-qualified yet.
            if (isUnbound && current is not null && change.ModificationType == 1
                && change.IncreaseReason == 11 && change.DecreaseReason == 0 && change.UpdateReason == 0
                && current.Count > previous.Count)
                gains.Add(new(at, ++_kinaSequence, character, current.Count - previous.Count));
        }
        foreach (var gain in gains)
        {
            if (_kinaDispatch.Count >= MaximumPendingKinaGains)
            {
                ResetKinaContext();
                _kinaFormatRejected = true;
                break;
            }
            _kinaDispatch.Enqueue((stream, gain));
        }
        DispatchKinaGains();
        SafeEvent.Raise(Changed, "Kina-Netzwerkstatus");
    }

    private void BindKinaBaseline(EntryCandidate? candidate)
    {
        if (candidate?.KinaRejected == true) { _kinaFormatRejected = true; return; }
        if (candidate?.KinaList is not { } list || _ownCharacter is null) return;
        _kinaCharacter = _ownCharacter;
        _kinaBaselineAt = candidate.KinaAt;
        foreach (var item in list.Items)
            if (item.ItemId == CurrencyInventoryMessage.UnboundKinaItemId) _unboundKina = item;
            else if (item.ItemId == CurrencyInventoryMessage.BoundKinaItemId) _boundKina = item;
    }

    private void DispatchKinaGains()
    {
        if (_dispatchingKina) return;
        _dispatchingKina = true;
        try
        {
            while (_kinaDispatch.TryDequeue(out var queued))
                foreach (var subscriber in KinaGained?.GetInvocationList().Cast<Action<NetworkKinaGain>>() ?? [])
                {
                    if (!Running || !ReferenceEquals(queued.Gain.Character, _ownCharacter)
                        || !ReferenceEquals(_kinaCharacter, _ownCharacter) || _identityStream != queued.Stream) break;
                    try { subscriber(queued.Gain); }
                    catch (Exception error) { LogFile.Error("Kina-Netzwerkereignis", error); }
                }
        }
        finally { _dispatchingKina = false; }
    }

    private void ResetKinaContext()
    {
        _unboundKina = _boundKina = null;
        _kinaCharacter = null;
        _kinaBaselineAt = null;
        _kinaFormatRejected = false;
        _kinaSequence = 0;
        _kinaDispatch.Clear();
    }
}
