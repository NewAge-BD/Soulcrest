using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class LootOverlayFocusTests
{
    private static MapTarget Target(string id, string? petId, string? after = null, string map = "altgard") =>
        new(id, map, 100, 200, "Shared display name", petId is null ? "Locations" : "Pets · Fera", null, after, petId);

    [Fact]
    public void ManualPetPrecedesProgressionAndDoesNotDependOnCollectedLoot()
    {
        var settings = new AppSettings { ProgressionEnabled = true };
        var manual = Target("manual", "ursus");
        var progression = Target("progression", "kailin");
        Assert.Same(manual, PetOverlayForm.SelectFocusTarget(settings, [manual], progression));
        Assert.Same(progression, PetOverlayForm.SelectFocusTarget(settings, [], progression));
        settings.ProgressionEnabled = false;
        Assert.Null(PetOverlayForm.SelectFocusTarget(settings, [], progression));
        Assert.Same(manual, PetOverlayForm.SelectFocusTarget(settings, [manual], progression));
    }

    [Fact]
    public void PetChainShowsItsActiveFirstStopAndAdvancesWhenThatStopIsRemoved()
    {
        var settings = new AppSettings();
        var first = Target("first", "ursus");
        var second = Target("second", "kailin", first.Id);
        var third = Target("third", "tayga", second.Id);
        Assert.Same(first, PetOverlayForm.SelectFocusTarget(settings, [first, second, third], null));
        Assert.Same(second, PetOverlayForm.SelectFocusTarget(settings, [second, third], null));
        Assert.Same(third, PetOverlayForm.SelectFocusTarget(settings, [third], null));
    }

    [Fact]
    public void FuturePetWaitsWhileTheRouteStillLeadsToANonPetStop()
    {
        var stop = Target("teleporter", null);
        var pet = Target("pet", "kailin", stop.Id);
        Assert.Null(PetOverlayForm.SelectFocusTarget(new AppSettings(), [stop, pet], null));
        Assert.Same(pet, PetOverlayForm.SelectFocusTarget(new AppSettings(), [pet], null));
    }

    [Fact]
    public void CurrentMapAndThenMarkingOrderChooseAmongIndependentPetTargets()
    {
        var elsewhere = Target("elsewhere", "tayga", map: "verteron");
        var first = Target("first", "ursus");
        var second = Target("second", "kailin");
        Assert.Same(first, PetOverlayForm.SelectFocusTarget(new AppSettings(), [elsewhere, first, second], null));
        Assert.Same(elsewhere, PetOverlayForm.SelectFocusTarget(new AppSettings { LastMap = "poeta" }, [elsewhere, first, second], null));
    }

    [Fact]
    public void DisplayNamesAndNonPetProgressionNeverCreateAnAccidentalPetFocus()
    {
        var marker = Target("resource", null) with { Name = "Kailin" };
        var oldMarker = marker with { Id = "old-pet-name", Kind = "Pets · Fera" };
        var emptyPetId = marker with { Id = "blank", PetId = " " };
        var settings = new AppSettings { ProgressionEnabled = true };
        Assert.Null(PetOverlayForm.SelectFocusTarget(settings, [marker, oldMarker, emptyPetId], marker));
        Assert.Null(PetOverlayForm.SelectFocusTarget(settings, [], null));
    }
}
