using System.Globalization;
using System.Numerics;

namespace Soulcrest.Core.Daevanion;

public sealed record DaevanionNodeValue(int NodeId, decimal Score, decimal IgnoredUnits);

public static class DaevanionScoring
{
    public static decimal PriorityWeight(DaevanionOptimizationRequest request, DaevanionPriorityKind kind, string key,
        DaevanionBudgetPool pool = DaevanionBudgetPool.Normal) =>
        Weights(DaevanionPoolSettings.ForPool(request, pool))[(kind, key)].Decimal;

    public static IReadOnlyDictionary<int, DaevanionNodeValue> NodeValues(DaevanionOptimizationRequest request) =>
        Compute(request).Values;

    /// <summary>Sums exact integer coefficients before a single display division.</summary>
    public static decimal ScoreForNodes(DaevanionOptimizationRequest request, IEnumerable<int> nodeIds)
    {
        var ids = nodeIds.Distinct().ToHashSet();
        if (request.Mode == DaevanionOptimizationMode.CombatScore)
            return DaevanionCombatScoreModel.Estimate(request, ids).ObjectiveDelta;
        var model = Compute(request);
        return PriorityScoreForNodes(request, ids, model) / (decimal)model.Scale;
    }

    /// <summary>Nonlinear contributions are allocated by the stable catalog board order; the total is rounded only once.</summary>
    public static IReadOnlyList<DaevanionBoardPlan> ScoreBoards(DaevanionOptimizationRequest request,
        IEnumerable<DaevanionBoardPlan> plans)
    {
        var byId = plans.ToDictionary(board => board.BoardId);
        if (request.Mode == DaevanionOptimizationMode.Priority && !DaevanionCombatScoreModel.HasTargets(request))
        {
            var linear = Compute(request);
            return Array.AsReadOnly(byId.Values.Select(plan => plan with
                { Score = plan.NodeIds.Sum(id => linear.Scores[id]) / (decimal)linear.Scale }).ToArray());
        }
        var ordered = DaevanionRuleCatalog.Default.ForClass(request.ClassId).Where(board => byId.ContainsKey(board.Board.Id));
        var selected = new HashSet<int>(); decimal previous = 0;
        var result = new List<DaevanionBoardPlan>();
        foreach (var board in ordered)
        {
            var plan = byId[board.Board.Id]; selected.UnionWith(plan.NodeIds);
            var total = ScoreForNodes(request, selected);
            result.Add(plan with { Score = total - previous }); previous = total;
        }
        return Array.AsReadOnly(result.ToArray());
    }

    internal static long PriorityScoreForNodes(DaevanionOptimizationRequest request, IReadOnlySet<int> selected, DaevanionScoreModel score)
    {
        var total = selected.Sum(id => score.Scores[id]);
        foreach (var state in request.CombatContext?.Skills.Where(state => state.TargetLevel is not null) ?? [])
        {
            var levels = DaevanionCombatScoreModel.Effects(request.ClassId, selected)
                .Where(effect => effect.SkillId == state.SkillId).Sum(effect => effect.LevelIncrement);
            if (levels == 0) continue;
            var baseline = DaevanionCombatScoreModel.BaseLevel(state)
                ?? throw new ArgumentException("Missing pre-board skill level for a target.");
            var excess = Math.Max(0, levels - Math.Max(0, state.TargetLevel!.Value - baseline));
            var weight = SkillUnitScore(request, state.SkillId, score);
            if (weight > 0) total = checked(total - weight * excess);
        }
        return total;
    }

    internal static long SkillUnitScore(DaevanionOptimizationRequest request, int skillId, DaevanionScoreModel score)
    {
        var skill = DaevanionCatalog.Default.GetSkill(skillId)!;
        var kind = skill.Kind == DaevanionSkillKind.Active ? DaevanionPriorityKind.Skill : DaevanionPriorityKind.Passive;
        var weight = Weights(DaevanionPoolSettings.ForPool(request, DaevanionBudgetPool.Normal))[(kind, skillId.ToString(CultureInfo.InvariantCulture))];
        return checked((long)(weight.Numerator * score.Scale / weight.Denominator));
    }

