namespace Soulcrest.Core.Daevanion;

public enum DaevanionBudgetPool { Normal, Ariel, Azphel }
public enum DaevanionOptimizationMode { Priority, CombatScore }
public enum DaevanionPriorityKind { Stat, Skill, Passive }
public enum DaevanionOptimizationStatus { Optimal, Cancelled, Unavailable, Invalid, Infeasible }

/// <summary>Order within each kind defines rank. Skill and passive keys are invariant skill IDs.</summary>
public sealed record DaevanionPriority(DaevanionPriorityKind Kind, string Key, bool Ignored = false, bool Required = false);
public sealed record DaevanionBudgets(int Normal = 0, int Ariel = 0, int Azphel = 0)
{
    public int For(DaevanionBudgetPool pool) => pool switch
    {
        DaevanionBudgetPool.Normal => Normal,
        DaevanionBudgetPool.Ariel => Ariel,
        DaevanionBudgetPool.Azphel => Azphel,
        _ => throw new ArgumentOutOfRangeException(nameof(pool))
    };
}

public sealed record DaevanionCategoryWeights(int Stats = 100, int Skills = 100, int Passives = 100)
{
    public int For(DaevanionPriorityKind kind) => kind switch
    {
        DaevanionPriorityKind.Stat => Stats,
        DaevanionPriorityKind.Skill => Skills,
        DaevanionPriorityKind.Passive => Passives,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}

public sealed record DaevanionOptimizationRequest
{
    public string ClassId { get; init; } = "";
    /// <summary>Observed own level; null is retained only for historical/offline requests.</summary>
    public int? CharacterLevel { get; init; }
    public DaevanionBudgets Budgets { get; init; } = new();
    public DaevanionCategoryWeights Weights { get; init; } = new();
    public DaevanionOptimizationMode Mode { get; init; }
    public IReadOnlyList<DaevanionPriority> Priorities { get; init; } = [];
    /// <summary>Independent pool overrides; absent pools inherit filtered legacy preferences.</summary>
    public IReadOnlyList<DaevanionPoolPreferences> PoolPreferences { get; init; } = [];
    /// <summary>Explicit character snapshot. Missing values are unknown, never implicit zeroes.</summary>
    public DaevanionCombatContext? CombatContext { get; init; }
}

/// <summary>GeneralStats and optional CP exclude all Daevanion boards; current skill levels include CurrentBoardBonus.</summary>
public sealed record DaevanionCombatContext
{
    public string ModelVersion { get; init; } = DaevanionCombatScoreModel.Version;
    public decimal? BaselineCombatPower { get; init; }
    public IReadOnlyList<DaevanionCombatStatValue> GeneralStats { get; init; } = [];
    public IReadOnlyList<DaevanionSkillState> Skills { get; init; } = [];
    /// <summary>Current source graph including the currently purchased boards. Historical direct-stat contexts omit it.</summary>
    public Soulcrest.Core.Combat.CombatSourceGraph? SourceGraph { get; init; }
    /// <summary>Observed current CP, used only to anchor a whole-route model difference.</summary>
    public decimal? CurrentCombatPower { get; init; }
    public string? EffectCatalogVersion { get; init; }
}

public sealed record DaevanionCombatStatValue(string StatId, decimal? Value = null, decimal? IncreasePercent = null);
public sealed record DaevanionSkillState(int SkillId, int? CurrentLevel = null, int? CurrentBoardBonus = null,
    int? TargetLevel = null);

public sealed record DaevanionPoolPreferences(DaevanionBudgetPool Pool, DaevanionCategoryWeights Weights,
    IReadOnlyList<DaevanionPriority> Priorities);

public sealed record DaevanionPlanEdge(int FromNodeId, int ToNodeId);

/// <summary>A plan from an empty board, never evidence of purchases in the game.</summary>
public sealed record DaevanionBoardPlan
{
    public int BoardId { get; init; }
    public string FamilyId { get; init; } = "";
    public DaevanionBudgetPool Pool { get; init; }
    public IReadOnlyList<int> NodeIds { get; init; } = [];
    public IReadOnlyList<DaevanionPlanEdge> Edges { get; init; } = [];
    /// <summary>Start first, then each purchased node after its tree parent.</summary>
    public IReadOnlyList<int> ActivationOrder { get; init; } = [];
    public int Cost { get; init; }
    public decimal Score { get; init; }
}

public sealed record DaevanionOptimizationProgress
{
    public bool GlobalObjective { get; init; }
    public DaevanionBudgetPool Pool { get; init; }
    public string Stage { get; init; } = "";
    public decimal? BestScore { get; init; }
    public double? BestBound { get; init; }
    public TimeSpan Elapsed { get; init; }
    public IReadOnlyList<DaevanionBoardPlan> Boards { get; init; } = [];
}

public sealed record DaevanionOptimizationResult
{
    public DaevanionOptimizationStatus Status { get; init; }
    public string ClassId { get; init; } = "";
    public string CatalogVersion { get; init; } = "";
    public string CatalogFingerprint { get; init; } = "";
    public string RulesVersion { get; init; } = "";
    public string? Message { get; init; }
    public IReadOnlyList<DaevanionBoardPlan> Boards { get; init; } = [];
    public string? ScoreModelVersion { get; init; }
    /// <summary>Target-capped, once-rounded estimated CP gain, not an official game value.</summary>
    public decimal? EstimatedCombatScore { get; init; }
    public decimal? ActualEstimatedCombatScore { get; init; }
    public decimal? EstimatedTotalCombatScore { get; init; }
    public decimal Score => Boards.Sum(board => board.Score);
    public bool Optimal => Status == DaevanionOptimizationStatus.Optimal;
}

public interface IDaevanionOptimizer
{
    Task<DaevanionOptimizationResult> OptimizeAsync(DaevanionOptimizationRequest request,
        IProgress<DaevanionOptimizationProgress>? progress = null, CancellationToken cancellationToken = default);
}
