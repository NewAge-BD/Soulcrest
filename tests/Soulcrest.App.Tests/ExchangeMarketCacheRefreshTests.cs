using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class ExchangeMarketCacheRefreshTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CurrentCatalogueWithASingleUnmappedRowKeepsItsFetchTime()
    {
        // A current provider catalogue may leave one row without a native key; only a key-less one is a legacy cache.
        using var directory = new CacheDirectory();
        Directory.CreateDirectory(directory.Root);
        object unmapped = new { ServerId = 11301, Name = "Example", Region = "eu", RegionName = "Europe", Faction = "Elyos", HasData = true };
        File.WriteAllText(Path.Combine(directory.Root, "exchange-market-cache.json"), JsonSerializer.Serialize(new
        {
            SchemaVersion = 1, Provider = "aion2.exchange", ServersFetchedAt = Start,
            Servers = new object[] { unmapped, Server() with { ServerId = 11302, GameServerKey = 1302 } },
            Quotes = Array.Empty<object>(), HistoricalQuotes = Array.Empty<object>(), Lookups = Array.Empty<object>(),
        }));
        var cache = new ExchangeMarketCache(new Clock(), directory.Root);
        Assert.Equal(Start, cache.ServersFetchedAt);
        Assert.Null(Assert.Single(cache.GetServers(), server => server.ServerId == 11301).GameServerKey);
    }

    [Theory]
    [InlineData(false)]
    public async Task LegacyCatalogueWithoutNativeKeysRefreshesOnceAndPreservesAllPriceEvidence(bool mixedCatalogue)
    {
        using var directory = new CacheDirectory();
        Directory.CreateDirectory(directory.Root);
        var clock = new Clock();
        var quote = Quote(clock, 1);
        var historical = new ExchangeMarketHistoricalQuote(11301, 2, 7.25m, Start.AddHours(-5), Start);
        object legacy = new { ServerId = 11301, Name = "Example", Region = "eu", RegionName = "Europe", Faction = "Elyos", HasData = true };
        object[] servers = mixedCatalogue ? [legacy, Server() with { ServerId = 11302, GameServerKey = 1302 }] : [legacy];
        File.WriteAllText(Path.Combine(directory.Root, "exchange-market-cache.json"), JsonSerializer.Serialize(new
        {
            SchemaVersion = 1, Provider = "aion2.exchange", ServersFetchedAt = Start,
            Servers = servers, Quotes = new[] { quote }, HistoricalQuotes = new[] { historical },
            Lookups = new[] { new { ServerId = 11301, ItemId = 3, FetchedAt = Start } },
        }));
        var cache = new ExchangeMarketCache(clock, directory.Root);
        Assert.Null(cache.ServersFetchedAt);
        Assert.Equal(servers.Length, cache.GetServers().Length);
        Assert.Equal(quote, cache.GetQuote(11301, 1));
        Assert.Equal(historical, cache.GetHistoricalQuote(11301, 2));
        Assert.True(cache.IsQuoteLookupFresh(11301, 3, Start));
        var client = new Client(clock);
        await Task.WhenAll(cache.GetServersAsync(client, CancellationToken.None), cache.GetServersAsync(client, CancellationToken.None));
        Assert.Equal(1, client.ServerCalls);
        Assert.Empty(client.QuoteCalls);
        var restored = new ExchangeMarketCache(clock, directory.Root);
        Assert.Equal(Start, restored.ServersFetchedAt);
        Assert.Equal(1301, Assert.Single(restored.GetServers()).GameServerKey);
        Assert.Equal(quote, restored.GetQuote(11301, 1));
        Assert.Equal(historical, restored.GetHistoricalQuote(11301, 2));
        Assert.True(restored.IsQuoteLookupFresh(11301, 3, Start));
        await restored.GetServersAsync(client, CancellationToken.None);
        Assert.Equal(1, client.ServerCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidNativeKeysCannotReplaceAValidCatalogue(int invalid)
    {
        using var directory = new CacheDirectory();
        var cache = new ExchangeMarketCache(new Clock(), directory.Root);
        cache.SaveServers([Server()]);
        var before = File.ReadAllBytes(Path.Combine(directory.Root, "exchange-market-cache.json"));
        Assert.Throws<ArgumentException>(() => cache.SaveServers([Server() with { GameServerKey = invalid }]));
        Assert.Equal(1301, Assert.Single(cache.GetServers()).GameServerKey);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(directory.Root, "exchange-market-cache.json")));
    }

    [Fact]
    public void AmbiguousNativeKeysRemainInTheCatalogueForExplicitServerSelection()
    {
        using var directory = new CacheDirectory();
        var cache = new ExchangeMarketCache(new Clock(), directory.Root);
        cache.SaveServers([Server(), Server() with { ServerId = 21301, Region = "other" }]);
        var restored = new ExchangeMarketCache(new Clock(), directory.Root);
        Assert.Equal(2, restored.GetServers().Length);
        Assert.All(restored.GetServers(), row => Assert.Equal(1301, row.GameServerKey));
        Assert.Equal(Start, restored.ServersFetchedAt);
    }

    [Fact]
    public async Task ParallelMissesRecheckAfterTheSharedGateAndFetchOnlyOnce()
    {
        var clock = new Clock();
        var cache = new ExchangeMarketCache(clock);
        var pending = new TaskCompletionSource<ImmutableArray<ExchangeMarketQuote>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Client(clock) { Quotes = (_, _, token) => pending.Task.WaitAsync(token) };
        var first = cache.GetQuotesAsync(client, 11301, [2, 1, 2], CancellationToken.None);
        var second = cache.GetQuotesAsync(client, 11301, [1, 2], CancellationToken.None);
        Assert.False(second.IsCompleted);
        pending.SetResult([Quote(clock, 1), Quote(clock, 2)]);
        Assert.Equal(2, (await first).Length);
        Assert.Equal(2, (await second).Length);
        Assert.Equal(new[] { 1, 2 }, Assert.Single(client.QuoteCalls).Ids);
    }

    [Fact]
    public async Task FreshHitsDoNotWaitForAnUnrelatedSlowRequest()
    {
        var clock = new Clock();
        var cache = new ExchangeMarketCache(clock);
        cache.PutQuotes([Quote(clock, 1)]);
        cache.SaveServers([Server()]);
        var pending = new TaskCompletionSource<ImmutableArray<ExchangeMarketQuote>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Client(clock) { Quotes = (_, _, token) => pending.Task.WaitAsync(token) };
        var slow = cache.GetQuotesAsync(client, 11301, [2], CancellationToken.None);
        var quotes = cache.GetQuotesAsync(client, 11301, [1], CancellationToken.None);
        var servers = cache.GetServersAsync(client, CancellationToken.None);
        Assert.True(quotes.IsCompletedSuccessfully);
        Assert.True(servers.IsCompletedSuccessfully);
        Assert.Single(await quotes);
        Assert.Single(await servers);
        pending.SetResult([]);
        await slow;
    }

    [Fact]
    public async Task MissingQuotesAreSharedAcrossCallersAndCacheRestartsUntilTheirTtlExpires()
    {
        using var directory = new CacheDirectory();
        var clock = new Clock();
        var client = new Client(clock) { Quotes = (_, _, _) => Task.FromResult(ImmutableArray<ExchangeMarketQuote>.Empty) };
        var cache = new ExchangeMarketCache(clock, directory.Root);
        Assert.Empty(await cache.GetQuotesAsync(client, 11301, [1], CancellationToken.None));
        Assert.True(cache.IsQuoteLookupFresh(11301, 1, clock.GetUtcNow()));
        Assert.Null(cache.GetQuote(11301, 1));
        cache = new ExchangeMarketCache(clock, directory.Root);
        Assert.Empty(await cache.GetQuotesAsync(client, 11301, [1], CancellationToken.None));
        Assert.Single(client.QuoteCalls);
        clock.Advance(ExchangeMarketCache.MissingQuoteTtl - TimeSpan.FromTicks(1));
        Assert.Empty(await cache.GetQuotesAsync(client, 11301, [1], CancellationToken.None));
        Assert.Single(client.QuoteCalls);
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.Empty(await cache.GetQuotesAsync(client, 11301, [1], CancellationToken.None));
        Assert.Equal(2, client.QuoteCalls.Count);
        using var file = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.Root, "exchange-market-cache.json")));
        Assert.Equal(1, file.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.Single(file.RootElement.GetProperty("Lookups").EnumerateArray());
        Assert.Single(Directory.GetFiles(directory.Root));
    }

    [Fact]
    public async Task SuccessfulUnusableResponseInvalidatesTheOldQuoteWithoutInventingZero()
    {
        var clock = new Clock();
        var cache = new ExchangeMarketCache(clock);
        cache.PutQuotes([Quote(clock, 1) with { CapturedAt = Start.AddMinutes(-12), FetchedAt = Start.AddMinutes(-11) }]);
        var client = new Client(clock) { Quotes = (_, _, _) => Task.FromResult<ImmutableArray<ExchangeMarketQuote>>([Quote(clock, 1) with { Listings = 0 }]) };
        Assert.Empty(await cache.GetQuotesAsync(client, 11301, [1], CancellationToken.None));
        Assert.Null(cache.GetQuote(11301, 1));
        Assert.True(cache.IsQuoteLookupFresh(11301, 1, clock.GetUtcNow()));
        Assert.Empty(await cache.GetQuotesAsync(client, 11301, [1], CancellationToken.None));
        Assert.Single(client.QuoteCalls);
    }

    [Fact]
    public async Task ServerAndItemKeysRemainIndependent()
    {
        var clock = new Clock();
        var cache = new ExchangeMarketCache(clock);
        var client = new Client(clock);
        await cache.GetQuotesAsync(client, 11301, [1], CancellationToken.None);
        await cache.GetQuotesAsync(client, 11302, [1], CancellationToken.None);
        await cache.GetQuotesAsync(client, 11301, [2], CancellationToken.None);
        Assert.Equal(3, client.QuoteCalls.Count);
        Assert.Equal(11301, cache.GetQuote(11301, 1)!.ServerId);
        Assert.Equal(11302, cache.GetQuote(11302, 1)!.ServerId);
        Assert.False(cache.IsQuoteLookupFresh(11302, 2, clock.GetUtcNow()));
    }

    [Fact]
    public async Task SourceAgeAndLookupAgeRemainIndependent()
    {
        var clock = new Clock();
        var cache = new ExchangeMarketCache(clock);
        var client = new Client(clock) { Quotes = (_, _, _) => Task.FromResult<ImmutableArray<ExchangeMarketQuote>>([Quote(clock, 1) with { CapturedAt = Start.AddHours(-2).AddTicks(-1) }]) };
        Assert.Empty(await cache.GetQuotesAsync(client, 11301, [1], CancellationToken.None));
        Assert.True(cache.IsQuoteLookupFresh(11301, 1, clock.GetUtcNow()));
        Assert.Null(cache.GetQuote(11301, 1));
        clock.Advance(ExchangeMarketCache.MissingQuoteTtl);
        client.Quotes = (_, _, _) => Task.FromResult<ImmutableArray<ExchangeMarketQuote>>([Quote(clock, 1)]);
        Assert.Single(await cache.GetQuotesAsync(client, 11301, [1], CancellationToken.None));
        Assert.Equal(2, client.QuoteCalls.Count);
        clock.Advance(ExchangeMarketCache.MaximumAge + TimeSpan.FromTicks(1));
        Assert.Null(cache.GetQuote(11301, 1));
        Assert.False(cache.IsQuoteLookupFresh(11301, 1, clock.GetUtcNow()));
    }

    [Fact]
    public async Task EarlierSuccessfulBatchesRemainCachedWhenTheNextBatchFails()
    {
        using var directory = new CacheDirectory();
        var clock = new Clock();
        var cache = new ExchangeMarketCache(clock, directory.Root);
        var client = new Client(clock)
        {
            Quotes = (server, ids, _) => ids.Contains(101)
                ? Task.FromException<ImmutableArray<ExchangeMarketQuote>>(new HttpRequestException("offline"))
                : Task.FromResult(ids.Select(id => Quote(clock, id, server)).ToImmutableArray())
        };
        await Assert.ThrowsAsync<ExchangeMarketApiException>(() => cache.GetQuotesAsync(client, 11301, Enumerable.Range(1, 101).ToArray(), CancellationToken.None));
        Assert.Equal(100, (await cache.GetQuotesAsync(client, 11301, Enumerable.Range(1, 100).ToArray(), CancellationToken.None)).Length);
        Assert.Equal(2, client.QuoteCalls.Count);
        var restored = new ExchangeMarketCache(clock, directory.Root);
        Assert.NotNull(restored.GetQuote(11301, 100));
        Assert.Null(restored.GetQuote(11301, 101));
        Assert.False(restored.IsQuoteLookupFresh(11301, 101, clock.GetUtcNow()));
    }

    [Fact]
    public async Task FailedRefreshKeepsOldQuotesAndSharesBackoffWithServerRequests()
    {
        var clock = new Clock();
        var cache = new ExchangeMarketCache(clock);
        var old = Quote(clock, 1) with { CapturedAt = Start.AddMinutes(-12), FetchedAt = Start.AddMinutes(-11) };
        cache.PutQuotes([old]);
        var client = new Client(clock) { Quotes = (_, _, _) => Task.FromException<ImmutableArray<ExchangeMarketQuote>>(new HttpRequestException("offline")) };
        var first = await Assert.ThrowsAsync<ExchangeMarketApiException>(() => cache.GetQuotesAsync(client, 11301, [1], CancellationToken.None));
        Assert.Equal(TimeSpan.FromSeconds(30), first.RetryAfter);
        Assert.Equal(old, cache.GetQuote(11301, 1));
        clock.Advance(TimeSpan.FromSeconds(7));
        var deferred = await Assert.ThrowsAsync<ExchangeMarketApiException>(() => cache.GetServersAsync(client, CancellationToken.None));
        Assert.Equal(TimeSpan.FromSeconds(23), deferred.RetryAfter);
        Assert.Equal(0, client.ServerCalls);
        Assert.Single(client.QuoteCalls);
        clock.Advance(TimeSpan.FromSeconds(23));
        var second = await Assert.ThrowsAsync<ExchangeMarketApiException>(() => cache.GetQuotesAsync(client, 11301, [1], CancellationToken.None));
        Assert.Equal(TimeSpan.FromSeconds(60), second.RetryAfter);
        clock.Advance(TimeSpan.FromSeconds(60));
        client.Quotes = (server, ids, _) => Task.FromResult(ids.Select(id => Quote(clock, id, server)).ToImmutableArray());
        Assert.Single(await cache.GetQuotesAsync(client, 11301, [1], CancellationToken.None));
        client.Quotes = (_, _, _) => Task.FromException<ImmutableArray<ExchangeMarketQuote>>(new TimeoutException());
        var afterSuccess = await Assert.ThrowsAsync<ExchangeMarketApiException>(() => cache.GetQuotesAsync(client, 11301, [2], CancellationToken.None));
        Assert.Equal(TimeSpan.FromSeconds(30), afterSuccess.RetryAfter);
        Assert.Single(await cache.GetQuotesAsync(client, 11301, [1], CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task TimeoutNetworkAndInvalidResponsesAllDeferSubsequentRequests(int kind)
    {
        var clock = new Clock();
        var cache = new ExchangeMarketCache(clock);
        var client = new Client(clock)
        {
            Quotes = (_, _, _) => kind switch
            {
                0 => Task.FromException<ImmutableArray<ExchangeMarketQuote>>(new OperationCanceledException("provider timeout")),
                1 => Task.FromException<ImmutableArray<ExchangeMarketQuote>>(new HttpRequestException("offline")),
                _ => Task.FromResult<ImmutableArray<ExchangeMarketQuote>>([Quote(clock, 1, 999)])
            }
        };
        await Assert.ThrowsAsync<ExchangeMarketApiException>(() => cache.GetQuotesAsync(client, 11301, [1], CancellationToken.None));
        await Assert.ThrowsAsync<ExchangeMarketApiException>(() => cache.GetQuotesAsync(client, 11301, [2], CancellationToken.None));
        Assert.Single(client.QuoteCalls);
        Assert.False(cache.IsQuoteLookupFresh(11301, 1, clock.GetUtcNow()));
    }

    [Fact]
    public async Task CallerCancellationAndCancellationWhileQueuedDoNotCreateBackoff()
    {
        var clock = new Clock();
        var cache = new ExchangeMarketCache(clock);
        var pending = new TaskCompletionSource<ImmutableArray<ExchangeMarketQuote>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Client(clock) { Quotes = (_, _, token) => pending.Task.WaitAsync(token) };
        using var activeCancel = new CancellationTokenSource();
        var active = cache.GetQuotesAsync(client, 11301, [1], activeCancel.Token);
        using var queueCancel = new CancellationTokenSource();
        var queued = cache.GetServersAsync(client, queueCancel.Token);
        queueCancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        activeCancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        Assert.Single(client.QuoteCalls);
        Assert.Equal(0, client.ServerCalls);
        Assert.Single(await cache.GetServersAsync(client, CancellationToken.None));
        Assert.Equal(1, client.ServerCalls);
        Assert.False(cache.IsQuoteLookupFresh(11301, 1, clock.GetUtcNow()));
    }

    [Fact]
    public async Task ProviderRetryAfterLongerThanTheBackoffCapIsRespected()
    {
        var clock = new Clock();
        var cache = new ExchangeMarketCache(clock);
        var client = new Client(clock)
        {
            Servers = _ => Task.FromException<ImmutableArray<ExchangeMarketServer>>(new ExchangeMarketApiException("busy", HttpStatusCode.TooManyRequests, TimeSpan.FromHours(1)))
        };
        var first = await Assert.ThrowsAsync<ExchangeMarketApiException>(() => cache.GetServersAsync(client, CancellationToken.None));
        Assert.Equal(TimeSpan.FromHours(1), first.RetryAfter);
        clock.Advance(TimeSpan.FromMinutes(59));
        var deferred = await Assert.ThrowsAsync<ExchangeMarketApiException>(() => cache.GetQuotesAsync(client, 11301, [1], CancellationToken.None));
        Assert.Equal(TimeSpan.FromMinutes(1), deferred.RetryAfter);
        Assert.Equal(HttpStatusCode.TooManyRequests, deferred.StatusCode);
        Assert.Empty(client.QuoteCalls);
    }

    [Fact]
    public async Task RetryDateAdditionSaturatesAtTheLargestDate()
    {
        var clock = new Clock(DateTimeOffset.MaxValue.AddHours(-1));
        var cache = new ExchangeMarketCache(clock);
        var client = new Client(clock)
        {
            Servers = _ => Task.FromException<ImmutableArray<ExchangeMarketServer>>(new ExchangeMarketApiException("busy", retryAfter: TimeSpan.MaxValue))
        };
        var error = await Assert.ThrowsAsync<ExchangeMarketApiException>(() => cache.GetServersAsync(client, CancellationToken.None));
        Assert.Equal(TimeSpan.FromHours(1), error.RetryAfter);
    }

    [Fact]
    public async Task EmptyServerListsHaveTheSamePersistentFetchTtl()
    {
        using var directory = new CacheDirectory();
        var clock = new Clock();
        var cache = new ExchangeMarketCache(clock, directory.Root);
        var client = new Client(clock) { Servers = _ => Task.FromResult(ImmutableArray<ExchangeMarketServer>.Empty) };
        Assert.Empty(await cache.GetServersAsync(client, CancellationToken.None));
        cache = new ExchangeMarketCache(clock, directory.Root);
        Assert.Empty(await cache.GetServersAsync(client, CancellationToken.None));
        Assert.Equal(1, client.ServerCalls);
        clock.Advance(ExchangeMarketCache.FetchTtl + TimeSpan.FromTicks(1));
        Assert.Empty(await cache.GetServersAsync(client, CancellationToken.None));
        Assert.Equal(2, client.ServerCalls);
    }

    [Fact]
    public async Task FailedPersistenceKeepsBothThePriceAndMissingLookupInRam()
    {
        using var directory = new CacheDirectory();
        Directory.CreateDirectory(directory.Root);
        var occupied = Path.Combine(directory.Root, "occupied");
        File.WriteAllText(occupied, "fixture");
        var clock = new Clock();
        var cache = new ExchangeMarketCache(clock, occupied);
        var client = new Client(clock) { Quotes = (_, _, _) => Task.FromResult<ImmutableArray<ExchangeMarketQuote>>([Quote(clock, 1)]) };
        Assert.Single(await cache.GetQuotesAsync(client, 11301, [1, 2], CancellationToken.None));
        Assert.NotNull(cache.PersistenceError);
        Assert.NotNull(cache.GetQuote(11301, 1));
        Assert.True(cache.IsQuoteLookupFresh(11301, 2, clock.GetUtcNow()));
        Assert.Single(await cache.GetQuotesAsync(client, 11301, [1, 2], CancellationToken.None));
        Assert.Single(client.QuoteCalls);
        Assert.Equal("fixture", File.ReadAllText(occupied));
    }

    [Fact]
    public async Task OriginalSchemaOnePositiveQuotesDoNotNeedTheOptionalLookupField()
    {
        using var directory = new CacheDirectory();
        Directory.CreateDirectory(directory.Root);
        var clock = new Clock();
        File.WriteAllText(Path.Combine(directory.Root, "exchange-market-cache.json"), JsonSerializer.Serialize(new
        {
            SchemaVersion = 1, Provider = "aion2.exchange", ServersFetchedAt = (DateTimeOffset?)null,
            Servers = Array.Empty<ExchangeMarketServer>(), Quotes = new[] { Quote(clock, 1) }
        }));
        var cache = new ExchangeMarketCache(clock, directory.Root);
        var client = new Client(clock);
        Assert.True(cache.IsQuoteLookupFresh(11301, 1, clock.GetUtcNow()));
        Assert.Single(await cache.GetQuotesAsync(client, 11301, [1], CancellationToken.None));
        Assert.Empty(client.QuoteCalls);
    }

    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(11301, 0, 0)]
    [InlineData(11301, 1, 3)]
    [InlineData(11301, 1, -100000000)]
    public void InvalidPersistedLookupIdsAndTimesNeverSuppressRequests(int server, int item, int minutes)
    {
        using var directory = new CacheDirectory();
        Directory.CreateDirectory(directory.Root);
        File.WriteAllText(Path.Combine(directory.Root, "exchange-market-cache.json"), JsonSerializer.Serialize(new
        {
            SchemaVersion = 1, Provider = "aion2.exchange", ServersFetchedAt = (DateTimeOffset?)null,
            Servers = Array.Empty<ExchangeMarketServer>(), Quotes = Array.Empty<ExchangeMarketQuote>(),
            Lookups = new[] { new { ServerId = server, ItemId = item, FetchedAt = Start.AddMinutes(minutes) } }
        }));
        var cache = new ExchangeMarketCache(new Clock(), directory.Root);
        Assert.False(cache.IsQuoteLookupFresh(11301, 1, Start));
    }

    [Fact]
    public async Task InputsAreDistinctAndBoundedBeforeRequests()
    {
        var clock = new Clock();
        var cache = new ExchangeMarketCache(clock);
        var client = new Client(clock);
        await cache.GetQuotesAsync(client, 11301, Enumerable.Repeat(1, 20_000).ToArray(), CancellationToken.None);
        await Assert.ThrowsAsync<ArgumentException>(() => cache.GetQuotesAsync(client, 11301, Enumerable.Repeat(1, 20_001).ToArray(), CancellationToken.None));
        Assert.Single(Assert.Single(client.QuoteCalls).Ids);
        await Assert.ThrowsAsync<ArgumentException>(() => cache.GetQuotesAsync(client, 11301, Enumerable.Range(1, 20_001).ToArray(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => cache.GetQuotesAsync(client, 11301, [0], CancellationToken.None));
        Assert.Single(client.QuoteCalls);
    }

    private static ExchangeMarketQuote Quote(Clock clock, int item, int server = 11301) => new(server, item, 12.5m, 2, clock.GetUtcNow().AddMinutes(-1), clock.GetUtcNow());
    private static ExchangeMarketServer Server() => new(11301, "Example", "eu", "Europe", "Elyos", true) { GameServerKey = 1301 };

    private sealed class Client(Clock clock) : IExchangeMarketClient
    {
        private int _serverCalls;
        public ConcurrentQueue<(int Server, int[] Ids)> QuoteCalls { get; } = new();
        public int ServerCalls => Volatile.Read(ref _serverCalls);
        public Func<int, IReadOnlyCollection<int>, CancellationToken, Task<ImmutableArray<ExchangeMarketQuote>>> Quotes { get; set; } =
            (server, ids, _) => Task.FromResult(ids.Select(id => Quote(clock, id, server)).ToImmutableArray());
        public Func<CancellationToken, Task<ImmutableArray<ExchangeMarketServer>>> Servers { get; set; } =
            _ => Task.FromResult<ImmutableArray<ExchangeMarketServer>>([Server()]);
        public Task<ImmutableArray<ExchangeMarketQuote>> GetQuotesAsync(int serverId, IReadOnlyCollection<int> itemIds, CancellationToken cancellationToken)
        {
            QuoteCalls.Enqueue((serverId, itemIds.ToArray()));
            return Quotes(serverId, itemIds, cancellationToken);
        }
        public Task<ImmutableArray<ExchangeMarketServer>> GetServersAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _serverCalls);
            return Servers(cancellationToken);
        }
    }

    private sealed class Clock(DateTimeOffset? start = null) : TimeProvider
    {
        private long _ticks;
        public override DateTimeOffset GetUtcNow() => (start ?? Start).AddTicks(Interlocked.Read(ref _ticks));
        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    private sealed class CacheDirectory : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "SoulcrestExchangeRefreshTests-" + Guid.NewGuid().ToString("N"));
        public void Dispose()
        {
            var full = Path.GetFullPath(Root);
            if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(full).StartsWith("SoulcrestExchangeRefreshTests-", StringComparison.Ordinal)) throw new InvalidOperationException();
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }
    }
}
