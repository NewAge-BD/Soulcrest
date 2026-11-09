using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Soulcrest.App.Network;
using Soulcrest.App.Services;

namespace Soulcrest.App.Components;

/// <summary>Explicit actor-free debugger projection. Never serialize the character snapshot.</summary>
public static class CombatWingObservationPresentation
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.Default,
        Converters = { new JsonStringEnumConverter() }
    };

    public static CombatWingObservationDto? Create(CharacterCombatSnapshot? snapshot)
    {
        if (snapshot?.IsFresh != true || string.IsNullOrWhiteSpace(snapshot.ProfileId)) return null;
        var owned = snapshot.WingOwnership;
        var equipped = snapshot.EquippedWing;
        var ownedSource = owned.Source == "own-wing-ownership-list";
        var equippedSource = equipped.Source is "own-entry-wing-state" or "own-wing-change";
        var ownedCurrent = IsCurrent(owned, ownedSource, snapshot.Generation);
        var equippedCurrent = IsCurrent(equipped, equippedSource, snapshot.Generation);
        return new(snapshot.Generation, snapshot.Sequence,
            new(Status(owned, ownedSource), ownedCurrent, owned.ObservedAt, owned.Sequence,
                ownedSource ? owned.Source : "unknown",
                ownedCurrent ? Array.AsReadOnly(owned.Value!.Wings.OrderBy(row => row.Key)
                    .Select(row => new CombatWingObservedRowDto(row.Key, row.Value)).ToArray()) : null),
            new(Status(equipped, equippedSource), equippedCurrent, equipped.ObservedAt, equipped.Sequence,
                equippedSource ? equipped.Source : "unknown",
                equippedCurrent ? equipped.Value!.WingId : null,
                equippedCurrent ? equipped.Value!.EnchantLevel : null,
                equippedCurrent ? equipped.Value!.SkinId : null));
    }

    public static string Serialize(CombatWingObservationDto observation) => JsonSerializer.Serialize(observation, JsonOptions);

    private static bool IsCurrent<T>(NetworkCombatDomain<T> domain, bool knownSource, long generation) where T : class =>
        knownSource && generation > 0 && domain.IsFresh && domain.ObservedAt is not null && domain.Value is not null
        && domain.Status is NetworkCombatSourceStatus.Complete or NetworkCombatSourceStatus.Partial;

    private static NetworkCombatSourceStatus Status<T>(NetworkCombatDomain<T> domain, bool knownSource) where T : class =>
        !knownSource ? NetworkCombatSourceStatus.Unknown
        : domain.Status == NetworkCombatSourceStatus.Conflict ? NetworkCombatSourceStatus.Conflict
        : domain.Status == NetworkCombatSourceStatus.Stale || domain.Value is not null && !domain.IsFresh
            ? NetworkCombatSourceStatus.Stale : domain.Value is null ? NetworkCombatSourceStatus.Unknown : domain.Status;
}

public sealed record CombatWingObservationDto(long Generation, long Sequence,
    CombatWingOwnershipObservationDto Ownership, CombatWingEquipmentObservationDto Equipped)
{
    public int Schema { get; init; } = 1;
}

public sealed record CombatWingObservedRowDto(int Id, byte EnchantLevel);
public sealed record CombatWingOwnershipObservationDto(NetworkCombatSourceStatus Status, bool IsFresh,
    DateTimeOffset? ObservedAt, long Sequence, string Source, IReadOnlyList<CombatWingObservedRowDto>? Wings);
public sealed record CombatWingEquipmentObservationDto(NetworkCombatSourceStatus Status, bool IsFresh,
    DateTimeOffset? ObservedAt, long Sequence, string Source, int? WingId, byte? EnchantLevel, int? SkinId);
