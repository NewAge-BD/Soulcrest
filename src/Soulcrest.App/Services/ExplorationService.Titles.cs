using System.Collections.Immutable;
using Soulcrest.App.Network;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Services;

/// <summary>A historical observation attached only to the containing local character profile.</summary>
public sealed record CharacterTitleOwnershipHistory(ImmutableHashSet<int> OwnedTitleIds,
    DateTimeOffset ObservedAt, string Source);

/// <summary>Historical UI data. It never supplies a fresh network domain or solver input.</summary>
public sealed record ProfileTitleSnapshot(string ProfileId, ImmutableHashSet<int> OwnedTitleIds,
    DateTimeOffset ObservedAt, string Source);

public sealed partial class ExplorationService
{
    public ProfileTitleSnapshot? TryGetSavedTitles(string? profileId = null)
    {
        lock (_gate)
        {
            var character = _state.Characters.SingleOrDefault(character => character.Id == (profileId ?? _state.Active));
            if (character?.TitleOwnership is not { } saved || !ValidTitleHistory(saved)) return null;
            return new(character.Id, saved.OwnedTitleIds, saved.ObservedAt, saved.Source);
        }
    }

    /// <summary>Replace only a fully observed list while the exact own identity still maps to the expected profile.</summary>
    internal bool ApplyNetworkTitleOwnership(OwnCharacterContext expected, string profileId,
        NetworkCombatDomain<ImmutableHashSet<int>> ownership)
    {
        if (ownership.Status != NetworkCombatSourceStatus.Complete || !ownership.IsFresh
            || ownership.Value is not { } ids || ownership.ObservedAt is not { } at) return false;
        var next = new CharacterTitleOwnershipHistory(ids, at, ownership.Source);
        if (!ValidTitleHistory(next)) return false;
        lock (_gate)
        {
            if (_networkContext is not { } current || current.Generation != expected.Generation
                || expected.Generation <= _invalidatedGeneration || current.Identity != expected.Identity
                || _state.Characters.SingleOrDefault(character => character.Id == _networkCharacter) is not { } character
                || character.Id != profileId || character.NetworkCharacterId != current.CharacterId
                || character.ServerId != current.ServerId) return false;
            if (character.TitleOwnership is { } previous && ValidTitleHistory(previous)
                && (previous.ObservedAt > at || previous.ObservedAt == at && previous.Source == next.Source
                    && previous.OwnedTitleIds.SetEquals(ids))) return false;
            character.TitleOwnership = next;
            PersistNetwork();
        }
        SafeEvent.Raise(Changed, "Titelbesitz im Charakterprofil");
        return true;
    }

    private static bool ValidTitleHistory(CharacterTitleOwnershipHistory history) =>
        history.OwnedTitleIds is not null && history.OwnedTitleIds.All(id => id > 0)
        && history.ObservedAt != default && history.Source == "own-title-ownership-list";
}
