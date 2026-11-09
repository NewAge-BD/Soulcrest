using System.Collections.Immutable;
using Soulcrest.App.Network;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Services;

/// <summary>Character-local history; enchantment is retained only for future use, not displayed or calculated.</summary>
public sealed record CharacterWingOwnershipHistory(ImmutableDictionary<int, byte> Wings,
    DateTimeOffset ObservedAt, string Source);

/// <summary>Historical UI data. This never supplies a fresh network domain or solver input.</summary>
public sealed record ProfileWingSnapshot(string ProfileId, ImmutableDictionary<int, byte> Wings,
    DateTimeOffset ObservedAt, string Source);

public sealed partial class ExplorationService
{
    private OwnCharacterIdentity? _wingOwnershipIdentity;
    private long _wingOwnershipGeneration = -1, _wingOwnershipSequence = -1;

    public ProfileWingSnapshot? TryGetSavedWings(string? profileId = null)
    {
        lock (_gate)
        {
            var character = _state.Characters.SingleOrDefault(character => character.Id == (profileId ?? _state.Active));
            if (character?.WingOwnership is not { } saved || !ValidWingHistory(saved)) return null;
            return new(character.Id, saved.Wings, saved.ObservedAt, saved.Source);
        }
    }

    /// <summary>Only an exact current own binding and complete list may replace character history.</summary>
    internal bool ApplyNetworkWingOwnership(OwnCharacterContext expected, string profileId,
        NetworkCombatDomain<CharacterWingOwnership> ownership)
    {
        if (ownership.Status != NetworkCombatSourceStatus.Complete || !ownership.IsFresh || ownership.Sequence <= 0
            || ownership.Value is not { } value || ownership.ObservedAt is not { } at) return false;
        var next = new CharacterWingOwnershipHistory(value.Wings, at, ownership.Source);
        if (!ValidWingHistory(next)) return false;
        lock (_gate)
        {
            if (_networkContext is not { } current || current.Generation != expected.Generation
                || expected.Generation <= _invalidatedGeneration || current.Identity != expected.Identity
                || _state.Characters.SingleOrDefault(character => character.Id == _networkCharacter) is not { } character
                || character.Id != profileId || character.NetworkCharacterId != current.CharacterId
                || character.ServerId != current.ServerId) return false;
            // Several messages can share a capture timestamp. A reentrant callback must not
            // overwrite a newer accepted domain with an older sequence of the same own binding.
            if (_wingOwnershipGeneration == expected.Generation && _wingOwnershipIdentity == expected.Identity
                && ownership.Sequence <= _wingOwnershipSequence) return false;
            if (character.WingOwnership is { } previous && ValidWingHistory(previous)
                && (previous.ObservedAt > at || previous.ObservedAt == at && previous.Source == next.Source
                    && previous.Wings.Count == next.Wings.Count
                    && previous.Wings.All(row => next.Wings.TryGetValue(row.Key, out var level) && level == row.Value))) return false;
            character.WingOwnership = next;
            _wingOwnershipIdentity = expected.Identity;
            _wingOwnershipGeneration = expected.Generation;
            _wingOwnershipSequence = ownership.Sequence;
            PersistNetwork();
        }
        SafeEvent.Raise(Changed, "Flügelbesitz im Charakterprofil");
        return true;
    }

    private static bool ValidWingHistory(CharacterWingOwnershipHistory history) =>
        history.Wings is not null && history.Wings.Count <= 65536 && history.Wings.Keys.All(id => id > 0)
        && history.ObservedAt != default && history.Source == "own-wing-ownership-list";
}
