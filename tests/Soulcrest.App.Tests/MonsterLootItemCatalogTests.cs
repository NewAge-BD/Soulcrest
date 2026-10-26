using System.Text.Json;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Only the bundled runtime table is read; private exports are not test/runtime dependencies.</summary>
public sealed class MonsterLootItemCatalogTests
{
    [Theory]
    [InlineData(210350009, "Chaos Helm", "Helm des Chaos", false, false)]
    [InlineData(534660212, "Soul: Kerubiel (Bound)", "Seele: Kerubiel (Gebunden)", true, false)]
    [InlineData(531900004, "Cogni Soul Crystal (Bound)", "Cognia-Seelenkristall (Gebunden)", true, false)]
    [InlineData(930100031, "Kina", "Kinah", false, true)]
    [InlineData(1999999999, "Item 1999999999", "Item 1999999999", false, false)]
    public void EvidencedExamplesHaveExactNamesAndExplicitClassifications(int id, string english, string german,
        bool automaticallyConsumedSoul, bool currency)
    {
        var catalog = MonsterLootItemCatalog.Default;
        Assert.Equal(english, catalog.Name(id));
        Assert.Equal(english, catalog.Name(id, "en"));
        Assert.Equal(german, catalog.Name(id, "de"));
        Assert.Equal(automaticallyConsumedSoul, catalog.IsAutomaticallyConsumedSoul(id));
        Assert.Equal(currency, catalog.IsCurrency(id));
    }

