using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class LootOverlayRowsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 10, 0, 0, TimeSpan.Zero);
    private static readonly PetCatalog Catalog = new([
        new PetDefinition { Id = "modified-karnif", En = "Modified Karnif", Genus = "fera", NumericId = "1098" },
        new PetDefinition { Id = "kalnif-02", En = "Modified Karnif", Genus = "fera", NumericId = "1098", LegacyDuplicate = true },
    ]);

    [Fact]
    public void FocusPickupsAreCombinedOnTheFocusRowIncludingLegacyIds()
    {
        var result = PetOverlayForm.LootToasts([
            Drop("modified-karnif", 2, 5), Drop("kalnif-02", 3, 10), Drop("other-pet", 1, 15),
        ], Catalog, "kalnif-02", Now);

        Assert.Equal(5, result.FocusQuantity);
        var other = Assert.Single(result.Drops);
        Assert.Equal("other-pet", other.PetId);
        Assert.Equal(1, other.Quantity);
    }

    [Fact]
    public void ChangingFocusReturnsThePreviousFocusToRecentLoot()
    {
        LoggedSoul[] recent = [Drop("modified-karnif", 2, 5), Drop("other-pet", 4, 10)];
        var first = PetOverlayForm.LootToasts(recent, Catalog, "modified-karnif", Now);
        var next = PetOverlayForm.LootToasts(recent, Catalog, "other-pet", Now);

        Assert.Equal(2, first.FocusQuantity);
        Assert.Equal("other-pet", Assert.Single(first.Drops).PetId);
        Assert.Equal(4, next.FocusQuantity);
        Assert.Equal("modified-karnif", Assert.Single(next.Drops).PetId);
        Assert.Equal(2, next.Drops[0].Quantity);
        Assert.Equal(6, recent.Sum(drop => drop.Quantity));
    }

    [Fact]
    public void FiveOtherPetsRemainVisibleAfterRemovingFocusFromRecentLoot()
    {
        var recent = Enumerable.Range(1, 6).Select(i => Drop($"synthetic-pet-{i}", 1, i))
            .Prepend(Drop("modified-karnif", 2, 0));
        var result = PetOverlayForm.LootToasts(recent, Catalog, "modified-karnif", Now);

        Assert.Equal(2, result.FocusQuantity);
        Assert.Equal(Enumerable.Range(1, 5).Select(i => $"synthetic-pet-{i}"), result.Drops.Select(drop => drop.PetId));
    }

    [Fact]
    public void ExpiredPickupsDoNotLeaveAQuantityOnThePersistentFocusRow()
    {
        var result = PetOverlayForm.LootToasts([
            Drop("modified-karnif", 5, 60), Drop("other-pet", 1, 59), Drop("expired-pet", 3, 61),
        ], Catalog, "modified-karnif", Now);

        Assert.Equal(0, result.FocusQuantity);
        Assert.Equal("other-pet", Assert.Single(result.Drops).PetId);
    }

    [Fact]
    public void WithoutFocusTheCanonicalPetAppearsOnceWithItsNewestTimestamp()
    {
        var result = PetOverlayForm.LootToasts([
            Drop("modified-karnif", 2, 5), Drop("kalnif-02", 3, 10), Drop("other-pet", 1, 15),
        ], Catalog, null, Now);

        Assert.Equal(0, result.FocusQuantity);
        Assert.Equal(2, result.Drops.Count);
        Assert.Equal(new PetOverlayForm.Toast("modified-karnif", 5, Now.AddSeconds(-5)), result.Drops[0]);
    }

    private static LoggedSoul Drop(string petId, int quantity, int ageSeconds) =>
        new(Now.AddSeconds(-ageSeconds), petId, quantity, "network", "synthetic loot event");
}
