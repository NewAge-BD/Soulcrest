using System.Globalization;
using System.Collections.Frozen;

namespace Soulcrest.Core.Daevanion;

/// <summary>Pool-local preferences with a backward-compatible fallback for existing planning files.</summary>
public static class DaevanionPoolSettings
{
    /// <summary>Every matching node is mandatory, across the complete selected pool rather than the visible board.</summary>
    public static IReadOnlySet<int> RequiredNodeIds(DaevanionOptimizationRequest request, DaevanionBudgetPool? pool = null)
    {
        if (pool is { } selectedPool && !Enum.IsDefined(selectedPool)) throw new ArgumentException("Unknown point pool.");
        var result = new HashSet<int>();
        foreach (var selected in Enum.GetValues<DaevanionBudgetPool>().Where(value => pool is null || value == pool.Value))
        {
            var required = ForPool(request, selected).Priorities.Where(row => row.Required).ToArray();
            foreach (var node in DaevanionRuleCatalog.Default.ForClass(request.ClassId).Where(board => board.Pool == selected)
                .SelectMany(board => board.Nodes))
                if (node.Node.Effects.Any(effect => required.Any(row =>
                    row.Kind == DaevanionPriorityKind.Stat ? effect.StatId == row.Key : effect.SkillId is { } skillId
                    && skillId.ToString(CultureInfo.InvariantCulture) == row.Key
                    && DaevanionCatalog.Default.GetSkill(skillId)!.Kind == (row.Kind == DaevanionPriorityKind.Skill
                        ? DaevanionSkillKind.Active : DaevanionSkillKind.Passive)))) result.Add(node.Node.Id);
        }
        return result.ToFrozenSet();
    }

    public static DaevanionPoolPreferences CreateDefault(string classId, DaevanionBudgetPool pool)
    {
        if (!Enum.IsDefined(pool)) throw new ArgumentException("Unknown point pool.", nameof(pool));
        var defaults = DaevanionPlanningDefaults.CreateRequest(classId);
        var keys = new HashSet<(DaevanionPriorityKind, string)>();
        foreach (var effect in DaevanionRuleCatalog.Default.ForClass(classId).Where(board => board.Pool == pool)
            .SelectMany(board => board.Nodes).SelectMany(node => node.Node.Effects))
        {
            if (effect.StatId is { } statId) keys.Add((DaevanionPriorityKind.Stat, statId));
            else if (effect.SkillId is { } skillId)
            {
                var skill = DaevanionCatalog.Default.GetSkill(skillId)!;
                keys.Add((skill.Kind == DaevanionSkillKind.Active ? DaevanionPriorityKind.Skill : DaevanionPriorityKind.Passive,
                    skillId.ToString(CultureInfo.InvariantCulture)));
            }
        }
        return new(pool, defaults.Weights, Array.AsReadOnly(defaults.Priorities.Where(row => keys.Contains((row.Kind, row.Key))).ToArray()));
    }

    public static DaevanionPoolPreferences ForPool(DaevanionOptimizationRequest request, DaevanionBudgetPool pool)
    {
        ArgumentNullException.ThrowIfNull(request);
        var defaults = CreateDefault(request.ClassId, pool);
        if (request.PoolPreferences is null || request.Priorities is null || request.Weights is null)
            throw new ArgumentException("Missing planning preferences.");
        var explicitPreferences = request.PoolPreferences.Where(item => item is not null && item.Pool == pool).ToArray();
        if (explicitPreferences.Length > 1) throw new ArgumentException("Duplicate point-pool preferences.");
        var source = explicitPreferences.SingleOrDefault();
        if (!ValidWeights(source?.Weights ?? request.Weights)) throw new ArgumentException("Invalid category weights.");
        var known = defaults.Priorities.Select(row => (row.Kind, row.Key)).ToHashSet();
        var rows = source?.Priorities ?? request.Priorities.Where(row => row is not null && known.Contains((row.Kind, row.Key))).ToArray();
        if (source is not null && (source.Weights is null || source.Priorities is null
            || RowsError(rows, defaults.Priorities) is not null)) throw new ArgumentException("Invalid point-pool preferences.");
        return new(pool, source?.Weights ?? request.Weights, MergeRows(rows, defaults.Priorities));
    }

