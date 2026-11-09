using System.Collections.Immutable;
using Soulcrest.App.Network;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Services;

/// <summary>One shared collection of raw genus boards. It contains no actor, profile, server or transport identity.</summary>
public sealed record GenusInsightHistory(ImmutableDictionary<byte, CharacterGenusBoard> Boards,
    DateTimeOffset ObservedAt, string Source);

/// <summary>Only the last confirmed active preset is character-local; board rolls are shared.</summary>
public sealed record CharacterGenusInsightPreset(byte Preset, DateTimeOffset ObservedAt, string Source);

/// <summary>Historical shared boards combined with exactly the requested local profile's preset, never fresh network evidence.</summary>
public sealed record ProfileGenusInsightSnapshot(string ProfileId, ImmutableDictionary<byte, CharacterGenusBoard> Boards,
    byte? ActivePreset, DateTimeOffset ObservedAt, string Source)
{
    public DateTimeOffset? ActivePresetObservedAt { get; init; }
    public string? ActivePresetSource { get; init; }
}

public sealed partial class ExplorationService
{
    private OwnCharacterIdentity? _genusInsightIdentity;
    private long _genusInsightGeneration = -1, _genusInsightSequence = -1;

    public ProfileGenusInsightSnapshot? TryGetSavedGenusInsight(string? profileId = null)
    {
        lock (_gate)
        {
            if (_state.GenusInsight is not { } saved || !ValidGenusHistory(saved)
                || _state.Characters.SingleOrDefault(character => character.Id == (profileId ?? _state.Active)) is not { } profile) return null;
            var preset = profile.GenusInsightPreset is { } selected && ValidGenusPreset(selected) ? selected : null;
            return new(profile.Id, saved.Boards, preset?.Preset, saved.ObservedAt, saved.Source)
            { ActivePresetObservedAt = preset?.ObservedAt, ActivePresetSource = preset?.Source };
        }
    }

    /// <summary>Atomically save shared complete boards and, only from a full list, this own profile's selected preset.</summary>
    internal bool ApplyNetworkGenusInsight(OwnCharacterContext expected, string profileId,
        NetworkCombatDomain<PetProgressSnapshot> observation)
    {
        if (observation is not { IsFresh: true, Status: NetworkCombatSourceStatus.Complete, Sequence: > 0,
                Value: { } value, ObservedAt: { } at } || !ValidGenusSource(observation.Source)) return false;
        var next = new GenusInsightHistory(value.GenusBoards, at, observation.Source);
        if (!ValidGenusHistory(next)) return false;
        var changed = false;
        lock (_gate)
        {
            var profile = GenusOwnProfile(expected, profileId);
            if (profile is null || _genusInsightGeneration == expected.Generation && _genusInsightIdentity == expected.Identity
                && observation.Sequence <= _genusInsightSequence) return false;
            var previous = _state.GenusInsight;
            if (previous is null || !ValidGenusHistory(previous) || at >= previous.ObservedAt)
            {
                if (previous is null || !ValidGenusHistory(previous) || at != previous.ObservedAt
                    || previous.Source != next.Source || !SameGenusBoards(previous.Boards, next.Boards))
                { _state.GenusInsight = next; changed = true; }
            }
            // Roll/lock replies do not acknowledge an active-preset choice. A retained raw
            // SelectedGenusPanel must never acquire the timestamp of an unrelated update.
            if (observation.Source == "own-pet-genus-list" && value.SelectedGenusPanel is >= 1 and <= 3)
            {
                var selected = new CharacterGenusInsightPreset(value.SelectedGenusPanel.Value, at, observation.Source);
                var priorPreset = profile.GenusInsightPreset;
                if (priorPreset is null || !ValidGenusPreset(priorPreset) || at >= priorPreset.ObservedAt)
                {
                    if (priorPreset != selected) { profile.GenusInsightPreset = selected; changed = true; }
                }
            }
            // Even an identical, non-writing observation advances the in-memory ordering
            // barrier so a delayed same-time callback cannot put an older value back.
            _genusInsightIdentity = expected.Identity;
            _genusInsightGeneration = expected.Generation;
            _genusInsightSequence = observation.Sequence;
            if (changed) PersistNetwork();
        }
        if (changed) SafeEvent.Raise(Changed, "Gespeicherte Genus-Analyse");
        return changed;
    }

