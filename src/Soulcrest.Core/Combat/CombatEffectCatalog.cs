using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;
using Soulcrest.Core.Daevanion;

namespace Soulcrest.Core.Combat;

public sealed record CombatStatDefinition
{
    public string StatId { get; init; } = "";
    public int? NativeId { get; init; }
    public decimal RawDivisor { get; init; } = 1;
    public decimal? NativeRawDivisor { get; init; }
    /// <summary>The legacy scalar fields describe CharacterStats only. Role is never inferred from a stat name.</summary>
    public CombatNativeStatRole NativeRole { get; init; } = CombatNativeStatRole.Unknown;
    public IReadOnlyList<CombatNativeDomainDefinition> NativeDomains { get; init; } = [];
    public CombatStatUnit Unit { get; init; }
    public CombatRounding Rounding { get; init; } = CombatRounding.Floor;
    public string? NameEn { get; init; }
    public string? NameDe { get; init; }
}
public sealed record CombatNativeDomainDefinition(CombatNativeValueDomain Domain, decimal? RawDivisor = null,
    CombatNativeStatRole Role = CombatNativeStatRole.Unknown);
public sealed record CombatNativeNormalizedValue(int NativeStatId, string StatId, CombatNativeValueDomain Domain,
    CombatNativeStatRole Role, decimal Value, CombatStatUnit Unit);
public sealed record CombatNativeForwardRule(string Id, CombatNativeValueDomain Domain, string OutputStatId,
    IReadOnlyList<int> FlatInputs, IReadOnlyList<int> IncreasePercentInputs, CombatRounding Rounding);
public sealed record CombatScoreRule(string StatId, decimal Coefficient, CombatStatUnit Unit);
public sealed record CombatStatRelation(string ModifierStatId, string StatId,
    CombatEffectOperation Operation = CombatEffectOperation.IncreasePercent);
public sealed record CombatCurveEffect(string StatId, decimal RawValue,
    CombatEffectOperation Operation = CombatEffectOperation.Flat);
public sealed record CombatPrimaryLevel(int Level, IReadOnlyList<CombatCurveEffect> Effects);
public sealed record CombatPrimaryCurve(string PrimaryStatId, IReadOnlyList<CombatPrimaryLevel> Values);
public sealed record CombatPassiveRule
{
    public int SkillId { get; init; }
    public string EffectId { get; init; } = "";
    public string StatId { get; init; } = "";
    public CombatEffectOperation Operation { get; init; }
    public decimal Start { get; init; }
    public decimal Step { get; init; }
    public int MinLevel { get; init; } = 1;
    public int MaxLevel { get; init; } = 40;
    public IReadOnlyList<decimal> Values { get; init; } = [];
    public CombatApplication Application { get; init; } = CombatApplication.Unknown;
    public string? ApplicationEvidence { get; init; }
    public int? AbnormalId { get; init; }
    public bool HasUnconditionalPassiveChain { get; init; }
    public IReadOnlyList<string> AllowedWeaponTypes { get; init; } = [];
    public string RuleId => "passive:" + SkillId + ":" + EffectId + ":" + StatId;

    public decimal? ValueAt(int level)
    {
        if (level == 0) return 0;
        if (level < MinLevel || level > MaxLevel) return null;
        if (Values.Count != 0) return level < Values.Count ? Values[level] : null;
        return Start + Step * (level - MinLevel);
    }
}
public sealed record CombatCatalogSource
{
    public CombatSourceKind SourceKind { get; init; }
    public string SourceId { get; init; } = "";
    public string Channel { get; init; } = "";
    public IReadOnlyList<string> Aliases { get; init; } = [];
    public CombatApplication Application { get; init; } = CombatApplication.Unknown;
    public IReadOnlyList<CombatEffect> Effects { get; init; } = [];
}
public sealed record CombatCatalogCoverage(CombatSourceKind Kind, CombatKnowledgeStatus Status, string Reason);
public sealed record CombatBaseLevel(string ClassId, int Level, IReadOnlyList<CombatEffect> Effects);
public sealed record CombatCatalogSkill(int SkillId, string ClassId, DaevanionSkillKind Kind,
    string? NameEn = null, string? NameDe = null);