    public static DaevanionOptimizationRequest WithPool(DaevanionOptimizationRequest request, DaevanionPoolPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(preferences);
        var defaults = CreateDefault(request.ClassId, preferences.Pool);
        if (!ValidWeights(preferences.Weights) || preferences.Priorities is null
            || RowsError(preferences.Priorities, defaults.Priorities) is not null)
            throw new ArgumentException("Invalid point-pool preferences.");
        var next = preferences with { Priorities = MergeRows(preferences.Priorities, defaults.Priorities) };
        var current = ForPool(request, preferences.Pool);
        if (current.Weights == next.Weights && current.Priorities.SequenceEqual(next.Priorities)) return request;
        return Freeze(request with { PoolPreferences = request.PoolPreferences.Where(item => item.Pool != next.Pool)
            .Append(next).OrderBy(item => item.Pool).ToArray() });
    }

    /// <summary>Removes retired keys and appends new defaults while retaining each pool's independent choices.</summary>
    public static DaevanionOptimizationRequest Reconcile(DaevanionOptimizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var defaults = DaevanionPlanningDefaults.CreateRequest(request.ClassId);
        var next = request with
        {
            Priorities = MergeRows(request.Priorities ?? [], defaults.Priorities),
            PoolPreferences = (request.PoolPreferences ?? []).Where(item => item is not null && Enum.IsDefined(item.Pool))
                .DistinctBy(item => item.Pool).Select(item => item with
                {
                    Priorities = MergeRows(item.Priorities ?? [], CreateDefault(request.ClassId, item.Pool).Priorities)
                }).ToArray()
        };
        return Freeze(Validate(next) is null ? next : defaults);
    }

