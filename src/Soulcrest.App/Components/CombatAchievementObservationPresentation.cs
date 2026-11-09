using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Soulcrest.App.Network;
using Soulcrest.App.Services;

namespace Soulcrest.App.Components;

/// <summary>Actor-free raw diagnostic observations, independent of achievement progress qualification.</summary>
public static class CombatAchievementObservationPresentation
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.Default,
        Converters = { new JsonStringEnumConverter() }
    };

    public static CombatAchievementObservationDto? Create(CharacterCombatSnapshot? snapshot)
    {
        // Profile binding is checked internally and never copied into the diagnostic shape.
        if (snapshot?.IsFresh != true || string.IsNullOrWhiteSpace(snapshot.ProfileId)) return null;
        var domain = snapshot.Achievements;
        var knownSource = domain.Source is "own-achievement-list" or "own-achievement-update" or "own-achievement-reward";
        var current = knownSource && snapshot.Generation > 0 && domain.IsFresh && domain.Value is not null
            && domain.ObservedAt is not null && domain.Status is NetworkCombatSourceStatus.Complete or NetworkCombatSourceStatus.Partial;
        var status = !knownSource ? NetworkCombatSourceStatus.Unknown
            : domain.Status == NetworkCombatSourceStatus.Conflict ? NetworkCombatSourceStatus.Conflict
            : domain.Status == NetworkCombatSourceStatus.Stale || domain.Value is not null && !domain.IsFresh
                ? NetworkCombatSourceStatus.Stale : domain.Value is null ? NetworkCombatSourceStatus.Unknown : domain.Status;
        return new()
        {
            Generation = snapshot.Generation, Sequence = snapshot.Sequence, DomainSequence = domain.Sequence,
            ObservedAt = domain.ObservedAt, Status = status, IsFresh = current,
            Source = knownSource ? domain.Source : "unknown",
            Rows = current ? Array.AsReadOnly(domain.Value!.Achievements.OrderBy(row => row.Key)
                .Select(row => new CombatAchievementObservedRowDto(row.Key, row.Value.Count,
                    row.Value.CompleteTime.ToString(CultureInfo.InvariantCulture), row.Value.RewardedStep)).ToArray()) : null,
            RewardGroups = current ? Array.AsReadOnly(domain.Value!.RewardGroups.OrderBy(row => row.Key)
                .Select(row => new CombatAchievementObservedRewardGroupDto(row.Key, Array.AsReadOnly(row.Value.ToArray()))).ToArray()) : null
        };
    }

    public static string Serialize(CombatAchievementObservationDto observation) => JsonSerializer.Serialize(observation, JsonOptions);
}

public sealed record CombatAchievementObservationDto
{
    public int Schema { get; init; } = 1;
    public long Generation { get; init; }
    public long Sequence { get; init; }
    public long DomainSequence { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }
    public NetworkCombatSourceStatus Status { get; init; }
    public bool IsFresh { get; init; }
    public string Source { get; init; } = "unknown";
    public IReadOnlyList<CombatAchievementObservedRowDto>? Rows { get; init; }
    public IReadOnlyList<CombatAchievementObservedRewardGroupDto>? RewardGroups { get; init; }
}

/// <summary>CompleteTime remains an invariant Int64 string to avoid JavaScript numeric rounding.</summary>
public sealed record CombatAchievementObservedRowDto(int Id, int Count, string CompleteTime, byte RewardedStep);
public sealed record CombatAchievementObservedRewardGroupDto(int Id, IReadOnlyList<bool> Bits);
