using System.Collections.Concurrent;
using System.Collections.Immutable;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Crafting;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class CraftingCalculatorServiceTests
{
    private const string First = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Second = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly DateTimeOffset Start = new(2026, 11, 10, 10, 0, 0, TimeSpan.Zero);
    private static readonly ExchangeMarketServer Market = new(11301, "Synthetic market", "EU", "Europe", "Elyos", true) { GameServerKey = 1301 };
    private static readonly ExchangeMarketServer OtherMarket = Market with { ServerId = 11302, GameServerKey = 1302 };

    [Fact]
    public async Task ConstructionEditsAndExplicitRefreshWithoutAnAcquiredViewNeverQueryTheMarket()
    {
        using var f = new Fixture(cacheServers: false);
        f.Service.SetAttempts(2); f.Service.SetProficiency("Blacksmithing", 10);
        await f.Service.RefreshAsync();
        Assert.Empty(f.Client.Requests); Assert.Equal(0, f.Client.ServerCalls);
        Assert.False(f.Service.Snapshot.Busy); Assert.Null(f.Service.Snapshot.Result);
    }

    [Fact]
    public async Task FixedStartingAttemptsReachTheCalculatorAndRemainStableAcrossOutputVariants()
    {
        using var f = new Fixture();
        f.Service.SetAttempts(3); f.Service.SetAttempts(0); f.Service.SetAttempts(100_001);
        Assert.Equal(3, f.Service.Snapshot.TargetAttempts);
        using var lease = f.Service.Acquire(); await f.Service.Pending.WaitAsync(TimeSpan.FromSeconds(10));
        var result = f.Service.Snapshot.Result!;
        Assert.Equal(CraftingPlanStatus.Complete, result.Status);
        Assert.Equal(3m, result.Steps.Single(row => row.RecipeId == 100).ExpectedAttempts);
        Assert.Equal(3m, Assert.Single(result.Outputs).ExpectedQuantity);
        Assert.Equal(75m, result.ExpectedCost); Assert.Equal(75m, result.P90Cost);
        Assert.Equal(264m, result.ExpectedNetRevenue); Assert.Equal(189m, result.ExpectedProfit);
        f.Service.SetTarget(101, 6); await f.Service.Pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(3, f.Service.Snapshot.TargetAttempts);
        Assert.Equal(result.ExpectedNetRevenue, f.Service.Snapshot.Result!.ExpectedNetRevenue);
        Assert.Equal(3m, f.Service.Snapshot.Result.Steps.Single(row => row.RecipeId == 100).ExpectedAttempts);
    }

    [Fact]
    public void DependencyMapBatchEditsTheScenarioOnceAndRejectsAnInvalidBatchAtomically()
    {
        using var f = new Fixture(cacheServers: false);
        var revision = f.Service.Snapshot.Revision;
        var changes = 0;
        f.Service.Changed += () => changes++;
        f.Service.SetAcquisitions([new(3, 200), new(2, null)]);
        var state = f.Service.Snapshot;
        Assert.True(state.Revision > revision);
        Assert.Equal(1, changes);
        Assert.Equal(CraftingAcquisition.Craft, state.Acquisition[3]);
        Assert.Equal(200, state.RecipeChoices[3]);
        Assert.Equal(CraftingAcquisition.Buy, state.Acquisition[2]);
        f.Service.SetAcquisitions([new(3, null), new(2, 999_999)]);
        Assert.Equal(state.Revision, f.Service.Snapshot.Revision);
        Assert.Equal(state.Acquisition, f.Service.Snapshot.Acquisition);
        Assert.Equal(1, changes);
        Assert.Empty(f.Client.Requests);
        Assert.Equal(0, f.Client.ServerCalls);
    }

    [Fact]
    public async Task CachedServerFactionMismatchCannotRequestQuotesOrProduceProfit()
    {
        using var f = new Fixture();
        f.Settings.Current.ExchangeMarketProfiles[First] = Model(Market) with { Faction = "Asmodians" };
        using var lease = f.Service.Acquire(); await f.Service.Pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(f.Client.Requests); Assert.Empty(f.Service.Snapshot.SellPrices);
        Assert.Null(f.Service.Snapshot.ServerName); Assert.Null(f.Service.Snapshot.Result?.ExpectedProfit);
    }

    [Fact]
    public async Task AutomaticSelectionRequiresTheCachedNativeServerKeyToMatchOwnAuthority()
    {
        using var f = new Fixture();
        f.Settings.Current.ExchangeMarketProfiles[First] = Model(Market) with { AutoDetectServer = true };
        f.Cache.SaveServers([Market with { GameServerKey = 9999 }, OtherMarket]);
        using var lease = f.Service.Acquire(); await f.Service.Pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(f.Client.Requests); Assert.Empty(f.Service.Snapshot.SellPrices);
        Assert.Null(f.Service.Snapshot.ServerName); Assert.Null(f.Service.Snapshot.Result?.ExpectedProfit);
    }

    [Fact]
    public async Task ExplicitManualServerOverrideMayUseAnotherExactlyMatchedCachedMarket()
    {
        using var f = new Fixture();
        f.Settings.Current.ExchangeMarketProfiles[First] = Model(OtherMarket);
        using var lease = f.Service.Acquire(); await f.Service.Pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(OtherMarket.ServerId, Assert.Single(f.Client.Requests).Server);
        Assert.Equal(OtherMarket.ServerId, f.Service.Snapshot.SellPrices[1].ServerId);
        Assert.Equal(CraftingPlanStatus.Complete, f.Service.Snapshot.Result!.Status);
        Assert.NotNull(f.Service.Snapshot.Result.ExpectedProfit);
    }

    [Fact]
    public async Task AnAcquiredViewLoadsAColdServerCatalogueBeforeRequestingItsExactMarket()
    {
        using var f = new Fixture(cacheServers: false);
        using var lease = f.Service.Acquire(); await f.Service.Pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, f.Client.ServerCalls);
        Assert.Equal(Market.ServerId, Assert.Single(f.Client.Requests).Server);
        Assert.Equal(CraftingPlanStatus.Complete, f.Service.Snapshot.Result!.Status);
    }

    [Fact]
    public async Task OnlySelectedNestedAndReplacementItemsAreRequestedAndQualifiedNpcMinimumWins()
    {
        using var f = new Fixture();
        f.Service.SetReplacement(100, 2, 3); f.Service.SetAcquisition(3, CraftingAcquisition.Craft, 200);
        using var lease = f.Service.Acquire(); await f.Service.Pending.WaitAsync(TimeSpan.FromSeconds(10));
        var request = Assert.Single(f.Client.Requests);
        Assert.Equal(new[] { 1, 3, 4, 5, 6 }, request.Ids);
        var snapshot = f.Service.Snapshot;
        Assert.False(snapshot.IsError); Assert.False(snapshot.Busy);
        Assert.Equal(CraftingPriceSource.Npc, snapshot.BuyPrices[4].Source);
        Assert.Equal(5m, snapshot.BuyPrices[4].UnitPrice);
        Assert.Equal(CraftingPlanStatus.Complete, snapshot.Result!.Status);
        Assert.Equal(25m, snapshot.Result.ExpectedCost); Assert.Equal(63m, snapshot.Result.ExpectedProfit);
        Assert.DoesNotContain(2, snapshot.BuyPrices.Keys); Assert.DoesNotContain(9, snapshot.BuyPrices.Keys);
    }

    [Fact]
    public void CraftChoiceRequiresTheExactProducingRecipeAndReplacementStaysScopedToItsIngredient()
    {
        using var f = new Fixture();
        f.Service.SetAcquisition(3, CraftingAcquisition.Craft);
        f.Service.SetAcquisition(3, CraftingAcquisition.Craft, 300);
        f.Service.SetReplacement(100, 4, 3);
        Assert.Empty(f.Service.Snapshot.Acquisition); Assert.Empty(f.Service.Snapshot.ReplacementChoices);
        f.Service.SetReplacement(100, 2, 3); f.Service.SetAcquisition(3, CraftingAcquisition.Craft, 200);
        Assert.Equal(new[] { 1, 3, 4, 5, 6 }, f.Service.NeededItems(f.Service.Snapshot).Order());
        f.Service.SelectRecipe(300);
        Assert.Empty(f.Service.Snapshot.Acquisition); Assert.Empty(f.Service.Snapshot.RecipeChoices);
        Assert.Empty(f.Service.Snapshot.ReplacementChoices);
        Assert.Equal(new[] { 9, 10 }, f.Service.NeededItems(f.Service.Snapshot).Order());
    }

    [Theory]
    [InlineData("profile")] [InlineData("server")] [InlineData("proficiency")]
    public async Task ContextChangesRejectOldWorkAndOnlyPublishTheNewScenario(string change)
    {
        using var f = new Fixture();
        var response = NewResponse(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Client.Quotes = (call, server, ids, _) =>
        {
            if (call != 1) return Task.FromResult(f.Quotes(server, ids));
            entered.TrySetResult(); return response.Task;
        };
        using var lease = f.Service.Acquire();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); var old = f.Service.Pending;
        if (change == "profile") { f.Context = (Second, OtherMarket.GameServerKey); f.Proficiency = Proficiency(Second, 21, 2); }
        if (change == "server")
        {
            f.Settings.Current.ExchangeMarketProfiles[First] = Model(OtherMarket);
            f.Context = (First, OtherMarket.GameServerKey);
        }
        if (change == "proficiency") f.Proficiency = Proficiency(First, 21, 2);
        var current = f.Service.RefreshAsync();
        response.SetResult(f.Quotes(Market.ServerId, [1, 2, 4, 6]));
        await Task.WhenAll(old, current).WaitAsync(TimeSpan.FromSeconds(10));
        var snapshot = f.Service.Snapshot;
        Assert.False(snapshot.Busy); Assert.NotNull(snapshot.Result);
        Assert.Equal(change == "profile" ? Second : First, snapshot.ProfileId);
        Assert.Equal(change == "server" || change == "profile" ? OtherMarket.ServerId : Market.ServerId, snapshot.SellPrices[1].ServerId);
        Assert.Equal(change == "server" ? 12 : 21, snapshot.ObservedLevels["Blacksmithing"]);
        Assert.Equal(CraftingPlanStatus.Complete, snapshot.Result.Status);
    }

    [Fact]
    public async Task ClosingTheLastLeaseCancelsPendingWorkAndCannotPublishItsLateResponse()
    {
        using var f = new Fixture(); var response = NewResponse();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observed = default;
        f.Client.Quotes = (_, _, _, token) => { observed = token; entered.TrySetResult(); return response.Task; };
        var lease = f.Service.Acquire(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var old = f.Service.Pending; lease.Dispose();
        Assert.True(observed.IsCancellationRequested); Assert.False(f.Service.Snapshot.Busy);
        response.SetResult(f.Quotes(Market.ServerId, [1, 2, 4, 6]));
        await old.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(f.Service.Snapshot.Result); Assert.Empty(f.Service.Snapshot.SellPrices);
        Assert.Null(f.Cache.GetQuote(Market.ServerId, 1));
    }

    [Fact]
    public async Task AContextChangeDetectedOnlyAtCompletionQueuesTheNewScenarioInsteadOfStayingBusy()
    {
        using var f = new Fixture(); var response = NewResponse();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Client.Quotes = (call, server, ids, _) =>
        {
            if (call != 1) return Task.FromResult(f.Quotes(server, ids));
            entered.TrySetResult(); return response.Task;
        };
        using var lease = f.Service.Acquire(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var old = f.Service.Pending;
        f.Context = (Second, OtherMarket.GameServerKey); f.Proficiency = Proficiency(Second, 21, 2);
        response.SetResult(f.Quotes(Market.ServerId, [1, 2, 4, 6]));
        await old.WaitAsync(TimeSpan.FromSeconds(10));
        await f.Service.Pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(f.Service.Snapshot.Busy); Assert.Equal(Second, f.Service.Snapshot.ProfileId);
        Assert.Equal(OtherMarket.ServerId, f.Service.Snapshot.SellPrices[1].ServerId);
        Assert.Equal(CraftingPlanStatus.Complete, f.Service.Snapshot.Result!.Status);
    }

    [Fact]
    public async Task FreshCacheNeedsNoRequestAndEveryDisplayedPriceKeepsItsOriginalCaptureTime()
    {
        using var f = new Fixture(); f.Cache.PutQuotes(f.Quotes(Market.ServerId, [1, 2, 4, 6]));
        using var lease = f.Service.Acquire(); await f.Service.Pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(f.Client.Requests); Assert.NotNull(f.Service.Snapshot.Result);
        Assert.Equal(Start, f.Service.Snapshot.SellPrices[1].ObservedAt);
        f.Clock.Now = Start.Add(ExchangeMarketCache.MaximumAge).AddSeconds(1);
        f.Settings.Current.OnlineMarketPricesEnabled = false;
        await f.Service.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(f.Client.Requests); Assert.Empty(f.Service.Snapshot.SellPrices);
        Assert.Null(f.Service.Snapshot.Result!.ExpectedProfit);
    }

    [Fact]
    public async Task FailedRefreshCanBeRetriedAfterProviderBackoffWithoutAnOldResult()
    {
        using var f = new Fixture();
        f.Client.Quotes = (call, server, ids, _) => call == 1
            ? Task.FromException<ImmutableArray<ExchangeMarketQuote>>(new HttpRequestException("synthetic failure"))
            : Task.FromResult(f.Quotes(server, ids));
        using var lease = f.Service.Acquire(); await f.Service.Pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(f.Service.Snapshot.IsError); Assert.NotNull(f.Service.Snapshot.PriceRefreshFailure);
        Assert.Equal(CraftingPlanStatus.Unknown, f.Service.Snapshot.Result!.Status);
        Assert.Null(f.Service.Snapshot.Result.ExpectedProfit);
        f.Clock.Now = Start.AddMinutes(10);
        await f.Service.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(f.Service.Snapshot.IsError); Assert.NotNull(f.Service.Snapshot.Result);
        Assert.Null(f.Service.Snapshot.PriceRefreshFailure);
        Assert.Equal(2, f.Client.Requests.Count);
    }

    [Fact]
    public async Task ProfileChangesClearManualLevelsAndNeverApplyAnotherProfilesObservedLevel()
    {
        using var f = new Fixture(); f.Service.SetProficiency("Blacksmithing", 99);
        Assert.Equal(99, f.Service.Snapshot.Level("Blacksmithing"));
        f.Context = (Second, OtherMarket.GameServerKey);
        await f.Service.RefreshAsync();
        Assert.Empty(f.Service.Snapshot.SimulatedLevels); Assert.Empty(f.Service.Snapshot.ObservedLevels);
        Assert.False(f.Service.Snapshot.ProficiencyFresh); Assert.False(f.Service.Snapshot.UsingSavedProficiency);
        Assert.Null(f.Service.Snapshot.ProficiencyObservedAt);
        f.Proficiency = Proficiency(Second, 22, 2) with { IsFresh = false, Source = "saved-own-entry-crafting-proficiencies" };
        await f.Service.RefreshAsync();
        Assert.Equal(22, f.Service.Snapshot.Level("Blacksmithing"));
        Assert.True(f.Service.Snapshot.UsingSavedProficiency); Assert.False(f.Service.Snapshot.ProficiencyFresh);
        Assert.Empty(f.Client.Requests);
    }

    private static TaskCompletionSource<ImmutableArray<ExchangeMarketQuote>> NewResponse() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static CraftingProficiencySnapshot Proficiency(string profile, ushort level = 12, long sequence = 1) => new(profile,
        ImmutableDictionary<byte, CharacterCraftingProficiency>.Empty.Add(1, new(1, level, 0, 0)), true,
        Start, 1, sequence, NetworkCombatSourceStatus.Complete, CraftingProficiencyStateService.LiveSource);
    private static ExchangeMarketProfileSettings Model(ExchangeMarketServer server) => new()
    { ServerId = server.ServerId, Region = server.Region, Faction = server.Faction, GameServerKey = server.GameServerKey, AutoDetectServer = false };
    private static CraftingRecipe Recipe(int id, int output, params (int Item, int Quantity)[] ingredients) => new()
    {
        Id = id, OutputItemId = output, OutputQuantity = 1, Profession = "Blacksmithing", Subcategory = "Greatsword",
        Ingredients = ingredients.Select(row => new CraftingIngredient { ItemId = row.Item, Quantity = row.Quantity }).ToImmutableArray(),
        SuccessRates = [1m], CraftingFee = 0, MechanicsQualified = true, Faction = "Light"
    };
    private static CraftingCatalog Catalog() => new("synthetic", Enumerable.Range(1, 10).Select(id => new CraftingItem
        { Id = id, IsTradable = true, NameEn = "Synthetic item " + id, NameDe = "Synthetisches Item " + id }),
        [Recipe(100, 1, (2, 2), (4, 1)) with
            { Ingredients = [new() { ItemId = 2, Quantity = 2, ReplacementItemIds = [3] }, new() { ItemId = 4, Quantity = 1 }], ComboRecipeId = 101, ComboActivationChance = 0 },
            Recipe(101, 6), Recipe(200, 3, (5, 1)), Recipe(300, 9, (10, 1))],
        [new() { ItemId = 4, UnitPrice = 5, ConditionsQualified = true, Faction = "Light" },
            new() { ItemId = 4, UnitPrice = 1, ConditionsQualified = true, Faction = "Dark" },
            new() { ItemId = 4, UnitPrice = .1m, ConditionsQualified = false }]);
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = Start;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Client : IExchangeMarketClient
    {
        public ConcurrentQueue<(int Server, int[] Ids)> Requests { get; } = new();
        public int ServerCalls;
        public Func<int, int, IReadOnlyCollection<int>, CancellationToken, Task<ImmutableArray<ExchangeMarketQuote>>> Quotes { get; set; } = null!;
        public Task<ImmutableArray<ExchangeMarketServer>> GetServersAsync(CancellationToken cancellationToken)
        { Interlocked.Increment(ref ServerCalls); return Task.FromResult<ImmutableArray<ExchangeMarketServer>>([Market, OtherMarket]); }
        public Task<ImmutableArray<ExchangeMarketQuote>> GetQuotesAsync(int serverId, IReadOnlyCollection<int> itemIds, CancellationToken cancellationToken)
        { Requests.Enqueue((serverId, itemIds.Order().ToArray())); return Quotes(Requests.Count, serverId, itemIds, cancellationToken); }
    }
    private sealed class Fixture : IDisposable
    {
        private readonly byte[]? _before = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        public SettingsService Settings { get; } = new();
        public Clock Clock { get; } = new();
        public ExchangeMarketCache Cache { get; }
        public Client Client { get; } = new();
        public (string? ProfileId, int? OwnServer) Context { get; set; } = (First, Market.GameServerKey);
        public CraftingProficiencySnapshot Proficiency { get; set; } = CraftingCalculatorServiceTests.Proficiency(First);
        public CraftingCalculatorService Service { get; }
        public Fixture(bool cacheServers = true)
        {
            Settings.Current.OnlineMarketPricesEnabled = true;
            Settings.Current.ExchangeMarketProfiles = new() { [First] = Model(Market), [Second] = Model(OtherMarket) };
            Cache = new(Clock); if (cacheServers) Cache.SaveServers([Market, OtherMarket]);
            Client.Quotes = (_, server, ids, _) => Task.FromResult(Quotes(server, ids));
            Service = new(Settings, Client, Cache, Catalog(), () => Proficiency, () => Context, Clock);
        }
        public ImmutableArray<ExchangeMarketQuote> Quotes(int server, IReadOnlyCollection<int> ids) => ids.Select(id =>
            new ExchangeMarketQuote(server, id, id == 1 ? 100 : 10, 1, Clock.Now, Clock.Now)).ToImmutableArray();
        public void Dispose()
        {
            Service.Dispose();
            if (_before is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, _before);
        }
    }
}
