using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class MonsterLootIconCatalogTests
{
    private const string Catalog = """
        {"schemaVersion":1,"items":[
          {"id":210350009,"asset":"loot-icons/i-helm.png"},
          {"id":531900005,"asset":"loot-icons/i-fera.png"},
          {"id":531900004,"asset":"loot-icons/i-cogni.png"},
          {"id":531900006,"asset":"loot-icons/i-natura.png"},
          {"id":531900007,"asset":"loot-icons/i-varian.png"},
          {"id":531900008,"asset":"loot-icons/i-special.png"}],
          "pets":[{"id":1004,"asset":"loot-icons/i-pet.png"}]}
        """;

    [Fact]
    public void ExplicitItemPetAndNativeCrystalBindingsRemainSeparate()
    {
        var catalog = MonsterLootIconCatalog.FromJson(Catalog);
        Assert.Equal(6, catalog.ItemCount); Assert.Equal(1, catalog.PetCount);
        Assert.Equal("loot-icons/i-helm.png", catalog.ItemAsset(210350009));
        Assert.Equal("loot-icons/i-pet.png", catalog.PetSoulAsset(1004));
        Assert.Null(catalog.ItemAsset(1004)); Assert.Null(catalog.PetSoulAsset(210350009));
        Assert.Equal(new[] { "loot-icons/i-fera.png", "loot-icons/i-cogni.png", "loot-icons/i-natura.png",
            "loot-icons/i-varian.png", "loot-icons/i-special.png" }, Enumerable.Range(1, 5).Select(catalog.SoulCrystalAsset));
        Assert.Null(catalog.SoulCrystalAsset(0)); Assert.Null(catalog.SoulCrystalAsset(6));
    }

    [Theory]
    [InlineData("Item:210350009", "loot-icons/i-helm.png")]
    [InlineData("item:210350009", "loot-icons/i-helm.png")]
    [InlineData("SoulCrystal:2", "loot-icons/i-cogni.png")]
    [InlineData("PetSoul:1004", "loot-icons/i-pet.png")]
    public void ExactCanonicalKeysSelectOnlyTheirExplicitType(string key, string expected) =>
        Assert.Equal(expected, MonsterLootIconCatalog.FromJson(Catalog).DropAsset(key));

    [Fact]
    public void UniversalCrystalUsesTheExactCurrencyIconBeforeItsOwnAutoConsumedItemFallback()
    {
        var catalog = MonsterLootIconCatalog.FromJson("""
            {"schemaVersion":1,"items":[
              {"id":930500001,"asset":"loot-icons/i-universal-currency.png"},
              {"id":531900009,"asset":"loot-icons/i-universal-item.png"}],"pets":[]}
            """);
        Assert.Equal("loot-icons/i-universal-currency.png", catalog.DropAsset("UniversalSoulCrystal:930500001"));
        var fallback = MonsterLootIconCatalog.FromJson("""
            {"schemaVersion":1,"items":[{"id":531900009,"asset":"loot-icons/i-universal-item.png"}],"pets":[]}
            """);
        Assert.Equal("loot-icons/i-universal-item.png", fallback.DropAsset("UniversalSoulCrystal:930500001"));
        Assert.Null(catalog.DropAsset("UniversalSoulCrystal:531900009"));
        Assert.Null(catalog.DropAsset("UniversalSoulCrystal:2"));
        Assert.Null(MonsterLootIconCatalog.FromJson(Catalog).DropAsset("UniversalSoulCrystal:930500001"));
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("unknown:210350009")]
    [InlineData("ITEM:210350009")] [InlineData("Chaos Helm")] [InlineData("Item:-1")]
    [InlineData("Item:0")] [InlineData("Item: 210350009")] [InlineData("Item:210350009:1")]
    [InlineData("Item:+210350009")] [InlineData("SoulCrystal:6")] [InlineData("PetSoul:999999")]
    public void UnknownOrMalformedKeysRetainTheNeutralFallback(string? key) =>
        Assert.Null(MonsterLootIconCatalog.FromJson(Catalog).DropAsset(key));

    [Theory]
    [InlineData("../secret.png")] [InlineData("loot-icons/../secret.png")]
    [InlineData("https://example.invalid/icon.png")] [InlineData("C:/icon.png")]
    [InlineData("loot-icons/a.webp")] [InlineData("loot-icons/a.png?remote=1")]
    public void NonBundledOrUnsafeAssetPathsAreRejected(string path)
    {
        var json = "{\"schemaVersion\":1,\"items\":[{\"id\":1,\"asset\":\"" + path + "\"}],\"pets\":[]}";
        Assert.Throws<InvalidDataException>(() => MonsterLootIconCatalog.FromJson(json));
    }

    [Theory]
    [InlineData("{\"schemaVersion\":2,\"items\":[],\"pets\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"items\":null,\"pets\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"items\":[{\"id\":1,\"asset\":\"loot-icons/a.png\"},{\"id\":1,\"asset\":\"loot-icons/b.png\"}],\"pets\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"items\":[{\"id\":0,\"asset\":\"loot-icons/a.png\"}],\"pets\":[]}")]
    public void UnsupportedSchemasAndAmbiguousIdsAreRejected(string json) =>
        Assert.Throws<InvalidDataException>(() => MonsterLootIconCatalog.FromJson(json));

    [Fact]
    public void BundledCatalogContainsOriginalIconsForRequiredObservedDrops()
    {
        var catalog = MonsterLootIconCatalog.Default;
        Assert.Equal(MonsterLootItemCatalog.Default.Count, catalog.ItemCount); Assert.Equal(207, catalog.PetCount);
        Assert.NotNull(catalog.ItemAsset(930100031)); Assert.NotNull(catalog.ItemAsset(930100001));
        Assert.NotNull(catalog.ItemAsset(210350009));
        Assert.NotNull(catalog.DropAsset("UniversalSoulCrystal:930500001"));
        Assert.All(Enumerable.Range(1, 5), type => Assert.NotNull(catalog.SoulCrystalAsset(type)));
        Assert.NotNull(catalog.PetSoulAsset(1004));
    }
}
