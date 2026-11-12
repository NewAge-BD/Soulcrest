using System.Collections.Frozen;
using System.Text.Json;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class ItemNamePresentationTests
{
    [Theory]
    [InlineData(LootItemRarity.Common, "#C3C3C3")]
    [InlineData(LootItemRarity.Rare, "#19E048")]
    [InlineData(LootItemRarity.Legend, "#379AFF")]
    [InlineData(LootItemRarity.Unique, "#FFD02B")]
    [InlineData(LootItemRarity.Epic, "#FF6C00")]
    [InlineData(LootItemRarity.Mythic, "#FF3245")]
    [InlineData(LootItemRarity.Special, "#00FFD8")]
    public void EveryGradeUsesOneSharedOpaqueTextForeground(LootItemRarity grade, string css)
    {
        var style = ItemNamePresentation.ForGrade(grade);
        Assert.Equal(css, style.CssColor);
        Assert.Equal(grade.ToString(), style.Grade);
        Assert.True(style.IsKnown);
        Assert.Equal(255, style.Color!.Value.A);
        Assert.Equal(css, $"#{style.Color.Value.R:X2}{style.Color.Value.G:X2}{style.Color.Value.B:X2}");
        Assert.Equal(style, ItemNamePresentation.ForGrade(grade.ToString()));
        Assert.Equal(style, ItemNamePresentation.ForGrade("EItemGrade::" + grade));
        Assert.Equal(style.Color, LootRarityVisuals.NameColor(grade));
        Assert.Equal(css, LootRarityVisuals.NameCssColor(grade));
    }

    [Fact]
    public void ExactItemLookupAndReadOnlyMapUseTheSameStylesWithoutChangingLighting()
    {
        Assert.Equal(9452, ItemNamePresentation.ItemCount);
        Assert.Equal(7, ItemNamePresentation.GradeCount);
        Assert.Equal(ItemNamePresentation.ForGrade("Unique"), ItemNamePresentation.ForItem(110120003));
        Assert.Equal("#FFD02B", ItemNamePresentation.CssColorsByItemId[110120003]);
        Assert.IsAssignableFrom<FrozenDictionary<int, string>>(ItemNamePresentation.CssColorsByItemId);
        Assert.Equal(ItemNamePresentation.ItemCount, ItemNamePresentation.CssColorsByItemId.Count);
        // The established animation accent remains a separate visual choice.
        Assert.Equal(System.Drawing.Color.FromArgb(195, 142, 251), LootRarityVisuals.Accent(LootItemRarity.Epic));
        Assert.NotEqual(LootRarityVisuals.Accent(LootItemRarity.Epic), ItemNamePresentation.ForGrade("Epic").Color);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Unknown")]
    [InlineData("FutureGrade")]
    [InlineData("EItemGrade::FutureGrade")]
    public void UnknownGradesAndItemIdsRemainNeutral(string? grade)
    {
        Assert.Equal(ItemNameStyle.Neutral, ItemNamePresentation.ForGrade(grade));
        Assert.Null(ItemNamePresentation.ForItem(1).CssColor);
        Assert.Null(ItemNamePresentation.ForItem(0).Color);
        Assert.Null(ItemNamePresentation.ForItem(-1).Color);
        Assert.Equal(ItemNameStyle.Neutral, ItemNamePresentation.ForGrade((LootItemRarity)999));
        Assert.Null(LootRarityVisuals.NameColor(LootItemRarity.Unknown));
    }

    [Theory]
    [InlineData("Item:110120003", 110120003)]
    [InlineData("item:110120003", 110120003)]
    [InlineData("SoulCrystal:1", 531900005)]
    [InlineData("SoulCrystal:2", 531900004)]
    [InlineData("SoulCrystal:3", 531900006)]
    [InlineData("SoulCrystal:4", 531900007)]
    [InlineData("SoulCrystal:5", 531900008)]
    [InlineData("UniversalSoulCrystal:930500001", 930500001)]
    public void CanonicalDisplayKeysResolveOnlyTheirExactItem(string key, int id) =>
        Assert.Equal(ItemNamePresentation.ForItem(id), ItemNamePresentation.ForDropKey(key));

    [Theory]
    [InlineData(null)]
    [InlineData("Item:1")]
    [InlineData("Item:+110120003")]
    [InlineData("Item: 110120003")]
    [InlineData("ITEM:110120003")]
    [InlineData("SoulCrystal:0")]
    [InlineData("SoulCrystal:6")]
    [InlineData("UniversalSoulCrystal:531900009")]
    [InlineData("PetSoul:110120003")]
    [InlineData("Kina:110120003")]
    public void UnknownOrMalformedKeysDoNotInferAQuality(string? key) =>
        Assert.Equal(ItemNameStyle.Neutral, ItemNamePresentation.ForDropKey(key));

    [Theory]
    [InlineData("""{"schemaVersion":2,"palette":[],"items":[]}""")]
    [InlineData("""{"schemaVersion":1,"palette":[null],"items":[]}""")]
    [InlineData("""{"schemaVersion":1,"palette":[{"grade":"Epic","cssColor":"red"}],"items":[]}""")]
    [InlineData("""{"schemaVersion":1,"palette":[{"grade":"Epic","cssColor":"#FF6C00;display:none"}],"items":[]}""")]
    [InlineData("""{"schemaVersion":1,"palette":[{"grade":"Future","cssColor":"#FF6C00"}],"items":[]}""")]
    [InlineData("""{"schemaVersion":1,"palette":[{"grade":"Epic","cssColor":"#FF6C00"},{"grade":"Epic","cssColor":"#FF6C00"}],"items":[]}""")]
    [InlineData("""{"schemaVersion":1,"palette":[],"items":[{"id":1,"grade":"Epic"}]}""")]
    [InlineData("""{"schemaVersion":1,"palette":[],"items":[],"sourcePath":"private"}""")]
    public void InvalidCatalogsCannotBecomePresentationFacts(string json) =>
        Assert.Throws<InvalidDataException>(() => ItemNamePresentation.FromJson(json));

    [Fact]
    public void BundledProductContainsOnlyPaletteAndExactIdentityFacts()
    {
        using var stream = typeof(ItemNamePresentation).Assembly.GetManifestResourceStream("Soulcrest.ItemNameAppearance.json");
        Assert.NotNull(stream);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        Assert.Equal(["items", "palette", "schemaVersion"], root.EnumerateObject().Select(p => p.Name).Order().ToArray());
        foreach (var row in root.GetProperty("palette").EnumerateArray())
            Assert.Equal(["cssColor", "grade"], row.EnumerateObject().Select(p => p.Name).Order().ToArray());
        foreach (var row in root.GetProperty("items").EnumerateArray())
            Assert.Equal(["grade", "id"], row.EnumerateObject().Select(p => p.Name).Order().ToArray());
    }
}
