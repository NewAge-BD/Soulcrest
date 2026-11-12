using System.Text.Json;
using Soulcrest.App.Services;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class PetDungeonMapDiagnosticsTests
{
    [Fact]
    public void DungeonMapsAreSelectableWithoutActivatingWorldTrackingOrCapture()
    {
        var resolved = DiagnosticFeatureCatalog.Resolve(["dungeon-pets"]).Select(feature => feature.Id).ToArray();
        Assert.Contains("system", resolved);
        Assert.DoesNotContain("network", resolved);
        Assert.DoesNotContain("capture", resolved);
        Assert.DoesNotContain("map", resolved);
        Assert.Equal(DiagnosticStatus.Unavailable, Collect(null).Status);
    }

    [Fact]
    public void ValidFilesDoNotClaimSuccessfulPresentationBeforeTheDialogReportsReady()
    {
        var progress = new ProgressService();
        var service = new PetDungeonMapService(progress, PetDungeonMapCatalog.All, _ => true);
        Assert.Equal(DiagnosticStatus.Waiting, Collect(service).Status);
        service.ObserveDisplay(DungeonPetMapDisplayState.Loading);
        Assert.Equal(DiagnosticStatus.Waiting, Collect(service).Status);
        service.ObserveDisplay(DungeonPetMapDisplayState.Ready);
        var result = Collect(service);
        Assert.Equal(DiagnosticStatus.Healthy, result.Status);
        Assert.Equal(46, result.Facts["catalogMaps"]);
        Assert.Equal(65, result.Facts["petSpecies"]);
        Assert.Equal(3341, result.Facts["placements"]);
        Assert.Equal(0, result.Facts["missingOrInvalidImages"]);
        Assert.Equal("Ready", result.Facts["displayState"]);
        Assert.IsType<DateTimeOffset>(result.Facts["displayObservedAtUtc"]);
        Assert.Equal(true, result.Facts["imageChecksumsRequired"]);
        Assert.Equal(false, result.Facts["changesTracking"]);
        var json = JsonSerializer.Serialize(result.Facts);
        Assert.DoesNotContain("Krao", json);
        Assert.DoesNotContain("PetNumber", json);
        Assert.DoesNotContain("NpcId", json);
        service.ObserveDisplay(DungeonPetMapDisplayState.Error);
        Assert.Equal(DiagnosticStatus.Error, Collect(service).Status);
        service.ObserveDisplay(DungeonPetMapDisplayState.Closed);
        Assert.Equal(DiagnosticStatus.Waiting, Collect(service).Status);
    }

    [Fact]
    public void MissingImageEvidenceCannotBecomeHealthyAndDiagnosisDoesNotOpenTheDialog()
    {
        var service = new PetDungeonMapService(new ProgressService(), PetDungeonMapCatalog.All, _ => false);
        service.ObserveDisplay(DungeonPetMapDisplayState.Ready);
        var before = service.Diagnostics();
        Assert.Equal(DiagnosticStatus.Warning, Collect(service).Status);
        Assert.Equal(before, service.Diagnostics());
        service.ObserveDisplay((DungeonPetMapDisplayState)999);
        Assert.Equal(before, service.Diagnostics());
    }

    private static DiagnosticResult Collect(PetDungeonMapService? service)
    {
        var contributors = DiagnosticContributors.Create(new DiagnosticServiceRegistry(service));
        return contributors.Single(contributor => contributor.FeatureIds.Contains("dungeon-pets")).Collect("dungeon-pets");
    }
}
