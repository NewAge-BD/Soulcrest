using Soulcrest.Core.Daevanion;
using Xunit;

namespace Soulcrest.Core.Tests;

/// <summary>User decision 2026-11-09: no community weights are bundled until a qualified own data basis exists.</summary>
public sealed class DaevanionCombatScoreTests
{
    [Fact]
    public void NoCommunityWeightsOrSkillCurveAreBundled()
    {
        Assert.Empty(DaevanionCombatScoreModel.Rules);
        Assert.Empty(DaevanionCombatScoreModel.NeededStatIds("Cleric"));
        Assert.Null(DaevanionCombatScoreModel.SkillScore(DaevanionSkillKind.Active, 20));
        Assert.Null(DaevanionCombatScoreModel.SkillScore(DaevanionSkillKind.Passive, 20));
        Assert.StartsWith("own-", DaevanionCombatScoreModel.Version, StringComparison.Ordinal);
    }

    [Fact]
    public void CombatScoreRoutesStayUnavailableWhilePriorityPlanningRemainsUsable()
    {
        var priority = DaevanionPlanningDefaults.CreateRequest("Cleric") with
        {
            CombatContext = DaevanionCombatScoreModel.CreateContext("Cleric")
        };
        Assert.Null(DaevanionCombatScoreModel.UnavailableReason(priority));
        var combat = priority with { Mode = DaevanionOptimizationMode.CombatScore };
        Assert.Equal("No qualified combat-score weights are available yet.", DaevanionCombatScoreModel.UnavailableReason(combat));
    }
}
