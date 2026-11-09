using System.Diagnostics;
using Soulcrest.Core.Daevanion;
using Xunit;
using Xunit.Abstractions;

namespace Soulcrest.Core.Tests;

public sealed class DaevanionPlanningTests(ITestOutputHelper output)
{
    [Fact]
    public void QualifiedRulesKeepAllBoardsCostsLevelsAndSymmetricOrthogonalEdges()
    {
        var catalog = DaevanionRuleCatalog.Default;
        Assert.Equal(48, catalog.Boards.Count);
        Assert.Equal(64, catalog.CatalogFingerprint.Length);
        foreach (var classId in DaevanionCatalog.Default.Classes.Select(row => row.Id))
        {
            var boards = catalog.ForClass(classId);
            Assert.Equal(new[] { 12, 20, 30, 40, 45, 45 }, boards.Select(board => board.MinimumLevel));
            Assert.Equal(new[] { 134, 134, 134, 168, 232, 232 }, boards.Select(board => board.MaximumCost));
            Assert.Equal(570, boards.Where(board => board.Pool == DaevanionBudgetPool.Normal).Sum(board => board.MaximumCost));
            foreach (var board in boards)
            {
                Assert.Same(board, catalog.GetBoardRules(board.Board.Id));
                var byId = board.Nodes.ToDictionary(node => node.Node.Id);
                Assert.Equal(0, board.Nodes.Single(node => node.Node.Kind == DaevanionNodeKind.Start).Cost);
                foreach (var node in board.Nodes)
                    foreach (var next in node.NeighborIds)
                    {
                        Assert.Contains(node.Node.Id, byId[next].NeighborIds);
                        Assert.Equal(1, Math.Abs(node.Node.Row - byId[next].Node.Row) + Math.Abs(node.Node.Column - byId[next].Node.Column));
                    }
            }
        }
    }

    [Fact]
    public void ScoringUsesRankCategoryUnitsAndSoftIgnoreAcrossEveryBoard()
    {
        var request = DaevanionPlanningDefaults.CreateRequest("Cleric");
        var critical = request.Priorities.Single(row => row.Key == "Critical");
        request = request with { Priorities = new[] { critical }.Concat(request.Priorities.Where(row => row != critical)).ToArray() };
        Assert.Equal(100m, DaevanionScoring.PriorityWeight(request, DaevanionPriorityKind.Stat, "Critical"));
        var values = DaevanionScoring.NodeValues(request);
        var nodes = DaevanionCatalog.Default.Boards.Where(board => board.ClassId == "Cleric").SelectMany(board => board.Nodes)
            .Where(node => node.Effects.Count == 1 && node.Effects[0].StatId == "Critical").ToArray();
        Assert.Contains(nodes, node => node.Effects[0].Amount == "5");
        Assert.Contains(nodes, node => node.Effects[0].Amount == "10");
        // Ariel/Azphel normalize their +10 critical increments within their own points pool.
        Assert.All(nodes, node => Assert.Equal(100m, values[node.Id].Score));
        request = request with { Priorities = request.Priorities.Select(row => row == critical ? row with { Ignored = true } : row).ToArray() };
        Assert.Equal(-5m, DaevanionScoring.PriorityWeight(request, critical.Kind, critical.Key));
        Assert.All(nodes, node => Assert.Equal(-5m, DaevanionScoring.NodeValues(request)[node.Id].Score));
        request = request with { Weights = new(0, 100, 100) };
        Assert.Equal(0m, DaevanionScoring.PriorityWeight(request, critical.Kind, critical.Key));
    }

    [Fact]
    public void ExhaustiveSmallGraphsAgreeWithAllThreeExactObjectiveStages()
    {
        for (var seed = 0; seed < 4; seed++)
        {
            var (board, scores) = Tiny(seed);
            for (var budget = 0; budget <= 8; budget++)
            {
                var expected = BruteForce(board, scores, budget);
                var result = DaevanionOptimizer.OptimizeGraphs("Cleric", [board], scores, new(budget), null, default);
                var plan = Assert.Single(result.Boards);
                Assert.Equal(DaevanionOptimizationStatus.Optimal, result.Status);
                Assert.Equal(expected.Score, (long)plan.Score);
                Assert.Equal(expected.Ignored, plan.NodeIds.Sum(id => scores.Ignored[id]));
                Assert.Equal(expected.Cost, plan.Cost);
                Assert.Equal(plan.NodeIds.Count - 1, plan.Edges.Count);
            }
        }
    }

