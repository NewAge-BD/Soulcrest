using System.Collections.Frozen;
using System.Globalization;

namespace Soulcrest.Core.Daevanion;

public sealed record DaevanionCombatStatRule(string StatId, decimal Coefficient, DaevanionStatUnit Unit);
public sealed record DaevanionCombatStatPreview(string StatId, decimal? Baseline, decimal RawGain,
    decimal? PlannedValue, decimal? TotalGain, decimal CombatScoreGain);
public sealed record DaevanionSkillPreview(int SkillId, int? CurrentLevel, int? CurrentBoardBonus, int? BaseLevel,
    int? TargetLevel, int PlannedBonus, int? PlannedLevel, int ExcessLevels, bool RequiredConflict);
public sealed record DaevanionCombatEstimate(decimal ObjectiveDelta, decimal ActualDelta, decimal? Total,
    IReadOnlyList<DaevanionCombatStatPreview> StatPreviews, IReadOnlyList<DaevanionSkillPreview> SkillPreviews,
    int ExcessLevels);

/// <summary>
/// Own combat-score weighting backend. User decision 2026-11-09: the former community weights and
/// skill curve are removed; until qualified own weights exist, every weighted estimate stays unavailable.
/// </summary>
public static partial class DaevanionCombatScoreModel
{
    public const string Version = "own-direct-stat-skill-v1";
    internal const long Precision = 1_000_000;

    // Qualified own weights only. Empty means no combat-score estimate can be produced.
    private static readonly FrozenDictionary<string, decimal> Coefficients =
        new Dictionary<string, decimal>(StringComparer.Ordinal).ToFrozenDictionary(StringComparer.Ordinal);

    internal static bool HasWeights => Coefficients.Count != 0;

