using Soulcrest.Core.Daevanion;
using Soulcrest.Core.Network;

namespace Soulcrest.Core.Combat;

public enum CombatKnowledgeStatus { Unknown, Partial, Complete, Stale, Conflict }
public enum CombatApplication { Unknown, Confirmed, Inactive }
public enum CombatEffectOperation { Flat, IncreasePercent }
public enum CombatRounding { None, Floor, Ceiling }
public enum CombatStatUnit { Points, Percent }
public enum CombatNativeValueDomain { CharacterStats, EquipmentOptions, GenusEffects }
public enum CombatNativeStatRole { Unknown, FlatComponent, IncreasePercentComponent, DisplayTotal }
public enum CombatSourceKind
{
    Base, Skill, Passive, Gear, Enchantment, EquipmentSet, Soulbind, Magicstone, Godstone,
    Arcana, Daevanion, TitleEquipped, TitleOwned, Pet, Genus, WingEquipped, WingOwned,
    Monolith, Skin, Achievement, Fixed, Unknown
}

/// <summary>Value is in display points or percentage points, never an unlabelled native fixed-point scalar.</summary>
public sealed record CombatEffect(string StatId, decimal Value,
    CombatEffectOperation Operation = CombatEffectOperation.Flat,
    CombatApplication Application = CombatApplication.Unknown, string? RuleId = null);

/// <summary>An additive level component from exactly one source. Observed total levels are comparisons only.</summary>
public sealed record CombatSkill(int SkillId, DaevanionSkillKind Kind, int? Level,
    CombatApplication Application = CombatApplication.Unknown);
public sealed record CombatNativeStat(int NativeStatId, long RawValue,
    CombatNativeValueDomain Domain = CombatNativeValueDomain.CharacterStats);
public sealed record CombatNativeSkill(int SkillId, int TotalLevel);
public sealed record CombatAbnormalWitness(int AbnormalId, int Level, bool ReadyToRemove);

public sealed record CombatSource
{
    public string Id { get; init; } = "";
    public CombatSourceKind Kind { get; init; } = CombatSourceKind.Unknown;
    public CombatKnowledgeStatus Status { get; init; } = CombatKnowledgeStatus.Unknown;
    public string? Reason { get; init; }
    public IReadOnlyList<CombatEffect> Effects { get; init; } = [];
    public IReadOnlyList<CombatSkill> Skills { get; init; } = [];
}

/// <summary>Only source effects form the calculation. Native observations do not fill missing source effects.</summary>
public sealed record CombatSourceGraph
{
    public string ClassId { get; init; } = "";
    public CombatKnowledgeStatus Status { get; init; } = CombatKnowledgeStatus.Unknown;
    public IReadOnlyList<CombatSource> Sources { get; init; } = [];
    /// <summary>Legacy property name: entries may be raw components. Domain and catalog role determine their meaning.</summary>
    public IReadOnlyList<CombatNativeStat> NativeTotals { get; init; } = [];
    public IReadOnlyList<CombatNativeSkill> NativeSkillTotals { get; init; } = [];
    /// <summary>Positive own application witnesses; absence never proves that a passive is inactive.</summary>
    public IReadOnlyList<int> ActiveAbnormalIds { get; init; } = [];
    public IReadOnlyList<CombatAbnormalWitness> ActiveAbnormals { get; init; } = [];
    public CombatKnowledgeStatus ActiveAbnormalStatus { get; init; } = CombatKnowledgeStatus.Unknown;
}

public sealed record CombatDerivedStat(string StatId, CombatStatUnit Unit, decimal Flat,
    decimal IncreasePercent, decimal? Value, CombatKnowledgeStatus Status);
public sealed record CombatDerivedSkill(int SkillId, DaevanionSkillKind Kind, int? Level,
    CombatKnowledgeStatus Status);
public sealed record CombatNativeComparison(int NativeStatId, string? StatId, long RawValue,
    decimal? NativeValue, decimal? ModelValue, decimal? Difference, CombatKnowledgeStatus Status)
{
    public CombatNativeValueDomain Domain { get; init; }
    public CombatNativeStatRole Role { get; init; } = CombatNativeStatRole.Unknown;
    /// <summary>Normalized wire component; this is not a display total unless Role explicitly says so.</summary>
    public decimal? NormalizedComponent { get; init; }
    public decimal? NativeFlat { get; init; }
    public decimal? NativeIncreasePercent { get; init; }
    public decimal? ModelFlat { get; init; }
    public decimal? ModelIncreasePercent { get; init; }
}
public sealed record CombatSkillComparison(int SkillId, int NativeLevel, int? ModelLevel, int? Difference);
public sealed record CombatStatChange(string StatId, decimal? WithoutSource, decimal? WithSource, decimal? Delta);
public sealed record CombatAppliedPassive(int SkillId, string RuleId, CombatEffect? Effect, CombatKnowledgeStatus Status);