    [Fact]
    public void EmbeddedProjectionContainsOnlyRuntimeLabelsAndFlagsWithinTheSizeBudget()
    {
        using var stream = typeof(MonsterLootItemCatalog).Assembly.GetManifestResourceStream("Soulcrest.LootItems.json");
        Assert.NotNull(stream);
        Assert.InRange(stream.Length, 1, 2_000_000);
        using var document = JsonDocument.Parse(stream);
        Assert.Equal(["schemaVersion", "items"], document.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        var items = document.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(9334, items.Length);
        Assert.Equal(items.Length, MonsterLootItemCatalog.Default.Count);
        string[] allowed = ["id", "nameDe", "nameEn", "automaticallyConsumedSoul", "currency", "rarity", "exchangeType", "hasTradeCategory", "gear"];
        foreach (var item in items)
        {
            Assert.All(item.EnumerateObject(), property => Assert.Contains(property.Name, allowed));
            Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("nameEn").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("nameDe").GetString()));
        }
        var ids = items.Select(item => item.GetProperty("id").GetInt32()).ToArray();
        Assert.Equal(207, ids.Count(MonsterLootItemCatalog.Default.IsAutomaticallyConsumedSoul));
        Assert.Equal(56, ids.Count(MonsterLootItemCatalog.Default.IsCurrency));
        Assert.Equal(3555, ids.Count(MonsterLootItemCatalog.Default.IsGear));
        Assert.Equal(440, ids.Count(id => MonsterLootItemCatalog.Default.IsGear(id)
            && MonsterLootItemCatalog.Default.Rarity(id) == LootItemRarity.Common));
    }

    [Theory]
    [InlineData(110160001, LootItemRarity.Common, true)] // Worn Greatsword: exact native Equip row.
    [InlineData(110160008, LootItemRarity.Common, true)] // Training Greatsword.
    [InlineData(210230006, LootItemRarity.Unique, true)] // Rarity is independent of equipment type.
    [InlineData(610530001, LootItemRarity.Common, false)] // Refining Stone: Misc, not white equipment.
    [InlineData(630530001, LootItemRarity.Common, false)] // Bound material variant.
    [InlineData(660530001, LootItemRarity.Common, false)]
    [InlineData(610660025, LootItemRarity.Common, false)] // Sapphire Gemstone: GatherResource material.
    [InlineData(1999999999, LootItemRarity.Unknown, false)]
    public void CommonGearIsSeparatedFromWhiteMaterialsByItsExactNativeType(int id, LootItemRarity rarity, bool gear)
    {
        Assert.Equal(rarity, MonsterLootItemCatalog.Default.Rarity(id));
        Assert.Equal(gear, MonsterLootItemCatalog.Default.IsGear(id));
    }

    [Fact]
    public void GearFlagNeverUsesItemNamesRarityOrNearbyIdsAsAHeuristic()
    {
        var catalog = MonsterLootItemCatalog.FromJson("""
            {"schemaVersion":1,"items":[
              {"id":41,"nameEn":"Worn Greatsword","rarity":1},
              {"id":42,"nameEn":"Refining Stone","rarity":1,"gear":true},
              {"id":43,"nameEn":"Unique Greaves","rarity":4}
            ]}
            """);
        Assert.False(catalog.IsGear(41));
        Assert.True(catalog.IsGear(42));
        Assert.False(catalog.IsGear(43));
        Assert.False(catalog.IsGear(44));
    }

    [Fact]
    public void SpectralVariantsKeepTheirExactGradesAndDifferentStaticTradeFacts()
    {
        var catalog = MonsterLootItemCatalog.Default;
        Assert.Equal(LootItemRarity.Unique, catalog.Rarity(210230006));
        Assert.Equal(LootItemRarity.Unique, catalog.Rarity(210230081));
        Assert.Equal(new(LootItemExchangeType.SoulBind, true), catalog.TradeFacts(210230006));
        Assert.Equal(new(LootItemExchangeType.SoulBindOnAcquire, false), catalog.TradeFacts(210230081));
        Assert.Equal(LootItemRarity.Unknown, catalog.Rarity(1999999999));
        Assert.Equal(LootItemTradeFacts.Unknown, catalog.TradeFacts(1999999999));
        Assert.Equal(LootItemRarity.Unique, catalog.DropRarity("Item:210230006"));
        Assert.Equal(LootItemRarity.Unknown, catalog.DropRarity("SoulCrystal:210230006"));
        Assert.Equal(LootItemRarity.Unknown, catalog.DropRarity("Spectral Greaves"));
    }

    [Theory]
    [InlineData(LootItemRarity.Unknown, false)]
    [InlineData(LootItemRarity.Legend, false)]
    [InlineData(LootItemRarity.Unique, true)]
    [InlineData(LootItemRarity.Epic, true)]
    [InlineData(LootItemRarity.Mythic, true)]
    [InlineData(LootItemRarity.Special, true)]
    public void ExceptionalDropsUseTheExplicitWhitelist(LootItemRarity rarity, bool expected) =>
        Assert.Equal(expected, MonsterLootItemCatalog.IsExceptional(rarity));

    [Fact]
    public void ClassificationsAreNeverInferredFromNamesOrNearbyIds()
    {
        var catalog = MonsterLootItemCatalog.FromJson("""
            {"schemaVersion":1,"items":[
              {"id":41,"nameEn":"Soul Crystal (Bound)","nameDe":"Kina"},
              {"id":42,"nameEn":"Example","automaticallyConsumedSoul":true},
              {"id":43,"nameEn":"Example currency","currency":true}
            ]}
            """);
        Assert.False(catalog.IsAutomaticallyConsumedSoul(41));
        Assert.False(catalog.IsCurrency(41));
        Assert.True(catalog.IsAutomaticallyConsumedSoul(42));
        Assert.True(catalog.IsCurrency(43));
        Assert.False(catalog.IsAutomaticallyConsumedSoul(44));
        Assert.False(catalog.IsCurrency(44));
        Assert.Equal("Item 44", catalog.Name(44));
        Assert.Equal("Example", catalog.Name(42, "de"));
    }

    [Theory]
    [InlineData("{\"schemaVersion\":2,\"items\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"items\":null}")]
    [InlineData("{\"schemaVersion\":1,\"items\":[{\"id\":0,\"nameEn\":\"Example\"}]}")]
    [InlineData("{\"schemaVersion\":1,\"items\":[{\"id\":42,\"nameEn\":\" \"}]}")]
    [InlineData("{\"schemaVersion\":1,\"items\":[{\"id\":42,\"nameEn\":\"Example\",\"currency\":true,\"automaticallyConsumedSoul\":true}]}")]
    [InlineData("{\"schemaVersion\":1,\"items\":[{\"id\":42,\"nameEn\":\"Example\",\"currency\":true,\"gear\":true}]}")]
    [InlineData("{\"schemaVersion\":1,\"items\":[{\"id\":42,\"nameEn\":\"Example\",\"automaticallyConsumedSoul\":true,\"gear\":true}]}")]
    [InlineData("{\"schemaVersion\":1,\"items\":[{\"id\":42,\"nameEn\":\"Example\",\"rarity\":99}]}")]
    [InlineData("{\"schemaVersion\":1,\"items\":[{\"id\":42,\"nameEn\":\"Example\",\"exchangeType\":99}]}")]
    [InlineData("{\"schemaVersion\":1,\"items\":[{\"id\":42,\"nameEn\":\"Example\"},{\"id\":42,\"nameEn\":\"Other\"}]}")]
    [InlineData("{\"schemaVersion\":1,\"items\":[],\"sourceBuild\":123}")]
    public void InvalidOrUnexpectedCatalogContentIsRejectedInsteadOfSilentlyLosingExclusions(string json) =>
        Assert.Throws<InvalidDataException>(() => MonsterLootItemCatalog.FromJson(json));
}
