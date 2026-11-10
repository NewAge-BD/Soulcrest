using System.Diagnostics;
using Soulcrest.App.Overlay;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class MerchantOverlayHealthTests
{
    [Fact]
    public void MissingReadsAndRealLayoutLossHaveSeparateReasonsAndCounters()
    {
        var health = new MerchantOverlayHealth();
        health.Observe("layout-ready", Stopwatch.GetTimestamp());
        health.Present("shown");
        health.Observe("capture-held", Stopwatch.GetTimestamp());
        health.Observe("capture-held", Stopwatch.GetTimestamp());
        health.Observe("layout-missing", Stopwatch.GetTimestamp());
        health.Present("hidden");
        var report = health.Report();
        Assert.Contains("Beobachtung: layout-missing; Anzeige: hidden", report);
        Assert.Contains("Layoutausfälle: 1; Aufnahmeaussetzer: 2; kurz überbrückt: 2; Übergaben: 1; Fehler: 0", report);
        Assert.Equal(1, report.Split("analysis:capture-held", StringSplitOptions.None).Length - 1);
        Assert.Contains("display:shown", report);
        Assert.Contains("display:hidden", report);
    }

    [Fact]
    public void VisibilityHistoryIsBoundedToTwentyFourTransitions()
    {
        var health = new MerchantOverlayHealth();
        for (var i = 0; i < 60; i++) health.Observe(i % 2 == 0 ? "layout-missing" : "layout-ready", 0);
        var report = health.Report();
        Assert.Equal(24, report.Split("analysis:", StringSplitOptions.None).Length - 1);
        Assert.Contains("Bildalter bei letzter Analyse (ms): unbekannt", report);
        Assert.Contains("Layoutausfälle: 30", report);
    }

    [Fact]
    public void ArbitraryReasonStringsCannotEnterTheDiagnosticPackage()
    {
        var health = new MerchantOverlayHealth();
        health.Observe("synthetic-private-value", -1);
        health.Present("synthetic-private-value");
        var report = health.Report();
        Assert.DoesNotContain("synthetic-private-value", report);
        Assert.Contains("Beobachtung: unknown; Anzeige: unknown", report);
        Assert.Contains("Bildalter bei letzter Analyse (ms): unbekannt", report);
    }

    [Fact]
    public void SnapshotKeepsUnknownCountsDistinctFromKnownZeroAndAllowsOnlyOwnedTokens()
    {
        var health = new MerchantOverlayHealth();
        var initial = health.Snapshot();
        Assert.Equal("not-started", initial.WorkerState);
        Assert.Null(initial.TotalOffers);
        Assert.Null(initial.PetRows);
        Assert.Null(initial.BadgeCount);
        Assert.Null(initial.AnalysisAgeMilliseconds);
        Assert.Null(initial.FrameAgeMilliseconds);
        health.ObserveOffers(10, 0, 0);
        health.Observe("badge-bounds-invalid", 0);
        Assert.Equal("badge-bounds-invalid", health.Snapshot().Observation);
        Assert.Equal(0, health.Snapshot().PetRows);
        health.ObserveOffers(10, null, -1);
        health.SetWorkerState("synthetic-private-value");
        health.Observe("synthetic-private-value", -1);
        health.Present("synthetic-private-value");
        var snapshot = health.Snapshot();
        Assert.Equal(10, snapshot.TotalOffers);
        Assert.Null(snapshot.PetRows);
        Assert.Null(snapshot.BadgeCount);
        Assert.Equal("unknown", snapshot.WorkerState);
        Assert.Equal("unknown", snapshot.Observation);
        Assert.Equal("unknown", snapshot.Presentation);
        Assert.DoesNotContain(snapshot.Transitions, transition => transition.Reason.Contains("synthetic-private-value"));
    }

    [Fact]
    public void MonotonicFrameAndAnalysisAgeAdvanceWithoutAWorkerUpdate()
    {
        var timestamp = Stopwatch.Frequency * 10;
        var at = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
        var health = new MerchantOverlayHealth(() => timestamp, () => at);
        health.Observe("layout-ready", timestamp - Stopwatch.Frequency / 4);
        health.Present("shown");
        var first = health.Snapshot();
        Assert.Equal(0d, first.AnalysisAgeMilliseconds);
        Assert.Equal(250d, first.FrameAgeMilliseconds);
        timestamp += Stopwatch.Frequency * 2;
        // Wall-clock correction cannot keep the old frame fresh or reverse its monotonic age.
        at = at.AddHours(-1);
        var later = health.Snapshot();
        Assert.Equal(2000d, later.AnalysisAgeMilliseconds);
        Assert.Equal(2250d, later.FrameAgeMilliseconds);
        Assert.Equal(first.AnalysisObservedAtUtc, later.AnalysisObservedAtUtc);
        Assert.Equal(first.PresentationObservedAtUtc, later.PresentationObservedAtUtc);
        Assert.Equal(250d, first.FrameAgeMilliseconds);
    }

    [Fact]
    public void SuppressionHidesAFormerlyShownStateWithoutInventingAReshowOnRelease()
    {
        var health = new MerchantOverlayHealth();
        health.Present("shown");
        health.SetPresentationSuppression(true, false);
        var placement = health.Snapshot();
        Assert.True(placement.PlacementHidden);
        Assert.False(placement.ForegroundSuppressed);
        Assert.Equal("hidden", placement.Presentation);
        health.SetPresentationSuppression(false, true);
        Assert.True(health.Snapshot().ForegroundSuppressed);
        health.SetPresentationSuppression(false, false);
        Assert.Equal("hidden", health.Snapshot().Presentation);
        health.SetWorkerState("running");
        health.SetWorkerState("faulted");
        health.SetWorkerState("faulted");
        Assert.Equal("faulted", health.Snapshot().WorkerState);
        Assert.Equal(1, health.Snapshot().Errors);
    }

    [Fact]
    public void SnapshotsOwnImmutableBoundedHistoryAndOfferCountsAreAtomic()
    {
        var health = new MerchantOverlayHealth();
        health.Observe("layout-ready", 0);
        var first = health.Snapshot();
        var history = Assert.IsAssignableFrom<IList<MerchantOverlayHealthTransition>>(first.Transitions);
        Assert.Throws<NotSupportedException>(() => history.Clear());
        Parallel.For(3, 1003, i =>
        {
            health.ObserveOffers(i, i - 1, i - 2);
            health.Observe(i % 2 == 0 ? "layout-ready" : "layout-missing", 0);
            var state = health.Snapshot();
            Assert.Equal(state.TotalOffers - 1, state.PetRows);
            Assert.Equal(state.TotalOffers - 2, state.BadgeCount);
            Assert.InRange(state.Transitions.Count, 1, 24);
        });
        Assert.Single(first.Transitions);
        Assert.Equal("analysis:layout-ready", first.Transitions[0].Reason);
        Assert.InRange(health.Snapshot().Transitions.Count, 1, 24);
    }
}
