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
        fixture.Cache.PutAveragePrices([fixture.Quote(1, 10.7m), fixture.Quote(1, 20, server: 8)]);
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

    [Theory]
    [InlineData(true, 1301, true)]
    [InlineData(true, 2301, false)]
    [InlineData(true, null, false)]
    [InlineData(false, 2301, true)]
    public async Task AnAutomaticNativeMappingMustStillMatchTheProviderCatalogue(bool automatic, int? catalogueKey, bool valued)
    {
        await using var fixture = new Fixture(Session(Item(1, 2)));
        fixture.Model = Model() with { AutoDetectServer = automatic, GameServerKey = 1301 };
        var server = new ExchangeMarketServer(7, "Market 7", "eu", "Europe", "elyos", true) { GameServerKey = catalogueKey };
        fixture.Cache.SaveServers([server]);
        fixture.Cache.PutAveragePrices([fixture.Quote(1, 10)]);
        var live = fixture.Service.Snapshot;
        Assert.Equal(valued ? 17L : (long?)null, live.NetKina);
        var direct = OverlayEditorMarketProjection.Project(fixture.Session, fixture.Model, true, server,
            id => fixture.Cache.GetAveragePrice(7, id), At, _ => new(false, true, false));
        Assert.Equal(live.NetKina, direct.NetKina);
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
    public async Task CurrentPricesAreFetchedInSharedHundredItemBatchesWithoutHistoryRequests()
    {
        await using var fixture = new Fixture(Session(Enumerable.Range(1, 205).Select(id => Item(id)).ToArray()));
        fixture.Client.CurrentPrice = 20;
        using var first = fixture.Service.Acquire();
        using var second = fixture.Service.Acquire();
        await fixture.Service.RefreshAsync();
        Assert.Equal(new[] { 100, 100, 5 }, fixture.Client.CurrentRequests.Select(request => request.Items.Length).ToArray());
        Assert.Empty(fixture.Client.Requests);
        Assert.Equal(205, fixture.Service.Snapshot.CurrentRows);
        Assert.Equal(0, fixture.Service.Snapshot.AverageRows);
        Assert.False(fixture.Service.Snapshot.Uses24HourAverage);
        Assert.True(fixture.Service.Snapshot.AverageFallbackEnabled);
        Assert.Equal(205 * 17, fixture.Service.Snapshot.NetKina);
        await fixture.Service.RefreshAsync();
        Assert.Equal(3, fixture.Client.CurrentRequests.Count);
        Assert.Empty(fixture.Client.Requests);
    }

    [Fact]
    public async Task CurrentOfferClientsNeedNoAverageCapabilityWhenAllPricesAreAvailable()
    {
        var session = Session(Item(1, 2));
        var clock = new Clock();
        var cache = new ExchangeMarketCache(clock);
        cache.SaveServers([new(7, "Synthetic market", "eu", "Europe", "elyos", true)]);
        var client = new CurrentOnlyClient(clock);
        await using var service = new OverlayEditorMarketService(() => State(session), _ => Model(), () => true,
            client, cache, clock, _ => new(false, true, false));
        using var lease = service.Acquire();
        await service.RefreshAsync();
        Assert.Equal(17, service.Snapshot.NetKina);
        Assert.Equal(1, service.Snapshot.CurrentRows);
        Assert.False(service.Snapshot.StatusIsError);
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task DisablingDuringTheCurrentOfferRequestDiscardsItsLateQuoteAndNeverRequestsHistory()
    {
        await using var fixture = new Fixture(Session(Item(1)));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Client.CurrentReply = async request =>
        {
            started.TrySetResult(); await release.Task;
            return [fixture.CurrentQuote(1, 100, request.Server)];
        };
        using var lease = fixture.Service.Acquire();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Online = false;
        var refresh = fixture.Service.RefreshAsync();
        release.TrySetResult();
        await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(fixture.Cache.GetQuote(7, 1));
        Assert.Null(fixture.Service.Snapshot.NetKina);
        Assert.Empty(fixture.Client.Requests);
        Assert.Single(fixture.Client.CurrentRequests);
    }

    [Fact]
    public async Task OnlyItemsMissingCurrentOffersRequestAverageHistory()
    {
        await using var fixture = new Fixture(Session(Item(1), Item(2), Item(3)));
        fixture.Client.CurrentReply = request => Task.FromResult(request.Items.Where(id => id != 2)
            .Select(id => fixture.CurrentQuote(id, id == 1 ? 20 : 30)).ToImmutableArray());
        using var lease = fixture.Service.Acquire();
        await fixture.Service.RefreshAsync();
        Assert.Equal(new[] { 1, 2, 3 }, Assert.Single(fixture.Client.CurrentRequests).Items);
        Assert.Equal(new[] { 2 }, Assert.Single(fixture.Client.Requests).Items);
        Assert.Equal(2, fixture.Service.Snapshot.CurrentRows);
        Assert.Equal(1, fixture.Service.Snapshot.AverageRows);
        Assert.Equal(51, fixture.Service.Snapshot.NetKina);
        Assert.True(fixture.Service.Snapshot.Uses24HourAverage);
        await fixture.Service.RefreshAsync();
        Assert.Single(fixture.Client.CurrentRequests);
        Assert.Single(fixture.Client.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshPositiveAndNegativeFallbacksSuppressMissingCurrentPollingAcrossConsumers(bool observed)
    {
        await using var fixture = new Fixture(Session(Item(1)));
        if (!observed) fixture.Client.Reply = _ => Task.FromResult(ImmutableArray<ExchangeMarketAveragePrice>.Empty);
        using var first = fixture.Service.Acquire();
        await fixture.Service.RefreshAsync();
        await using var other = new OverlayEditorMarketService(() => fixture.State, _ => fixture.Model, () => fixture.Online,
            fixture.Client, fixture.Cache, fixture.Clock, _ => new(false, true, false));
        using var second = other.Acquire();
        fixture.Clock.Advance(ExchangeMarketCache.MissingQuoteTtl + TimeSpan.FromTicks(1));
        await fixture.Service.RefreshAsync();
        await other.RefreshAsync();
        Assert.Single(fixture.Client.CurrentRequests);
        Assert.Single(fixture.Client.Requests);
        Assert.Equal(observed ? 8L : (long?)null, other.Snapshot.NetKina);
        // This widget-specific suppression must not extend the receipt cache's missing-offer TTL.
        await fixture.Cache.GetQuotesAsync(fixture.Client, 7, [1], CancellationToken.None);
        Assert.Equal(2, fixture.Client.CurrentRequests.Count);
        await other.RefreshAsync();
        Assert.Equal(2, fixture.Client.CurrentRequests.Count);
        Assert.Single(fixture.Client.Requests);
    }

    [Fact]
    public async Task AnotherConsumersCurrentQuoteReplacesAnAverageWithoutAnotherHistoryRequest()
    {
        await using var fixture = new Fixture(Session(Item(1)));
        using var lease = fixture.Service.Acquire();
        await fixture.Service.RefreshAsync();
        Assert.Equal(1, fixture.Service.Snapshot.AverageRows);
        fixture.Cache.PutQuotes([fixture.CurrentQuote(1, 100)]);
        await fixture.Service.RefreshAsync();
        Assert.Equal(88, fixture.Service.Snapshot.NetKina);
        Assert.Equal(1, fixture.Service.Snapshot.CurrentRows);
        Assert.Equal(0, fixture.Service.Snapshot.AverageRows);
        Assert.False(fixture.Service.Snapshot.Uses24HourAverage);
        Assert.Null(fixture.Service.Snapshot.AverageWindowStart);
        Assert.Equal(0, fixture.Service.Snapshot.AverageSamples);
        Assert.Single(fixture.Client.Requests);
        Assert.Single(fixture.Client.CurrentRequests);
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
        Assert.Empty(fixture.Client.CurrentRequests);
        fixture.Online = false;
        using var lease = fixture.Service.Acquire();
        await fixture.Service.RefreshAsync();
        Assert.Empty(fixture.Client.Requests);
        Assert.Empty(fixture.Client.CurrentRequests);
        Assert.Null(fixture.Service.Snapshot.NetKina);
        Assert.False(fixture.Service.Snapshot.OnlineEnabled);
    }

    [Fact]
    public async Task OfflineReadsUsableCacheAndOldSourcePricesRemainUnknown()
    {
        await using var fixture = new Fixture(Session(Item(1, 2))) { Online = false };
        fixture.Cache.PutAveragePrices([fixture.Quote(1, 10)]);
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
        fixture.Cache.PutAveragePrices([fixture.Quote(1, 10), fixture.Quote(3, 100)]);
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
        fixture.Cache.PutAveragePrices([fixture.Quote(1, 10), fixture.Quote(2, 101)]);
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
        fixture.Cache.PutAveragePrices([fixture.Quote(1, 10)]);
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
        fixture.Cache.PutAveragePrices([fixture.Quote(1, 10)]);
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
        fixture.Cache.PutAveragePrices([fixture.Quote(1, 10)]);
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
        if (change == "disabled") { Assert.Null(current.NetKina); Assert.Null(fixture.Cache.GetAveragePrice(7, 1)); }
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
        Assert.Null(fixture.Cache.GetAveragePrice(7, 1));
        Assert.Null(fixture.Service.Snapshot.NetKina);
    }

    [Fact]
    public async Task ExactHistoryRequestsArePerItemAndSharedMissingAveragesWaitTenMinutes()
    {
        await using var fixture = new Fixture(Session(Enumerable.Range(1, 205).Select(id => Item(id)).ToArray()));
        fixture.Client.Reply = _ => Task.FromResult(ImmutableArray<ExchangeMarketAveragePrice>.Empty);
        using var first = fixture.Service.Acquire();
        using var second = fixture.Service.Acquire();
        await fixture.Service.RefreshAsync();
        Assert.Equal(205, fixture.Client.Requests.Count);
        Assert.All(fixture.Client.Requests, request => Assert.Single(request.Items));
        Assert.Equal(new[] { 100, 100, 5 }, fixture.Client.CurrentRequests.Select(request => request.Items.Length).ToArray());
        fixture.Clock.Advance(ExchangeMarketCache.MissingQuoteTtl + TimeSpan.FromSeconds(1));
        await fixture.Service.RefreshAsync();
        Assert.Equal(205, fixture.Client.Requests.Count);
        Assert.Equal(3, fixture.Client.CurrentRequests.Count);
        Assert.Equal(205, fixture.Service.Snapshot.UnknownQuantity);
    }

    [Fact]
    public async Task RoundingAppliesOnceToTheEntireQuantityOfEachItemAndOverflowStaysUnknown()
    {
        await using var fixture = new Fixture(Session(Item(1), Item(1)));
        fixture.Cache.PutAveragePrices([fixture.Quote(1, 1.1m)]);
        Assert.Equal(1, fixture.Service.Snapshot.NetKina);
        Assert.Equal(1, fixture.Service.Snapshot.PricedRows);
        fixture.State = State(Session(Item(1, long.MaxValue), Item(1)));
        var overflow = fixture.Service.Snapshot;
        Assert.Null(overflow.NetKina);
        Assert.Null(overflow.PartialNetKina);
        Assert.True(overflow.StatusIsError);
    }

    [Theory]
    [InlineData(0, DiagnosticStatus.Healthy)]
    [InlineData(11, DiagnosticStatus.Warning)]
    public async Task AverageMarketDiagnosisContainsOnlyPriceBasisAndAnonymousCoverage(int ageMinutes, DiagnosticStatus status)
    {
        await using var fixture = new Fixture(Session(Item(1, 2)));
        fixture.Cache.PutAveragePrices([fixture.Quote(1, 10)]);
        fixture.Clock.Advance(TimeSpan.FromMinutes(ageMinutes));
        var contributors = DiagnosticContributors.Create(new ExistingProvider(fixture.Service, fixture.Cache));
        var result = contributors.Single(contributor => contributor.FeatureIds.Contains("loot-market")).Collect("loot-market");
        Assert.Equal(status, result.Status);
        Assert.Equal(ageMinutes > 10, result.Facts["liveMarketStale"]);
        Assert.Equal(true, result.Facts["liveMarketAverage24Hours"]);
        Assert.Equal(true, result.Facts["liveMarketAverageFallbackEnabled"]);
        Assert.Equal(0, result.Facts["liveMarketCurrentRows"]);
        Assert.Equal(1, result.Facts["liveMarketAverageRows"]);
        Assert.Equal(true, result.Facts["liveMarketAvailable"]);
        Assert.Equal(1, result.Facts["liveMarketPricedRows"]);
        Assert.Equal(2L, result.Facts["liveMarketSamples"]);
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain(fixture.Session.Id, json);
        Assert.DoesNotContain(fixture.Session.Character!.ProfileId!, json);
        Assert.Empty(fixture.Client.Requests);
    }

    private sealed class ExistingProvider(params object[] instances) : IServiceProvider
    { public object? GetService(Type type) => instances.FirstOrDefault(type.IsInstanceOfType); }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = At;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }

    private sealed record Request(int Server, int[] Items);
    private sealed class CurrentOnlyClient(Clock clock) : IExchangeMarketClient
    {
        internal int Calls;
        public Task<ImmutableArray<ExchangeMarketQuote>> GetQuotesAsync(int serverId, IReadOnlyCollection<int> ids, CancellationToken token)
        {
            Calls++;
            return Task.FromResult(ids.Select(id => new ExchangeMarketQuote(serverId, id, 10, 2,
                clock.GetUtcNow(), clock.GetUtcNow())).ToImmutableArray());
        }
        public Task<ImmutableArray<ExchangeMarketServer>> GetServersAsync(CancellationToken token) =>
            throw new InvalidOperationException("The fixture already has its server.");
    }

    private sealed class Client(Clock clock) : IExchangeMarketClient, IExchangeMarketAverageClient
    {
        internal ConcurrentQueue<Request> Requests { get; } = new();
        internal ConcurrentQueue<Request> CurrentRequests { get; } = new();
        internal decimal Price { get; set; } = 10;
        internal decimal? CurrentPrice { get; set; }
        internal Func<Request, Task<ImmutableArray<ExchangeMarketAveragePrice>>>? Reply { get; set; }
        internal Func<Request, Task<ImmutableArray<ExchangeMarketQuote>>>? CurrentReply { get; set; }
        public Task<ImmutableArray<ExchangeMarketQuote>> GetQuotesAsync(int serverId, IReadOnlyCollection<int> itemIds, CancellationToken cancellationToken)
        {
            var request = new Request(serverId, itemIds.ToArray()); CurrentRequests.Enqueue(request);
            return CurrentReply?.Invoke(request) ?? Task.FromResult(CurrentPrice is { } price ? request.Items.Select(id =>
                new ExchangeMarketQuote(serverId, id, price, 2, clock.GetUtcNow(), clock.GetUtcNow())).ToImmutableArray() : []);
        }
        public Task<ImmutableArray<ExchangeMarketServer>> GetServersAsync(CancellationToken cancellationToken) =>
            Task.FromResult<ImmutableArray<ExchangeMarketServer>>([new(7, "Market 7", "eu", "Europe", "elyos", true), new(8, "Market 8", "eu", "Europe", "elyos", true)]);
        public Task<ImmutableArray<ExchangeMarketAveragePrice>> GetAveragePricesAsync(int serverId, IReadOnlyCollection<int> itemIds, CancellationToken cancellationToken)
        {
            var request = new Request(serverId, itemIds.ToArray()); Requests.Enqueue(request);
            return Reply?.Invoke(request) ?? Task.FromResult(request.Items.Select(id =>
                new ExchangeMarketAveragePrice(serverId, id, Price, 2, clock.GetUtcNow().AddHours(-24), clock.GetUtcNow(), clock.GetUtcNow())).ToImmutableArray());
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
        internal ExchangeMarketAveragePrice Quote(int id, decimal price, int server = 7) => new(server, id, price, 2, Clock.GetUtcNow().AddHours(-24), Clock.GetUtcNow(), Clock.GetUtcNow());
        internal ExchangeMarketQuote CurrentQuote(int id, decimal price, int server = 7) => new(server, id, price, 2, Clock.GetUtcNow(), Clock.GetUtcNow());
        public ValueTask DisposeAsync() => Service.DisposeAsync();
    }
}
