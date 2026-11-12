using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class ExchangeMarketAverageCacheTests
{
    private const int Server = 12304;
    private static readonly DateTimeOffset Start = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PositiveAndNegativeAverageLookupsShareTenMinutesWithoutUsingCurrentOrHistoricalQuotes()
    {
        var clock = new Clock();
        var cache = new ExchangeMarketCache(clock);
        var quote = Current(clock);
        cache.PutQuotes([quote]);
        Assert.Null(cache.GetAveragePrice(Server, 1));
        Assert.False(cache.IsAveragePriceLookupFresh(Server, 1, Start));
        var client = new Client { Reply = (_, _, _) => Task.FromResult(ImmutableArray.Create(Average(clock))) };
        Assert.Single(await cache.GetAveragePricesAsync(client, Server, [1, 2], CancellationToken.None));
        Assert.Equal(quote, cache.GetQuote(Server, 1));
        Assert.Equal(12.5m, cache.GetAveragePrice(Server, 1)!.UnitPrice);
        Assert.Null(cache.GetAveragePrice(Server, 2));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(cache.IsAveragePriceLookupFresh(Server, 1, clock.GetUtcNow()));
        Assert.True(cache.IsAveragePriceLookupFresh(Server, 2, clock.GetUtcNow()));
        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.Single(await cache.GetAveragePricesAsync(client, Server, [1, 2], CancellationToken.None));
        Assert.Single(client.Calls);
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.False(cache.IsAveragePriceLookupFresh(Server, 1, clock.GetUtcNow()));
        Assert.False(cache.IsAveragePriceLookupFresh(Server, 2, clock.GetUtcNow()));
        await cache.GetAveragePricesAsync(client, Server, [1, 2], CancellationToken.None);
        Assert.Equal(2, client.Calls.Count);
        Assert.False(cache.IsAveragePriceLookupFresh(Server + 1, 1, clock.GetUtcNow()));
    }

    [Fact]
    public async Task CachedProviderWindowDoesNotShortenTheTenMinuteRequestLifetimeIncludingAfterRestart()
    {
        using var directory = new CacheDirectory();
        var clock = new Clock();
        var cache = new ExchangeMarketCache(clock, directory.Root);
        var client = new Client { Reply = (_, _, _) => Task.FromResult(ImmutableArray.Create(Average(clock) with
            { RangeStart = Start.AddHours(-24).AddMinutes(-5), RangeEnd = Start.AddMinutes(-5) })) };
        var price = Assert.Single(await cache.GetAveragePricesAsync(client, Server, [1], CancellationToken.None));
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.False(ExchangeMarketCache.AverageIsFresh(price, clock.GetUtcNow()));
        Assert.True(cache.IsAveragePriceLookupFresh(Server, 1, clock.GetUtcNow()));
        Assert.Equal(price, Assert.Single(await cache.GetAveragePricesAsync(client, Server, [1], CancellationToken.None)));
        var restored = new ExchangeMarketCache(clock, directory.Root);
        Assert.Equal(price, Assert.Single(await restored.GetAveragePricesAsync(client, Server, [1], CancellationToken.None)));
        Assert.Single(client.Calls);
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.True(restored.IsAveragePriceLookupFresh(Server, 1, clock.GetUtcNow()));
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.False(restored.IsAveragePriceLookupFresh(Server, 1, clock.GetUtcNow()));
        client.Reply = (_, _, _) => Task.FromResult(ImmutableArray.Create(Average(clock)));
        await restored.GetAveragePricesAsync(client, Server, [1], CancellationToken.None);
        Assert.Equal(2, client.Calls.Count);
    }

    [Fact]
    public async Task ParallelConsumersDeduplicateBehindTheExistingGateWhileFreshHitsRemainImmediate()
    {
        var clock = new Clock();
        var cache = new ExchangeMarketCache(clock);
        cache.PutAveragePrices([Average(clock, 3)]);
        var pending = new TaskCompletionSource<ImmutableArray<ExchangeMarketAveragePrice>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Client { Reply = (_, _, _) => pending.Task };
        var first = cache.GetAveragePricesAsync(client, Server, [2, 1, 2], CancellationToken.None);
        var second = cache.GetAveragePricesAsync(client, Server, [1, 2], CancellationToken.None);
        var catalogue = cache.GetServersAsync(client, CancellationToken.None);
        var fresh = cache.GetAveragePricesAsync(client, Server, [3], CancellationToken.None);
        Assert.False(second.IsCompleted);
        Assert.False(catalogue.IsCompleted);
        Assert.True(fresh.IsCompletedSuccessfully);
        Assert.Single(await fresh);
        using var cancelled = new CancellationTokenSource();
        var waiting = cache.GetAveragePricesAsync(client, Server, [4], cancelled.Token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        pending.SetResult([Average(clock, 1), Average(clock, 2)]);
        Assert.Equal(2, (await first).Length);
        Assert.Equal(2, (await second).Length);
        Assert.Empty(await catalogue);
        Assert.Equal(new[] { 1, 2 }, Assert.Single(client.Calls).Ids);
        Assert.Equal(1, client.ServerCalls);
        Assert.False(cache.IsAveragePriceLookupFresh(Server, 4, Start));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task InvalidAverageResponseNeverCommitsPricesOrAnAbsenceLookup(int invalid)
    {
        var clock = new Clock();
        var cache = new ExchangeMarketCache(clock);
        var price = Average(clock);
        ImmutableArray<ExchangeMarketAveragePrice> reply = invalid switch
        {
            0 => default,
            1 => [price with { ServerId = Server + 1 }],
            2 => [price with { ItemId = 2 }],
            3 => [price, price],
            4 => [null!],
            _ => [price with { RangeStart = price.RangeStart.AddSeconds(-2) }],
        };
        var client = new Client { Reply = (_, _, _) => Task.FromResult(reply) };
        await Assert.ThrowsAsync<ExchangeMarketApiException>(() => cache.GetAveragePricesAsync(client, Server, [1], CancellationToken.None));
        Assert.Null(cache.GetAveragePrice(Server, 1));
        Assert.False(cache.IsAveragePriceLookupFresh(Server, 1, Start));
        Assert.Null(cache.GetQuote(Server, 1));
    }

    [Fact]
    public async Task ContextCancellationAfterTheResponsePreventsPublicationAndDoesNotStartBackoff()
    {
        var clock = new Clock();
        var cache = new ExchangeMarketCache(clock);
        using var context = new CancellationTokenSource();
        var pending = new TaskCompletionSource<ImmutableArray<ExchangeMarketAveragePrice>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Client { Reply = (_, _, _) => pending.Task };
        var request = cache.GetAveragePricesAsync(client, Server, [1], context.Token);
        context.Cancel();
        pending.SetResult([Average(clock)]); // Deliberately ignores the caller's cancellation.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Null(cache.GetAveragePrice(Server, 1));
        Assert.False(cache.IsAveragePriceLookupFresh(Server, 1, Start));
        client.Reply = (_, _, _) => Task.FromResult(ImmutableArray.Create(Average(clock)));
        Assert.Single(await cache.GetAveragePricesAsync(client, Server, [1], CancellationToken.None));
        Assert.Equal(2, client.Calls.Count);
    }

    [Fact]
    public async Task AverageFailuresShareProviderBackoffWithLegacyQuotesAndSuccessfulRetryClearsIt()
    {
        var clock = new Clock();
        var cache = new ExchangeMarketCache(clock);
        var client = new Client { Reply = (_, _, _) => throw new ExchangeMarketApiException("Synthetic retry", HttpStatusCode.TooManyRequests, TimeSpan.FromMinutes(2)) };
        var failure = await Assert.ThrowsAsync<ExchangeMarketApiException>(() => cache.GetAveragePricesAsync(client, Server, [1], CancellationToken.None));
        Assert.Equal(TimeSpan.FromMinutes(2), failure.RetryAfter);
        await Assert.ThrowsAsync<ExchangeMarketApiException>(() => cache.GetQuotesAsync(client, Server, [2], CancellationToken.None));
        Assert.Equal(0, client.QuoteCalls);
        Assert.Single(client.Calls);
        clock.Advance(TimeSpan.FromMinutes(2));
        client.Reply = (_, _, _) => Task.FromResult(ImmutableArray.Create(Average(clock)));
        Assert.Single(await cache.GetAveragePricesAsync(client, Server, [1], CancellationToken.None));
        Assert.Empty(await cache.GetQuotesAsync(client, Server, [2], CancellationToken.None));
        Assert.Equal(1, client.QuoteCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void AverageBoundsRejectUnqualifiedWindowsPricesSamplesAndFutureTimes(int invalid)
    {
        var price = Average(new Clock());
        price = invalid switch
        {
            0 => price with { ServerId = 0 },
            1 => price with { ItemId = 0 },
            2 => price with { UnitPrice = 0 },
            3 => price with { Samples = 0 },
            4 => price with { RangeStart = price.RangeStart.AddSeconds(-2) },
            5 => price with { RangeStart = price.RangeStart.AddSeconds(2) },
            6 => price with { FetchedAt = Start.AddMinutes(2).AddTicks(1) },
            _ => price with { RangeEnd = Start.AddMinutes(2).AddTicks(1), RangeStart = Start.AddHours(-24).AddMinutes(2).AddTicks(1) },
        };
        Assert.False(ExchangeMarketCache.AverageIsUsable(price, Start));
        Assert.False(ExchangeMarketCache.AverageIsFresh(price, Start));
    }

    [Fact]
    public void FreshFetchCannotRefreshAnOldWindowAndUsabilityExpiresAfterTwoHours()
    {
        var clock = new Clock();
        var price = Average(clock) with { RangeEnd = Start.AddMinutes(-11), RangeStart = Start.AddHours(-24).AddMinutes(-11) };
        Assert.True(ExchangeMarketCache.AverageIsUsable(price, Start));
        Assert.False(ExchangeMarketCache.AverageIsFresh(price, Start));
        var cache = new ExchangeMarketCache(clock);
        cache.PutAveragePrices([price]);
        Assert.Equal(price, cache.GetAveragePrice(Server, 1));
        Assert.False(cache.IsAveragePriceLookupFresh(Server, 1, Start));
        clock.Advance(TimeSpan.FromHours(2) - TimeSpan.FromMinutes(11));
        Assert.NotNull(cache.GetAveragePrice(Server, 1));
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.Null(cache.GetAveragePrice(Server, 1));
        var current = Average(new Clock());
        Assert.True(ExchangeMarketCache.AverageIsUsable(current with { RangeStart = current.RangeStart.AddSeconds(-1) }, Start));
        Assert.True(ExchangeMarketCache.AverageIsUsable(current with { RangeStart = current.RangeStart.AddSeconds(1) }, Start));
        Assert.True(ExchangeMarketCache.AverageIsUsable(current with { RangeStart = current.RangeStart.AddMinutes(2), RangeEnd = current.RangeEnd.AddMinutes(2) }, Start));
    }

    [Fact]
    public async Task AverageEvidenceAndTenMinuteAbsenceSurviveRestartWithoutChangingLegacyEvidence()
    {
        using var directory = new CacheDirectory();
        var clock = new Clock();
        SeedLegacy(directory, clock);
        var cache = new ExchangeMarketCache(clock, directory.Root);
        Assert.Null(cache.GetAveragePrice(Server, 1));
        var client = new Client { Reply = (_, _, _) => Task.FromResult(ImmutableArray.Create(Average(clock))) };
        await cache.GetAveragePricesAsync(client, Server, [1, 2], CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(2));
        cache = new ExchangeMarketCache(clock, directory.Root);
        Assert.Single(await cache.GetAveragePricesAsync(client, Server, [1, 2], CancellationToken.None));
        Assert.Single(client.Calls);
        Assert.Equal(Current(new Clock()), cache.GetQuote(Server, 1));
        Assert.NotNull(cache.GetHistoricalQuote(Server, 1));
        Assert.False(cache.IsQuoteLookupFresh(Server, 3, clock.GetUtcNow())); // Its original one-minute absence is unchanged.
        Assert.True(cache.IsAveragePriceLookupFresh(Server, 2, clock.GetUtcNow()));
        using var json = JsonDocument.Parse(File.ReadAllBytes(directory.File));
        Assert.Equal(1, json.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.Single(json.RootElement.GetProperty("AveragePrices").EnumerateArray());
        Assert.Equal(2, json.RootElement.GetProperty("AverageLookups").GetArrayLength());
        Assert.DoesNotContain("Profile", json.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Character", json.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("shape")]
    [InlineData("lookup-shape")]
    [InlineData("duplicate")]
    [InlineData("future")]
    [InlineData("lookup-future")]
    [InlineData("lookup-duplicate")]
    [InlineData("window")]
    [InlineData("oversized")]
    public void CorruptOptionalAverageDataNeverDiscardsOrRewritesLegacyCacheEvidence(string corruption)
    {
        using var directory = new CacheDirectory();
        var clock = new Clock();
        SeedLegacy(directory, clock);
        var json = JsonNode.Parse(File.ReadAllBytes(directory.File))!;
        var price = Average(clock);
        json["AveragePrices"] = JsonSerializer.SerializeToNode(new[] { price });
        json["AverageLookups"] = JsonSerializer.SerializeToNode(new[] { new { ServerId = Server, ItemId = 2, FetchedAt = Start } });
        switch (corruption)
        {
            case "shape": json["AveragePrices"] = "invalid"; break;
            case "lookup-shape": json["AverageLookups"] = "invalid"; break;
            case "duplicate": json["AveragePrices"] = JsonSerializer.SerializeToNode(new[] { price, price }); break;
            case "future": json["AveragePrices"] = JsonSerializer.SerializeToNode(new[] { price with { FetchedAt = Start.AddMinutes(3) } }); break;
            case "lookup-future": json["AverageLookups"] = JsonSerializer.SerializeToNode(new[] { new { ServerId = Server, ItemId = 2, FetchedAt = Start.AddMinutes(3) } }); break;
            case "lookup-duplicate": json["AverageLookups"] = JsonSerializer.SerializeToNode(Enumerable.Repeat(new { ServerId = Server, ItemId = 2, FetchedAt = Start }, 2).ToArray()); break;
            case "window": json["AveragePrices"] = JsonSerializer.SerializeToNode(new[] { price with { RangeStart = price.RangeStart.AddHours(1) } }); break;
            case "oversized": json["AveragePrices"] = JsonSerializer.SerializeToNode(Enumerable.Range(1, 20_001).Select(id => price with { ItemId = id }).ToArray()); break;
        }
        File.WriteAllText(directory.File, json.ToJsonString());
        var before = File.ReadAllBytes(directory.File);
        var cache = new ExchangeMarketCache(clock, directory.Root);
        Assert.Equal(Current(clock), cache.GetQuote(Server, 1));
        Assert.NotNull(cache.GetHistoricalQuote(Server, 1));
        Assert.True(cache.IsQuoteLookupFresh(Server, 3, Start));
        Assert.Null(cache.GetAveragePrice(Server, 1));
        Assert.False(cache.IsAveragePriceLookupFresh(Server, 2, Start));
        Assert.Null(cache.PersistenceError);
        Assert.Equal(before, File.ReadAllBytes(directory.File));
    }

    [Fact]
    public void BoundedPersistenceEvictsOptionalAveragesBeforeLegacyQuotesOrHistory()
    {
        using var directory = new CacheDirectory();
        var clock = new Clock();
        File.WriteAllText(directory.File, JsonSerializer.Serialize(new
        {
            SchemaVersion = 1, Provider = "aion2.exchange", ServersFetchedAt = (DateTimeOffset?)null,
            Servers = Array.Empty<ExchangeMarketServer>(),
            Quotes = Enumerable.Range(1, 20_000).Select(id => Current(clock) with { ItemId = id }).ToArray(),
            HistoricalQuotes = Enumerable.Range(1, 20_000).Select(id => new ExchangeMarketHistoricalQuote(Server, id, 7m, Start.AddDays(-3), Start)).ToArray(),
        }));
        Assert.InRange(new FileInfo(directory.File).Length, 1, 8 * 1024 * 1024);
        var cache = new ExchangeMarketCache(clock, directory.Root);
        cache.PutAveragePrices(Enumerable.Range(1, 20_000).Select(id => Average(clock, id)));
        Assert.Null(cache.PersistenceError);
        Assert.InRange(new FileInfo(directory.File).Length, 1, 8 * 1024 * 1024);
        using var json = JsonDocument.Parse(File.ReadAllBytes(directory.File));
        Assert.Equal(20_000, json.RootElement.GetProperty("Quotes").GetArrayLength());
        Assert.Equal(20_000, json.RootElement.GetProperty("HistoricalQuotes").GetArrayLength());
        Assert.InRange(json.RootElement.GetProperty("AveragePrices").GetArrayLength(), 1, 19_999);
        var restarted = new ExchangeMarketCache(clock, directory.Root);
        Assert.NotNull(restarted.GetQuote(Server, 20_000));
        Assert.NotNull(restarted.GetHistoricalQuote(Server, 20_000));
        Assert.NotNull(restarted.GetAveragePrice(Server, 1));
    }

    [Fact]
    public async Task InvalidInputAndCancelledCallsNeverReachTheAverageClient()
    {
        var clock = new Clock(); var cache = new ExchangeMarketCache(clock); var client = new Client();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => cache.GetAveragePricesAsync(client, 0, [1], CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => cache.GetAveragePricesAsync(client, Server, [0], CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => cache.GetAveragePricesAsync(client, Server, Enumerable.Range(1, 20_001).ToArray(), CancellationToken.None));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetAveragePricesAsync(client, Server, [1], cancelled.Token));
        Assert.Empty(client.Calls);
        Assert.Throws<ArgumentException>(() => cache.PutAveragePrices(Enumerable.Range(1, 20_001).Select(id => Average(clock, id))));
    }

    private static ExchangeMarketAveragePrice Average(Clock clock, int item = 1) =>
        new(Server, item, 12.5m, 3, clock.GetUtcNow().AddHours(-24), clock.GetUtcNow(), clock.GetUtcNow());
    private static ExchangeMarketQuote Current(Clock clock) => new(Server, 1, 99m, 1, clock.GetUtcNow(), clock.GetUtcNow());

    private static void SeedLegacy(CacheDirectory directory, Clock clock) => File.WriteAllText(directory.File, JsonSerializer.Serialize(new
    {
        SchemaVersion = 1, Provider = "aion2.exchange", ServersFetchedAt = (DateTimeOffset?)null,
        Servers = Array.Empty<ExchangeMarketServer>(), Quotes = new[] { Current(clock) },
        Lookups = new[] { new { ServerId = Server, ItemId = 3, FetchedAt = clock.GetUtcNow() } },
        HistoricalQuotes = new[] { new ExchangeMarketHistoricalQuote(Server, 1, 7m, Start.AddDays(-3), Start) },
    }));

    private sealed class Client : IExchangeMarketAverageClient, IExchangeMarketClient
    {
        public ConcurrentQueue<(int Server, int[] Ids)> Calls { get; } = new();
        public int ServerCalls, QuoteCalls;
        public Func<int, IReadOnlyCollection<int>, CancellationToken, Task<ImmutableArray<ExchangeMarketAveragePrice>>> Reply { get; set; } =
            (_, _, _) => Task.FromResult(ImmutableArray<ExchangeMarketAveragePrice>.Empty);
        public Task<ImmutableArray<ExchangeMarketAveragePrice>> GetAveragePricesAsync(int serverId, IReadOnlyCollection<int> ids, CancellationToken token)
        { Calls.Enqueue((serverId, ids.ToArray())); return Reply(serverId, ids, token); }
        public Task<ImmutableArray<ExchangeMarketServer>> GetServersAsync(CancellationToken token)
        { Interlocked.Increment(ref ServerCalls); return Task.FromResult(ImmutableArray<ExchangeMarketServer>.Empty); }
        public Task<ImmutableArray<ExchangeMarketQuote>> GetQuotesAsync(int serverId, IReadOnlyCollection<int> ids, CancellationToken token)
        { Interlocked.Increment(ref QuoteCalls); return Task.FromResult(ImmutableArray<ExchangeMarketQuote>.Empty); }
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = Start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
    private sealed class CacheDirectory : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "soulcrest-average-cache-" + Guid.NewGuid().ToString("N"));
        public string File => Path.Combine(Root, "exchange-market-cache.json");
        public CacheDirectory() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, true);
    }
}
