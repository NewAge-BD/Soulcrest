using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Crafting;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Read-only price recovery: an outage changes source health, never the provenance of cached prices.</summary>
[Collection("Settings file")]
public sealed class CraftingPriceRecoveryTests
{
    private const string FirstProfile = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string SecondProfile = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 10, 0, 0, TimeSpan.Zero);
    private static readonly ExchangeMarketServer FirstMarket = new(11301, "Synthetic market A", "EU", "Europe", "Elyos", true)
        { GameServerKey = 1301 };
    private static readonly ExchangeMarketServer SecondMarket = FirstMarket with
        { ServerId = 11302, Name = "Synthetic market B", GameServerKey = 1302 };

    [Theory]
    [InlineData(503, 30)]
    [InlineData(429, 90)]
    public async Task FailedRefreshUsesAnElevenMinuteOldQualifiedCacheAndNpcWithoutRebasingPrices(int status, int retrySeconds)
    {
        using var fixture = new Fixture();
        var observed = Now.AddMinutes(-11);
        fixture.SeedQuotes(FirstMarket.ServerId, observed);
        fixture.FailQuotes((HttpStatusCode)status, TimeSpan.FromSeconds(retrySeconds));

        using var lease = fixture.Service.Acquire();
        await fixture.Service.Pending.WaitAsync(TimeSpan.FromSeconds(10));

        var snapshot = fixture.Service.Snapshot;
        Assert.False(snapshot.Busy); Assert.False(snapshot.IsError);
        Assert.Null(snapshot.CalculationFailure);
        AssertProviderFailure(snapshot, "MarketQuotes", status, Now.AddSeconds(retrySeconds));
        AssertCompleteCosts(snapshot);
        Assert.Equal(CraftingPriceSource.Npc, snapshot.BuyPrices[3].Source);
        Assert.Equal(5m, snapshot.BuyPrices[3].UnitPrice);
        Assert.Null(snapshot.BuyPrices[3].ObservedAt);
        Assert.Equal(observed, snapshot.SellPrices[1].ObservedAt);
        Assert.Equal(observed, snapshot.BuyPrices[2].ObservedAt);
        Assert.Equal(observed, fixture.Cache.GetQuote(FirstMarket.ServerId, 1)!.CapturedAt);
        Assert.Equal(observed, fixture.Cache.GetQuote(FirstMarket.ServerId, 1)!.FetchedAt);
        Assert.Equal(Now, snapshot.CalculatedAt);
        Assert.Single(fixture.Client.QuoteRequests);
    }

    [Fact]
    public async Task ExpiredQuotesAreNeverRevivedByAFailedRefresh()
    {
        using var fixture = new Fixture();
        fixture.SeedQuotes(FirstMarket.ServerId, Now - ExchangeMarketCache.MaximumAge - TimeSpan.FromSeconds(1));
        fixture.FailQuotes(HttpStatusCode.ServiceUnavailable, TimeSpan.FromSeconds(30));

        using var lease = fixture.Service.Acquire();
        await fixture.Service.Pending.WaitAsync(TimeSpan.FromSeconds(10));

        var snapshot = fixture.Service.Snapshot;
        Assert.False(snapshot.IsError); Assert.Null(snapshot.CalculationFailure);
        AssertProviderFailure(snapshot, "MarketQuotes", 503, Now.AddSeconds(30));
        AssertUnknownMoney(snapshot);
        Assert.Empty(snapshot.SellPrices);
        Assert.False(snapshot.BuyPrices.ContainsKey(2));
        Assert.Equal(CraftingPriceSource.Npc, snapshot.BuyPrices[3].Source);
        Assert.Null(fixture.Cache.GetQuote(FirstMarket.ServerId, 1));
    }

    [Fact]
    public async Task MissingCacheRemainsUnknownInsteadOfAZeroCostOrProfit()
    {
        using var fixture = new Fixture();
        fixture.FailQuotes(HttpStatusCode.ServiceUnavailable, TimeSpan.FromSeconds(30));

        using var lease = fixture.Service.Acquire();
        await fixture.Service.Pending.WaitAsync(TimeSpan.FromSeconds(10));

        var snapshot = fixture.Service.Snapshot;
        Assert.False(snapshot.Busy); Assert.False(snapshot.IsError);
        Assert.Null(snapshot.CalculationFailure);
        AssertProviderFailure(snapshot, "MarketQuotes", 503, Now.AddSeconds(30));
        AssertUnknownMoney(snapshot);
        Assert.NotEmpty(snapshot.Result!.Issues);
        Assert.Empty(snapshot.SellPrices);
        Assert.Equal(5m, snapshot.BuyPrices[3].UnitPrice);
    }

    [Fact]
    public async Task ColdServerFailureCannotGuessAMarketFromStoredQuotesOrTheProfile()
    {
        using var fixture = new Fixture(cacheServers: false);
        fixture.SeedQuotes(FirstMarket.ServerId, Now.AddMinutes(-11));
        fixture.Client.GetServers = _ => Task.FromException<ImmutableArray<ExchangeMarketServer>>(
            ProviderFailure(HttpStatusCode.ServiceUnavailable, TimeSpan.FromSeconds(30)));

        using var lease = fixture.Service.Acquire();
        await fixture.Service.Pending.WaitAsync(TimeSpan.FromSeconds(10));

        var snapshot = fixture.Service.Snapshot;
        Assert.False(snapshot.IsError); Assert.Null(snapshot.CalculationFailure);
        AssertProviderFailure(snapshot, "MarketServers", 503, Now.AddSeconds(30));
        AssertUnknownMoney(snapshot);
        Assert.Null(snapshot.ServerName); Assert.Empty(snapshot.SellPrices);
        Assert.Empty(fixture.Client.QuoteRequests); Assert.Equal(1, fixture.Client.ServerCalls);
    }

    [Fact]
    public async Task APriceOutageKeepsTheLowLevelWarningAndUsesMinimumReferenceLevels()
    {
        using var fixture = new Fixture(level: 9);
        fixture.SeedQuotes(FirstMarket.ServerId, Now.AddMinutes(-11));
        fixture.FailQuotes(HttpStatusCode.ServiceUnavailable, TimeSpan.FromSeconds(30));

        using var lease = fixture.Service.Acquire();
        await fixture.Service.Pending.WaitAsync(TimeSpan.FromSeconds(10));

        var snapshot = fixture.Service.Snapshot;
        Assert.False(snapshot.IsError); Assert.Null(snapshot.CalculationFailure);
        AssertProviderFailure(snapshot, "MarketQuotes", 503, Now.AddSeconds(30));
        AssertCompleteCosts(snapshot);
        Assert.Equal(9, snapshot.ObservedLevels["Blacksmithing"]);
        Assert.Empty(snapshot.SimulatedLevels);
        Assert.Equal(10, snapshot.ReferenceLevels["Blacksmithing"]);
        Assert.NotEmpty(CraftingCalculatorPresentation.ProficiencyWarnings(snapshot));
        Assert.DoesNotContain(snapshot.Result!.Issues, issue => issue.Code == "ProficiencyTooLow");
    }

    [Fact]
    public async Task SuccessfulRetryClearsTheWarningAndUsesTheNewQuotesOwnObservationTime()
    {
        using var fixture = new Fixture();
        var oldObservation = Now.AddMinutes(-11);
        fixture.SeedQuotes(FirstMarket.ServerId, oldObservation);
        fixture.Client.GetQuotes = (call, server, ids, _) => call == 1
            ? Task.FromException<ImmutableArray<ExchangeMarketQuote>>(ProviderFailure(HttpStatusCode.ServiceUnavailable, TimeSpan.FromSeconds(30)))
            : Task.FromResult(fixture.Quotes(server, ids, fixture.Clock.Now));

        using var lease = fixture.Service.Acquire();
        await fixture.Service.Pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(fixture.Service.Snapshot.PriceRefreshFailure);
        Assert.Equal(oldObservation, fixture.Service.Snapshot.SellPrices[1].ObservedAt);

        fixture.Clock.Now = Now.AddSeconds(31);
        await fixture.Service.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var snapshot = fixture.Service.Snapshot;
        Assert.Null(snapshot.PriceRefreshFailure); Assert.Null(snapshot.CalculationFailure);
        Assert.False(snapshot.IsError); Assert.False(snapshot.Busy);
        AssertCompleteCosts(snapshot);
        Assert.Equal(fixture.Clock.Now, snapshot.SellPrices[1].ObservedAt);
        Assert.Equal(2, fixture.Client.QuoteRequests.Count);
    }

    [Fact]
    public async Task ADelayedFailureFromTheOldProfileCannotPublishWarningOrPricesInTheNewProfile()
    {
        using var fixture = new Fixture();
        fixture.SeedQuotes(FirstMarket.ServerId, Now.AddMinutes(-11));
        var response = new TaskCompletionSource<ImmutableArray<ExchangeMarketQuote>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Client.GetQuotes = (call, server, ids, _) =>
        {
            if (call != 1) return Task.FromResult(fixture.Quotes(server, ids, fixture.Clock.Now));
            entered.TrySetResult(); return response.Task;
        };

        using var lease = fixture.Service.Acquire();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var oldWork = fixture.Service.Pending;
        fixture.Context = (SecondProfile, SecondMarket.GameServerKey);
        fixture.Proficiency = Proficiency(SecondProfile, 20, sequence: 2);
        var newWork = fixture.Service.RefreshAsync();
        response.SetException(ProviderFailure(HttpStatusCode.ServiceUnavailable, TimeSpan.FromSeconds(30)));
        await Task.WhenAll(oldWork, newWork).WaitAsync(TimeSpan.FromSeconds(10));

        var snapshot = fixture.Service.Snapshot;
        Assert.Equal(SecondProfile, snapshot.ProfileId);
        Assert.Equal(SecondMarket.Name, snapshot.ServerName);
        Assert.False(snapshot.IsError); Assert.False(snapshot.Busy);
        Assert.Null(snapshot.PriceRefreshFailure); Assert.Null(snapshot.CalculationFailure);
        AssertCompleteCosts(snapshot);
        Assert.All(snapshot.SellPrices.Values, price => Assert.Equal(SecondMarket.ServerId, price.ServerId));
        Assert.All(snapshot.BuyPrices.Values.Where(price => price.Source == CraftingPriceSource.Exchange),
            price => Assert.Equal(SecondMarket.ServerId, price.ServerId));
        Assert.Equal(new[] { FirstMarket.ServerId, SecondMarket.ServerId }, fixture.Client.QuoteRequests.Select(row => row.Server));
    }

    [Fact]
    public async Task CurrentLowestOfferWinsEvenWhenACachedAverageIsCheaperAndNeedsNoAverageRequest()
    {
        using var f = new Fixture();
        f.Cache.PutAveragePrices([Average(1, 20), Average(2, 1)]);
        using var lease = f.Service.Acquire(); await f.Service.Pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(f.Client.AverageRequests);
        Assert.Equal(CraftingPriceSource.Exchange, f.Service.Snapshot.SellPrices[1].Source);
        Assert.Equal(100m, f.Service.Snapshot.SellPrices[1].UnitPrice);
        Assert.Equal(10m, f.Service.Snapshot.BuyPrices[2].UnitPrice);
        AssertCompleteCosts(f.Service.Snapshot);
    }

    [Fact]
    public async Task AveragesAreRequestedOnlyForMissingTradableItemsAndRemainIdentifiable()
    {
        using var f = new Fixture();
        f.Client.GetQuotes = (_, server, _, _) => Task.FromResult(f.Quotes(server, [1], Now));
        f.Client.GetAverages = (server, ids, _) => Task.FromResult(ids.Select(id => Average(id, 4, server)).ToImmutableArray());
        using var lease = f.Service.Acquire(); await f.Service.Pending.WaitAsync(TimeSpan.FromSeconds(10));
        var request = Assert.Single(f.Client.AverageRequests);
        Assert.Equal(new[] { 2 }, request.ItemIds); Assert.Equal(FirstMarket.ServerId, request.Server);
        var input = f.Service.Snapshot.BuyPrices[2];
        Assert.Equal(CraftingPriceSource.ExchangeAverage, input.Source); Assert.True(input.IsMarket);
        Assert.Equal(Now.AddHours(-24), input.AverageRangeStart); Assert.Equal(Now, input.ObservedAt);
        Assert.Equal(24, input.AverageSamples);
        Assert.Equal(CraftingPriceSource.Npc, f.Service.Snapshot.BuyPrices[3].Source);
        Assert.Equal(CraftingPlanStatus.Complete, f.Service.Snapshot.Result!.Status);
        Assert.Equal(13m, f.Service.Snapshot.Result.ExpectedCost);
        Assert.Equal(75m, f.Service.Snapshot.Result.ExpectedProfit);
    }

    [Fact]
    public async Task MissingOrExpiredAveragesCannotTurnUnknownPricesIntoProfit()
    {
        using var f = new Fixture();
        f.Cache.PutAveragePrices([Average(2, 4) with { RangeEnd = Now.AddHours(-3), RangeStart = Now.AddHours(-27) }]);
        f.Client.GetQuotes = (_, server, _, _) => Task.FromResult(f.Quotes(server, [1], Now));
        using var lease = f.Service.Acquire(); await f.Service.Pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new[] { 2 }, Assert.Single(f.Client.AverageRequests).ItemIds);
        Assert.False(f.Service.Snapshot.BuyPrices.ContainsKey(2)); AssertUnknownMoney(f.Service.Snapshot);
    }

    [Fact]
    public async Task FailedAverageRefreshCanUseItsQualifiedCachedValueWithoutMasqueradingAsAnOffer()
    {
        using var f = new Fixture();
        f.Cache.PutAveragePrices([Average(2, 4) with
        { RangeStart = Now.AddHours(-24).AddMinutes(-11), RangeEnd = Now.AddMinutes(-11), FetchedAt = Now.AddMinutes(-11) }]);
        f.Client.GetQuotes = (_, server, _, _) => Task.FromResult(f.Quotes(server, [1], Now));
        f.Client.GetAverages = (_, _, _) => Task.FromException<ImmutableArray<ExchangeMarketAveragePrice>>(new TimeoutException("synthetic"));
        using var lease = f.Service.Acquire(); await f.Service.Pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(CraftingFailureStage.MarketAverages, f.Service.Snapshot.PriceRefreshFailure!.Stage);
        Assert.Equal(CraftingFailureKind.Timeout, f.Service.Snapshot.PriceRefreshFailure.Kind);
        Assert.Equal(CraftingPriceSource.ExchangeAverage, f.Service.Snapshot.BuyPrices[2].Source);
        Assert.Equal(Now.AddMinutes(-11), f.Service.Snapshot.BuyPrices[2].ObservedAt);
        Assert.Equal(CraftingPlanStatus.Complete, f.Service.Snapshot.Result!.Status);
    }

    private static ExchangeMarketAveragePrice Average(int item, decimal value, int server = 11301) =>
        new(server, item, value, 24, Now.AddHours(-24), Now, Now);

    private static void AssertCompleteCosts(CraftingCalculatorSnapshot snapshot)
    {
        var result = Assert.IsType<CraftingPlanResult>(snapshot.Result);
        Assert.Equal(CraftingPlanStatus.Complete, result.Status);
        Assert.Equal(25m, result.ExpectedCost); Assert.Equal(25m, result.P90Cost);
        Assert.Equal(88m, result.ExpectedProfit + result.ExpectedCost); Assert.Equal(63m, result.ExpectedProfit);
        Assert.Equal(12m, result.ExpectedFees);
    }

    private static void AssertUnknownMoney(CraftingCalculatorSnapshot snapshot)
    {
        var result = Assert.IsType<CraftingPlanResult>(snapshot.Result);
        Assert.Equal(CraftingPlanStatus.Unknown, result.Status);
        Assert.Null(result.ExpectedCost); Assert.Null(result.P90Cost);
        Assert.Null(result.ExpectedProfit);
        Assert.Null(result.ExpectedFees);
    }

    private static void AssertProviderFailure(CraftingCalculatorSnapshot snapshot, string stage, int status, DateTimeOffset retryAt)
    {
        var failure = Assert.IsType<CraftingCalculationFailure>(snapshot.PriceRefreshFailure);
        Assert.Equal(stage, failure.Stage.ToString()); Assert.Equal("Provider", failure.Kind.ToString());
        Assert.Equal(status, Convert.ToInt32(failure.HttpStatus, CultureInfo.InvariantCulture));
        Assert.Equal(retryAt, failure.RetryAt);
        Assert.DoesNotContain("SYNTHETIC-SENSITIVE-CONTEXT", JsonSerializer.Serialize(failure));
    }

    private static ExchangeMarketApiException ProviderFailure(HttpStatusCode status, TimeSpan retryAfter) =>
        new("SYNTHETIC-SENSITIVE-CONTEXT", status, retryAfter);

    private static CraftingProficiencySnapshot Proficiency(string profile, ushort level, long sequence = 1) =>
        new(profile, ImmutableDictionary<byte, CharacterCraftingProficiency>.Empty.Add(1, new(1, level, 0, 0)),
            true, Now, 1, sequence, NetworkCombatSourceStatus.Complete, CraftingProficiencyStateService.LiveSource);

    private static ExchangeMarketProfileSettings Model(ExchangeMarketServer market) => new()
    {
        ServerId = market.ServerId, Region = market.Region, Faction = market.Faction,
        GameServerKey = market.GameServerKey, AutoDetectServer = false
    };

    private static CraftingCatalog Catalog() => new("synthetic-recovery", [
        new() { Id = 1, NameEn = "Synthetic output", NameDe = "Synthetisches Ergebnis", IsTradable = true },
        new() { Id = 2, NameEn = "Synthetic market input", NameDe = "Synthetische Marktzutat", IsTradable = true },
        new() { Id = 3, NameEn = "Synthetic vendor input", NameDe = "Synthetische Händlerzutat", IsTradable = false }
    ], [
        new()
        {
            Id = 100, Profession = "Blacksmithing", Subcategory = "Greatsword", Faction = "Light",
            RequiredProficiency = 10, OutputItemId = 1, OutputQuantity = 1,
            Ingredients = [new() { ItemId = 2, Quantity = 2 }, new() { ItemId = 3, Quantity = 1 }],
            SuccessRates = Enumerable.Repeat(1m, 91).ToImmutableArray(), CraftingFee = 0, MechanicsQualified = true
        }
    ], [new() { ItemId = 3, UnitPrice = 5, BundleQuantity = 1, Faction = "Light", ConditionsQualified = true }]);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = CraftingPriceRecoveryTests.Now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Client : IExchangeMarketClient, IExchangeMarketAverageClient
    {
        private int _quoteCalls;
        public int ServerCalls;
        public ConcurrentQueue<(int Server, int[] ItemIds)> QuoteRequests { get; } = new();
        public ConcurrentQueue<(int Server, int[] ItemIds)> AverageRequests { get; } = new();
        public Func<int, IReadOnlyCollection<int>, CancellationToken, Task<ImmutableArray<ExchangeMarketAveragePrice>>> GetAverages { get; set; } =
            (_, _, _) => Task.FromResult(ImmutableArray<ExchangeMarketAveragePrice>.Empty);
        public Task<ImmutableArray<ExchangeMarketAveragePrice>> GetAveragePricesAsync(int server, IReadOnlyCollection<int> ids, CancellationToken token)
        { AverageRequests.Enqueue((server, ids.Order().ToArray())); return GetAverages(server, ids, token); }
        public Func<CancellationToken, Task<ImmutableArray<ExchangeMarketServer>>> GetServers { get; set; } =
            _ => Task.FromResult<ImmutableArray<ExchangeMarketServer>>([FirstMarket, SecondMarket]);
        public Func<int, int, IReadOnlyCollection<int>, CancellationToken, Task<ImmutableArray<ExchangeMarketQuote>>> GetQuotes { get; set; } = null!;
        public Task<ImmutableArray<ExchangeMarketServer>> GetServersAsync(CancellationToken token)
        { Interlocked.Increment(ref ServerCalls); return GetServers(token); }
        public Task<ImmutableArray<ExchangeMarketQuote>> GetQuotesAsync(int serverId, IReadOnlyCollection<int> itemIds, CancellationToken token)
        {
            QuoteRequests.Enqueue((serverId, itemIds.Order().ToArray()));
            return GetQuotes(Interlocked.Increment(ref _quoteCalls), serverId, itemIds, token);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public SettingsService Settings { get; } = new();
        public Clock Clock { get; } = new();
        public Client Client { get; } = new();
        public ExchangeMarketCache Cache { get; }
        public (string? ProfileId, int? OwnServer) Context { get; set; } = (FirstProfile, FirstMarket.GameServerKey);
        public CraftingProficiencySnapshot Proficiency { get; set; }
        public CraftingCalculatorService Service { get; }

        public Fixture(bool cacheServers = true, ushort level = 20)
        {
            // The service only reads these in-memory values. No real settings or profile file is changed.
            Settings.Current.OnlineMarketPricesEnabled = true;
            Settings.Current.ExchangeMarketProfiles = new() { [FirstProfile] = Model(FirstMarket), [SecondProfile] = Model(SecondMarket) };
            Proficiency = CraftingPriceRecoveryTests.Proficiency(FirstProfile, level);
            Cache = new(Clock);
            if (cacheServers) Cache.SaveServers([FirstMarket, SecondMarket]);
            Client.GetQuotes = (_, server, ids, _) => Task.FromResult(Quotes(server, ids, Clock.Now));
            Service = new(Settings, Client, Cache, Catalog(), () => Proficiency, () => Context, Clock);
        }

        public ImmutableArray<ExchangeMarketQuote> Quotes(int server, IReadOnlyCollection<int> ids, DateTimeOffset observed) =>
            ids.Select(id => new ExchangeMarketQuote(server, id, id == 1 ? 100m : id == 2 ? 10m : 50m,
                1, observed, observed)).ToImmutableArray();

        public void SeedQuotes(int server, DateTimeOffset observed)
        {
            var current = Clock.Now;
            Clock.Now = observed;
            try { Cache.PutQuotes(Quotes(server, [1, 2, 3], observed)); }
            finally { Clock.Now = current; }
        }

        public void FailQuotes(HttpStatusCode status, TimeSpan retryAfter) => Client.GetQuotes = (_, _, _, _) =>
            Task.FromException<ImmutableArray<ExchangeMarketQuote>>(ProviderFailure(status, retryAfter));

        public void Dispose() => Service.Dispose();
    }
}
