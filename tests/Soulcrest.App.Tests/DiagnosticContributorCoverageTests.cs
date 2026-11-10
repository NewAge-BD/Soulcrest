using System.Text.Json;
using Soulcrest.App.Capture;
using Soulcrest.App.Components;
using Soulcrest.App.Network;
using Soulcrest.App.OverlayEditorKit;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class DiagnosticContributorCoverageTests
{
    [Fact]
    public void ProductRegistriesHaveExplicitSelectableDiagnosticCoverage()
    {
        var ids = DiagnosticFeatureCatalog.All.Select(feature => feature.Id).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(DiagnosticFeatureCatalog.All.Count, ids.Count);
        Assert.Equal(7, DiagnosticFeatureCatalog.All.Select(feature => feature.Group).Distinct().Count());
        Assert.All(CombatWorkspaceCatalog.Sections, section => Assert.Contains("combat-" + section.Id, ids));
        Assert.All(OverlayRegistry.All, overlay => Assert.Contains("overlay-" + overlay.Id, ids));
        Assert.All(OverlayCatalog.Widgets, widget => Assert.Contains("widget-" + widget.Kind, ids));
        Assert.Contains("widget-" + OverlayLockWidget.Kind, ids);
        Assert.Contains("overlay-hotkeys", ids);
        Assert.All(DiagnosticFeatureCatalog.All, feature => Assert.All(feature.Dependencies, dependency => Assert.Contains(dependency, ids)));
    }

    [Fact]
    public void SelectionIncludesTransitiveDependenciesWithoutUnrelatedFeatures()
    {
        var resolved = DiagnosticFeatureCatalog.Resolve(["widget-genus-insight", "widget-genus-insight"]);
        var ids = resolved.Select(feature => feature.Id).ToArray();
        Assert.Contains("combat-genus-insight", ids);
        Assert.Contains("overlay-editor", ids);
        Assert.Contains("network", ids);
        Assert.Contains("system", ids);
        Assert.DoesNotContain("loot-market", ids);
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.Empty(DiagnosticFeatureCatalog.Resolve([]));
        Assert.Throws<ArgumentException>(() => DiagnosticFeatureCatalog.Resolve(["unknown-private-feature"]));
    }

    [Fact]
    public void EveryFeatureHasExactlyOneContributorAndRegistrationResolvesNothing()
    {
        var provider = new ExistingOnlyProvider();
        var contributors = DiagnosticContributors.Create(provider);
        Assert.Equal(0, provider.Reads);
        Assert.Equal(7, contributors.Count);
        Assert.All(DiagnosticFeatureCatalog.All, feature =>
            Assert.Single(contributors, contributor => contributor.FeatureIds.Contains(feature.Id)));
        Assert.Throws<ArgumentException>(() => contributors[0].Collect("unknown"));
    }

    [Fact]
    public void AbsentSourcesStayUnavailableAndDoNotBecomeHealthyZeroValues()
    {
        var contributors = DiagnosticContributors.Create(new ExistingOnlyProvider());
        foreach (var feature in DiagnosticFeatureCatalog.All.Where(feature => feature.Id != "system"))
        {
            var result = contributors.Single(contributor => contributor.FeatureIds.Contains(feature.Id)).Collect(feature.Id);
            Assert.Equal(DiagnosticStatus.Unavailable, result.Status);
            Assert.False(Assert.IsType<bool>(result.Facts["serviceAvailable"]));
            Assert.DoesNotContain("packets", result.Facts.Keys);
        }
    }

    [Fact]
    public void DiagnosisDoesNotStartSourcesChangeSettingsOrExportArbitraryServiceStrings()
    {
        const string privateText = "Synthetic private identity C:/private/example 192.0.2.8";
        var settings = new SettingsService();
        settings.Current.UiLanguage = privateText;
        settings.Current.NameLanguage = privateText;
        settings.Current.DetectedGameLanguage = privateText;
        settings.Current.LootTrackingEnabled = false;
        var progress = new ProgressService();
        using var capture = new GameCaptureService();
        using var network = new NetworkLootService(progress);
        using var map = new MapTrackingService(settings, progress, capture);
        typeof(MapTrackingService).GetProperty(nameof(MapTrackingService.Status))!.SetValue(map, privateText);
        typeof(NetworkLootService).GetProperty(nameof(NetworkLootService.Status))!.SetValue(network, privateText);
        var before = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        var contributors = DiagnosticContributors.Create(new ExistingOnlyProvider(settings, progress, capture, network, map));
        var results = DiagnosticFeatureCatalog.All.Select(feature => contributors.Single(contributor =>
            contributor.FeatureIds.Contains(feature.Id)).Collect(feature.Id)).ToArray();
        Assert.DoesNotContain(privateText, JsonSerializer.Serialize(results));
        Assert.False(network.Running);
        Assert.False(map.Running);
        Assert.Equal(0, capture.Diagnostics.Grabs);
        Assert.False(settings.Current.LootTrackingEnabled);
        Assert.Equal(before, File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null);
        Assert.Equal(DiagnosticStatus.Disabled, results.Single(result => result.FeatureId == "network").Status);
        Assert.All(results.SelectMany(result => result.Facts.Values), value => Assert.True(value is null or bool or byte or int or long or double or decimal or string or DateTime or DateTimeOffset,
            "Diagnostic facts must be explicitly projected primitive values."));
    }

    [Fact]
    public void EnabledNetworkWithoutReceptionWaitsAndDisabledPreferenceRemainsDistinct()
    {
        var settings = new SettingsService();
        settings.Current.LootTrackingEnabled = true;
        var progress = new ProgressService();
        using var network = new NetworkLootService(progress);
        var contributors = DiagnosticContributors.Create(new ExistingOnlyProvider(settings, network));
        var contributor = contributors.Single(item => item.FeatureIds.Contains("network"));
        Assert.Equal(DiagnosticStatus.Waiting, contributor.Collect("network").Status);
        settings.Current.LootTrackingEnabled = false;
        Assert.Equal(DiagnosticStatus.Disabled, contributor.Collect("network").Status);
        Assert.False(network.Running);
    }

    [Fact]
    public void MerchantSourceProjectsPetRowCountWithoutStartingReception()
    {
        var settings = new SettingsService();
        settings.Current.LootTrackingEnabled = true;
        settings.Current.MerchantOverlayEnabled = true;
        var progress = new ProgressService();
        using var network = new NetworkLootService(progress);
        using var merchant = new MerchantService(network, progress);
        var contributors = DiagnosticContributors.Create(new ExistingOnlyProvider(settings, network, merchant));
        var result = contributors.Single(contributor => contributor.FeatureIds.Contains("merchant")).Collect("merchant");
        Assert.Equal(DiagnosticStatus.Waiting, result.Status);
        Assert.Equal(false, result.Facts["hasOfferSnapshot"]);
        Assert.Null(result.Facts["offerCount"]);
        Assert.Equal(0, result.Facts["petRowCount"]);
        Assert.False(network.Running);
        settings.Current.LootTrackingEnabled = false;
        Assert.Equal(DiagnosticStatus.Disabled, contributors.Single(contributor => contributor.FeatureIds.Contains("merchant"))
            .Collect("merchant").Status);
    }

    [Fact]
    public void OverlayConfigurationDoesNotClaimRenderingUntilTheHostHasObservedIt()
    {
        var settings = new SettingsService();
        settings.Current.OverlayEnabled = true;
        var runtime = new DiagnosticOverlayRuntime();
        var contributors = DiagnosticContributors.Create(new ExistingOnlyProvider(settings, runtime));
        var contributor = contributors.Single(item => item.FeatureIds.Contains("overlay-loot"));
        var unknown = contributor.Collect("overlay-loot");
        Assert.Equal(DiagnosticStatus.Waiting, unknown.Status);
        Assert.Null(unknown.Facts["nativeVisible"]);
        runtime.Observe("loot", visible: false, handleCreated: true, disposed: false);
        var observed = contributor.Collect("overlay-loot");
        Assert.Equal(DiagnosticStatus.Healthy, observed.Status);
        Assert.Equal(false, observed.Facts["nativeVisible"]);
        runtime.Observe("loot", visible: false, handleCreated: true, disposed: true);
        Assert.Equal(DiagnosticStatus.Error, contributor.Collect("overlay-loot").Status);
        settings.Current.OverlayEnabled = false;
        Assert.Equal(DiagnosticStatus.Disabled, contributor.Collect("overlay-loot").Status);
    }

    private sealed class ExistingOnlyProvider(params object[] instances) : IServiceProvider
    {
        public int Reads { get; private set; }
        public object? GetService(Type serviceType)
        {
            Reads++;
            return instances.FirstOrDefault(serviceType.IsInstanceOfType);
        }
    }
}
