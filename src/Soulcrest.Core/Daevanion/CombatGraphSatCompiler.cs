using System.Numerics;
using Google.OrTools.Sat;
using Soulcrest.Core.Combat;

namespace Soulcrest.Core.Daevanion;

/// <summary>Exact integer/rational translation of the shared source model. No per-node CP approximation.</summary>
internal sealed class CombatGraphSatCompiler(CpModel model, List<IntVar> allVariables, CombatEffectCatalog catalog)
{
    private int _next;
    private readonly Dictionary<string, Aggregate> _stats = new(StringComparer.Ordinal);
    private readonly Dictionary<int, (DaevanionSkillKind Kind, Number Level)> _skills = [];
    private sealed class Aggregate { internal readonly List<Number> Flat = []; internal readonly List<Number> Increase = []; }
    private sealed record Number(LinearExpr Expression, long Minimum, long Maximum, long Denominator);

    internal LinearExpr Compile(DaevanionOptimizationRequest request, IReadOnlyList<DaevanionNodeRule> nodes,
        IReadOnlyDictionary<int, BoolVar> selected, IReadOnlySet<int>? reachableNodeIds = null)
    {
        var graph = request.CombatContext?.SourceGraph ?? throw new ArgumentException("Missing source model.");
        var current = CombatScoreModel.Calculate(graph, catalog);
        if (current.Total is not { } before || current.Derivation.Status != CombatKnowledgeStatus.Complete)
            throw new ArgumentException("Incomplete source model.");
        foreach (var source in graph.Sources.Where(source => source.Kind != CombatSourceKind.Daevanion))
        {
            if (source.Status != CombatKnowledgeStatus.Complete) throw new ArgumentException("Incomplete source: " + source.Id);
            foreach (var effect in source.Effects)
            {
                if (effect.Application == CombatApplication.Inactive || effect.Value == 0) continue;
                if (effect.Application != CombatApplication.Confirmed) throw new ArgumentException("Unqualified source effect.");
                Add(effect.StatId, effect.Operation, Constant(effect.Value));
            }
            foreach (var skill in source.Skills)
            {
                if (skill.Application == CombatApplication.Inactive || skill.Level == 0) continue;
                if (skill.Application != CombatApplication.Confirmed || skill.Level is null)
                    throw new ArgumentException("Unqualified skill source component.");
                AddSkill(skill.SkillId, skill.Kind, Constant(skill.Level.Value));
            }
        }
        foreach (var node in nodes)
        {
            if (reachableNodeIds is not null && !reachableNodeIds.Contains(node.Node.Id)) continue;
            foreach (var effect in node.Node.Effects)
            {
                var variable = new Number(selected[node.Node.Id], 0, 1, 1);
                if (effect.StatId is { } statId) Add(statId, CombatEffectOperation.Flat, Multiply(variable, Constant(DaevanionCombatScoreModel.Amount(effect))));
                else if (effect.SkillId is { } skillId)
                    AddSkill(skillId, DaevanionCatalog.Default.GetSkill(skillId)!.Kind, Multiply(variable, Constant(effect.LevelIncrement)));
            }
        }
        foreach (var state in request.CombatContext!.Skills.Where(state => state.TargetLevel is not null))
        {
            if (!_skills.TryGetValue(state.SkillId, out var skill)) continue;
            var baseline = DaevanionCombatScoreModel.BaseLevel(state) ?? throw new ArgumentException("Missing skill baseline.");
            var maximum = Math.Max(baseline, state.TargetLevel!.Value);
            _skills[state.SkillId] = (skill.Kind, Minimum(skill.Level, maximum));
        }
        foreach (var rule in catalog.PassiveEffects)
        {
            if (!_skills.TryGetValue(rule.SkillId, out var skill) || skill.Level.Maximum == 0) continue;
            if (rule.Application == CombatApplication.Inactive) continue;
            if (!CombatStatDerivation.IsPassiveApplicable(graph, rule))
                throw new ArgumentException("Unqualified passive application: " + rule.RuleId);
            Add(rule.StatId, rule.Operation, Lookup(skill.Level, level => rule.ValueAt(level), "passive"));
        }
        var primaryIds = catalog.PrimaryStatCurves.Select(curve => curve.PrimaryStatId).ToHashSet(StringComparer.Ordinal);
        foreach (var curve in catalog.PrimaryStatCurves)
        {
            if (!_stats.TryGetValue(curve.PrimaryStatId, out var primary)) continue;
            var level = StatValue(curve.PrimaryStatId, primary);
            var rows = curve.Values.ToDictionary(value => value.Level);
            foreach (var key in curve.Values.SelectMany(row => row.Effects).Select(effect => (effect.StatId, effect.Operation)).Distinct())
            {
                if (primaryIds.Contains(key.StatId)) throw new ArgumentException("Recursive primary-stat dependency.");
                var amount = Lookup(level, ownLevel =>
                {
                    if (ownLevel == 0) return 0;
                    if (!rows.TryGetValue(ownLevel, out var row)) return null;
                    return row.Effects.Where(effect => effect.StatId == key.StatId && effect.Operation == key.Operation)
                        .Sum(effect => catalog.NormalizeEffect(effect.StatId, effect.RawValue)
                            ?? throw new ArgumentException("Unknown primary effect scale."));
                }, "primary");
                Add(key.StatId, key.Operation, amount);
            }
        }
        foreach (var relation in catalog.StatRelations)
            if (_stats.TryGetValue(relation.ModifierStatId, out var modifier))
                Add(relation.StatId, relation.Operation, RawStat(modifier));
        var weights = CombatScoreModel.RulesFor(catalog).ToDictionary(rule => rule.StatId, rule => rule.Coefficient, StringComparer.Ordinal);
        var intermediates = primaryIds.Concat(catalog.StatRelations.Select(relation => relation.ModifierStatId)).ToHashSet(StringComparer.Ordinal);
        var scoreTerms = new List<Number>();
        foreach (var (id, aggregate) in _stats)
        {
            if (!weights.TryGetValue(id, out var weight))
            {
                if (intermediates.Contains(id)) continue;
                var value = StatValue(id, aggregate);
                if (value.Minimum != 0 || value.Maximum != 0) throw new ArgumentException("Missing combat-score weight: " + id);
                continue;
            }
            if (weight != 0) scoreTerms.Add(Multiply(StatValue(id, aggregate), Constant(weight)));
        }
        foreach (var skill in _skills.Values)
            scoreTerms.Add(Lookup(skill.Level, level => CombatScoreModel.SkillScore(skill.Kind, level), "skillCP"));
        var delta = Sum(scoreTerms.Append(Constant(-before)));
        return Round(Sum([delta, Constant(0.5m)]), CombatRounding.Floor).Expression;
    }

