using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;
using Soulcrest.App.Components;

namespace Soulcrest.App.Services;

public enum AchievementProgressCounterSemantics { Cumulative, CurrentState }
public enum AchievementProgressPresentation { Count, Requirement }

public sealed record AchievementProgressThresholdStage(int Id, long Target)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    [JsonConverter(typeof(JsonStringEnumConverter<AchievementProgressPresentation>))]
    public AchievementProgressPresentation Presentation { get; init; } = AchievementProgressPresentation.Count;
}
public sealed record AchievementProgressThresholdFamily(int Id, string DefinitionSignature,
    IReadOnlyList<AchievementProgressThresholdStage> Stages)
{
    public AchievementProgressCounterSemantics CounterSemantics { get; init; } = AchievementProgressCounterSemantics.Cumulative;
    public bool ModelCurrentState => CounterSemantics == AchievementProgressCounterSemantics.CurrentState;
}

/// <summary>
/// Build- and operand-bound raw Count goals for explicit complete normal chains. This supplies no
/// reward claim, actor binding or period/reset authority. Measured unit contracts remain primary.
/// </summary>
public sealed class AchievementProgressThresholdCatalog
{
    private const int MaximumJsonCharacters = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly Lazy<AchievementProgressThresholdCatalog> Embedded = new(LoadEmbedded);
    private readonly FrozenDictionary<int, AchievementProgressThresholdFamily> _families;
    public static AchievementProgressThresholdCatalog Default => Embedded.Value;
    public int MetadataBuild { get; }
    public IReadOnlyList<AchievementProgressThresholdFamily> Families { get; }
    public AchievementProgressThresholdFamily? FindFamily(int familyId) => _families.GetValueOrDefault(familyId);

    private AchievementProgressThresholdCatalog(int build, IEnumerable<AchievementProgressThresholdFamily> families)
    {
        MetadataBuild = build;
        Families = Array.AsReadOnly(families.ToArray());
        _families = Families.ToFrozenDictionary(family => family.Id);
    }

    internal static AchievementProgressThresholdCatalog FromJson(string json,
        AchievementDefinitionCatalog? definitions = null, CombatAchievementDisplayCatalog? display = null)
    {
        definitions ??= AchievementDefinitionCatalog.Default;
        display ??= CombatAchievementDisplayCatalog.Default;
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumJsonCharacters)
            throw new InvalidDataException("Achievement progress thresholds exceed the size limit.");
        try
        {
            var file = JsonSerializer.Deserialize<ThresholdFile>(json, Options);
            if (file is not { Schema: 1, MetadataBuild: > 0, Families: { Length: > 0 and <= 50000 } }
                || file.MetadataBuild != definitions.MetadataBuild || file.MetadataBuild != display.MetadataBuild)
                throw new InvalidDataException("Unsupported or mismatched achievement progress thresholds.");
            var familyIds = new HashSet<int>();
            var stageIds = new HashSet<int>();
            var families = new List<AchievementProgressThresholdFamily>();
            foreach (var row in file.Families)
            {
                if (row is null || row.Id <= 0 || !familyIds.Add(row.Id)
                    || definitions.Find(row.Id) is not { Area: AchievementArea.Normal, Role: AchievementRole.PlayerFacing } definition
                    || display.Find(row.Id) is not { HasNativeName: true } shown
                    || row.DefinitionSignature != definition.Signature
                    || row.Stages is not { Length: > 0 and <= 10000 }
                    || row.Stages.Any(stage => stage is null)
                    || !row.Stages.Select(stage => stage.Id).SequenceEqual(definition.StageIds)
                    || !row.Stages.Select(stage => stage.Id).SequenceEqual(shown.Stages.Select(stage => stage.Id))
                    || shown.UnknownStageIds.Count != 0)
                    throw new InvalidDataException("Invalid achievement progress threshold family or stage membership.");
                var semantics = row.CounterSemantics switch
                {
                    "Cumulative" => AchievementProgressCounterSemantics.Cumulative,
                    "CurrentState" => AchievementProgressCounterSemantics.CurrentState,
                    _ => throw new InvalidDataException("Unqualified achievement progress counter semantics.")
                };
                var stages = new List<AchievementProgressThresholdStage>();
                foreach (var stage in row.Stages)
                {
                    if (stage.Id <= 0 || !stageIds.Add(stage.Id) || stageIds.Count > 100000 || stage.Target <= 0
                        || stages.Count > 0 && stage.Target <= stages[^1].Target)
                        throw new InvalidDataException("Invalid or unordered achievement progress threshold.");
                    var presentation = stage.Presentation switch
                    {
                        "Count" => AchievementProgressPresentation.Count,
                        "Requirement" when semantics == AchievementProgressCounterSemantics.CurrentState
                            => AchievementProgressPresentation.Requirement,
                        _ => throw new InvalidDataException("Unqualified achievement progress presentation.")
                    };
                    stages.Add(new(stage.Id, stage.Target) { Presentation = presentation });
                }
                families.Add(new(row.Id, row.DefinitionSignature, Array.AsReadOnly(stages.ToArray()))
                { CounterSemantics = semantics });
            }
            return new(file.MetadataBuild, families);
        }
        catch (JsonException error) { throw new InvalidDataException("Invalid achievement progress threshold data.", error); }
    }

    private static AchievementProgressThresholdCatalog LoadEmbedded()
    {
        using var stream = typeof(AchievementProgressThresholdCatalog).Assembly
            .GetManifestResourceStream("Soulcrest.AchievementProgressThresholds.json")
            ?? throw new InvalidDataException("Missing achievement progress thresholds.");
        using var reader = new StreamReader(stream);
        return FromJson(reader.ReadToEnd());
    }

    private sealed record ThresholdFile
    {
        public required int Schema { get; init; }
        public required int MetadataBuild { get; init; }
        public required ThresholdFamily[] Families { get; init; }
    }
    private sealed record ThresholdFamily(int Id, string DefinitionSignature, ThresholdStage[] Stages)
    {
        public string CounterSemantics { get; init; } = "Cumulative";
    }
    private sealed record ThresholdStage(int Id, long Target)
    {
        public string Presentation { get; init; } = "Count";
    }
}
