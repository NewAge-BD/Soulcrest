using Soulcrest.App.OverlayEditorKit;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class OverlayEditorMarketAveragePresentationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
    private const string Profile = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Theory]
    [InlineData("de", "12.345", "49.380", "Marktwert netto")]
    [InlineData("en", "12,345", "49,380", "Net market value")]
    public void AverageTotalAndRateShareOneNetEstimateAndSessionDuration(string language, string total, string rate, string label)
    {
        var session = Session();
        var snapshot = Project(session, Average(session), language);
        Assert.Equal(total, snapshot.Metrics["market-kina"].Value);
        Assert.Equal(rate, snapshot.Metrics["market-kina-hour"].Value);
        Assert.Equal(label, snapshot.Metrics["market-kina"].Label);
        Assert.Equal(session.Elapsed, snapshot.SessionElapsed);
        Assert.Null(snapshot.Metrics["market-kina"].HistoricalAge);
        Assert.Null(snapshot.Metrics["market-kina-hour"].HistoricalAge);
    }

    [Theory]
    [InlineData("de", "12.345 + ?", "49.380 + ?")]
    [InlineData("en", "12,345 + ?", "49,380 + ?")]
    public void PartialAverageNeverIncludesLegacyFallbackFieldsOrHistoricalMarkers(string language, string total, string rate)
    {
        var session = Session();
        var market = Average(session) with
        {
            NetKina = null, PricedRows = 1, PricedQuantity = 10,
            KnownEstimateNetKina = 987_654, HistoricalRows = 1, HistoricalQuantity = 5,
            OldestHistoricalSeenAt = Now.AddDays(-7), NewestHistoricalSeenAt = Now.AddDays(-7),
        };
        var snapshot = Project(session, market, language);
        Assert.Equal(total, snapshot.Metrics["market-kina"].Value);
        Assert.Equal(rate, snapshot.Metrics["market-kina-hour"].Value);
        foreach (var kind in new[] { "market-kina", "market-kina-hour" })
        {
            var metric = snapshot.Metrics[kind];
            Assert.Null(metric.HistoricalAge);
            Assert.DoesNotContain("≈", metric.Value);
            Assert.DoesNotContain("~", metric.Value);
            Assert.DoesNotContain("histor", metric.Tooltip!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("2026-11-04", metric.Tooltip!);
            Assert.Equal(OverlayMetricTone.Muted, metric.Tone);
        }
        Assert.Null(market.NetKina);
        Assert.Equal(12_345, market.PartialNetKina);
        Assert.Equal(987_654, market.KnownEstimateNetKina);
    }

    [Theory]
    [InlineData(true, 0L, 2, "0")]
    [InlineData(false, 0L, 1, "0 + ?")]
    [InlineData(false, 100L, 0, "—")]
    [InlineData(false, -1L, 1, "—")]
    [InlineData(false, null, 1, "—")]
    public void MissingAverageCoverageStaysUnknownAndConfirmedZeroStaysZero(bool complete, long? amount, int pricedRows, string expected)
    {
        var session = Session();
        var market = Average(session) with
        {
            NetKina = complete ? amount : null, PartialNetKina = amount, PricedRows = pricedRows,
            HistoricalRows = 1, KnownEstimateNetKina = 123_456, OldestHistoricalSeenAt = Now.AddHours(-1),
        };
        var snapshot = Project(session, market, "en");
        Assert.Equal(expected, snapshot.Metrics["market-kina"].Value);
        Assert.Equal(expected, snapshot.Metrics["market-kina-hour"].Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnAverageCannotInventARateBeforeAnySessionTimeHasElapsed(bool complete)
    {
        var session = Session() with { Elapsed = TimeSpan.Zero };
        var market = Average(session) with { NetKina = complete ? 100 : null, PartialNetKina = 100, PricedRows = complete ? 2 : 1 };
        var snapshot = Project(session, market, "en");
        Assert.Equal(complete ? "100" : "100 + ?", snapshot.Metrics["market-kina"].Value);
        Assert.Equal("—", snapshot.Metrics["market-kina-hour"].Value);
    }

    [Theory]
    [InlineData("de", "24h-Durchschnitt niedrigster Stückpreise", "Durchschnittsfenster", "Preisbeobachtungen: 1.234", "Steuer: 7,5 %")]
    [InlineData("en", "24h average of lowest unit prices", "Average window", "Price observations: 1,234", "Sales tax: 7.5 %")]
    public void BothAverageTooltipsExposeTheirWindowSamplesCoverageAndUnchangedFeeAndEquipmentBasis(
        string language, string basis, string window, string samples, string tax)
    {
        var session = Session();
        var market = Average(session) with { NetKina = null, PricedRows = 1, PricedQuantity = 10, HasEquipmentReference = true };
        var snapshot = Project(session, market, language);
        var tooltip = snapshot.Metrics["market-kina"].Tooltip!;
        Assert.Equal(tooltip, snapshot.Metrics["market-kina-hour"].Tooltip);
        Assert.Contains(basis, tooltip);
        Assert.Contains(window + ": 2026-11-10 12:00:00 UTC – 2026-11-11 12:00:00 UTC", tooltip);
        Assert.Contains(samples, tooltip);
        Assert.DoesNotContain("source time", tooltip, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Quellenzeit", tooltip);
        Assert.Contains("aion2.exchange", tooltip);
        Assert.Contains("Synthetic market", tooltip);
        Assert.Contains("EU", tooltip); Assert.Contains("Elyos", tooltip);
        Assert.Contains(tax, tooltip);
        Assert.Contains(language == "de" ? "Einstellgebühr: 2 %" : "Registration fee: 2 %", tooltip);
        Assert.Contains(language == "de" ? "Mengenabdeckung: 10/15" : "Quantity coverage: 10/15", tooltip);
        Assert.Contains(language == "de" ? "Unbekannte Menge: 5" : "Unknown quantity: 5", tooltip);
        Assert.Contains(language == "de" ? "Ausrüstung: Basispreis-Richtwert" : "Equipment: base-price estimate", tooltip);
        Assert.Contains(language == "de" ? "Die Handelbarkeit einzelner Instanzen ist nicht bestätigt." : "Individual item tradability is not confirmed.", tooltip);
        Assert.DoesNotContain(Profile, tooltip);
        Assert.DoesNotContain(session.Id, tooltip);
    }

    [Theory]
    [InlineData("de", "Durchschnittsfenster: Unbekannt – Unbekannt", "Preisbeobachtungen: Unbekannt")]
    [InlineData("en", "Average window: Unknown – Unknown", "Price observations: Unknown")]
    public void MissingAverageProvenanceDoesNotInventTimestampsOrSampleCounts(string language, string window, string samples)
    {
        var session = Session();
        var market = Average(session) with { AverageWindowStart = null, AverageWindowEnd = null, AverageSamples = -1 };
        var tooltip = Project(session, market, language).Metrics["market-kina"].Tooltip!;
        Assert.Contains(window, tooltip); Assert.Contains(samples, tooltip);
    }

    [Theory]
    [InlineData("session")]
    [InlineData("profile")]
    [InlineData("unavailable")]
    [InlineData("tracking")]
    public void AverageMetadataDoesNotBypassTheExistingLiveAuthority(string boundary)
    {
        var session = Session();
        var market = Average(session) with
        {
            SessionId = boundary == "session" ? "other-session" : session.Id,
            SourceProfileId = boundary == "profile" ? "other-profile" : Profile,
            Available = boundary != "unavailable",
        };
        var snapshot = OverlayEditorPresentation.Project(State(session) with { TrackingAvailable = boundary != "tracking" },
            market, null, Now, TimeZoneInfo.Utc, "en");
        Assert.Equal("—", snapshot.Metrics["market-kina"].Value);
        Assert.Equal("—", snapshot.Metrics["market-kina-hour"].Value);
    }

    [Theory]
    [InlineData("de", "24h-Durchschnitt niedrigster Stückpreise", "Preisbeobachtungen: 72")]
    [InlineData("en", "24h average of lowest unit prices", "Price observations: 72")]
    public void ProductionExamplesUseTheSameAverageProvenanceWithoutAHistoricalAge(string language, string basis, string samples)
    {
        var example = OverlaySnapshot.DemoFor(language);
        foreach (var kind in new[] { "market-kina", "market-kina-hour" })
        {
            var metric = example.Metrics[kind];
            Assert.Contains(basis, metric.Tooltip!); Assert.Contains(samples, metric.Tooltip!);
            Assert.Null(metric.HistoricalAge);
            Assert.DoesNotContain("≈", metric.Value);
        }
    }

    private static LootTrackerSession Session() => new()
    {
        Id = "synthetic-average-session", Character = new(Profile, "Synthetic profile"),
        Kind = LootTrackerSessionKind.Mixed, Status = LootTrackerSessionStatus.Live,
        StartedAt = Now.AddMinutes(-15), Elapsed = TimeSpan.FromMinutes(15),
    };

    private static OverlayEditorMarketSnapshot Average(LootTrackerSession session) => new()
    {
        Available = true, OnlineEnabled = true, Uses24HourAverage = true,
        SessionId = session.Id, SourceProfileId = Profile, ServerName = "Synthetic market",
        MarketProfile = new() { Region = "EU", Faction = "Elyos", ServerId = 7, SalesTaxPercent = 7.5m, RegistrationFeePercent = 2m },
        NetKina = 12_345, PartialNetKina = 12_345, PricedRows = 2, EligibleRows = 2,
        PricedQuantity = 15, EligibleQuantity = 15, AverageSamples = 1_234,
        AverageWindowStart = Now.AddHours(-24), AverageWindowEnd = Now,
        OldestObservedAt = Now.AddMinutes(-3), NewestObservedAt = Now,
    };

    private static UnifiedLootOverlayState State(LootTrackerSession session) => new(session, true, new(null, null, null, 0), null, false, Now);
    private static OverlaySnapshot Project(LootTrackerSession session, OverlayEditorMarketSnapshot market, string language) =>
        OverlayEditorPresentation.Project(State(session), market, null, Now, TimeZoneInfo.Utc, language);
}
