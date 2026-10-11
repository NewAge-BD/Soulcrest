using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class PetSoulIdentityTests
{
    private static PetDefinition Pet(string id, string? numeric = null, bool legacy = false) => new()
    {
        Id = id, En = "Synthetic identity fixture", Genus = "fixture", NumericId = numeric,
        LegacyDuplicate = legacy, Icon = "synthetic-shared-portrait.png",
    };

    [Theory]
    [InlineData(1098, "modified-karnif")]
    [InlineData(1146, "gatekeeper-pinopi")]
    public void SourcedNumericIdentityDoesNotDependOnDisplayNamePortraitOrMapSpawns(int id, string sourcedCatalogId)
    {
        Assert.Equal(sourcedCatalogId, PetIdTable.Find(id)?.CatalogId);
        var pet = Pet("fixture-from-source-" + id, id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var catalog = new PetCatalog([pet]);
        Assert.Equal(id, PetSoulIdentity.Resolve(pet, catalog));
        Assert.Same(pet, Assert.Single(PetSoulIdentity.Verified(catalog)));
        Assert.Empty(pet.Spawns);
        Assert.Empty(pet.Monsters);
    }

    [Fact]
    public void SharedNamesAndPortraitsNeverPromoteAnUnverifiedLearnedPet()
    {
        var source = Assert.IsType<PetIdEntry>(PetIdTable.Find(1098));
        var verified = Pet("fixture-verified", "1098") with { En = source.En };
        var learned = Pet("fixture-learned") with { En = source.En, De = "Synthetic German display name" };
        var catalog = new PetCatalog([learned, verified]);
        Assert.Equal(verified.En, learned.En);
        Assert.Equal(verified.Icon, learned.Icon);
        Assert.Null(PetSoulIdentity.Resolve(learned, catalog));
        Assert.Same(verified, Assert.Single(PetSoulIdentity.Verified(catalog)));
        Assert.Null(learned.NumericId);
        Assert.Empty(catalog.Aliases);
        Assert.Equal(2, catalog.Pets.Count);
    }

    [Theory]
    [InlineData(1098)]
    [InlineData(1146)]
    public void OlderPackagesCanUseOnlyTheExactEmbeddedCatalogIdentity(int id)
    {
        var source = Assert.IsType<PetIdEntry>(PetIdTable.Find(id));
        Assert.NotNull(source.CatalogId);
        var pet = Pet(source.CatalogId);
        var catalog = new PetCatalog([pet]);
        Assert.Null(pet.NumericId);
        Assert.Equal(id, PetSoulIdentity.Resolve(pet, catalog));
        Assert.Same(pet, Assert.Single(PetSoulIdentity.Verified(catalog)));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("1098x")]
    [InlineData("+1098")]
    [InlineData("-1098")]
    [InlineData("1098.0")]
    [InlineData("2147483648")]
    [InlineData("999999")]
    public void PresentInvalidOrUnknownNumericMetadataCannotFallBackToAnOtherwiseValidCatalogId(string numeric)
    {
        var pet = Pet("modified-karnif", numeric);
        var catalog = new PetCatalog([pet]);
        Assert.NotNull(PetIdTable.Find(1098));
        Assert.Null(PetIdTable.Find(999999));
        Assert.Null(PetSoulIdentity.Resolve(pet, catalog));
        Assert.Empty(PetSoulIdentity.Verified(catalog));
    }

    [Theory]
    [InlineData("Modified-Karnif")]
    [InlineData("MODIFIED-KARNIF")]
    [InlineData("modified-karnif ")]
    [InlineData("fixture-portrait")]
    public void SimilarCatalogIdsAndAnExactDisplayNameAreNotIdentityEvidence(string catalogId)
    {
        var pet = Pet(catalogId) with { En = PetIdTable.Find(1098)!.En };
        var catalog = new PetCatalog([pet]);
        Assert.Null(PetSoulIdentity.Resolve(pet, catalog));
        Assert.Empty(PetSoulIdentity.Verified(catalog));
    }

    [Fact]
    public void ProvenKalnifAliasHasOneVerifiedPrimaryInsteadOfTwoDisplayedPets()
    {
        var primary = Pet("modified-karnif", "1098");
        var legacy = Pet("kalnif-02", "1098", legacy: true);
        var catalog = new PetCatalog([legacy, primary]);
        Assert.Same(primary, catalog.Find(legacy.Id));
        Assert.Equal(primary.Id, catalog.Aliases[legacy.Id]);
        Assert.Equal(1098, PetSoulIdentity.Resolve(catalog.Find(legacy.Id)!, catalog));
        Assert.Same(primary, Assert.Single(PetSoulIdentity.Verified(catalog)));
    }

    [Fact]
    public void PreservedProvenAliasCanResolveAnOlderPrimaryWithoutNumericMetadata()
    {
        var primary = Pet("fixture-preserved-primary");
        var catalog = new PetCatalog([primary], new Dictionary<string, string> { ["modified-karnif"] = primary.Id });
        Assert.Same(primary, catalog.Find("modified-karnif"));
        Assert.Equal(1098, PetSoulIdentity.Resolve(primary, catalog));
        Assert.Same(primary, Assert.Single(PetSoulIdentity.Verified(catalog)));
        var rebuilt = new PetCatalog(catalog.Pets, catalog.Aliases);
        Assert.Equal(1098, PetSoulIdentity.Resolve(primary, rebuilt));
        Assert.Same(primary, Assert.Single(PetSoulIdentity.Verified(rebuilt)));
    }

    [Fact]
    public void TwoSourcedSoulIdsPointingToOneAliasTargetRemainAmbiguous()
    {
        var primary = Pet("modified-karnif");
        var catalog = new PetCatalog([primary], new Dictionary<string, string> { ["gatekeeper-pinopi"] = primary.Id });
        Assert.Same(primary, catalog.Find("modified-karnif"));
        Assert.Same(primary, catalog.Find("gatekeeper-pinopi"));
        Assert.Null(PetSoulIdentity.Resolve(primary, catalog));
        Assert.Empty(PetSoulIdentity.Verified(catalog));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TwoPrimaryPetsWithTheSameSoulIdAreBothHiddenRegardlessOfInputOrder(bool reverse)
    {
        var first = Pet("fixture-first", "1098");
        var second = Pet("fixture-second", "1098");
        var independent = Pet("gatekeeper-pinopi", "1146");
        PetDefinition[] input = reverse ? [independent, second, first] : [first, second, independent];
        var catalog = new PetCatalog(input);
        Assert.Equal(1098, PetSoulIdentity.Resolve(first, catalog));
        Assert.Equal(1098, PetSoulIdentity.Resolve(second, catalog));
        Assert.Same(independent, Assert.Single(PetSoulIdentity.Verified(catalog)));
        Assert.Empty(catalog.Aliases);
        Assert.Equal(3, catalog.Pets.Count);
    }

    [Fact]
    public void AmbiguityUsesTheNumericSoulIdEvenWhenMetadataHasDifferentDigitFormatting()
    {
        var first = Pet("fixture-first", "1098");
        var second = Pet("fixture-second", "01098");
        var catalog = new PetCatalog([first, second]);
        Assert.Equal(1098, PetSoulIdentity.Resolve(first, catalog));
        Assert.Equal(1098, PetSoulIdentity.Resolve(second, catalog));
        Assert.Empty(PetSoulIdentity.Verified(catalog));
    }

    [Fact]
    public void NumericMetadataAndOldPackageFallbackCannotSelectAnArbitraryDuplicate()
    {
        var oldPackagePet = Pet("modified-karnif");
        var numericPet = Pet("fixture-new-package", "1098");
        var catalog = new PetCatalog([oldPackagePet, numericPet]);
        Assert.Equal(1098, PetSoulIdentity.Resolve(oldPackagePet, catalog));
        Assert.Equal(1098, PetSoulIdentity.Resolve(numericPet, catalog));
        Assert.Empty(PetSoulIdentity.Verified(catalog));
    }

    [Fact]
    public void EmbeddedNumericIdentityDoesNotRequireTheSourceCatalogIdInTheRuntimeCatalog()
    {
        var source = Assert.IsType<PetIdEntry>(PetIdTable.Find(1098));
        Assert.NotNull(source.CatalogId);
        var pet = Pet("network-only-fixture", "1098");
        var catalog = new PetCatalog([pet]);
        Assert.Null(catalog.Find(source.CatalogId));
        Assert.Equal(source.Id, PetSoulIdentity.Resolve(pet, catalog));
        Assert.Same(pet, Assert.Single(PetSoulIdentity.Verified(catalog)));
    }
}
