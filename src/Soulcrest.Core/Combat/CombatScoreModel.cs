using Soulcrest.Core.Daevanion;
using System.Collections.Frozen;
using System.Runtime.CompilerServices;

namespace Soulcrest.Core.Combat;

public sealed record CombatScoreValue(decimal Unrounded, decimal Rounded, bool IsComplete, bool HasUnknownTerms);
public sealed record CombatCalculation(decimal? Total, decimal KnownSubtotal, CombatDerivation Derivation);

/// <summary>
/// The own versioned weighted model backend, not the official combat-power formula. Research 2026-11-09: the score is
/// linear in the raw components; every flat component and its percentage modifier carry separate measured weights
/// (<see cref="CombatScoreWeights"/>). Without a measured weight a nonzero term stays unknown.
/// Counterfactuals re-evaluate the entire source graph, including skills, passive curves and stat multipliers.
/// </summary>
public static class CombatScoreModel
{
    private static readonly ConditionalWeakTable<CombatEffectCatalog, CachedRules> RuleCache = new();
    public const string Version = "own-raw-linear-source-counterfactual-v2";
    public static IReadOnlyList<CombatScoreRule> Rules => RulesFor(CombatEffectCatalog.Default);

    public static IReadOnlyList<CombatScoreRule> RulesFor(CombatEffectCatalog catalog) => RuleCache.GetValue(catalog, CreateRules).Rows;

    private static CachedRules CreateRules(CombatEffectCatalog catalog)
    {
        var rules = catalog.ScoreRules.ToDictionary(row => row.StatId, StringComparer.Ordinal);
        foreach (var weight in CombatScoreWeights.Stats)
            rules.TryAdd(weight.StatId, new(weight.StatId, weight.Coefficient, weight.Unit));
        // Primary/deity stats act only through their curve effects; they never carry a direct weight.
        var primaryIds = catalog.PrimaryStatCurves.Select(curve => curve.PrimaryStatId).ToHashSet(StringComparer.Ordinal);
        var modifiers = catalog.StatRelations.Where(relation => relation.Operation == CombatEffectOperation.IncreasePercent)
            .ToDictionary(relation => relation.StatId, relation => relation.ModifierStatId, StringComparer.Ordinal);
        var rows = Array.AsReadOnly(rules.Values.Where(rule => !primaryIds.Contains(rule.StatId))
            .OrderBy(rule => rule.StatId, StringComparer.Ordinal).ToArray());
        return new(rows, rows.ToFrozenDictionary(rule => rule.StatId, StringComparer.Ordinal), primaryIds.ToFrozenSet(StringComparer.Ordinal),
            modifiers.ToFrozenDictionary(StringComparer.Ordinal), modifiers.Values.ToFrozenSet(StringComparer.Ordinal));
    }

    public static int? SkillScore(DaevanionSkillKind kind, int level) => CombatScoreWeights.SkillScore(kind, level);
    public static decimal RoundScore(decimal value) => decimal.Floor(value + 0.5m);

    public static CombatScoreValue Score(CombatSourceGraph graph, CombatEffectCatalog? catalog = null)
    {
        catalog ??= CombatEffectCatalog.Default;
        return ScoreDerivation(CombatStatDerivation.Derive(graph, catalog), catalog).Value;
    }

    /// <summary>Fast solver path: one derivation, no per-source counterfactual reports. Total is unrounded.</summary>
    public static CombatCalculation Calculate(CombatSourceGraph graph, CombatEffectCatalog? catalog = null)
    {
        catalog ??= CombatEffectCatalog.Default;
        var derivation = CombatStatDerivation.Derive(graph, catalog);
        var score = ScoreDerivation(derivation, catalog).Value;
        return new(score.IsComplete ? score.Unrounded : null, score.Unrounded, derivation);
    }

