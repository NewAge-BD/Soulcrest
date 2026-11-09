using Soulcrest.Core.Daevanion;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class DaevanionPlanSummaryTests
{
    private static DaevanionCatalog Catalog => DaevanionCatalog.Default;

    [Fact]
    public void CompleteSixBoardsKeepExactStatUnitsAndAllSkillAndPassiveLevels()
    {
        var plans = FullPlans();
        var summary = DaevanionPlanSummary.Create("Cleric", plans);
        Assert.Equal(5300m, summary.Stats.Single(stat => stat.StatId == "HPMax").Amount);
        Assert.Equal(2750m, summary.Stats.Single(stat => stat.StatId == "MPMax").Amount);
        var combatSpeed = summary.Stats.Single(stat => stat.StatId == "CombatSpeed");
        Assert.Equal(3m, combatSpeed.Amount);
        Assert.Equal(DaevanionStatUnit.Percent, combatSpeed.Unit);
        Assert.Equal(DaevanionStatUnit.Points, summary.Stats.Single(stat => stat.StatId == "HPMax").Unit);
        Assert.Equal(22, summary.Skills.Count);
        Assert.Equal(12, summary.Skills.Count(skill => skill.Kind == DaevanionSkillKind.Active));
        Assert.Equal(10, summary.Skills.Count(skill => skill.Kind == DaevanionSkillKind.Passive));
        Assert.All(summary.Skills, skill => Assert.Equal(4, skill.Levels));
    }

    [Fact]
    public void BoardFilterMatchesTheCurrentTableFilterWithoutMixingClassOrBoardNodes()
    {
        var summary = DaevanionPlanSummary.Create("Cleric", FullPlans(), "nezekan");
        Assert.Equal(1100m, summary.Stats.Single(stat => stat.StatId == "HPMax").Amount);
        Assert.DoesNotContain(summary.Stats, stat => stat.StatId == "PvEAmplifyDamage");
        Assert.Equal(12, summary.Skills.Count(skill => skill.Kind == DaevanionSkillKind.Active));
        Assert.All(summary.Skills.Where(skill => skill.Kind == DaevanionSkillKind.Active), skill => Assert.Equal(1, skill.Levels));
        Assert.Empty(DaevanionPlanSummary.Create("Sorcerer", FullPlans()).Stats);
    }

    [Fact]
    public void DuplicatePlanAndNodeIdsNeverCountADualBonusTwice()
    {
        var board = Catalog.GetBoard("Cleric", "ariel")!;
        var node = board.Nodes.First(node => node.Effects.Count == 2);
        var plan = new DaevanionBoardPlan { BoardId = board.Id, NodeIds = [node.Id, node.Id] };
        var summary = DaevanionPlanSummary.Create("Cleric", [plan, plan]);
        Assert.Equal(2, summary.Stats.Count);
        foreach (var effect in node.Effects)
            Assert.Equal(decimal.Parse(effect.Amount, System.Globalization.CultureInfo.InvariantCulture), summary.Stats.Single(stat => stat.StatId == effect.StatId).Amount);
    }

    private static DaevanionBoardPlan[] FullPlans() => Catalog.Boards.Where(board => board.ClassId == "Cleric")
        .Select(board => new DaevanionBoardPlan { BoardId = board.Id, FamilyId = board.FamilyId, NodeIds = board.Nodes.Select(node => node.Id).ToArray() }).ToArray();
}
