using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Soulcrest.App.Components;

public sealed record CombatAchievementCategoryDefinition(string Id, string NameDe, string NameEn, string? IconPath = null);
public sealed record CombatAchievementGroupDefinition(int Id, string? CategoryId, string NameDe, string NameEn, string? IconPath = null)
{
    public bool HasNativeName { get; init; } = true;
    public string? ScopeDe { get; init; }
    public string? ScopeEn { get; init; }
}
public sealed record CombatAchievementDisplayReward(string NameDe, string NameEn, string? IconPath, long? Quantity);
public sealed record CombatAchievementDisplayStage(int Id, string NameDe, string NameEn, string ConditionDe,
    string ConditionEn, string? IconPath, long? NativeTarget, IReadOnlyList<CombatAchievementDisplayReward> Rewards);
public sealed record CombatAchievementDisplayDefinition(int Id, string? CategoryId, int? GroupId, string NameDe,
    string NameEn, string ConditionDe, string ConditionEn, string? IconPath, long? NativeTarget,
    IReadOnlyList<CombatAchievementDisplayReward> Rewards)
{
    public bool HasNativeName { get; init; } = true;
    public IReadOnlyList<CombatAchievementDisplayStage> Stages { get; init; } = [];
    public IReadOnlyList<int> UnknownStageIds { get; init; } = [];
}

