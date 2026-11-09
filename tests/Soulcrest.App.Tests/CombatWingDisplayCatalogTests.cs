using Soulcrest.App.Components;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class CombatWingDisplayCatalogTests
{
    private const string Entry = "{\"id\":42,\"nameDe\":\"Beispielflügel\",\"nameEn\":\"Example Wings\","
        + "\"race\":\"Dark\",\"grade\":\"Rare\",\"iconPath\":\"icons/combat/wings/example.png\"}";
    private static string Document(string entries) => "{\"schema\":1,\"metadataBuild\":26425393,\"presentationOnly\":true,"
        + "\"doesNotEstablishOwnershipOrEffectApplicability\":true,\"wings\":[" + entries + "]}";

    [Fact]
    public void EmbeddedMetadataKeepsRacialIdentitiesAndBaseOwnedEffectsSeparateFromEquipment()
    {
        var catalog = CombatWingDisplayCatalog.Default;
        Assert.Equal(26425393, catalog.MetadataBuild);
        Assert.Equal(84, catalog.Count);
        Assert.Equal(42, catalog.Entries.Values.Count(wing => wing.Race == "Dark"));
        Assert.Equal(42, catalog.Entries.Values.Count(wing => wing.Race == "Light"));
        Assert.Equal(46, catalog.Entries.Values.Select(wing => wing.IconPath).Distinct().Count());
        foreach (var id in new[] { 30101100, 40101100 })
        {
            var wing = Assert.IsType<CombatWingDisplayEntry>(catalog.Find(id));
            Assert.Equal("Talisra Wings", wing.NameEn);
            Assert.Equal(4m, wing.EquippedEffects!.Single(effect => effect.StatId == "CoolTimeDecrease").Value);
            Assert.Equal("Percent", wing.EquippedEffects!.Single(effect => effect.StatId == "CoolTimeDecrease").Unit);
            Assert.Equal(200m, wing.OwnedEffects!.Single(effect => effect.StatId == "FPMax").Value);
            Assert.Equal(10m, wing.OwnedEffects!.Single(effect => effect.StatId == "FixingDamage").Value);
            Assert.Equal(60m, wing.EquippedEffects!.Single(effect => effect.StatId == "FixingDamage").Value);
        }
        Assert.Null(catalog.Find(0));
        Assert.Null(catalog.Name(int.MaxValue));
    }

    [Fact]
    public void ExplicitEmptyAndUnknownEffectsStayDistinctAndCollectionsAreImmutable()
    {
        var entry = Entry.Replace("\"id\":42", "\"equippedEffects\":[],\"ownedEffects\":["
            + "{\"statId\":\"HPMax\",\"labelDe\":\"LP\",\"labelEn\":\"HP\",\"value\":50,\"unit\":\"Points\"}],\"id\":42", StringComparison.Ordinal);
        var wing = CombatWingDisplayCatalog.FromJson(Document(entry)).Find(42)!;
        Assert.Empty(wing.EquippedEffects!);
        Assert.Throws<NotSupportedException>(() => Assert.IsAssignableFrom<IList<CombatTitleDisplayEffect>>(wing.OwnedEffects).Clear());
        Assert.Null(CombatWingDisplayCatalog.FromJson(Document(Entry)).Find(42)!.OwnedEffects);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("foreign-icon")]
    [InlineData("traversal-icon")]
    [InlineData("unknown-race")]
    [InlineData("owns")]
    [InlineData("schema")]
    [InlineData("not-presentation")]
    public void MalformedMetadataCannotClaimCharacterOwnership(string variant)
    {
        var json = variant switch
        {
            "duplicate" => Document(Entry + "," + Entry),
            "foreign-icon" => Document(Entry).Replace("icons/combat/wings/example.png", "https://example.invalid/wing.png", StringComparison.Ordinal),
            "traversal-icon" => Document(Entry).Replace("wings/example", "wings/../example", StringComparison.Ordinal),
            "unknown-race" => Document(Entry).Replace("Dark", "Future", StringComparison.Ordinal),
            "owns" => Document(Entry).Replace("\"id\":42", "\"owned\":true,\"id\":42", StringComparison.Ordinal),
            "schema" => Document(Entry).Replace("\"schema\":1", "\"schema\":2", StringComparison.Ordinal),
            "not-presentation" => Document(Entry).Replace("\"presentationOnly\":true", "\"presentationOnly\":false", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(variant))
        };
        Assert.Throws<InvalidDataException>(() => CombatWingDisplayCatalog.FromJson(json));
    }
}
