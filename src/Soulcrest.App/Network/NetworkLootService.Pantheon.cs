using Soulcrest.Core.Network;

namespace Soulcrest.App.Network;

public sealed partial class NetworkLootService : INetworkPantheonSource
{
    private NetworkPantheonSnapshot _pantheonSnapshot = NetworkPantheonSnapshot.Empty;
    private (long CharacterId, int ServerId)? _pantheonLastCharacter;
    private DateTimeOffset? _pantheonMessageAt;

    public NetworkPantheonSnapshot PantheonSnapshot { get { lock (_gate) return _pantheonSnapshot; } }

    private void ResetPantheonContext()
    {
        _pantheonMessageAt = null;
        _pantheonSnapshot = _pantheonSnapshot with
        {
            Character = null, Generation = _progressGeneration,
            Decorations = Stale(_pantheonSnapshot.Decorations)
        };
    }

    /// <summary>The shared complete Own binder has already verified the tentative login transaction.</summary>
    private void BindPantheonBaseline(EntryCandidate? candidate, OwnCharacterContext own)
    {
        if (!_combatOwnQualified || !ReferenceEquals(_ownCharacter, own)
            || !ReferenceEquals(_combatSnapshot.Character, own) || own.Generation != _progressGeneration) return;
        var same = _pantheonLastCharacter is { } previous
            && previous.CharacterId == own.Identity.CharacterId && previous.ServerId == own.Identity.ServerId;
        var source = same ? _pantheonSnapshot.Decorations : NetworkCombatDomain<CharacterPantheonState>.Unknown;
        if (candidate is { Prepared: true, PreparedAt: { } prepared } && candidate.Stream == _identityStream
            && prepared >= candidate.StartedAt && own.ObservedAt is { } entered && entered >= prepared
            && candidate.CharacterId == own.Identity.CharacterId && candidate.EntityKey == own.Identity.EntityKey)
            source = BindPending(candidate.Pantheon, source, own.ObservedAt!.Value);
        _pantheonLastCharacter = (own.Identity.CharacterId, own.Identity.ServerId);
        _pantheonMessageAt = candidate?.PantheonAt is { } seen && own.ObservedAt is { } observed && seen <= observed ? seen : null;
        _pantheonSnapshot = new()
        {
            Character = own, Generation = own.Generation, Sequence = _progressSequence, Decorations = source
        };
    }

    /// <summary>Called under the shared dispatch gate; an entity-free list needs fresh Own or a bounded login candidate.</summary>
    private bool ProcessOwnPantheonMessage(string stream, byte[] message, DateTimeOffset at)
    {
        if (!Running) return false;
        var own = _combatOwnQualified && _identityStream == stream ? _ownCharacter : null;
        if (own is not null && (own.Generation != _progressGeneration
            || !ReferenceEquals(_combatSnapshot.Character, own) || own.ObservedAt is not { } entered || at < entered)) return false;
        var pending = own is null && _entryCandidate is { } candidate && candidate.Stream == stream
            && at >= candidate.StartedAt && (!candidate.Prepared || candidate.PreparedAt is { } prepared && at >= prepared)
            ? candidate : null;
        if (own is null && pending is null) return false;
        if ((pending is not null ? pending.PantheonAt : _pantheonMessageAt) is { } lastMessage && at < lastMessage) return false;
        var previous = pending?.Pantheon ?? _pantheonSnapshot.Decorations;
        if (IsOlder(previous, at)) return false;
        var sequence = ++_progressSequence;
        NetworkCombatDomain<CharacterPantheonState> next;
        if (pending is { PantheonSeen: true }) next = Conflict(previous);
        else
        {
            var parsed = CharacterPantheonMessage.TryParseList(message);
            next = parsed is null ? Conflict(previous) : Observed(parsed, at, sequence, "own-pantheon-decoration-list");
        }
        if (pending is not null)
        {
            // Exactly one bounded list per transaction; duplicates never regain provisional authority.
            pending.PantheonSeen = true;
            pending.Pantheon = next;
            pending.PantheonAt = at;
        }
        else
        {
            _pantheonMessageAt = at;
            _pantheonSnapshot = new()
            {
                Character = own, Generation = own!.Generation, Sequence = sequence, Decorations = next
            };
        }
        return true;
    }
}
