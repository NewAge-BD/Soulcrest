using System.Text.Json;
using Soulcrest.Core.Daevanion;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class DaevanionRequiredPriorityTests
{
    [Fact]
    public void RequiredDefaultsFalseAndConflictsWithIgnoreAreRejected()
    {
        var legacy = JsonSerializer.Deserialize<DaevanionPriority>("{\"Kind\":0,\"Key\":\"Critical\",\"Ignored\":false}")!;
        Assert.False(legacy.Required);
        var request = DaevanionPlanningDefaults.CreateRequest("Cleric");
        var normal = DaevanionPoolSettings.ForPool(request, DaevanionBudgetPool.Normal);
        var invalid = request with { PoolPreferences = [normal with { Priorities = normal.Priorities
            .Select((row, index) => index == 0 ? row with { Required = true, Ignored = true } : row).ToArray() }] };
        Assert.NotNull(DaevanionPoolSettings.Validate(invalid));
        Assert.Throws<ArgumentException>(() => DaevanionPriorityOrdering.Apply(request, legacy.Kind, legacy.Key,
            null, null, true, DaevanionBudgetPool.Normal, true));
    }

    [Fact]
    public void RequiredOrderingHasThreeIndependentSectionsAndHighestActiveRanks()
    {
        var request = DaevanionPlanningDefaults.CreateRequest("Cleric");
        var normal = DaevanionPoolSettings.ForPool(request, DaevanionBudgetPool.Normal);
        var stats = normal.Priorities.Where(row => row.Kind == DaevanionPriorityKind.Stat).ToArray();
        request = DaevanionPoolSettings.WithPool(request, normal with { Priorities = normal.Priorities
            .Select(row => row == stats[0] ? row with { Required = true } : row == stats[1] ? row with { Ignored = true } : row).ToArray() });
        var moved = DaevanionPriorityOrdering.Apply(request, DaevanionPriorityKind.Stat, stats[2].Key, null, null,
            false, DaevanionBudgetPool.Normal, true);
        moved = DaevanionPriorityOrdering.Apply(moved, DaevanionPriorityKind.Stat, stats[2].Key, stats[0].Key, null,
            false, DaevanionBudgetPool.Normal, true);
        var preferences = DaevanionPoolSettings.ForPool(moved, DaevanionBudgetPool.Normal);
        Assert.Equal(new[] { stats[2].Key, stats[0].Key }, preferences.Priorities.Where(row => row.Required).Select(row => row.Key));
        Assert.Equal(100m, DaevanionScoring.PriorityWeight(moved, DaevanionPriorityKind.Stat, stats[2].Key));
        Assert.True(preferences.Priorities.Single(row => row.Key == stats[1].Key).Ignored);
        Assert.Equal(-5m, DaevanionScoring.PriorityWeight(moved, DaevanionPriorityKind.Stat, stats[1].Key));
        Assert.Same(moved, DaevanionPriorityOrdering.Apply(moved, DaevanionPriorityKind.Stat, stats[2].Key,
            stats[2].Key, null, false, DaevanionBudgetPool.Normal, true));
        Assert.Same(moved, DaevanionPriorityOrdering.Apply(moved, DaevanionPriorityKind.Stat, stats[2].Key,
            stats[1].Key, null, false, DaevanionBudgetPool.Normal, true)); // ignored anchor is another section
        Assert.DoesNotContain(DaevanionPoolSettings.ForPool(moved, DaevanionBudgetPool.Ariel).Priorities, row => row.Required);
    }

    [Fact]
    public void EveryMatchingNodeIsRequiredAcrossAllFourNormalBoardsAndDoubleBonusesAreIncluded()
    {
        var request = DaevanionPlanningDefaults.CreateRequest("Cleric");
        var normal = DaevanionPoolSettings.ForPool(request, DaevanionBudgetPool.Normal);
        var skill = normal.Priorities.First(row => row.Kind == DaevanionPriorityKind.Skill);
        var passive = normal.Priorities.First(row => row.Kind == DaevanionPriorityKind.Passive);
        request = DaevanionPoolSettings.WithPool(request, normal with { Priorities = normal.Priorities
            .Select(row => row == skill || row == passive ? row with { Required = true } : row).ToArray() });
        var required = DaevanionPoolSettings.RequiredNodeIds(request);
        Assert.Equal(8, required.Count);
        var selectedSkillIds = new[] { skill.Key, passive.Key }.Select(key => int.Parse(key,
            System.Globalization.CultureInfo.InvariantCulture)).ToHashSet();
        var normalBoards = DaevanionRuleCatalog.Default.ForClass("Cleric").Where(board => board.Pool == DaevanionBudgetPool.Normal).ToArray();
        var originalMatches = normalBoards.SelectMany(board => board.Board.Nodes)
            .Where(node => node.Effects.Any(effect => effect.SkillId is { } id && selectedSkillIds.Contains(id)))
            .Select(node => node.Id).ToHashSet();
        Assert.True(originalMatches.SetEquals(required));
        Assert.Equal(new[] { 1, 3, 2, 2 }, normalBoards.Select(board => board.Nodes.Count(node => required.Contains(node.Node.Id))));
        var ariel = DaevanionPoolSettings.ForPool(request, DaevanionBudgetPool.Ariel);
        request = DaevanionPoolSettings.WithPool(request, ariel with { Priorities = ariel.Priorities
            .Select(row => row.Key is "PvEAddDamage" or "PvEDamageDefense" ? row with { Required = true } : row).ToArray() });
        var expected = DaevanionRuleCatalog.Default.ForClass("Cleric").Single(board => board.Pool == DaevanionBudgetPool.Ariel).Nodes
            .Where(node => node.Node.Effects.Any(effect => effect.StatId is "PvEAddDamage" or "PvEDamageDefense"))
            .Select(node => node.Node.Id).ToHashSet();
        Assert.True(expected.SetEquals(DaevanionPoolSettings.RequiredNodeIds(request, DaevanionBudgetPool.Ariel)));
        Assert.Contains(750003, expected); // Both requested effects share this two-bonus node.
        Assert.Equal(8, DaevanionPoolSettings.RequiredNodeIds(request, DaevanionBudgetPool.Normal).Count);
        Assert.Empty(DaevanionPoolSettings.RequiredNodeIds(request, DaevanionBudgetPool.Azphel));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void ExactRequiredSolverMatchesBruteForceIncludingNecessaryNegativeConnectors(int budget)
    {
        var (board, scores) = Graph();
        IReadOnlySet<int> required = new HashSet<int> { 3, 4 };
        var expected = BruteForce(board, scores, budget, required);
        var result = DaevanionOptimizer.OptimizeGraphs("Cleric", [board], scores, new(budget), null, default, required);
        if (expected is null)
        {
            Assert.Equal(DaevanionOptimizationStatus.Infeasible, result.Status);
            Assert.Empty(result.Boards);
        }
        else
        {
            Assert.Equal(DaevanionOptimizationStatus.Optimal, result.Status);
            var plan = Assert.Single(result.Boards);
            Assert.Equal(expected.Value.Score, plan.Score);
            Assert.Equal(expected.Value.Cost, plan.Cost);
            Assert.True(required.IsSubsetOf(plan.NodeIds.ToHashSet()));
            Assert.Contains(2, plan.NodeIds); // A negative-score connection is still necessary.
        }
    }

    [Fact]
    public void AStillUnfeasibleLaterPoolNeverPublishesAnIncompleteMandatoryRoute()
    {
        var (normal, normalScores) = Graph(); var (special, specialScores) = Graph(100, DaevanionBudgetPool.Ariel);
        var scores = new DaevanionScoreModel(1, normalScores.Scores.Concat(specialScores.Scores).ToDictionary(item => item.Key, item => item.Value),
            normalScores.Ignored.Concat(specialScores.Ignored).ToDictionary(item => item.Key, item => item.Value),
            normalScores.Values.Concat(specialScores.Values).ToDictionary(item => item.Key, item => item.Value));
        var reports = new List<DaevanionOptimizationProgress>();
        var result = DaevanionOptimizer.OptimizeGraphs("Cleric", [normal, special], scores, new(5, 2),
            new InlineProgress(reports.Add), default, new HashSet<int> { 104 }); // Cost 2 node needs an extra cost 1 connector.
        Assert.Equal(DaevanionOptimizationStatus.Infeasible, result.Status);
        Assert.NotEmpty(reports);
        Assert.All(reports, report => { Assert.Empty(report.Boards); Assert.Null(report.BestScore); });
    }

    [Fact]
    public async Task CancelBeforeMandatoryFeasibilityReturnsNoSaveablePlanAndValidatorRejectsMissingNodes()
    {
        var request = DaevanionPlanningDefaults.CreateRequest("Cleric");
        var empty = await new DaevanionOptimizer().OptimizeAsync(request);
        var ariel = DaevanionPoolSettings.ForPool(request, DaevanionBudgetPool.Ariel);
        request = DaevanionPoolSettings.WithPool(request, ariel with { Priorities = ariel.Priorities
            .Select(row => row.Key == "Critical" ? row with { Required = true } : row).ToArray() });
        Assert.NotNull(DaevanionPlanValidator.Validate(request, empty));
        using var stop = new CancellationTokenSource(); stop.Cancel();
        var cancelled = await new DaevanionOptimizer().OptimizeAsync(request, cancellationToken: stop.Token);
        Assert.Equal(DaevanionOptimizationStatus.Cancelled, cancelled.Status);
        Assert.Empty(cancelled.Boards);
        Assert.NotNull(DaevanionPlanValidator.Validate(request, cancelled));
        var impossible = await new DaevanionOptimizer().OptimizeAsync(request);
        Assert.Equal(DaevanionOptimizationStatus.Infeasible, impossible.Status);
        Assert.Empty(impossible.Boards);
        Assert.Contains("Ariel", impossible.Message);
    }

    private static (DaevanionBoardRules, DaevanionScoreModel) Graph(int offset = 0, DaevanionBudgetPool pool = DaevanionBudgetPool.Normal)
    {
        var positions = new[] { (1, 1), (1, 2), (1, 3), (2, 2), (2, 3) };
        var nodes = positions.Select((position, index) => new DaevanionNode { Id = offset + index + 1,
            Row = position.Item1, Column = position.Item2, Kind = index == 0 ? DaevanionNodeKind.Start : DaevanionNodeKind.Stat,
            Quality = index == 3 ? DaevanionNodeQuality.Rare : DaevanionNodeQuality.Common }).ToArray();
        var rules = nodes.Select((node, index) => new DaevanionNodeRule(node, index == 0 ? 0 : index == 3 ? 2 : 1,
            nodes.Where(other => Math.Abs(node.Row - other.Row) + Math.Abs(node.Column - other.Column) == 1).Select(other => other.Id).ToArray())).ToArray();
        var amounts = new long[] { 0, -2, 5, 3, 8 };
        var scores = nodes.Select((node, index) => (node.Id, amounts[index])).ToDictionary(item => item.Id, item => item.Item2);
        return (new(new() { Id = offset + 1, ClassId = "Cleric", FamilyId = pool == DaevanionBudgetPool.Normal ? "nezekan" : "ariel", Nodes = nodes },
            pool, 12, rules), new(1, scores, scores.ToDictionary(item => item.Key, _ => 0L),
                scores.ToDictionary(item => item.Key, item => new DaevanionNodeValue(item.Key, item.Value, 0))));
    }

    private static (decimal Score, int Cost)? BruteForce(DaevanionBoardRules board, DaevanionScoreModel scores, int budget, IReadOnlySet<int> required)
    {
        (decimal Score, int Cost)? best = null;
        for (var mask = 1; mask < 1 << board.Nodes.Count; mask += 2)
        {
            var selected = board.Nodes.Where((_, index) => (mask & 1 << index) != 0).ToArray();
            var ids = selected.Select(node => node.Node.Id).ToHashSet(); var cost = selected.Sum(node => node.Cost);
            if (cost > budget || !required.IsSubsetOf(ids)) continue;
            var seen = new HashSet<int> { board.Nodes[0].Node.Id }; var queue = seen.ToList();
            for (var index = 0; index < queue.Count; index++)
                foreach (var id in board.Nodes.Single(node => node.Node.Id == queue[index]).NeighborIds.Where(ids.Contains))
                    if (seen.Add(id)) queue.Add(id);
            if (seen.Count != ids.Count) continue;
            var value = ids.Sum(id => scores.Scores[id]);
            if (best is null || value > best.Value.Score || value == best.Value.Score && cost < best.Value.Cost) best = (value, cost);
        }
        return best;
    }

    private sealed class InlineProgress(Action<DaevanionOptimizationProgress> action) : IProgress<DaevanionOptimizationProgress>
    { public void Report(DaevanionOptimizationProgress value) => action(value); }
}
