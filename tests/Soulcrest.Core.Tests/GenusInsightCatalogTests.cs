using Soulcrest.Core.GenusInsight;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class GenusInsightCatalogTests
{
    private const string Stat = "{\"statId\":\"FixingDamage\",\"labelDe\":\"Zusatzangriffskraft\",\"labelEn\":\"Attack Bonus\",\"unit\":\"Points\",\"rawDivisor\":1}";
    private const string Slot = "{\"genus\":\"special\",\"slot\":1,\"options\":[{\"rank\":1,\"statId\":\"FixingDamage\",\"minimum\":2,\"maximum\":4}]}";
    private static string Document(string stats = Stat, string slots = Slot) => "{\"schema\":1,\"metadataBuild\":26425393,"
        + "\"presentationOnly\":true,\"doesNotEstablishNetworkStatBindings\":true,\"stats\":[" + stats + "],\"slots\":[" + slots + "]}";

    [Fact]
    public void EmbeddedCatalogCoversAllFiveGeneraAndNineSlotsWithoutInventingRankOptions()
    {
        var catalog = GenusInsightCatalog.Default;
        Assert.Equal(26425393, catalog.MetadataBuild);
        Assert.Equal(89, catalog.Stats.Count);
        Assert.Equal(45, catalog.Slots.Count);
        Assert.Equal(2062, catalog.Slots.Sum(slot => slot.Options.Count));
        foreach (var genus in new[] { "cogni", "fera", "natura", "varian", "special" })
        {
            Assert.Equal(Enumerable.Range(1, 9), catalog.Slots.Where(slot => slot.Genus == genus).Select(slot => (int)slot.Slot));
            for (var slot = 1; slot <= 9; slot++)
            {
                Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, catalog.Options(genus, slot).Select(option => option.Rank).Distinct());
                Assert.All(catalog.Options(genus, slot), option => Assert.NotNull(catalog.FindStat(option.StatId)));
            }
        }
        Assert.DoesNotContain(catalog.Options("cogni", 1), option => option.Rank == 1 && option.StatId == "FixingDamage");
        Assert.Contains(catalog.Options("cogni", 1), option => option.Rank == 4 && option.StatId == "FixingDamage");
    }

    [Fact]
    public void SpecialAttackBonusHasTheExactSeparateRarityBandsAndCombinedRange()
    {
        var catalog = GenusInsightCatalog.Default;
        var expected = new (decimal Minimum, decimal Maximum)[] { (2, 4), (3, 6), (4, 8), (6, 12), (8, 16) };
        for (byte rank = 1; rank <= 5; rank++)
            Assert.Equal(new GenusInsightRange(expected[rank - 1].Minimum, expected[rank - 1].Maximum),
                catalog.Range("special", 1, "FixingDamage", rank));
        Assert.Equal(new(2m, 16m), catalog.Range("special", 1, "FixingDamage"));
        Assert.Null(catalog.Range("special", 1, "Attack"));
        Assert.Null(catalog.Range("special", 1, "FixingDamage", 0));
        Assert.Null(catalog.Range("special", 1, "FixingDamage", 6));
    }

    [Fact]
    public void NamedDisplayScalingIsIndependentFromAnyWireStatIdentifier()
    {
        var catalog = GenusInsightCatalog.Default;
        Assert.Equal(new GenusInsightRange(0.1m, 0.3m), catalog.Range("special", 4, "AmplifyAllDamage", 1));
        var stat = catalog.FindStat("AmplifyAllDamage")!;
        Assert.Equal("Percent", stat.Unit); Assert.Equal(100m, stat.RawDivisor);
        Assert.Equal("Damage Boost", stat.LabelEn);
        Assert.Equal("Points", catalog.FindStat("FixingDamage")!.Unit);
        Assert.Equal(1m, catalog.FindStat("FixingDamage")!.RawDivisor);
        Assert.Equal("Attack Bonus", catalog.FindStat("FixingDamage")!.LabelEn);
        Assert.Null(catalog.FindStat("19"));
    }

    [Fact]
    public void AllCollectionsAreFrozenAndUnknownKeysStayEmptyOrNull()
    {
        var catalog = GenusInsightCatalog.FromJson(Document());
        Assert.Throws<NotSupportedException>(() => ((IList<GenusInsightOption>)catalog.Options("special", 1)).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<GenusInsightSlotDefinition>)catalog.Slots).Clear());
        Assert.Empty(catalog.Options("unknown", 1)); Assert.Empty(catalog.Options("special", 0)); Assert.Empty(catalog.Options("special", 10));
        Assert.Empty(catalog.Options(null!, 1));
        Assert.Null(catalog.Range("unknown", 1, "FixingDamage")); Assert.Null(catalog.FindStat(null!));
    }

    [Theory]
    [InlineData("duplicate-stat")]
    [InlineData("duplicate-slot")]
    [InlineData("unknown-stat")]
    [InlineData("unknown-genus")]
    [InlineData("bad-slot")]
    [InlineData("bad-rank")]
    [InlineData("inverted-range")]
    [InlineData("wire-id")]
    [InlineData("probability")]
    [InlineData("schema")]
    public void UnqualifiedMetadataCannotEnterThePresentationCatalog(string variant)
    {
        var json = variant switch
        {
            "duplicate-stat" => Document(stats: Stat + "," + Stat),
            "duplicate-slot" => Document(slots: Slot + "," + Slot),
            "unknown-stat" => Document(slots: Slot.Replace("FixingDamage", "Unknown", StringComparison.Ordinal)),
            "unknown-genus" => Document(slots: Slot.Replace("special", "unknown", StringComparison.Ordinal)),
            "bad-slot" => Document(slots: Slot.Replace("\"slot\":1", "\"slot\":0", StringComparison.Ordinal)),
            "bad-rank" => Document(slots: Slot.Replace("\"rank\":1", "\"rank\":6", StringComparison.Ordinal)),
            "inverted-range" => Document(slots: Slot.Replace("\"minimum\":2", "\"minimum\":5", StringComparison.Ordinal)),
            "wire-id" => Document().Replace("\"rawDivisor\":1", "\"nativeId\":19,\"rawDivisor\":1", StringComparison.Ordinal),
            "probability" => Document().Replace("\"rank\":1", "\"probability\":0.5,\"rank\":1", StringComparison.Ordinal),
            "schema" => Document().Replace("\"schema\":1", "\"schema\":2", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(variant))
        };
        Assert.Throws<InvalidDataException>(() => GenusInsightCatalog.FromJson(json));
    }
}
