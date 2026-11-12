using Soulcrest.App.OverlayEditorKit;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class OverlayEditorMarketFallbackTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    private const string Profile = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly ExchangeMarketProfileSettings Model = new() { ServerId = 7, Region = "eu", Faction = "elyos" };
    private static readonly ExchangeMarketServer Server = new(7, "Synthetic market", "eu", "Europe", "elyos", true);
    private static LootTrackerSession Session(params (int Id, int Quantity)[] rows) => new()
    {
        Id = "synthetic-current-fallback-session", Character = new(Profile, null),
        Status = LootTrackerSessionStatus.Live, StartedAt = Now.AddMinutes(-30), Elapsed = TimeSpan.FromMinutes(30),
        Loot = [.. rows.Select(row => new LootTrackerLoot("Item:" + row.Id, "Synthetic item", LootTrackerLootKind.Item, row.Quantity))],
    };
    private static ExchangeMarketQuote Quote(int id = 1) => new(7, id, 10, 2, Now, Now);
    private static ExchangeMarketAveragePrice Average(int id = 1) => new(7, id, 20, 7, Now.AddHours(-24).AddMinutes(-5), Now.AddMinutes(-5), Now);
    private static OverlayEditorMarketSnapshot Project(LootTrackerSession session, Func<int, ExchangeMarketQuote?> quote,
        Func<int, ExchangeMarketAveragePrice?> average) => OverlayEditorMarketProjection.Project(session, Model, true, Server,
            quote, average, Now, id => new(false, true, id == 2));

    [Fact]
    public void CurrentOfferWinsWithoutReadingAverageAndKeepsOnlyCurrentProvenance()
    {
        var result = Project(Session((1, 2)), _ => Quote(), _ => throw new InvalidOperationException("No average read expected."));
        Assert.Equal(17, result.NetKina);
        Assert.Equal(1, result.CurrentRows); Assert.Equal(0, result.AverageRows);
        Assert.True(result.AverageFallbackEnabled); Assert.False(result.Uses24HourAverage);
        Assert.Null(result.AverageWindowStart); Assert.Null(result.AverageWindowEnd); Assert.Equal(0, result.AverageSamples);
        Assert.Equal(Now, result.OldestObservedAt); Assert.False(result.Stale);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-server")]
    [InlineData("wrong-item")]
    [InlineData("old-source")]
    [InlineData("future-fetch")]
    [InlineData("zero")]
    [InlineData("no-listings")]
    public void AnAbsentOrUnusableCurrentPriceUsesOnlyAQualifiedAverage(string invalid)
    {
        var quote = invalid switch
        {
            "missing" => null,
            "wrong-server" => Quote() with { ServerId = 8 },
            "wrong-item" => Quote() with { ItemId = 9 },
            "old-source" => Quote() with { CapturedAt = Now - ExchangeMarketCache.MaximumAge - TimeSpan.FromTicks(1) },
            "future-fetch" => Quote() with { FetchedAt = Now + ExchangeMarketCache.FutureSkew + TimeSpan.FromTicks(1) },
            "zero" => Quote() with { UnitPrice = 0 },
            _ => Quote() with { Listings = 0 },
        };
        var result = Project(Session((1, 2)), _ => quote, _ => Average());
        Assert.Equal(35, result.NetKina);
        Assert.Equal(0, result.CurrentRows); Assert.Equal(1, result.AverageRows);
        Assert.True(result.Uses24HourAverage); Assert.Equal(7, result.AverageSamples);
        Assert.Equal(TimeSpan.FromHours(24), result.AverageWindowEnd - result.AverageWindowStart);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-server")]
    [InlineData("wrong-item")]
    [InlineData("expired")]
    public void MissingCurrentPriceCannotUseAnUnqualifiedAverage(string invalid)
    {
        var average = invalid switch
        {
            "missing" => null,
            "wrong-server" => Average() with { ServerId = 8 },
            "wrong-item" => Average() with { ItemId = 9 },
            _ => Average() with { RangeStart = Now.AddHours(-27), RangeEnd = Now.AddHours(-3) },
        };
        var result = Project(Session((1, 2)), _ => null, _ => average);
        Assert.Null(result.NetKina); Assert.Null(result.KnownEstimateNetKina);
        Assert.Equal(1, result.UnknownRows); Assert.Equal(0, result.PricedRows); Assert.Equal(0, result.AverageRows);
    }

    [Theory]
    [InlineData("de", "Aktuelle Preiszeilen: 1", "24h-Ersatzwerte: 1")]
    [InlineData("en", "Current price rows: 1", "24h fallback rows: 1")]
    public void MixedPricesShareFeesSessionTimeAndClearlyIdentifyOnlyTheAverageFallback(string language, string current, string average)
    {
        var session = Session((1, 2), (2, 3), (3, 5));
        var result = Project(session, id => id == 1 ? Quote() : null, id => id == 2 ? Average(2) : null);
        Assert.Null(result.NetKina); Assert.Equal(69, result.PartialNetKina); Assert.Equal(69, result.KnownEstimateNetKina);
        Assert.Equal(2, result.PricedRows); Assert.Equal(1, result.CurrentRows); Assert.Equal(1, result.AverageRows);
        Assert.Equal(1, result.UnknownRows); Assert.Equal(5, result.PricedQuantity); Assert.Equal(5, result.UnknownQuantity);
        Assert.Equal(52, result.EquipmentReferenceNetKina); Assert.Equal(7, result.AverageSamples);
        Assert.Equal(Now.AddMinutes(-5), result.AverageWindowEnd);
        var view = OverlayEditorPresentation.Project(new(session, true, new(null, null, null, 0), null, false, Now),
            result, null, Now, TimeZoneInfo.Utc, language);
        Assert.Equal("69 + ?", view.Metrics["market-kina"].Value);
        Assert.Equal("138 + ?", view.Metrics["market-kina-hour"].Value);
        var tooltip = view.Metrics["market-kina"].Tooltip!;
        Assert.Contains(current, tooltip); Assert.Contains(average, tooltip);
        Assert.Contains("2026-11-10 11:55:00 UTC", tooltip);
        Assert.Equal(tooltip, view.Metrics["market-kina-hour"].Tooltip);
        Assert.Null(view.Metrics["market-kina"].HistoricalAge);
    }

    [Fact]
    public void CurrentOnlyModeCannotReviveLegacyHistoricalFields()
    {
        var session = Session((1, 2), (2, 1));
        var result = Project(session, id => id == 1 ? Quote() : null, _ => null) with
        {
            HistoricalRows = 1, HistoricalQuantity = 1, KnownEstimateNetKina = 99_999,
            OldestHistoricalSeenAt = Now.AddHours(-9),
        };
        var view = OverlayEditorPresentation.Project(new(session, true, new(null, null, null, 0), null, false, Now),
            result, null, Now, TimeZoneInfo.Utc, "en");
        Assert.False(result.Uses24HourAverage);
        Assert.Equal("17 + ?", view.Metrics["market-kina"].Value);
        Assert.DoesNotContain("99,999", view.Metrics["market-kina"].Tooltip!);
        Assert.DoesNotContain("Historical", view.Metrics["market-kina"].Tooltip!);
        Assert.Contains("24h fallback rows: 0", view.Metrics["market-kina"].Tooltip!);
        Assert.Null(view.Metrics["market-kina"].HistoricalAge);
    }
}
