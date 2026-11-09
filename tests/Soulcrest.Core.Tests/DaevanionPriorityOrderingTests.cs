using Soulcrest.Core.Daevanion;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class DaevanionPriorityOrderingTests
{
    [Fact]
    public void FilteredDownUsesTheImmediateGlobalPositionAfterTheVisibleTarget()
    {
        var request = DaevanionPlanningDefaults.CreateRequest("Cleric");
        var changed = DaevanionPriorityOrdering.Apply(request, DaevanionPriorityKind.Stat,
            "CombatSpeed", null, "CoolTimeDecrease", false);
        var stats = changed.Priorities.Where(row => row.Kind == DaevanionPriorityKind.Stat).Select(row => row.Key).ToArray();
        var index = Array.IndexOf(stats, "CoolTimeDecrease");
        Assert.Equal("CombatSpeed", stats[index + 1]);
        Assert.Equal("AmplifyCriticalDamage", stats[index + 2]); // hidden from Nezekan's table
        Assert.Equal(request.Priorities.Where(row => row.Kind != DaevanionPriorityKind.Stat), changed.Priorities.Where(row => row.Kind != DaevanionPriorityKind.Stat));
    }

    [Fact]
    public void DroppingAfterTheLastFilteredRowKeepsTheFollowingHiddenGlobalRows()
    {
        var request = DaevanionPlanningDefaults.CreateRequest("Cleric");
        var changed = DaevanionPriorityOrdering.Apply(request, DaevanionPriorityKind.Stat,
            "CombatSpeed", null, "MPMax", false);
        var stats = changed.Priorities.Where(row => row.Kind == DaevanionPriorityKind.Stat).Select(row => row.Key).ToArray();
        var index = Array.IndexOf(stats, "MPMax");
        Assert.Equal("CombatSpeed", stats[index + 1]);
        Assert.Equal("AdditionalHitRate", stats[index + 2]);
    }

    [Fact]
    public void OwnRowAndAlreadyCorrectPositionReturnTheOriginalRequest()
    {
        var request = DaevanionPlanningDefaults.CreateRequest("Cleric");
        Assert.Same(request, DaevanionPriorityOrdering.Apply(request, DaevanionPriorityKind.Stat, "CombatSpeed", null, "CombatSpeed", false));
        Assert.Same(request, DaevanionPriorityOrdering.Apply(request, DaevanionPriorityKind.Stat, "CombatSpeed", "CombatSpeed", null, false));
        var stats = request.Priorities.Where(row => row.Kind == DaevanionPriorityKind.Stat).ToArray();
        Assert.Same(request, DaevanionPriorityOrdering.Apply(request, DaevanionPriorityKind.Stat, stats[1].Key, null, stats[0].Key, false));
        Assert.Same(request, DaevanionPriorityOrdering.Apply(request, DaevanionPriorityKind.Stat, stats[^1].Key, null, null, false));
    }

    [Fact]
    public void IgnoreSwitchAndInvalidCrossCategoryAnchorsPreserveEveryPriorityExactlyOnce()
    {
        var request = DaevanionPlanningDefaults.CreateRequest("Cleric");
        var changed = DaevanionPriorityOrdering.Apply(request, DaevanionPriorityKind.Stat, "MPMax", null, null, true);
        Assert.True(changed.Priorities.Single(row => row.Kind == DaevanionPriorityKind.Stat && row.Key == "MPMax").Ignored);
        Assert.Equal(request.Priorities.Count, changed.Priorities.Select(row => (row.Kind, row.Key)).Distinct().Count());
        Assert.Same(changed, DaevanionPriorityOrdering.Apply(changed, DaevanionPriorityKind.Stat, "MPMax", null, null, true));
        var skill = request.Priorities.First(row => row.Kind == DaevanionPriorityKind.Skill);
        Assert.Same(request, DaevanionPriorityOrdering.Apply(request, DaevanionPriorityKind.Stat, "MPMax", skill.Key, null, false));
    }

    [Fact]
    public void InterleavedIgnoreRowsDoNotMakeAnUnchangedVisibleOrderInvalidateTheRequest()
    {
        var request = new DaevanionOptimizationRequest { ClassId = "Cleric", Priorities = [
            new(DaevanionPriorityKind.Stat, "A"), new(DaevanionPriorityKind.Stat, "X", true),
            new(DaevanionPriorityKind.Skill, "1"), new(DaevanionPriorityKind.Stat, "B")] };
        Assert.Same(request, DaevanionPriorityOrdering.Apply(request, DaevanionPriorityKind.Stat, "B", null, "A", false));
    }
}