    public static CombatEvaluation Evaluate(CombatSourceGraph graph, CombatEffectCatalog? catalog = null)
    {
        catalog ??= CombatEffectCatalog.Default;
        graph = Freeze(graph);
        var derivation = CombatStatDerivation.Derive(graph, catalog);
        var scored = ScoreDerivation(derivation, catalog, includeComposition: true);
        if (!scored.Value.IsComplete)
        {
            const string reason = "Full source-stat losses and combat-score counterfactuals require complete character source coverage.";
            return Assemble(Array.AsReadOnly(graph.Sources.Select(source => new CombatSourceReport
            {
                SourceId = source.Id, Kind = source.Kind, Status = source.Status, Effects = source.Effects, Skills = source.Skills,
                Reason = source.Reason is { Length: > 0 } original ? original + " " + reason : reason
            }).ToArray()));
        }
        var beforeStats = derivation.Stats.ToDictionary(row => row.StatId, StringComparer.Ordinal);
        var reports = new List<CombatSourceReport>();
        foreach (var source in graph.Sources)
        {
            var without = graph with { Sources = Array.AsReadOnly(graph.Sources.Where(row => row.Id != source.Id).ToArray()) };
            var removedDerivation = CombatStatDerivation.Derive(without, catalog);
            var removedScore = ScoreDerivation(removedDerivation, catalog).Value;
            var removedStats = removedDerivation.Stats.ToDictionary(row => row.StatId, StringComparer.Ordinal);
            var changes = beforeStats.Keys.Concat(removedStats.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
                .Select(id =>
                {
                    var current = beforeStats.TryGetValue(id, out var currentRow) ? currentRow.Value : 0;
                    var previous = removedStats.TryGetValue(id, out var previousRow) ? previousRow.Value : 0;
                    return new CombatStatChange(id, previous, current,
                        current is { } after && previous is { } before ? after - before : null);
                }).Where(row => row.Delta != 0 || row.WithSource is null || row.WithoutSource is null).ToArray();
            var canAttribute = scored.Value.IsComplete && removedScore.IsComplete;
            reports.Add(new()
            {
                SourceId = source.Id, Kind = source.Kind, Status = source.Status, Effects = source.Effects, Skills = source.Skills,
                StatContributions = Array.AsReadOnly(changes),
                ModelLoss = canAttribute ? RoundScore(scored.Value.Unrounded - removedScore.Unrounded) : null,
                Reason = source.Reason
            });
        }
        return Assemble(Array.AsReadOnly(reports.ToArray()));

        CombatEvaluation Assemble(IReadOnlyList<CombatSourceReport> rows) => new()
        {
            Status = scored.Value.IsComplete ? CombatKnowledgeStatus.Complete : derivation.Status == CombatKnowledgeStatus.Complete
                ? CombatKnowledgeStatus.Partial : derivation.Status,
            ModelTotal = scored.Value.IsComplete ? scored.Value.Rounded : null,
            KnownSubtotal = scored.Value.Rounded, Composition = scored.Composition!, Sources = rows, Stats = derivation.Stats,
            Skills = derivation.Skills, Passives = derivation.Passives, NativeComparisons = derivation.NativeComparisons, SkillComparisons = derivation.SkillComparisons,
            MissingRequirements = Array.AsReadOnly(derivation.MissingRequirements.Concat(scored.Missing).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray())
        };
    }

    public static CombatCounterfactual Counterfactual(CombatSourceGraph graph, IEnumerable<string> removeSourceIds,
        IEnumerable<CombatSource> addSources, CombatEffectCatalog? catalog = null)
    {
        var before = Evaluate(graph, catalog);
        var replacement = ReplaceSources(graph, removeSourceIds, addSources);
        var after = Evaluate(replacement, catalog);
        var firstScore = Score(graph, catalog); var secondScore = Score(replacement, catalog);
        return new(before, after,
            firstScore.IsComplete && secondScore.IsComplete
                ? RoundScore(secondScore.Unrounded - firstScore.Unrounded) : null,
            RoundScore(secondScore.Unrounded - firstScore.Unrounded));
    }

    public static CombatSourceGraph ReplaceSources(CombatSourceGraph graph, IEnumerable<string> removeSourceIds,
        IEnumerable<CombatSource> addSources)
    {
        var removed = removeSourceIds.ToHashSet(StringComparer.Ordinal);
        return Freeze(graph with { Sources = graph.Sources.Where(source => !removed.Contains(source.Id)).Concat(addSources).ToArray() });
    }

    public static CombatSourceGraph Freeze(CombatSourceGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        CombatStatDerivation.ValidateGraph(graph);
        return graph with
        {
            Sources = Array.AsReadOnly(graph.Sources.Select(source => source with
            {
                Effects = Array.AsReadOnly(source.Effects.ToArray()), Skills = Array.AsReadOnly(source.Skills.ToArray())
            }).ToArray()), NativeTotals = Array.AsReadOnly(graph.NativeTotals.ToArray()),
            NativeSkillTotals = Array.AsReadOnly(graph.NativeSkillTotals.ToArray()),
            ActiveAbnormalIds = Array.AsReadOnly(graph.ActiveAbnormalIds.Distinct().Order().ToArray()),
            ActiveAbnormals = Array.AsReadOnly(graph.ActiveAbnormals.ToArray())
        };
    }

    private static (CombatScoreValue Value, IReadOnlyList<string> Missing, CombatScoreComposition? Composition) ScoreDerivation(
        CombatDerivation derivation, CombatEffectCatalog catalog, bool includeComposition = false)
    {
        // Share the existing numerical rule set; there is no second independently maintained Daevanion model.
        var cached = RuleCache.GetValue(catalog, CreateRules);
        var rules = cached.ById;
        var intermediates = cached.Intermediates;
        var missing = new List<string>();
        // The solver's fast path keeps the same arithmetic without allocating presentation terms.
        var statTerms = includeComposition ? new List<CombatScoreStatTerm>() : null;
        var skillTerms = includeComposition ? new List<CombatScoreSkillTerm>() : null;
        decimal statSubtotal = 0, skillSubtotal = 0;
        decimal value = 0;
        var unknown = derivation.Status is CombatKnowledgeStatus.Conflict or CombatKnowledgeStatus.Stale;
        foreach (var stat in derivation.Stats)
        {
            if (intermediates.Contains(stat.StatId))
            {
                statTerms?.Add(new(stat.StatId, stat.Unit, stat.Value, null, null, true, stat.Status));
                continue;
            }
            // A percentage modifier is scored once as the target's total increase (incl. direct % effects).
            if (cached.Modifiers.Contains(stat.StatId)) continue;
            var unknownStat = stat.Status == CombatKnowledgeStatus.Unknown;
            AddTerm(stat.StatId, stat.Unit, unknownStat ? null : stat.Flat, stat.Status);
            if (stat.IncreasePercent != 0 || unknownStat)
            {
                if (cached.ModifierOf.TryGetValue(stat.StatId, out var modifier))
                    AddTerm(modifier, CombatStatUnit.Percent, unknownStat ? null : stat.IncreasePercent, stat.Status);
                else
                {
                    unknown = true;
                    missing.Add("Missing combat-score weight mapping: " + stat.StatId + " increase.");
                }
            }
        }
        // Modifiers without a target row in this derivation still count with their own value.
        foreach (var stat in derivation.Stats.Where(row => cached.Modifiers.Contains(row.StatId)))
            if (!derivation.Stats.Any(row => cached.ModifierOf.TryGetValue(row.StatId, out var m) && m == stat.StatId))
                AddTerm(stat.StatId, stat.Unit, stat.Status == CombatKnowledgeStatus.Unknown ? null : stat.Flat, stat.Status);
        foreach (var skill in derivation.Skills)
        {
            if (skill.Level is not { } level)
            {
                skillTerms?.Add(new(skill.SkillId, skill.Kind, null, null, skill.Status));
                unknown = true; continue;
            }
            if (SkillScore(skill.Kind, level) is not { } contribution)
            {
                skillTerms?.Add(new(skill.SkillId, skill.Kind, level, null, skill.Status));
                unknown = true; continue;
            }
            skillTerms?.Add(new(skill.SkillId, skill.Kind, level, contribution, skill.Status));
            value += contribution;
            if (includeComposition) skillSubtotal += contribution;
        }
        var rounded = RoundScore(value);
        var isComplete = derivation.Status == CombatKnowledgeStatus.Complete && !unknown;
        void AddTerm(string id, CombatStatUnit unit, decimal? termValue, CombatKnowledgeStatus status)
        {
            if (termValue == 0) return;
            if (!rules.TryGetValue(id, out var rule))
            {
                statTerms?.Add(new(id, unit, termValue, null, null, false, status));
                unknown = true;
                missing.Add("Missing combat-score weight mapping: " + id + ".");
                return;
            }
            if (termValue is not { } known)
            {
                statTerms?.Add(new(id, unit, null, rule.Coefficient, null, false, status));
                unknown = true; return;
            }
            var contribution = known * rule.Coefficient;
            statTerms?.Add(new(id, unit, known, rule.Coefficient, contribution, false, status));
            value += contribution;
            if (includeComposition) statSubtotal += contribution;
        }
        return (new(value, rounded, isComplete, unknown), missing, includeComposition ? new()
        {
            Stats = Array.AsReadOnly(statTerms!.ToArray()), Skills = Array.AsReadOnly(skillTerms!.ToArray()),
            KnownStatSubtotal = statSubtotal, KnownSkillSubtotal = skillSubtotal, UnroundedSubtotal = value,
            RoundedSubtotal = rounded, RoundingAdjustment = rounded - value, IsComplete = isComplete
        } : null);
    }

    private sealed record CachedRules(IReadOnlyList<CombatScoreRule> Rows,
        FrozenDictionary<string, CombatScoreRule> ById, FrozenSet<string> Intermediates,
        FrozenDictionary<string, string> ModifierOf, FrozenSet<string> Modifiers);
}