/// <summary>
/// Immutable reference metadata. Names, objectives, catalogue stages and rewards never establish
/// current achievement progress, availability, character scope, completion or reward claims.
/// </summary>
public sealed class CombatAchievementDisplayCatalog
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly Lazy<CombatAchievementDisplayCatalog> Embedded = new(LoadEmbedded);
    private readonly FrozenDictionary<int, CombatAchievementDisplayDefinition> _lookup;

    public static CombatAchievementDisplayCatalog Default => Embedded.Value;
    public int MetadataBuild { get; }
    public IReadOnlyList<CombatAchievementCategoryDefinition> Categories { get; }
    public IReadOnlyList<CombatAchievementGroupDefinition> Groups { get; }
    public IReadOnlyList<CombatAchievementDisplayDefinition> Achievements { get; }

    private CombatAchievementDisplayCatalog(int metadataBuild, CatalogFile file)
    {
        if (file.Categories.Length > 64 || file.Groups.Length > 10000 || file.Achievements.Length > 100000)
            throw new InvalidDataException("Achievement catalogue exceeds its display bounds.");
        var categoryIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var category in file.Categories)
            if (category is null || !ValidKey(category.Id) || !categoryIds.Add(category.Id)
                || !ValidLabel(category.NameDe) || !ValidLabel(category.NameEn) || !ValidIcon(category.IconPath))
                throw new InvalidDataException("Invalid or duplicate achievement category.");
        var groups = new Dictionary<int, CombatAchievementGroupDefinition>();
        foreach (var group in file.Groups)
            if (group is null || group.Id <= 0 || !ValidCategory(group.CategoryId, categoryIds)
                || !ValidLabel(group.NameDe) || !ValidLabel(group.NameEn) || !ValidIcon(group.IconPath)
                || group.ScopeDe is not null && !ValidLabel(group.ScopeDe)
                || group.ScopeEn is not null && !ValidLabel(group.ScopeEn) || !groups.TryAdd(group.Id, group))
                throw new InvalidDataException("Invalid or duplicate achievement group.");
        var achievements = new Dictionary<int, CombatAchievementDisplayDefinition>();
        var stageIds = new HashSet<int>();
        foreach (var row in file.Achievements)
        {
            if (row is null || row.Id <= 0 || !ValidCategory(row.CategoryId, categoryIds)
                || !ValidLabel(row.NameDe) || !ValidLabel(row.NameEn)
                || !ValidLabel(row.ConditionDe, 4096) || !ValidLabel(row.ConditionEn, 4096)
                || !ValidIcon(row.IconPath) || row.NativeTarget is < 0
                || row.Stages is null || row.Stages.Count > 10000 || row.UnknownStageIds is null || row.UnknownStageIds.Count > 10000)
                throw new InvalidDataException("Invalid achievement display definition.");
            if (row.GroupId is { } groupId && (!groups.TryGetValue(groupId, out var group) || group.CategoryId != row.CategoryId))
                throw new InvalidDataException("Unresolved achievement display group.");
            var stages = new List<CombatAchievementDisplayStage>();
            foreach (var stage in row.Stages)
            {
                if (stage is null || stage.Id <= 0 || !stageIds.Add(stage.Id)
                    || !ValidLabel(stage.NameDe) || !ValidLabel(stage.NameEn)
                    || !ValidLabel(stage.ConditionDe, 4096) || !ValidLabel(stage.ConditionEn, 4096)
                    || !ValidIcon(stage.IconPath) || stage.NativeTarget is < 0)
                    throw new InvalidDataException("Invalid or duplicate achievement catalogue stage.");
                stages.Add(stage with { Rewards = FreezeRewards(stage.Rewards) });
            }
            foreach (var id in row.UnknownStageIds)
                if (id <= 0 || !stageIds.Add(id)) throw new InvalidDataException("Invalid or duplicate unmapped achievement stage.");
            var copy = row with { Rewards = FreezeRewards(row.Rewards), Stages = Array.AsReadOnly(stages.ToArray()),
                UnknownStageIds = Array.AsReadOnly(row.UnknownStageIds.ToArray()) };
            if (!achievements.TryAdd(copy.Id, copy)) throw new InvalidDataException("Duplicate achievement display definition.");
        }
        MetadataBuild = metadataBuild;
        Categories = Array.AsReadOnly(file.Categories.ToArray());
        Groups = Array.AsReadOnly(file.Groups.ToArray());
        Achievements = Array.AsReadOnly(achievements.Values.ToArray());
        _lookup = achievements.ToFrozenDictionary();
    }

    public CombatAchievementDisplayDefinition? Find(int achievementId) => _lookup.GetValueOrDefault(achievementId);

    internal static CombatAchievementDisplayCatalog FromJson(string json)
    {
        try
        {
            var file = JsonSerializer.Deserialize<CatalogFile>(json, Options);
            if (file is not { Schema: 1, MetadataBuild: > 0, ReferenceOnly: true,
                DoesNotEstablishCompletionOrClaim: true, Categories: not null, Groups: not null, Achievements: not null })
                throw new InvalidDataException("Unsupported achievement display schema.");
            return new(file.MetadataBuild, file);
        }
        catch (JsonException error) { throw new InvalidDataException("Invalid achievement display data.", error); }
    }

    private static IReadOnlyList<CombatAchievementDisplayReward> FreezeRewards(IReadOnlyList<CombatAchievementDisplayReward>? rewards)
    {
        if (rewards is null || rewards.Count > 1000 || rewards.Any(reward => reward is null
            || !ValidLabel(reward.NameDe) || !ValidLabel(reward.NameEn) || !ValidIcon(reward.IconPath) || reward.Quantity is < 0))
            throw new InvalidDataException("Invalid achievement display reward.");
        return Array.AsReadOnly(rewards.ToArray());
    }

    private static bool ValidKey(string? key) => !string.IsNullOrEmpty(key) && key.Length <= 64 && key != "All"
        && key.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    private static bool ValidCategory(string? key, IReadOnlySet<string> categories) => key is null || categories.Contains(key);
    private static bool ValidLabel(string? label, int limit = 1024) => !string.IsNullOrWhiteSpace(label)
        && label == label.Trim() && label.Length <= limit
        && !label.Any(character => char.IsControl(character) || character is '<' or '>' or '\uFFFD');
    private static bool ValidIcon(string? path) => path is null
        || (path.StartsWith("loot-icons/", StringComparison.Ordinal) || path.StartsWith("combat-icons/", StringComparison.Ordinal)
            || path.StartsWith("icons/combat/", StringComparison.Ordinal))
        && path.EndsWith(".png", StringComparison.Ordinal) && !path.Contains("..", StringComparison.Ordinal)
        && !path.Any(character => char.IsControl(character) || character is '\\' or ':' or '?' or '#');

    private static CombatAchievementDisplayCatalog LoadEmbedded()
    {
        using var stream = typeof(CombatAchievementDisplayCatalog).Assembly.GetManifestResourceStream("Soulcrest.CombatAchievementDisplayCatalog.json")
            ?? throw new InvalidDataException("Missing embedded achievement display data.");
        using var reader = new StreamReader(stream);
        return FromJson(reader.ReadToEnd());
    }

    private sealed record CatalogFile
    {
        public required int Schema { get; init; }
        public required int MetadataBuild { get; init; }
        public required bool ReferenceOnly { get; init; }
        public required bool DoesNotEstablishCompletionOrClaim { get; init; }
        public required CombatAchievementCategoryDefinition[] Categories { get; init; }
        public required CombatAchievementGroupDefinition[] Groups { get; init; }
        public required CombatAchievementDisplayDefinition[] Achievements { get; init; }
    }
}
