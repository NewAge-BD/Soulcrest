using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Soulcrest.App.Services;

public enum AchievementArea { Normal, Season, Event, BattlePass, Attendance, Onboarding, Reference }
public enum AchievementRole { PlayerFacing, Candidate, Reference }

/// <summary>
/// Versioned definition metadata only. Area, role and semantic group do not establish
/// current eligibility, character ownership, counter units, completion or reward claims.
/// Stage IDs retain their explicit source order without assigning runtime claim semantics.
/// </summary>
public sealed record AchievementDefinitionInfo(int Id, AchievementArea Area, AchievementRole Role,
    string SemanticGroup, string ResetPolicy, string Signature, IReadOnlyList<int> StageIds, string Reason);

/// <summary>
/// Immutable classification of every bundled definition. Native operands are represented
/// only by an opaque metadata fingerprint; private resource keys and provenance are absent.
/// </summary>
public sealed class AchievementDefinitionCatalog
{
    private const int MaximumJsonCharacters = 8 * 1024 * 1024;
    private const int MaximumDefinitions = 50000;
    private const int MaximumStages = 100000;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly Lazy<AchievementDefinitionCatalog> Embedded = new(LoadEmbedded);
    private readonly FrozenDictionary<int, AchievementDefinitionInfo> _definitions;

    public static AchievementDefinitionCatalog Default => Embedded.Value;
    public int MetadataBuild { get; }
    public IReadOnlyList<AchievementDefinitionInfo> Definitions { get; }
    public AchievementDefinitionInfo? Find(int achievementId) => _definitions.GetValueOrDefault(achievementId);

    private AchievementDefinitionCatalog(int metadataBuild, IReadOnlyList<AchievementDefinitionInfo> definitions)
    {
        MetadataBuild = metadataBuild;
        Definitions = Array.AsReadOnly(definitions.ToArray());
        _definitions = definitions.ToFrozenDictionary(definition => definition.Id);
    }

    internal static AchievementDefinitionCatalog FromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumJsonCharacters)
            throw new InvalidDataException("Achievement definition metadata exceeds the size limit.");
        try
        {
            var file = JsonSerializer.Deserialize<DefinitionFile>(json, Options);
            if (file is not { Schema: 1, MetadataBuild: > 0, Definitions: { Length: > 0 and <= MaximumDefinitions } })
                throw new InvalidDataException("Unsupported achievement definition metadata.");
            var familyIds = new HashSet<int>();
            var stageIds = new HashSet<int>();
            var definitions = new List<AchievementDefinitionInfo>();
            foreach (var row in file.Definitions)
            {
                if (row is null || row.Id <= 0 || !familyIds.Add(row.Id)
                    || !ValidKey(row.SemanticGroup) || !ValidKey(row.Reason)
                    || row.Signature is not { Length: 64 } || !row.Signature.All(IsLowerHex)
                    || row.ResetPolicy is not ("None" or "Daily" or "Weekly")
                    || row.StageIds is not { Length: > 0 and <= 10000 })
                    throw new InvalidDataException("Invalid or duplicate achievement definition metadata.");
                var area = row.Area switch
                {
                    "Normal" => AchievementArea.Normal, "Season" => AchievementArea.Season,
                    "Event" => AchievementArea.Event, "BattlePass" => AchievementArea.BattlePass,
                    "Attendance" => AchievementArea.Attendance, "Onboarding" => AchievementArea.Onboarding,
                    "Reference" => AchievementArea.Reference,
                    _ => throw new InvalidDataException("Unknown achievement definition area.")
                };
                var role = row.Role switch
                {
                    "PlayerFacing" => AchievementRole.PlayerFacing, "Candidate" => AchievementRole.Candidate,
                    "Reference" => AchievementRole.Reference,
                    _ => throw new InvalidDataException("Unknown achievement definition role.")
                };
                // Module classifications remain candidates. Reference definitions never acquire
                // a player-facing role merely because they have a name or an observed wire row.
                if (role == AchievementRole.PlayerFacing && area != AchievementArea.Normal
                    || area == AchievementArea.Reference && role != AchievementRole.Reference)
                    throw new InvalidDataException("Incompatible achievement definition area and role.");
                foreach (var stageId in row.StageIds)
                    if (stageId <= 0 || !stageIds.Add(stageId) || stageIds.Count > MaximumStages)
                        throw new InvalidDataException("Invalid or duplicate achievement definition stage.");
                definitions.Add(new(row.Id, area, role, row.SemanticGroup, row.ResetPolicy, row.Signature,
                    Array.AsReadOnly(row.StageIds.ToArray()), row.Reason));
            }
            return new(file.MetadataBuild, definitions);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Invalid achievement definition metadata.", error);
        }
    }

    private static bool IsLowerHex(char character) => character is >= '0' and <= '9' or >= 'a' and <= 'f';
    private static bool ValidKey(string? key) => !string.IsNullOrEmpty(key) && key.Length <= 64
        && key.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '-');

    private static AchievementDefinitionCatalog LoadEmbedded()
    {
        using var stream = typeof(AchievementDefinitionCatalog).Assembly
            .GetManifestResourceStream("Soulcrest.AchievementDefinitionScopes.json")
            ?? throw new InvalidDataException("Missing achievement definition metadata.");
        using var reader = new StreamReader(stream);
        return FromJson(reader.ReadToEnd());
    }

    private sealed record DefinitionFile
    {
        public required int Schema { get; init; }
        public required int MetadataBuild { get; init; }
        public required DefinitionRow?[] Definitions { get; init; }
    }
    private sealed record DefinitionRow
    {
        public required int Id { get; init; }
        public required string Area { get; init; }
        public required string Role { get; init; }
        public required string SemanticGroup { get; init; }
        public required string ResetPolicy { get; init; }
        public required string Signature { get; init; }
        public required int[] StageIds { get; init; }
        public required string Reason { get; init; }
    }
}
