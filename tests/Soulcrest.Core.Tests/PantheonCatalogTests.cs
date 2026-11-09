using Soulcrest.Core.Pantheon;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class PantheonCatalogTests
{
    [Fact]
    public void DisplayCollectionHasExactly120GlobalAnd98PerKnownFactionWithoutInternalRows()
    {
        var catalog = PantheonCatalog.Default;
        Assert.Equal(26425393, catalog.MetadataBuild);
        Assert.Equal(120, catalog.Items.Count);
        Assert.Equal(120, catalog.Items.Select(item => item.Id).Distinct().Count());
        Assert.Equal(96, catalog.Items.Count(item => item.Category == PantheonDecorCategory.Artwork));
        Assert.Equal(20, catalog.Items.Count(item => item.Category == PantheonDecorCategory.Statue));
        Assert.Equal(4, catalog.Items.Count(item => item.Category == PantheonDecorCategory.Colossus));
        foreach (var faction in new[] { "elyos", "asmodian" })
        {
            var own = catalog.Items.Where(item => item.IncludesFaction(faction)).ToArray();
            Assert.Equal(98, own.Length);
            Assert.Equal(79, own.Count(item => item.Category == PantheonDecorCategory.Artwork));
            Assert.Equal(16, own.Count(item => item.Category == PantheonDecorCategory.Statue));
            Assert.Equal(3, own.Count(item => item.Category == PantheonDecorCategory.Colossus));
        }
        Assert.DoesNotContain(catalog.Items, item => item.IncludesFaction(null));
        Assert.DoesNotContain(catalog.Items, item => item.IncludesFaction("unknown"));
        Assert.Null(catalog.Find(1)); Assert.Null(catalog.Find(2)); Assert.Null(catalog.Find(-1));
    }

    [Fact]
    public void NativeSlotsKeepTheExactCategoryMembershipAnd12Artwork4Statue1ColossusCapacity()
    {
        var slots = PantheonCatalog.Default.Slots;
        Assert.Equal(Enumerable.Range(1, 17), slots.Select(slot => slot.Id));
        Assert.Equal(Enumerable.Range(1, 12), slots.Where(slot => slot.Category == PantheonDecorCategory.Artwork).Select(slot => slot.Id));
        Assert.Equal(Enumerable.Range(13, 4), slots.Where(slot => slot.Category == PantheonDecorCategory.Statue).Select(slot => slot.Id));
        Assert.Equal(17, Assert.Single(slots, slot => slot.Category == PantheonDecorCategory.Colossus).Id);
    }

    [Fact]
    public void ReciprocalItemIdentityAndNamedDisplayEffectsRemainSeparateFromPossession()
    {
        var artwork = PantheonCatalog.Default.Find(10)!;
        Assert.Equal(534310010, artwork.ItemId); Assert.Equal("Artwork: Quai Merchants", artwork.Name("en"));
        Assert.Equal(PantheonDecorCategory.Artwork, artwork.Category); Assert.Equal(2, artwork.Grade);
        Assert.True(artwork.IncludesFaction("asmodian")); Assert.False(artwork.IncludesFaction("elyos"));
        Assert.Equal(new[] { new PantheonGodEffect("Illusion", 2), new PantheonGodEffect("Life", 2) }, artwork.Effects);
        var shulak = PantheonCatalog.Default.Find(11)!;
        Assert.Equal(3, shulak.EffectAmount("Illusion")); Assert.Equal(3, shulak.EffectAmount("Destiny"));
        var colossus = PantheonCatalog.Default.Find(5010)!;
        Assert.Equal(534330011, colossus.ItemId); Assert.Equal(4, colossus.Grade);
        Assert.Equal(PantheonDecorCategory.Colossus, colossus.Category);
        Assert.Equal(new[] { new PantheonGodEffect("Destruction", 45), new PantheonGodEffect("Justice", 45), new PantheonGodEffect("Time", 45) }, colossus.Effects);
        Assert.Equal(0, colossus.EffectAmount("Attack")); Assert.Equal(0, colossus.EffectAmount("Light"));
    }

    [Fact]
    public void MetadataHasOnlyNamedGodPointEffectsAndNeverNativeWireOrCombatConversionRules()
    {
        var gods = new[] { "Justice", "Freedom", "Illusion", "Life", "Time", "Destruction", "Death", "Wisdom", "Destiny", "Space" };
        var catalog = PantheonCatalog.Default;
        Assert.All(catalog.Items, item =>
        {
            Assert.NotEmpty(item.Effects); Assert.True(item.ItemId > 0);
            Assert.All(item.Effects, effect => { Assert.Contains(effect.StatId, gods); Assert.True(effect.Amount > 0); });
            Assert.Equal(item.Effects.Count, item.Effects.Select(effect => effect.StatId).Distinct().Count());
            Assert.DoesNotContain('\uFFFD', item.NameDe); Assert.DoesNotContain('\uFFFD', item.NameEn);
            Assert.False(string.IsNullOrWhiteSpace(item.NameDe)); Assert.False(string.IsNullOrWhiteSpace(item.NameEn));
        });
        Assert.Equal(gods.OrderBy(id => id), catalog.Items.SelectMany(item => item.Effects).Select(effect => effect.StatId).Distinct().OrderBy(id => id));
    }

    [Fact]
    public void MissingArtworkImagesStayExplicitlyEmptyAndCatalogCollectionsAreFrozen()
    {
        var catalog = PantheonCatalog.Default;
        Assert.Equal(new[] { 198, 199, 200, 201 }, catalog.Items.Where(item => item.IconPath.Length == 0).Select(item => item.Id));
        Assert.All(catalog.Items.Where(item => item.IconPath.Length != 0), item =>
        {
            Assert.StartsWith("pantheon/p-", item.IconPath); Assert.EndsWith(".png", item.IconPath);
            Assert.DoesNotContain("..", item.IconPath); Assert.DoesNotContain(':', item.IconPath);
        });
        Assert.Throws<NotSupportedException>(() => ((IList<PantheonDecor>)catalog.Items).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<PantheonDecorSlot>)catalog.Slots).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<PantheonGodEffect>)catalog.Find(5010)!.Effects).Clear());
    }
}
