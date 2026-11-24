using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class ExchangeMarketLastPriceTests
{
    private const int Server = 12304;
    private static readonly DateTimeOffset Start = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SuccessfulPricesSurviveExpiryPruningAndRestartsWithOriginalTimes()
    {
        using var directory = new CacheDirectory();
        var clock = new Clock();
        var client = new Client { Quotes = [Quote()], Averages = [Average()] };
        var cache = new ExchangeMarketCache(clock, directory.Root);
        await cache.GetQuotesAsync(client, Server, [1], CancellationToken.None);
        await cache.GetAveragePricesAsync(client, Server, [1], CancellationToken.None);
        clock.Advance(TimeSpan.FromDays(30));
        cache = new ExchangeMarketCache(clock, directory.Root);
        cache.PutQuotes([]); // Force age-based live-cache pruning and another persisted snapshot.
        cache.PutAveragePrices([]);
        cache = new ExchangeMarketCache(clock, directory.Root);
        Assert.Equal(Quote(), cache.GetLastQuote(Server, 1));
        Assert.Equal(Average(), cache.GetLastAveragePrice(Server, 1));
        Assert.Null(cache.GetQuote(Server, 1));
        Assert.Null(cache.GetAveragePrice(Server, 1));
        Assert.False(cache.IsQuoteLookupFresh(Server, 1, clock.GetUtcNow()));
        Assert.False(cache.IsAveragePriceLookupFresh(Server, 1, clock.GetUtcNow()));
        Assert.Null(cache.GetLastQuote(Server + 1, 1));
        Assert.Null(cache.GetLastAveragePrice(Server, 2));
        Assert.Equal(new ExchangeMarketPriceArchiveSummary(true, 1, 1, Start, Start), cache.LastPriceArchive);
        using var json = JsonDocument.Parse(File.ReadAllBytes(directory.File));
        Assert.Single(json.RootElement.GetProperty("LastQuotes").EnumerateArray());
        Assert.Single(json.RootElement.GetProperty("LastAveragePrices").EnumerateArray());
        Assert.Empty(json.RootElement.GetProperty("Quotes").EnumerateArray());
        Assert.Empty(json.RootElement.GetProperty("AveragePrices").EnumerateArray());
        Assert.DoesNotContain("Character", json.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Profile", json.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EmptyRepliesAndHttpFailuresPreserveLastPricesWithoutServingThemAsCurrent()
    {
        using var directory = new CacheDirectory();
        var clock = new Clock();
        var cache = new ExchangeMarketCache(clock, directory.Root);
        cache.PutQuotes([Quote()]);
        cache.PutAveragePrices([Average()]);
        clock.Advance(TimeSpan.FromMinutes(11));
        var client = new Client();
        Assert.Empty(await cache.GetQuotesAsync(client, Server, [1], CancellationToken.None));
        Assert.Empty(await cache.GetAveragePricesAsync(client, Server, [1], CancellationToken.None));
        Assert.Null(cache.GetQuote(Server, 1));
        Assert.Null(cache.GetAveragePrice(Server, 1));
        clock.Advance(TimeSpan.FromMinutes(11));
        client.Fail = true;
        var failure = await Assert.ThrowsAsync<ExchangeMarketApiException>(() => cache.GetQuotesAsync(client, Server, [1], CancellationToken.None));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failure.StatusCode);
        await Assert.ThrowsAsync<ExchangeMarketApiException>(() => cache.GetAveragePricesAsync(client, Server, [1], CancellationToken.None));
        Assert.Equal(3, client.Requests); // Shared backoff still blocks the second failed request.
        cache = new ExchangeMarketCache(clock, directory.Root);
        Assert.Equal(Quote(), cache.GetLastQuote(Server, 1));
        Assert.Equal(Average(), cache.GetLastAveragePrice(Server, 1));
        Assert.Null(cache.GetQuote(Server, 1));
        Assert.Null(cache.GetAveragePrice(Server, 1));
    }

    [Fact]
    public void OlderPricesCannotOverwriteLatestObservationAndMarketsRemainSeparate()
    {
        var clock = new Clock();
        var cache = new ExchangeMarketCache(clock);
        cache.PutQuotes([Quote(), Quote() with { ServerId = Server + 1, UnitPrice = 222 }]);
        cache.PutAveragePrices([Average(), Average() with { ItemId = 2, UnitPrice = 333 }]);
        clock.Advance(TimeSpan.FromMinutes(5));
        cache.PutQuotes([Quote() with { UnitPrice = 999, CapturedAt = Start.AddMinutes(-1), FetchedAt = clock.GetUtcNow() }]);
        cache.PutAveragePrices([Average() with { UnitPrice = 999, RangeStart = Start.AddHours(-24).AddMinutes(-1),
            RangeEnd = Start.AddMinutes(-1), FetchedAt = clock.GetUtcNow() }]);
        Assert.Equal(Quote(), cache.GetLastQuote(Server, 1));
        Assert.Equal(Average(), cache.GetLastAveragePrice(Server, 1));
        Assert.Equal(222, cache.GetLastQuote(Server + 1, 1)!.UnitPrice);
        Assert.Equal(333, cache.GetLastAveragePrice(Server, 2)!.UnitPrice);
        var newer = Quote() with { UnitPrice = 123, CapturedAt = clock.GetUtcNow(), FetchedAt = clock.GetUtcNow() };
        cache.PutQuotes([newer]);
        Assert.Equal(newer, cache.GetLastQuote(Server, 1));
    }

    [Fact]
    public void LegacyExpiredPricesMigrateOnNextSaveWithoutWritingDuringLoad()
    {
        using var directory = new CacheDirectory();
        File.WriteAllText(directory.File, JsonSerializer.Serialize(new
        {
            SchemaVersion = 1, Provider = "aion2.exchange", Servers = Array.Empty<ExchangeMarketServer>(),
            Quotes = new[] { Quote() }, AveragePrices = new[] { Average() },
        }));
        var before = File.ReadAllBytes(directory.File);
        var clock = new Clock();
        clock.Advance(TimeSpan.FromDays(365));
        var cache = new ExchangeMarketCache(clock, directory.Root);
        Assert.Equal(before, File.ReadAllBytes(directory.File));
        Assert.Equal(Quote(), cache.GetLastQuote(Server, 1));
        Assert.Equal(Average(), cache.GetLastAveragePrice(Server, 1));
        Assert.Null(cache.GetQuote(Server, 1));
        Assert.Null(cache.GetAveragePrice(Server, 1));
        cache.SaveServers([]);
        cache = new ExchangeMarketCache(clock, directory.Root);
        Assert.Equal(Quote(), cache.GetLastQuote(Server, 1));
        Assert.Equal(Average(), cache.GetLastAveragePrice(Server, 1));
    }

    [Theory]
    [InlineData("shape")]
    [InlineData("duplicate")]
    [InlineData("null")]
    [InlineData("future")]
    [InlineData("negative")]
    [InlineData("accumulated-future-skew")]
    [InlineData("oversized")]
    public void CorruptOptionalArchivesCannotDiscardValidLegacyEvidence(string corruption)
    {
        using var directory = new CacheDirectory();
        var json = JsonSerializer.SerializeToNode(new
        {
            SchemaVersion = 1, Provider = "aion2.exchange", Servers = Array.Empty<ExchangeMarketServer>(),
            Quotes = new[] { Quote() }, AveragePrices = new[] { Average() },
        })!;
        JsonNode? quotes = JsonSerializer.SerializeToNode(new[] { Quote() with { ItemId = 2 } });
        JsonNode? averages = JsonSerializer.SerializeToNode(new[] { Average() with { ItemId = 2 } });
        switch (corruption)
        {
            case "shape": quotes = JsonValue.Create("invalid"); averages = JsonValue.Create("invalid"); break;
            case "duplicate": quotes = JsonSerializer.SerializeToNode(new[] { Quote(), Quote() }); averages = JsonSerializer.SerializeToNode(new[] { Average(), Average() }); break;
            case "null": quotes = JsonNode.Parse("[null]"); averages = JsonNode.Parse("[null]"); break;
            case "future": quotes![0]!["CapturedAt"] = Start.AddDays(1); averages![0]!["FetchedAt"] = Start.AddDays(1); break;
            case "accumulated-future-skew":
                quotes![0]!["CapturedAt"] = Start.AddMinutes(4); quotes![0]!["FetchedAt"] = Start.AddMinutes(2);
                averages![0]!["FetchedAt"] = Start.AddMinutes(2); averages![0]!["RangeStart"] = Start.AddHours(-24).AddMinutes(4);
                averages![0]!["RangeEnd"] = Start.AddMinutes(4); break;
            case "negative": quotes![0]!["UnitPrice"] = -1; averages![0]!["UnitPrice"] = -1; break;
            case "oversized": quotes = JsonSerializer.SerializeToNode(Enumerable.Range(1, 20_001).Select(id => Quote() with { ItemId = id }));
                averages = JsonSerializer.SerializeToNode(Enumerable.Range(1, 20_001).Select(id => Average() with { ItemId = id })); break;
        }
        json["LastQuotes"] = quotes;
        json["LastAveragePrices"] = averages;
        File.WriteAllText(directory.File, json.ToJsonString());
        var before = File.ReadAllBytes(directory.File);
        var cache = new ExchangeMarketCache(new Clock(), directory.Root);
        Assert.Equal(Quote(), cache.GetQuote(Server, 1));
        Assert.Equal(Average(), cache.GetAveragePrice(Server, 1));
        Assert.Equal(Quote(), cache.GetLastQuote(Server, 1));
        Assert.Equal(Average(), cache.GetLastAveragePrice(Server, 1));
        Assert.Null(cache.GetLastQuote(Server, 2));
        Assert.Null(cache.GetLastAveragePrice(Server, 2));
        Assert.Null(cache.PersistenceError);
        Assert.Equal(before, File.ReadAllBytes(directory.File));
    }

    [Fact]
    public void MarketDiagnosisReportsStoredCountsAndOriginalTimesWithoutFetchingPrices()
    {
        using var directory = new CacheDirectory();
        var cache = new ExchangeMarketCache(new Clock(), directory.Root);
        cache.PutQuotes([Quote()]);
        cache.PutAveragePrices([Average()]);
        var contributor = DiagnosticContributors.Create(new CacheProvider(cache)).Single(row => row.FeatureIds.Contains("loot-market"));
        var result = contributor.Collect("loot-market");
        Assert.Equal(DiagnosticStatus.Waiting, result.Status);
        Assert.Equal(true, result.Facts["priceArchivePersistent"]);
        Assert.Equal(1, result.Facts["lastQuoteCount"]);
        Assert.Equal(1, result.Facts["lastAverageCount"]);
        Assert.Equal(Start, result.Facts["lastQuoteObservedAtUtc"]);
        Assert.Equal(Start, result.Facts["lastAverageWindowEndUtc"]);
        Assert.Equal(false, result.Facts["cacheStorageError"]);
        Assert.DoesNotContain(directory.Root, JsonSerializer.Serialize(result));
    }

    private static ExchangeMarketQuote Quote() => new(Server, 1, 100.5m, 7, Start, Start);
    private static ExchangeMarketAveragePrice Average() => new(Server, 1, 95.25m, 12, Start.AddHours(-24), Start, Start);

    private sealed class CacheProvider(ExchangeMarketCache cache) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(ExchangeMarketCache) ? cache : null;
    }

    private sealed class Client : IExchangeMarketClient, IExchangeMarketAverageClient
    {
        public ImmutableArray<ExchangeMarketQuote> Quotes { get; init; } = [];
        public ImmutableArray<ExchangeMarketAveragePrice> Averages { get; init; } = [];
        public bool Fail { get; set; }
        public int Requests { get; private set; }
        public Task<ImmutableArray<ExchangeMarketServer>> GetServersAsync(CancellationToken cancellationToken) => throw new InvalidOperationException();
        public Task<ImmutableArray<ExchangeMarketQuote>> GetQuotesAsync(int serverId, IReadOnlyCollection<int> itemIds, CancellationToken cancellationToken)
        { Count(); return Task.FromResult(Quotes); }
        public Task<ImmutableArray<ExchangeMarketAveragePrice>> GetAveragePricesAsync(int serverId, IReadOnlyCollection<int> itemIds, CancellationToken cancellationToken)
        { Count(); return Task.FromResult(Averages); }
        private void Count()
        {
            Requests++;
            if (Fail) throw new ExchangeMarketApiException("Synthetic outage", HttpStatusCode.ServiceUnavailable);
        }
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = Start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class CacheDirectory : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "soulcrest-last-prices-" + Guid.NewGuid().ToString("N"));
        public string File => Path.Combine(Root, "exchange-market-cache.json");
        public CacheDirectory() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, true);
    }
}
