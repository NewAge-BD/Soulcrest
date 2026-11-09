using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class ExchangeMarketHistoricalCacheTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
    private const int ServerId = 11301;

    [Fact]
    public async Task DetailsCapabilityUsesOneCallPerBatchAndReturnsOnlyCurrentQuotes()
    {
        var clock = new Clock();
        var cache = new ExchangeMarketCache(clock);
        var client = new DetailsClient
        {
            Reply = (server, ids, _) => Task.FromResult(new ExchangeMarketQuoteDetails(
                ids.Select(id => Current(clock, id, server)).ToImmutableArray(),
                ids.Select(id => Historical(clock, id, server)).ToImmutableArray()))
        };
        var result = await cache.GetQuotesAsync(client, ServerId, Enumerable.Range(1, 101).ToArray(), CancellationToken.None);
        Assert.Equal(101, result.Length);
        Assert.Equal(new[] { 100, 1 }, client.Calls.Select(call => call.Ids.Length));
        Assert.Equal(0, client.CurrentCalls);
        Assert.Equal(12.5m, cache.GetQuote(ServerId, 1)!.UnitPrice);
        Assert.Equal(8.25m, cache.GetHistoricalQuote(ServerId, 1)!.UnitPrice);
        Assert.Null(cache.GetHistoricalQuote(ServerId + 1, 1));
        Assert.Null(cache.GetHistoricalQuote(ServerId, 102));
    }

    [Fact]
    public async Task HistoricalObservationPersistsWithoutAnAgeLimitButNeverBecomesCurrent()
    {
        using var directory = new CacheDirectory();
        var clock = new Clock();
        var history = Historical(clock, 1) with { LastSeenAt = Start.AddYears(-2) };
        var client = new DetailsClient { Reply = (_, _, _) => Task.FromResult(new ExchangeMarketQuoteDetails([], [history])) };
        var cache = new ExchangeMarketCache(clock, directory.Root);
        Assert.Empty(await cache.GetQuotesAsync(client, ServerId, [1], CancellationToken.None));
        clock.Advance(TimeSpan.FromDays(365));
        var restored = new ExchangeMarketCache(clock, directory.Root);
        Assert.Equal(history, restored.GetHistoricalQuote(ServerId, 1));
        Assert.Null(restored.GetQuote(ServerId, 1));
        Assert.False(restored.IsQuoteLookupFresh(ServerId, 1, clock.GetUtcNow()));
        using var file = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.Root, "exchange-market-cache.json")));
        Assert.Equal(1, file.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.Equal("aion2.exchange", file.RootElement.GetProperty("Provider").GetString());
        Assert.Empty(file.RootElement.GetProperty("Quotes").EnumerateArray());
        Assert.Single(file.RootElement.GetProperty("HistoricalQuotes").EnumerateArray());
        Assert.DoesNotContain("Profile", file.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Character", file.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HistoricalPresenceDoesNotExtendMissingQuoteTtlOrBypassSharedBackoff()
    {
        var clock = new Clock();
        var cache = new ExchangeMarketCache(clock);
        var client = new DetailsClient { Reply = (_, _, _) => Task.FromResult(new ExchangeMarketQuoteDetails([], [Historical(clock, 1)])) };
        await cache.GetQuotesAsync(client, ServerId, [1], CancellationToken.None);
        clock.Advance(ExchangeMarketCache.MissingQuoteTtl);
        Assert.False(cache.IsQuoteLookupFresh(ServerId, 1, clock.GetUtcNow()));
        client.Reply = (_, _, _) => Task.FromException<ExchangeMarketQuoteDetails>(new HttpRequestException("offline"));
        await Assert.ThrowsAsync<ExchangeMarketApiException>(() => cache.GetQuotesAsync(client, ServerId, [1], CancellationToken.None));
        Assert.NotNull(cache.GetHistoricalQuote(ServerId, 1));
        await Assert.ThrowsAsync<ExchangeMarketApiException>(() => cache.GetQuotesAsync(client, ServerId, [2], CancellationToken.None));
        Assert.Equal(2, client.Calls.Count);
        clock.Advance(TimeSpan.FromSeconds(30));
        client.Reply = (_, _, _) => Task.FromResult(new ExchangeMarketQuoteDetails([Current(clock, 1)], []));
        Assert.Single(await cache.GetQuotesAsync(client, ServerId, [1], CancellationToken.None));
        Assert.Equal(3, client.Calls.Count);
    }

    [Fact]
    public async Task OlderObservationsAndOlderFetchesCannotReplaceTheLatestHistoricalBasis()
    {
        var clock = new Clock();
        var cache = new ExchangeMarketCache(clock);
        var initial = Historical(clock, 1);
        var next = initial;
        var client = new DetailsClient { Reply = (_, _, _) => Task.FromResult(new ExchangeMarketQuoteDetails([], [next])) };
        await cache.GetQuotesAsync(client, ServerId, [1], CancellationToken.None);
        clock.Advance(ExchangeMarketCache.MissingQuoteTtl);
        next = initial with { UnitPrice = 99, LastSeenAt = initial.LastSeenAt.AddMinutes(-1), FetchedAt = clock.GetUtcNow() };
        await cache.GetQuotesAsync(client, ServerId, [1], CancellationToken.None);
        Assert.Equal(initial, cache.GetHistoricalQuote(ServerId, 1));
        clock.Advance(ExchangeMarketCache.MissingQuoteTtl);
        next = initial with { UnitPrice = 98, FetchedAt = initial.FetchedAt.AddMinutes(-1) };
        await cache.GetQuotesAsync(client, ServerId, [1], CancellationToken.None);
        Assert.Equal(initial, cache.GetHistoricalQuote(ServerId, 1));
        clock.Advance(ExchangeMarketCache.MissingQuoteTtl);
        next = initial with { UnitPrice = 9, LastSeenAt = initial.LastSeenAt.AddMinutes(1), FetchedAt = clock.GetUtcNow() };
        await cache.GetQuotesAsync(client, ServerId, [1], CancellationToken.None);
        Assert.Equal(next, cache.GetHistoricalQuote(ServerId, 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task InvalidHistoricalResponseIdentityOrShapeDoesNotCommitEitherArray(int invalid)
    {
        var clock = new Clock();
        var cache = new ExchangeMarketCache(clock);
        var quote = Historical(clock, 1);
        ImmutableArray<ExchangeMarketHistoricalQuote> history = invalid switch
        {
            0 => [quote with { ServerId = ServerId + 1 }],
            1 => [quote with { ItemId = 3 }],
            2 => [quote, quote],
            3 => default,
            _ => [null!]
        };
        var client = new DetailsClient { Reply = (_, _, _) => Task.FromResult(new ExchangeMarketQuoteDetails([Current(clock, 1)], history)) };
        await Assert.ThrowsAsync<ExchangeMarketApiException>(() => cache.GetQuotesAsync(client, ServerId, [1, 2], CancellationToken.None));
        Assert.Null(cache.GetQuote(ServerId, 1));
        Assert.Null(cache.GetHistoricalQuote(ServerId, 1));
        Assert.False(cache.IsQuoteLookupFresh(ServerId, 1, clock.GetUtcNow()));
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
    public void HistoricalUsabilityRequiresRealPositiveObservationsAndCoherentTimes(int invalid)
    {
        var quote = Historical(new Clock(), 1);
        var bad = invalid switch
        {
            0 => quote with { ServerId = 0 },
            1 => quote with { ItemId = 0 },
            2 => quote with { UnitPrice = 0 },
            3 => quote with { UnitPrice = -1 },
            4 => quote with { LastSeenAt = DateTimeOffset.UnixEpoch },
            5 => quote with { FetchedAt = DateTimeOffset.UnixEpoch },
            6 => quote with { LastSeenAt = Start.Add(ExchangeMarketCache.FutureSkew).AddTicks(1) },
            _ => quote with { FetchedAt = Start.Add(ExchangeMarketCache.FutureSkew).AddTicks(1) }
        };
        Assert.False(ExchangeMarketCache.HistoryIsUsable(bad, Start));
        Assert.False(ExchangeMarketCache.HistoryIsUsable(quote with { LastSeenAt = Start, FetchedAt = Start.AddMinutes(-3) }, Start));
        Assert.True(ExchangeMarketCache.HistoryIsUsable(quote with { LastSeenAt = Start.AddYears(-10), FetchedAt = Start.AddYears(-9) }, Start));
    }

    [Fact]
    public async Task CanceledLateDetailsNeverPublishAndDoNotCreateFailureBackoff()
    {
        var clock = new Clock();
        var cache = new ExchangeMarketCache(clock);
        var response = new TaskCompletionSource<ExchangeMarketQuoteDetails>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new DetailsClient { Reply = (_, _, _) => response.Task };
        using var cancellation = new CancellationTokenSource();
        var fetch = cache.GetQuotesAsync(client, ServerId, [1], cancellation.Token);
        cancellation.Cancel();
        response.SetResult(new([Current(clock, 1)], [Historical(clock, 1)]));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch);
        Assert.Null(cache.GetQuote(ServerId, 1));
        Assert.Null(cache.GetHistoricalQuote(ServerId, 1));
        Assert.False(cache.IsQuoteLookupFresh(ServerId, 1, clock.GetUtcNow()));
        client.Reply = (_, _, _) => Task.FromResult(new ExchangeMarketQuoteDetails([Current(clock, 1)], [Historical(clock, 1)]));
        Assert.Single(await cache.GetQuotesAsync(client, ServerId, [1], CancellationToken.None));
        Assert.Equal(2, client.Calls.Count);
    }

    [Fact]
    public async Task OriginalClientsContinueUsingOnlyTheirCurrentQuoteMethod()
    {
        var clock = new Clock();
        var cache = new ExchangeMarketCache(clock);
        var client = new CurrentClient(clock);
        Assert.Single(await cache.GetQuotesAsync(client, ServerId, [1], CancellationToken.None));
        Assert.Equal(1, client.Calls);
        Assert.Null(cache.GetHistoricalQuote(ServerId, 1));
    }

    [Fact]
    public void OriginalSchemaOneFileWithoutHistoryStillRestoresCurrentQuotes()
    {
        using var directory = new CacheDirectory();
        Directory.CreateDirectory(directory.Root);
        var clock = new Clock();
        File.WriteAllText(Path.Combine(directory.Root, "exchange-market-cache.json"), JsonSerializer.Serialize(new
        {
            SchemaVersion = 1, Provider = "aion2.exchange", ServersFetchedAt = (DateTimeOffset?)null,
            Servers = Array.Empty<ExchangeMarketServer>(), Quotes = new[] { Current(clock, 1) }
        }));
        var cache = new ExchangeMarketCache(clock, directory.Root);
        Assert.NotNull(cache.GetQuote(ServerId, 1));
        Assert.Null(cache.GetHistoricalQuote(ServerId, 1));
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
    [InlineData(8)]
    public async Task InvalidOptionalHistoryNeverDiscardsValidCurrentPricesOrLookupThrottling(int invalid)
    {
        using var directory = new CacheDirectory();
        Directory.CreateDirectory(directory.Root);
        var clock = new Clock();
        var quote = Historical(clock, 1);
        object? history = invalid switch
        {
            0 => null,
            1 => new object?[] { null },
            2 => new[] { quote, quote },
            3 => "invalid shape",
            4 => new { WrongShape = true },
            5 => new[] { new { ServerId, ItemId = 1, UnitPrice = "not a number", quote.LastSeenAt, quote.FetchedAt } },
            6 => new[] { quote with { LastSeenAt = Start.Add(ExchangeMarketCache.FutureSkew).AddTicks(1) } },
            7 => new[] { quote with { UnitPrice = 0 } },
            _ => Enumerable.Range(1, 20_001).Select(id => quote with { ItemId = id }).ToArray()
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            SchemaVersion = 1, Provider = "aion2.exchange", ServersFetchedAt = Start,
            Servers = new[] { new ExchangeMarketServer(ServerId, "Example", "eu", "Europe", "elyos", true) },
            Quotes = new[] { Current(clock, 1) },
            Lookups = new[] { new { ServerId, ItemId = 2, FetchedAt = Start } },
            HistoricalQuotes = history
        });
        Assert.True(bytes.Length < 8 * 1024 * 1024);
        File.WriteAllBytes(Path.Combine(directory.Root, "exchange-market-cache.json"), bytes);
        var cache = new ExchangeMarketCache(clock, directory.Root);
        var client = new CurrentClient(clock);
        Assert.Equal(Current(clock, 1), Assert.Single(await cache.GetQuotesAsync(client, ServerId, [1, 2], CancellationToken.None)));
        Assert.Equal(0, client.Calls);
        Assert.True(cache.IsQuoteLookupFresh(ServerId, 2, Start));
        Assert.Single(cache.GetServers());
        Assert.Equal(Start, cache.ServersFetchedAt);
        Assert.Null(cache.GetHistoricalQuote(ServerId, 1));
        Assert.Null(cache.PersistenceError);
    }

    [Fact]
    public void HistoricalPricesFromAnotherProviderNeverLoadIntoTheExchangeCache()
    {
        using var directory = new CacheDirectory();
        Directory.CreateDirectory(directory.Root);
        var clock = new Clock();
        File.WriteAllText(Path.Combine(directory.Root, "exchange-market-cache.json"), JsonSerializer.Serialize(new
        {
            SchemaVersion = 1, Provider = "own-server-market", ServersFetchedAt = (DateTimeOffset?)null,
            Servers = Array.Empty<ExchangeMarketServer>(), Quotes = Array.Empty<ExchangeMarketQuote>(),
            HistoricalQuotes = new[] { Historical(clock, 1) }
        }));
        var cache = new ExchangeMarketCache(clock, directory.Root);
        Assert.Null(cache.GetHistoricalQuote(ServerId, 1));
        Assert.Null(cache.GetQuote(ServerId, 1));
        Assert.False(cache.IsQuoteLookupFresh(ServerId, 1, clock.GetUtcNow()));
    }

    [Fact]
    public async Task FrozenReceiptRemainsPendingWhenTheOnlyAvailablePriceIsHistorical()
    {
        using var directory = new CacheDirectory();
        var clock = new Clock();
        var cache = new ExchangeMarketCache(clock);
        cache.SaveServers([new(ServerId, "Example", "eu", "Europe", "elyos", true)]);
        var item = 610530001;
        var client = new DetailsClient { Reply = (_, _, _) => Task.FromResult(new ExchangeMarketQuoteDetails([], [Historical(clock, item)])) };
        var source = new ReceiptSource(clock);
        await using var valuation = new ExchangeLootValuationService(() => true, source, client, cache, new(directory.Root), clock);
        await valuation.FlushAsync();
        source.Commit(item);
        await valuation.FlushAsync();
        var entry = Assert.Single(valuation.Snapshot.Entries);
        Assert.Equal(ExchangeLootValueStatus.Pending, entry.Status);
        Assert.Null(entry.NetKina);
        Assert.Null(entry.UnitPrice);
        Assert.Null(entry.SourceCapturedAt);
        Assert.NotNull(cache.GetHistoricalQuote(ServerId, item));
        Assert.Null(cache.GetQuote(ServerId, item));
        Assert.Single(client.Calls);
    }

    private static ExchangeMarketQuote Current(Clock clock, int item, int server = ServerId) =>
        new(server, item, 12.5m, 2, clock.GetUtcNow().AddMinutes(-1), clock.GetUtcNow());
    private static ExchangeMarketHistoricalQuote Historical(Clock clock, int item, int server = ServerId) =>
        new(server, item, 8.25m, clock.GetUtcNow().AddDays(-7), clock.GetUtcNow());

    private sealed class DetailsClient : IExchangeMarketDetailsClient
    {
        public ConcurrentQueue<(int Server, int[] Ids)> Calls { get; } = new();
        public int CurrentCalls { get; private set; }
        public Func<int, IReadOnlyCollection<int>, CancellationToken, Task<ExchangeMarketQuoteDetails>> Reply { get; set; } =
            (_, _, _) => Task.FromResult(new ExchangeMarketQuoteDetails([], []));
        public Task<ExchangeMarketQuoteDetails> GetQuoteDetailsAsync(int serverId, IReadOnlyCollection<int> itemIds, CancellationToken cancellationToken)
        {
            Calls.Enqueue((serverId, itemIds.ToArray()));
            return Reply(serverId, itemIds, cancellationToken);
        }
        public Task<ImmutableArray<ExchangeMarketQuote>> GetQuotesAsync(int serverId, IReadOnlyCollection<int> itemIds, CancellationToken cancellationToken)
        {
            CurrentCalls++;
            throw new InvalidOperationException("The details path must not call the legacy method as well.");
        }
        public Task<ImmutableArray<ExchangeMarketServer>> GetServersAsync(CancellationToken cancellationToken) => Task.FromResult(ImmutableArray<ExchangeMarketServer>.Empty);
    }

    private sealed class CurrentClient(Clock clock) : IExchangeMarketClient
    {
        public int Calls { get; private set; }
        public Task<ImmutableArray<ExchangeMarketQuote>> GetQuotesAsync(int serverId, IReadOnlyCollection<int> itemIds, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(itemIds.Select(id => Current(clock, id, serverId)).ToImmutableArray());
        }
        public Task<ImmutableArray<ExchangeMarketServer>> GetServersAsync(CancellationToken cancellationToken) => Task.FromResult(ImmutableArray<ExchangeMarketServer>.Empty);
    }

    private sealed class ReceiptSource(Clock clock) : ICommittedLootFactSource
    {
        private readonly string _profile = Guid.NewGuid().ToString("N");
        private LootTrackerSession _session = new() { Id = Guid.NewGuid().ToString("N"), StartedAt = Start, TimelineVersion = 1, UnifiedVersion = 1 };
        public LootTrackerSnapshot Snapshot => new([_session], clock.GetUtcNow());
        public event Action? Changed;
        public event Action<CommittedLootFact>? LootFactCommitted;
        public event Action<string>? SessionDeleted { add { } remove { } }
        public void Commit(int item)
        {
            var row = new LootTrackerLoot("Item:" + item, "Example material", LootTrackerLootKind.Item, 1);
            var fact = new LootTrackerTimelineEvent(Guid.NewGuid().ToString("N"), LootTrackerTimelineEventKind.Drop, clock.GetUtcNow(), TimeSpan.Zero)
                { Loot = [row], Origin = LootTrackerActivityOrigin.Pve };
            _session = _session with { Character = new(_profile, "Example profile"), Loot = [row], TimelineEvents = [fact], ItemCount = 1, LastLootAt = fact.At };
            LootFactCommitted?.Invoke(new(_session.Id, _profile, fact, new() { ServerId = ServerId, Region = "eu", Faction = "elyos", SalesTaxPercent = 10, RegistrationFeePercent = 2 }));
            Changed?.Invoke();
        }
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override DateTimeOffset GetUtcNow() => Start.AddTicks(Interlocked.Read(ref _ticks));
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _ticks, elapsed.Ticks);
    }

    private sealed class CacheDirectory : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "SoulcrestHistoricalCacheTests-" + Guid.NewGuid().ToString("N"));
        public void Dispose()
        {
            var full = Path.GetFullPath(Root);
            if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(full).StartsWith("SoulcrestHistoricalCacheTests-", StringComparison.Ordinal)) throw new InvalidOperationException();
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }
    }
}