    [Fact]
    public async Task RealLowBudgetsProduceLegalPlansAndIndependentTotals()
    {
        var request = DaevanionPlanningDefaults.CreateRequest("Cleric") with { Budgets = new(10, 5, 5) };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await new DaevanionOptimizer().OptimizeAsync(request, cancellationToken: cancellation.Token);
        Assert.Equal(DaevanionOptimizationStatus.Optimal, result.Status);
        Assert.Null(DaevanionPlanValidator.Validate(request, result));
        foreach (var pool in Enum.GetValues<DaevanionBudgetPool>())
            Assert.InRange(result.Boards.Where(board => board.Pool == pool).Sum(board => board.Cost), 0, request.Budgets.For(pool));
        var plan = result.Boards.First(board => board.Edges.Count > 0);
        Assert.NotNull(DaevanionPlanValidator.Validate(request, result with
        {
            Boards = result.Boards.Select(board => board == plan ? board with { Cost = board.Cost + 1 } : board).ToArray()
        }));
        Assert.NotNull(DaevanionPlanValidator.Validate(request, result with { CatalogFingerprint = "stale" }));
    }

    [Fact]
    public async Task ZeroBudgetsAndPreCancelledRequestsKeepOnlyStarts()
    {
        var request = DaevanionPlanningDefaults.CreateRequest("Cleric");
        var optimizer = new DaevanionOptimizer();
        var empty = await optimizer.OptimizeAsync(request);
        Assert.Equal(DaevanionOptimizationStatus.Optimal, empty.Status);
        Assert.All(empty.Boards, board => { Assert.Single(board.NodeIds); Assert.Empty(board.Edges); Assert.Equal(0, board.Cost); });
        using var stop = new CancellationTokenSource(); stop.Cancel();
        var cancelled = await optimizer.OptimizeAsync(request, cancellationToken: stop.Token);
        Assert.Equal(DaevanionOptimizationStatus.Cancelled, cancelled.Status);
        Assert.Null(DaevanionPlanValidator.Validate(request, cancelled));
    }

    [Theory]
    [InlineData("Assassin")] [InlineData("Chanter")] [InlineData("Cleric")] [InlineData("Elementalist")]
    [InlineData("Gladiator")] [InlineData("Ranger")] [InlineData("Sorcerer")] [InlineData("Templar")]
    public async Task CompleteBudgetsRetainEveryNodeForEveryClass(string classId)
    {
        var request = DaevanionPlanningDefaults.CreateRequest(classId) with { Budgets = new(570, 232, 232) };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await new DaevanionOptimizer().OptimizeAsync(request, cancellationToken: stop.Token);
        Assert.Equal(DaevanionOptimizationStatus.Optimal, result.Status);
        Assert.Null(DaevanionPlanValidator.Validate(request, result));
        Assert.Equal(690, result.Boards.Sum(board => board.NodeIds.Count));
        Assert.Equal(1034, result.Boards.Sum(board => board.Cost));
        Assert.Equal(684, result.Boards.Sum(board => board.Edges.Count));
    }

