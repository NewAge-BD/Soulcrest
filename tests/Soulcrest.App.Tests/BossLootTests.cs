using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class BossLootTests
{
    [Theory]
    [InlineData("de", "apple", "zebra")]
    [InlineData("en", "zebra", "apple")]
    public void EqualRarityUsesLocalizedNamesAndStableItemIds(string language, string first, string second)
    {
        BossLoot[] items = [
            new("zebra", "Alpha", "Zebra", null, "legend"),
            new("apple", "Zulu", "Apfel", null, "legend"),
            new("b", "Same name", "Gleicher Name", null, "rare"),
            new("a", "Same name", "Gleicher Name", null, "rare"),
            new("unknown", "Aardvark", "Aal", null, "unpublished-grade")];
        var expected = new[] { first, second, "a", "b", "unknown" };
        Assert.Equal(expected, BossLoot.OrderByRarity(items, language).Select(item => item.Id));
        Assert.Equal(expected, BossLoot.OrderByRarity(Enumerable.Reverse(items), language).Select(item => item.Id));
    }

    [Fact]
    public void UnknownGradesStayNeutralAndSortAfterAllPublishedGrades()
    {
        var known = new[] { "special", "epic", "unique", "legend", "rare", "common" };
        var items = Enumerable.Reverse(known).Select(rarity => new BossLoot(rarity, "Same name", null, null, rarity))
            .Prepend(new("unknown", "Aardvark", null, null, "unpublished-grade"))
            .Prepend(new("missing", "Zebra", null, null, null));
        Assert.Equal(known.Concat(["unknown", "missing"]), BossLoot.OrderByRarity(items, "en").Select(item => item.Id));
        foreach (var value in new string?[] { null, "", "unpublished-grade" })
        {
            var item = new BossLoot("unknown", "Unknown", null, null, value);
            Assert.Equal("unknown", item.RarityStyle);
            Assert.Equal(-1, item.RarityRank);
        }
    }

}
