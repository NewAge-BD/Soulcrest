namespace Soulcrest.Core.Combat;

public enum CharacterDisplayStatCategory { Main, Sub }
public enum CharacterDisplayStatQualification { Unknown, ObservedMatch, ControlledChange, NativeDirect }
public enum CharacterDisplayVitalSourceKind { Unknown, Own, StateUpdate8D00 }
public enum CharacterDisplayInputRole { Flat, IncreaseBasisPoints }

/// <summary>Already own-bound, atomic raw observations. Partial lists have no implicit zero keys.</summary>
public sealed record CharacterDisplayNativeStats(IReadOnlyDictionary<ushort, int> Values,
    CombatKnowledgeStatus Status, bool IsFresh, long Generation, DateTimeOffset? ObservedAt, string Source);

/// <summary>One separately observed resource maximum; HP and MP never borrow each other's timestamp.</summary>
public sealed record CharacterDisplayMaximumFact(long Value, CombatKnowledgeStatus Status, bool IsFresh,
    long Generation, DateTimeOffset? ObservedAt, string Source, CharacterDisplayVitalSourceKind Kind);

/// <summary>The caller supplies current own authority; no identity or OS lookup occurs in this projection.</summary>
public sealed record CharacterDisplayStatsInput
{
    public int ClientBuild { get; init; }
    public string? ClassId { get; init; }
    public long Generation { get; init; }
    public CharacterDisplayNativeStats? NativeStats { get; init; }
    public CharacterDisplayMaximumFact? HpMaximum { get; init; }
    public CharacterDisplayMaximumFact? MpMaximum { get; init; }
}

public sealed record CharacterDisplayStatDependency(ushort NativeId, int? RawValue, decimal Coefficient,
    CharacterDisplayInputRole Role = CharacterDisplayInputRole.Flat);

public sealed record CharacterDisplayStatTerm(ushort NativeId, decimal Coefficient = 1);

/// <summary>A domain-specific forward rule, independent of effect-table or combat-score stat IDs.</summary>
public sealed record CharacterDisplayForwardRule
{
    public string Id { get; init; } = "";
    public string OutputId { get; init; } = "";
    public string LabelStatId { get; init; } = "";
    public string? LabelDe { get; init; }
    public string? LabelEn { get; init; }
    public int ClientBuild { get; init; }
    public string? RequiredClassId { get; init; }
    public CharacterDisplayStatCategory Category { get; init; }
    public CombatStatUnit Unit { get; init; } = CombatStatUnit.Points;
    public IReadOnlyList<CharacterDisplayStatTerm> FlatInputs { get; init; } = [];
    public ushort? IncreaseBasisPointsInput { get; init; }
    public decimal Divisor { get; init; } = 1;
    public CombatRounding Rounding { get; init; }
    public int? DisplayDecimalPlaces { get; init; }
    public bool AllowNegative { get; init; }
    public CharacterDisplayStatQualification Qualification { get; init; }
    public string Formula { get; init; } = "";
    public IReadOnlyList<string> Evidence { get; init; } = [];
}

public sealed record CharacterDisplayStatRow
{
    public string Id { get; init; } = "";
    public string LabelDe { get; init; } = "";
    public string LabelEn { get; init; } = "";
    public CharacterDisplayStatCategory Category { get; init; }
    public decimal? Value { get; init; }
    public decimal? UnroundedValue { get; init; }
    public CombatStatUnit Unit { get; init; } = CombatStatUnit.Points;
    public CombatKnowledgeStatus Status { get; init; }
    public CharacterDisplayStatQualification Qualification { get; init; }
    public string RuleId { get; init; } = "";
    public string Formula { get; init; } = "";
    public IReadOnlyList<string> Evidence { get; init; } = [];
    public IReadOnlyList<CharacterDisplayStatDependency> Inputs { get; init; } = [];
    public IReadOnlyList<ushort> MissingInputIds { get; init; } = [];
    public DateTimeOffset? ObservedAt { get; init; }
    public string Source { get; init; } = "unknown";
    public decimal? ForwardValue { get; init; }
    public CombatKnowledgeStatus ForwardStatus { get; init; }
    public string? ForwardReason { get; init; }
    public CombatKnowledgeStatus ComparisonStatus { get; init; }
    /// <summary>Fixed diagnostic code, never a payload or a player-supplied exception string.</summary>
    public string? Reason { get; init; }
}

/// <summary>Independent display-value projection; it grants no effect-ID or combat-score mapping.</summary>
public sealed record CharacterDisplayStats
{
    public string Version { get; init; } = CharacterDisplayStatProjection.Version;
    public int ClientBuild { get; init; }
    public long Generation { get; init; }
    public CombatKnowledgeStatus Status { get; init; }
    public IReadOnlyList<CharacterDisplayStatRow> Rows { get; init; } = [];
}
