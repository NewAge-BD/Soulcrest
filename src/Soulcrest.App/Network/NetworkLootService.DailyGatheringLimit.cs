using Soulcrest.App.Services;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Network;

public sealed record NetworkDailyGatheringLimitSnapshot(int? Count, DateTimeOffset At);

public sealed partial class NetworkLootService
{
    // The server also sends FFA7, Prepare and full Own on one stream without a 3620 prefix.
    private sealed record UnprefixedGatheringEntry(string Stream, DateTimeOffset StartedAt,
        NetworkDailyGatheringLimitSnapshot? Initial, DateTimeOffset? PreparedAt = null,
        (long CharacterId, int ServerId)? ConfirmedCharacter = null);

    private UnprefixedGatheringEntry? _unprefixedGatheringEntry;
    private NetworkDailyGatheringLimitSnapshot? _dailyGatheringLimit;
    private long _dailyGatheringMessageRevision;

    public NetworkDailyGatheringLimitSnapshot? DailyGatheringLimit
    {
        get { lock (_gate) return Running && _ownCharacter is not null ? _dailyGatheringLimit : null; }
    }

    public event Action? DailyGatheringLimitChanged;

    private void ProcessDailyGatheringLimitMessage(string stream, ushort opcode, byte[] message, DateTimeOffset at)
    {
        if (!Running) return;
        _dailyGatheringMessageRevision++;
        if (_ownCharacter is null)
        {
            if (_entryCandidate is not { } candidate)
            {
                BufferUnprefixedGatheringEntry(stream, opcode, message, at);
                return;
            }
            if (candidate.GatheringLimitRejected || candidate.Stream != stream || at < candidate.StartedAt) return;
            if (!candidate.Prepared && opcode == DailyGatheringLimitMessage.InitialOpcode
                && candidate.GatheringLimit is null && DailyGatheringLimitMessage.TryParse(message) is { } initial)
                candidate.GatheringLimit = new(initial, at);
            else
            {
                candidate.GatheringLimit = null;
                candidate.GatheringLimitRejected = true;
            }
            return;
        }

        if (_identityStream != stream || opcode != DailyGatheringLimitMessage.UpdateOpcode
            || at < _ownCharacter.ObservedAt || at < _dailyGatheringLimit?.At) return;
        _dailyGatheringLimit = new(DailyGatheringLimitMessage.TryParse(message), at);
        SafeEvent.Raise(DailyGatheringLimitChanged, "Sammel-Tagesstand");
    }

    private void BufferUnprefixedGatheringEntry(string stream, ushort opcode, byte[] message, DateTimeOffset at)
    {
        if (_unprefixedGatheringEntry is { } pending && !FreshGatheringEntry(pending, at))
            _unprefixedGatheringEntry = null;
        if (_unprefixedGatheringEntry is { } existing)
        {
            // Any repeated initial or unexpected update on the same stream invalidates the transaction.
            if (existing.Stream == stream) _unprefixedGatheringEntry = existing with { Initial = null };
            return;
        }
        if (opcode == DailyGatheringLimitMessage.InitialOpcode)
            _unprefixedGatheringEntry = new(stream, at, DailyGatheringLimitMessage.TryParse(message) is { } count
                ? new(count, at) : null);
    }

    private UnprefixedGatheringEntry? PrepareUnprefixedGatheringEntry(string stream, DateTimeOffset at)
    {
        if (_unprefixedGatheringEntry is { Initial: not null, PreparedAt: null } pending
            && pending.Stream == stream && FreshGatheringEntry(pending, at))
            return pending with { PreparedAt = at };

        // A second Prepare/Own can follow the initial login on the same transport without resending FFA7.
        // Only a fresh entry baseline can cross it; a recent gathering update is a different source.
        if (_identityStream == stream && _ownCharacter is { ObservedAt: { } ownAt } own
            && _dailyGatheringLimit is { Count: not null } initial && initial.At <= ownAt && ownAt <= at
            && at - initial.At <= EntryCandidateLifetime)
            return new(stream, initial.At, initial, at, (own.Identity.CharacterId, own.Identity.ServerId));

        return null;
    }

    private NetworkDailyGatheringLimitSnapshot? ReadUnprefixedGatheringBaseline(string stream,
        OwnCharacterIdentity identity, DateTimeOffset at) =>
        _unprefixedGatheringEntry is { PreparedAt: { } prepared } pending && pending.Stream == stream
            && prepared <= at && FreshGatheringEntry(pending, at)
            && (pending.ConfirmedCharacter is null || pending.ConfirmedCharacter == (identity.CharacterId, identity.ServerId))
            ? pending.Initial : null;

    private static bool FreshGatheringEntry(UnprefixedGatheringEntry pending, DateTimeOffset at) =>
        at >= pending.StartedAt && at - pending.StartedAt <= EntryCandidateLifetime;

    private void InvalidateUnprefixedGatheringEntry(string stream)
    {
        // Entry resets can invoke callbacks while the pending value is held by the caller.
        _dailyGatheringMessageRevision++;
        if (stream == "*" || _unprefixedGatheringEntry?.Stream == stream)
            _unprefixedGatheringEntry = null;
    }
}