    [Fact]
    public async Task ProgressCancellationStopsNativeSearchAndPreservesAFeasibleIncumbent()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var request = DaevanionPlanningDefaults.CreateRequest("Cleric") with { Budgets = new(250, 120, 120) };
        var clock = Stopwatch.StartNew();
        var reports = new List<DaevanionOptimizationProgress>();
        var progress = new InlineProgress(value =>
        {
            reports.Add(value);
            // Wait for native-search evidence before cancelling under a busy full-suite runner.
            if (value.Elapsed >= TimeSpan.FromSeconds(1.1)
                && reports.Any(report => report.Stage == "Score" && report.BestBound is not null))
                stop.Cancel();
        });
        var result = await new DaevanionOptimizer().OptimizeAsync(request, progress, stop.Token);
        Assert.Equal(DaevanionOptimizationStatus.Cancelled, result.Status);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Contains(reports, report => report.Elapsed >= TimeSpan.FromSeconds(1));
        var bound = reports.First(report => report.Stage == "Score" && report.BestBound is not null);
        Assert.True(bound.BestBound >= (double)bound.BestScore! - 0.00001);
        // A normalized bound is on the same score scale, not the integer coefficient scale.
        Assert.True(bound.BestBound < 100_000);
        Assert.Null(DaevanionPlanValidator.Validate(request, result));
    }

    [Fact]
    public async Task CombatScoreIsExplicitlyUnavailableAndBadInputsAreRejected()
    {
        var request = DaevanionPlanningDefaults.CreateRequest("Cleric");
        var optimizer = new DaevanionOptimizer();
        Assert.Equal(DaevanionOptimizationStatus.Unavailable,
            (await optimizer.OptimizeAsync(request with { Mode = DaevanionOptimizationMode.CombatScore })).Status);
        Assert.Equal(DaevanionOptimizationStatus.Invalid,
            (await optimizer.OptimizeAsync(request with { Budgets = new(-1) })).Status);
        Assert.Equal(DaevanionOptimizationStatus.Invalid,
            (await optimizer.OptimizeAsync(request with { Weights = new(101) })).Status);
    }

    [DiagnosticTheory]
    [InlineData(20)] [InlineData(100)] [InlineData(250)] [InlineData(500)] [InlineData(570)]
    public async Task BenchmarkRealSharedPoolWithExplicitDiagnosticCancellation(int budget)
    {
        var request = DaevanionPlanningDefaults.CreateRequest("Cleric") with { Budgets = new(budget) };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var clock = Stopwatch.StartNew();
        var result = await new DaevanionOptimizer().OptimizeAsync(request, cancellationToken: stop.Token);
        Assert.Null(DaevanionPlanValidator.Validate(request, result));
        output.WriteLine($"budget={budget}; status={result.Status}; elapsed={clock.Elapsed.TotalSeconds:F3}s; score={result.Score}; cost={result.Boards.Sum(board => board.Cost)}");
    }

    private static (DaevanionBoardRules, DaevanionScoreModel) Tiny(int seed)
    {
        var random = new Random(seed);
        var nodes = Enumerable.Range(0, 8).Select(index => new DaevanionNode { Id = index + 1,
            Row = index / 3 + 1, Column = index % 3 + 1,
            Kind = index == 0 ? DaevanionNodeKind.Start : DaevanionNodeKind.Stat,
            Quality = index % 3 == 0 ? DaevanionNodeQuality.Rare : DaevanionNodeQuality.Common }).ToArray();
        var rules = nodes.Select(node => new DaevanionNodeRule(node, node.Id == 1 ? 0 : node.Quality == DaevanionNodeQuality.Rare ? 2 : 1,
            nodes.Where(other => Math.Abs(node.Row - other.Row) + Math.Abs(node.Column - other.Column) == 1).Select(other => other.Id).ToArray())).ToArray();
        var scores = nodes.ToDictionary(node => node.Id, node => node.Id == 1 ? 0L : random.Next(-4, 10));
        var ignored = nodes.ToDictionary(node => node.Id, node => node.Id == 1 ? 0L : random.Next(0, 2));
        return (new(new() { Id = 1, ClassId = "Cleric", FamilyId = "nezekan", Nodes = nodes }, DaevanionBudgetPool.Normal, 12, rules),
            new(1, scores, ignored, scores.ToDictionary(row => row.Key, row => new DaevanionNodeValue(row.Key, row.Value, ignored[row.Key]))));
    }

    private static (long Score, long Ignored, int Cost) BruteForce(DaevanionBoardRules board, DaevanionScoreModel score, int budget)
    {
        (long Score, long Ignored, int Cost) best = (long.MinValue, long.MaxValue, int.MaxValue);
        for (var mask = 1; mask < (1 << board.Nodes.Count); mask += 2)
        {
            var selected = board.Nodes.Where((_, index) => (mask & (1 << index)) != 0).ToArray();
            var cost = selected.Sum(node => node.Cost);
            if (cost > budget) continue;
            var ids = selected.Select(node => node.Node.Id).ToHashSet();
            var seen = new HashSet<int> { 1 }; var queue = new List<int> { 1 };
            for (var index = 0; index < queue.Count; index++)
                foreach (var next in board.Nodes.Single(node => node.Node.Id == queue[index]).NeighborIds.Where(ids.Contains))
                    if (seen.Add(next)) queue.Add(next);
            if (seen.Count != ids.Count) continue;
            var utility = ids.Sum(id => score.Scores[id]); var ignored = ids.Sum(id => score.Ignored[id]);
            if (utility > best.Score || utility == best.Score && (ignored < best.Ignored || ignored == best.Ignored && cost < best.Cost))
                best = (utility, ignored, cost);
        }
        return best;
    }

    private sealed class InlineProgress(Action<DaevanionOptimizationProgress> action) : IProgress<DaevanionOptimizationProgress>
    {
        public void Report(DaevanionOptimizationProgress value) => action(value);
    }
}
