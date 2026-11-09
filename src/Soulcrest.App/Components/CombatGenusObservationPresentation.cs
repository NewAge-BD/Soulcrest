using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Soulcrest.App.Network;
using Soulcrest.App.Services;

namespace Soulcrest.App.Components;

/// <summary>Opt-in actor-free raw genus projection. Never serialize the character or native transport context.</summary>
public static class CombatGenusObservationPresentation
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.Default,
        Converters = { new JsonStringEnumConverter() }
    };

    public static CombatGenusObservationDto? Create(CharacterCombatSnapshot? snapshot)
    {
        if (snapshot?.IsFresh != true || string.IsNullOrWhiteSpace(snapshot.ProfileId) || snapshot.Generation <= 0) return null;
        var pets = snapshot.Pets;
        if (pets.Source is not ("own-pet-genus-list" or "own-pet-genus-lock-update" or "own-pet-genus-roll-update") || !pets.IsFresh || pets.ObservedAt is not { } observed
            || pets.Status is not (NetworkCombatSourceStatus.Complete or NetworkCombatSourceStatus.Partial)
            || pets.Value is not { } value) return null;
        var boards = value.GenusBoards.OrderBy(board => board.Key).Select(board => new CombatGenusBoardObservationDto(
            board.Value.CreatureType, board.Value.Level,
            Array.AsReadOnly(board.Value.Panels.OrderBy(panel => panel.Key).Select(panel => new CombatGenusPanelObservationDto(
                panel.Key, Array.AsReadOnly(panel.Value.Select(slot => new CombatGenusSlotObservationDto(
                    slot.Slot, slot.Grade, slot.StatType, slot.Value.ToString(CultureInfo.InvariantCulture),
                    slot.IsLocked, slot.IsActivated)).ToArray()))).ToArray()))).ToArray();
        return new(snapshot.Generation, snapshot.Sequence, pets.Sequence, pets.Status, true, pets.Source,
            observed, value.SelectedGenusPanel, Array.AsReadOnly(boards));
    }

    public static string Serialize(CombatGenusObservationDto observation) => JsonSerializer.Serialize(observation, JsonOptions);
}

public sealed record CombatGenusObservationDto(long Generation, long Sequence, long DomainSequence,
    NetworkCombatSourceStatus Status, bool IsFresh, string Source, DateTimeOffset ObservedAt,
    byte? SelectedPanel, IReadOnlyList<CombatGenusBoardObservationDto> Boards)
{
    public int Schema { get; init; } = 1;
}

public sealed record CombatGenusBoardObservationDto(byte CreatureType, int Level,
    IReadOnlyList<CombatGenusPanelObservationDto> Panels);
public sealed record CombatGenusPanelObservationDto(byte Panel, IReadOnlyList<CombatGenusSlotObservationDto> Slots);
/// <summary>Value is invariant I64 text so JavaScript cannot round a native value through IEEE-754.</summary>
public sealed record CombatGenusSlotObservationDto(byte Slot, byte Grade, ushort StatType, string Value,
    bool IsLocked, bool IsActivated);
