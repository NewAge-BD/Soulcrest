using System.Collections.Immutable;
using Soulcrest.App.Network;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Services;

/// <summary>Character-local last complete observation. Contains no native actor or transport identity.</summary>
public sealed record CharacterPantheonHistory(string BindingRevision, ImmutableDictionary<int, byte> Decorations,
    DateTimeOffset ObservedAt, string Source);

public sealed record ProfilePantheonSnapshot(string ProfileId, CharacterPantheonHistory State);

public sealed partial class ExplorationService
{
    private OwnCharacterIdentity? _pantheonIdentity;
    private long _pantheonGeneration = -1, _pantheonSequence = -1;

    public ProfilePantheonSnapshot? TryGetSavedPantheon(string? profileId = null)
    {
        lock (_gate)
        {
            var profile = _state.Characters.SingleOrDefault(row => row.Id == (profileId ?? _state.Active));
            if (profile?.Pantheon is not { } saved || !ValidPantheonHistory(saved)
                || profile.AchievementBindingRevision != saved.BindingRevision
                || TryGetAchievementBinding(profile.Id) is null) return null;
            return new(profile.Id, saved);
        }
    }

    internal bool ApplyNetworkPantheon(OwnCharacterContext expected, string profileId,
        NetworkCombatDomain<CharacterPantheonState> observation)
    {
        if (observation is not { IsFresh: true, Status: NetworkCombatSourceStatus.Complete,
                Value: not null, ObservedAt: not null, Sequence: > 0 }
            || !PantheonSource(observation.Source)) return false;
        bool changed;
        lock (_gate)
        {
            if (_networkContext is not { } current || current.Generation != expected.Generation
                || expected.Generation <= _invalidatedGeneration || current.Identity != expected.Identity
                || _state.Characters.SingleOrDefault(row => row.Id == _networkCharacter) is not { } profile
                || profile.Id != profileId || profile.NetworkCharacterId != current.CharacterId
                || profile.ServerId != current.ServerId || GetAchievementBinding(expected) is not { } binding
                || binding.ProfileId != profileId) return false;
            var next = new CharacterPantheonHistory(binding.Revision, observation.Value.Decorations,
                observation.ObservedAt.Value, observation.Source);
            if (!ValidPantheonHistory(next)) return false;
            if (_pantheonGeneration == expected.Generation && _pantheonIdentity == expected.Identity
                && observation.Sequence <= _pantheonSequence) return false;
            var old = profile.Pantheon;
            if (old is not null && ValidPantheonHistory(old) && old.BindingRevision == next.BindingRevision
                && old.ObservedAt > next.ObservedAt) return false;
            changed = old is null || old.BindingRevision != next.BindingRevision || old.ObservedAt != next.ObservedAt
                || old.Source != next.Source || old.Decorations.Count != next.Decorations.Count
                || old.Decorations.Any(pair => !next.Decorations.TryGetValue(pair.Key, out var slot) || slot != pair.Value);
            _pantheonIdentity = expected.Identity; _pantheonGeneration = expected.Generation; _pantheonSequence = observation.Sequence;
            if (changed) { profile.Pantheon = next; PersistNetwork(); }
        }
        if (changed) SafeEvent.Raise(Changed, "Pantheon im Charakterprofil");
        return changed;
    }

    internal static bool PantheonSource(string source) => source is "own-pantheon-decoration-list" or "own-pantheon-update";
    internal static bool ValidPantheonHistory(CharacterPantheonHistory saved) =>
        saved.BindingRevision is { Length: 32 } && Guid.TryParseExact(saved.BindingRevision, "N", out var id) && id != Guid.Empty
        && saved.Decorations is not null && saved.Decorations.Count <= 65536 && saved.Decorations.Keys.All(key => key > 0)
        && saved.ObservedAt != default && PantheonSource(saved.Source);
}