    private void Add(string id, CombatEffectOperation operation, Number value)
    {
        if (!_stats.TryGetValue(id, out var aggregate)) _stats[id] = aggregate = new();
        (operation == CombatEffectOperation.Flat ? aggregate.Flat : aggregate.Increase).Add(value);
    }

    private void AddSkill(int id, DaevanionSkillKind kind, Number level)
    {
        if (_skills.TryGetValue(id, out var existing))
        {
            if (existing.Kind != kind) throw new ArgumentException("Conflicting skill kind.");
            _skills[id] = (kind, Sum([existing.Level, level]));
        }
        else _skills[id] = (kind, level);
    }

    private Number RawStat(Aggregate value) => Multiply(Sum(value.Flat), Sum([Constant(1), Divide(Sum(value.Increase), 100)]));
    private Number StatValue(string id, Aggregate value)
    {
        var definition = catalog.GetStat(id) ?? throw new ArgumentException("Unknown stat definition: " + id);
        var raw = RawStat(value);
        return definition.Unit == CombatStatUnit.Percent || definition.Rounding == CombatRounding.None ? raw : Round(raw, definition.Rounding);
    }

    private Number Lookup(Number index, Func<int, decimal?> valueAt, string name)
    {
        if (index.Denominator != 1 || index.Minimum < 0 || index.Maximum > 10000)
            throw new ArgumentException("Unsupported or nonintegral model lookup domain: " + name);
        var values = Enumerable.Range(checked((int)index.Minimum), checked((int)(index.Maximum - index.Minimum + 1)))
            .Select(level => valueAt(level) is { } value ? Constant(value)
                : throw new ArgumentException("Missing qualified model curve row: " + name + "/" + level)).ToArray();
        var denominator = CommonDenominator(values);
        var numbers = values.Select(value => Safe((BigInteger)value.Minimum * (denominator / value.Denominator))).ToArray();
        var result = New(numbers.Min(), numbers.Max(), name);
        var offset = New(0, index.Maximum - index.Minimum, name + "Index");
        model.Add(offset == index.Expression - index.Minimum);
        model.AddElement(offset, numbers, result);
        return new(result, numbers.Min(), numbers.Max(), denominator);
    }

    private Number Minimum(Number value, long maximum)
    {
        if (value.Denominator != 1) throw new ArgumentException("Skill levels must be integral.");
        if (value.Maximum <= maximum) return value;
        var low = Math.Min(value.Minimum, maximum); var high = Math.Min(value.Maximum, maximum);
        var variable = New(low, high, "targetSkillLevel");
        model.AddMinEquality(variable, new[] { value.Expression, LinearExpr.Constant(maximum) });
        return new(variable, low, high, 1);
    }