public sealed record CombatPetLevelBinding(int PetNumber, int Level, string SourceId,
    CombatApplication Application = CombatApplication.Unknown, string? ApplicationEvidence = null);
public sealed record CombatNativeExpPointLevel(int Level, int NormalBasePoints);
public enum CombatBudgetContractStatus { Unknown, QualifiedBoundedInitialGrowth45 }
public sealed record CombatNativeExpPointQualification
{
    public string Version { get; init; } = "";
    public int ClientBuild { get; init; }
    public string NormalBudgetContractVersion { get; init; } = "";
    public IReadOnlyList<int> NormalBudgetQualifiedLevels { get; init; } = [];
    public string SpecialBudgetContractVersion { get; init; } = "";
    public IReadOnlyList<int> SpecialBudgetQualifiedLevels { get; init; } = [];
    public int? RequiredCombatLevel { get; init; }
    public int? RequiredConquerorLevel { get; init; }
    public CombatBudgetContractStatus BudgetContractStatus { get; init; }
    public CombatKnowledgeStatus SpecialPoolsStatus { get; init; } = CombatKnowledgeStatus.Unknown;
    public bool IsNativeFreeWallet { get; init; }
}

/// <summary>Offline facts only. A numeric effect and its application qualification remain separate.</summary>
public sealed class CombatEffectCatalog
{
    private static readonly Lazy<CombatEffectCatalog> Embedded = new(LoadEmbedded);
    private readonly FrozenDictionary<string, CombatStatDefinition> _stats;
    private readonly FrozenDictionary<int, CombatStatDefinition> _nativeStats;
    private readonly FrozenDictionary<(CombatSourceKind Kind, string Id, string Channel), CombatCatalogSource> _sourceEffects;
    private readonly FrozenDictionary<(string StatId, int Level), CombatPrimaryLevel> _primaryLevels;
    private readonly FrozenDictionary<int, CombatNativeExpPointLevel> _expPoints;
    public static CombatEffectCatalog Default => Embedded.Value;
    public string Version { get; }
    public int ClientBuild { get; }
    public IReadOnlyList<CombatStatDefinition> StatDefinitions { get; }
    public IReadOnlyList<CombatScoreRule> ScoreRules { get; }
    public IReadOnlyList<CombatPrimaryCurve> PrimaryStatCurves { get; }
    public IReadOnlySet<string> PrimaryStatIds { get; }
    public IReadOnlyList<CombatPassiveRule> PassiveEffects { get; }
    public IReadOnlyList<CombatCatalogSource> SourceEffects { get; }
    public IReadOnlyList<CombatCatalogCoverage> SourceCoverage { get; }
    public IReadOnlyList<CombatStatRelation> StatRelations { get; }
    public IReadOnlyList<CombatBaseLevel> BaseLevelTable { get; }
    public IReadOnlyList<CombatCatalogSkill> Skills { get; }
    public IReadOnlyList<CombatPetLevelBinding> PetLevelBindings { get; }
    public IReadOnlyList<CombatNativeExpPointLevel> NativeExpPointLevels { get; }
    public CombatNativeExpPointQualification NativeExpPointQualification { get; }
    /// <summary>No native-component-to-display-total rule has been qualified for this projection yet.</summary>
    public IReadOnlyList<CombatNativeForwardRule> NativeForwardRules { get; } = Array.AsReadOnly(Array.Empty<CombatNativeForwardRule>());

