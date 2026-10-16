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
}
