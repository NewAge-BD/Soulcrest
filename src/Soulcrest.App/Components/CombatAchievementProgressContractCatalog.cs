using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Soulcrest.App.Components;

public enum CombatAchievementCounterRule { Cumulative, Unqualified }
public enum CombatAchievementCompletionRule { AtLeastTarget, FromClaim }
public enum CombatAchievementClaimRule { FirstRewardOrdinal, Unqualified, ContiguousRewardOrdinal }
public enum CombatAchievementProgressUnit { Count, Level, Minutes, Unknown, Seconds, Items, Operations, Experience, Rank, Percent }
public sealed record CombatAchievementStageContract(int Id, int Ordinal, long? Target);
public sealed record CombatAchievementFamilyContract(int Id, IReadOnlyList<CombatAchievementStageContract> Stages)
{
    internal bool HasCompleteStageScope { get; init; }
}
public sealed record CombatAchievementTypeContract(string Id, CombatAchievementCounterRule CounterRule,
    CombatAchievementProgressUnit Unit, CombatAchievementCompletionRule CompletionRule, CombatAchievementClaimRule ClaimRule,
    IReadOnlyList<CombatAchievementFamilyContract> Families);

/// <summary>Finite, versioned rules with explicit native family/stage membership. Labels never select a rule.</summary>
public sealed class CombatAchievementProgressContractCatalog
{
    private const int MaximumJsonCharacters = 16000000;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly Lazy<CombatAchievementProgressContractCatalog> Embedded = new(LoadEmbedded);
    private readonly FrozenDictionary<int, CombatAchievementTypeContract> _familyTypes;
    public static CombatAchievementProgressContractCatalog Default => Embedded.Value;
    public int MetadataBuild { get; }
    public IReadOnlyList<CombatAchievementTypeContract> Contracts { get; }
    public CombatAchievementTypeContract? FindFamily(int familyId) => _familyTypes.GetValueOrDefault(familyId);

    private CombatAchievementProgressContractCatalog(int build, IReadOnlyList<CombatAchievementTypeContract> contracts,
        IDictionary<int, CombatAchievementTypeContract> familyTypes)
    {
        MetadataBuild = build; Contracts = contracts; _familyTypes = familyTypes.ToFrozenDictionary();
    }

