using Google.OrTools.Sat;
using Soulcrest.Core.Combat;
using Soulcrest.Core.Daevanion;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class DaevanionSourceGraphTests
{
    [Fact(Skip = PendingOwnWeights.Reason)]
    public async Task ReplacingAnOldRouteUsesOneSignedWholeRouteDeltaAnchoredToObservedCp()
    {
        var context = DaevanionCombatScoreModel.CreateContext("Cleric") with
        {
            ModelVersion = CombatScoreModel.Version, CurrentCombatPower = 10_000,
            SourceGraph = new() { ClassId = "Cleric", Status = CombatKnowledgeStatus.Complete, Sources = [
                new() { Id = "base", Kind = CombatSourceKind.Base, Status = CombatKnowledgeStatus.Complete,
                    Effects = [new("HPMax", 1000, Application: CombatApplication.Confirmed)] },
                new() { Id = "old-board", Kind = CombatSourceKind.Daevanion, Status = CombatKnowledgeStatus.Complete,
                    Effects = [new("HPMax", 100, Application: CombatApplication.Confirmed)] }] }
        };
        var request = DaevanionPlanningDefaults.CreateRequest("Cleric") with
        {
            Mode = DaevanionOptimizationMode.CombatScore, CharacterLevel = 45, CombatContext = context
        };
        var estimate = DaevanionCombatScoreModel.Estimate(request, []);
        Assert.Equal(-15, estimate.ActualDelta);
        Assert.Equal(9985, estimate.Total);
        Assert.Equal(-100, Assert.Single(estimate.StatPreviews).TotalGain);
        var result = await new DaevanionOptimizer().OptimizeAsync(request);
        Assert.True(result.Status == DaevanionOptimizationStatus.Optimal, result.Message);
        Assert.Equal(-15, result.Score);
        Assert.Equal(9985, result.EstimatedTotalCombatScore);
        Assert.Equal(CombatScoreModel.Version, result.ScoreModelVersion);
        Assert.Null(DaevanionPlanValidator.Validate(request, result));
    }

    [Theory(Skip = PendingOwnWeights.Reason)]
    [InlineData(0, false)] [InlineData(1, false)] [InlineData(2, false)] [InlineData(3, false)]
    [InlineData(0, true)] [InlineData(1, true)] [InlineData(2, true)] [InlineData(3, true)]
    public void RationalSatCompilerMatchesTheSharedNonlinearModelForEverySubset(int mask, bool capTarget)
    {
        var skill = DaevanionCatalog.Default.Skills.First(skill => skill.ClassId == "Cleric" && skill.Kind == DaevanionSkillKind.Passive);
        var catalog = new CombatEffectCatalog("synthetic", 1,
            [new() { StatId = "HPMax", Rounding = CombatRounding.Ceiling }],
            scoreRules: [new("HPMax", 0.15m, CombatStatUnit.Points)],
            passiveEffects: [
                new() { SkillId = skill.Id, EffectId = "hp", StatId = "HPMax", Start = 100, Step = 100,
                    Application = CombatApplication.Confirmed },
                new() { SkillId = skill.Id, EffectId = "ratio", StatId = "HPMax", Start = 10, Step = 10,
                    Operation = CombatEffectOperation.IncreasePercent, Application = CombatApplication.Confirmed }]);
        var graph = new CombatSourceGraph { ClassId = "Cleric", Status = CombatKnowledgeStatus.Complete, Sources = [
            new() { Id = "base", Kind = CombatSourceKind.Base, Status = CombatKnowledgeStatus.Complete,
                Effects = [new("HPMax", 100, Application: CombatApplication.Confirmed), new("unmapped-zero", 0)],
                Skills = [new(skill.Id, skill.Kind, 1, CombatApplication.Confirmed), new(skill.Id, skill.Kind, 0)] }] };
        var request = DaevanionPlanningDefaults.CreateRequest("Cleric") with
        {
            CombatContext = new() { ModelVersion = CombatScoreModel.Version, SourceGraph = graph,
                Skills = [new(skill.Id, 1, 0, capTarget ? 1 : null)] }
        };
        var nodes = new[] {
            new DaevanionNodeRule(new() { Id = 1, Effects = [new() { Kind = DaevanionEffectKind.StatBonus, StatId = "HPMax", Amount = "10" }] }, 1, []),
            new DaevanionNodeRule(new() { Id = 2, Effects = [new() { Kind = DaevanionEffectKind.SkillLevelBonus, SkillId = skill.Id, LevelIncrement = 1 }] }, 2, []) };
        var model = new CpModel();
        var variables = nodes.ToDictionary(node => node.Node.Id, node => model.NewBoolVar("n" + node.Node.Id));
        foreach (var (id, variable) in variables) model.Add(variable == ((mask & (1 << (id - 1))) == 0 ? 0 : 1));
        var objective = new CombatGraphSatCompiler(model, [], catalog).Compile(request, nodes, variables);
        using var solver = new CpSolver();
        Assert.Equal(CpSolverStatus.Optimal, solver.Solve(model));
        var added = new CombatSource { Id = "new-board", Kind = CombatSourceKind.Daevanion, Status = CombatKnowledgeStatus.Complete,
            Effects = (mask & 1) == 0 ? [] : [new("HPMax", 10, Application: CombatApplication.Confirmed)],
            Skills = (mask & 2) == 0 || capTarget ? [] : [new(skill.Id, skill.Kind, 1, CombatApplication.Confirmed)] };
        var after = graph with { Sources = graph.Sources.Append(added).ToArray() };
        var expected = CombatScoreModel.RoundScore(CombatScoreModel.Calculate(after, catalog).Total!.Value
            - CombatScoreModel.Calculate(graph, catalog).Total!.Value);
        Assert.Equal(expected, solver.Value(objective));
    }

    [Fact(Skip = PendingOwnWeights.Reason)]
    public void InputFreezeProtectsSourcesAndCurrentScoreFromLaterEdits()
    {
        var effects = new List<CombatEffect> { new("HPMax", 1000, Application: CombatApplication.Confirmed) };
        var request = DaevanionPlanningDefaults.CreateRequest("Cleric") with { CombatContext = new()
        {
            ModelVersion = CombatScoreModel.Version, CurrentCombatPower = 10000,
            SourceGraph = new() { ClassId = "Cleric", Status = CombatKnowledgeStatus.Complete, Sources = [
                new() { Id = "base", Kind = CombatSourceKind.Base, Status = CombatKnowledgeStatus.Complete, Effects = effects }] }
        }};
        var frozen = DaevanionPoolSettings.Freeze(request);
        effects[0] = effects[0] with { Value = 9999 };
        Assert.Equal(1000, frozen.CombatContext!.SourceGraph!.Sources[0].Effects[0].Value);
        Assert.False(DaevanionPoolSettings.Equivalent(frozen, request));
        Assert.False(DaevanionPoolSettings.Equivalent(frozen, frozen with
            { CombatContext = frozen.CombatContext with { CurrentCombatPower = 9999 } }));
        var changedCatalog = frozen with { Mode = DaevanionOptimizationMode.CombatScore,
            CombatContext = frozen.CombatContext with { EffectCatalogVersion = "older-qualified-catalog" } };
        Assert.Contains("catalog has changed", DaevanionCombatScoreModel.UnavailableReason(changedCatalog));
    }
}
