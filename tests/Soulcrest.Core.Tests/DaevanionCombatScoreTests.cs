using Soulcrest.Core.Combat;
using Soulcrest.Core.Daevanion;
using Xunit;

namespace Soulcrest.Core.Tests;

/// <summary>Research 2026-11-09: only measured own weights are bundled; unmeasured board stats keep routes unavailable.</summary>
public sealed class DaevanionCombatScoreTests
{
    [Fact]
    public void OnlyMeasuredBoardStatWeightsAndSkillStepsAreBundled()
    {
        Assert.Equal(["AdditionalHitResistRate", "Critical", "Defense", "HPMax", "MPMax"],
            DaevanionCombatScoreModel.Rules.Select(rule => rule.StatId));
        foreach (var rule in DaevanionCombatScoreModel.Rules)
            Assert.Equal(CombatScoreWeights.Get(rule.StatId)!.Coefficient, rule.Coefficient);
        // Raw-linear weighting needs no pre-board totals.
        Assert.Empty(DaevanionCombatScoreModel.NeededStatIds("Cleric"));
        Assert.Equal(1300, DaevanionCombatScoreModel.SkillScore(DaevanionSkillKind.Active, 12)
            - DaevanionCombatScoreModel.SkillScore(DaevanionSkillKind.Active, 3));
        Assert.Null(DaevanionCombatScoreModel.SkillScore(DaevanionSkillKind.Passive, 20));
        Assert.StartsWith("own-", DaevanionCombatScoreModel.Version, StringComparison.Ordinal);
    }

    [Fact]
    public void CombatScoreRoutesNameTheUnmeasuredStatsWhilePriorityPlanningRemainsUsable()
    {
        var priority = DaevanionPlanningDefaults.CreateRequest("Cleric") with
        {
            Budgets = new(200, 100, 100), CombatContext = DaevanionCombatScoreModel.CreateContext("Cleric")
        };
        Assert.Null(DaevanionCombatScoreModel.UnavailableReason(priority));
        var combat = priority with { Mode = DaevanionOptimizationMode.CombatScore };
        var reason = DaevanionCombatScoreModel.UnavailableReason(combat);
        Assert.NotNull(reason);
        Assert.StartsWith("No measured combat-score weight yet for: ", reason, StringComparison.Ordinal);
        Assert.Contains("CombatSpeed", reason, StringComparison.Ordinal);
        var listed = reason["No measured combat-score weight yet for: ".Length..].TrimEnd('.').Split(", ");
        Assert.DoesNotContain("Critical", listed);
        Assert.DoesNotContain("HPMax", listed);
    }
}