    private Number Round(Number value, CombatRounding rounding)
    {
        if (value.Denominator == 1 || rounding == CombatRounding.None) return value;
        var denominator = value.Denominator;
        long Integral(long numerator) => rounding == CombatRounding.Ceiling
            ? Safe(-Floor(-(BigInteger)numerator, denominator)) : Safe(Floor(numerator, denominator));
        var minimum = Integral(value.Minimum); var maximum = Integral(value.Maximum);
        var rounded = New(minimum, maximum, "roundedStat");
        if (rounding == CombatRounding.Ceiling)
        {
            model.Add(rounded * denominator >= value.Expression);
            model.Add(rounded * denominator <= value.Expression + (denominator - 1));
        }
        else
        {
            model.Add(rounded * denominator <= value.Expression);
            model.Add(rounded * denominator >= value.Expression - (denominator - 1));
        }
        return new(rounded, minimum, maximum, 1);
    }

    private Number Sum(IEnumerable<Number> expressions)
    {
        var values = expressions.ToArray();
        if (values.Length == 0) return Constant(0);
        var denominator = CommonDenominator(values);
        var coefficients = values.Select(value => denominator / value.Denominator).ToArray();
        var minimum = Safe(values.Select((value, i) => (BigInteger)value.Minimum * coefficients[i]).Aggregate(BigInteger.Zero, (a, b) => a + b));
        var maximum = Safe(values.Select((value, i) => (BigInteger)value.Maximum * coefficients[i]).Aggregate(BigInteger.Zero, (a, b) => a + b));
        return new(LinearExpr.WeightedSum(values.Select(value => value.Expression), coefficients), minimum, maximum, denominator);
    }

    private Number Multiply(Number left, Number right)
    {
        var denominator = Safe((BigInteger)left.Denominator * right.Denominator);
        if (left.Minimum == left.Maximum || right.Minimum == right.Maximum)
        {
            if (left.Minimum != left.Maximum) (left, right) = (right, left);
            var coefficient = left.Minimum;
            var divisor = (long)BigInteger.GreatestCommonDivisor(BigInteger.Abs(coefficient), denominator);
            coefficient /= divisor; denominator /= divisor;
            var a = Safe((BigInteger)right.Minimum * coefficient); var b = Safe((BigInteger)right.Maximum * coefficient);
            return new(right.Expression * coefficient, Math.Min(a, b), Math.Max(a, b), denominator);
        }
        var products = new[] { (BigInteger)left.Minimum * right.Minimum, (BigInteger)left.Minimum * right.Maximum,
            (BigInteger)left.Maximum * right.Minimum, (BigInteger)left.Maximum * right.Maximum };
        var minimum = Safe(products.Min()); var maximum = Safe(products.Max());
        var variable = New(minimum, maximum, "statProduct");
        model.AddMultiplicationEquality(variable, new LinearExpr[] { AsVariable(left), AsVariable(right) });
        return new(variable, minimum, maximum, denominator);
    }

    private IntVar AsVariable(Number number)
    {
        var variable = New(number.Minimum, number.Maximum, "factor");
        model.Add(variable == number.Expression);
        return variable;
    }

    private static Number Divide(Number value, long denominator) => value with
        { Denominator = Safe((BigInteger)value.Denominator * denominator) };
    private static Number Constant(decimal value)
    {
        var bits = decimal.GetBits(value);
        var numerator = ((BigInteger)(uint)bits[2] << 64) | ((BigInteger)(uint)bits[1] << 32) | (uint)bits[0];
        if (bits[3] < 0) numerator = -numerator;
        var denominator = BigInteger.Pow(10, (bits[3] >> 16) & 0xff);
        var divisor = BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator), denominator);
        numerator /= divisor; denominator /= divisor;
        var integer = Safe(numerator);
        return new(LinearExpr.Constant(integer), integer, integer, Safe(denominator));
    }

    private IntVar New(long minimum, long maximum, string name)
    {
        var variable = model.NewIntVar(minimum, maximum, name + ++_next);
        allVariables.Add(variable);
        return variable;
    }
    private static long CommonDenominator(IEnumerable<Number> values) => Safe(values.Aggregate(BigInteger.One,
        (current, value) => current / BigInteger.GreatestCommonDivisor(current, value.Denominator) * value.Denominator));
    private static BigInteger Floor(BigInteger numerator, BigInteger denominator)
    {
        var quotient = BigInteger.DivRem(numerator, denominator, out var remainder);
        return remainder.Sign < 0 ? quotient - 1 : quotient;
    }
    private static long Safe(BigInteger value) => value > long.MaxValue / 8 || value < long.MinValue / 8
        ? throw new ArgumentException("Exact source-model precision exceeds the supported integer range.") : (long)value;
}
