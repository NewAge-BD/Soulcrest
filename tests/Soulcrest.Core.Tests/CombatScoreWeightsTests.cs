using Soulcrest.Core.Combat;
using Soulcrest.Core.Daevanion;
using Xunit;

namespace Soulcrest.Core.Tests;

/// <summary>Measured own combat-score weights (research 2026-11-09) reproduce every own controlled contrast.</summary>
public sealed class CombatScoreWeightsTests
{
    private static decimal W(string id) => CombatScoreWeights.Get(id)!.Coefficient;

    public static TheoryData<string, (string Stat, decimal Delta)[], int> OwnContrasts => new()
    {
        { "Critical refund", [("Critical", -5)], -35 },
        { "HP+100 node, context R0", [("HPMax", -100)], -19 },
        { "HP+100 node, context R1", [("HPMax", -100)], -20 },
        { "MP+50 node", [("MPMax", -50)], -13 },
        { "MP title +200 -> +235", [("MPMax", 35)], 9 },
        { "Defense refund", [("Defense", -30)], -8 },
        { "HP percent title 1.6 -> 1.8 %", [("MaxHPRatio", 0.2m)], 6 },
        { "MP percent title", [("MaxMPRatio", 2)], 18 },
        { "Multi-hit Resist title", [("AdditionalHitResistRate", 1.4m)], 42 },
        { "Back Attack Damage Boost title", [("AmplifyBackAttack", 1.6m)], 162 },
        { "HP percent vs MP context", [("MaxHPRatio", 1.8m), ("MPMax", -200)], 4 },
        { "MP percent, MP and potion context", [("MaxMPRatio", 2), ("MPMax", -200), ("HpPotionRegen", 200)], 87 },
    };

    [Theory]
    [MemberData(nameof(OwnContrasts))]
    public void MidpointWeightsReproduceOwnContrastsWithinOneRounding(string name, (string Stat, decimal Delta)[] deltas, int observed)
    {
        var predicted = deltas.Sum(delta => W(delta.Stat) * delta.Delta);
        Assert.True(Math.Abs(predicted - observed) < 1, $"{name}: predicted {predicted}, observed {observed}");
    }

    [Fact]
    public void EveryWeightLiesInsideItsMeasuredInterval()
    {
        Assert.NotEmpty(CombatScoreWeights.Stats);
        foreach (var weight in CombatScoreWeights.Stats)
        {
            Assert.InRange(weight.Coefficient, weight.Lower, weight.Upper);
            Assert.NotNull(CombatEffectCatalog.Default.GetStat(weight.StatId));
        }
    }

    [Fact]
    public void ActiveStepsReproduceDivineAuraAndEarthsRetributionLadders()
    {
        int Step(int level) => CombatScoreWeights.SkillScore(DaevanionSkillKind.Active, level)!.Value
            - CombatScoreWeights.SkillScore(DaevanionSkillKind.Active, level - 1)!.Value;
        Assert.Equal(new[] { 500, 100, 100, 100, 900, 200, 200, 200, 1000 }, Enumerable.Range(12, 9).Select(Step));
        Assert.Equal(1300, CombatScoreWeights.SkillScore(DaevanionSkillKind.Active, 12) - CombatScoreWeights.SkillScore(DaevanionSkillKind.Active, 3));
        Assert.Equal(800, CombatScoreWeights.SkillScore(DaevanionSkillKind.Active, 10) - CombatScoreWeights.SkillScore(DaevanionSkillKind.Active, 2));
        Assert.Equal(2300, CombatScoreWeights.SkillScore(DaevanionSkillKind.Active, 19) - CombatScoreWeights.SkillScore(DaevanionSkillKind.Active, 11));
    }

    [Fact]
    public void PassiveStepsReproduceHealBlockAndUnmeasuredLevelsStayUnknown()
    {
        Assert.Equal(600, CombatScoreWeights.SkillScore(DaevanionSkillKind.Passive, 10) - CombatScoreWeights.SkillScore(DaevanionSkillKind.Passive, 4));
        Assert.Null(CombatScoreWeights.SkillScore(DaevanionSkillKind.Passive, 11));
        Assert.Null(CombatScoreWeights.SkillScore(DaevanionSkillKind.Passive, 3));
        Assert.Null(CombatScoreWeights.SkillScore(DaevanionSkillKind.Active, 1));
        Assert.Null(CombatScoreWeights.SkillScore(DaevanionSkillKind.Active, 21));
    }

    [Fact]
    public void SourceModelWeightsFlatAndPercentComponentsSeparately()
    {
        var graph = new CombatSourceGraph
        {
            Status = CombatKnowledgeStatus.Complete,
            Sources = [new() { Id = "fictional", Status = CombatKnowledgeStatus.Complete, Effects =
                [new("HPMax", 100, Application: CombatApplication.Confirmed), new("MaxHPRatio", 10, Application: CombatApplication.Confirmed)] }]
        };
        var evaluation = CombatScoreModel.Evaluate(graph);
        var hp = Assert.Single(evaluation.Composition.Stats, term => term.StatId == "HPMax");
        var ratio = Assert.Single(evaluation.Composition.Stats, term => term.StatId == "MaxHPRatio");
        Assert.Equal(100m, hp.Value); Assert.Equal(100m * W("HPMax"), hp.Contribution);
        Assert.Equal(10m, ratio.Value); Assert.Equal(10m * W("MaxHPRatio"), ratio.Contribution);
        Assert.Equal(CombatScoreModel.RoundScore(100m * W("HPMax") + 10m * W("MaxHPRatio")), evaluation.ModelTotal);
    }

    [Fact]
    public void UnmeasuredStatKeepsTheTotalUnknown()
    {
        var graph = new CombatSourceGraph
        {
            Status = CombatKnowledgeStatus.Complete,
            Sources = [new() { Id = "fictional", Status = CombatKnowledgeStatus.Complete, Effects =
                [new("Critical", 10, Application: CombatApplication.Confirmed), new("CombatSpeed", 3, Application: CombatApplication.Confirmed)] }]
        };
        var score = CombatScoreModel.Score(graph);
        Assert.False(score.IsComplete);
        Assert.Equal(70m, score.Unrounded);
    }
}
