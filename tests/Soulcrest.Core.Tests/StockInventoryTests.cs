using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class StockInventoryTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch;
    private static CurrencyInventoryItem Item(long key, long count, byte container = 1, int id = 610660025) => new(key, id, count, container);
    private static CurrencyInventoryList List(params CurrencyInventoryItem[] items) => new(528, []) { AllItems = Array.AsReadOnly(items) };
    private static CurrencyInventoryChanges Changes(params CurrencyInventoryChange[] changes) => new([]) { AllChanges = Array.AsReadOnly(changes) };
    private static CurrencyInventoryChange Change(CurrencyInventoryItem item, byte modification = 1, byte reason = 0) => new(modification, item, reason, 0, 0);

    [Fact]
    public void BaselineSumsExactItemStacksButDoesNotInferStorageOrAbsentZeros()
    {
        var reducer = new StockInventoryReducer();
        Assert.True(reducer.Baseline(List(Item(1, 3), Item(2, 4), Item(3, 100, 2), Item(4, 2, id: 610660028)), At));
        Assert.Equal(7, reducer.Snapshot.Items[610660025]);
        Assert.Equal(2, reducer.Snapshot.Items[610660028]);
        Assert.False(reducer.Snapshot.Items.ContainsKey(777));
        Assert.Equal(2, reducer.Snapshot.Items.Count);
    }

    [Fact]
    public void InstanceMoveUpdatesBagMinimumWithoutAcquisition()
    {
        var reducer = new StockInventoryReducer(); reducer.Baseline(List(Item(1, 5)), At);
        var deltas = reducer.Apply(Changes(Change(Item(1, 5, 2), reason: 5)), At.AddSeconds(1));
        Assert.Equal(0, reducer.Snapshot.Items[610660025]);
        Assert.Equal(ItemReceiptKind.Transfer, Assert.Single(deltas).Kind);
        Assert.DoesNotContain(deltas, delta => delta.Kind == ItemReceiptKind.Acquisition);
    }

    [Fact]
    public void SplitWithNewInstanceCannotCreateIncomeEvenWhenOneHalfCarriesQualifiedReason()
    {
        var reducer = new StockInventoryReducer(); reducer.Baseline(List(Item(1, 10)), At);
        var deltas = reducer.Apply(Changes(Change(Item(1, 6)), Change(Item(2, 4), modification: 0, reason: 5)), At.AddSeconds(1));
        Assert.Equal(10, reducer.Snapshot.Items[610660025]);
        Assert.Empty(deltas);
    }

    [Fact]
    public void UnknownPositiveAbsoluteModificationIsStockButNeverACraftOrPurchaseReceipt()
    {
        var reducer = new StockInventoryReducer(); reducer.Baseline(List(Item(1, 2)), At);
        Assert.Empty(reducer.Apply(Changes(Change(Item(1, 8), reason: 41)), At.AddSeconds(1)));
        Assert.Equal(8, reducer.Snapshot.Items[610660025]);
    }

    [Fact]
    public void QualifiedAcquisitionSharesCommittedLootAuthorityAndDuplicateDoesNotCountAgain()
    {
        var reducer = new StockInventoryReducer(); reducer.Baseline(List(Item(1, 2)), At);
        var change = Changes(Change(Item(1, 5), reason: 5));
        var receipt = Assert.Single(reducer.Apply(change, At.AddSeconds(1)));
        Assert.Equal(3, receipt.Quantity); Assert.Equal(ItemReceiptKind.Acquisition, receipt.Kind);
        Assert.True(receipt.CoveredByCommittedLoot);
        Assert.Empty(reducer.Apply(change, At.AddSeconds(2)));
        Assert.Equal(5, reducer.Snapshot.Items[610660025]);
    }

    [Fact]
    public void InvalidBatchRevokesPriorStockWithoutPublishingItsValidPrefix()
    {
        var reducer = new StockInventoryReducer(); reducer.Baseline(List(Item(1, 2)), At);
        Assert.Empty(reducer.Apply(Changes(Change(Item(1, 5)), Change(Item(1, 6))), At.AddSeconds(1)));
        Assert.False(reducer.Snapshot.Available); Assert.Empty(reducer.Snapshot.Items);
    }

    [Fact]
    public void DuplicateKeysAndOverflowCannotQualifyBaseline()
    {
        var reducer = new StockInventoryReducer();
        Assert.False(reducer.Baseline(List(Item(1, 3), Item(1, 3)), At));
        Assert.False(reducer.Snapshot.Available);
        Assert.False(reducer.Baseline(List(Item(1, long.MaxValue), Item(2, 1)), At));
        Assert.Empty(reducer.Snapshot.Items);
    }

    [Fact]
    public void OlderAbsoluteObservationRevokesRatherThanRollingBackCurrentStock()
    {
        var reducer = new StockInventoryReducer(); reducer.Baseline(List(Item(1, 5)), At.AddSeconds(2));
        Assert.Empty(reducer.Apply(Changes(Change(Item(1, 2))), At.AddSeconds(1)));
        Assert.False(reducer.Snapshot.Available);
    }
}
