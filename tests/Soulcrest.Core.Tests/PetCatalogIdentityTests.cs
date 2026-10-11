using Soulcrest.Core.Pets;
using Soulcrest.Core.PetWindow;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class PetCatalogIdentityTests
{
    [Fact]
    public void ExplicitLegacyIdentityHasOneDisplayedPetAndAnAliasLookup()
    {
        var primary = Pet("modified-karnif", "1098");
        var legacy = Pet("kalnif-02", "1098", legacy: true);

        var catalog = new PetCatalog([legacy, primary]);

        Assert.Same(primary, Assert.Single(catalog.Pets));
        Assert.Same(primary, catalog.Find("kalnif-02"));
        Assert.Same(primary, catalog.Find("modified-karnif"));
        Assert.Equal("modified-karnif", catalog.Aliases["kalnif-02"]);
        Assert.Same(primary, PanelPetName.Resolve("Modified Karnif", catalog.Pets));
        Assert.Same(primary, PanelPetName.Resolve("Modifizierter Karnif", catalog.Pets));
    }

    [Theory]
    [InlineData(null, true, "1098", false)]
    [InlineData("1099", true, "1098", false)]
    [InlineData("1098", false, "1098", false)]
    [InlineData("1098", true, "1098", true)]
    public void NamesAndIconsNeverReplaceMissingOrAmbiguousIdentityProof(
        string? firstNumeric, bool firstLegacy, string? secondNumeric, bool secondLegacy)
    {
        var catalog = new PetCatalog([
            Pet("first", firstNumeric, firstLegacy), Pet("second", secondNumeric, secondLegacy)]);

        Assert.Equal(2, catalog.Pets.Count);
        Assert.Empty(catalog.Aliases);
        Assert.Equal("first", catalog.Find("first")?.Id);
        Assert.Equal("second", catalog.Find("second")?.Id);
        Assert.Null(PanelPetName.Resolve("Modified Karnif", catalog.Pets));
    }

    [Fact]
    public void TwoPrimaryCandidatesKeepTheirLegacyCandidateUnresolved()
    {
        var catalog = new PetCatalog([
            Pet("first", "1098"), Pet("second", "1098"), Pet("legacy", "1098", legacy: true)]);

        Assert.Equal(3, catalog.Pets.Count);
        Assert.Empty(catalog.Aliases);
    }

    [Fact]
    public void ProvenAliasesSurviveRebuildingTheCatalogWithOnlyPrimaryRows()
    {
        var primary = Pet("modified-karnif", "1098");
        var original = new PetCatalog([Pet("kalnif-02", "1098", legacy: true), primary]);
        var rebuilt = new PetCatalog(original.Pets, original.Aliases);

        Assert.Same(primary, Assert.Single(rebuilt.Pets));
        Assert.Same(primary, rebuilt.Find("kalnif-02"));
        Assert.Equal(original.Aliases, rebuilt.Aliases);
    }

    [Fact]
    public void ImportedJsonRetainsTheIdentityMetadataUsedToProveTheAlias()
    {
        var path = Path.Combine(Path.GetTempPath(), $"soulcrest-pet-identity-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """
                [
                  { "id":"kalnif-02", "en":"Modified Karnif", "genus":"fera", "numericId":"1098", "legacyDuplicate":true },
                  { "id":"modified-karnif", "en":"Modified Karnif", "genus":"fera", "numericId":"1098", "legacyDuplicate":false }
                ]
                """);

            var catalog = PetCatalog.Load(path);

            Assert.Equal("1098", Assert.Single(catalog.Pets).NumericId);
            Assert.False(Assert.Single(catalog.Pets).LegacyDuplicate);
            Assert.Equal("modified-karnif", catalog.Find("kalnif-02")?.Id);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void APreviouslyProvenAliasAlsoHidesALearnedRowWithoutNumericMetadata()
    {
        var primary = Pet("modified-karnif", "1098");
        var learnedLegacy = new PetDefinition { Id = "kalnif-02", En = "Kalnif 02", Genus = "unknown" };
        var catalog = new PetCatalog([primary, learnedLegacy],
            new Dictionary<string, string> { ["kalnif-02"] = primary.Id });

        Assert.Same(primary, Assert.Single(catalog.Pets));
        Assert.Same(primary, catalog.Find(learnedLegacy.Id));
        Assert.Equal(primary.Id, catalog.Aliases[learnedLegacy.Id]);
    }

    private static PetDefinition Pet(string id, string? numeric, bool legacy = false) => new()
    {
        Id = id, En = "Modified Karnif", De = "Modifizierter Karnif", Genus = "fera",
        Icon = "same-portrait.png", NumericId = numeric, LegacyDuplicate = legacy,
    };
}
