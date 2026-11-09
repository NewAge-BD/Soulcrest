using Soulcrest.App.OverlayEditorKit;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class OverlayEditorHistoricalMarketTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
    private const string Profile = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
    private static readonly ExchangeMarketServer Server = new(7, "Example server", "eu", "Europe", "elyos", true);
    private static ExchangeMarketProfileSettings Model => new()
        { ServerId = 7, Region = "eu", Faction = "elyos", SalesTaxPercent = 10, RegistrationFeePercent = 2 };

    [Theory]
    [InlineData("market-kina")]
    [InlineData("market-kina-hour")]
    public void HiddenLabelsStillReserveSpaceForTheRequiredAgeAtLargeFontScale(string kind)
    {
        var widget = OverlayCatalog.CreateWidget(kind) with { Width = 168, Height = 72, FontScale = 2, ShowLabel = false };
        var ordinary = new OverlaySnapshot { Metrics = new Dictionary<string, OverlayMetric> { [kind] = new("Market", "123,456") } };
        var historical = ordinary with { Metrics = new Dictionary<string, OverlayMetric> { [kind] = new("Market", "≈ 123,456", HistoricalAge: "26 h") } };
        var before = OverlayContentLayout.Create(widget, ordinary);
        var after = OverlayContentLayout.Create(widget, historical);
        Assert.True(after.MinimumHeight > before.MinimumHeight);
        Assert.True(after.Scale < before.Scale);
        Assert.Equal(widget.Height, after.LayoutWidget.Height * after.Scale, 6);
    }

    [Fact]
    public void HistoricalAgeReplacesTheOptionalDetailRatherThanReservingTwoDetailRows()
    {
        var widget = OverlayCatalog.CreateWidget("market-kina") with { FontScale = 2, ShowLabel = true };
        var ageOnly = new OverlaySnapshot { Metrics = new Dictionary<string, OverlayMetric>
            { [widget.Kind] = new("Market", "≈ 123,456", HistoricalAge: "26 h") } };
        var withCoverage = ageOnly with { Metrics = new Dictionary<string, OverlayMetric>
            { [widget.Kind] = ageOnly.Metrics[widget.Kind] with { Detail = "Current / historical coverage" } } };
        Assert.Equal(OverlayContentLayout.Create(widget, ageOnly), OverlayContentLayout.Create(widget, withCoverage));
    }

    [Fact]
    public void HistoricalEstimateAddsOnceToTheCurrentSubtotalWithoutChangingStrictCurrentCoverage()
    {
        var session = Session(Item(1, 2), Item(2, 3));
        var historicalCalls = new List<int>();
        var result = Project(session, id => id == 1 ? Current(1, 50) : null, id =>
        {
            historicalCalls.Add(id);
            return Historical(id, 100);
        });
        Assert.Null(result.NetKina);
        Assert.Equal(88, result.PartialNetKina);
        Assert.Equal(1, result.PricedRows);
        Assert.Equal(2, result.PricedQuantity);
        Assert.Equal(2, result.EligibleRows);
        Assert.Equal(5, result.EligibleQuantity);
        Assert.Equal(1, result.UnknownRows);
        Assert.Equal(3, result.UnknownQuantity);
        Assert.Equal(352, result.KnownEstimateNetKina);
        Assert.Equal(1, result.HistoricalRows);
        Assert.Equal(3, result.HistoricalQuantity);
        Assert.Equal(2, Assert.Single(historicalCalls));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnyUsableCurrentPriceTakesPriorityEvenWhenItsFetchTtlHasExpired(bool stale)
    {
        var quote = Current(1, 50) with
        {
            CapturedAt = Now.AddMinutes(-30), FetchedAt = stale ? Now.AddMinutes(-11) : Now,
        };
        var historyCalls = 0;
        var result = Project(Session(Item(1, 2)), _ => quote, id => { historyCalls++; return Historical(id, 999); });
        Assert.Equal(88, result.NetKina);
        Assert.Equal(88, result.KnownEstimateNetKina);
        Assert.Equal(88, result.PartialNetKina);
        Assert.Equal(1, result.PricedRows);
        Assert.Equal(0, result.HistoricalRows);
        Assert.Equal(0, historyCalls);
        Assert.Equal(stale, result.Stale);
        Assert.Null(result.OldestHistoricalSeenAt);
    }

    [Fact]
    public void ACurrentQuoteBeyondItsSourceAgeCanUseAnExplicitHistoricalReference()
    {
        var result = Project(Session(Item(1, 2)), _ => Current(1, 50) with { CapturedAt = Now.AddHours(-3) },
            id => Historical(id, 100));
        Assert.Null(result.NetKina);
        Assert.Equal(0, result.PartialNetKina);
        Assert.Equal(0, result.PricedRows);
        Assert.Equal(1, result.HistoricalRows);
        Assert.Equal(176, result.KnownEstimateNetKina);
    }

    [Fact]
    public void OldHistoricalPricesHaveNoCurrentTwoHourAgeCap()
    {
        var lastSeen = Now.AddYears(-1);
        var result = Project(Session(Item(1)), _ => null, id => Historical(id, 10) with { LastSeenAt = lastSeen });
        Assert.Equal(8, result.KnownEstimateNetKina);
        Assert.Equal(lastSeen, result.OldestHistoricalSeenAt);
        Assert.Equal(lastSeen, result.NewestHistoricalSeenAt);
        Assert.Null(result.NetKina);
        Assert.Equal(0, result.PricedRows);
    }

    [Theory]
    [InlineData("negative-price")]
    [InlineData("zero-price")]
    [InlineData("wrong-server")]
    [InlineData("wrong-item")]
    [InlineData("invalid-server")]
    [InlineData("invalid-item")]
    [InlineData("future-seen")]
    [InlineData("future-fetch")]
    [InlineData("missing-seen")]
    [InlineData("missing-fetch")]
    public void InvalidHistoricalReferencesNeverCreateKnownMoney(string invalid)
    {
        var history = Historical(1, 10);
        history = invalid switch
        {
            "negative-price" => history with { UnitPrice = -1 },
            "zero-price" => history with { UnitPrice = 0 },
            "wrong-server" => history with { ServerId = 8 },
            "wrong-item" => history with { ItemId = 2 },
            "invalid-server" => history with { ServerId = 0 },
            "invalid-item" => history with { ItemId = 0 },
            "future-seen" => history with { LastSeenAt = Now.AddMinutes(3) },
            "future-fetch" => history with { FetchedAt = Now.AddMinutes(3) },
            "missing-seen" => history with { LastSeenAt = default },
            _ => history with { FetchedAt = default },
        };
        var result = Project(Session(Item(1)), _ => null, _ => history);
        Assert.Null(result.KnownEstimateNetKina);
        Assert.Null(result.NetKina);
        Assert.Equal(0, result.HistoricalRows);
        Assert.Equal(0, result.HistoricalQuantity);
        Assert.Null(result.OldestHistoricalSeenAt);
    }

    [Fact]
    public void UnknownTradabilityAndMismatchedMarketContextCannotUseHistoricalPrices()
    {
        var session = Session(Item(1));
        var unknown = OverlayEditorMarketProjection.Project(session, Model, true, Server, _ => null, Now,
            _ => new(false, false, false), id => Historical(id, 10));
        Assert.Null(unknown.KnownEstimateNetKina);
        Assert.Equal(0, unknown.HistoricalRows);
        var mismatched = OverlayEditorMarketProjection.Project(session, Model with { ServerId = 8 }, true, Server,
            _ => null, Now, _ => new(false, true, false), id => Historical(id, 10));
        Assert.Null(mismatched.KnownEstimateNetKina);
        Assert.Equal(0, mismatched.HistoricalRows);
    }

    [Fact]
    public void QuantityAndFeeFloorApplyOnceToTheAggregatedHistoricalItem()
    {
        var result = Project(Session(Item(1), Item(1)), _ => null, id => Historical(id, 1.1m));
        Assert.Equal(1, result.EligibleRows);
        Assert.Equal(2, result.HistoricalQuantity);
        Assert.Equal(1, result.HistoricalRows);
        Assert.Equal(1, result.KnownEstimateNetKina); // Floor(2 * 1.1 * .88), not 2 * Floor(1.1 * .88).
        Assert.Null(result.NetKina);
    }

    [Fact]
    public void AValidHistoricalPriceCanYieldAnExplicitKnownZeroAfterFees()
    {
        var result = Project(Session(Item(1)), _ => null, id => Historical(id, .1m));
        Assert.Equal(0, result.KnownEstimateNetKina);
        Assert.Equal(1, result.HistoricalRows);
        Assert.Null(result.NetKina);
    }

    [Fact]
    public void HistoricalObservationBoundsExcludeUnrelatedCurrentQuoteTimes()
    {
        var result = Project(Session(Item(1), Item(2), Item(3)), id => id == 1 ? Current(id, 10) : null,
            id => Historical(id, 20) with { LastSeenAt = Now.AddHours(-id) });
        Assert.Equal(Now.AddHours(-3), result.OldestHistoricalSeenAt);
        Assert.Equal(Now.AddHours(-2), result.NewestHistoricalSeenAt);
        Assert.Equal(Now.AddMinutes(-1), result.OldestObservedAt);
        Assert.Equal(2, result.HistoricalRows);
    }

    [Fact]
    public void OverflowCannotPublishAnInventedCombinedEstimate()
    {
        var result = Project(Session(Item(1, long.MaxValue)), _ => null, id => Historical(id, decimal.MaxValue));
        Assert.True(result.StatusIsError);
        Assert.Null(result.KnownEstimateNetKina);
        Assert.Null(result.NetKina);
    }

    [Theory]
    [InlineData("session")]
    [InlineData("profile")]
    [InlineData("unavailable")]
    [InlineData("tracking")]
    [InlineData("completed")]
    public void UnqualifiedOwnerOrSessionNeverExposesAHistoricalEstimate(string invalid)
    {
        var session = Session(Item(1));
        var state = State(invalid == "completed" ? session with { Status = LootTrackerSessionStatus.Completed } : session);
        if (invalid == "tracking") state = state with { TrackingAvailable = false };
        var market = Estimate(session) with
        {
            SessionId = invalid == "session" ? "old-session" : session.Id,
            SourceProfileId = invalid == "profile" ? "ffffffffffffffffffffffffffffffff" : Profile,
            Available = invalid != "unavailable",
        };
        var snapshot = OverlayEditorPresentation.Project(state, market, null, Now, TimeZoneInfo.Utc, "en");
        Assert.Equal("—", snapshot.Metrics["market-kina"].Value);
        Assert.Equal("—", snapshot.Metrics["market-kina-hour"].Value);
    }

    [Theory]
    [InlineData("de", "≈ 123.456 + ?", "≈ 246.912 + ?")]
    [InlineData("en", "≈ 123,456 + ?", "≈ 246,912 + ?")]
    public void HistoricalTotalAndRateKeepApproximationAndUnknownMarkersInTheValue(string language, string total, string rate)
    {
        var session = Session(Item(1));
        var market = Estimate(session);
        var snapshot = OverlayEditorPresentation.Project(State(session), market, null, Now, TimeZoneInfo.Utc, language);
        Assert.Equal(total, snapshot.Metrics["market-kina"].Value);
        Assert.Equal(rate, snapshot.Metrics["market-kina-hour"].Value);
        Assert.Equal("2 h", snapshot.Metrics["market-kina"].HistoricalAge);
        Assert.Equal("2 h", snapshot.Metrics["market-kina-hour"].HistoricalAge);
        Assert.Null(market.NetKina);
        Assert.Equal(100, market.PartialNetKina);
        Assert.Equal(1, market.UnvaluedRows);
        Assert.Equal(1, market.UnvaluedQuantity);
    }

    [Theory]
    [InlineData("de", "≈ 123.456", "≈ 246.912")]
    [InlineData("en", "≈ 123,456", "≈ 246,912")]
    public void FullyEstimatedCoverageHasNoUnknownSuffixButStillHasTheHistoricalMarker(string language, string total, string rate)
    {
        var session = Session(Item(1));
        var market = Estimate(session) with { EligibleRows = 2, EligibleQuantity = 5 };
        var snapshot = OverlayEditorPresentation.Project(State(session), market, null, Now, TimeZoneInfo.Utc, language);
        Assert.Equal(total, snapshot.Metrics["market-kina"].Value);
        Assert.Equal(rate, snapshot.Metrics["market-kina-hour"].Value);
        Assert.Equal("2 h", snapshot.Metrics["market-kina"].HistoricalAge);
        Assert.Equal(0, market.UnvaluedRows);
        Assert.Null(market.NetKina);
    }

    [Theory]
    [InlineData("en", 0, "<1 min")]
    [InlineData("de", 59, "<1 min")]
    [InlineData("en", 60, "1 min")]
    [InlineData("de", 3599, "59 min")]
    [InlineData("en", 3600, "1 h")]
    [InlineData("de", 7199, "1 h")]
    [InlineData("en", 7200, "2 h")]
    [InlineData("de", 4442400, "1.234 h")]
    [InlineData("en", 4442400, "1,234 h")]
    public void AgeUsesTheOldestHistoricalPriceTimeAndFloorsItsDisplayedUnit(string language, int seconds, string age)
    {
        var session = Session(Item(1));
        var market = Estimate(session) with
        {
            OldestHistoricalSeenAt = Now.AddSeconds(-seconds), NewestHistoricalSeenAt = Now,
        };
        var snapshot = OverlayEditorPresentation.Project(State(session), market, null, Now, TimeZoneInfo.Utc, language);
        Assert.Equal(age, snapshot.Metrics["market-kina"].HistoricalAge);
        Assert.Equal(age, snapshot.Metrics["market-kina-hour"].HistoricalAge);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("epoch")]
    [InlineData("future")]
    public void MissingOrInvalidAgeCannotPresentAnUnlabelledHistoricalAmount(string invalid)
    {
        var session = Session(Item(1));
        var market = Estimate(session) with
        {
            OldestHistoricalSeenAt = invalid switch
            {
                "missing" => null, "epoch" => DateTimeOffset.UnixEpoch, _ => Now.AddMinutes(3),
            },
        };
        var snapshot = OverlayEditorPresentation.Project(State(session), market, null, Now, TimeZoneInfo.Utc, "en");
        Assert.Equal("100 + ?", snapshot.Metrics["market-kina"].Value);
        Assert.Equal("200 + ?", snapshot.Metrics["market-kina-hour"].Value);
        Assert.Null(snapshot.Metrics["market-kina"].HistoricalAge);
        Assert.Null(snapshot.Metrics["market-kina-hour"].HistoricalAge);
    }

    [Fact]
    public void HistoricalAgeDoesNotInventARateForAZeroDurationSession()
    {
        var session = Session(Item(1)) with { Elapsed = TimeSpan.Zero };
        var snapshot = OverlayEditorPresentation.Project(State(session), Estimate(session), null, Now, TimeZoneInfo.Utc, "en");
        Assert.Equal("≈ 123,456 + ?", snapshot.Metrics["market-kina"].Value);
        Assert.Equal("—", snapshot.Metrics["market-kina-hour"].Value);
    }

    [Theory]
    [InlineData("de", "aktuell", "unbekannt")]
    [InlineData("en", "current", "unknown")]
    public void TooltipsSeparateCurrentHistoricalAndUnknownCoverageWithoutProfileOrSessionIdentifiers(
        string language, string currentLabel, string unknownLabel)
    {
        var session = Session(Item(1));
        var market = Estimate(session) with
        {
            PricedRows = 3, HistoricalRows = 2, EligibleRows = 6,
            PricedQuantity = 5, HistoricalQuantity = 7, EligibleQuantity = 23,
        };
        var snapshot = OverlayEditorPresentation.Project(State(session), market, null, Now, TimeZoneInfo.Utc, language);
        var tooltip = snapshot.Metrics["market-kina"].Tooltip!;
        Assert.Equal(tooltip, snapshot.Metrics["market-kina-hour"].Tooltip);
        Assert.Contains(currentLabel, tooltip, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("histor", tooltip, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(unknownLabel, tooltip, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2026-11-08 10:00:00 UTC", tooltip);
        Assert.Contains("aion2.exchange", tooltip);
        Assert.DoesNotContain(Profile, tooltip);
        Assert.DoesNotContain(session.Id, tooltip);
    }

    private static LootTrackerLoot Item(int id, long quantity = 1) => new("Item:" + id, "Synthetic item", LootTrackerLootKind.Item, quantity);
    private static LootTrackerSession Session(params LootTrackerLoot[] items) => new()
    {
        Id = "synthetic-historical-market-session", Character = new(Profile, "Example"),
        Status = LootTrackerSessionStatus.Live, StartedAt = Now.AddMinutes(-30), Elapsed = TimeSpan.FromMinutes(30),
        Kind = LootTrackerSessionKind.Mixed, UnifiedVersion = 1, Loot = [.. items],
    };
    private static UnifiedLootOverlayState State(LootTrackerSession session) => new(session, true, new(null, null, null, 0), null, false, Now);
    private static ExchangeMarketQuote Current(int id, decimal price) => new(7, id, price, 2, Now.AddMinutes(-1), Now);
    private static ExchangeMarketHistoricalQuote Historical(int id, decimal price) => new(7, id, price, Now.AddHours(-2), Now);
    private static OverlayEditorMarketSnapshot Project(LootTrackerSession session,
        Func<int, ExchangeMarketQuote?> current, Func<int, ExchangeMarketHistoricalQuote?> history) =>
        OverlayEditorMarketProjection.Project(session, Model, true, Server, current, Now, _ => new(false, true, false), history);
    private static OverlayEditorMarketSnapshot Estimate(LootTrackerSession session) => new()
    {
        Available = true, OnlineEnabled = true, SessionId = session.Id, SourceProfileId = Profile, MarketProfile = Model,
        ServerName = Server.Name, NetKina = null, PartialNetKina = 100, PricedRows = 1, EligibleRows = 3,
        PricedQuantity = 2, EligibleQuantity = 6, KnownEstimateNetKina = 123_456,
        HistoricalRows = 1, HistoricalQuantity = 3,
        OldestHistoricalSeenAt = Now.AddHours(-2), NewestHistoricalSeenAt = Now.AddHours(-2),
    };
}
