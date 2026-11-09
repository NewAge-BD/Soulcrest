using System.Text.Json;
using Soulcrest.Core.Daevanion;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class DaevanionPoolSettingsTests
{
    [Fact]
    public void LegacyJsonFallsBackToFilteredCompletePreferencesForEachPool()
    {
        var initial = DaevanionPlanningDefaults.CreateRequest("Cleric");
        var json = JsonSerializer.SerializeToNode(initial)!;
        json.AsObject().Remove(nameof(DaevanionOptimizationRequest.PoolPreferences));
        var request = json.Deserialize<DaevanionOptimizationRequest>()!;
        Assert.Empty(request.PoolPreferences);
        Assert.Null(DaevanionPoolSettings.Validate(request));
        foreach (var pool in Enum.GetValues<DaevanionBudgetPool>())
        {
            var preferences = DaevanionPoolSettings.ForPool(request, pool);
            Assert.Equal(pool, preferences.Pool);
            Assert.Equal(new DaevanionCategoryWeights(), preferences.Weights);
            Assert.Equal(DaevanionPoolSettings.CreateDefault("Cleric", pool).Priorities, preferences.Priorities);
            if (pool != DaevanionBudgetPool.Normal) Assert.All(preferences.Priorities, row => Assert.Equal(DaevanionPriorityKind.Stat, row.Kind));
        }
    }

    [Fact]
    public void SameStatKeepsIndependentRanksIgnoreAndWeightsAcrossPools()
    {
        var request = DaevanionPlanningDefaults.CreateRequest("Cleric");
        var before = DaevanionScoring.NodeValues(request);
        var originalNormal = DaevanionPoolSettings.ForPool(request, DaevanionBudgetPool.Normal);
        var originalAzphel = DaevanionPoolSettings.ForPool(request, DaevanionBudgetPool.Azphel);
        var ariel = DaevanionPoolSettings.ForPool(request, DaevanionBudgetPool.Ariel);
        request = DaevanionPoolSettings.WithPool(request, ariel with { Weights = new(40, 15, 20), Priorities = ariel.Priorities
            .Select(row => row.Key == "Critical" ? row with { Ignored = true } : row).ToArray() });
        Assert.Equal(-2m, DaevanionScoring.PriorityWeight(request, DaevanionPriorityKind.Stat, "Critical", DaevanionBudgetPool.Ariel));
        Assert.Equal(originalNormal.Priorities, DaevanionPoolSettings.ForPool(request, DaevanionBudgetPool.Normal).Priorities);
        Assert.Equal(originalAzphel.Priorities, DaevanionPoolSettings.ForPool(request, DaevanionBudgetPool.Azphel).Priorities);
        var after = DaevanionScoring.NodeValues(request);
        foreach (var board in DaevanionRuleCatalog.Default.ForClass(request.ClassId))
            foreach (var node in board.Nodes)
                if (board.Pool != DaevanionBudgetPool.Ariel) Assert.Equal(before[node.Node.Id], after[node.Node.Id]);
                else if (node.Node.Effects is [{ StatId: "Critical" }]) Assert.Equal(-2m, after[node.Node.Id].Score);
        var anchor = DaevanionPoolSettings.ForPool(request, DaevanionBudgetPool.Ariel).Priorities
            .First(row => !row.Ignored && row.Key != "PvEAddDamage").Key;
        var changed = DaevanionPriorityOrdering.Apply(request, DaevanionPriorityKind.Stat, "PvEAddDamage", anchor, null,
            false, DaevanionBudgetPool.Ariel);
        Assert.Equal("PvEAddDamage", DaevanionPoolSettings.ForPool(changed, DaevanionBudgetPool.Ariel).Priorities[0].Key);
        Assert.Equal(originalNormal.Priorities, DaevanionPoolSettings.ForPool(changed, DaevanionBudgetPool.Normal).Priorities);
        Assert.Equal(originalAzphel.Priorities, DaevanionPoolSettings.ForPool(changed, DaevanionBudgetPool.Azphel).Priorities);
        Assert.Equal(40, DaevanionPoolSettings.ForPool(changed, DaevanionBudgetPool.Ariel).Weights.Stats);
    }

    [Fact]
    public void PoolOrderingUpdatesAllFourNormalBoardsButNeitherSpecialPool()
    {
        var request = DaevanionPlanningDefaults.CreateRequest("Cleric");
        var ariel = DaevanionPoolSettings.ForPool(request, DaevanionBudgetPool.Ariel);
        var changed = DaevanionPriorityOrdering.Apply(request, DaevanionPriorityKind.Stat, "Critical", null, null,
            true, DaevanionBudgetPool.Normal);
        Assert.True(DaevanionPoolSettings.ForPool(changed, DaevanionBudgetPool.Normal).Priorities.Single(row => row.Key == "Critical").Ignored);
        Assert.Equal(ariel.Priorities, DaevanionPoolSettings.ForPool(changed, DaevanionBudgetPool.Ariel).Priorities);
        var values = DaevanionScoring.NodeValues(changed);
        Assert.All(DaevanionRuleCatalog.Default.ForClass("Cleric").Where(board => board.Pool == DaevanionBudgetPool.Normal)
            .SelectMany(board => board.Nodes).Where(node => node.Node.Effects is [{ StatId: "Critical" }]),
            node => Assert.Equal(-5m, values[node.Node.Id].Score));
    }

    [Fact]
    public void InvalidOverridesAreRejectedAndReconciliationKeepsIndependentValidChoices()
    {
        var request = DaevanionPlanningDefaults.CreateRequest("Cleric");
        var ariel = DaevanionPoolSettings.CreateDefault("Cleric", DaevanionBudgetPool.Ariel);
        foreach (var invalid in new[] {
            request with { PoolPreferences = [ariel, ariel] },
            request with { PoolPreferences = [ariel with { Pool = (DaevanionBudgetPool)99 }] },
            request with { PoolPreferences = [ariel with { Weights = new(101) }] },
            request with { PoolPreferences = [ariel with { Priorities = [new(DaevanionPriorityKind.Stat, "PvPAddDamage")] }] },
            request with { PoolPreferences = null! }, request with { PoolPreferences = [null!] } })
            Assert.NotNull(DaevanionPoolSettings.Validate(invalid));
        var kept = ariel.Priorities[0] with { Ignored = true };
        var old = request with { PoolPreferences = [ariel with { Weights = new(41), Priorities = [kept, new(DaevanionPriorityKind.Stat, "retired")] }] };
        var migrated = DaevanionPoolSettings.Reconcile(old);
        Assert.Null(DaevanionPoolSettings.Validate(migrated));
        var migratedAriel = DaevanionPoolSettings.ForPool(migrated, DaevanionBudgetPool.Ariel);
        Assert.Equal(41, migratedAriel.Weights.Stats);
        Assert.True(migratedAriel.Priorities.Single(row => row.Key == kept.Key).Ignored);
        Assert.DoesNotContain(migratedAriel.Priorities, row => row.Key == "retired");
        Assert.Equal(ariel.Priorities.Count, migratedAriel.Priorities.Count);
    }

    [Fact]
    public void EquivalentInputsAndOwnRowMovesPreserveTheOriginalRequestAndFrozenCollections()
    {
        var request = DaevanionPlanningDefaults.CreateRequest("Cleric");
        var ariel = DaevanionPoolSettings.ForPool(request, DaevanionBudgetPool.Ariel);
        Assert.Same(request, DaevanionPoolSettings.WithPool(request, ariel));
        Assert.Same(request, DaevanionPriorityOrdering.Apply(request, ariel.Priorities[0].Kind, ariel.Priorities[0].Key,
            ariel.Priorities[0].Key, null, false, DaevanionBudgetPool.Ariel));
        var explicitDefaults = request with { PoolPreferences = Enum.GetValues<DaevanionBudgetPool>()
            .Select(pool => DaevanionPoolSettings.ForPool(request, pool)).ToArray() };
        Assert.True(DaevanionPoolSettings.Equivalent(request, explicitDefaults));
        var frozen = DaevanionPoolSettings.Freeze(explicitDefaults);
        Assert.Throws<NotSupportedException>(() => ((IList<DaevanionPoolPreferences>)frozen.PoolPreferences).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<DaevanionPriority>)frozen.PoolPreferences[0].Priorities).Clear());
    }

    [Fact]
    public async Task SolverUsesIndependentPoolWeightsAndExactScoreTotals()
    {
        var request = DaevanionPlanningDefaults.CreateRequest("Cleric") with { Budgets = new(10, 8, 8) };
        request = DaevanionPoolSettings.WithPool(request, DaevanionPoolSettings.ForPool(request, DaevanionBudgetPool.Ariel)
            with { Weights = new(0, 0, 0) });
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await new DaevanionOptimizer().OptimizeAsync(request, cancellationToken: stop.Token);
        Assert.Equal(DaevanionOptimizationStatus.Optimal, result.Status);
        Assert.Null(DaevanionPlanValidator.Validate(request, result));
        var ariel = result.Boards.Single(board => board.Pool == DaevanionBudgetPool.Ariel);
        Assert.Equal(0, ariel.Cost); Assert.Single(ariel.NodeIds);
        Assert.True(result.Boards.Single(board => board.Pool == DaevanionBudgetPool.Azphel).Cost > 0);
        Assert.All(result.Boards, board => Assert.Equal(board.Score, DaevanionScoring.ScoreForNodes(request, board.NodeIds)));
        Assert.Equal("2026-11-02-v4", result.RulesVersion);
        Assert.NotNull(DaevanionPlanValidator.Validate(request, result with { RulesVersion = "2026-10-31-v2" }));
    }
}