    internal static DaevanionScoreModel Compute(DaevanionOptimizationRequest request)
    {
        var error = DaevanionPoolSettings.Validate(request);
        if (error is not null) throw new ArgumentException(error);
        var boards = DaevanionRuleCatalog.Default.ForClass(request.ClassId);
        var raw = new Dictionary<int, (Rational Score, Rational Ignored)>();
        foreach (var poolBoards in boards.GroupBy(board => board.Pool))
        {
            var preferences = DaevanionPoolSettings.ForPool(request, poolBoards.Key);
            var weights = Weights(preferences);
            var effects = poolBoards.SelectMany(board => board.Nodes).SelectMany(node => node.Node.Effects).ToArray();
            var steps = effects.Where(effect => effect.StatId is not null).GroupBy(effect => effect.StatId!)
                .ToDictionary(group => group.Key, group => group.Select(effect => decimal.Parse(effect.Amount,
                    CultureInfo.InvariantCulture)).Where(amount => amount > 0).Min(), StringComparer.Ordinal);
            var ignored = preferences.Priorities.Where(row => row.Ignored).Select(row => (row.Kind, row.Key)).ToHashSet();
            foreach (var node in poolBoards.SelectMany(board => board.Nodes))
            {
                var score = Rational.Zero; var ignoredUnits = Rational.Zero;
                foreach (var effect in node.Node.Effects)
                {
                    var kind = DaevanionPriorityKind.Stat;
                    string key;
                    Rational units;
                    if (effect.StatId is { } statId)
                    {
                        key = statId;
                        units = Rational.FromDecimal(decimal.Parse(effect.Amount, CultureInfo.InvariantCulture))
                            / Rational.FromDecimal(steps[statId]);
                    }
                    else
                    {
                        var skill = DaevanionCatalog.Default.GetSkill(effect.SkillId!.Value)!;
                        kind = skill.Kind == DaevanionSkillKind.Active ? DaevanionPriorityKind.Skill : DaevanionPriorityKind.Passive;
                        key = skill.Id.ToString(CultureInfo.InvariantCulture);
                        units = new(effect.LevelIncrement, 1);
                    }
                    score += weights[(kind, key)] * units;
                    if (ignored.Contains((kind, key))) ignoredUnits += units;
                }
                raw.Add(node.Node.Id, (score, ignoredUnits));
            }
        }
        var scale = raw.Values.Aggregate(BigInteger.One, (value, entry) =>
            Lcm(Lcm(value, entry.Score.Denominator), entry.Ignored.Denominator));
        if (scale > long.MaxValue / 1_000_000) throw new ArgumentException("Scoring precision exceeds the supported range.");
        var scores = raw.ToDictionary(entry => entry.Key,
            entry => checked((long)(entry.Value.Score.Numerator * (scale / entry.Value.Score.Denominator))));
        var penalties = raw.ToDictionary(entry => entry.Key,
            entry => checked((long)(entry.Value.Ignored.Numerator * (scale / entry.Value.Ignored.Denominator))));
        if (scores.Values.Aggregate(BigInteger.Zero, (total, value) => total + BigInteger.Abs(value)) > long.MaxValue / 4)
            throw new ArgumentException("Scoring magnitude exceeds the supported range.");
        return new((long)scale, scores, penalties, raw.ToDictionary(entry => entry.Key,
            entry => new DaevanionNodeValue(entry.Key, entry.Value.Score.Decimal, entry.Value.Ignored.Decimal)));
    }

    private static Dictionary<(DaevanionPriorityKind, string), Rational> Weights(DaevanionPoolPreferences preferences)
    {
        var result = new Dictionary<(DaevanionPriorityKind, string), Rational>();
        foreach (var group in preferences.Priorities.GroupBy(row => row.Kind))
        {
            var active = group.Where(row => !row.Ignored).OrderByDescending(row => row.Required).ToArray();
            for (var rank = 0; rank < active.Length; rank++)
                result[(group.Key, active[rank].Key)] = new((BigInteger)preferences.Weights.For(group.Key)
                    * (active.Length - rank), active.Length);
            foreach (var row in group.Where(row => row.Ignored)) result[(group.Key, row.Key)] = new(-preferences.Weights.For(group.Key), 20);
        }
        return result;
    }

    private static BigInteger Lcm(BigInteger left, BigInteger right) => left / BigInteger.GreatestCommonDivisor(left, right) * right;

    private readonly record struct Rational
    {
        internal BigInteger Numerator { get; }
        internal BigInteger Denominator { get; }
        internal decimal Decimal => (decimal)Numerator / (decimal)Denominator;
        internal static Rational Zero => new(0, 1);
        internal Rational(BigInteger numerator, BigInteger denominator)
        {
            if (denominator <= 0) throw new ArgumentOutOfRangeException(nameof(denominator));
            var divisor = BigInteger.GreatestCommonDivisor(numerator, denominator);
            Numerator = numerator / divisor; Denominator = denominator / divisor;
        }
        internal static Rational FromDecimal(decimal value)
        {
            var bits = decimal.GetBits(value);
            var numerator = ((BigInteger)(uint)bits[2] << 64) | ((BigInteger)(uint)bits[1] << 32) | (uint)bits[0];
            if (bits[3] < 0) numerator = -numerator;
            return new(numerator, BigInteger.Pow(10, (bits[3] >> 16) & 0xff));
        }
        public static Rational operator +(Rational left, Rational right) => new(left.Numerator * right.Denominator
            + right.Numerator * left.Denominator, left.Denominator * right.Denominator);
        public static Rational operator *(Rational left, Rational right) => new(left.Numerator * right.Numerator, left.Denominator * right.Denominator);
        public static Rational operator /(Rational left, Rational right) => new(left.Numerator * right.Denominator, left.Denominator * right.Numerator);
    }
}

internal sealed record DaevanionScoreModel(long Scale, IReadOnlyDictionary<int, long> Scores,
    IReadOnlyDictionary<int, long> Ignored, IReadOnlyDictionary<int, DaevanionNodeValue> Values);
