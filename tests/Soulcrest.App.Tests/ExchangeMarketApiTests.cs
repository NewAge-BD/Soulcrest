using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class ExchangeMarketApiTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 20, 40, 0, TimeSpan.Zero);

    [Fact]
    public async Task RollingAverageUsesCanonicalHourlyEndpointAndKeepsAnExactDay()
    {
        using var handler = new Handler((request, _) => Task.FromResult(Json(AverageSeries(request.RequestUri!, Now))));
        using var client = new ExchangeMarketApiClient(handler, new Clock());
        var average = Assert.Single(await client.GetAveragePricesAsync(12304, [610530001], CancellationToken.None));
        Assert.Equal(12304, average.ServerId); Assert.Equal(610530001, average.ItemId);
        Assert.Equal(125.375m, average.UnitPrice); Assert.Equal(3, average.Samples);
        Assert.Equal(Now.AddHours(-24), average.RangeStart); Assert.Equal(Now, average.RangeEnd); Assert.Equal(Now, average.FetchedAt);
        var uri = Assert.Single(handler.Requests);
        Assert.Equal("/api/items/610530001/prices", uri.AbsolutePath);
        var query = AverageQuery(uri);
        Assert.Equal("12304", query["server"]); Assert.Equal("raw", query["bucket"]);
        Assert.Equal("2026-10-31T20:00:00Z", query["from"]);
        Assert.Equal(3, query.Count); Assert.False(query.ContainsKey("range")); Assert.False(query.ContainsKey("to"));
    }

    [Fact]
    public async Task AverageRequestsChooseTheirCanonicalHourAndExactWindowAfterTheSharedWait()
    {
        var clock = new Clock();
        clock.Advance(TimeSpan.FromMinutes(19) + TimeSpan.FromSeconds(58));
        using var handler = new Handler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/prices", StringComparison.Ordinal)
            ? Json(AverageSeries(request.RequestUri, clock.GetUtcNow())) : Json(new { items = new[] { Row(1) } })));
        using var client = new ExchangeMarketApiClient(handler, clock);
        Assert.Single(await client.GetQuotesAsync(12304, [1], CancellationToken.None));
        var waiting = client.GetAveragePricesAsync(12304, [2], CancellationToken.None);
        Assert.Single(handler.Requests); Assert.False(waiting.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(5));
        var average = Assert.Single(await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
        var expectedEnd = new DateTimeOffset(2026, 11, 1, 21, 0, 3, TimeSpan.Zero);
        Assert.Equal(expectedEnd, average.RangeEnd); Assert.Equal(expectedEnd.AddHours(-24), average.RangeStart);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("2026-10-31T21:00:00Z", AverageQuery(handler.Requests[1])["from"]);
    }

    [Fact]
    public async Task AverageInputsAreDeduplicatedSortedAndRequestedIndividually()
    {
        var clock = new Clock();
        using var handler = new Handler((request, _) => Task.FromResult(Json(AverageSeries(request.RequestUri!, clock.GetUtcNow()))));
        using var client = new ExchangeMarketApiClient(handler, clock);
        var pending = client.GetAveragePricesAsync(12304, [2, 1, 2], CancellationToken.None);
        Assert.Single(handler.Requests);
        clock.Advance(TimeSpan.FromSeconds(5));
        var averages = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { 1, 2 }, averages.Select(row => row.ItemId));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(Now, averages[0].RangeEnd); Assert.Equal(Now.AddSeconds(5), averages[1].RangeEnd);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task MissingAverageIsAValidEmptyResultAndNeverUsesOtherPriceFields(int samples)
    {
        using var handler = new Handler((request, _) =>
        {
            var body = AverageSeries(request.RequestUri!, Now);
            var summary = (Dictionary<string, object?>)body["summary"]!;
            summary["samples"] = samples; summary["avg"] = null;
            summary["sold_avg"] = 999m; body["last_price"] = 888m;
            return Task.FromResult(Json(body));
        });
        using var client = new ExchangeMarketApiClient(handler, new Clock());
        Assert.Empty(await client.GetAveragePricesAsync(12304, [1], CancellationToken.None));
    }

    [Theory]
    [InlineData("wrong-item")]
    [InlineData("wrong-bucket")]
    [InlineData("old-window")]
    [InlineData("future-window")]
    [InlineData("wide-window")]
    [InlineData("missing-lower-coverage")]
    [InlineData("expired-provider-window")]
    [InlineData("excess-upper-coverage")]
    [InlineData("auto-bucket")]
    [InlineData("duplicate-timestamp")]
    [InlineData("aggregated-point")]
    [InlineData("point-price-mismatch")]
    [InlineData("point-without-listings")]
    [InlineData("point-outside-envelope")]
    [InlineData("truncated-raw-points")]
    [InlineData("sum-overflow")]
    [InlineData("negative-samples")]
    [InlineData("fractional-samples")]
    [InlineData("zero-samples-positive-average")]
    [InlineData("zero-average")]
    [InlineData("negative-average")]
    [InlineData("string-average")]
    [InlineData("huge-average")]
    [InlineData("missing-summary")]
    [InlineData("missing-points")]
    public async Task InvalidAverageIdentityWindowOrSummaryCannotBecomeAValuation(string invalid)
    {
        using var handler = new Handler((request, _) =>
        {
            var body = AverageSeries(request.RequestUri!, Now);
            var summary = (Dictionary<string, object?>)body["summary"]!;
            var points = (Dictionary<string, object?>[])body["points"]!;
            var start = Now.AddHours(-24); var end = Now;
            switch (invalid)
            {
                case "wrong-item": body["item_key"] = 999; break;
                case "wrong-bucket": body["bucket"] = "1d"; break;
                case "old-window": body["range_start"] = start.AddMinutes(-11); body["range_end"] = end.AddMinutes(-11); break;
                case "future-window": body["range_start"] = start.AddMinutes(3); body["range_end"] = end.AddMinutes(3); break;
                case "wide-window": body["range_start"] = start.AddHours(-1).AddTicks(-1); break;
                case "missing-lower-coverage": body["range_start"] = start.AddMilliseconds(1100); break;
                case "expired-provider-window": body["range_end"] = end - ExchangeMarketCache.FetchTtl - TimeSpan.FromTicks(1); break;
                case "excess-upper-coverage": body["range_end"] = end.AddMinutes(2).AddTicks(1); break;
                case "auto-bucket": body["auto"] = true; break;
                case "duplicate-timestamp": points[1]["ts"] = points[0]["ts"]; break;
                case "aggregated-point": points[0]["samples"] = 2; break;
                case "point-price-mismatch": points[0]["close"] = 999m; break;
                case "point-without-listings": points[0]["listings"] = 0; break;
                case "point-outside-envelope": points[0]["ts"] = start.AddHours(-2); break;
                case "truncated-raw-points": summary["samples"] = 4; break;
                case "sum-overflow": body["points"] = new[] { RawPoint(end.AddHours(-2), decimal.MaxValue), RawPoint(end.AddHours(-1), decimal.MaxValue) }; summary["samples"] = 2; break;
                case "negative-samples": summary["samples"] = -1; break;
                case "fractional-samples": summary["samples"] = 1.5m; break;
                case "zero-samples-positive-average": summary["samples"] = 0; break;
                case "zero-average": summary["avg"] = 0m; break;
                case "negative-average": summary["avg"] = -1m; break;
                case "string-average": summary["avg"] = "125"; break;
                case "huge-average": summary["avg"] = 1e100; break;
                case "missing-summary": body.Remove("summary"); break;
                case "missing-points": body.Remove("points"); break;
            }
            return Task.FromResult(Json(body));
        });
        using var client = new ExchangeMarketApiClient(handler, new Clock());
        await Assert.ThrowsAsync<ExchangeMarketApiException>(() => client.GetAveragePricesAsync(12304, [1], CancellationToken.None));
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(1100)]
    [InlineData(300_000)]
    [InlineData(600_000)]
    public async Task FreshCachedProviderEndKeepsACompleteDayAndFiltersItsLowerBoundary(int millisecondsOld)
    {
        var end = Now.AddMilliseconds(-millisecondsOld);
        var start = end.AddHours(-24);
        using var handler = new Handler((request, _) =>
        {
            var body = AverageSeries(request.RequestUri!, end);
            body["points"] = new[] { RawPoint(start.AddTicks(-1), 100_000), RawPoint(start, 100), RawPoint(end.AddTicks(-1), 150) };
            var summary = (Dictionary<string, object?>)body["summary"]!;
            summary["samples"] = 3; summary["avg"] = 33_416.666666666666m;
            return Task.FromResult(Json(body));
        });
        using var client = new ExchangeMarketApiClient(handler, new Clock());
        var average = Assert.Single(await client.GetAveragePricesAsync(12304, [1], CancellationToken.None));
        Assert.Equal(125m, average.UnitPrice); Assert.Equal(2, average.Samples);
        Assert.Equal(start, average.RangeStart); Assert.Equal(end, average.RangeEnd); Assert.Equal(Now, average.FetchedAt);
        Assert.Equal(TimeSpan.FromHours(24), average.RangeEnd - average.RangeStart);
    }

    [Fact]
    public async Task CachedProviderEndCannotInventMissingLowerCoverageAcrossAnHourBoundary()
    {
        var clock = new Clock();
        clock.Advance(TimeSpan.FromMinutes(20) + TimeSpan.FromSeconds(3));
        var end = clock.GetUtcNow().AddMinutes(-5);
        using var handler = new Handler((request, _) => Task.FromResult(Json(AverageSeries(request.RequestUri!, end))));
        using var client = new ExchangeMarketApiClient(handler, clock);
        await Assert.ThrowsAsync<ExchangeMarketApiException>(() => client.GetAveragePricesAsync(12304, [1], CancellationToken.None));
        Assert.Equal("2026-10-31T21:00:00Z", AverageQuery(Assert.Single(handler.Requests))["from"]);
    }

    [Fact]
    public async Task RoundedProviderEnvelopeIsFilteredToTheExactLowerInclusiveUpperExclusiveDay()
    {
        using var handler = new Handler((request, _) =>
        {
            var body = AverageSeries(request.RequestUri!, Now.AddSeconds(1));
            var start = Now.AddHours(-24); var end = Now;
            body["points"] = new[] { RawPoint(start.AddTicks(-1), 100_000), RawPoint(start, 100),
                RawPoint(end.AddTicks(-1), 150), RawPoint(end, 999), RawPoint(end.AddMilliseconds(500), 888) };
            var summary = (Dictionary<string, object?>)body["summary"]!;
            summary["samples"] = 5; summary["avg"] = 20_427.4m;
            return Task.FromResult(Json(body));
        });
        using var client = new ExchangeMarketApiClient(handler, new Clock());
        var average = Assert.Single(await client.GetAveragePricesAsync(12304, [1], CancellationToken.None));
        Assert.Equal(125m, average.UnitPrice); Assert.Equal(2, average.Samples);
        Assert.Equal(Now.AddHours(-24), average.RangeStart); Assert.Equal(Now, average.RangeEnd);
    }

    [Fact]
    public async Task RawMeansKeepFractionalUnitPricesAndIncludeValidOutliersWithoutFiltering()
    {
        using var handler = new Handler((request, _) =>
        {
            var body = AverageSeries(request.RequestUri!, Now); var end = Now;
            body["points"] = new[] { RawPoint(end.AddHours(-3), 1), RawPoint(end.AddHours(-2), 2), RawPoint(end.AddHours(-1), 10_000) };
            var summary = (Dictionary<string, object?>)body["summary"]!; summary["samples"] = 3; summary["avg"] = 3334.3333333333335;
            return Task.FromResult(Json(body));
        });
        using var client = new ExchangeMarketApiClient(handler, new Clock());
        var average = Assert.Single(await client.GetAveragePricesAsync(12304, [1], CancellationToken.None));
        Assert.Equal(10_003m / 3, average.UnitPrice);
        Assert.Equal(3, average.Samples);
    }

    [Fact]
    public async Task APositiveSummaryWithOnlyOlderEnvelopePointsRemainsUnknownForTheExactDay()
    {
        using var handler = new Handler((request, _) =>
        {
            var body = AverageSeries(request.RequestUri!, Now); var start = Now.AddHours(-24);
            body["points"] = new[] { RawPoint(start.AddMinutes(-30), 100), RawPoint(start.AddMinutes(-20), 125.125m), RawPoint(start.AddMinutes(-10), 151) };
            return Task.FromResult(Json(body));
        });
        using var client = new ExchangeMarketApiClient(handler, new Clock());
        Assert.Empty(await client.GetAveragePricesAsync(12304, [1], CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AverageRedirectsRemainBlockedEvenForTheSameOrigin(bool foreign)
    {
        using var handler = new Handler((request, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.PermanentRedirect);
            response.Headers.Location = foreign ? new Uri("https://example.invalid/prices") : request.RequestUri;
            return Task.FromResult(response);
        });
        using var client = new ExchangeMarketApiClient(handler, new Clock());
        var error = await Assert.ThrowsAsync<ExchangeMarketApiException>(() => client.GetAveragePricesAsync(12304, [1], CancellationToken.None));
        Assert.Equal(HttpStatusCode.PermanentRedirect, error.StatusCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RawAverageResponsesRetainTheSharedBodySizeBound()
    {
        using var handler = new Handler((request, _) =>
        {
            var response = Json(AverageSeries(request.RequestUri!, Now));
            response.Content.Headers.ContentLength = 8 * 1024 * 1024 + 1;
            return Task.FromResult(response);
        });
        using var client = new ExchangeMarketApiClient(handler, new Clock());
        await Assert.ThrowsAsync<ExchangeMarketApiException>(() => client.GetAveragePricesAsync(12304, [1], CancellationToken.None));
    }

    [Fact]
    public async Task RawHistoryPointCountIsBoundedEvenBelowTheSharedByteLimit()
    {
        using var handler = new Handler((request, _) =>
        {
            var body = AverageSeries(request.RequestUri!, Now);
            var end = Now;
            body["points"] = Enumerable.Range(1, 20_001).Select(index => RawPoint(end.AddSeconds(-4 * index), 10m)).ToArray();
            ((Dictionary<string, object?>)body["summary"]!)["samples"] = 20_001;
            return Task.FromResult(Json(body));
        });
        using var client = new ExchangeMarketApiClient(handler, new Clock());
        await Assert.ThrowsAsync<ExchangeMarketApiException>(() => client.GetAveragePricesAsync(12304, [1], CancellationToken.None));
    }

    [Fact]
    public async Task AverageRequestsRetainTheSharedDuplicateJsonGuard()
    {
        using var handler = new Handler((request, _) => Task.FromResult(JsonText(JsonSerializer.Serialize(AverageSeries(request.RequestUri!, Now))
            .Replace("\"summary\":{", "\"summary\":{\"avg\":1,", StringComparison.Ordinal))));
        using var client = new ExchangeMarketApiClient(handler, new Clock());
        await Assert.ThrowsAsync<ExchangeMarketApiException>(() => client.GetAveragePricesAsync(12304, [1], CancellationToken.None));
    }

    [Fact]
    public async Task InvalidOrEmptyAverageInputsNeverSendRequests()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("No request expected."));
        using var client = new ExchangeMarketApiClient(handler, new Clock());
        Assert.Empty(await client.GetAveragePricesAsync(12304, [], CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.GetAveragePricesAsync(0, [1], CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.GetAveragePricesAsync(12304, [0], CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.GetAveragePricesAsync(12304, Enumerable.Range(1, 20_001).ToArray(), CancellationToken.None));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAveragePricesAsync(12304, [1], cancellation.Token));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task HundredIdsUseOneExactAnonymousServerBoundBatchWithoutLosingDecimalPrecision()
    {
        var clock = new Clock();
        var ids = Enumerable.Range(610610001, 100).ToArray();
        using var handler = new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Null(request.Content);
            Assert.Equal("aion2.exchange", request.RequestUri!.Host);
            Assert.Equal("/api/items", request.RequestUri.AbsolutePath);
            Assert.Contains("server=11301", request.RequestUri.Query);
            Assert.Contains("limit=100", request.RequestUri.Query);
            Assert.Contains("keys=" + string.Join(',', ids), request.RequestUri.Query);
            Assert.False(request.Headers.Contains("Authorization"));
            Assert.False(request.Headers.Contains("Cookie"));
            return Task.FromResult(Json(new { items = ids.Select(id => Row(id, .125m)).ToArray() }));
        });
        using var client = new ExchangeMarketApiClient(handler, clock);
        var quotes = await client.GetQuotesAsync(11301, ids, CancellationToken.None);
        Assert.Equal(100, quotes.Length);
        Assert.All(quotes, quote =>
        {
            Assert.Equal(11301, quote.ServerId);
            Assert.Equal(.125m, quote.UnitPrice);
            Assert.Equal(Now.AddMinutes(-32), quote.CapturedAt);
            Assert.Equal(Now, quote.FetchedAt);
        });
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task MoreThanHundredIdsWaitFiveSecondsBeforeSecondBatch()
    {
        var clock = new Clock();
        using var handler = new Handler((request, _) =>
        {
            var ids = request.RequestUri!.Query.Split("keys=")[1].Split('&')[0].Split(',').Select(int.Parse).ToArray();
            return Task.FromResult(Json(new { items = ids.Select(id => Row(id)).ToArray() }));
        });
        using var client = new ExchangeMarketApiClient(handler, clock);
        var task = client.GetQuotesAsync(11301, Enumerable.Range(1, 101).ToArray(), CancellationToken.None);
        Assert.Single(handler.Requests);
        Assert.False(task.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Single(handler.Requests);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(101, (await task).Length);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task MixedUnlistedNullObservationDoesNotDiscardAnotherValidQuote()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Json(new { items = new object[]
        {
            Row(1), new { item_key = 2, price = (decimal?)null, listings = 0, listed = false, captured_at = (string?)null }
        } })));
        using var client = new ExchangeMarketApiClient(handler, new Clock());
        Assert.Equal(1, Assert.Single(await client.GetQuotesAsync(11301, [1, 2], CancellationToken.None)).ItemId);
    }

    [Fact]
    public async Task CurrentAndHistoricalPricesStaySeparateWithinTheSameAnonymousBatch()
    {
        var seen = Now.AddHours(-26);
        using var handler = new Handler((request, _) =>
        {
            Assert.Equal("/api/items", request.RequestUri!.AbsolutePath);
            Assert.Contains("server=11301", request.RequestUri.Query);
            Assert.Contains("keys=1,2,3", request.RequestUri.Query);
            Assert.False(request.Headers.Contains("Authorization"));
            Assert.False(request.Headers.Contains("Cookie"));
            return Task.FromResult(Json(new { items = new object[]
            {
                new { item_key = 1, price = 10m, listings = 2, listed = true, captured_at = Now.AddMinutes(-32).ToString("O"),
                    last_price = .125m, last_seen_at = seen.ToString("O") },
                HistoricalRow(2, 20.75m, seen),
                new { item_key = 3, price = (decimal?)null, listings = 0, listed = false, captured_at = (string?)null },
            } }));
        });
        using var client = new ExchangeMarketApiClient(handler, new Clock());
        var details = await client.GetQuoteDetailsAsync(11301, [3, 2, 1], CancellationToken.None);
        var current = Assert.Single(details.CurrentQuotes);
        Assert.Equal(1, current.ItemId);
        Assert.Equal(10m, current.UnitPrice);
        Assert.Equal(Now.AddMinutes(-32), current.CapturedAt);
        Assert.Equal(new[] { 1, 2 }, details.HistoricalQuotes.Select(quote => quote.ItemId).ToArray());
        Assert.Equal(.125m, details.HistoricalQuotes[0].UnitPrice);
        Assert.Equal(20.75m, details.HistoricalQuotes[1].UnitPrice);
        Assert.All(details.HistoricalQuotes, quote =>
        {
            Assert.Equal(11301, quote.ServerId);
            Assert.Equal(seen, quote.LastSeenAt);
            Assert.Equal(Now, quote.FetchedAt);
        });
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task LegacyClientNeverPromotesAnUnlistedHistoricalPriceToACurrentQuote()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Json(new
            { items = new[] { HistoricalRow(1, 25.5m, Now.AddDays(-3)) } })));
        using var client = new ExchangeMarketApiClient(handler, new Clock());
        IExchangeMarketClient legacy = client;
        Assert.Empty(await legacy.GetQuotesAsync(11301, [1], CancellationToken.None));
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("null", "\"2026-10-31T20:40:00Z\"")]
    [InlineData("0", "\"2026-10-31T20:40:00Z\"")]
    [InlineData("-1", "\"2026-10-31T20:40:00Z\"")]
    [InlineData("\"10\"", "\"2026-10-31T20:40:00Z\"")]
    [InlineData("true", "\"2026-10-31T20:40:00Z\"")]
    [InlineData("{}", "\"2026-10-31T20:40:00Z\"")]
    [InlineData("1e100", "\"2026-10-31T20:40:00Z\"")]
    [InlineData("10", "null")]
    [InlineData("10", "17")]
    [InlineData("10", "{}")]
    [InlineData("10", "\"not a timestamp\"")]
    [InlineData("10", "\"2026-10-31T20:40:00\"")]
    [InlineData("10", "\"1970-01-01T00:00:00Z\"")]
    [InlineData("10", "\"2026-11-01T20:43:00Z\"")]
    public async Task InvalidOptionalHistoryCannotDiscardCurrentQuotesOrOtherItemsHistory(string price, string seen)
    {
        var json = "{\"items\":[{\"item_key\":1,\"price\":10,\"listings\":2,\"listed\":true,"
            + "\"captured_at\":\"2026-11-01T20:08:00Z\",\"last_price\":" + price + ",\"last_seen_at\":" + seen + "},"
            + JsonSerializer.Serialize(HistoricalRow(2, 7.25m, Now.AddDays(-1))) + "]}";
        using var handler = new Handler((_, _) => Task.FromResult(JsonText(json)));
        using var client = new ExchangeMarketApiClient(handler, new Clock());
        var details = await client.GetQuoteDetailsAsync(11301, [1, 2], CancellationToken.None);
        Assert.Equal(10m, Assert.Single(details.CurrentQuotes).UnitPrice);
        var history = Assert.Single(details.HistoricalQuotes);
        Assert.Equal(2, history.ItemId);
        Assert.Equal(7.25m, history.UnitPrice);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task HistoricalObservationKeepsItsOriginalAgeAndNormalizesAnExplicitOffset()
    {
        var seen = Now.AddYears(-3).ToOffset(TimeSpan.FromHours(2));
        using var handler = new Handler((_, _) => Task.FromResult(Json(new { items = new[] { HistoricalRow(1, 10, seen) } })));
        using var client = new ExchangeMarketApiClient(handler, new Clock());
        var details = await client.GetQuoteDetailsAsync(11301, [1], CancellationToken.None);
        Assert.Empty(details.CurrentQuotes);
        var historical = Assert.Single(details.HistoricalQuotes);
        Assert.Equal(seen, historical.LastSeenAt);
        Assert.Equal(TimeSpan.Zero, historical.LastSeenAt.Offset);
        Assert.Equal(Now, historical.FetchedAt);
    }

    [Theory]
    [InlineData(120, true)]
    [InlineData(121, false)]
    public async Task HistoricalClockSkewUsesTheExistingBoundWithoutChangingItsSourceTime(int secondsAhead, bool accepted)
    {
        var seen = Now.AddSeconds(secondsAhead);
        using var handler = new Handler((_, _) => Task.FromResult(Json(new { items = new[] { HistoricalRow(1, 10, seen) } })));
        using var client = new ExchangeMarketApiClient(handler, new Clock());
        var details = await client.GetQuoteDetailsAsync(11301, [1], CancellationToken.None);
        Assert.Empty(details.CurrentQuotes);
        Assert.Equal(accepted ? 1 : 0, details.HistoricalQuotes.Length);
        if (accepted) Assert.Equal(seen, details.HistoricalQuotes[0].LastSeenAt);
    }

    [Fact]
    public async Task DetailedBatchesKeepTheHundredItemAndFiveSecondLimits()
    {
        var clock = new Clock();
        using var handler = new Handler((request, _) =>
        {
            var ids = request.RequestUri!.Query.Split("keys=")[1].Split('&')[0].Split(',').Select(int.Parse).ToArray();
            Assert.InRange(ids.Length, 1, 100);
            return Task.FromResult(Json(new { items = ids.Select(id => HistoricalRow(id, .125m, Now.AddHours(-26))).ToArray() }));
        });
        using var client = new ExchangeMarketApiClient(handler, clock);
        var task = client.GetQuoteDetailsAsync(11301, Enumerable.Range(1, 101).ToArray(), CancellationToken.None);
        Assert.Single(handler.Requests);
        Assert.False(task.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Single(handler.Requests);
        clock.Advance(TimeSpan.FromSeconds(1));
        var details = await task;
        Assert.Empty(details.CurrentQuotes);
        Assert.Equal(101, details.HistoricalQuotes.Length);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task DetailedAndLegacyCallsUseTheSameRateGate()
    {
        var clock = new Clock();
        using var handler = new Handler((_, _) => Task.FromResult(Json(new { items = new[] { Row(1) } })));
        using var client = new ExchangeMarketApiClient(handler, clock);
        Assert.Single((await client.GetQuoteDetailsAsync(11301, [1], CancellationToken.None)).CurrentQuotes);
        IExchangeMarketClient legacy = client;
        var next = legacy.GetQuotesAsync(11301, [1], CancellationToken.None);
        Assert.False(next.IsCompleted);
        Assert.Single(handler.Requests);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Single(await next);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData("{\"items\":[{\"item_key\":2,\"price\":10,\"listings\":1,\"listed\":true,\"captured_at\":\"2026-11-01T20:08:00Z\"}]}")]
    [InlineData("{\"items\":[{\"item_key\":1,\"price\":10,\"price\":20,\"listings\":1,\"listed\":true,\"captured_at\":\"2026-11-01T20:08:00Z\"}]}")]
    [InlineData("{\"items\":[{\"item_key\":1,\"price\":-1,\"listings\":1,\"listed\":true,\"captured_at\":\"2026-11-01T20:08:00Z\"}]}")]
    [InlineData("{\"items\":[{\"item_key\":1,\"price\":1,\"listings\":-1,\"listed\":true,\"captured_at\":\"2026-11-01T20:08:00Z\"}]}")]
    [InlineData("{\"items\":[{\"item_key\":1,\"price\":1,\"listings\":1,\"listed\":true,\"captured_at\":\"2026-11-01T20:43:00Z\"}]}")]
    [InlineData("{\"items\":[{\"item_key\":1,\"price\":1,\"listings\":1,\"listed\":true,\"captured_at\":\"2026-11-01T20:08:00\"}]}")]
    [InlineData("{\"items\":null}")]
    public async Task InvalidContextAmountsTimesAndAmbiguousJsonNeverProduceAQuote(string json)
    {
        using var handler = new Handler((_, _) => Task.FromResult(JsonText(json)));
        using var client = new ExchangeMarketApiClient(handler, new Clock());
        await Assert.ThrowsAsync<ExchangeMarketApiException>(() => client.GetQuotesAsync(11301, [1], CancellationToken.None));
    }

    [Fact]
    public async Task InvalidAndEmptyInputNeverSendsAnHttpRequest()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("No request expected."));
        using var client = new ExchangeMarketApiClient(handler, new Clock());
        Assert.Empty(await client.GetQuotesAsync(11301, [], CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.GetQuotesAsync(0, [1], CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.GetQuotesAsync(11301, [0], CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task NullableServerMetadataIsPreservedAsUnknownAndHasDataRemainsExplicit()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Json(new
        {
            @default = 11301,
            servers = new[] { new { server_id = 11301, game_server_key = 1301, name = "Example", region = "eu", region_name = "Europe", faction = (string?)null, has_data = false } }
        })));
        using var client = new ExchangeMarketApiClient(handler, new Clock());
        var server = Assert.Single(await client.GetServersAsync(CancellationToken.None));
        Assert.Equal("eu", server.Region);
        Assert.Equal("", server.Faction);
        Assert.False(server.HasData);
        Assert.Equal(1301, server.GameServerKey);
    }

    [Fact]
    public async Task ProviderServerIdentifierAndNativeGameServerKeyRemainDistinct()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Json(new
        {
            servers = new[] { new { server_id = 12304, game_server_key = 2304, name = "Synthetic server", region = "EU", region_name = "Europe", faction = "Asmodian", has_data = true } }
        })));
        using var client = new ExchangeMarketApiClient(handler, new Clock());
        var row = Assert.Single(await client.GetServersAsync(CancellationToken.None));
        Assert.Equal(12304, row.ServerId);
        Assert.Equal(2304, row.GameServerKey);
        Assert.NotEqual(row.ServerId, row.GameServerKey);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("\"2304\"")]
    [InlineData("2147483648")]
    public async Task MissingOrInvalidNativeGameServerKeysNeverBecomeAnInferredMapping(string value)
    {
        var row = new Dictionary<string, object?>
        {
            ["server_id"] = 12304, ["name"] = "Synthetic server", ["region"] = "EU", ["region_name"] = "Europe",
            ["faction"] = "Asmodian", ["has_data"] = true,
        };
        if (value != "missing") row["game_server_key"] = JsonSerializer.Deserialize<JsonElement>(value);
        var mapped = new Dictionary<string, object?>
        {
            ["server_id"] = 12305, ["game_server_key"] = 2305, ["name"] = "Synthetic mapped", ["region"] = "EU",
            ["region_name"] = "Europe", ["faction"] = "Elyos", ["has_data"] = true,
        };
        using var handler = new Handler((_, _) => Task.FromResult(Json(new { servers = new[] { row, mapped } })));
        using var client = new ExchangeMarketApiClient(handler, new Clock());
        // One unusable key keeps only that row unmapped; the catalogue and its other mappings stay usable.
        var servers = await client.GetServersAsync(CancellationToken.None);
        Assert.Null(Assert.Single(servers, server => server.ServerId == 12304).GameServerKey);
        Assert.Equal(2305, Assert.Single(servers, server => server.ServerId == 12305).GameServerKey);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ValidationFailureHasItsStatusWithoutRetryOrSecondRequest()
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)));
        using var client = new ExchangeMarketApiClient(handler, new Clock());
        var error = await Assert.ThrowsAsync<ExchangeMarketApiException>(() => client.GetServersAsync(CancellationToken.None));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, error.StatusCode);
        Assert.Null(error.RetryAfter);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task HourLongRetryAfterCannotBeShortenedToTenMinutes()
    {
        var clock = new Clock();
        using var handler = new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1));
            return Task.FromResult(response);
        });
        using var client = new ExchangeMarketApiClient(handler, clock);
        var first = await Assert.ThrowsAsync<ExchangeMarketApiException>(() => client.GetServersAsync(CancellationToken.None));
        Assert.Equal(TimeSpan.FromHours(1), first.RetryAfter);
        clock.Advance(TimeSpan.FromMinutes(10));
        var deferred = await Assert.ThrowsAsync<ExchangeMarketApiException>(() => client.GetServersAsync(CancellationToken.None));
        Assert.Equal(TimeSpan.FromMinutes(50), deferred.RetryAfter);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task TransientFailureWithoutHeaderUsesBoundedBackoffAndDoesNotRetryImmediately()
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var client = new ExchangeMarketApiClient(handler, new Clock());
        var error = await Assert.ThrowsAsync<ExchangeMarketApiException>(() => client.GetServersAsync(CancellationToken.None));
        Assert.Equal(TimeSpan.FromSeconds(30), error.RetryAfter);
        await Assert.ThrowsAsync<ExchangeMarketApiException>(() => client.GetServersAsync(CancellationToken.None));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ForeignRedirectAndNonJsonResponsesAreNotFollowedOrAccepted()
    {
        using var handler = new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://example.invalid/private");
            return Task.FromResult(response);
        });
        using var client = new ExchangeMarketApiClient(handler, new Clock());
        var error = await Assert.ThrowsAsync<ExchangeMarketApiException>(() => client.GetServersAsync(CancellationToken.None));
        Assert.Equal(HttpStatusCode.Redirect, error.StatusCode);
        Assert.Single(handler.Requests);
        using var htmlHandler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("<html>not JSON</html>", Encoding.UTF8, "text/html") }));
        using var htmlClient = new ExchangeMarketApiClient(htmlHandler, new Clock());
        await Assert.ThrowsAsync<ExchangeMarketApiException>(() => htmlClient.GetServersAsync(CancellationToken.None));
    }

    [Fact]
    public async Task OversizedResponseIsRejectedBeforeUnboundedReading()
    {
        using var handler = new Handler((_, _) =>
        {
            var response = Json(new { servers = Array.Empty<object>() });
            response.Content.Headers.ContentLength = 8 * 1024 * 1024 + 1;
            return Task.FromResult(response);
        });
        using var client = new ExchangeMarketApiClient(handler, new Clock());
        await Assert.ThrowsAsync<ExchangeMarketApiException>(() => client.GetServersAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RequestDeadlineAndCallerCancellationHaveSeparateOutcomes()
    {
        var clock = new Clock();
        using var handler = new Handler(async (_, token) =>
        { await Task.Delay(Timeout.InfiniteTimeSpan, token); throw new InvalidOperationException(); });
        using var client = new ExchangeMarketApiClient(handler, clock);
        var timed = client.GetServersAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(10));
        var error = await Assert.ThrowsAsync<ExchangeMarketApiException>(() => timed);
        Assert.Contains("timed out", error.Message);
        using var cancel = new CancellationTokenSource();
        var cancelled = client.GetServersAsync(cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
    }

    [Fact]
    public async Task QueuedAndThrottledCancellationCannotSendAnExtraConcurrentRequest()
    {
        var clock = new Clock();
        var reply = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler((_, _) => reply.Task);
        using var client = new ExchangeMarketApiClient(handler, clock);
        var first = client.GetQuotesAsync(11301, [1], CancellationToken.None);
        using var queuedCancel = new CancellationTokenSource();
        var queued = client.GetServersAsync(queuedCancel.Token);
        Assert.Single(handler.Requests);
        queuedCancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        reply.SetResult(Json(new { items = new[] { Row(1) } }));
        Assert.Single(await first);
        using var throttleCancel = new CancellationTokenSource();
        var throttled = client.GetServersAsync(throttleCancel.Token);
        Assert.False(throttled.IsCompleted);
        throttleCancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => throttled);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task OfflineClientFailureDoesNotDestroyPreviouslyPersistedQuotes()
    {
        using var directory = new CacheDirectory();
        var clock = new Clock();
        var cache = new ExchangeMarketCache(clock, directory.Root);
        var quote = Quote() with { FetchedAt = Now.AddMinutes(-30) };
        cache.PutQuotes([quote]);
        using var handler = new Handler((_, _) => throw new HttpRequestException("offline"));
        using var client = new ExchangeMarketApiClient(handler, clock);
        await Assert.ThrowsAsync<ExchangeMarketApiException>(() => client.GetQuotesAsync(11301, [1], CancellationToken.None));
        Assert.Equal(quote, new ExchangeMarketCache(clock, directory.Root).GetQuote(11301, 1));
        Assert.False(ExchangeMarketCache.IsFresh(quote, Now));
        Assert.True(ExchangeMarketCache.IsUsable(quote, Now));
    }

    [Fact]
    public void CacheRoundTripKeepsExactProviderDataAndOnlyAtomicCompleteFiles()
    {
        using var directory = new CacheDirectory();
        var clock = new Clock();
        var cache = new ExchangeMarketCache(clock, directory.Root);
        cache.SaveServers([new(11301, "Example", "eu", "Europe", "Elyos", true) { GameServerKey = 1301 }]);
        cache.PutQuotes([Quote()]);
        var restored = new ExchangeMarketCache(clock, directory.Root);
        Assert.Equal(Quote(), restored.GetQuote(11301, 1));
        Assert.Null(restored.GetQuote(11302, 1));
        Assert.Equal(1301, Assert.Single(restored.GetServers()).GameServerKey);
        Assert.Equal(Now, restored.ServersFetchedAt);
        Assert.Null(restored.PersistenceError);
        Assert.Single(Directory.GetFiles(directory.Root));
        cache.RemoveQuotes(11301, [1]);
        Assert.Null(new ExchangeMarketCache(clock, directory.Root).GetQuote(11301, 1));
    }

    [Fact]
    public void SourceAgeAndFetchTtlAreIndependentAndFutureSkewIsBounded()
    {
        var quote = Quote();
        Assert.True(ExchangeMarketCache.IsFresh(quote, Now));
        Assert.True(ExchangeMarketCache.IsUsable(quote with { CapturedAt = Now.AddHours(-2) }, Now));
        Assert.False(ExchangeMarketCache.IsUsable(quote with { CapturedAt = Now.AddHours(-2).AddTicks(-1) }, Now));
        Assert.False(ExchangeMarketCache.IsUsable(quote with { CapturedAt = Now.AddMinutes(2).AddTicks(1) }, Now));
        Assert.False(ExchangeMarketCache.IsUsable(quote with { FetchedAt = Now.AddMinutes(3) }, Now));
        Assert.True(ExchangeMarketCache.IsUsable(quote with { FetchedAt = Now.AddMinutes(-11) }, Now));
        Assert.False(ExchangeMarketCache.IsFresh(quote with { FetchedAt = Now.AddMinutes(-11) }, Now));
        Assert.False(ExchangeMarketCache.IsUsable(quote with { Listings = 0 }, Now));
        Assert.False(ExchangeMarketCache.IsUsable(quote with { UnitPrice = 0 }, Now));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"SchemaVersion\":99,\"Provider\":\"aion2.exchange\",\"Servers\":[],\"Quotes\":[]}")]
    [InlineData("{not-json}")]
    public void CorruptAndUnsupportedCachesRemainUnknown(string json)
    {
        using var directory = new CacheDirectory();
        Directory.CreateDirectory(directory.Root);
        File.WriteAllText(Path.Combine(directory.Root, "exchange-market-cache.json"), json);
        var cache = new ExchangeMarketCache(new Clock(), directory.Root);
        Assert.Empty(cache.GetServers());
        Assert.Null(cache.GetQuote(11301, 1));
    }

    [Fact]
    public void NewerObservationsCannotBeReplacedByOlderResponsesAndExpiredCacheCannotRevive()
    {
        var clock = new Clock();
        var cache = new ExchangeMarketCache(clock);
        cache.PutQuotes([Quote()]);
        cache.PutQuotes([Quote() with { UnitPrice = 999, FetchedAt = Now.AddMinutes(-1) }]);
        Assert.Equal(.125m, cache.GetQuote(11301, 1)!.UnitPrice);
        clock.Advance(TimeSpan.FromHours(3));
        Assert.Null(cache.GetQuote(11301, 1));
    }

    [Fact]
    public void UncCacheRootsAreRejectedBeforeAnyNetworkFileAccess()
    {
        Assert.Throws<ArgumentException>(() => new ExchangeMarketCache(root: @"\\example.invalid\share\market"));
        Assert.Throws<ArgumentException>(() => new ExchangeMarketCache(root: "//example.invalid/share/market"));
    }

    [Fact]
    public void UnwritableDiskDoesNotDiscardAUsableInMemoryQuote()
    {
        using var directory = new CacheDirectory();
        Directory.CreateDirectory(directory.Root);
        var occupied = Path.Combine(directory.Root, "occupied");
        File.WriteAllText(occupied, "fixture");
        var cache = new ExchangeMarketCache(new Clock(), occupied);
        cache.PutQuotes([Quote()]);
        Assert.Equal(Quote(), cache.GetQuote(11301, 1));
        Assert.NotNull(cache.PersistenceError);
        Assert.Equal("fixture", File.ReadAllText(occupied));
    }

    private static ExchangeMarketQuote Quote() => new(11301, 1, .125m, 2, Now.AddMinutes(-32), Now);
    private static Dictionary<string, string> AverageQuery(Uri uri) => uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Split('=', 2)).ToDictionary(parts => Uri.UnescapeDataString(parts[0]), parts => Uri.UnescapeDataString(parts[1]), StringComparer.Ordinal);
    private static Dictionary<string, object?> AverageSeries(Uri uri, DateTimeOffset end)
    {
        var query = AverageQuery(uri);
        return new()
        {
            ["item_key"] = int.Parse(uri.Segments[^2].Trim('/'), System.Globalization.CultureInfo.InvariantCulture),
            ["range_start"] = DateTimeOffset.Parse(query["from"], System.Globalization.CultureInfo.InvariantCulture),
            ["range_end"] = end, ["bucket"] = "raw", ["auto"] = false,
            ["points"] = new[] { RawPoint(end.AddHours(-3), 100m), RawPoint(end.AddHours(-2), 125.125m), RawPoint(end.AddHours(-1), 151m) },
            ["summary"] = new Dictionary<string, object?>
            {
                ["samples"] = 3, ["low"] = 100m, ["high"] = 151m, ["avg"] = 125.375m, ["first"] = 100m, ["last"] = 151m,
                ["change_pct"] = 51m, ["listings_min"] = 5, ["listings_max"] = 5, ["listings_avg"] = 5m,
                ["sold_avg"] = 777m,
            },
            ["last_price"] = 888m,
        };
    }
    private static Dictionary<string, object?> RawPoint(DateTimeOffset at, decimal amount) => new()
    {
        ["ts"] = at, ["low"] = amount, ["high"] = amount, ["avg"] = amount, ["close"] = amount,
        ["listings"] = 5, ["listings_min"] = 5, ["listings_max"] = 5, ["samples"] = 1,
    };
    private static object Row(int id, decimal price = 10m) => new
        { item_key = id, price, listings = 2, listed = true, captured_at = Now.AddMinutes(-32).ToString("O") };
    private static object HistoricalRow(int id, decimal lastPrice, DateTimeOffset lastSeenAt) => new
        { item_key = id, price = (decimal?)null, listings = 0, listed = false, captured_at = (string?)null,
            last_price = lastPrice, last_seen_at = lastSeenAt.ToString("O") };
    private static HttpResponseMessage Json(object value) => JsonText(JsonSerializer.Serialize(value));
    private static HttpResponseMessage JsonText(string json) => new(HttpStatusCode.OK)
        { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Requests.Add(request.RequestUri!); return handle(request, cancellationToken); }
    }

    private sealed class CacheDirectory : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "SoulcrestExchangeTests-" + Guid.NewGuid().ToString("N"));
        public void Dispose()
        {
            var full = Path.GetFullPath(Root);
            if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(full).StartsWith("SoulcrestExchangeTests-", StringComparison.Ordinal)) throw new InvalidOperationException();
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        private readonly List<ManualTimer> _timers = [];
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override DateTimeOffset GetUtcNow() => Now.AddTicks(GetTimestamp());
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, () => callback(state), dueTime, period);
            lock (_timers) _timers.Add(timer);
            return timer;
        }
        public void Advance(TimeSpan by)
        {
            Interlocked.Add(ref _ticks, by.Ticks);
            ManualTimer[] timers;
            lock (_timers) timers = _timers.ToArray();
            foreach (var timer in timers) timer.FireIfDue();
        }
        private sealed class ManualTimer(Clock clock, Action callback, TimeSpan due, TimeSpan period) : ITimer
        {
            private long _due = due == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.GetTimestamp() + due.Ticks;
            private bool _disposed;
            public void FireIfDue()
            {
                if (_disposed || clock.GetTimestamp() < _due) return;
                _due = period == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.GetTimestamp() + period.Ticks;
                callback();
            }
            public bool Change(TimeSpan dueTime, TimeSpan newPeriod)
            { period = newPeriod; _due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.GetTimestamp() + dueTime.Ticks; return !_disposed; }
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