    public static IReadOnlyList<DaevanionCombatStatRule> Rules { get; } = Array.AsReadOnly(
        Coefficients.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item =>
            new DaevanionCombatStatRule(item.Key, item.Value, DaevanionCatalog.Default.GetStat(item.Key)!.Unit)).ToArray());

    public static IReadOnlyList<string> NeededStatIds(string classId) => Array.AsReadOnly(
        DaevanionRuleCatalog.Default.ForClass(classId).SelectMany(board => board.Nodes).SelectMany(node => node.Node.Effects)
            .Where(effect => effect.StatId is { } id && NeedsBaseline(id)).Select(effect => effect.StatId!)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());

    public static DaevanionCombatContext CreateContext(string classId) => new()
    {
        GeneralStats = Array.AsReadOnly(NeededStatIds(classId).Select(id => new DaevanionCombatStatValue(id)).ToArray()),
        Skills = Array.AsReadOnly(DaevanionCatalog.Default.Skills.Where(skill => skill.ClassId == classId)
            .OrderBy(skill => skill.Id).Select(skill => new DaevanionSkillState(skill.Id)).ToArray())
    };

    /// <summary>Missing inputs are unavailable; malformed known inputs are handled separately by ValidateContext.</summary>
    public static string? UnavailableReason(DaevanionOptimizationRequest request)
    {
        var context = request.CombatContext;
        if (request.Mode == DaevanionOptimizationMode.CombatScore && !HasWeights)
            return "No qualified combat-score weights are available yet.";
        if (request.Mode == DaevanionOptimizationMode.CombatScore && context is null)
            return "Missing character snapshot for the combat-score estimate.";
        if (context is null) return null;
        if (context.SourceGraph is not null) return SourceGraphUnavailableReason(request);
        if (request.Mode == DaevanionOptimizationMode.CombatScore && context.ModelVersion != Version)
            return "The character snapshot belongs to a different estimate model version.";
        var possible = PossibleEffects(request).ToArray();
        var reachableSkills = possible.Where(effect => effect.SkillId is not null)
            .Select(effect => effect.SkillId!.Value).ToHashSet();
        foreach (var state in context.Skills.Where(state => state.TargetLevel is not null && reachableSkills.Contains(state.SkillId)))
            if (state.CurrentLevel is null || state.CurrentBoardBonus is null)
                return "Missing current level or current Daevanion bonus for skill " + state.SkillId + ".";
        if (request.Mode != DaevanionOptimizationMode.CombatScore) return null;
        foreach (var id in possible.Where(effect => effect.StatId is { } id && NeedsBaseline(id))
            .Select(effect => effect.StatId!).Distinct(StringComparer.Ordinal))
        {
            var value = context.GeneralStats.SingleOrDefault(value => value.StatId == id);
            if (value?.Value is null || value.IncreasePercent is null)
                return "Missing pre-board total or increase percentage for stat " + id + ".";
        }
        foreach (var id in possible.Where(effect => effect.SkillId is not null).Select(effect => effect.SkillId!.Value).Distinct())
        {
            var state = context.Skills.SingleOrDefault(state => state.SkillId == id);
            if (state?.CurrentLevel is null || state.CurrentBoardBonus is null)
                return "Missing current level or current Daevanion bonus for skill " + id + ".";
        }
        return null;
    }

    public static DaevanionCombatEstimate Estimate(DaevanionOptimizationRequest request, IEnumerable<int> nodeIds)
    {
        var invalid = ValidateContext(request);
        if (invalid is not null) throw new ArgumentException(invalid);
        if (request.CombatContext?.SourceGraph is not null) return EstimateSourceGraph(request, nodeIds);
        if (request.CombatContext is { } context && context.ModelVersion != Version)
            throw new ArgumentException("The character snapshot belongs to a different estimate model version.");
        var selected = nodeIds.ToHashSet();
        var effects = Effects(request.ClassId, selected).ToArray();
        var statPreviews = new List<DaevanionCombatStatPreview>();
        decimal statScore = 0;
        foreach (var group in effects.Where(effect => effect.StatId is not null).GroupBy(effect => effect.StatId!, StringComparer.Ordinal))
        {
            var amount = group.Sum(effect => Amount(effect));
            var coefficient = Coefficient(group.Key);
            var baseline = request.CombatContext?.GeneralStats.SingleOrDefault(row => row.StatId == group.Key);
            decimal? gain;
            decimal? planned;
            if (DaevanionCatalog.Default.GetStat(group.Key)!.Unit == DaevanionStatUnit.Points && amount != 0)
            {
                if (baseline?.Value is not { } before || baseline.IncreasePercent is not { } increase)
                {
                    if (coefficient != 0) throw new ArgumentException("Missing pre-board stat snapshot: " + group.Key);
                    // A zero CP policy does not establish the unknown total-stat change.
                    planned = null; gain = null;
                }
                else
                {
                    planned = RoundTotal(group.Key, before + amount * (1 + increase / 100));
                    gain = planned.Value - RoundTotal(group.Key, before);
                }
            }
            else
            {
                gain = amount;
                planned = baseline?.Value is { } before ? before + gain : null;
            }
            var contribution = coefficient == 0 ? 0 : gain!.Value * coefficient;
            statScore += contribution;
            statPreviews.Add(new(group.Key, baseline?.Value, amount, planned, gain, contribution));
        }
        var previews = SkillPreviews(request, selected);
        decimal capped = statScore, actual = statScore;
        foreach (var preview in previews.Where(preview => preview.PlannedBonus != 0))
        {
            if (preview.BaseLevel is not { } baseLevel) throw new ArgumentException("Missing pre-board skill level: " + preview.SkillId);
            var kind = DaevanionCatalog.Default.GetSkill(preview.SkillId)!.Kind;
            var final = Math.Min(40, baseLevel + preview.PlannedBonus);
            var cappedFinal = preview.TargetLevel is { } target ? Math.Min(final, Math.Max(baseLevel, target)) : final;
            actual += Skill(kind, final) - Skill(kind, baseLevel);
            capped += Skill(kind, cappedFinal) - Skill(kind, baseLevel);
        }
        var objective = RoundScore(capped); var actualDelta = RoundScore(actual);
        return new(objective, actualDelta, request.CombatContext?.BaselineCombatPower is { } cp ? cp + actualDelta : null,
            Array.AsReadOnly(statPreviews.OrderBy(row => row.StatId, StringComparer.Ordinal).ToArray()), previews,
            previews.Sum(row => row.ExcessLevels));
    }

    public static IReadOnlyList<DaevanionSkillPreview> SkillPreviews(DaevanionOptimizationRequest request, IEnumerable<int> nodeIds)
    {
        var selected = nodeIds.ToHashSet();
        var gains = Effects(request.ClassId, selected).Where(effect => effect.SkillId is not null).GroupBy(effect => effect.SkillId!.Value)
            .ToDictionary(group => group.Key, group => group.Sum(effect => effect.LevelIncrement));
        var states = (request.CombatContext?.Skills ?? []).ToDictionary(state => state.SkillId);
        var required = DaevanionPoolSettings.RequiredNodeIds(request);
        var requiredSkills = Effects(request.ClassId, required).Where(effect => effect.SkillId is not null)
            .Select(effect => effect.SkillId!.Value).ToHashSet();
        return Array.AsReadOnly(DaevanionCatalog.Default.Skills.Where(skill => skill.ClassId == request.ClassId).OrderBy(skill => skill.Id)
            .Select(skill =>
            {
                var state = states.GetValueOrDefault(skill.Id);
                var baseline = BaseLevel(state); var bonus = gains.GetValueOrDefault(skill.Id);
                var excess = state?.TargetLevel is { } target && baseline is { } level ? Math.Max(0, bonus - Math.Max(0, target - level)) : 0;
                return new DaevanionSkillPreview(skill.Id, state?.CurrentLevel, state?.CurrentBoardBonus, baseline,
                    state?.TargetLevel, bonus, baseline is { } start ? start + bonus : null,
                    excess, excess > 0 && requiredSkills.Contains(skill.Id));
            }).ToArray());
    }

    /// <summary>No qualified own skill weighting exists yet; callers treat this as an unknown term.</summary>
    public static int? SkillScore(DaevanionSkillKind kind, int level)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentException("Unknown skill kind.");
        return null;
    }

    internal static string? ValidateContext(DaevanionOptimizationRequest request)
    {
        var context = request.CombatContext;
        if (context is null) return null;
        if (context.GeneralStats is null || context.Skills is null || string.IsNullOrWhiteSpace(context.ModelVersion)
            || context.BaselineCombatPower is < 0 || context.CurrentCombatPower is < 0) return "Invalid character snapshot.";
        if (context.SourceGraph is { } graph)
        {
            if (graph.ClassId != request.ClassId || context.ModelVersion != Soulcrest.Core.Combat.CombatScoreModel.Version)
                return "Source model class or version mismatch.";
            try { Soulcrest.Core.Combat.CombatStatDerivation.ValidateGraph(graph); }
            catch (ArgumentException error) { return error.Message; }
        }
        var stats = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stat in context.GeneralStats)
            if (stat is null || stat.StatId is not { } id || DaevanionCatalog.Default.GetStat(id) is null || !stats.Add(id)
                || !ValidNumber(stat.Value, 1_000_000_000) || !ValidNumber(stat.IncreasePercent, 10_000))
                return "Invalid, duplicate or unknown character stat.";
        var skills = new HashSet<int>();
        foreach (var state in context.Skills)
            if (state is null || DaevanionCatalog.Default.GetSkill(state.SkillId)?.ClassId != request.ClassId || !skills.Add(state.SkillId)
                || state.CurrentLevel is < 0 or > 255 || state.CurrentBoardBonus is < 0 or > 255 || state.TargetLevel is < 0 or > 40
                || state.CurrentLevel is { } current && state.CurrentBoardBonus is { } bonus && bonus > current)
                return "Invalid, duplicate or foreign character skill.";
        return null;
    }

    private static int Skill(DaevanionSkillKind kind, int level) =>
        SkillScore(kind, level) ?? throw new ArgumentException("No qualified skill weighting is available.");

    internal static bool HasTargets(DaevanionOptimizationRequest request) => request.CombatContext?.Skills.Any(state => state.TargetLevel is not null) == true;
    internal static int? BaseLevel(DaevanionSkillState? state) => state?.CurrentLevel is { } current
        && state.CurrentBoardBonus is { } bonus ? current - bonus : null;
    internal static decimal Coefficient(string id) => Coefficients.TryGetValue(id, out var coefficient)
        ? coefficient : throw new ArgumentException("Unmapped estimate stat: " + id);
    internal static bool NeedsBaseline(string id) => Coefficients.TryGetValue(id, out var coefficient) && coefficient != 0
        && DaevanionCatalog.Default.GetStat(id)!.Unit == DaevanionStatUnit.Points;
    internal static decimal RoundTotal(string id, decimal value) => id is "HPMax" or "MPMax" ? decimal.Ceiling(value) : decimal.Floor(value);
    internal static decimal RoundScore(decimal value) => decimal.Floor(value + 0.5m);
    internal static decimal Amount(DaevanionEffect effect) => decimal.Parse(effect.Amount, CultureInfo.InvariantCulture);
    internal static IEnumerable<DaevanionEffect> Effects(string classId, IReadOnlySet<int> selected)
    {
        var nodes = DaevanionRuleCatalog.Default.ForClass(classId).SelectMany(board => board.Nodes).ToArray();
        if (!selected.IsSubsetOf(nodes.Select(node => node.Node.Id).ToHashSet()))
            throw new ArgumentException("Unknown or foreign Daevanion node.");
        return nodes.Where(node => selected.Contains(node.Node.Id)).SelectMany(node => node.Node.Effects);
    }
    private static bool ValidNumber(decimal? value, decimal max) => value is null || value >= 0 && value <= max && decimal.Round(value.Value, 4) == value;

    private static IEnumerable<DaevanionEffect> PossibleEffects(DaevanionOptimizationRequest request) =>
        Effects(request.ClassId, PossibleNodeIds(request));

    internal static IReadOnlySet<int> PossibleNodeIds(DaevanionOptimizationRequest request)
    {
        var result = new HashSet<int>();
        foreach (var board in DaevanionRuleCatalog.Default.ForClass(request.ClassId))
        {
            if (request.CharacterLevel is { } level && level < board.MinimumLevel) continue;
            var byId = board.Nodes.ToDictionary(node => node.Node.Id);
            var root = board.Nodes.Single(node => node.Node.Kind == DaevanionNodeKind.Start).Node.Id;
            var distances = new Dictionary<int, int> { [root] = 0 };
            var queue = new PriorityQueue<int, int>(); queue.Enqueue(root, 0);
            while (queue.TryDequeue(out var id, out var distance))
            {
                if (distance != distances[id]) continue;
                result.Add(id);
                foreach (var next in byId[id].NeighborIds)
                {
                    var candidate = distance + byId[next].Cost;
                    if (candidate > request.Budgets.For(board.Pool) || distances.GetValueOrDefault(next, int.MaxValue) <= candidate) continue;
                    distances[next] = candidate; queue.Enqueue(next, candidate);
                }
            }
        }
        return result;
    }
}