    public static DaevanionOptimizationRequest Freeze(DaevanionOptimizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Priorities is null || request.PoolPreferences is null) throw new ArgumentException("Missing planning preferences.");
        return request with
        {
            CombatContext = request.CombatContext is { } context ? context with
            {
                SourceGraph = context.SourceGraph is { } graph ? Soulcrest.Core.Combat.CombatScoreModel.Freeze(graph) : null,
                GeneralStats = Array.AsReadOnly((context.GeneralStats ?? throw new ArgumentException("Missing character stats."))
                    .Select(row => row ?? throw new ArgumentException("Missing character stat.")).ToArray()),
                Skills = Array.AsReadOnly((context.Skills ?? throw new ArgumentException("Missing character skills."))
                    .Select(row => row ?? throw new ArgumentException("Missing character skill.")).ToArray())
            } : null,
            Priorities = Array.AsReadOnly(request.Priorities.Select(row => row ?? throw new ArgumentException("Missing priority.")).ToArray()),
            PoolPreferences = Array.AsReadOnly(request.PoolPreferences.Select(item => item is null || item.Priorities is null
                ? throw new ArgumentException("Missing point-pool preferences.") : item with
                { Priorities = Array.AsReadOnly(item.Priorities.Select(row => row ?? throw new ArgumentException("Missing priority.")).ToArray()) }).ToArray())
        };
    }

    /// <summary>Compares actual three-pool inputs, independent of explicit override versus legacy storage.</summary>
    public static bool Equivalent(DaevanionOptimizationRequest left, DaevanionOptimizationRequest right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null || left.ClassId != right.ClassId || left.CharacterLevel != right.CharacterLevel
            || left.Budgets != right.Budgets || left.Mode != right.Mode) return false;
        try
        {
            var aContext = left.CombatContext; var bContext = right.CombatContext;
            if ((aContext is null) != (bContext is null)) return false;
            if (aContext is not null && bContext is not null && (aContext.ModelVersion != bContext.ModelVersion
                || aContext.BaselineCombatPower != bContext.BaselineCombatPower
                || aContext.CurrentCombatPower != bContext.CurrentCombatPower
                || aContext.EffectCatalogVersion != bContext.EffectCatalogVersion
                || !EquivalentGraph(aContext.SourceGraph, bContext.SourceGraph)
                || !aContext.GeneralStats.OrderBy(row => row.StatId, StringComparer.Ordinal).SequenceEqual(bContext.GeneralStats.OrderBy(row => row.StatId, StringComparer.Ordinal))
                || !aContext.Skills.OrderBy(row => row.SkillId).SequenceEqual(bContext.Skills.OrderBy(row => row.SkillId)))) return false;
            return Enum.GetValues<DaevanionBudgetPool>().All(pool =>
            {
                var a = ForPool(left, pool); var b = ForPool(right, pool);
                return a.Weights == b.Weights && a.Priorities.SequenceEqual(b.Priorities);
            });
        }
        catch (ArgumentException) { return false; }
    }

    /// <returns>Null for valid inputs; missing priority rows are completed with defaults by ForPool.</returns>
    public static string? Validate(DaevanionOptimizationRequest request)
    {
        if (request is null || request.Budgets is null || request.Priorities is null || request.PoolPreferences is null
            || !ValidWeights(request.Weights)) return "Missing or invalid planning inputs.";
        if (!Enum.IsDefined(request.Mode)) return "Unknown optimization mode.";
        if (request.CharacterLevel is <= 0) return "Invalid observed character level.";
        if (Enum.GetValues<DaevanionBudgetPool>().Any(pool => request.Budgets.For(pool) < 0)) return "Negative point budget.";
        if (!DaevanionCatalog.Default.Classes.Any(row => row.Id == request.ClassId)) return "Unknown class.";
        var error = RowsError(request.Priorities, DaevanionPlanningDefaults.CreateRequest(request.ClassId).Priorities);
        if (error is not null) return error;
        var seen = new HashSet<DaevanionBudgetPool>();
        foreach (var item in request.PoolPreferences)
        {
            if (item is null || !Enum.IsDefined(item.Pool) || !seen.Add(item.Pool) || !ValidWeights(item.Weights) || item.Priorities is null)
                return "Invalid or duplicate point-pool preferences.";
            error = RowsError(item.Priorities, CreateDefault(request.ClassId, item.Pool).Priorities);
            if (error is not null) return error;
        }
        return DaevanionCombatScoreModel.ValidateContext(request);
    }

    private static bool EquivalentGraph(Soulcrest.Core.Combat.CombatSourceGraph? left, Soulcrest.Core.Combat.CombatSourceGraph? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;
        // Native comparisons do not drive the model or invalidate a running route calculation.
        return System.Text.Json.JsonSerializer.Serialize(left with { NativeTotals = [], NativeSkillTotals = [] })
            == System.Text.Json.JsonSerializer.Serialize(right with { NativeTotals = [], NativeSkillTotals = [] });
    }

    private static bool ValidWeights(DaevanionCategoryWeights? weights) => weights is not null
        && Enum.GetValues<DaevanionPriorityKind>().All(kind => weights.For(kind) is >= 0 and <= 100);

    private static string? RowsError(IReadOnlyList<DaevanionPriority> rows, IReadOnlyList<DaevanionPriority> defaults)
    {
        var known = defaults.Select(row => (row.Kind, row.Key)).ToHashSet();
        var seen = new HashSet<(DaevanionPriorityKind, string)>();
        return rows.Any(row => row is null || row.Ignored && row.Required || !known.Contains((row.Kind, row.Key)) || !seen.Add((row.Kind, row.Key)))
            ? "Unknown, conflicting, irrelevant or duplicate priority." : null;
    }

    private static IReadOnlyList<DaevanionPriority> MergeRows(IEnumerable<DaevanionPriority> rows, IReadOnlyList<DaevanionPriority> defaults)
    {
        var known = defaults.Select(row => (row.Kind, row.Key)).ToHashSet();
        var kept = rows.Where(row => row is not null && known.Contains((row.Kind, row.Key))).DistinctBy(row => (row.Kind, row.Key)).ToArray();
        var seen = kept.Select(row => (row.Kind, row.Key)).ToHashSet();
        return Array.AsReadOnly(kept.Concat(defaults.Where(row => !seen.Contains((row.Kind, row.Key)))).ToArray());
    }
}