    public CombatEffectCatalog(string version, int clientBuild, IReadOnlyList<CombatStatDefinition> statDefinitions,
        IReadOnlyList<CombatScoreRule>? scoreRules = null, IReadOnlyList<CombatPrimaryCurve>? primaryStatCurves = null,
        IReadOnlyList<CombatPassiveRule>? passiveEffects = null, IReadOnlyList<CombatCatalogSource>? sourceEffects = null,
        IReadOnlyList<CombatCatalogCoverage>? sourceCoverage = null, IReadOnlyList<CombatStatRelation>? statRelations = null,
        IReadOnlyList<CombatBaseLevel>? baseLevelTable = null, IReadOnlyList<CombatCatalogSkill>? skills = null,
        IReadOnlyList<CombatPetLevelBinding>? petLevelBindings = null,
        IReadOnlyList<CombatNativeExpPointLevel>? nativeExpPointLevels = null,
        CombatNativeExpPointQualification? nativeExpPointQualification = null)
    {
        if (string.IsNullOrWhiteSpace(version)) throw new ArgumentException("Missing catalog version.", nameof(version));
        Version = version; ClientBuild = clientBuild;
        StatDefinitions = Array.AsReadOnly(statDefinitions.Select(row => row with
            { NativeDomains = Array.AsReadOnly(row.NativeDomains.ToArray()) }).ToArray());
        _stats = StatDefinitions.ToFrozenDictionary(row => row.StatId, StringComparer.Ordinal);
        _nativeStats = StatDefinitions.Where(row => row.NativeId is not null).ToFrozenDictionary(row => row.NativeId!.Value);
        if (StatDefinitions.Any(row => string.IsNullOrWhiteSpace(row.StatId) || row.RawDivisor <= 0 || row.NativeRawDivisor is <= 0
                || !Enum.IsDefined(row.NativeRole) || row.NativeRole == CombatNativeStatRole.IncreasePercentComponent && row.Unit != CombatStatUnit.Percent
                || row.NativeDomains.Select(domain => domain.Domain).Distinct().Count() != row.NativeDomains.Count
                || row.NativeDomains.Any(domain => !Enum.IsDefined(domain.Domain) || !Enum.IsDefined(domain.Role) || domain.RawDivisor is <= 0
                    || domain.Role == CombatNativeStatRole.IncreasePercentComponent && row.Unit != CombatStatUnit.Percent)))
            throw new InvalidDataException("Invalid stat definition or scale.");
        ScoreRules = Array.AsReadOnly((scoreRules ?? []).ToArray());
        if (ScoreRules.Any(row => !_stats.ContainsKey(row.StatId)) || ScoreRules.Select(row => row.StatId).Distinct().Count() != ScoreRules.Count)
            throw new InvalidDataException("Invalid or duplicate combat score rule.");
        PrimaryStatCurves = Array.AsReadOnly((primaryStatCurves ?? []).Select(curve => curve with
        {
            Values = Array.AsReadOnly(curve.Values.Select(level => level with { Effects = Array.AsReadOnly(level.Effects.ToArray()) }).ToArray())
        }).ToArray());
        _primaryLevels = PrimaryStatCurves.SelectMany(curve => curve.Values.Select(level =>
            new KeyValuePair<(string, int), CombatPrimaryLevel>((curve.PrimaryStatId, level.Level), level))).ToFrozenDictionary();
        PrimaryStatIds = PrimaryStatCurves.Select(curve => curve.PrimaryStatId).ToFrozenSet(StringComparer.Ordinal);
        PassiveEffects = Array.AsReadOnly((passiveEffects ?? []).Select(row => row with
        {
            Values = Array.AsReadOnly(row.Values.ToArray()), AllowedWeaponTypes = Array.AsReadOnly(row.AllowedWeaponTypes.ToArray())
        }).ToArray());
        SourceEffects = Array.AsReadOnly((sourceEffects ?? []).Select(row => row with
        {
            Effects = Array.AsReadOnly(row.Effects.ToArray()), Aliases = Array.AsReadOnly(row.Aliases.ToArray())
        }).ToArray());
        _sourceEffects = SourceEffects.SelectMany(row => row.Aliases.Prepend(row.SourceId)
            .Select(id => new KeyValuePair<(CombatSourceKind, string, string), CombatCatalogSource>((row.SourceKind, id, row.Channel), row)))
            .ToFrozenDictionary();
        SourceCoverage = Array.AsReadOnly((sourceCoverage ?? []).ToArray());
        StatRelations = Array.AsReadOnly((statRelations ?? []).ToArray());
        BaseLevelTable = Array.AsReadOnly((baseLevelTable ?? []).Select(row => row with { Effects = Array.AsReadOnly(row.Effects.ToArray()) }).ToArray());
        Skills = Array.AsReadOnly((skills ?? []).ToArray());
        PetLevelBindings = Array.AsReadOnly((petLevelBindings ?? []).ToArray());
        NativeExpPointLevels = Array.AsReadOnly((nativeExpPointLevels ?? []).ToArray());
        _expPoints = NativeExpPointLevels.ToFrozenDictionary(row => row.Level);
        var pointQualification = nativeExpPointQualification ?? new();
        NativeExpPointQualification = pointQualification with
        {
            NormalBudgetQualifiedLevels = Array.AsReadOnly(pointQualification.NormalBudgetQualifiedLevels.ToArray()),
            SpecialBudgetQualifiedLevels = Array.AsReadOnly(pointQualification.SpecialBudgetQualifiedLevels.ToArray())
        };
        if (NativeExpPointLevels.Any(row => row.Level < 1 || row.NormalBasePoints < 0)
            || !Enum.IsDefined(NativeExpPointQualification.SpecialPoolsStatus)
            || !Enum.IsDefined(NativeExpPointQualification.BudgetContractStatus)
            || NativeExpPointQualification.NormalBudgetQualifiedLevels.Concat(NativeExpPointQualification.SpecialBudgetQualifiedLevels)
                .Any(level => !_expPoints.ContainsKey(level)))
            throw new InvalidDataException("Invalid experience point curve or qualification scope.");
        if (NativeExpPointQualification.BudgetContractStatus == CombatBudgetContractStatus.QualifiedBoundedInitialGrowth45
            && (NativeExpPointQualification.ClientBuild != ClientBuild || ClientBuild != 26262663
                || !NativeExpPointQualification.NormalBudgetQualifiedLevels.SequenceEqual([45])
                || !NativeExpPointQualification.SpecialBudgetQualifiedLevels.SequenceEqual([45])
                || NativeExpPointQualification.RequiredCombatLevel != 1 || NativeExpPointQualification.RequiredConquerorLevel != 0
                || NativeExpPointQualification.IsNativeFreeWallet || NativeExpPointQualification.SpecialPoolsStatus != CombatKnowledgeStatus.Complete
                || string.IsNullOrWhiteSpace(NativeExpPointQualification.NormalBudgetContractVersion)
                || string.IsNullOrWhiteSpace(NativeExpPointQualification.SpecialBudgetContractVersion)))
            throw new InvalidDataException("The bounded point-budget contract requires its exact qualified build and growth scope.");
        if (PrimaryStatCurves.Any(curve => !_stats.ContainsKey(curve.PrimaryStatId)
                || curve.Values.Select(value => value.Level).Distinct().Count() != curve.Values.Count
                || curve.Values.SelectMany(value => value.Effects).Any(effect => !_stats.ContainsKey(effect.StatId)))
            || PassiveEffects.Any(row => !_stats.ContainsKey(row.StatId) || row.MinLevel < 1 || row.MaxLevel < row.MinLevel)
            || StatRelations.Any(row => !_stats.ContainsKey(row.ModifierStatId) || !_stats.ContainsKey(row.StatId)))
            throw new InvalidDataException("Invalid derived effect catalog.");
    }

