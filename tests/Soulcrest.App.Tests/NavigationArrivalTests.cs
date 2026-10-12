using Soulcrest.App.Services;
using Xunit;
namespace Soulcrest.App.Tests;
public sealed class ProgressionKindsTests
{
    [Theory]
    [InlineData("pets", new[] { "pets" }, false)]
    [InlineData("closest", new[] { "pets" }, true)]
    [InlineData("dungeon", new[] { "dungeon" }, false)]
    [InlineData("stronghold", new[] { "stronghold" }, false)]
    [InlineData("exploration", new[] { "dungeon", "stronghold" }, false)]
    public void TheFormerSingleModeBecomesASelection(string mode, string[] kinds, bool closest)
    {
        // User request 2026-10-10: several progression targets at once; old settings keep their meaning.
        var (actual, petsClosest) = SettingsService.ProgressionFromMode(mode);
        Assert.Equal(kinds, actual);
        Assert.Equal(closest, petsClosest);
    }
}

public sealed class TargetArrivalTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly MapTarget A = new("a", "altgard", 100, 100, "Teleporter", "Locations · Teleport", null);
    private static readonly MapTarget B = new("b", "altgard", 500, 100, "Kibelisk", "Locations · Kibelisk", null, After: "a");

    [Fact]
    public void ReachedTargetDisappearsOnlyAfterThePlayerWasAway()
    {
        var armed = new HashSet<string>();
        // Set while standing on it: stays.
        Assert.Empty(NavigationArrivalService.ArrivedTargets([A, B], new("altgard", 100, 100, Now), 40, armed, Now));
        // Walked away, then back: A is reached, B (far) stays.
        Assert.Empty(NavigationArrivalService.ArrivedTargets([A, B], new("altgard", 300, 100, Now), 40, armed, Now));
        Assert.Equal(["a"], NavigationArrivalService.ArrivedTargets([A, B], new("altgard", 120, 125, Now), 40, armed, Now));
    }

    [Fact]
    public void HandMarkedPetsStayOnArrivalRouteStopsGo()
    {
        var pet = new MapTarget("p", "altgard", 100, 100, "Kuru", "Pets · Fera", null, PetId: "kuru");
        var routePet = pet with { Id = "r", RouteId = "route" };
        var after = new MapTarget("b", "altgard", 500, 100, "Kibelisk", "Locations · Kibelisk", null, After: "p");
        var armed = new HashSet<string>();
        Assert.Empty(NavigationArrivalService.ArrivedTargets([pet, routePet, after], new("altgard", 300, 300, Now), 40, armed, Now));
        // On the pet: only the route stop goes; the hand-marked pet waits for its next level.
        Assert.Equal(["r"], NavigationArrivalService.ArrivedTargets([pet, routePet, after], new("altgard", 100, 100, Now), 40, armed, Now));
        Assert.Empty(NavigationArrivalService.ArrivedTargets([pet, after], new("altgard", 300, 300, Now), 40, armed, Now));
        // Reaching the stop after it does not take the skipped pet along.
        Assert.Equal(["b"], NavigationArrivalService.ArrivedTargets([pet, after], new("altgard", 500, 100, Now), 40, armed, Now));
        Assert.True(NavigationArrivalService.StaysUntilNextLevel(new MapTarget("o", "altgard", 0, 0, "Kuru", "Pets · Fera", null))); // older mark without petId
    }

    [Fact]
    public void SkippedStopsDisappearWithTheReachedOne()
    {
        var c = new MapTarget("c", "altgard", 900, 100, "Teleporter", "Locations", null, After: "b");
        var other = new MapTarget("x", "altgard", 100, 900, "Other", "Locations", null);
        var armed = new HashSet<string>();
        Assert.Empty(NavigationArrivalService.ArrivedTargets([A, B, c, other], new("altgard", 300, 300, Now), 40, armed, Now));
        // Straight to C: A and B were skipped and go with it, first stop first; the unchained target stays.
        Assert.Equal(["a", "b", "c"], NavigationArrivalService.ArrivedTargets([A, B, c, other], new("altgard", 900, 100, Now), 40, armed, Now));
    }

    [Fact]
    public void OtherMapStalePositionOrRadiusZeroRemovesNothing()
    {
        var armed = new HashSet<string> { "a" };
        Assert.Empty(NavigationArrivalService.ArrivedTargets([A], new("poeta", 100, 100, Now), 40, armed, Now));
        Assert.Empty(NavigationArrivalService.ArrivedTargets([A], new("altgard", 100, 100, Now.AddSeconds(-3)), 40, armed, Now));
        Assert.Empty(NavigationArrivalService.ArrivedTargets([A], new("altgard", 100, 100, Now), 0, armed, Now));
    }

}
