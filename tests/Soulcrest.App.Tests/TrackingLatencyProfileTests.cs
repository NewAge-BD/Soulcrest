using System.Diagnostics;
using Soulcrest.App.Capture;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class TrackingLatencyProfileTests
{
    private static long At(double milliseconds) => (long)Math.Round(milliseconds * Stopwatch.Frequency / 1000);
    private static void Ms(double expected, LatencySummary actual) => Assert.Equal(expected, actual.MeanMs, 3);

    [Fact]
    public void PresentedStagesAreSeparatedFromAllCompositionsAndCaptureComponents()
    {
        var profile = new TrackingLatencyProfile();
        profile.AddCapture(default); // failed, GDI or absent WGC data must not turn into a zero sample
        profile.AddCapture(new CaptureTiming(true, 1, 2, 3, 4, 12));
        profile.AddFlowAge(18);
        profile.AddComposition(30); // a frame that was superseded before presentation
        profile.AddComposition(10);
        profile.AddPresentation(new(At(1000), At(1020), At(1030), At(1040), At(1042), At(1045)));
        var snapshot = profile.Drain();
        Assert.Equal(1, snapshot.CaptureTotal.Count);
        Ms(1, snapshot.CaptureGate); Ms(2, snapshot.CaptureCopy); Ms(3, snapshot.CaptureMap);
        Ms(4, snapshot.CaptureConversion); Ms(12, snapshot.CaptureTotal); Ms(18, snapshot.FlowAge);
        Assert.Equal(2, snapshot.ComposeAll.Count); Ms(20, snapshot.ComposeAll);
        Assert.Equal(1, snapshot.ComposeShown.Count); Ms(10, snapshot.ComposeShown);
        Ms(20, snapshot.AgeAtCompose); Ms(10, snapshot.UiWait); Ms(2, snapshot.UiWork);
        Ms(3, snapshot.Present); Ms(45, snapshot.OverlayAge);
        Assert.Equal(0, snapshot.PresentationGap.Count);
    }

    [Fact]
    public void ActivePresentationGapSurvivesLogDrainButIntentionalHideResetsItsBaseline()
    {
        var profile = new TrackingLatencyProfile();
        profile.AddPresentation(new(0, 0, 0, 0, 0, At(1000)));
        profile.Drain();
        profile.AddPresentation(new(0, 0, 0, 0, 0, At(1080)));
        Ms(80, profile.Drain().PresentationGap);
        profile.ResetPresentation();
        profile.AddPresentation(new(0, 0, 0, 0, 0, At(5000)));
        Assert.Equal(0, profile.Drain().PresentationGap.Count);
        profile.AddPresentation(new(0, 0, 0, 0, 0, At(5016)));
        Ms(16, profile.Drain().PresentationGap);
    }

    [Fact]
    public void WorldMissesCountFailedChecksEvenWhenTheNextCheckRecoversBeforeLogging()
    {
        var profile = new TrackingLatencyProfile();
        profile.AddWorldDetection(false);
        profile.AddWorldDetection(true);
        profile.AddFlow(true, true);
        profile.AddFlow(false, true);
        profile.AddFlow(true, false);
        profile.AddCaptureUnavailable();
        profile.AddCaptureRejected();
        var snapshot = profile.Drain();
        Assert.Equal(2, snapshot.WorldChecks); Assert.Equal(1, snapshot.WorldMisses);
        Assert.Equal(3, snapshot.FlowSteps); Assert.Equal(2, snapshot.FlowNulls);
        Assert.Equal(2, snapshot.WorldFlowSteps); Assert.Equal(1, snapshot.WorldFlowNulls);
        Assert.Equal(1, snapshot.CaptureNulls); Assert.Equal(1, snapshot.CaptureRejected);
        var next = profile.Drain();
        Assert.Equal(0, next.WorldChecks); Assert.Equal(0, next.WorldMisses);
        Assert.Equal(0, next.FlowSteps); Assert.Equal(0, next.FlowNulls);
        Assert.Equal(0, next.CaptureNulls); Assert.Equal(0, next.CaptureRejected);
    }

    [Fact]
    public void MissingOrReversedTimestampsCannotCreateFalseStageSamples()
    {
        var profile = new TrackingLatencyProfile();
        profile.AddPresentation(default);
        profile.AddPresentation(new(At(1500), At(1400), At(1300), At(1200), At(1100), At(1000)));
        var snapshot = profile.Drain();
        Assert.Equal(0, snapshot.OverlayAge.Count); Assert.Equal(0, snapshot.AgeAtCompose.Count);
        Assert.Equal(0, snapshot.ComposeShown.Count); Assert.Equal(0, snapshot.UiWait.Count);
        Assert.Equal(0, snapshot.UiWork.Count); Assert.Equal(0, snapshot.Present.Count);
    }

    [Fact]
    public void DiagnosticTextExposesBoundedQuantilePopulationAndShownFramePopulation()
    {
        var profile = new TrackingLatencyProfile();
        for (var i = 0; i < 4100; i++) profile.AddComposition(i);
        var snapshot = profile.Drain();
        Assert.Equal(4100, snapshot.ComposeAll.Count);
        Assert.Equal(4096, snapshot.ComposeAll.Retained);
        var text = snapshot.ToLogText();
        Assert.Contains("profil_ms[mean/p50/p95/max;n/ret]", text);
        Assert.Contains(";4100/4096", text);
        Assert.Contains("compose_shown=0.0/0.0/0.0/0.0;0/0", text);
    }

    [Fact]
    public void TrackingCostSerializesConcurrentUpdatesAndDrainsTheWholeWindow()
    {
        var cost = new TrackingCost();
        Parallel.For(0, 1000, _ =>
        {
            cost.AddWorldDetection(false);
            cost.AddFlow(TimeSpan.FromMilliseconds(1), true, true);
        });
        var first = cost.TakeSummary();
        Assert.Contains("worldcheck_miss=1000/1000", first);
        Assert.Contains("worldflow_null=1000/1000", first);
        var next = cost.TakeSummary();
        Assert.Contains("worldcheck_miss=0/0", next);
        Assert.Contains("worldflow_null=0/0", next);
    }

    [Fact]
    public void TrackingCostBufferSwapsKeepActiveGapContinuityAndRespectExplicitHides()
    {
        var cost = new TrackingCost();
        cost.AddOverlayPresentation(new(0, 0, 0, 0, 0, At(1000)));
        cost.TakeSummary();
        cost.AddOverlayPresentation(new(0, 0, 0, 0, 0, At(1080)));
        Assert.Contains("present_gap=80.0/80.0/80.0/80.0;1/1", cost.TakeSummary());
        cost.AddOverlayPresentation(new(0, 0, 0, 0, 0, At(1100)));
        Assert.Contains("present_gap=20.0/20.0/20.0/20.0;1/1", cost.TakeSummary());
        cost.ResetOverlayPresentation();
        cost.AddOverlayPresentation(new(0, 0, 0, 0, 0, At(5000)));
        Assert.Contains("present_gap=0.0/0.0/0.0/0.0;0/0", cost.TakeSummary());
    }

    private static CaptureTiming Source(long session, long frame, long arrived, long published, long drops, double age) =>
        new(true, 0, 0, 0, 0, 0, age, frame, arrived, published, drops, session);

    [Fact]
    public void SourceObservationsSeparateRepeatsRegressionsAndNewSessionsWithoutCountingEarlierHistory()
    {
        var profile = new TrackingLatencyProfile();
        profile.AddCapture(Source(1, 10, 100, 90, 10, 20));
        profile.AddCapture(Source(1, 10, 105, 95, 10, 30));
        profile.AddCapture(Source(1, 11, 108, 97, 11, 10));
        profile.AddCapture(Source(1, 9, 110, 99, 11, 40));
        profile.AddCapture(Source(2, 1, 2, 1, 0, 5));
        profile.AddCapture(Source(2, 2, 3, 2, 0, 6));
        var snapshot = profile.Drain();
        Assert.Equal(6, snapshot.SourceSamples); Assert.Equal(4, snapshot.SourceNew);
        Assert.Equal(1, snapshot.SourceRepeats); Assert.Equal(1, snapshot.SourceRegressions);
        Assert.Equal(11, snapshot.ArrivedDelta); Assert.Equal(10, snapshot.PublishedDelta);
        Assert.Equal(1, snapshot.FrameGateDropDelta); Ms(18.5, snapshot.SourceAge);
    }

    [Fact]
    public void MissingSourceIdentityDoesNotCreateFalseZeroAgeOrArrivalSamples()
    {
        var profile = new TrackingLatencyProfile();
        profile.AddCapture(new(true, 1, 2, 3, 4, 12));
        var snapshot = profile.Drain();
        Assert.Equal(1, snapshot.CaptureTotal.Count);
        Assert.Equal(0, snapshot.SourceAge.Count); Assert.Equal(0, snapshot.SourceSamples);
        Assert.Equal(0, snapshot.ArrivedDelta);
    }

    [Fact]
    public void TrackingCostBufferSwapsCarrySourceIdentityAndCounterBaselines()
    {
        var cost = new TrackingCost();
        cost.AddCapture(Source(7, 4, 10, 10, 0, 10));
        cost.TakeSummary();
        cost.AddCapture(Source(7, 4, 12, 12, 0, 15));
        var next = cost.TakeSummary();
        Assert.Contains("source_samples=1 source_new=0 source_repeat=1 source_backwards=0", next);
        Assert.Contains("wgc_arrived=2 wgc_published=2 wgc_gate_drop=0", next);
        cost.AddCapture(Source(8, 1, 1, 1, 0, 0));
        var restarted = cost.TakeSummary();
        Assert.Contains("source_samples=1 source_new=1 source_repeat=0", restarted);
        Assert.Contains("wgc_arrived=0 wgc_published=0 wgc_gate_drop=0", restarted);
    }
}
