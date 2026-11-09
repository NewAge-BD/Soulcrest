using System.Collections.Concurrent;
using System.Collections.Immutable;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class OverlayEditorMarketHistoryIntegrationTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
    private const int ServerId = 7;
    private const int ItemId = 1; // Synthetic ID; the fixture supplies its own trade facts.
    private const string ProfileId = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
    private static readonly ExchangeMarketProfileSettings Model = new()
        { ServerId = ServerId, Region = "eu", Faction = "elyos", SalesTaxPercent = 10, RegistrationFeePercent = 2 };

    [Fact]
    public async Task ActiveServiceCaptureReadsHistoryFromTheSharedCacheWithoutCreatingACurrentPriceOrDoubleFetching()
    {
        await using var fixture = new Fixture();
        using var lease = fixture.Service.Acquire();
        await fixture.Service.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var snapshot = fixture.Service.Capture(fixture.Session, fixture.Clock.GetUtcNow());
        Assert.True(snapshot.Available);
        Assert.Equal(fixture.Session.Id, snapshot.SessionId);
        Assert.Equal(ProfileId, snapshot.SourceProfileId);
        Assert.Null(snapshot.NetKina);
        Assert.Equal(0, snapshot.PartialNetKina);
        Assert.Equal(35, snapshot.KnownEstimateNetKina); // Floor(4 * 10 * .88).
        Assert.Equal(0, snapshot.PricedRows);
        Assert.Equal(1, snapshot.HistoricalRows);
        Assert.Equal(4, snapshot.HistoricalQuantity);
        Assert.Equal(0, snapshot.UnvaluedRows);
        Assert.Equal(Start.AddHours(-26), snapshot.OldestHistoricalSeenAt);
        Assert.Equal(snapshot.KnownEstimateNetKina, fixture.Service.Snapshot.KnownEstimateNetKina);
        Assert.Null(fixture.Cache.GetQuote(ServerId, ItemId));
        Assert.NotNull(fixture.Cache.GetHistoricalQuote(ServerId, ItemId));
        var request = Assert.Single(fixture.Client.Requests);
        Assert.Equal(ServerId, request.Server);
        Assert.Equal(new[] { ItemId }, request.Items);
        Assert.Equal(0, fixture.Client.LegacyCalls);
        Assert.Equal(0, fixture.Client.ServerCalls);
    }

    [Fact]
    public async Task CurrentPriceAfterTheMissingLookupTtlReplacesTheHistoricalEstimateWithoutDoubleCounting()
    {
        await using var fixture = new Fixture();
        using var lease = fixture.Service.Acquire();
        await fixture.Service.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(fixture.Service.Snapshot.NetKina);
        Assert.Equal(35, fixture.Service.Snapshot.KnownEstimateNetKina);
        Assert.Single(fixture.Client.Requests);

        fixture.Client.CurrentAvailable = true;
        fixture.Clock.Advance(ExchangeMarketCache.MissingQuoteTtl + TimeSpan.FromTicks(1));
        fixture.State = fixture.State with { At = fixture.Clock.GetUtcNow() };
        await fixture.Service.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var snapshot = fixture.Service.Snapshot;
        Assert.Equal(70, snapshot.NetKina); // Floor(4 * 20 * .88), not plus the prior estimate.
        Assert.Equal(70, snapshot.PartialNetKina);
        Assert.Equal(70, snapshot.KnownEstimateNetKina);
        Assert.Equal(1, snapshot.PricedRows);
        Assert.Equal(4, snapshot.PricedQuantity);
        Assert.Equal(0, snapshot.HistoricalRows);
        Assert.Equal(0, snapshot.HistoricalQuantity);
        Assert.Null(snapshot.OldestHistoricalSeenAt);
        Assert.Null(snapshot.NewestHistoricalSeenAt);
        Assert.Equal(0, snapshot.UnvaluedRows);
        Assert.Equal(20m, fixture.Cache.GetQuote(ServerId, ItemId)!.UnitPrice);
        Assert.Equal(10m, fixture.Cache.GetHistoricalQuote(ServerId, ItemId)!.UnitPrice);
        Assert.Equal(2, fixture.Client.Requests.Count);
        Assert.Equal(0, fixture.Client.LegacyCalls);
        Assert.Equal(0, fixture.Client.ServerCalls);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal Clock Clock { get; } = new();
        internal ExchangeMarketCache Cache { get; }
        internal DetailsClient Client { get; }
        internal LootTrackerSession Session { get; } = new()
        {
            Id = "synthetic-market-history-session", Character = new(ProfileId, null),
            Status = LootTrackerSessionStatus.Live, StartedAt = Start.AddMinutes(-10), Elapsed = TimeSpan.FromMinutes(10),
            Loot = [new("Item:1", "Synthetic item", LootTrackerLootKind.Item, 4)],
        };
        internal UnifiedLootOverlayState State { get; set; }
        internal OverlayEditorMarketService Service { get; }

        internal Fixture()
        {
            State = new(Session, true, new(null, null, null, 0), null, false, Start);
            Cache = new(Clock);
            Cache.SaveServers([new(ServerId, "Example market", "eu", "Europe", "elyos", true)]);
            Client = new(Clock);
            Service = new(() => State, _ => Model, () => true, Client, Cache, Clock, _ => new(false, true, false));
        }

        public ValueTask DisposeAsync() => Service.DisposeAsync();
    }

    private sealed record Request(int Server, int[] Items);
    private sealed class DetailsClient(Clock clock) : IExchangeMarketDetailsClient
    {
        private int _legacyCalls, _serverCalls;
        internal ConcurrentQueue<Request> Requests { get; } = new();
        internal bool CurrentAvailable { get; set; }
        internal int LegacyCalls => Volatile.Read(ref _legacyCalls);
        internal int ServerCalls => Volatile.Read(ref _serverCalls);

        public Task<ExchangeMarketQuoteDetails> GetQuoteDetailsAsync(int serverId, IReadOnlyCollection<int> itemIds,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ids = itemIds.ToArray();
            Requests.Enqueue(new(serverId, ids));
            var now = clock.GetUtcNow();
            ImmutableArray<ExchangeMarketQuote> current = CurrentAvailable
                ? ids.Select(id => new ExchangeMarketQuote(serverId, id, 20m, 2, now.AddMinutes(-1), now)).ToImmutableArray() : [];
            var historical = ids.Select(id => new ExchangeMarketHistoricalQuote(serverId, id, 10m, Start.AddHours(-26), now)).ToImmutableArray();
            return Task.FromResult(new ExchangeMarketQuoteDetails(current, historical));
        }

        public Task<ImmutableArray<ExchangeMarketQuote>> GetQuotesAsync(int serverId, IReadOnlyCollection<int> itemIds,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _legacyCalls);
            throw new InvalidOperationException("The cache must use the details capability exactly once.");
        }

        public Task<ImmutableArray<ExchangeMarketServer>> GetServersAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _serverCalls);
            throw new InvalidOperationException("The fixture already has the matching server.");
        }
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override DateTimeOffset GetUtcNow() => Start.AddTicks(Interlocked.Read(ref _ticks));
        internal void Advance(TimeSpan elapsed) => Interlocked.Add(ref _ticks, elapsed.Ticks);
    }
}