    /// <summary>Save only a visually confirmed choice under the exact current own binding. No stat freshness is created.</summary>
    internal bool ApplyObservedGenusPreset(OwnCharacterContext expected, string profileId, byte preset, DateTimeOffset observedAt)
    {
        if (preset is < 1 or > 3 || observedAt == default
            || expected.ObservedAt is { } entered && observedAt < entered) return false;
        var next = new CharacterGenusInsightPreset(preset, observedAt, "game-menu-preset");
        lock (_gate)
        {
            var profile = GenusOwnProfile(expected, profileId);
            if (profile is null || _state.GenusInsight is not { } boards || !ValidGenusHistory(boards)) return false;
            var previous = profile.GenusInsightPreset;
            if (previous is not null && ValidGenusPreset(previous)
                && (observedAt < previous.ObservedAt || observedAt == previous.ObservedAt
                    || preset == previous.Preset)) return false;
            // Repeated menu frames do not rewrite the same choice every 200 ms. The
            // confirmed time belongs to the selection, not to subsequent frame polling.
            profile.GenusInsightPreset = next;
            PersistNetwork();
        }
        SafeEvent.Raise(Changed, "Gespeicherte Genus-Voreinstellung");
        return true;
    }

    private CharacterExploration? GenusOwnProfile(OwnCharacterContext expected, string profileId)
    {
        if (_networkContext is not { } current || current.Generation != expected.Generation
            || expected.Generation <= _invalidatedGeneration || current.Identity != expected.Identity
            || _state.Characters.SingleOrDefault(character => character.Id == _networkCharacter) is not { } profile
            || profile.Id != profileId || profile.NetworkCharacterId != current.CharacterId || profile.ServerId != current.ServerId) return null;
        return profile;
    }

    private static bool ValidGenusSource(string source) => source is
        "own-pet-genus-list" or "own-pet-genus-lock-update" or "own-pet-genus-roll-update";

    private static bool ValidGenusPreset(CharacterGenusInsightPreset preset) => preset.Preset is >= 1 and <= 3
        && preset.ObservedAt != default && preset.Source is "own-pet-genus-list" or "game-menu-preset";

    internal static bool ValidGenusHistory(GenusInsightHistory history) => history.ObservedAt != default
        && ValidGenusSource(history.Source) && history.Boards is { Count: 5 }
        && history.Boards.Values.All(board => board is not null && board.CreatureType is >= 2 and <= 6
            && board.Level is >= 0 and <= 10 && board.Experience >= 0
            && board.Panels is { Count: 3 } && board.Panels.All(panel => panel.Key is >= 1 and <= 3
                && !panel.Value.IsDefault && panel.Value.Length == 9 && panel.Value.All(slot => slot is not null && slot.Slot <= 8)
                && panel.Value.Select(slot => slot.Slot).Distinct().Count() == 9))
        && history.Boards.Values.Select(board => board.CreatureType).Distinct().Count() == 5;

    private static bool SameGenusBoards(ImmutableDictionary<byte, CharacterGenusBoard> left,
        ImmutableDictionary<byte, CharacterGenusBoard> right) => left.Count == right.Count && left.All(pair =>
        right.TryGetValue(pair.Key, out var board) && pair.Value.CreatureType == board.CreatureType
        && pair.Value.Level == board.Level && pair.Value.Experience == board.Experience
        && pair.Value.Panels.Count == board.Panels.Count && pair.Value.Panels.All(panel =>
            board.Panels.TryGetValue(panel.Key, out var slots)
            && panel.Value.OrderBy(slot => slot.Slot).SequenceEqual(slots.OrderBy(slot => slot.Slot))));
}
