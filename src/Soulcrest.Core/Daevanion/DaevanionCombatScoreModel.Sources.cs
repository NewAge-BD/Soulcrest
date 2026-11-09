using Soulcrest.Core.Combat;

namespace Soulcrest.Core.Daevanion;

public static partial class DaevanionCombatScoreModel
{
    internal static string ModelVersionFor(DaevanionOptimizationRequest request) => request.CombatContext?.SourceGraph is null
        ? Version : CombatScoreModel.Version;

    private static string? SourceGraphUnavailableReason(DaevanionOptimizationRequest request)
    {
        var context = request.CombatContext!;
        if (context.ModelVersion != CombatScoreModel.Version || context.SourceGraph!.ClassId != request.ClassId)
            return "The character source graph belongs to a different model version or class.";
        if (context.EffectCatalogVersion is { } version && version != CombatEffectCatalog.Default.Version)
            return "The saved source effect catalog has changed; recalculate using current character data.";
        var reachableSkills = PossibleEffects(request).Where(effect => effect.SkillId is not null).Select(effect => effect.SkillId!.Value).ToHashSet();
        foreach (var state in context.Skills.Where(state => reachableSkills.Contains(state.SkillId)))
            if ((request.Mode == DaevanionOptimizationMode.CombatScore || state.TargetLevel is not null)
                && BaseLevel(state) is null) return "Missing current skill level or Daevanion component: " + state.SkillId + ".";
        if (request.Mode != DaevanionOptimizationMode.CombatScore) return null;
        var before = CombatScoreModel.Calculate(context.SourceGraph);
        if (before.Total is null || before.Derivation.Status != CombatKnowledgeStatus.Complete)
            return "The current character sources, effect rules or application conditions are incomplete.";
        foreach (var rule in CombatEffectCatalog.Default.PassiveEffects.Where(rule => reachableSkills.Contains(rule.SkillId)))
            if (rule.Application != CombatApplication.Inactive && !CombatStatDerivation.IsPassiveApplicable(context.SourceGraph, rule))
                return "Passive application is not qualified for a reachable skill: " + rule.SkillId + ".";
        var empty = CombatScoreModel.Calculate(SourceGraphForNodes(request, new HashSet<int>(), false));
        return empty.Total is null || empty.Derivation.Status != CombatKnowledgeStatus.Complete
            ? "The character source model without the current boards is incomplete." : null;
    }

    internal static CombatSourceGraph SourceGraphForNodes(DaevanionOptimizationRequest request, IReadOnlySet<int> selected, bool capTargets)
    {
        var graph = request.CombatContext?.SourceGraph ?? throw new ArgumentException("Missing character source graph.");
        // Validation also rejects foreign node IDs, including a route from another class.
        _ = Effects(request.ClassId, selected).ToArray();
        var remaining = request.CombatContext!.Skills.Where(state => capTargets && state.TargetLevel is not null)
            .ToDictionary(state => state.SkillId, state => Math.Max(0, state.TargetLevel!.Value - (BaseLevel(state)
                ?? throw new ArgumentException("Missing skill baseline for target."))));
        var sources = graph.Sources.Where(source => source.Kind != CombatSourceKind.Daevanion).ToList();
        foreach (var board in DaevanionRuleCatalog.Default.ForClass(request.ClassId))
        {
            var effects = board.Nodes.Where(node => selected.Contains(node.Node.Id)).SelectMany(node => node.Node.Effects).ToArray();
            var stats = effects.Where(effect => effect.StatId is not null).GroupBy(effect => effect.StatId!, StringComparer.Ordinal)
                .Select(group => new CombatEffect(group.Key, group.Sum(Amount), Application: CombatApplication.Confirmed)).ToArray();
            var skills = effects.Where(effect => effect.SkillId is not null).GroupBy(effect => effect.SkillId!.Value).Select(group =>
            {
                var amount = group.Sum(effect => effect.LevelIncrement);
                if (remaining.TryGetValue(group.Key, out var wanted))
                {
                    amount = Math.Min(amount, wanted); remaining[group.Key] = wanted - amount;
                }
                return new CombatSkill(group.Key, DaevanionCatalog.Default.GetSkill(group.Key)!.Kind, amount, CombatApplication.Confirmed);
            }).ToArray();
            sources.Add(new() { Id = "planned-daevanion:" + board.Board.Id, Kind = CombatSourceKind.Daevanion,
                Status = CombatKnowledgeStatus.Complete, Effects = Array.AsReadOnly(stats), Skills = Array.AsReadOnly(skills) });
        }
        return graph with { Sources = Array.AsReadOnly(sources.ToArray()) };
    }

    private static DaevanionCombatEstimate EstimateSourceGraph(DaevanionOptimizationRequest request, IEnumerable<int> nodeIds)
    {
        var selected = nodeIds.ToHashSet();
        var context = request.CombatContext!;
        var before = CombatScoreModel.Calculate(context.SourceGraph!);
        var actual = CombatScoreModel.Calculate(SourceGraphForNodes(request, selected, false));
        var objective = HasTargets(request) ? CombatScoreModel.Calculate(SourceGraphForNodes(request, selected, true)) : actual;
        if (before.Total is not { } current || actual.Total is not { } next || objective.Total is not { } capped
            || before.Derivation.Status != CombatKnowledgeStatus.Complete || actual.Derivation.Status != CombatKnowledgeStatus.Complete
            || objective.Derivation.Status != CombatKnowledgeStatus.Complete)
            throw new ArgumentException("Incomplete source graph cannot produce a full route estimate.");
        var rules = CombatScoreModel.RulesFor(CombatEffectCatalog.Default).ToDictionary(rule => rule.StatId, StringComparer.Ordinal);
        var previous = before.Derivation.Stats.ToDictionary(stat => stat.StatId, StringComparer.Ordinal);
        var changed = actual.Derivation.Stats.ToDictionary(stat => stat.StatId, StringComparer.Ordinal);
        var raw = Effects(request.ClassId, selected).Where(effect => effect.StatId is not null)
            .GroupBy(effect => effect.StatId!, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Sum(Amount), StringComparer.Ordinal);
        var previews = previous.Keys.Concat(changed.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(id =>
        {
            decimal? oldValue = previous.TryGetValue(id, out var old) ? old.Value : 0;
            decimal? newValue = changed.TryGetValue(id, out var value) ? value.Value : 0;
            var delta = oldValue is { } a && newValue is { } b ? b - a : (decimal?)null;
            return new DaevanionCombatStatPreview(id, oldValue, raw.GetValueOrDefault(id), newValue, delta,
                rules.TryGetValue(id, out var rule) && delta is { } gain ? gain * rule.Coefficient : 0);
        }).Where(row => row.TotalGain != 0 || row.RawGain != 0).ToArray();
        var skillPreviews = SkillPreviews(request, selected);
        var actualDelta = CombatScoreModel.RoundScore(next - current);
        return new(CombatScoreModel.RoundScore(capped - current), actualDelta,
            context.CurrentCombatPower is { } native ? native + actualDelta : null,
            Array.AsReadOnly(previews), skillPreviews, skillPreviews.Sum(row => row.ExcessLevels));
    }
}
