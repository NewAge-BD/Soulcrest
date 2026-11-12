using System.Collections.Immutable;
using Soulcrest.App.Services;
using Soulcrest.Core.Crafting;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class CraftingProgressTests
{
    [Fact]
    public async Task ExactPinnedDemandUsesItsPieceGoalInsteadOfTheBrowsedHundredAttempts()
    {
        using var f = new Fixture();
        var scenario = f.Scenario with { TargetAttempts = 100, SimulationSamples = 5,
            Result = new() { Steps = [new(10, 100, 100, 0, 1, 1), new(11, 100, 100, 0, 1, null)] } };
        Assert.True(f.Service.Pin(scenario, 1));
        await f.Service.WaitForPlanAsync();
        Assert.Equal(100, f.Service.Snapshot.Pinned!.Scenario.Quantity);
        Assert.Equal(1, f.Service.Snapshot.Pinned.TargetQuantity);
        Assert.Equal(2m, Assert.Single(f.Service.Snapshot.Pinned.PlanItems.Where(row => row.ItemId == 4)).RequiredQuantity);
        Assert.Equal(1m, Assert.Single(f.Service.Snapshot.Pinned.PlanItems.Where(row => row.ItemId == 2)).RequiredQuantity);
        Assert.True(f.Service.Snapshot.PlanIsFresh);
    }

    [Fact]
    public async Task ReplacingAPinCancelsTheOldDemandWorkerAndRejectsItsLateResult()
    {
        using var f = new Fixture();
        f.Service.Dispose();
        using var started = new ManualResetEventSlim();
        using var service = f.Create(f.Store, (request, token) =>
        {
            if (request.TargetOutputQuantity == 1)
            {
                started.Set();
                Assert.True(token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)));
                token.ThrowIfCancellationRequested();
            }
            return new CraftingOutputTargetCalculator(Catalog()).Calculate(request, token);
        });
        Assert.True(service.Pin(f.Scenario, 1));
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(service.Pin(f.Scenario, 2));
        await service.WaitForPlanAsync();
        Assert.Equal(2, service.Snapshot.Pinned!.TargetQuantity);
        Assert.Equal(4m, Assert.Single(service.Snapshot.Pinned.PlanItems.Where(row => row.ItemId == 4)).RequiredQuantity);
        Assert.False(service.Snapshot.PlanBusy);
        Assert.Null(service.Snapshot.Error);
    }

    [Fact]
    public async Task FailedDemandWorkerClearsBusyAndExposesOnlyAnAnonymousFailure()
    {
        using var f = new Fixture();
        f.Service.Dispose();
        var calls = 0;
        using var service = f.Create(f.Store, (request, token) => Interlocked.Increment(ref calls) == 1
            ? throw new InvalidOperationException("private-source-detail")
            : new CraftingOutputTargetCalculator(Catalog()).Calculate(request, token));
        Assert.True(service.Pin(f.Scenario));
        await service.WaitForPlanAsync();
        Assert.False(service.Snapshot.PlanBusy);
        Assert.False(service.Snapshot.PlanIsFresh);
        Assert.NotNull(service.Snapshot.Error);
        Assert.DoesNotContain("private-source-detail", service.Snapshot.Error);
        Assert.All(service.Snapshot.Pinned!.PlanItems, row => Assert.Null(row.RequiredQuantity));
        Assert.True(service.UpdatePinnedPlan(f.Scenario));
        await service.WaitForPlanAsync();
        Assert.Null(service.Snapshot.Error);
        Assert.True(service.Snapshot.PlanIsFresh);
    }

    [Fact]
    public async Task PersistedPinKeepsManualChoicesAndCounterWithoutCopyingLiveSourceSnapshots()
    {
        using var f = new Fixture();
        f.Stock = f.Stock with
        {
            ObservedMinimumItems = ImmutableDictionary<int, long>.Empty.Add(4, 999),
            EffectiveItems = ImmutableDictionary<int, CraftingStockItem>.Empty.Add(4,
                new(4, 999, StockFreshness.Fresh, StockCompleteness.Partial, StockOrigin.Automatic))
        };
        Assert.True(f.Service.Pin(f.Scenario with { OwnedItems = ImmutableDictionary<int, long>.Empty.Add(4, 999) }));
        await f.Service.WaitForPlanAsync();
        await f.Service.FlushAsync();
        Assert.Empty(f.Service.Snapshot.Pinned!.Scenario.OwnedItems);
        var json = await File.ReadAllTextAsync(f.Path);
        foreach (var sourceField in new[] { "ObservedLevels", "BuyPrices", "SellPrices", "Identity", "TargetIcon", "NameEn", "LastReceivedAt", "SourceQuantities", "ObservedMinimumItems" })
            Assert.DoesNotContain(sourceField, json);
    }

    [Fact]
    public void ExplicitExactComboGoalIsIndependentOfAttemptsInventoryAndUnrelatedReceipts()
    {
        using var f = new Fixture();
        f.Stock = f.Stock with { EffectiveItems = ImmutableDictionary<int, CraftingStockItem>.Empty
            .Add(2, new(2, 999, StockFreshness.Fresh, StockCompleteness.Partial, StockOrigin.ManualOverride)) };
        Assert.True(f.Service.Pin(f.Scenario with { TargetAttempts = 99 }));
        Assert.Equal(1, f.Service.Snapshot.Pinned!.TargetQuantity);
        Assert.Equal(0, f.Service.Snapshot.Pinned.ConfirmedQuantity);
        f.Source.Publish(f.Fact(1, 20)); // Base result is not the concrete COMBO result.
        f.Source.Publish(f.Fact(3, 20)); // Unrelated item.
        f.Service.Tick();
        Assert.Equal(0, f.Service.Snapshot.Pinned.ConfirmedQuantity);
        Assert.Empty(f.Service.Snapshot.Rows);
        var exact = f.Fact(2, 1);
        f.Source.Publish(exact); f.Source.Publish(exact); f.Service.Tick();
        Assert.Equal(1, f.Service.Snapshot.Pinned.ConfirmedQuantity);
        Assert.True(f.Service.Snapshot.IsComplete);
        Assert.True(f.Service.Snapshot.IsCompletionVisible);
        Assert.Single(f.Service.Snapshot.Rows);
        Assert.Equal(1, f.Service.Snapshot.Rows[0].Quantity);
        // Exact new target receipts prove this goal independently of a manual/partial stock baseline.
        Assert.True(f.Service.Snapshot.Rows[0].IsComplete);
    }

    [Fact]
    public void PinExcludesAlreadyQueuedEventsAndOrdinaryBrowsingDoesNotReplaceTheGoal()
    {
        using var f = new Fixture();
        f.Source.Publish(f.Fact(2, 5));
        Assert.True(f.Service.Pin(f.Scenario, 3));
        Assert.Equal(0, f.Service.Snapshot.Pinned!.ConfirmedQuantity);
        Assert.False(f.Service.UpdatePinnedPlan(f.Scenario with { SelectedRecipeId = 12, TargetItemId = 3 }));
        Assert.Equal(2, f.Service.Snapshot.Pinned.TargetItemId);
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        f.Source.Publish(f.Fact(2, 2)); f.Service.Tick();
        Assert.Equal(2, f.Service.Snapshot.Pinned.ConfirmedQuantity);
        Assert.False(f.Service.Snapshot.IsComplete);
    }

    [Fact]
    public async Task RestartRestoresTheCounterAndDeduplicationWithoutReplayingRecentActivity()
    {
        using var f = new Fixture();
        Assert.True(f.Service.Pin(f.Scenario, 3));
        var fact = f.Fact(2, 2);
        f.Source.Publish(fact); f.Service.Tick();
        await f.Store.FlushAsync();
        f.Service.Dispose();
        using var restoredStore = new CraftingProgressStore(f.Path, f.Clock);
        using var restored = f.Create(restoredStore);
        Assert.Equal(2, restored.Snapshot.Pinned!.ConfirmedQuantity);
        Assert.True(restored.Snapshot.IsHistorical);
        Assert.Empty(restored.Snapshot.Rows);
        Assert.False(restored.Snapshot.IsCompletionVisible);
        f.Source.Publish(fact); restored.Tick();
        Assert.Equal(2, restored.Snapshot.Pinned.ConfirmedQuantity);
        Assert.Empty(restored.Snapshot.Rows);
    }

    [Fact]
    public async Task ShutdownCheckpointDrainsTheLastCommittedReceiptWithoutAPresentationTick()
    {
        using var f = new Fixture();
        Assert.True(f.Service.Pin(f.Scenario, 5));
        await f.Service.WaitForPlanAsync();
        var fact = f.Fact(2, 2);
        f.Source.Publish(fact);
        Assert.Equal(0, f.Service.Snapshot.Pinned!.ConfirmedQuantity);
        // No timer runs in this fixture, and no explicit Tick follows the final producer callback.
        await f.Service.FlushAsync();
        Assert.Equal(2, f.Service.Snapshot.Pinned.ConfirmedQuantity);
        f.Service.Dispose();
        using var restoredStore = new CraftingProgressStore(f.Path, f.Clock);
        using var restored = f.Create(restoredStore);
        Assert.Equal(2, restored.Snapshot.Pinned!.ConfirmedQuantity);
        Assert.Single(restored.Snapshot.Pinned.SeenReceipts);
        Assert.Empty(restored.Snapshot.Rows);
        f.Source.Publish(fact);
        await restored.FlushAsync();
        Assert.Equal(2, restored.Snapshot.Pinned.ConfirmedQuantity);
        Assert.Single(restored.Snapshot.Pinned.SeenReceipts);
        Assert.Empty(restored.Snapshot.Rows);
    }

    [Fact]
    public async Task ShutdownCheckpointWaitsForARunningTickAndDrainsReceiptsDeliveredBehindIt()
    {
        using var f = new Fixture();
        f.Service.Dispose();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var blockRead = 0;
        CraftingStockSnapshot ReadStock()
        {
            if (Volatile.Read(ref blockRead) != 0)
            {
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            }
            return f.Stock;
        }
        using var service = new CraftingProgressService(f.Source, f.Store, Catalog(), ReadStock, () => f.Stock.ProfileId, time: f.Clock);
        Assert.True(service.Pin(f.Scenario, 10));
        await service.WaitForPlanAsync();
        f.Source.Publish(f.Fact(2, 2));
        Volatile.Write(ref blockRead, 1);
        var tick = Task.Run(service.Tick);
        Task checkpoint = Task.CompletedTask;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5))); // First receipt is taken, but not applied yet.
            f.Source.Publish(f.Fact(2, 3)); // This receipt remains behind the running tick.
            checkpoint = Task.Run(service.FlushAsync);
            Assert.False(checkpoint.IsCompleted);
        }
        finally { Volatile.Write(ref blockRead, 0); release.Set(); }
        await Task.WhenAll(tick, checkpoint);
        using var restored = new CraftingProgressStore(f.Path, f.Clock);
        Assert.Equal(5, restored.Read(f.Stock.ProfileId)!.ConfirmedQuantity);
        Assert.Equal(2, restored.Read(f.Stock.ProfileId)!.SeenReceipts.Length);
    }

    [Fact]
    public void RecentRowsAggregateQualifiedSourcesExpireWhileHiddenAndDoNotInventCraftOrMarket()
    {
        using var f = new Fixture();
        f.Persistent = false;
        Assert.True(f.Service.Pin(f.Scenario, 5));
        f.Source.Publish(f.Fact(4, 2, LootTrackerTimelineEventKind.HarvestSuccess));
        f.Source.Publish(f.Fact(4, 3)); f.Service.Tick();
        var row = Assert.Single(f.Service.Snapshot.Rows);
        Assert.Equal(5, row.Quantity);
        Assert.Equal(2, row.SourceQuantities[CraftingProgressSource.Gathering]);
        Assert.Equal(3, row.SourceQuantities[CraftingProgressSource.Other]);
        Assert.DoesNotContain(CraftingProgressSource.Craft, row.SourceQuantities.Keys);
        Assert.DoesNotContain(CraftingProgressSource.Market, row.SourceQuantities.Keys);
        Assert.True(f.Service.Snapshot.HasContent);
        f.Clock.Advance(TimeSpan.FromSeconds(21)); f.Service.Tick();
        Assert.Empty(f.Service.Snapshot.Rows);
        Assert.False(f.Service.Snapshot.HasContent);
        Assert.NotNull(f.Service.Snapshot.Pinned);
        f.Source.Publish(f.Fact(2, 5)); f.Service.Tick();
        Assert.True(f.Service.Snapshot.IsCompletionVisible);
        f.Clock.Advance(TimeSpan.FromSeconds(21)); f.Service.Tick();
        Assert.True(f.Service.Snapshot.HasContent); // Completion retains the goal for the fixed sixty seconds.
        f.Clock.Advance(TimeSpan.FromSeconds(40)); f.Service.Tick();
        Assert.False(f.Service.Snapshot.HasContent);
        Assert.Equal(5, f.Service.Snapshot.Pinned!.ConfirmedQuantity);
    }

    [Fact]
    public void CharacterSwitchKeepsLateCommittedCountersOnTheirOwnerWithoutReplayingTransientRows()
    {
        using var f = new Fixture();
        Assert.True(f.Service.Pin(f.Scenario, 3));
        f.Source.Publish(f.Fact(2, 2)); f.Service.Tick();
        var original = f.Stock.ProfileId;
        f.Stock = f.Stock with { ProfileId = "22222222222222222222222222222222" }; f.Service.Tick();
        Assert.Null(f.Service.Snapshot.Pinned);
        f.Source.Publish(f.Fact(2, 4, profile: original)); f.Service.Tick();
        Assert.Null(f.Service.Snapshot.Pinned);
        Assert.Empty(f.Service.Snapshot.Rows);
        f.Stock = f.Stock with { ProfileId = original }; f.Service.Tick();
        Assert.Equal(6, f.Service.Snapshot.Pinned!.ConfirmedQuantity);
        Assert.Empty(f.Service.Snapshot.Rows);
        Assert.True(f.Service.Snapshot.IsComplete);
        Assert.False(f.Service.Snapshot.IsCompletionVisible);
    }

    [Fact]
    public async Task DamagedOrDuplicateStorageIsPreservedAndNeverCreatesAnInventedGoal()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "soulcrest-crafting-progress-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = System.IO.Path.Combine(root, "goal.json");
        const string damaged = "{\"SchemaVersion\":1,\"SchemaVersion\":1,\"Pins\":[],\"DeletedProfiles\":[]}";
        try
        {
            await File.WriteAllTextAsync(path, damaged);
            using var store = new CraftingProgressStore(path);
            Assert.True(store.IsReadOnly);
            Assert.Equal(CraftingProgressStore.LoadError, store.Error);
            Assert.Null(store.Read("11111111111111111111111111111111"));
            await store.FlushAsync();
            Assert.Equal(damaged, await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class Fixture : IDisposable
    {
        public readonly Clock Clock = new();
        public readonly Source Source = new();
        public readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "soulcrest-crafting-progress-" + Guid.NewGuid().ToString("N"), "goal.json");
        public readonly CraftingProgressStore Store;
        public readonly CraftingProgressService Service;
        public bool Persistent = true;
        public CraftingStockSnapshot Stock = new() { ProfileId = "11111111111111111111111111111111", IsFresh = true };
        public CraftingCalculatorSnapshot Scenario => new() { ProfileId = Stock.ProfileId, SelectedRecipeId = 11, TargetItemId = 2,
            SimulationSamples = 5, ObservedLevels = ImmutableDictionary<string, int>.Empty.Add("Blacksmithing", 0),
            BuyPrices = ImmutableDictionary<int, CraftingPrice>.Empty.Add(4, new(4, 1, CraftingPriceSource.Manual)),
            SellPrices = ImmutableDictionary<int, CraftingPrice>.Empty.Add(2, new(2, 100, CraftingPriceSource.Manual)) };
        public Fixture() { Store = new(Path, Clock); Service = Create(Store); }
        public CraftingProgressService Create(CraftingProgressStore store,
            Func<CraftingPlanRequest, CancellationToken, CraftingPlanResult>? calculate = null) => new(Source, store, Catalog(), () => Stock,
            () => Stock.ProfileId, () => Persistent, time: Clock, calculate: calculate);
        public CommittedLootFact Fact(int item, long quantity, LootTrackerTimelineEventKind kind = LootTrackerTimelineEventKind.Drop, string? profile = null)
        {
            Clock.Advance(TimeSpan.FromMilliseconds(1));
            return new("test-session", profile ?? Stock.ProfileId!, new(Guid.NewGuid().ToString("N"), kind, Clock.GetUtcNow(), TimeSpan.Zero)
                { Loot = [new("Item:" + item, "Example item", LootTrackerLootKind.Item, quantity)] }, new());
        }
        public void Dispose()
        {
            Service.Dispose(); Store.Dispose();
            var directory = System.IO.Path.GetDirectoryName(Path)!;
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
    private static CraftingCatalog Catalog() => new("test",
        Enumerable.Range(1, 4).Select(id => new CraftingItem { Id = id, NameDe = "Testteil " + id, NameEn = "Example item " + id, IsTradable = true }),
        [new() { Id = 10, Profession = "Blacksmithing", Subcategory = "Mace", OutputItemId = 1, OutputQuantity = 1,
            Ingredients = [new() { ItemId = 4, Quantity = 2 }], ComboRecipeId = 11, ComboActivationChance = 1,
            RequiredProficiency = 0, SuccessRates = [1], CraftingFee = 0, MechanicsQualified = true },
         new() { Id = 11, Profession = "Blacksmithing", Subcategory = "Mace", OutputItemId = 2, OutputQuantity = 1,
            RequiredProficiency = 0, SuccessRates = [1], CraftingFee = 0, MechanicsQualified = true },
         new() { Id = 12, Profession = "Blacksmithing", Subcategory = "Mace", OutputItemId = 3, OutputQuantity = 1,
            RequiredProficiency = 0, SuccessRates = [1], CraftingFee = 0, MechanicsQualified = true }]);
    private sealed class Source : ICommittedLootFactSource
    {
        public LootTrackerSnapshot Snapshot => LootTrackerSnapshot.Empty;
        public event Action? Changed { add { } remove { } }
        public event Action<string>? SessionDeleted { add { } remove { } }
        public event Action<CommittedLootFact>? LootFactCommitted;
        public void Publish(CommittedLootFact fact) => LootFactCommitted?.Invoke(fact);
    }
    private sealed class Clock : TimeProvider
    {
        private long _stamp;
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero).AddTicks(Interlocked.Read(ref _stamp));
        public override long GetTimestamp() => Interlocked.Read(ref _stamp);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan duration) => Interlocked.Add(ref _stamp, duration.Ticks);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new Timer();
        private sealed class Timer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
