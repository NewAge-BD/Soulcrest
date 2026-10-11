using Soulcrest.Core.Pets;
using Soulcrest.Core.PetWindow;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class PanelPetScanNameTests
{
    [Theory]
    [InlineData("Aultrossts Doppelga...", "1044", "Aultross's Doppelganger")]
    [InlineData("Decomposed Lupyllini t.Z5", "1202", "Decomposed Lupyllini")]
    [InlineData(" decomposed lupyllini t.z5 ", "1202", "Decomposed Lupyllini")]
    public void ReportedBrokenNamesResolveOnlyTheirUniqueSourcedIdentity(string raw, string numeric, string name)
    {
        var primary = Pet("primary", name, numeric);
        var similar = Pet("similar", name + " Scout", "9999");
        var legacy = primary with { Id = "legacy", LegacyDuplicate = true };

        Assert.Same(primary, PanelPetName.Resolve(raw, [similar, legacy, primary]));
        Assert.False(PanelPetName.CanLearn(raw));
    }

    [Theory]
    [InlineData("Aultrossts Doppelga...", "1044", "Aultross's Doppelganger")]
    [InlineData("Decomposed Lupyllini t.Z5", "1202", "Decomposed Lupyllini")]
    public void MissingOrAmbiguousNumericIdentityNeverFallsBackToAnotherPet(string raw, string numeric, string name)
    {
        var primary = Pet("primary", name, numeric);
        Assert.Null(PanelPetName.Resolve(raw, [primary with { NumericId = null }]));
        Assert.Null(PanelPetName.Resolve(raw, [primary with { NumericId = "9999" }]));
        Assert.Null(PanelPetName.Resolve(raw, [primary with { LegacyDuplicate = true }]));
        Assert.Null(PanelPetName.Resolve(raw, [primary, primary with { Id = "second" }]));
    }

    [Theory]
    [InlineData("Aultrossts Doppelgb...")]
    [InlineData("Decomposed Lupyllini t.Z6")]
    [InlineData("Decomposed Lupyllini Commander")]
    public void SimilarUnreportedNamesDoNotUseTheSpecificRepair(string raw)
    {
        Assert.Null(PanelPetName.Resolve(raw, [
            Pet("aultross", "Aultross's Doppelganger", "1044"),
            Pet("lupyllini", "Decomposed Lupyllini", "1202")]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("..")]
    [InlineData("A")]
    [InlineData("AB")]
    [InlineData("Unknown Pet...")]
    [InlineData("Unknown Pet…")]
    [InlineData("Pet Info")]
    [InlineData(" PET  INFO. ")]
    [InlineData("Pet-Info")]
    [InlineData("Gatekeeper")]
    [InlineData(" GATEKEEPER. ")]
    public void InvalidOrIncompletePanelTextCannotCreateANewPet(string raw) =>
        Assert.False(PanelPetName.CanLearn(raw));

    [Theory]
    [InlineData("Gatekeeper Pinopi")]
    [InlineData("Swarm")]
    [InlineData("Tamed Zajf")]
    [InlineData("New Pet")]
    public void CompletePetNamesRemainLearnable(string raw) => Assert.True(PanelPetName.CanLearn(raw));

    [Fact]
    public void ARealCatalogIdentityCanStillResolveWhenItsTextIsNotLearnable()
    {
        var existing = Pet("gatekeeper", "Gatekeeper", "test");
        Assert.False(PanelPetName.CanLearn(existing.En));
        Assert.Same(existing, PanelPetName.Resolve(existing.En, [existing]));
    }

    private static PetDefinition Pet(string id, string name, string? numeric) =>
        new() { Id = id, En = name, NumericId = numeric, Genus = "cogni" };
}
