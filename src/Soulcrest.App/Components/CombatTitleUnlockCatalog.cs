using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Soulcrest.App.Components;

/// <summary>Versioned reward references; never evidence of availability, possession or achievement completion.</summary>
public sealed class CombatTitleUnlockCatalog
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly Lazy<CombatTitleUnlockCatalog> Embedded = new(LoadEmbedded);
    private readonly FrozenDictionary<int, CombatTitleUnlockDefinition> _titles;
    public static CombatTitleUnlockCatalog Default => Embedded.Value;
    public int MetadataBuild { get; }
    public IReadOnlyDictionary<int, CombatTitleUnlockDefinition> Titles => _titles;
    public CombatTitleUnlockDefinition? Find(int id) => _titles.GetValueOrDefault(id);

    private CombatTitleUnlockCatalog(int build, IEnumerable<CombatTitleUnlockDefinition> titles)
    {
        var entries = new Dictionary<int, CombatTitleUnlockDefinition>();
        foreach (var title in titles)
        {
            if (title is null || title.Id <= 0 || title.DirectGrantItemIds is null || title.Routes is null
                || title.DirectGrantItemIds.Any(id => id <= 0)
                || title.DirectGrantItemIds.Distinct().Count() != title.DirectGrantItemIds.Count)
                throw new InvalidDataException("Invalid title unlock definition.");
            foreach (var route in title.Routes)
            {
                if (route is null || route.Kind is not ("AchievementRewardItem" or "QuestRewardItem" or "SeasonRankingRewardItem" or "RankingCatalogReference")
                    || route.SourceId <= 0 || !title.DirectGrantItemIds.Contains(route.ItemId)
                    || (route.Kind == "AchievementRewardItem" ? route.StageId is not > 0 : route.StageId is not null)
                    || !ValidText(route.NameDe) || !ValidText(route.NameEn)
                    || !ValidText(route.ConditionDe) || !ValidText(route.ConditionEn))
                    throw new InvalidDataException("Invalid title reward reference.");
            }
            if (title.Routes.Distinct().Count() != title.Routes.Count || !entries.TryAdd(title.Id, title with
                { DirectGrantItemIds = Array.AsReadOnly(title.DirectGrantItemIds.ToArray()), Routes = Array.AsReadOnly(title.Routes.ToArray()) }))
                throw new InvalidDataException("Duplicate title unlock definition or reference.");
        }
        MetadataBuild = build;
        _titles = entries.ToFrozenDictionary();
    }

    internal static CombatTitleUnlockCatalog FromJson(string json)
    {
        try
        {
            var file = JsonSerializer.Deserialize<CatalogFile>(json, Options);
            if (file is not { Schema: 1, MetadataBuild: > 0, ReferenceOnly: true,
                DoesNotEstablishAvailabilityOwnershipCompletionOrClaim: true, Titles: not null })
                throw new InvalidDataException("Unsupported title unlock schema.");
            return new(file.MetadataBuild, file.Titles);
        }
        catch (JsonException error) { throw new InvalidDataException("Invalid title unlock metadata.", error); }
    }

    private static bool ValidText(string? value) => value is null || !string.IsNullOrWhiteSpace(value)
        && value == value.Trim() && value.Length <= 4096
        && !value.Any(character => character is '<' or '>' or '\uFFFD' || char.IsControl(character) && character != '\n');

    private static CombatTitleUnlockCatalog LoadEmbedded()
    {
        using var stream = typeof(CombatTitleUnlockCatalog).Assembly.GetManifestResourceStream("Soulcrest.CombatTitleUnlocks.json")
            ?? throw new InvalidDataException("Missing title unlock metadata.");
        using var reader = new StreamReader(stream);
        return FromJson(reader.ReadToEnd());
    }

    private sealed record CatalogFile
    {
        public required int Schema { get; init; }
        public required int MetadataBuild { get; init; }
        public required bool ReferenceOnly { get; init; }
        public required bool DoesNotEstablishAvailabilityOwnershipCompletionOrClaim { get; init; }
        public required CombatTitleUnlockDefinition[] Titles { get; init; }
    }
}

public sealed record CombatTitleUnlockDefinition(int Id, IReadOnlyList<int> DirectGrantItemIds,
    IReadOnlyList<CombatTitleUnlockRoute> Routes);

public sealed record CombatTitleUnlockRoute(string Kind, int SourceId, int? StageId, int ItemId,
    string? NameDe, string? NameEn, string? ConditionDe, string? ConditionEn);
