using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class OverlayEditorMarketTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private static ExchangeMarketProfileSettings Model(int server = 7, decimal tax = 10, decimal fee = 2) =>
        new() { ServerId = server, Region = "eu", Faction = "elyos", SalesTaxPercent = tax, RegistrationFeePercent = fee };
    private static LootTrackerLoot Item(int id, long quantity = 1) => new("Item:" + id, "Synthetic item", LootTrackerLootKind.Item, quantity);
    private static LootTrackerSession Session(params LootTrackerLoot[] items) => new()
    {
        Id = Guid.NewGuid().ToString("N"), Character = new(Guid.NewGuid().ToString("N"), null),
        Status = LootTrackerSessionStatus.Live, StartedAt = At, Elapsed = TimeSpan.FromMinutes(10), Loot = [.. items],
    };
    private static UnifiedLootOverlayState State(LootTrackerSession session) => new(session, true, new(null, null, null, 0), null, false, At);

    [Fact]
    public async Task CaptureIsPureAndRevaluesTheWholeExistingQuantityWithCurrentFeesAndMarket()
    {
        await using var fixture = new Fixture(Session(Item(1, 3)));
        fixture.Cache.PutQuotes([fixture.Quote(1, 10.7m), fixture.Quote(1, 20, server: 8)]);
        Assert.Equal(28, fixture.Service.Capture(fixture.Session, At).NetKina);
        fixture.Model = Model(tax: 0, fee: 0);
        Assert.Equal(32, fixture.Service.Snapshot.NetKina);
        fixture.Model = Model(8, tax: 0, fee: 0);
        var changed = fixture.Service.Snapshot;
        Assert.Equal(60, changed.NetKina);
        Assert.Equal(8, changed.MarketProfile.ServerId);
        Assert.Equal(fixture.Session.Character!.ProfileId, changed.SourceProfileId);
        Assert.Equal("Market 8", changed.ServerName);
        Assert.Empty(fixture.Client.Requests);
    }

    [Fact]
    public async Task ExistingSessionItemsFetchWithoutNewLootReceiptsAndRefreshAfterTheCacheTtl()
    {
        var session = Session(Item(1, 4));
        var original = JsonSerializer.Serialize(session);
        await using var fixture = new Fixture(session);
        using var lease = fixture.Service.Acquire();
        await fixture.Service.RefreshAsync();
        Assert.Equal(35, fixture.Service.Snapshot.NetKina);
        fixture.Client.Price = 20;
        await fixture.Service.RefreshAsync();
        Assert.Equal(35, fixture.Service.Snapshot.NetKina);
        Assert.Single(fixture.Client.Requests);
        fixture.Clock.Advance(ExchangeMarketCache.FetchTtl + TimeSpan.FromSeconds(1));
        await fixture.Service.RefreshAsync();
        Assert.Equal(70, fixture.Service.Snapshot.NetKina);
        Assert.Equal(2, fixture.Client.Requests.Count);
        Assert.Equal(original, JsonSerializer.Serialize(session));
        Assert.Empty(session.TimelineEvents);
    }

    [Fact]
    public async Task DuplicateConsumersShareRequestsAndLastLeaseStopsAllRefreshes()
    {
        await using var fixture = new Fixture(Session(Item(1)));
        var first = fixture.Service.Acquire();
        var second = fixture.Service.Acquire();
        await fixture.Service.RefreshAsync();
        Assert.Single(fixture.Client.Requests);
        first.Dispose(); first.Dispose();
        fixture.Clock.Advance(ExchangeMarketCache.FetchTtl + TimeSpan.FromSeconds(1));
        await fixture.Service.RefreshAsync();
        Assert.Equal(2, fixture.Client.Requests.Count);
        second.Dispose();
        fixture.Clock.Advance(ExchangeMarketCache.FetchTtl + TimeSpan.FromSeconds(1));
        await fixture.Service.RefreshAsync();
        Assert.Equal(2, fixture.Client.Requests.Count);
    }

    [Fact]
    public async Task NoConsumersAndDisabledOnlineModeNeverFetchEvenWhenPricesAreMissing()
    {
        await using var fixture = new Fixture(Session(Item(1)));
        await fixture.Service.RefreshAsync();
        Assert.Empty(fixture.Client.Requests);
        fixture.Online = false;
        using var lease = fixture.Service.Acquire();
        await fixture.Service.RefreshAsync();
        Assert.Empty(fixture.Client.Requests);
        Assert.Null(fixture.Service.Snapshot.NetKina);
        Assert.False(fixture.Service.Snapshot.OnlineEnabled);
    }

    [Fact]
    public async Task OfflineReadsUsableCacheAndOldSourcePricesRemainUnknown()
    {
        await using var fixture = new Fixture(Session(Item(1, 2))) { Online = false };
        fixture.Cache.PutQuotes([fixture.Quote(1, 10)]);
        fixture.Clock.Advance(TimeSpan.FromMinutes(11));
        var cached = fixture.Service.Snapshot;
        Assert.Equal(17, cached.NetKina);
        Assert.True(cached.Stale);
        fixture.Clock.Advance(TimeSpan.FromHours(2));
        Assert.Null(fixture.Service.Snapshot.NetKina);
        Assert.Equal(2, fixture.Service.Snapshot.UnknownQuantity);
        Assert.Empty(fixture.Client.Requests);
    }

    [Fact]
    public async Task MissingPriceOrUnknownTradeabilityLeavesMainValueUnknownAndKeepsThePricedSubtotal()
    {
        await using var fixture = new Fixture(Session(Item(1, 2), Item(2, 3), Item(3, 4)));
        fixture.Facts = id => new(false, id != 3, id == 2);
        fixture.Cache.PutQuotes([fixture.Quote(1, 10), fixture.Quote(3, 100)]);
        var snapshot = fixture.Service.Snapshot;
        Assert.Null(snapshot.NetKina);
        Assert.Equal(17, snapshot.PartialNetKina);
        Assert.Equal(3, snapshot.EligibleRows);
        Assert.Equal(1, snapshot.PricedRows);
        Assert.Equal(9, snapshot.EligibleQuantity);
        Assert.Equal(7, snapshot.UnknownQuantity);
        Assert.True(snapshot.HasEquipmentReference);
        using var lease = fixture.Service.Acquire();
        await fixture.Service.RefreshAsync();
        Assert.DoesNotContain(fixture.Client.Requests.SelectMany(request => request.Items), id => id == 3);
    }

    [Fact]
    public async Task EquipmentRetainsTheBaseItemReferenceFlagAndItsSeparateSubtotal()
    {
        await using var fixture = new Fixture(Session(Item(1), Item(2)));
        fixture.Facts = id => new(false, true, id == 2);
        fixture.Cache.PutQuotes([fixture.Quote(1, 10), fixture.Quote(2, 101)]);
        var snapshot = fixture.Service.Snapshot;
        Assert.Equal(96, snapshot.NetKina);
        Assert.True(snapshot.HasEquipmentReference);
        Assert.Equal(88, snapshot.EquipmentReferenceNetKina);
    }

    [Fact]
    public async Task KnownNontradeableItemsAndSoulRowsAreExcludedRatherThanBeingPriced()
    {
        await using var fixture = new Fixture(Session(Item(1), Item(2, 100), new("SoulCrystal:1", "Synthetic soul", LootTrackerLootKind.Soul, 5)));
        fixture.Facts = id => new(id == 2, true, false);
        using var lease = fixture.Service.Acquire();
        await fixture.Service.RefreshAsync();
        Assert.Equal(1, fixture.Service.Snapshot.EligibleQuantity);
        Assert.Equal(8, fixture.Service.Snapshot.NetKina);
        Assert.Equal(new[] { 1 }, fixture.Client.Requests.SelectMany(request => request.Items).ToArray());
        Assert.True(OverlayEditorMarketItemFacts.ForItem(210230081).Excluded);
        Assert.True(OverlayEditorMarketItemFacts.ForItem(210230006).Equipment);
        Assert.False(OverlayEditorMarketItemFacts.ForItem(int.MaxValue).TradeKnown);
    }

    [Fact]
    public async Task ConfirmedEmptySessionHasKnownZeroWithoutASelectedMarket()
    {
        await using var fixture = new Fixture(Session());
        fixture.Model = new();
        var snapshot = fixture.Service.Snapshot;
        Assert.True(snapshot.Available);
        Assert.Equal(0, snapshot.NetKina);
        Assert.Equal(0, snapshot.PartialNetKina);
        Assert.Equal(0, snapshot.EligibleRows);
        Assert.Empty(fixture.Client.Requests);
    }

    [Theory]
    [InlineData(false, LootTrackerSessionStatus.Live, false)]
    [InlineData(true, LootTrackerSessionStatus.Completed, false)]
    [InlineData(true, LootTrackerSessionStatus.Live, true)]
    public async Task UnqualifiedOrEndedSessionsNeverExposeCachedOrFetchedValues(bool tracking, LootTrackerSessionStatus status, bool ended)
    {
        var session = Session(Item(1)) with { Status = status, EndedAt = ended ? At : null };
        await using var fixture = new Fixture(session);
        fixture.State = fixture.State with { TrackingAvailable = tracking };
        fixture.Cache.PutQuotes([fixture.Quote(1, 10)]);
        using var lease = fixture.Service.Acquire();
        await fixture.Service.RefreshAsync();
        Assert.False(fixture.Service.Snapshot.Available);
        Assert.Null(fixture.Service.Snapshot.NetKina);
        Assert.False(fixture.Service.Capture(session, At).Available);
        Assert.Empty(fixture.Client.Requests);
    }

    [Theory]
    [InlineData("future-publication")]
    [InlineData("missing-publication")]
    [InlineData("future-start")]
    [InlineData("missing-start")]
    [InlineData("negative-elapsed")]
    [InlineData("missing-profile")]
    [InlineData("malformed-profile")]
    [InlineData("empty-profile")]
    public async Task InvalidTimeOrProfileCannotExposeEvenKnownZeroOrIssueRequests(string invalid)
    {
        var session = Session(Item(1));
        session = invalid switch
        {
            "future-start" => session with { StartedAt = At.AddTicks(1) },
            "missing-start" => session with { StartedAt = default },
            "negative-elapsed" => session with { Elapsed = TimeSpan.FromTicks(-1) },
            "missing-profile" => session with { Character = null },
            "malformed-profile" => session with { Character = new("invalid-profile", null) },
            "empty-profile" => session with { Character = new(Guid.Empty.ToString("N"), null) },
            _ => session,
        };
        await using var fixture = new Fixture(session);
        if (invalid == "future-publication") fixture.State = fixture.State with { At = At.AddTicks(1) };
        if (invalid == "missing-publication") fixture.State = fixture.State with { At = default };
        fixture.Cache.PutQuotes([fixture.Quote(1, 10)]);
        using var lease = fixture.Service.Acquire();
        await fixture.Service.RefreshAsync();
        Assert.False(fixture.Service.Snapshot.Available);
        Assert.Null(fixture.Service.Snapshot.NetKina);
        Assert.False(fixture.Service.Capture(session, At).Available);
        fixture.State = fixture.State with { Session = fixture.Session with { Loot = [] } };
        Assert.Null(fixture.Service.Snapshot.NetKina);
        Assert.Empty(fixture.Client.Requests);
    }

    [Fact]
    public async Task EarlierFrameInstantDoesNotRejectAConcurrentQualifiedPublication()
    {
        await using var fixture = new Fixture(Session(Item(1, 2)));
        fixture.Cache.PutQuotes([fixture.Quote(1, 10)]);
        var frame = fixture.Session;
        var frameInstant = fixture.Clock.GetUtcNow();
        fixture.Clock.Advance(TimeSpan.FromTicks(1));
        fixture.State = fixture.State with { At = fixture.Clock.GetUtcNow() };
        var captured = fixture.Service.Capture(frame, frameInstant);
        Assert.True(captured.Available);
        Assert.Equal(frame.Id, captured.SessionId);
        Assert.Equal(17, captured.NetKina);
        Assert.Empty(fixture.Client.Requests);
    }

    [Theory]
    [InlineData("session")]
    [InlineData("profile")]
    [InlineData("market")]
    [InlineData("fees")]
    [InlineData("disabled")]
    public async Task LateResponsesCannotPublishThePreviousSessionProfileMarketOrFeeBasis(string change)
    {
        await using var fixture = new Fixture(Session(Item(1)));
        var oldSession = fixture.Session;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var number = 0;
        fixture.Client.Reply = async request =>
        {
            if (Interlocked.Increment(ref number) == 1) { started.SetResult(); await release.Task; }
            return [fixture.Quote(1, 20, request.Server)];
        };
        using var lease = fixture.Service.Acquire();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (change == "session") fixture.State = State(Session(Item(1, 2)));
        if (change == "profile") fixture.State = State(oldSession with { Character = new(Guid.NewGuid().ToString("N"), null) });
        if (change == "market") fixture.Model = Model(8);
        if (change == "fees") fixture.Model = Model(tax: 0, fee: 0);
        if (change == "disabled") fixture.Online = false;
        var refreshed = fixture.Service.RefreshAsync();
        release.SetResult();
        await refreshed.WaitAsync(TimeSpan.FromSeconds(5));
        var current = fixture.Service.Snapshot;
        Assert.Equal(fixture.Session.Id, current.SessionId);
        Assert.Equal(fixture.Session.Character!.ProfileId, current.SourceProfileId);
        Assert.Equal(fixture.Model, current.MarketProfile);
        if (change == "disabled") { Assert.Null(current.NetKina); Assert.Null(fixture.Cache.GetQuote(7, 1)); }
        else Assert.Equal(change == "session" ? 35 : change == "fees" ? 20 : 17, current.NetKina);
        if (change is "session" or "profile") Assert.False(fixture.Service.Capture(oldSession, fixture.Clock.GetUtcNow()).Available);
    }

    [Fact]
    public async Task LastConsumerCancellationDiscardsItsInFlightResponse()
    {
        await using var fixture = new Fixture(Session(Item(1)));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Client.Reply = async request => { started.SetResult(); await release.Task; return [fixture.Quote(1, 100, request.Server)]; };
        var lease = fixture.Service.Acquire();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        lease.Dispose(); release.SetResult();
        await fixture.Service.RefreshAsync();
        Assert.Null(fixture.Cache.GetQuote(7, 1));
        Assert.Null(fixture.Service.Snapshot.NetKina);
    }

    [Fact]
    public async Task BatchesAreBoundedAndValidAbsencesAreNotRetriedByEveryConsumerOrTick()
    {
        await using var fixture = new Fixture(Session(Enumerable.Range(1, 205).Select(id => Item(id)).ToArray()));
        fixture.Client.Reply = _ => Task.FromResult(ImmutableArray<ExchangeMarketQuote>.Empty);
        using var first = fixture.Service.Acquire();
        using var second = fixture.Service.Acquire();
        await fixture.Service.RefreshAsync();
        Assert.Equal(new[] { 100, 100, 5 }, fixture.Client.Requests.Select(request => request.Items.Length).ToArray());
        await fixture.Service.RefreshAsync();
        Assert.Equal(3, fixture.Client.Requests.Count);
        Assert.Equal(205, fixture.Service.Snapshot.UnknownQuantity);
    }

    [Fact]
    public async Task RoundingAppliesOnceToTheEntireQuantityOfEachItemAndOverflowStaysUnknown()
    {
        await using var fixture = new Fixture(Session(Item(1), Item(1)));
        fixture.Cache.PutQuotes([fixture.Quote(1, 1.1m)]);
        Assert.Equal(1, fixture.Service.Snapshot.NetKina);
        Assert.Equal(1, fixture.Service.Snapshot.PricedRows);
        fixture.State = State(Session(Item(1, long.MaxValue), Item(1)));
        var overflow = fixture.Service.Snapshot;
        Assert.Null(overflow.NetKina);
        Assert.Null(overflow.PartialNetKina);
        Assert.True(overflow.StatusIsError);
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = At;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }

    private sealed record Request(int Server, int[] Items);
    private sealed class Client(Clock clock) : IExchangeMarketClient
    {
        internal ConcurrentQueue<Request> Requests { get; } = new();
        internal decimal Price { get; set; } = 10;
        internal Func<Request, Task<ImmutableArray<ExchangeMarketQuote>>>? Reply { get; set; }
        public Task<ImmutableArray<ExchangeMarketServer>> GetServersAsync(CancellationToken cancellationToken) =>
            Task.FromResult<ImmutableArray<ExchangeMarketServer>>([new(7, "Market 7", "eu", "Europe", "elyos", true), new(8, "Market 8", "eu", "Europe", "elyos", true)]);
        public Task<ImmutableArray<ExchangeMarketQuote>> GetQuotesAsync(int serverId, IReadOnlyCollection<int> itemIds, CancellationToken cancellationToken)
        {
            var request = new Request(serverId, itemIds.ToArray()); Requests.Enqueue(request);
            return Reply?.Invoke(request) ?? Task.FromResult(request.Items.Select(id =>
                new ExchangeMarketQuote(serverId, id, Price, 2, clock.GetUtcNow().AddMinutes(-1), clock.GetUtcNow())).ToImmutableArray());
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal Clock Clock { get; } = new();
        internal ExchangeMarketCache Cache { get; }
        internal Client Client { get; }
        internal ExchangeMarketProfileSettings Model { get; set; } = OverlayEditorMarketTests.Model();
        internal bool Online { get; set; } = true;
        internal UnifiedLootOverlayState State { get; set; }
        internal LootTrackerSession Session => State.Session!;
        // Synthetic IDs deliberately have an injected classification; they assert no game facts.
        internal Func<int, OverlayEditorMarketItemFacts> Facts { get; set; } = _ => new(false, true, false);
        internal OverlayEditorMarketService Service { get; }
        internal Fixture(LootTrackerSession session)
        {
            State = OverlayEditorMarketTests.State(session); Cache = new(Clock); Client = new(Clock);
            Cache.SaveServers([new(7, "Market 7", "eu", "Europe", "elyos", true), new(8, "Market 8", "eu", "Europe", "elyos", true)]);
            Service = new(() => State, _ => Model, () => Online, Client, Cache, Clock, id => Facts(id));
        }
        internal ExchangeMarketQuote Quote(int id, decimal price, int server = 7) => new(server, id, price, 2, Clock.GetUtcNow().AddMinutes(-1), Clock.GetUtcNow());
        public ValueTask DisposeAsync() => Service.DisposeAsync();
    }
}
