using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class OverlayEditorMarketHistoryIntegrationTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    private const string Profile = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
    private static readonly ExchangeMarketProfileSettings Model = new() { ServerId = 7, Region = "eu", Faction = "elyos" };

    [Fact]
    public async Task MissingCurrentAndAverageNeverUsesLastHistoricalOffersAndWaitsTenMinutesBeforeRetry()
    {
        await using var fixture = new Fixture(4);
        await fixture.Cache.GetQuotesAsync(new LegacyClient(), 7, [1], CancellationToken.None);
        var currentBefore = fixture.Cache.GetQuote(7, 1);
        var historyBefore = fixture.Cache.GetHistoricalQuote(7, 1);
        var sessionBefore = JsonSerializer.Serialize(fixture.Session);
        using var lease = fixture.Service.Acquire();
        await fixture.Service.RefreshAsync();
        Assert.Null(fixture.Service.Snapshot.NetKina);
        Assert.Null(fixture.Service.Snapshot.KnownEstimateNetKina);
        Assert.Equal(0, fixture.Service.Snapshot.HistoricalRows);
        Assert.False(fixture.Service.Snapshot.Uses24HourAverage);
        Assert.True(fixture.Service.Snapshot.AverageFallbackEnabled);
        Assert.Equal(1, fixture.Client.Calls);
        fixture.Clock.Advance(ExchangeMarketCache.MissingQuoteTtl + TimeSpan.FromTicks(1));
        await fixture.Service.RefreshAsync();
        Assert.Equal(1, fixture.Client.Calls);
        fixture.Client.Price = 10;
        fixture.Clock.Advance(ExchangeMarketCache.FetchTtl);
        await fixture.Service.RefreshAsync();
        Assert.Equal(35, fixture.Service.Snapshot.NetKina);
        Assert.Equal(2, fixture.Client.Calls);
        Assert.Equal(currentBefore, fixture.Cache.GetQuote(7, 1));
        Assert.Equal(historyBefore, fixture.Cache.GetHistoricalQuote(7, 1));
        Assert.Equal(sessionBefore, JsonSerializer.Serialize(fixture.Session));
        Assert.Equal(1, fixture.Client.LegacyCalls);
    }

    [Fact]
    public async Task SlowHistoryPublishesEachPartialValueAndMultipleConsumersShareTheRequests()
    {
        await using var fixture = new Fixture(1, 3);
        var started = Enumerable.Range(0, 3).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var release = Enumerable.Range(0, 3).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var number = -1;
        fixture.Client.Reply = async (server, id) =>
        {
            var index = Interlocked.Increment(ref number);
            started[index].SetResult(); await release[index].Task;
            return [Average(server, id, (index + 1) * 10, fixture.Clock.GetUtcNow())];
        };
        var delivered = new ConcurrentQueue<int>();
        fixture.Service.Changed += () => delivered.Enqueue(fixture.Service.Snapshot.PricedRows);
        using var first = fixture.Service.Acquire();
        using var second = fixture.Service.Acquire();
        await started[0].Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(fixture.Service.Snapshot.NetKina);
        release[0].SetResult(); await started[1].Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, fixture.Service.Snapshot.PricedRows);
        Assert.Equal(8, fixture.Service.Snapshot.PartialNetKina);
        release[1].SetResult(); await started[2].Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, fixture.Service.Snapshot.PricedRows);
        Assert.Equal(25, fixture.Service.Snapshot.PartialNetKina);
        release[2].SetResult(); await fixture.Service.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(51, fixture.Service.Snapshot.NetKina);
        Assert.Equal(3, fixture.Client.Calls);
        Assert.Contains(1, delivered); Assert.Contains(2, delivered); Assert.Contains(3, delivered);
        Assert.Equal(1, fixture.Client.LegacyCalls);
    }

    private static ExchangeMarketAveragePrice Average(int server, int id, decimal price, DateTimeOffset now) =>
        new(server, id, price, 12, now.AddHours(-24), now, now);

    private sealed class Fixture : IAsyncDisposable
    {
        internal Clock Clock { get; } = new();
        internal ExchangeMarketCache Cache { get; }
        internal AverageClient Client { get; }
        internal LootTrackerSession Session { get; }
        internal OverlayEditorMarketService Service { get; }
        internal Fixture(int quantity, int items = 1)
        {
            Session = new() { Id = "synthetic-average-session", Character = new(Profile, null), Status = LootTrackerSessionStatus.Live,
                StartedAt = Start.AddMinutes(-10), Elapsed = TimeSpan.FromMinutes(10),
                Loot = [.. Enumerable.Range(1, items).Select(id => new LootTrackerLoot("Item:" + id, "Synthetic item", LootTrackerLootKind.Item, quantity))] };
            Cache = new(Clock); Cache.SaveServers([new(7, "Synthetic market", "eu", "Europe", "elyos", true)]);
            Client = new(Clock);
            Service = new(() => new(Session, true, new(null, null, null, 0), null, false, Clock.GetUtcNow()),
                _ => Model, () => true, Client, Cache, Clock, _ => new(false, true, false));
        }
        public ValueTask DisposeAsync() => Service.DisposeAsync();
    }

    private sealed class AverageClient(Clock clock) : IExchangeMarketClient, IExchangeMarketAverageClient
    {
        internal int Calls, LegacyCalls;
        internal decimal? Price;
        internal Func<int, int, Task<ImmutableArray<ExchangeMarketAveragePrice>>>? Reply;
        public Task<ImmutableArray<ExchangeMarketAveragePrice>> GetAveragePricesAsync(int server, IReadOnlyCollection<int> ids, CancellationToken token)
        {
            Interlocked.Increment(ref Calls); var id = Assert.Single(ids);
            return Reply?.Invoke(server, id) ?? Task.FromResult<ImmutableArray<ExchangeMarketAveragePrice>>(Price is { } price ? [Average(server, id, price, clock.GetUtcNow())] : []);
        }
        public Task<ImmutableArray<ExchangeMarketQuote>> GetQuotesAsync(int server, IReadOnlyCollection<int> ids, CancellationToken token)
        { Interlocked.Increment(ref LegacyCalls); return Task.FromResult(ImmutableArray<ExchangeMarketQuote>.Empty); }
        public Task<ImmutableArray<ExchangeMarketServer>> GetServersAsync(CancellationToken token) =>
            throw new InvalidOperationException("The fixture already has a server.");
    }

    private sealed class LegacyClient : IExchangeMarketDetailsClient
    {
        public Task<ExchangeMarketQuoteDetails> GetQuoteDetailsAsync(int server, IReadOnlyCollection<int> ids, CancellationToken token) =>
            Task.FromResult(new ExchangeMarketQuoteDetails([],
                [new(server, 1, 11_000_321, Start.AddHours(-9), Start)]));
        public Task<ImmutableArray<ExchangeMarketQuote>> GetQuotesAsync(int server, IReadOnlyCollection<int> ids, CancellationToken token) => throw new InvalidOperationException();
        public Task<ImmutableArray<ExchangeMarketServer>> GetServersAsync(CancellationToken token) => throw new InvalidOperationException();
    }
    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override DateTimeOffset GetUtcNow() => Start.AddTicks(Interlocked.Read(ref _ticks));
        internal void Advance(TimeSpan elapsed) => Interlocked.Add(ref _ticks, elapsed.Ticks);
    }
}
