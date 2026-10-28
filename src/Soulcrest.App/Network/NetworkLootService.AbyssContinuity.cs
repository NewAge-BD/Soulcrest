using Soulcrest.Core.Network;

namespace Soulcrest.App.Network;

public sealed partial class NetworkLootService
{
    private enum AbyssPointTransition { Candidate, Prepared, Rebinding }

    // A one-use RAM comparison base, never a persisted balance or an entry identity.
    // Only an uninterrupted, already observed transport may carry it across a new Own.
    private sealed class AbyssPointContinuity(OwnCharacterIdentity identity, string stream,
        GameMessageStream transport, long captureGeneration, CurrencyInventoryItem wallet,
        DateTimeOffset factAt, DateTimeOffset startedAt, bool qualified, AbyssPointTransition stage)
    {
        public OwnCharacterIdentity Identity { get; } = identity;
        public string Stream { get; } = stream;
        public GameMessageStream Transport { get; } = transport;
        public long CaptureGeneration { get; } = captureGeneration;
        public CurrencyInventoryItem Wallet { get; } = wallet;
        public DateTimeOffset FactAt { get; } = factAt;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public DateTimeOffset LatestAt { get; set; } = startedAt;
        public bool Qualified { get; } = qualified;
        public AbyssPointTransition Stage { get; set; } = stage;
        public int? CandidateEntity { get; set; }
        public bool Resetting { get; set; }
    }

    private AbyssPointContinuity? _abyssPointContinuity;
    private GameMessageStream? _abyssPointTransport;
    private DateTimeOffset? _abyssPointCarryStartedAt;

    private GameMessageStream? AbyssPointTransport(string stream) =>
        _streams.FirstOrDefault(pair => pair.Key.StreamKey == stream).Value.Messages;

    private void RevokeAbyssPointContinuity() => _abyssPointContinuity = null;

    private bool AbyssPointContinuityMatches(AbyssPointContinuity continuity, string stream, DateTimeOffset at) =>
        Running && continuity.Stream == stream && continuity.CaptureGeneration == _generation
        && ReferenceEquals(continuity.Transport, AbyssPointTransport(stream))
        && at != default && at >= continuity.LatestAt && at <= DateTimeOffset.UtcNow
        && at - continuity.StartedAt <= EntryCandidateLifetime;

    private void ExpireAbyssPointContinuity(string stream, DateTimeOffset at)
    {
        if (_abyssPointContinuity is { } continuity && continuity.Stream == stream
            && !AbyssPointContinuityMatches(continuity, stream, at))
            RevokeAbyssPointContinuity();
    }

    private AbyssPointContinuity? CaptureAbyssPointContinuity(string stream, DateTimeOffset at, AbyssPointTransition stage)
    {
        // An in-flight callback may have queued older inventory that the Own reset discards.
        // Such a dispatch is deliberately not a continuity proof.
        if (_dispatchingAbyssObservations || !Running || _ownCharacter is not { } own
            || _identityStream != stream || own.Generation != _progressGeneration
            || !ReferenceEquals(_abyssCharacter, own) || _abyssPointRejected || !_abyssPointModificationSeen
            || _abyssWallet is not { Count: >= 0, InstanceKey: > 0, Container: CurrencyInventoryMessage.WalletContainer,
                HasEquipmentData: false } wallet || _abyssAt is not { } factAt || factAt == default
            || own.ObservedAt is not { } ownAt || ownAt == default
            || at == default || at < factAt || at < ownAt || at > DateTimeOffset.UtcNow
            || _abyssPointCarryStartedAt is { } started && (at < started || at - started > EntryCandidateLifetime)
            || _abyssPointTransport is not { } transport || !ReferenceEquals(transport, AbyssPointTransport(stream)))
        {
            RevokeAbyssPointContinuity();
            return null;
        }
        return _abyssPointContinuity = new(own.Identity, stream, transport, _generation, wallet,
            factAt, _abyssPointCarryStartedAt ?? at, _abyssPointGainsQualified, stage) { LatestAt = at };
    }