/// <summary>One additive model term. A missing weight is unknown; intermediates are excluded from direct scoring.</summary>
public sealed record CombatScoreStatTerm(string StatId, CombatStatUnit Unit, decimal? Value, decimal? Coefficient,
    decimal? Contribution, bool IsIntermediate, CombatKnowledgeStatus Status);
public sealed record CombatScoreSkillTerm(int SkillId, DaevanionSkillKind Kind, int? Level,
    decimal? Contribution, CombatKnowledgeStatus Status);
public sealed record CombatScoreComposition
{
    public IReadOnlyList<CombatScoreStatTerm> Stats { get; init; } = [];
    public IReadOnlyList<CombatScoreSkillTerm> Skills { get; init; } = [];
    public decimal KnownStatSubtotal { get; init; }
    public decimal KnownSkillSubtotal { get; init; }
    public decimal UnroundedSubtotal { get; init; }
    public decimal RoundedSubtotal { get; init; }
    public decimal RoundingAdjustment { get; init; }
    public bool IsComplete { get; init; }
}

public sealed record CombatDerivation
{
    public CombatKnowledgeStatus Status { get; init; }
    public IReadOnlyList<CombatDerivedStat> Stats { get; init; } = [];
    public IReadOnlyList<CombatDerivedSkill> Skills { get; init; } = [];
    public IReadOnlyList<CombatAppliedPassive> Passives { get; init; } = [];
    public IReadOnlyList<CombatNativeComparison> NativeComparisons { get; init; } = [];
    public IReadOnlyList<CombatSkillComparison> SkillComparisons { get; init; } = [];
    public IReadOnlyList<string> MissingRequirements { get; init; } = [];
}

public sealed record CombatSourceReport
{
    public string SourceId { get; init; } = "";
    public CombatSourceKind Kind { get; init; }
    public CombatKnowledgeStatus Status { get; init; }
    public IReadOnlyList<CombatEffect> Effects { get; init; } = [];
    public IReadOnlyList<CombatStatChange> StatContributions { get; init; } = [];
    public IReadOnlyList<CombatSkill> Skills { get; init; } = [];
    /// <summary>Round(M(all) - M(without this source)); overlapping interactions make these losses non-additive.</summary>
    public decimal? ModelLoss { get; init; }
    public string? Reason { get; init; }
}

public sealed record CombatEvaluation
{
    public string ModelVersion { get; init; } = CombatScoreModel.Version;
    public CombatKnowledgeStatus Status { get; init; }
    public decimal? ModelTotal { get; init; }
    public decimal KnownSubtotal { get; init; }
    public CombatScoreComposition Composition { get; init; } = new();
    public IReadOnlyList<CombatSourceReport> Sources { get; init; } = [];
    public IReadOnlyList<CombatDerivedStat> Stats { get; init; } = [];
    public IReadOnlyList<CombatDerivedSkill> Skills { get; init; } = [];
    public IReadOnlyList<CombatNativeComparison> NativeComparisons { get; init; } = [];
    public IReadOnlyList<CombatSkillComparison> SkillComparisons { get; init; } = [];
    public IReadOnlyList<CombatAppliedPassive> Passives { get; init; } = [];
    public IReadOnlyList<string> MissingRequirements { get; init; } = [];
}

public sealed record CombatCounterfactual(CombatEvaluation Before, CombatEvaluation After,
    decimal? ModelDelta, decimal KnownSubtotalDelta);

/// <summary>Transport and own-character authority have already been checked by the caller.</summary>
public sealed record CombatObserved<T>(T? Value, CombatKnowledgeStatus Status = CombatKnowledgeStatus.Unknown);

public sealed record CombatObservation
{
    public string ClassId { get; init; } = "";
    public int? CharacterLevel { get; init; }
    public CombatObserved<IReadOnlyDictionary<ushort, int>> NativeStats { get; init; } = new(null);
    public CombatObserved<IReadOnlyList<OwnSkillLevel>> Skills { get; init; } = new(null);
    public CombatObserved<CharacterTitleState> Titles { get; init; } = new(null);
    /// <summary>Character-owned titles, independent of equipped slots. Null preserves frozen legacy input semantics.</summary>
    public CombatObserved<IReadOnlySet<int>>? TitleOwnership { get; init; }
    public CombatObserved<CharacterEquipmentState> Equipment { get; init; } = new(null);
    public CombatObserved<CharacterAchievementState> Achievements { get; init; } = new(null);
    public CombatObserved<CharacterDaevanionState> Boards { get; init; } = new(null);
    public CombatObserved<PetProgressSnapshot> Pets { get; init; } = new(null);
    public IReadOnlyList<CombatSource> AdditionalSources { get; init; } = [];
    public CombatObserved<IReadOnlyList<int>> ActiveAbnormalIds { get; init; } = new(null);
    public CombatObserved<IReadOnlyList<CharacterAbnormal>> ActiveAbnormals { get; init; } = new(null);
}
