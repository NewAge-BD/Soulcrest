using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;
using Soulcrest.App.Services;

namespace Soulcrest.App.Components;

public enum CombatAchievementFaction { Unknown, All, Asmodian, Elyos }

public sealed record CombatAchievementFactionDefinition(int Id, CombatAchievementFaction Faction,
    IReadOnlyList<int> StageIds);

/// <summary>
/// Reference metadata for faction selection in the achievement browser only. This catalogue
/// never establishes availability, ownership, progress, completion, claims or tracking scope.
/// Unknown definitions remain visible for every faction.
/// </summary>
public sealed class CombatAchievementFactionCatalog
{
    private const int MaximumJsonCharacters = 4 * 1024 * 1024;
    private const int MaximumFamilies = 50000;
    private const int MaximumStages = 100000;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly Lazy<CombatAchievementFactionCatalog> Embedded = new(LoadEmbedded);
    private readonly FrozenDictionary<int, CombatAchievementFaction> _families;
    private readonly FrozenDictionary<int, CombatAchievementFaction> _stages;

    public static CombatAchievementFactionCatalog Default => Embedded.Value;
    public int MetadataBuild { get; }
    public IReadOnlyList<CombatAchievementFactionDefinition> Definitions { get; }

    private CombatAchievementFactionCatalog(int metadataBuild,
        IReadOnlyList<CombatAchievementFactionDefinition> definitions)
    {
        MetadataBuild = metadataBuild;
        Definitions = Array.AsReadOnly(definitions.ToArray());
        _families = definitions.ToFrozenDictionary(row => row.Id, row => row.Faction);
        _stages = definitions.SelectMany(row => row.StageIds.Select(id => (Id: id, row.Faction)))
            .ToFrozenDictionary(row => row.Id, row => row.Faction);
    }

    public CombatAchievementFaction FindFamily(int achievementId) =>
        _families.GetValueOrDefault(achievementId, CombatAchievementFaction.Unknown);

    public CombatAchievementFaction FindStage(int stageId) =>
        _stages.GetValueOrDefault(stageId, CombatAchievementFaction.Unknown);

    public bool IncludesFamily(int achievementId, string? faction) => Includes(FindFamily(achievementId), faction);

    public bool IncludesStage(int stageId, string? faction) => Includes(FindStage(stageId), faction);

    private static bool Includes(CombatAchievementFaction definition, string? faction) => faction switch
    {
        Factions.Elyos => definition != CombatAchievementFaction.Asmodian,
        Factions.Asmodian => definition != CombatAchievementFaction.Elyos,
        _ => true
    };

    internal static CombatAchievementFactionCatalog FromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumJsonCharacters)
            throw new InvalidDataException("Achievement faction metadata exceeds the size limit.");
        try
        {
            var file = JsonSerializer.Deserialize<CatalogFile>(json, Options);
            if (file is not { Schema: 1, MetadataBuild: > 0, ReferenceOnly: true,
                Families: { Length: > 0 and <= MaximumFamilies } })
                throw new InvalidDataException("Unsupported achievement faction metadata.");
            var familyIds = new HashSet<int>();
            var stageIds = new HashSet<int>();
            var definitions = new List<CombatAchievementFactionDefinition>();
            foreach (var row in file.Families)
            {
                if (row is null || row.Id <= 0 || !familyIds.Add(row.Id)
                    || row.StageIds is not { Length: > 0 and <= 10000 })
                    throw new InvalidDataException("Invalid or duplicate achievement faction definition.");
                var faction = row.Faction switch
                {
                    "Unknown" => CombatAchievementFaction.Unknown,
                    "All" => CombatAchievementFaction.All,
                    "Asmodian" => CombatAchievementFaction.Asmodian,
                    "Elyos" => CombatAchievementFaction.Elyos,
                    _ => throw new InvalidDataException("Unknown achievement faction metadata value.")
                };
                foreach (var stageId in row.StageIds)
                    if (stageId <= 0 || !stageIds.Add(stageId) || stageIds.Count > MaximumStages)
                        throw new InvalidDataException("Invalid or duplicate achievement faction stage.");
                definitions.Add(new(row.Id, faction, Array.AsReadOnly(row.StageIds.ToArray())));
            }
            return new(file.MetadataBuild, definitions);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Invalid achievement faction metadata.", error);
        }
    }

    private static CombatAchievementFactionCatalog LoadEmbedded()
    {
        using var stream = typeof(CombatAchievementFactionCatalog).Assembly
            .GetManifestResourceStream("Soulcrest.CombatAchievementFactions.json")
            ?? throw new InvalidDataException("Missing achievement faction metadata.");
        using var reader = new StreamReader(stream);
        return FromJson(reader.ReadToEnd());
    }

    private sealed record CatalogFile
    {
        public required int Schema { get; init; }
        public required int MetadataBuild { get; init; }
        public required bool ReferenceOnly { get; init; }
        public required FamilyRow?[] Families { get; init; }
    }

    private sealed record FamilyRow
    {
        public required int Id { get; init; }
        public required string Faction { get; init; }
        public required int[] StageIds { get; init; }
    }
}
