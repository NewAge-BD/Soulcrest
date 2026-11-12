using System.Collections.Immutable;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class CraftingStockServiceTests
{
    private const string First = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Second = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const int Material = 610660025;
    private static readonly DateTimeOffset At = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PartialAutomaticMinimumIsUsableButCannotReplaceLegacyAggregate()
    {
        using var f = new Fixture(legacy: 20);
        f.Source.Publish(3);
        Assert.Equal(20, f.Service.Snapshot.EffectiveItems[Material].Quantity);
        Assert.Equal(StockOrigin.LegacyManual, f.Service.Snapshot.EffectiveItems[Material].Origin);
        Assert.Equal(3, f.Service.Snapshot.ObservedMinimumItems[Material]);
        Assert.False(f.Service.Snapshot.IsComplete);
        Assert.Equal(StockCompleteness.Unknown, f.Service.Snapshot.Scopes.Single(scope => scope.Scope == StockScope.CharacterStorage).Completeness);
    }

    [Fact]
    public void WithoutLegacyConfirmedPartialMinimumReducesPurchasesWithoutClaimingCompleteness()
    {
        using var f = new Fixture(); f.Source.Publish(3);
        var item = f.Service.Snapshot.EffectiveItems[Material];
        Assert.Equal(3, item.Quantity); Assert.Equal(StockOrigin.Automatic, item.Origin);
        Assert.Equal(StockFreshness.Fresh, item.Freshness); Assert.Equal(StockCompleteness.Partial, item.Completeness);
        Assert.False(f.Service.Snapshot.EffectiveItems.ContainsKey(777));
    }

    [Fact]
    public void ProtectedZeroSurvivesAutomaticChangesAndRestartUntilExplicitAutomaticSelection()
    {
        using var f = new Fixture(legacy: 20); f.Source.Publish(3);
        f.Service.SetOverride(Material, 0); f.Source.Publish(9);
        Assert.Equal(0, f.Service.Snapshot.EffectiveItems[Material].Quantity);
        Assert.Equal(StockOrigin.ManualOverride, f.Service.Snapshot.EffectiveItems[Material].Origin);
        f.Store.Flush();
        using var restoredStore = new CraftingStockStore(f.StockFile);
        Assert.Equal(0, restoredStore.Read(First)!.Overrides[Material]);
        f.Service.UseAutomatic(Material);
        Assert.Equal(9, f.Service.Snapshot.EffectiveItems[Material].Quantity);
        Assert.Equal(StockOrigin.Automatic, f.Service.Snapshot.EffectiveItems[Material].Origin);
    }

    [Fact]
    public void FirstCompleteSameScopeAggregationReplacesLegacyButLaterUserCorrectionWins()
    {
        using var f = new Fixture(legacy: 20); f.Source.Publish(3, complete: true);
        Assert.Equal(3, f.Service.Snapshot.EffectiveItems[Material].Quantity);
        Assert.Equal(StockOrigin.Automatic, f.Service.Snapshot.EffectiveItems[Material].Origin);
        f.Service.SetOverride(Material, 7); f.Source.Publish(4, complete: true);
        Assert.Equal(7, f.Service.Snapshot.EffectiveItems[Material].Quantity);
    }

    [Fact]
    public void HistoricalMinimumIsLabelledAfterInvalidationAndDoesNotLeakIntoAnotherCharacter()
    {
        using var f = new Fixture(); f.Source.Publish(3); f.Source.Invalidate();
        Assert.False(f.Service.Snapshot.IsFresh);
        Assert.Equal(StockFreshness.Historical, f.Service.Snapshot.EffectiveItems[Material].Freshness);
        f.Selected = Second; f.Source.Invalidate();
        Assert.Equal(Second, f.Service.Snapshot.ProfileId);
        Assert.Empty(f.Service.Snapshot.EffectiveItems);
        Assert.Equal(3, f.Service.Read(First).EffectiveItems[Material].Quantity);
        Assert.Equal(StockFreshness.Historical, f.Service.Read(First).EffectiveItems[Material].Freshness);
    }

    [Fact]
    public void AutomaticQuantitiesAreNotCapturedAsManualScenarioPreferences()
    {
        using var f = new Fixture(); f.Source.Publish(3);
        var stock = f.Service.Snapshot;
        var draft = CraftingScenarioDraft.Capture(new() { ProfileId = First, Stock = stock, OwnedItems = stock.EffectiveItems.ToImmutableDictionary(pair => pair.Key, pair => pair.Value.Quantity) }, 1);
        Assert.Empty(draft.OwnedItems);
        f.Service.SetOverride(Material, 0);
        stock = f.Service.Snapshot;
        draft = CraftingScenarioDraft.Capture(new() { ProfileId = First, Stock = stock, OwnedItems = ImmutableDictionary<int, long>.Empty.Add(Material, 0) }, 1);
        Assert.True(draft.OwnedItems.ContainsKey(Material)); Assert.Equal(0, draft.OwnedItems[Material]);
    }

    [Fact]
    public void PartialNewObservationRetainsOtherCachedItemsWithTheirOwnHistoricalFactTime()
    {
        using var f = new Fixture(); f.Source.Publish(3);
        var firstAt = f.Service.Snapshot.EffectiveItems[Material].ObservedAt;
        f.Source.Invalidate();
        f.Source.Publish(9, itemId: 610660028);
        var old = f.Service.Snapshot.EffectiveItems[Material];
        Assert.Equal(3, old.Quantity); Assert.Equal(StockFreshness.Historical, old.Freshness); Assert.Equal(firstAt, old.ObservedAt);
        var updated = f.Service.Snapshot.EffectiveItems[610660028];
        Assert.Equal(9, updated.Quantity); Assert.Equal(StockFreshness.Fresh, updated.Freshness);
        Assert.True(updated.ObservedAt > firstAt);
        Assert.Equal(StockFreshness.Historical, f.Service.Snapshot.ObservedItems[Material].Freshness);
    }

    [Fact]
    public void CacheContainsOnlyAggregatesAndCorruptCacheRemainsUnmodified()
    {
        using var f = new Fixture(); f.Source.Publish(3); f.Store.Flush();
        var text = File.ReadAllText(f.StockFile);
        Assert.DoesNotContain("InstanceKey", text); Assert.DoesNotContain("SyntheticStock", text);
        Assert.DoesNotContain("EntityKey", text); Assert.DoesNotContain("CharacterId", text);
        var corrupt = Path.Combine(f.DirectoryPath, "corrupt.json"); File.WriteAllText(corrupt, "{broken");
        using var store = new CraftingStockStore(corrupt);
        store.Save(new() { ProfileId = First, Overrides = ImmutableDictionary<int, long>.Empty.Add(Material, 0) }); store.Flush();
        Assert.NotNull(store.StorageError); Assert.Equal("{broken", File.ReadAllText(corrupt));
    }

    private sealed class Fixture : IDisposable
    {
        internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "soulcrest-stock-" + Guid.NewGuid().ToString("N"));
        internal string StockFile => Path.Combine(DirectoryPath, "stock.json");
        internal string Selected = First;
        internal readonly FakeSource Source = new();
        internal CraftingStockStore Store { get; }
        internal CraftingScenarioStore Scenarios { get; }
        internal CraftingStockService Service { get; }
        internal Fixture(long? legacy = null)
        {
            Directory.CreateDirectory(DirectoryPath);
            Store = new(StockFile); Scenarios = new(Path.Combine(DirectoryPath, "scenarios.json"));
            if (legacy is { } amount) Scenarios.Stage(First, new() { OwnedItems = ImmutableDictionary<int, long>.Empty.Add(Material, amount) });
            Service = new(Source, _ => new(Selected, "Synthetic Stock"), () => Selected, Store, Scenarios);
        }
        public void Dispose() { Service.Dispose(); Store.Dispose(); Scenarios.Dispose(); Directory.Delete(DirectoryPath, recursive: true); }
    }

    private sealed class FakeSource : INetworkStockSource
    {
        private long _sequence;
        private readonly OwnCharacterContext _own = new(new(7, 9, 300, "SyntheticStock"), 1) { ObservedAt = At };
        public bool Running => true;
        public NetworkStockSnapshot StockSnapshot { get; private set; } = NetworkStockSnapshot.Empty;
        public event Action? Changed;
        public event Action? OwnCharacterChanged;
        public event Action<ItemReceipt>? ItemReceived { add { } remove { } }
        internal void Publish(long quantity, bool complete = false, int itemId = Material)
        {
            var sequence = ++_sequence;
            StockSnapshot = new(_own, ImmutableDictionary<int, long>.Empty.Add(itemId, quantity), true, At.AddSeconds(sequence), sequence)
            { Scopes = [new(StockScope.Bag, complete ? StockCompleteness.Complete : StockCompleteness.Partial),
                new(StockScope.CharacterStorage, complete ? StockCompleteness.Complete : StockCompleteness.Unknown),
                new(StockScope.ServerStorage, complete ? StockCompleteness.Complete : StockCompleteness.Unknown)] };
            Changed?.Invoke();
        }
        internal void Invalidate() { StockSnapshot = NetworkStockSnapshot.Empty; OwnCharacterChanged?.Invoke(); Changed?.Invoke(); }
    }
}
