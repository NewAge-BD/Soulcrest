using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class ExchangeLootValuationTests
{
    private const int Material = 610530001, OtherMaterial = 610530005, Gear = 210230006, BoundGear = 210230081;
    private static readonly DateTimeOffset At = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private static string Id() => Guid.NewGuid().ToString("N");
    private static ExchangeMarketProfileSettings Model(int server = 7, decimal tax = 10, decimal fee = 2) =>
        new() { ServerId = server, Region = "eu", Faction = "elyos", SalesTaxPercent = tax, RegistrationFeePercent = fee };
    private static ExchangeLootValuationEntry Pending(int item = Material, long quantity = 3) =>
        new(Id(), Id(), Id(), item, quantity, At, Model(), ExchangeLootValueStatus.Pending);
    private static ExchangeMarketQuote Quote(int item = Material, int server = 7, decimal price = 10.7m) =>
        new(server, item, price, 2, At.AddMinutes(-1), At);

    [Fact]
    public void DecimalNetFloorsOnlyAfterBothCapturedPercentageAssumptions()
    {
        var value = ExchangeLootValuation.Evaluate(Pending(), Quote(), At)!;
        Assert.Equal(32, value.GrossKina); Assert.Equal(28, value.NetKina);
        Assert.Equal(ExchangeLootValueStatus.Estimated, value.Status);
        Assert.Equal("aion2.exchange", value.Source);
        Assert.NotEqual("own-server-market", value.Source);
        Assert.Null(ExchangeLootValuation.Evaluate(Pending(), Quote(server: 8), At));
        Assert.Null(ExchangeLootValuation.Evaluate(Pending(), Quote(item: OtherMaterial), At));
    }

    [Fact]
    public void EquipmentUsesNetBaseReferenceWhileBoundIdsAndSoulsAreExcluded()
    {
        var value = ExchangeLootValuation.Evaluate(Pending(Gear, 1), Quote(Gear, price: 101), At)!;
        Assert.Equal(ExchangeLootValueStatus.ReferenceOnly, value.Status); Assert.Equal(88, value.NetKina);
        Assert.False(ExchangeLootValuation.Eligible(new("Item:" + BoundGear, "Bound", LootTrackerLootKind.Item, 1)));
        Assert.False(ExchangeLootValuation.Eligible(new("SoulCrystal:1", "Soul", LootTrackerLootKind.Soul, 1)));
    }

    [Fact]
    public void SidecarRestoresFrozenValuesNeverRepricesAndKeepsHistoricalSourceTime()
    {
        using var f = new Files(); var entry = Pending(); var store = new ExchangeLootValuationStore(f.Root);
        Assert.True(store.Put(entry)); var priced = ExchangeLootValuation.Evaluate(entry, Quote(), At)!;
        Assert.True(store.Put(priced));
        var restored = Assert.Single(new ExchangeLootValuationStore(f.Root).ReadAll());
        Assert.Equal(priced, restored); Assert.Equal(At.AddMinutes(-1), restored.SourceCapturedAt);
        Assert.Throws<InvalidDataException>(() => store.Put(priced with { NetKina = 100 }));
        Assert.Throws<InvalidDataException>(() => store.Put(entry with { Quantity = 4 }));
        Assert.Equal(priced, Assert.Single(new ExchangeLootValuationStore(f.Root).ReadAll()));
    }

    [Fact]
    public void CorruptSidecarIsUnknownWriteProtectedAndNotOverwrittenByDelete()
    {
        using var f = new Files(); var entry = Pending(); var path = Path.Combine(f.Root, entry.SessionId + ".json");
        Directory.CreateDirectory(f.Root); File.WriteAllText(path, "{broken");
        var store = new ExchangeLootValuationStore(f.Root);
        Assert.Empty(store.ReadAll()); Assert.True(store.HasIssues); Assert.False(store.Put(entry));
        store.Tombstone(entry.SessionId); Assert.Equal("{broken", File.ReadAllText(path));
    }

    [Fact]
    public void HistoricalProofUsesItsValuationTimeAndRejectsAFirstPriceAfterTenMinutes()
    {
        var entry = Pending(); var priced = ExchangeLootValuation.Evaluate(entry, Quote(), At)!;
        ExchangeLootValuationStore.ValidateEntry(priced); // No current clock may age a frozen value out.
        Assert.Throws<InvalidDataException>(() => ExchangeLootValuationStore.ValidateEntry(priced with
            { SourceCapturedAt = At.AddHours(-3) }));
        Assert.Throws<InvalidDataException>(() => ExchangeLootValuationStore.ValidateEntry(priced with
            { ValuedAt = At.AddMinutes(11) }));
    }

    [Fact]
    public void TombstonePersistsAndNeverResurrectsDeletedSession()
    {
        using var f = new Files(); var entry = Pending(); var store = new ExchangeLootValuationStore(f.Root);
        Assert.True(store.Put(entry)); store.Tombstone(entry.SessionId);
        var restart = new ExchangeLootValuationStore(f.Root);
        Assert.True(restart.IsDeleted(entry.SessionId)); Assert.False(restart.Put(entry)); Assert.Empty(restart.ReadAll());
    }

    [Fact]
    public void PendingWithoutCapturedServerIsCorruptAndWriteProtectedOnRecovery()
    {
        using var f = new Files(); var entry = Pending(); var store = new ExchangeLootValuationStore(f.Root); Assert.True(store.Put(entry));
        var path = Path.Combine(f.Root, entry.SessionId + ".json");
        var text = File.ReadAllText(path).Replace("\"ServerId\":7", "\"ServerId\":null", StringComparison.Ordinal);
        File.WriteAllText(path, text); var restarted = new ExchangeLootValuationStore(f.Root);
        Assert.Empty(restarted.ReadAll()); Assert.True(restarted.IsBlocked(entry.SessionId)); Assert.False(restarted.Put(entry));
        Assert.Equal(text, File.ReadAllText(path));
    }

    [Fact]
    public async Task OrphanPendingExpiresWithoutHistoryWithoutRequestingOrRunningForever()
    {
        using var files = new Files(); var clock = new Clock(); var entry = Pending();
        Assert.True(new ExchangeLootValuationStore(files.Root).Put(entry)); clock.Advance(TimeSpan.FromMinutes(11));
        var client = new Client(clock); var source = new Source(clock);
        await using var service = new ExchangeLootValuationService(() => true, source, client, Cache(clock), new(files.Root), clock);
        await service.FlushAsync(); Assert.Equal(0, service.Snapshot.PendingCount); Assert.Empty(client.Requests);
        Assert.Equal(ExchangeLootValueStatus.Unknown, Assert.Single(service.Snapshot.Entries).Status);
    }

    [Fact]
    public async Task NewReceiptsFreezeDifferentPricesFeesAndServersWithoutChangingLootOrClock()
    {
        await using var f = new Fixture(); await f.Service.FlushAsync();
        var first = f.Source.Commit(Material, 2, Model()); await f.Service.FlushAsync();
        var original = f.Source.Snapshot;
        Assert.Equal(17, f.Service.GetSessionSummary(f.Source.Session).NetKina);
        Assert.Equal(2, Assert.Single(f.Source.Session.Loot).Quantity);
        f.Clock.Advance(TimeSpan.FromSeconds(5));
        f.Source.Commit(Material, 1, Model(8, tax: 20, fee: 5)); await f.Service.FlushAsync();
        var summary = f.Service.GetSessionSummary(f.Source.Session);
        Assert.Equal(25, summary.NetKina); Assert.Equal(2, summary.PricedRows); Assert.True(summary.Complete);
        Assert.Equal(17, f.Service.GetSessionSummary(original.Sessions[0]).NetKina);
        Assert.Equal(17, f.Service.GetRowSummary(original.Sessions[0], original.Sessions[0].Loot[0]).NetKina);
        Assert.Equal(1, f.Source.Session.TimelineEvents.Count(x => x.Id == first.Fact.Id));
        Assert.Equal(At, f.Source.Session.StartedAt); Assert.Equal(3, f.Source.Session.ItemCount);
        Assert.Contains(f.Client.Requests, x => x.Server == 8);
    }

    [Fact]
    public async Task ExistingFactsDoNotBackfillAndExplicitPendingSidecarsResumeIdempotently()
    {
        using var files = new Files(); var clock = new Clock(); var source = new Source(clock);
        var fact = source.Commit(Material, 2, Model()); var client = new Client(clock); var cache = Cache(clock);
        var store = new ExchangeLootValuationStore(files.Root);
        await using (var service = new ExchangeLootValuationService(() => true, source, client, cache, store, clock))
        {
            await service.FlushAsync(); Assert.Empty(client.Requests); Assert.Null(service.GetSessionSummary(source.Session).NetKina);
        }
        var pending = new ExchangeLootValuationEntry(source.Session.Id, source.ProfileId, fact.Fact.Id, Material, 2, At, Model(), ExchangeLootValueStatus.Pending);
        Assert.True(new ExchangeLootValuationStore(files.Root).Put(pending));
        await using (var resumed = new ExchangeLootValuationService(() => true, source, client, cache, new(files.Root), clock))
        {
            await resumed.FlushAsync(); Assert.Equal(17, resumed.GetSessionSummary(source.Session).NetKina);
            await resumed.FlushAsync(); Assert.Single(client.Requests);
        }
        clock.Advance(TimeSpan.FromDays(3));
        await using var frozen = new ExchangeLootValuationService(() => true, source, client, cache, new(files.Root), clock);
        await frozen.FlushAsync(); Assert.Equal(17, frozen.GetSessionSummary(source.Session).NetKina); Assert.Single(client.Requests);
    }

    [Fact]
    public async Task MissingResponsesStayPendingUntilWindowThenUnknownAndNeverRepriceLater()
    {
        await using var f = new Fixture(); f.Client.Reply = (_, _, _) => Task.FromResult(ImmutableArray<ExchangeMarketQuote>.Empty);
        f.Source.Commit(Material, 1, Model()); await f.Service.FlushAsync();
        Assert.Equal(1, f.Service.Snapshot.PendingCount); Assert.Null(f.Service.GetSessionSummary(f.Source.Session).NetKina);
        f.Clock.Advance(TimeSpan.FromMinutes(1)); await f.Service.RefreshPendingAsync(); Assert.Equal(2, f.Client.Requests.Count);
        f.Clock.Advance(TimeSpan.FromMinutes(10)); await f.Service.RefreshPendingAsync();
        Assert.Equal(ExchangeLootValueStatus.Unknown, Assert.Single(f.Service.Snapshot.Entries).Status);
        f.Client.Reply = null; await f.Service.RefreshPendingAsync(); Assert.Equal(2, f.Client.Requests.Count);
    }

    [Fact]
    public async Task OfflineKeepsUsableCacheAndExpiredCacheNeverCreatesAValue()
    {
        await using var f = new Fixture { Online = false };
        f.Cache.PutQuotes([Quote() with { CapturedAt = At.AddHours(-1), FetchedAt = At.AddMinutes(-20) }]);
        f.Source.Commit(Material, 1, Model()); await f.Service.FlushAsync();
        Assert.Equal(9, f.Service.GetSessionSummary(f.Source.Session).NetKina); Assert.Empty(f.Client.Requests);
        f.Clock.Advance(TimeSpan.FromHours(2)); f.Source.Commit(OtherMaterial, 1, Model()); await f.Service.FlushAsync();
        Assert.Equal(1, f.Service.Snapshot.PendingCount); Assert.Empty(f.Client.Requests);
    }

    [Fact]
    public async Task ValidPriceAfterInitialMissingResponseCanFreezeOnlyInsideReceiptWindow()
    {
        await using var f = new Fixture(); f.Client.Reply = (_, _, _) => Task.FromResult(ImmutableArray<ExchangeMarketQuote>.Empty);
        f.Source.Commit(Material, 2, Model()); await f.Service.FlushAsync(); Assert.Equal(1, f.Service.Snapshot.PendingCount);
        f.Clock.Advance(TimeSpan.FromMinutes(1)); f.Client.Reply = null; await f.Service.RefreshPendingAsync();
        Assert.Equal(17, f.Service.GetSessionSummary(f.Source.Session).NetKina); Assert.Equal(0, f.Service.Snapshot.PendingCount);
    }

    [Fact]
    public async Task CachedPriceCannotCrossAConflictingCapturedRegionOrFaction()
    {
        await using var f = new Fixture { Online = false }; f.Cache.PutQuotes([Quote()]);
        f.Source.Commit(Material, 1, Model() with { Region = "asia" }); await f.Service.FlushAsync();
        Assert.Null(f.Service.GetSessionSummary(f.Source.Session).NetKina); Assert.Empty(f.Client.Requests);
    }

    [Fact]
    public async Task GearReferenceIsIncludedInNetButNotClaimedAsExactVariantAndBoundDoesNotRequest()
    {
        await using var f = new Fixture(); f.Source.Commit(Gear, 1, Model()); f.Source.Commit(BoundGear, 1, Model());
        await f.Service.FlushAsync();
        var value = f.Service.GetSessionSummary(f.Source.Session);
        Assert.Equal(8, value.NetKina); Assert.Equal(value.NetKina, value.ReferenceNetKina);
        Assert.True(value.ReferenceOnly); Assert.Equal(1, value.EligibleRows); Assert.True(value.Complete);
        Assert.DoesNotContain(f.Client.Requests.SelectMany(x => x.Items), x => x == BoundGear);
    }

    [Fact]
    public async Task SummaryRejectsSameQuantityFromWrongProfileOrDifferentFactTimeAndDeduplicatesDays()
    {
        await using var f = new Fixture(); f.Source.Commit(Material, 1, Model()); await f.Service.FlushAsync();
        var session = f.Source.Session;
        Assert.Equal(8, f.Service.GetDaySummary([session, session]).NetKina);
        Assert.Null(f.Service.GetSessionSummary(session with { Character = session.Character! with { ProfileId = Id() } }).NetKina);
        Assert.Null(f.Service.GetSessionSummary(session with { TimelineEvents = [session.TimelineEvents[0] with { At = At.AddSeconds(1) }] }).NetKina);
    }

    [Fact]
    public async Task DeletionDuringHttpCancelsAndLateResponseCannotResurrectSidecar()
    {
        await using var f = new Fixture(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<ImmutableArray<ExchangeMarketQuote>>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Client.Reply = (_, _, _) => { entered.TrySetResult(); return late.Task; };
        f.Source.Commit(Material, 1, Model()); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var sessionId = f.Source.Session.Id; f.Source.Delete(); late.SetResult([Quote()]);
        await f.Service.FlushAsync(); Assert.Empty(f.Service.Snapshot.Entries);
        Assert.True(new ExchangeLootValuationStore(f.Files.Root).IsDeleted(sessionId));
    }

    [Fact]
    public async Task OfflineCancelsTheInFlightQuoteAndQueuedServerRequestWithoutSendingASecondHttpRequest()
    {
        await using var f = new Fixture(); var quoteStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = 0;
        async Task<ImmutableArray<ExchangeMarketQuote>> WaitQuotes(int _, IReadOnlyCollection<int> __, CancellationToken ct)
        { quoteStarted.TrySetResult(); try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); } catch (OperationCanceledException) { Interlocked.Increment(ref canceled); throw; } return []; }
        async Task<ImmutableArray<ExchangeMarketServer>> WaitServers(CancellationToken ct)
        { serverStarted.TrySetResult(); try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); } catch (OperationCanceledException) { Interlocked.Increment(ref canceled); throw; } return []; }
        f.Client.Reply = WaitQuotes; f.Client.ServerReply = WaitServers;
        f.Clock.Advance(ExchangeMarketCache.FetchTtl + TimeSpan.FromSeconds(1));
        f.Source.Commit(Material, 1, Model()); await quoteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var loading = f.Service.LoadServersAsync();
        Assert.False(loading.IsCompleted); Assert.False(serverStarted.Task.IsCompleted);
        f.Online = false; f.Source.Signal(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading);
        await f.Service.FlushAsync(); Assert.Equal(1, canceled); Assert.False(serverStarted.Task.IsCompleted);
        Assert.Equal(1, f.Source.Session.ItemCount);
    }

    [Fact]
    public async Task NoOpRefreshDoesNotRepublishFrozenSnapshots()
    {
        await using var f = new Fixture(); f.Source.Commit(Material, 1, Model()); await f.Service.FlushAsync();
        var old = f.Service.Snapshot; var notices = 0; f.Service.Changed += () => notices++;
        await f.Service.FlushAsync(); f.Source.Signal(); await f.Service.FlushAsync();
        Assert.Same(old, f.Service.Snapshot); Assert.Equal(0, notices);
    }

    [Fact]
    public async Task RetryAfterIsRespectedAndCoverageKeepsPartialQuantitiesAndCapturedBases()
    {
        await using var f = new Fixture();
        f.Source.Commit(Material, 2, Model()); await f.Service.FlushAsync();
        f.Client.Reply = (_, _, _) => throw new ExchangeMarketApiException("Synthetic busy", HttpStatusCode.TooManyRequests, TimeSpan.FromMinutes(5));
        f.Source.Commit(OtherMaterial, 3, Model(8, 20, 4)); await f.Service.FlushAsync();
        var calls = f.Client.Requests.Count;
        f.Clock.Advance(TimeSpan.FromMinutes(4)); await f.Service.RefreshPendingAsync(); Assert.Equal(calls, f.Client.Requests.Count);
        var summary = f.Service.GetSessionSummary(f.Source.Session);
        Assert.Equal(5, summary.EligibleQuantity); Assert.Equal(2, summary.PricedQuantity);
        Assert.Equal(3, summary.UnknownQuantity); Assert.Equal(3, summary.PendingQuantity);
        Assert.Contains(summary.Bases, b => b.ServerId == 8 && b.SalesTaxPercent == 20 && b.RegistrationFeePercent == 4);
        f.Clock.Advance(TimeSpan.FromMinutes(1)); f.Client.Reply = null; await f.Service.RefreshPendingAsync();
        Assert.Equal(calls + 1, f.Client.Requests.Count); Assert.Equal(5, f.Service.GetSessionSummary(f.Source.Session).PricedQuantity);
    }

    [Fact]
    public async Task ServerLoadCanceledByOfflineCannotPublishLateReplyAfterReenable()
    {
        await using var f = new Fixture();
        f.Clock.Advance(ExchangeMarketCache.FetchTtl + TimeSpan.FromSeconds(1));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<ImmutableArray<ExchangeMarketServer>>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Client.ServerReply = _ => { entered.TrySetResult(); return late.Task; };
        var loading = f.Service.LoadServersAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Online = false; f.Source.Signal(); f.Online = true;
        late.SetResult([new(99, "Canceled response", "eu", "Europe", "elyos", true)]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading);
        Assert.DoesNotContain(f.Service.Servers, s => s.ServerId == 99);
    }

    [Fact]
    public async Task NewReceiptsForTheSameUnlistedItemReuseTheSuccessfulMissingPriceLookup()
    {
        await using var f = new Fixture();
        f.Client.Reply = (_, _, _) => Task.FromResult(ImmutableArray<ExchangeMarketQuote>.Empty);
        f.Source.Commit(Material, 1, Model()); await f.Service.FlushAsync();
        for (var index = 0; index < 5; index++)
        {
            f.Source.Commit(Material, 2, Model()); await f.Service.FlushAsync();
        }
        Assert.Single(f.Client.Requests);
        Assert.Equal(11, f.Source.Session.ItemCount);
        Assert.All(f.Service.Snapshot.Entries, entry => Assert.Equal(ExchangeLootValueStatus.Pending, entry.Status));
        f.Clock.Advance(ExchangeMarketCache.FetchTtl + TimeSpan.FromSeconds(1));
        f.Source.Commit(Material, 1, Model()); await f.Service.FlushAsync();
        Assert.Equal(2, f.Client.Requests.Count);
    }

    [Fact]
    public async Task NewReceiptsCannotBypassSharedNetworkFailureBackoff()
    {
        await using var f = new Fixture();
        f.Client.Reply = (_, _, _) => throw new HttpRequestException("Synthetic provider outage");
        f.Source.Commit(Material, 1, Model()); await f.Service.FlushAsync();
        f.Source.Commit(OtherMaterial, 2, Model()); await f.Service.FlushAsync();
        f.Source.Commit(Material, 3, Model()); await f.Service.FlushAsync();
        Assert.Single(f.Client.Requests);
        Assert.Equal(6, f.Source.Session.ItemCount);
        Assert.True(f.Service.Snapshot.StatusIsError);
        Assert.Null(f.Service.GetSessionSummary(f.Source.Session).NetKina);
    }

    [Fact]
    public async Task TheLiveEditorAndFrozenValuationShareTheSamePendingPriceFetch()
    {
        await using var f = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Client.Reply = async (server, ids, cancellationToken) =>
        {
            entered.TrySetResult(); await release.Task.WaitAsync(cancellationToken);
            return ids.Select(id => new ExchangeMarketQuote(server, id, 10, 2, f.Clock.GetUtcNow(), f.Clock.GetUtcNow())).ToImmutableArray();
        };
        f.Source.Commit(Material, 2, Model()); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using var editor = new OverlayEditorMarketService(
            () => new(f.Source.Session, true, new(null, null, null, 0), null, false, f.Clock.GetUtcNow()),
            _ => Model(), () => f.Online, f.Client, f.Cache, f.Clock);
        var editorWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        editor.Changed += () => { if (editor.Snapshot.Loading) editorWaiting.TrySetResult(); };
        using var lease = editor.Acquire();
        var refresh = editor.RefreshAsync();
        // Both consumers have planned the same miss before the first response is released.
        await editorWaiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.TrySetResult();
        await Task.WhenAll(refresh, f.Service.FlushAsync()).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(f.Client.Requests);
        Assert.Equal(17, editor.Snapshot.NetKina);
        Assert.Equal(17, f.Service.GetSessionSummary(f.Source.Session).NetKina);
    }

    [Fact]
    public async Task AnExtremeProviderRetryDoesNotFaultTheWorkerOrMarkItsValuationStoreAsDamaged()
    {
        await using var f = new Fixture();
        f.Client.Reply = (_, _, _) => throw new ExchangeMarketApiException(
            "Synthetic long maintenance", HttpStatusCode.ServiceUnavailable, TimeSpan.MaxValue);
        f.Source.Commit(Material, 1, Model()); await f.Service.FlushAsync();
        f.Source.Commit(OtherMaterial, 2, Model()); await f.Service.FlushAsync();
        Assert.Single(f.Client.Requests);
        Assert.All(f.Service.Snapshot.Entries, entry => Assert.Equal(DateTimeOffset.MaxValue, entry.NextAttemptAt));
        Assert.True(f.Service.Snapshot.StatusIsError);
        Assert.DoesNotContain("beschädigt", f.Service.Snapshot.Status);
        Assert.Equal(3, f.Source.Session.ItemCount);
    }

    [Fact]
    public async Task AnotherConsumersSuccessfulCacheRefreshClearsAnOldRequestErrorWithoutRepricingFrozenReceipts()
    {
        await using var f = new Fixture();
        f.Client.Reply = (_, _, _) => throw new HttpRequestException("Synthetic outage");
        f.Source.Commit(Material, 2, Model()); await f.Service.FlushAsync();
        Assert.True(f.Service.Snapshot.StatusIsError);
        f.Clock.Advance(TimeSpan.FromSeconds(31));
        f.Cache.PutQuotes([new(7, Material, 10, 2, f.Clock.GetUtcNow(), f.Clock.GetUtcNow())]);
        await f.Service.FlushAsync();
        Assert.False(f.Service.Snapshot.StatusIsError);
        Assert.Equal(17, f.Service.GetSessionSummary(f.Source.Session).NetKina);
        Assert.Single(f.Client.Requests);
        var frozen = f.Service.Snapshot.Entries;
        f.Cache.PutQuotes([new(7, Material, 20, 2, f.Clock.GetUtcNow(), f.Clock.GetUtcNow())]);
        await f.Service.FlushAsync();
        Assert.Equal(frozen, f.Service.Snapshot.Entries);
        Assert.Equal(17, f.Service.GetSessionSummary(f.Source.Session).NetKina);
    }

    private static ExchangeMarketCache Cache(Clock clock)
    {
        var cache = new ExchangeMarketCache(clock);
        cache.SaveServers([new(7, "Test A", "eu", "Europe", "elyos", true), new(8, "Test B", "eu", "Europe", "elyos", true)]);
        return cache;
    }
    private sealed class Files : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "Soulcrest-ExchangeValuationTests-" + Id());
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = At;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
    private sealed class Source(Clock clock) : ICommittedLootFactSource
    {
        public string ProfileId { get; } = Id();
        public LootTrackerSession Session { get; private set; } = new()
        { Id = Id(), TimelineVersion = 1, UnifiedVersion = 1, StartedAt = At, Loot = [] };
        public LootTrackerSnapshot Snapshot => new([Session], clock.GetUtcNow());
        public event Action? Changed;
        public event Action<CommittedLootFact>? LootFactCommitted;
        public event Action<string>? SessionDeleted;
        public CommittedLootFact Commit(int item, long quantity, ExchangeMarketProfileSettings model)
        {
            var row = new LootTrackerLoot("Item:" + item, "Synthetic material", LootTrackerLootKind.Item, quantity);
            var fact = new LootTrackerTimelineEvent(Id(), LootTrackerTimelineEventKind.Drop, clock.GetUtcNow(), clock.GetUtcNow() - At) { Loot = [row], Origin = LootTrackerActivityOrigin.Pve };
            var total = Session.Loot.Where(x => x.Id != row.Id).ToImmutableArray().Add(row with { Quantity = (Session.Loot.FirstOrDefault(x => x.Id == row.Id)?.Quantity ?? 0) + quantity });
            Session = Session with { Character = new(ProfileId, "Synthetic profile"), TimelineEvents = Session.TimelineEvents.Add(fact), Loot = total,
                ItemCount = (Session.ItemCount ?? 0) + quantity, LastLootAt = fact.At };
            var committed = new CommittedLootFact(Session.Id, ProfileId, fact, model); LootFactCommitted?.Invoke(committed); Signal(); return committed;
        }
        public void Delete() => SessionDeleted?.Invoke(Session.Id);
        public void Signal() => Changed?.Invoke();
    }
    private sealed class Client(Clock clock) : IExchangeMarketClient
    {
        public List<(int Server, int[] Items)> Requests { get; } = [];
        public Func<int, IReadOnlyCollection<int>, CancellationToken, Task<ImmutableArray<ExchangeMarketQuote>>>? Reply;
        public Func<CancellationToken, Task<ImmutableArray<ExchangeMarketServer>>>? ServerReply;
        public Task<ImmutableArray<ExchangeMarketServer>> GetServersAsync(CancellationToken ct) => ServerReply?.Invoke(ct) ?? Task.FromResult(Cache(clock).GetServers());
        public Task<ImmutableArray<ExchangeMarketQuote>> GetQuotesAsync(int server, IReadOnlyCollection<int> items, CancellationToken ct)
        {
            Requests.Add((server, items.ToArray()));
            return Reply?.Invoke(server, items, ct) ?? Task.FromResult(items.Select(id => new ExchangeMarketQuote(server, id, server == 8 ? 11m : 10m, 2, clock.GetUtcNow().AddMinutes(-1), clock.GetUtcNow())).ToImmutableArray());
        }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public Files Files { get; } = new(); public Clock Clock { get; } = new(); public Source Source { get; }
        public Client Client { get; } public ExchangeMarketCache Cache { get; } public bool Online { get; set; } = true;
        public ExchangeLootValuationService Service { get; }
        public Fixture()
        {
            Source = new(Clock); Client = new(Clock); Cache = ExchangeLootValuationTests.Cache(Clock);
            Service = new(() => Online, Source, Client, Cache, new(Files.Root), Clock);
        }
        public async ValueTask DisposeAsync() { await Service.DisposeAsync(); Files.Dispose(); }
    }
}
