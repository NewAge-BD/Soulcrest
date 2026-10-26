using System.Text.Json;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class LifeskillItemNameCatalogTests
{
    [Theory]
    [InlineData("de", "Saphir-Edelstein")]
    [InlineData("en", "Sapphire Gemstone")]
    public void EmbeddedNamesUseTheRequestedLanguage(string language, string expected)
    {
        Assert.Equal(49, LifeskillItemNameCatalog.Default.Count);
        Assert.Equal(expected, LifeskillItemNameCatalog.Default.Name(610660025, language));
    }

    [Fact]
    public void CurrentUiLanguageIsReadForEachLookup()
    {
        var previous = UiText.Language;
        try
        {
            UiText.Language = "de";
            Assert.Equal("Saphir-Edelstein", LifeskillItemNameCatalog.Default.Name(610660025));
            UiText.Language = "en";
            Assert.Equal("Sapphire Gemstone", LifeskillItemNameCatalog.Default.Name(610660025));
        }
        finally { UiText.Language = previous; }
    }

    [Fact]
    public void EachIdKeepsItsOwnLabelAndMissingLanguageUsesTheAvailableName()
    {
        var catalog = LifeskillItemNameCatalog.FromJson("""
            {"schemaVersion":1,"items":[
              {"id":101,"nameDe":"Testmaterial","nameEn":"Test material"},
              {"id":102,"nameDe":"Feines Testmaterial","nameEn":"Fine test material"},
              {"id":103,"nameDe":null,"nameEn":"English label"},
              {"id":104,"nameDe":"Deutscher Name","nameEn":""}]}
            """);
        Assert.Equal("Test material", catalog.Name(101, "en"));
        Assert.Equal("Fine test material", catalog.Name(102, "en"));
        Assert.Equal("English label", catalog.Name(103, "de"));
        Assert.Equal("Deutscher Name", catalog.Name(104, "en"));
        Assert.Equal("Item #105", catalog.Name(105, "de"));
        Assert.Equal("Item #610660025", LifeskillItemNameCatalog.Empty.Name(610660025, "en"));
    }

    [Theory]
    [InlineData("""{"schemaVersion":2,"items":[]}""")]
    [InlineData("""{"schemaVersion":1,"items":[null]}""")]
    [InlineData("""{"schemaVersion":1,"items":[{"id":0,"nameEn":"Label"}]}""")]
    [InlineData("""{"schemaVersion":1,"items":[{"id":1}]}""")]
    [InlineData("""{"schemaVersion":1,"items":[{"id":1,"nameEn":"A"},{"id":1,"nameEn":"B"}]}""")]
    public void UnknownSchemaInvalidRowsAndDuplicateIdsAreRejected(string json)
    {
        Assert.Throws<InvalidDataException>(() => LifeskillItemNameCatalog.FromJson(json));
    }

    [Fact]
    public void BundledAssetContainsOnlyUniqueIdsAndGermanEnglishNames()
    {
        using var stream = typeof(LifeskillItemNameCatalog).Assembly
            .GetManifestResourceStream("Soulcrest.LifeskillItemNames.json");
        Assert.NotNull(stream);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        Assert.Equal(["items", "schemaVersion"], root.EnumerateObject().Select(p => p.Name).Order().ToArray());
        var ids = new HashSet<int>();
        foreach (var row in root.GetProperty("items").EnumerateArray())
        {
            Assert.Equal(["id", "nameDe", "nameEn"], row.EnumerateObject().Select(p => p.Name).Order().ToArray());
            Assert.True(ids.Add(row.GetProperty("id").GetInt32()));
            Assert.False(string.IsNullOrWhiteSpace(row.GetProperty("nameDe").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(row.GetProperty("nameEn").GetString()));
        }
        Assert.Equal(49, ids.Count);
    }
}
