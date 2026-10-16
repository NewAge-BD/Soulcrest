using Soulcrest.App.Overlay;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class PetLevelUpPlacementTests
{
    private static PetOverlayForm.LootRow Row(string? id, int quantity = 1, int alpha = 120, bool max = false) =>
        new("Shared monster name", "fera", max ? 3 : 2, max, 4, max ? 0 : 75,
            Quantity: quantity, Alpha: alpha, SoulName: "Shared pet name", PetId: id);
    private static PetOverlayForm.LevelUpRow Frame(string? id, int level = 2, bool max = false) =>
        new("Shared pet name", level, max, null, .4, PetId: id);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void TheMatchingFocusRowWinsWithoutDuplicatingThePetInRecentLoot(int level)
    {
        var active = Frame("active-pet", level);
        var focused = Row("active-pet", quantity: 7);
        var other = Row("other-pet", quantity: 3);
        var result = PetOverlayForm.PlaceLevelUpRows([focused], [focused, other], active, Row("active-pet", 0));
        var decorated = Assert.Single(result.Focus);
        Assert.Equal(focused with { Alpha = 255, LevelUp = active }, decorated);
        Assert.Same(active, decorated.LevelUp);
        Assert.Equal(other, Assert.Single(result.Drops));
        Assert.Null(focused.LevelUp);
        Assert.Equal(120, focused.Alpha);
        Assert.Equal(7, focused.Quantity);
    }

    [Fact]
    public void APreviousFocusPetAnimatesOnItsOwnDropWhileTheNewFocusRemainsUnchanged()
    {
        var focused = Row("next-focus", quantity: 4);
        var previous = Row("previous-focus", quantity: 2);
        var active = Frame("previous-focus");
        var result = PetOverlayForm.PlaceLevelUpRows([focused], [Row("other"), previous], active);
        Assert.Equal(focused, Assert.Single(result.Focus));
        Assert.Equal(previous with { Alpha = 255, LevelUp = active }, result.Drops[0]);
        Assert.Equal("other", result.Drops[1].PetId);
        Assert.Null(result.Focus[0].LevelUp);
    }

    [Fact]
    public void AnExistingSixthDropIsPinnedBeforeTheLimitWithoutLosingItsActualQuantity()
    {
        var drops = Enumerable.Range(1, 7).Select(index => Row("pet-" + index, quantity: index)).ToArray();
        var active = Frame("pet-6");
        var result = PetOverlayForm.PlaceLevelUpRows([], drops, active, Row("pet-6", 0));
        Assert.Empty(result.Focus);
        Assert.Equal(new[] { "pet-6", "pet-1", "pet-2", "pet-3", "pet-4" }, result.Drops.Select(row => row.PetId));
        Assert.Equal(6, result.Drops[0].Quantity);
        Assert.Equal(255, result.Drops[0].Alpha);
        Assert.Same(active, result.Drops[0].LevelUp);
        Assert.All(result.Drops.Skip(1), row => Assert.Null(row.LevelUp));
        Assert.All(drops, row => { Assert.Null(row.LevelUp); Assert.Equal(120, row.Alpha); });
    }

    [Fact]
    public void AnUpgradeWithoutLootGetsOnlyATemporaryZeroQuantityDropAndNoFocusRow()
    {
        var fallback = Row("active-pet", quantity: 0, alpha: 60);
        var active = Frame("active-pet", 1);
        var seeds = new[] { Row("other-pet", quantity: 5) };
        var result = PetOverlayForm.PlaceLevelUpRows([], seeds, active, fallback);
        Assert.Empty(result.Focus);
        Assert.Equal(fallback with { Alpha = 255, LevelUp = active }, result.Drops[0]);
        Assert.Equal(0, result.Drops[0].Quantity);
        Assert.Equal(4, result.Drops[0].Current);
        Assert.Equal(75, result.Drops[0].Needed);
        Assert.Equal(5, result.Drops.Sum(row => row.Quantity));
        var expired = PetOverlayForm.PlaceLevelUpRows([], seeds, null, fallback);
        Assert.Equal(seeds, expired.Drops);
        Assert.Null(fallback.LevelUp);
    }

    [Theory]
    [InlineData("different-id")]
    [InlineData("missing-id")]
    [InlineData("blank-id")]
    [InlineData("positive-quantity")]
    [InlineData("max")]
    public void OnlyAMatchingNonMaxZeroQuantityFallbackIsEligible(string invalid)
    {
        var fallback = invalid switch
        {
            "different-id" => Row("other", 0),
            "missing-id" => Row(null, 0),
            "blank-id" => Row(" ", 0),
            "positive-quantity" => Row("active", 9),
            "max" => Row("active", 0, max: true),
            _ => throw new ArgumentOutOfRangeException(nameof(invalid))
        };
        var result = PetOverlayForm.PlaceLevelUpRows([], [], Frame("active"), fallback);
        Assert.Empty(result.Focus);
        Assert.Empty(result.Drops);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("canonical-pet")]
    public void SharedNamesMissingIdsAndLegacyIdSpellingsNeverGuessAnAnimationTarget(string? activeId)
    {
        var old = Frame("old");
        var focused = Row("legacy-pet") with { LevelUp = old };
        var dropped = Row("other-pet") with { LevelUp = old };
        var result = PetOverlayForm.PlaceLevelUpRows([focused], [dropped], Frame(activeId));
        Assert.Equal(focused with { LevelUp = null }, Assert.Single(result.Focus));
        Assert.Equal(dropped with { LevelUp = null }, Assert.Single(result.Drops));
        Assert.Same(old, focused.LevelUp);
        Assert.Same(old, dropped.LevelUp);
    }

    [Fact]
    public void MaximumRowsAndTheActiveMaximumPetAreRemovedBeforeOtherDropsMoveUp()
    {
        var active = Frame("active-max", 3, max: true);
        var drops = new[] { Row("already-max", max: true), Row("active-max", max: false) }
            .Concat(Enumerable.Range(1, 6).Select(index => Row("eligible-" + index))).ToArray();
        var focused = Row("next-focus");
        var result = PetOverlayForm.PlaceLevelUpRows([focused], drops, active, Row("active-max", 0));
        Assert.Equal(focused, Assert.Single(result.Focus));
        Assert.Equal(Enumerable.Range(1, 5).Select(index => "eligible-" + index), result.Drops.Select(row => row.PetId));
        Assert.All(result.Focus.Concat(result.Drops), row => Assert.Null(row.LevelUp));
        Assert.Equal(8, drops.Length);
    }

    [Fact]
    public void MaximumDropsAreFilteredWithoutAnyActiveAnimationOrAnId()
    {
        var result = PetOverlayForm.PlaceLevelUpRows([], [Row(null, max: true), Row("max", max: true), Row("normal")], null);
        Assert.Equal("normal", Assert.Single(result.Drops).PetId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(4)]
    public void OnlyRegularLevelsOneAndTwoCanDecorateAnInlineRow(int level)
    {
        var focused = Row("pet");
        var result = PetOverlayForm.PlaceLevelUpRows([focused], [], Frame("pet", level), Row("pet", 0));
        Assert.Equal(focused, Assert.Single(result.Focus));
        Assert.Empty(result.Drops);
    }

    [Fact]
    public void RecompositionFromUndecoratedSeedsClearsOldInlineStateWithoutChangingRealCounters()
    {
        var seeds = new[] { Row("first", 3), Row("second", 4) };
        var first = PetOverlayForm.PlaceLevelUpRows([], seeds, Frame("first"));
        var secondFrame = Frame("second", 1);
        var second = PetOverlayForm.PlaceLevelUpRows([], first.Drops, secondFrame);
        Assert.Equal("second", second.Drops[0].PetId);
        Assert.Same(secondFrame, second.Drops[0].LevelUp);
        Assert.Null(second.Drops[1].LevelUp);
        Assert.Equal(7, second.Drops.Sum(row => row.Quantity));
        Assert.Equal(4, second.Drops[0].Current);
        Assert.Equal(75, second.Drops[0].Needed);
        Assert.All(seeds, row => Assert.Null(row.LevelUp));
    }
}
