using Soulcrest.Core.Daevanion;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class DaevanionCharacterLevelTests
{
    [Fact]
    public async Task ObservedLevelRestrictsPurchasesButKeepsEveryBoardRoot()
    {
        var request = DaevanionPlanningDefaults.CreateRequest("Cleric") with
        {
            CharacterLevel = 12,
            Budgets = new(2, 2, 2)
        };
        var result = await new DaevanionOptimizer().OptimizeAsync(request);
        Assert.Equal(DaevanionOptimizationStatus.Optimal, result.Status);
        Assert.Equal(6, result.Boards.Count);
        Assert.Null(DaevanionPlanValidator.Validate(request, result));
        foreach (var board in result.Boards.Where(board => board.FamilyId != "nezekan"))
        {
            Assert.Single(board.NodeIds);
            Assert.Equal(0, board.Cost);
        }
        Assert.True(result.Boards.Single(board => board.FamilyId == "nezekan").Cost > 0);
    }

    [Fact]
    public async Task MustHaveOnALockedBoardIsExplainedAsInfeasible()
    {
        var request = DaevanionPlanningDefaults.CreateRequest("Cleric") with { CharacterLevel = 12, Budgets = new(2, 999, 999) };
        var preferences = DaevanionPoolSettings.CreateDefault("Cleric", DaevanionBudgetPool.Ariel);
        request = DaevanionPoolSettings.WithPool(request, preferences with
        {
            Priorities = preferences.Priorities.Select((row, index) => index == 0 ? row with { Required = true } : row).ToArray()
        });
        var result = await new DaevanionOptimizer().OptimizeAsync(request);
        Assert.Equal(DaevanionOptimizationStatus.Infeasible, result.Status);
        Assert.Contains("locked", result.Message);
        Assert.Empty(result.Boards);
    }

    [Fact]
    public void CharacterLevelChangesInvalidateInputEquivalenceAndInvalidLevelsAreRejected()
    {
        var request = DaevanionPlanningDefaults.CreateRequest("Cleric") with { CharacterLevel = 12 };
        Assert.False(DaevanionPoolSettings.Equivalent(request, request with { CharacterLevel = 20 }));
        Assert.NotNull(DaevanionPoolSettings.Validate(request with { CharacterLevel = 0 }));
        Assert.NotNull(DaevanionPoolSettings.Validate(request with { CharacterLevel = -1 }));
    }
}
