using System.Collections.Immutable;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Combat;

namespace Soulcrest.App.Components;

/// <summary>Explicit actor-free diagnostic projection. Never serialize the character snapshot itself.</summary>
public static class CombatStatObservationPresentation
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.Default,
        Converters = { new JsonStringEnumConverter() }
    };

    public static CombatStatObservationDto? Create(CharacterCombatSnapshot? snapshot)
    {
        if (snapshot?.IsFresh != true) return null;
        var native = snapshot.NativeStats;
        var nativeKnownSource = native.Source is "own-entry-stat-subset" or "own-full-stat-list" or "own-stat-update";
        var nativeCurrent = nativeKnownSource && IsCurrent(native, snapshot.Generation);
        var nativeValues = nativeCurrent ? native.Value : null;
        var projectionCurrent = snapshot.DisplayStats.Generation == snapshot.Generation;
        return new()
        {
            Generation = snapshot.Generation, Sequence = snapshot.Sequence, ClassId = snapshot.ClassId,
            CharacterLevel = snapshot.CharacterLevel, CurrentCombatScore = snapshot.CurrentCombatScore,
            DisplayReferenceBuild = snapshot.DisplayStats.ClientBuild,
            DisplayVersion = snapshot.DisplayStats.Version,
            NativeStats = new(Status(native, nativeKnownSource), nativeCurrent, native.ObservedAt,
                nativeKnownSource ? native.Source : "unknown", nativeValues is not null
                    ? nativeValues.ToImmutableSortedDictionary() : null),
            HpMaximum = Maximum(snapshot.HpMaximum, snapshot.Generation), MpMaximum = Maximum(snapshot.MpMaximum, snapshot.Generation),
            ProjectedRows = Array.AsReadOnly(snapshot.DisplayStats.Rows.Select(row => new CombatStatProjectedObservationDto
            {
                Id = row.Id, Value = RowValue(snapshot, row, projectionCurrent, nativeCurrent),
                Status = projectionCurrent ? row.Status : CombatKnowledgeStatus.Stale,
                Qualification = row.Qualification, RuleId = row.RuleId,
                Reason = projectionCurrent ? row.Reason : "ObservationGenerationMismatch",
                MissingInputIds = Array.AsReadOnly(row.MissingInputIds.ToArray()),
                RawDependencies = Array.AsReadOnly(row.Inputs.Select(input => new CombatStatRawDependencyDto(input.NativeId,
                    projectionCurrent && nativeValues?.TryGetValue(input.NativeId, out var observed) == true ? observed : null,
                    input.Coefficient, input.Role)).ToArray())
            }).ToArray())
        };
    }

    public static string Serialize(CombatStatObservationDto observation) => JsonSerializer.Serialize(observation, JsonOptions);

    private static bool IsCurrent<T>(NetworkCombatDomain<T> domain, long generation) where T : class => generation > 0
        && domain.ObservedAt is not null && domain.IsFresh && domain.Value is not null
        && domain.Status is NetworkCombatSourceStatus.Complete or NetworkCombatSourceStatus.Partial;

    private static NetworkCombatSourceStatus Status<T>(NetworkCombatDomain<T> domain, bool knownSource) where T : class
    {
        if (!knownSource) return NetworkCombatSourceStatus.Unknown;
        if (domain.Status == NetworkCombatSourceStatus.Conflict) return domain.Status;
        if (domain.Status == NetworkCombatSourceStatus.Stale || domain.Value is not null && !domain.IsFresh)
            return NetworkCombatSourceStatus.Stale;
        return domain.Value is null ? NetworkCombatSourceStatus.Unknown : domain.Status;
    }

    private static CombatStatMaximumObservationDto Maximum(NetworkCombatDomain<NetworkCharacterResourceMaximum> maximum, long generation)
    {
        var knownSource = maximum.Source is "own-entry-resource-maxima" or "own-resource-maxima-update";
        var current = knownSource && IsCurrent(maximum, generation);
        return new(Status(maximum, knownSource), current, maximum.ObservedAt, knownSource ? maximum.Source : "unknown",
            current ? maximum.Value!.Value : null);
    }

    private static decimal? RowValue(CharacterCombatSnapshot snapshot, CharacterDisplayStatRow row,
        bool projectionCurrent, bool nativeCurrent)
    {
        if (!projectionCurrent || row.Status is not (CombatKnowledgeStatus.Complete or CombatKnowledgeStatus.Partial)) return null;
        if (row.Qualification == CharacterDisplayStatQualification.NativeDirect)
        {
            var maximum = row.Id switch { "HPMax" => snapshot.HpMaximum, "MPMax" => snapshot.MpMaximum, _ => null };
            return maximum is not null && Maximum(maximum, snapshot.Generation).IsFresh && row.ObservedAt == maximum.ObservedAt
                && row.Source == maximum.Source ? row.Value : null;
        }
        return nativeCurrent && row.ObservedAt == snapshot.NativeStats.ObservedAt ? row.Value : null;
    }
}

public sealed record CombatStatObservationDto
{
    public int Schema { get; init; } = 1;
    public long Generation { get; init; }
    public long Sequence { get; init; }
    public string? ClassId { get; init; }
    public int? CharacterLevel { get; init; }
    public long? CurrentCombatScore { get; init; }
    public int DisplayReferenceBuild { get; init; }
    public string DisplayVersion { get; init; } = CharacterDisplayStatProjection.Version;
    public CombatStatNativeObservationDto NativeStats { get; init; } = new(NetworkCombatSourceStatus.Unknown, false, null, "unknown", null);
    public CombatStatMaximumObservationDto HpMaximum { get; init; } = new(NetworkCombatSourceStatus.Unknown, false, null, "unknown", null);
    public CombatStatMaximumObservationDto MpMaximum { get; init; } = new(NetworkCombatSourceStatus.Unknown, false, null, "unknown", null);
    public IReadOnlyList<CombatStatProjectedObservationDto> ProjectedRows { get; init; } = [];
}

public sealed record CombatStatNativeObservationDto(NetworkCombatSourceStatus Status, bool IsFresh, DateTimeOffset? ObservedAt,
    string Source, IReadOnlyDictionary<ushort, int>? Values);
public sealed record CombatStatMaximumObservationDto(NetworkCombatSourceStatus Status, bool IsFresh, DateTimeOffset? ObservedAt,
    string Source, long? Value);
public sealed record CombatStatRawDependencyDto(ushort NativeId, int? RawValue, decimal Coefficient, CharacterDisplayInputRole Role);
public sealed record CombatStatProjectedObservationDto
{
    public string Id { get; init; } = "";
    public decimal? Value { get; init; }
    public CombatKnowledgeStatus Status { get; init; }
    public CharacterDisplayStatQualification Qualification { get; init; }
    public string RuleId { get; init; } = "";
    public string? Reason { get; init; }
    public IReadOnlyList<ushort> MissingInputIds { get; init; } = [];
    public IReadOnlyList<CombatStatRawDependencyDto> RawDependencies { get; init; } = [];
}