    private AbyssPointContinuity? BeginAbyssPointCandidate(string stream, OwnEntryCandidate? candidate, DateTimeOffset at)
    {
        if (candidate is null || _abyssPointContinuity is not null || _ownCharacter is not { } own
            || candidate.CharacterId != own.Identity.CharacterId)
        {
            RevokeAbyssPointContinuity();
            return null;
        }
        var continuity = CaptureAbyssPointContinuity(stream, at, AbyssPointTransition.Candidate);
        if (continuity is not null) continuity.CandidateEntity = candidate.EntityKey;
        return continuity;
    }

    private AbyssPointContinuity? PrepareAbyssPointContinuity(string stream, DateTimeOffset at)
    {
        if (_abyssPointContinuity is not { } continuity)
            return CaptureAbyssPointContinuity(stream, at, AbyssPointTransition.Prepared);
        if (continuity.Resetting || continuity.Stage != AbyssPointTransition.Candidate || !AbyssPointContinuityMatches(continuity, stream, at)
            || _entryCandidate is not { Prepared: false } candidate || candidate.Stream != stream
            || candidate.CharacterId != continuity.Identity.CharacterId || candidate.EntityKey != continuity.CandidateEntity)
        {
            RevokeAbyssPointContinuity();
            return null;
        }
        continuity.Stage = AbyssPointTransition.Prepared;
        continuity.LatestAt = at;
        return continuity;
    }

    private AbyssPointContinuity? BeginAbyssPointRebind(string stream, OwnCharacterIdentity identity, DateTimeOffset at)
    {
        var continuity = _abyssPointContinuity ?? CaptureAbyssPointContinuity(stream, at, AbyssPointTransition.Rebinding);
        if (continuity is null) return null;
        if (continuity.Resetting || !AbyssPointContinuityMatches(continuity, stream, at)
            || continuity.Identity.CharacterId != identity.CharacterId || continuity.Identity.ServerId != identity.ServerId
            || continuity.Stage == AbyssPointTransition.Candidate
            || continuity.Stage == AbyssPointTransition.Rebinding && _ownCharacter is null
            || continuity.CandidateEntity is { } entity && (identity.EntityKey != entity
                || _entryCandidate is not { Prepared: true }))
        {
            RevokeAbyssPointContinuity();
            return null;
        }
        continuity.Stage = AbyssPointTransition.Rebinding;
        continuity.LatestAt = at;
        return continuity;
    }

    private void BindAbyssPointContinuity(AbyssPointContinuity? continuity, string stream, DateTimeOffset at)
    {
        if (continuity is null || !ReferenceEquals(_abyssPointContinuity, continuity)) return;
        // Consume before any later service callback. Rebinding is not an acquisition fact.
        RevokeAbyssPointContinuity();
        if (!AbyssPointContinuityMatches(continuity, stream, at) || _ownCharacter is not { } own
            || continuity.Identity.CharacterId != own.Identity.CharacterId || continuity.Identity.ServerId != own.Identity.ServerId)
            return;
        _abyssWallet = continuity.Wallet;
        _abyssCharacter = own;
        _abyssAt = continuity.FactAt;
        _abyssPointTransport = continuity.Transport;
        // Several Owns without a fresh inventory fact share the first transition's deadline.
        _abyssPointCarryStartedAt = continuity.StartedAt;
        _abyssPointModificationSeen = true;
        _abyssPointGainsQualified = continuity.Qualified;
        _abyssSequence++;
    }

    private static void CompleteAbyssPointTransitionReset(AbyssPointContinuity? continuity)
    {
        if (continuity is not null) continuity.Resetting = false;
    }

    private void ObserveSuspendedAbyssPointInventory(string stream, ushort opcode, byte[] message, DateTimeOffset at)
    {
        if (_abyssPointContinuity is not { } continuity || continuity.Stream != stream) return;
        if (!AbyssPointContinuityMatches(continuity, stream, at) || opcode == 0x5611)
        {
            RevokeAbyssPointContinuity();
            return;
        }
        var changes = CurrencyInventoryMessage.TryParseChanges(message);
        if (changes is null || changes.AllChanges.Any(change => change.Item.ItemId == AbyssPointItemId
                || change.Item.InstanceKey == continuity.Wallet.InstanceKey))
            RevokeAbyssPointContinuity();
        else continuity.LatestAt = at;
    }
}