    internal static CombatAchievementProgressContractCatalog FromJson(string json, CombatAchievementDisplayCatalog displayCatalog)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumJsonCharacters) throw new InvalidDataException("Achievement progress contracts exceed the size limit.");
        try
        {
            var file = JsonSerializer.Deserialize<ContractFile>(json, Options);
            if (file is not { Schema: 2, MetadataBuild: > 0, Contracts: { Length: > 0 and <= 256 } }
                || file.MetadataBuild != displayCatalog.MetadataBuild)
                throw new InvalidDataException("Unsupported or mismatched achievement progress contracts.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var familyIds = new HashSet<int>(); var stageIds = new HashSet<int>();
            var contracts = new List<CombatAchievementTypeContract>();
            var familyTypes = new Dictionary<int, CombatAchievementTypeContract>();
            foreach (var type in file.Contracts)
            {
                if (type is null || string.IsNullOrEmpty(type.Id) || type.Id.Length > 64
                    || !type.Id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
                    || !ids.Add(type.Id) || type.Families is not { Length: > 0 and <= 50000 })
                    throw new InvalidDataException("Invalid or duplicate achievement type contract.");
                var counter = type.CounterRule switch { "Cumulative" => CombatAchievementCounterRule.Cumulative,
                    "Unqualified" => CombatAchievementCounterRule.Unqualified, _ => throw new InvalidDataException("Unqualified achievement counter rule.") };
                var completion = type.CompletionRule switch { "AtLeastTarget" => CombatAchievementCompletionRule.AtLeastTarget,
                    "FromClaim" => CombatAchievementCompletionRule.FromClaim, _ => throw new InvalidDataException("Unqualified achievement completion rule.") };
                var unit = type.Unit switch { "Count" => CombatAchievementProgressUnit.Count, "Level" => CombatAchievementProgressUnit.Level,
                    "Minutes" => CombatAchievementProgressUnit.Minutes, "Unknown" => CombatAchievementProgressUnit.Unknown, _ => throw new InvalidDataException("Unqualified achievement unit.") };
                var claim = type.ClaimRule switch { "FirstRewardOrdinal" => CombatAchievementClaimRule.FirstRewardOrdinal,
                    "Unqualified" => CombatAchievementClaimRule.Unqualified, "ContiguousRewardOrdinal" => CombatAchievementClaimRule.ContiguousRewardOrdinal,
                    _ => throw new InvalidDataException("Unqualified achievement claim rule.") };
                var counterQualified = counter == CombatAchievementCounterRule.Cumulative
                    && completion == CombatAchievementCompletionRule.AtLeastTarget && unit != CombatAchievementProgressUnit.Unknown;
                var claimOnly = counter == CombatAchievementCounterRule.Unqualified
                    && completion == CombatAchievementCompletionRule.FromClaim && unit == CombatAchievementProgressUnit.Unknown
                    && claim != CombatAchievementClaimRule.Unqualified;
                if (!counterQualified && !claimOnly) throw new InvalidDataException("Incompatible achievement progress rule combination.");
                var families = new List<CombatAchievementFamilyContract>();
                foreach (var family in type.Families)
                {
                    if (family is null || family.Id <= 0 || !familyIds.Add(family.Id) || familyIds.Count > 50000
                        || displayCatalog.Find(family.Id) is not { } definition || family.Stages is not { Length: > 0 and <= 10000 })
                        throw new InvalidDataException("Invalid, duplicate or unresolved achievement family.");
                    var members = definition.Stages.Select(stage => stage.Id).Concat(definition.UnknownStageIds).ToHashSet();
                    var stages = new List<CombatAchievementStageContract>();
                    for (var i = 0; i < family.Stages.Length; i++)
                    {
                        var stage = family.Stages[i];
                        if (stage is null || stage.Id <= 0 || !members.Contains(stage.Id) || !stageIds.Add(stage.Id)
                            || stageIds.Count > 100000 || stage.Ordinal != i + 1
                            || claimOnly && stage.Target is not null
                            || counterQualified && (stage.Target is not > 0 || i > 0 && stage.Target!.Value <= family.Stages[i - 1].Target!.Value))
                            throw new InvalidDataException("Invalid, duplicate or unresolved achievement stage.");
                        stages.Add(new(stage.Id, stage.Ordinal, stage.Target));
                    }
                    var completeScope = members.SetEquals(stages.Select(stage => stage.Id));
                    if ((claimOnly || claim == CombatAchievementClaimRule.ContiguousRewardOrdinal) && !completeScope)
                        throw new InvalidDataException("Qualified reward-prefix chains must cover every native stage.");
                    families.Add(new(family.Id, Array.AsReadOnly(stages.ToArray())) { HasCompleteStageScope = completeScope });
                }
                var contract = new CombatAchievementTypeContract(type.Id, counter, unit, completion, claim,
                    Array.AsReadOnly(families.ToArray()));
                contracts.Add(contract);
                foreach (var family in families) familyTypes.Add(family.Id, contract);
            }
            return new(file.MetadataBuild, Array.AsReadOnly(contracts.ToArray()), familyTypes);
        }
        catch (JsonException error) { throw new InvalidDataException("Invalid achievement progress contract data.", error); }
    }

    private static CombatAchievementProgressContractCatalog LoadEmbedded()
    {
        using var stream = typeof(CombatAchievementProgressContractCatalog).Assembly.GetManifestResourceStream("Soulcrest.CombatAchievementProgressContracts.json")
            ?? throw new InvalidDataException("Missing achievement progress type contracts.");
        using var reader = new StreamReader(stream);
        return FromJson(reader.ReadToEnd(), CombatAchievementDisplayCatalog.Default);
    }

    private sealed record ContractFile
    {
        public required int Schema { get; init; }
        public required int MetadataBuild { get; init; }
        public required TypeFile[] Contracts { get; init; }
    }
    private sealed record TypeFile
    {
        public required string Id { get; init; }
        public required string CounterRule { get; init; }
        public required string Unit { get; init; }
        public required string CompletionRule { get; init; }
        public required string ClaimRule { get; init; }
        public required FamilyFile[] Families { get; init; }
    }
    private sealed record FamilyFile(int Id, StageFile[] Stages);
    private sealed record StageFile(int Id, int Ordinal, long? Target);
}