    public CombatStatDefinition? GetStat(string id) => _stats.GetValueOrDefault(id);
    public CombatStatDefinition? GetNativeStat(int id) => _nativeStats.GetValueOrDefault(id);
    public CombatPrimaryLevel? GetPrimaryLevel(string id, int level) => _primaryLevels.GetValueOrDefault((id, level));
    public int? NormalBasePointsAt(int level) => _expPoints.GetValueOrDefault(level)?.NormalBasePoints;
    public CombatNativeNormalizedValue? GetNativeNormalized(int id, long value,
        CombatNativeValueDomain domain = CombatNativeValueDomain.CharacterStats)
    {
        if (!Enum.IsDefined(domain)) throw new ArgumentException("Unknown native stat domain.", nameof(domain));
        if (GetNativeStat(id) is not { } stat) return null;
        var binding = stat.NativeDomains.SingleOrDefault(binding => binding.Domain == domain);
        if (binding is null && domain == CombatNativeValueDomain.CharacterStats && stat.NativeRawDivisor is { } characterDivisor)
            binding = new(domain, characterDivisor, stat.NativeRole);
        return binding?.RawDivisor is { } divisor
            ? new(id, stat.StatId, domain, binding.Role, value / divisor, stat.Unit) : null;
    }
    public decimal? NormalizeNative(int id, long value,
        CombatNativeValueDomain domain = CombatNativeValueDomain.CharacterStats) => GetNativeNormalized(id, value, domain)?.Value;
    public decimal? NormalizeEffect(string id, decimal value) => GetStat(id) is { } stat ? value / stat.RawDivisor : null;
    public CombatCatalogSource? GetSource(CombatSourceKind kind, string id, string channel) => _sourceEffects.GetValueOrDefault((kind, id, channel));
    public CombatCatalogSkill? GetSkill(int id) => Skills.SingleOrDefault(skill => skill.SkillId == id);
    public string StatLabel(string id, string language) => GetStat(id) is { } stat
        ? (language.StartsWith("de", StringComparison.OrdinalIgnoreCase) ? stat.NameDe : stat.NameEn) ?? id : id;
    public string SkillLabel(int id, string language) => GetSkill(id) is { } skill
        ? (language.StartsWith("de", StringComparison.OrdinalIgnoreCase) ? skill.NameDe : skill.NameEn) ?? id.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static CombatEffectCatalog LoadEmbedded()
    {
        using var stream = typeof(CombatEffectCatalog).Assembly.GetManifestResourceStream("Soulcrest.CombatCatalog.json")
            ?? throw new InvalidDataException("Missing offline combat effect catalog.");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new JsonStringEnumConverter());
        var document = JsonSerializer.Deserialize<CatalogDocument>(stream, options) ?? throw new InvalidDataException("Invalid combat catalog.");
        return new(document.Version, document.ClientBuild, document.StatDefinitions, document.ScoreRules,
            document.PrimaryStatCurves, document.PassiveEffects, document.SourceEffects, document.SourceCoverage,
            document.StatRelations, document.BaseLevelTable, document.Skills, document.PetLevelBindings,
            document.NativeExpPointLevels, document.NativeExpPointQualification);
    }

    private sealed record CatalogDocument
    {
        public string Version { get; init; } = "";
        public int ClientBuild { get; init; }
        public IReadOnlyList<CombatStatDefinition> StatDefinitions { get; init; } = [];
        public IReadOnlyList<CombatScoreRule> ScoreRules { get; init; } = [];
        public IReadOnlyList<CombatPrimaryCurve> PrimaryStatCurves { get; init; } = [];
        public IReadOnlyList<CombatPassiveRule> PassiveEffects { get; init; } = [];
        public IReadOnlyList<CombatCatalogSource> SourceEffects { get; init; } = [];
        public IReadOnlyList<CombatCatalogCoverage> SourceCoverage { get; init; } = [];
        public IReadOnlyList<CombatStatRelation> StatRelations { get; init; } = [];
        public IReadOnlyList<CombatBaseLevel> BaseLevelTable { get; init; } = [];
        public IReadOnlyList<CombatCatalogSkill> Skills { get; init; } = [];
        public IReadOnlyList<CombatPetLevelBinding> PetLevelBindings { get; init; } = [];
        public IReadOnlyList<CombatNativeExpPointLevel> NativeExpPointLevels { get; init; } = [];
        public CombatNativeExpPointQualification NativeExpPointQualification { get; init; } = new();
    }
}
