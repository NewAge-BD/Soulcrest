using Soulcrest.App.Network;
using Soulcrest.Core.Combat;
using Soulcrest.Core.Daevanion;

namespace Soulcrest.App.Services;

public sealed record DaevanionLiveInputResolution(DaevanionOptimizationRequest Request,
    IReadOnlyList<string> MissingRequirements)
{
    public bool Ready => MissingRequirements.Count == 0;
}

/// <summary>Builds a RAM-only solve request; no stored manual quantity is evidence of current character state.</summary>
public static class DaevanionLiveInputResolver
{
    public static DaevanionLiveInputResolution Resolve(DaevanionOptimizationRequest preferences, CharacterCombatSnapshot? character)
    {
        var missing = new List<string>();
        var matches = character is { IsFresh: true, ProfileId.Length: > 0 } && character.ClassId == preferences.ClassId;
        if (!matches) missing.Add("Ein aktueller eigener Charakterstand wird benötigt.");
        var level = matches && character!.CharacterLevel is > 0 ? character.CharacterLevel : null;
        if (level is null) missing.Add("Das aktuelle Charakterlevel fehlt.");
        var budgets = new Dictionary<DaevanionBudgetPool, int>();
        foreach (var pool in Enum.GetValues<DaevanionBudgetPool>())
        {
            var available = level is { } ownLevel && DaevanionRuleCatalog.Default.ForClass(preferences.ClassId)
                .Any(board => board.Pool == pool && board.MinimumLevel <= ownLevel);
            if (level is not null && !available) { budgets[pool] = 0; continue; }
            var state = matches ? character!.PointPools.SingleOrDefault(item => item.Pool == pool) : null;
            var sum = state?.Free is { } free && state.Spent is { } spent ? (long)free + spent : (long?)null;
            var total = state?.Total ?? (sum is >= 0 and <= int.MaxValue ? (int)sum.Value : (int?)null);
            var consistent = state is { Status: NetworkCombatSourceStatus.Complete } && total is >= 0
                && state.Free is not < 0 && state.Spent is not < 0
                && !(state.Free is { } f && state.Spent is { } s && state.Total is { } t && (long)f + s != t)
                && !(state.Capacity is { } capacity && total > capacity);
            if (!consistent) missing.Add("Das bestätigte Gesamtbudget fehlt: " + pool + ".");
            budgets[pool] = consistent ? total!.Value : 0;
        }
        var defaults = DaevanionCombatScoreModel.CreateContext(preferences.ClassId);
        var targets = (preferences.CombatContext?.Skills ?? []).ToDictionary(item => item.SkillId, item => item.TargetLevel);
        var knownSkills = matches && character!.SkillStatus is NetworkCombatSourceStatus.Complete or NetworkCombatSourceStatus.Partial;
        var skills = defaults.Skills.Select(row =>
        {
            var observed = knownSkills ? character!.Skills.GetValueOrDefault(row.SkillId) : null;
            return row with { CurrentLevel = observed?.TotalLevel, CurrentBoardBonus = observed?.AdditionalLevels?.Daevanion,
                TargetLevel = targets.GetValueOrDefault(row.SkillId) };
        }).ToArray();
        var graph = matches && preferences.Mode == DaevanionOptimizationMode.CombatScore ? character!.Graph : null;
        var request = preferences with
        {
            CharacterLevel = level,
            Budgets = new(budgets[DaevanionBudgetPool.Normal], budgets[DaevanionBudgetPool.Ariel], budgets[DaevanionBudgetPool.Azphel]),
            CombatContext = new()
            {
                ModelVersion = graph is null ? DaevanionCombatScoreModel.Version : CombatScoreModel.Version,
                Skills = Array.AsReadOnly(skills), SourceGraph = graph,
                EffectCatalogVersion = graph is null ? null : CombatEffectCatalog.Default.Version,
                CurrentCombatPower = matches && preferences.Mode == DaevanionOptimizationMode.CombatScore ? character!.CurrentCombatScore : null
            }
        };
        if (preferences.Mode == DaevanionOptimizationMode.CombatScore)
        {
            if (request.CombatContext.CurrentCombatPower is null) missing.Add("Der aktuelle Ingame-Combat-Score fehlt.");
            if (graph is null) missing.Add("Das aktuelle Quellenmodell fehlt.");
            else
            {
                var calculation = CombatScoreModel.Calculate(graph);
                if (calculation.Total is null || calculation.Derivation.Status != CombatKnowledgeStatus.Complete)
                {
                    missing.Add("Für die CP-Routenberechnung ist das Quellenmodell noch unvollständig.");
                    missing.AddRange(calculation.Derivation.MissingRequirements);
                }
            }
        }
        var unavailable = preferences.Mode != DaevanionOptimizationMode.CombatScore || graph is not null
            ? DaevanionCombatScoreModel.UnavailableReason(request) : null;
        if (unavailable is not null) missing.Add(unavailable);
        return new(DaevanionPoolSettings.Freeze(request), Array.AsReadOnly(missing.Distinct(StringComparer.Ordinal).ToArray()));
    }
}
