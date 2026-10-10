using Soulcrest.App.Services;
using Xunit;
namespace Soulcrest.App.Tests;
public sealed class ExplorationArrivalTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static AppSettings Settings => new() { ProgressionEnabled = true, ProgressionKinds = ["dungeon"], ExplorationCompletionRadius = 50 };
    private static MapTarget Target => new("progression","altgard",100,100,"Cave","Sealed Dungeon",null,ExplorationId:"cave",CharacterId:"one");
    [Theory]
    [InlineData(130,140,true)]
    [InlineData(131,140,false)]
    [InlineData(100,100,true)]
    public void RadiusIncludesBoundary(double x,double y,bool expected)
    {
        Assert.Equal(expected,ExplorationArrivalService.Reached(Settings,Target,new("altgard",x,y,Now),"one",Now) is not null);
    }
    [Fact]
    public void WrongCharacterMapOrStalePositionCannotComplete()
    {
        Assert.Null(ExplorationArrivalService.Reached(Settings,Target,new("altgard",100,100,Now),"two",Now));
        Assert.Null(ExplorationArrivalService.Reached(Settings,Target,new("poeta",100,100,Now),"one",Now));
        Assert.Null(ExplorationArrivalService.Reached(Settings,Target,new("altgard",100,100,Now.AddSeconds(-3)),"one",Now));
        Assert.Null(ExplorationArrivalService.Reached(Settings,Target,new("altgard",double.NaN,100,Now),"one",Now));
    }
    [Fact]
    public void DisabledRadiusPetModeAndWrongCategoryCannotComplete()
    {
        var settings=Settings; var position=new PlayerPosition("altgard",100,100,Now);
        settings.ExplorationCompletionRadius=0;
        Assert.Null(ExplorationArrivalService.Reached(settings,Target,position,"one",Now));
        settings.ExplorationCompletionRadius=50; settings.ProgressionKinds=["pets"];
        Assert.Null(ExplorationArrivalService.Reached(settings,Target,position,"one",Now));
        settings.ProgressionKinds=["stronghold"];
        Assert.Null(ExplorationArrivalService.Reached(settings,Target,position,"one",Now));
        settings.ProgressionKinds=["pets","stronghold","dungeon"]; // a mixed selection with dungeons
        Assert.Equal("cave",ExplorationArrivalService.Reached(settings,Target,position,"one",Now));
        settings.ProgressionEnabled=false;
        Assert.Null(ExplorationArrivalService.Reached(settings,Target,position,"one",Now));
    }
}

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
        Assert.Empty(ExplorationArrivalService.ArrivedTargets([A, B], new("altgard", 100, 100, Now), 40, armed, Now));
        // Walked away, then back: A is reached, B (far) stays.
        Assert.Empty(ExplorationArrivalService.ArrivedTargets([A, B], new("altgard", 300, 100, Now), 40, armed, Now));
        Assert.Equal(["a"], ExplorationArrivalService.ArrivedTargets([A, B], new("altgard", 120, 125, Now), 40, armed, Now));
    }

    [Fact]
    public void HandMarkedPetsStayOnArrivalRouteStopsGo()
    {
        var pet = new MapTarget("p", "altgard", 100, 100, "Kuru", "Pets · Fera", null, PetId: "kuru");
        var routePet = pet with { Id = "r", RouteId = "route" };
        var after = new MapTarget("b", "altgard", 500, 100, "Kibelisk", "Locations · Kibelisk", null, After: "p");
        var armed = new HashSet<string>();
        Assert.Empty(ExplorationArrivalService.ArrivedTargets([pet, routePet, after], new("altgard", 300, 300, Now), 40, armed, Now));
        // On the pet: only the route stop goes; the hand-marked pet waits for its next level.
        Assert.Equal(["r"], ExplorationArrivalService.ArrivedTargets([pet, routePet, after], new("altgard", 100, 100, Now), 40, armed, Now));
        Assert.Empty(ExplorationArrivalService.ArrivedTargets([pet, after], new("altgard", 300, 300, Now), 40, armed, Now));
        // Reaching the stop after it does not take the skipped pet along.
        Assert.Equal(["b"], ExplorationArrivalService.ArrivedTargets([pet, after], new("altgard", 500, 100, Now), 40, armed, Now));
        Assert.True(ExplorationArrivalService.StaysUntilNextLevel(new MapTarget("o", "altgard", 0, 0, "Kuru", "Pets · Fera", null))); // older mark without petId
    }

    [Fact]
    public void SkippedStopsDisappearWithTheReachedOne()
    {
        var c = new MapTarget("c", "altgard", 900, 100, "Teleporter", "Locations", null, After: "b");
        var other = new MapTarget("x", "altgard", 100, 900, "Other", "Locations", null);
        var armed = new HashSet<string>();
        Assert.Empty(ExplorationArrivalService.ArrivedTargets([A, B, c, other], new("altgard", 300, 300, Now), 40, armed, Now));
        // Straight to C: A and B were skipped and go with it, first stop first; the unchained target stays.
        Assert.Equal(["a", "b", "c"], ExplorationArrivalService.ArrivedTargets([A, B, c, other], new("altgard", 900, 100, Now), 40, armed, Now));
    }

    [Fact]
    public void OtherMapStalePositionOrRadiusZeroRemovesNothing()
    {
        var armed = new HashSet<string> { "a" };
        Assert.Empty(ExplorationArrivalService.ArrivedTargets([A], new("poeta", 100, 100, Now), 40, armed, Now));
        Assert.Empty(ExplorationArrivalService.ArrivedTargets([A], new("altgard", 100, 100, Now.AddSeconds(-3)), 40, armed, Now));
        Assert.Empty(ExplorationArrivalService.ArrivedTargets([A], new("altgard", 100, 100, Now), 0, armed, Now));
    }
}
