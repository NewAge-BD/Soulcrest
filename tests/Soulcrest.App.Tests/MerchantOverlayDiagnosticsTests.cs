using System.Diagnostics;
using System.Text.Json;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class MerchantOverlayDiagnosticsTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly (string Key, object? Value)[] SourceFacts =
        [("serviceAvailable", true), ("hasOfferSnapshot", true), ("offers", 10), ("petRowCount", 3)];

    [Fact]
    public void FreshHiddenRuntimeWaitsAndExportsTheActualLayoutReason()
    {
        var health = Health();
        health.ObserveOffers(10, null, null);
        health.Observe("layout-missing", Stopwatch.GetTimestamp());
        var settings = new SettingsService();
        settings.Current.MerchantOverlayEnabled = true;
        var runtime = new DiagnosticOverlayRuntime();
        runtime.Observe("merchant", visible: false, handleCreated: true, disposed: false);
        var contributors = DiagnosticContributors.Create(new DiagnosticServiceRegistry(settings, runtime, health));
        var result = contributors.Single(contributor => contributor.FeatureIds.Contains("overlay-merchant"))
            .Collect("overlay-merchant");
        Assert.Equal(DiagnosticStatus.Waiting, result.Status);
        Assert.Equal(false, result.Facts["nativeVisible"]);
        Assert.Equal("layout-missing", result.Facts["analysisReason"]);
        Assert.Equal("running", result.Facts["workerState"]);
        Assert.Equal(10, result.Facts["totalOffers"]);
        Assert.Null(result.Facts["petRows"]);
        Assert.Null(result.Facts["badgeCount"]);
        Assert.Equal(1L, result.Facts["layoutMisses"]);
        Assert.Equal(false, result.Facts["placementHidden"]);
    }

    [Theory]
    [InlineData("no-offers")]
    [InlineData("offer-count")]
    [InlineData("not-foreground")]
    [InlineData("capture-missing")]
    [InlineData("not-window-capture")]
    [InlineData("frame-size-mismatch")]
    [InlineData("confirming-layout")]
    [InlineData("layout-missing")]
    [InlineData("no-pet-offers")]
    [InlineData("badge-bounds-invalid")]
    public void CurrentNonRenderingGatesRemainWaitingInsteadOfHealthy(string reason)
    {
        var health = Health();
        health.Observe(reason, Stopwatch.GetTimestamp());
        var result = Project(health, visible: false);
        Assert.Equal(DiagnosticStatus.Waiting, result.Status);
        Assert.Equal(reason, result.Facts["analysisReason"]);
    }

    [Theory]
    [InlineData("layout-ready")]
    [InlineData("capture-held")]
    public void FreshAcceptedFrameAndVisibleShownWindowAreHealthyEvenWithoutANewTransfer(string reason)
    {
        var timestamp = Stopwatch.Frequency * 10;
        var now = At;
        var health = new MerchantOverlayHealth(() => timestamp, () => now);
        health.SetWorkerState("running");
        health.ObserveOffers(10, 3, 3);
        health.Observe("layout-ready", timestamp);
        health.Present("shown");
        timestamp += Stopwatch.Frequency * 600;
        now = now.AddMinutes(10);
        health.Observe(reason, timestamp);
        var result = DiagnosticContributors.MerchantOverlaySnapshot(true, new(true, true, false, now), health.Snapshot(), now, SourceFacts);
        Assert.Equal(DiagnosticStatus.Healthy, result.Status);
        Assert.Equal(At, result.Facts["presentationObservedAtUtc"]);
        Assert.Equal(now, result.Facts["analysisObservedAtUtc"]);
        Assert.Equal(0d, result.Facts["analysisFrameAgeMs"]);
        Assert.Equal(3, result.Facts["badgeCount"]);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void PlacementAndForegroundSuppressionCannotClaimHealthyEvenIfNativeObservationLags(bool placement, bool foreground)
    {
        var health = ShownHealth();
        health.SetPresentationSuppression(placement, foreground);
        var result = Project(health, visible: true);
        Assert.Equal(DiagnosticStatus.Waiting, result.Status);
        Assert.Equal(placement, result.Facts["placementHidden"]);
        Assert.Equal(foreground, result.Facts["foregroundSuppressed"]);
        Assert.Equal("hidden", result.Facts["presentationState"]);
    }

    [Fact]
    public void NeverStartedStoppedAndMissingHostStatesRemainWaitingAndDisabledWins()
    {
        var health = new MerchantOverlayHealth();
        Assert.Equal(DiagnosticStatus.Waiting, Project(health, visible: true).Status);
        health = ShownHealth();
        health.SetWorkerState("stopped");
        Assert.Equal(DiagnosticStatus.Waiting, Project(health, visible: true).Status);
        Assert.Equal(DiagnosticStatus.Waiting, DiagnosticContributors.MerchantOverlaySnapshot(true, null, health.Snapshot(), At).Status);
        health.SetWorkerState("faulted");
        Assert.Equal(DiagnosticStatus.Disabled, Project(health, visible: false, enabled: false).Status);
    }

    [Theory]
    [InlineData("worker")]
    [InlineData("capture")]
    [InlineData("presentation")]
    [InlineData("disposed")]
    public void CurrentRuntimeFailuresAreErrors(string failure)
    {
        var health = ShownHealth();
        if (failure == "worker") health.SetWorkerState("faulted");
        if (failure == "capture") health.Observe("capture-error", 0);
        if (failure == "presentation") health.Present("presentation-error");
        Assert.Equal(DiagnosticStatus.Error, Project(health, visible: false, disposed: failure == "disposed").Status);
    }

    [Fact]
    public void AnOldAnalysisOrFrameAndAnOldHostObservationAreWarnings()
    {
        var timestamp = Stopwatch.Frequency * 10;
        var health = new MerchantOverlayHealth(() => timestamp, () => At);
        health.SetWorkerState("running");
        health.Observe("layout-ready", timestamp);
        health.Present("shown");
        timestamp += Stopwatch.Frequency * 2;
        Assert.Equal(DiagnosticStatus.Warning, Project(health, visible: true).Status);
        health.Observe("layout-ready", timestamp - Stopwatch.Frequency * 2);
        Assert.Equal(DiagnosticStatus.Warning, Project(health, visible: true).Status);
        health.Observe("layout-ready", timestamp);
        var result = DiagnosticContributors.MerchantOverlaySnapshot(true, new(true, true, false, At.AddSeconds(-6)), health.Snapshot(), At);
        Assert.Equal(DiagnosticStatus.Warning, result.Status);
        Assert.Equal(6000d, result.Facts["runtimeAgeMs"]);
    }

    [Fact]
    public void HistoricalFailuresDoNotPermanentlyPoisonARecoveredFreshFrame()
    {
        var health = Health();
        health.Observe("capture-error", 0);
        health.Present("presentation-error");
        health.Observe("layout-ready", Stopwatch.GetTimestamp());
        health.Present("shown");
        var result = Project(health, visible: true);
        Assert.Equal(DiagnosticStatus.Healthy, result.Status);
        Assert.Equal(2L, result.Facts["errors"]);
    }

    [Fact]
    public void MissingCurrentMerchantContextCannotReuseAHealthyStaticOrNativeObservation()
    {
        var health = ShownHealth();
        var result = DiagnosticContributors.MerchantOverlaySnapshot(true, new(true, true, false, At), health.Snapshot(), At);
        Assert.Equal(DiagnosticStatus.Waiting, result.Status);
        Assert.Null(result.Facts["serviceAvailable"]);
        Assert.Null(result.Facts["hasOfferSnapshot"]);
        foreach (var incomplete in new (string Key, object? Value)[][]
        {
            [("serviceAvailable", false), ("hasOfferSnapshot", true), ("offers", 10), ("petRowCount", 3)],
            [("serviceAvailable", true), ("hasOfferSnapshot", false), ("offers", 10), ("petRowCount", 3)],
            [("serviceAvailable", true), ("hasOfferSnapshot", true), ("offers", 9), ("petRowCount", 3)],
            [("serviceAvailable", true), ("hasOfferSnapshot", true), ("offers", 10), ("petRowCount", 0)]
        })
            Assert.Equal(DiagnosticStatus.Waiting, DiagnosticContributors.MerchantOverlaySnapshot(true,
                new(true, true, false, At), health.Snapshot(), At, incomplete).Status);
        health.ObserveOffers(10, 3, 0);
        Assert.Equal(DiagnosticStatus.Waiting, Project(health, visible: true).Status);
        var settings = new SettingsService();
        settings.Current.MerchantOverlayEnabled = true;
        var runtime = new DiagnosticOverlayRuntime();
        runtime.Observe("merchant", visible: true, handleCreated: true, disposed: false);
        var contributors = DiagnosticContributors.Create(new DiagnosticServiceRegistry(settings, runtime, ShownHealth()));
        result = contributors.Single(contributor => contributor.FeatureIds.Contains("overlay-merchant")).Collect("overlay-merchant");
        Assert.Equal(DiagnosticStatus.Waiting, result.Status);
        Assert.Equal(false, result.Facts["serviceAvailable"]);
        Assert.Equal(false, result.Facts["hasOfferSnapshot"]);
        Assert.Null(result.Facts["petRowCount"]);
    }

    [Fact]
    public void SelectionIncludesMerchantSourceAndProjectionContainsOnlyBoundedAnonymousScalars()
    {
        var included = DiagnosticFeatureCatalog.Resolve(["overlay-merchant"]).Select(feature => feature.Id).ToArray();
        Assert.Contains("merchant", included);
        Assert.Contains("network", included);
        Assert.Contains("capture", included);
        Assert.DoesNotContain("pets", included);
        const string privateValue = "Synthetic private identity C:/private/example 192.0.2.8";
        var health = Health();
        for (var i = 0; i < 80; i++) health.Observe(i % 2 == 0 ? "layout-ready" : "layout-missing", 0);
        health.Observe(privateValue, -1);
        health.Present(privateValue);
        health.SetWorkerState(privateValue);
        var result = Project(health, visible: false);
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain(privateValue, json);
        Assert.Equal(24, result.Facts["transitionCount"]);
        Assert.InRange(result.Facts.Count, 1, 128);
        Assert.DoesNotContain(result.Facts.Keys, key => key.Contains("Stream") || key.Contains("PetId") || key.Contains("ServerId") || key.Contains("GoodsId"));
        Assert.All(result.Facts.Values, value => Assert.True(value is null or bool or int or long or double or string or DateTimeOffset));
    }

    private static MerchantOverlayHealth Health()
    {
        var health = new MerchantOverlayHealth();
        health.SetWorkerState("running");
        return health;
    }

    private static MerchantOverlayHealth ShownHealth()
    {
        var health = Health();
        health.Observe("layout-ready", Stopwatch.GetTimestamp());
        health.Present("shown");
        return health;
    }

    private static DiagnosticResult Project(MerchantOverlayHealth health, bool visible, bool enabled = true, bool disposed = false) =>
        DiagnosticContributors.MerchantOverlaySnapshot(enabled, new(visible, true, disposed, At), health.Snapshot(), At, SourceFacts);
}
