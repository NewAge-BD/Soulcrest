using System.Security.Cryptography;
using System.Text;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Services;

public sealed partial class ExplorationService
{
    /// <summary>Raised after a profile is removed. Pending background history writes must be invalidated.</summary>
    public event Action<string>? CharacterRemoved;

    /// <summary>
    /// Resolves the frozen native owner of an already accepted observation. It deliberately does not use
    /// the active UI profile, a name match, or create a profile for a delayed event.
    /// </summary>
    public AchievementProfileBinding? GetAchievementBinding(OwnCharacterContext expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (expected.Identity.CharacterId <= 0 || expected.Identity.ServerId <= 0 || expected.Generation < 0) return null;
        lock (_gate)
        {
            var matches = _state.Characters.Where(character => character.NetworkCharacterId == expected.Identity.CharacterId
                && character.ServerId == expected.Identity.ServerId).ToArray();
            if (matches.Length != 1) return null;
            var character = matches[0];
            if (!IsAchievementOpaqueId(character.Id) || !IsAchievementOpaqueId(character.AchievementBindingRevision)
                || character.AchievementOwnerFingerprint != AchievementOwnerFingerprint(character)) return null;
            return new(character.Id, character.AchievementBindingRevision!);
        }
    }

    /// <summary>Returns only an existing, still matching saved assignment; reading offline creates no baseline.</summary>
    public AchievementProfileBinding? TryGetAchievementBinding(string? profileId = null)
    {
        lock (_gate)
        {
            var character = _state.Characters.SingleOrDefault(candidate => candidate.Id == (profileId ?? _state.Active));
            if (character is null || !IsAchievementOpaqueId(character.Id) || !IsAchievementOpaqueId(character.AchievementBindingRevision)
                || character.NetworkCharacterId is not > 0 || character.ServerId is not > 0
                || character.AchievementOwnerFingerprint != AchievementOwnerFingerprint(character)) return null;
            return new(character.Id, character.AchievementBindingRevision!);
        }
    }

    /// <summary>Deleting a profile never reassigns its GUID. Other existing profile data remains intact.</summary>
    public bool Remove(string profileId)
    {
        var removed = false;
        lock (_gate)
        {
            var character = _state.Characters.SingleOrDefault(candidate => candidate.Id == profileId);
            if (character is null) return false;
            _state.Characters.Remove(character);
            if (_networkCharacter == profileId)
            {
                if (_networkContext is { } context) _invalidatedGeneration = Math.Max(_invalidatedGeneration, context.Generation);
                _networkCharacter = null;
                _networkContext = null;
            }
            if (_state.Characters.Count == 0) _state.Characters.Add(new CharacterExploration());
            if (_state.Active == profileId) _state.Active = _state.Characters[0].Id;
            Persist();
            removed = true;
        }
        if (removed)
        {
            SafeEvent.Raise(CharacterRemoved, profileId, "Achievement-Profil entfernt");
            SafeEvent.Raise(Changed, "Charakterprofil entfernt");
        }
        return removed;
    }

    private static bool EnsureAchievementBinding(CharacterExploration character)
    {
        if (character.NetworkCharacterId is not > 0 || character.ServerId is not > 0 || !IsAchievementOpaqueId(character.Id)) return false;
        var fingerprint = AchievementOwnerFingerprint(character);
        if (character.AchievementOwnerFingerprint == fingerprint && IsAchievementOpaqueId(character.AchievementBindingRevision)) return false;
        character.AchievementOwnerFingerprint = fingerprint;
        character.AchievementBindingRevision = Guid.NewGuid().ToString("N");
        return true;
    }

    private static string AchievementOwnerFingerprint(CharacterExploration character) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(FormattableString.Invariant($"achievement-owner-v1:{character.ServerId}:{character.NetworkCharacterId}"))));

    private static bool IsAchievementOpaqueId(string? value) => value is { Length: 32 }
        && Guid.TryParseExact(value, "N", out var id) && id != Guid.Empty && id.ToString("N") == value;
}
