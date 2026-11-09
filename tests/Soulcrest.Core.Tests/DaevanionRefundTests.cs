using Soulcrest.Core.Daevanion;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class DaevanionRefundTests
{
    [Fact]
    public void ChainProtectsEveryConnectorAndOnlyRefundsTheEnd()
    {
        var board = Board((1, 0, 0), (2, 1, 0), (3, 2, 0), (4, 3, 0));
        var active = new HashSet<int> { 1, 2, 3, 4 };
        var result = DaevanionRefundRules.Evaluate(board, active, true);

        Assert.Equal(DaevanionRefundStatus.Complete, result.Status);
        Assert.Equal([4], result.RefundableNodeIds);
        Assert.False(result.CanRefund(2));
        Assert.True(result.CanRefund(4));
        Assert.True(active.SetEquals([1, 2, 3, 4]));
    }

    [Fact]
    public void BranchListsEveryRefundableEndWithoutDetachingItsSiblings()
    {
        var board = Board((1, 0, 0), (2, 1, 0), (3, 2, 0), (4, 1, 1), (5, 0, -1));
        var result = DaevanionRefundRules.Evaluate(board, [1, 2, 3, 4, 5], true);

        Assert.Equal([3, 4, 5], result.RefundableNodeIds);
        Assert.False(result.CanRefund(2));
    }

    [Fact]
    public void ActiveCycleAllowsNonLeafRefundsThroughTheAlternativePath()
    {
        var board = Board((1, 0, 0), (2, 1, 0), (3, 1, 1), (4, 0, 1));
        var result = DaevanionRefundRules.Evaluate(board, [1, 2, 3, 4], true);

        Assert.Equal([2, 3, 4], result.RefundableNodeIds);
        Assert.All(result.RefundableNodeIds, id => Assert.Equal(2, board.Nodes.Single(row => row.Node.Id == id).NeighborIds.Count));
    }

    [Fact]
    public void CycleDoesNotMakeItsAttachedBranchConnectorRefundable()
    {
        var board = Board((1, 0, 0), (2, 1, 0), (3, 1, 1), (4, 0, 1), (5, 2, 1));
        var result = DaevanionRefundRules.Evaluate(board, [1, 2, 3, 4, 5], true);

        Assert.Equal([2, 4, 5], result.RefundableNodeIds);
        Assert.False(result.CanRefund(3));
    }

    [Fact]
    public void InactiveAlternativePathCannotCarryAConnection()
    {
        var board = Board((1, 0, 0), (2, 1, 0), (3, 1, 1), (4, 0, 1));
        var result = DaevanionRefundRules.Evaluate(board, [1, 2, 3], true);

        Assert.Equal([3], result.RefundableNodeIds);
        Assert.False(result.CanRefund(2));
        Assert.False(result.CanRefund(4));
    }

    [Fact]
    public void FixedCenterIsImplicitAndNeverRefundable()
    {
        var board = Board((1, 0, 0), (2, 1, 0));
        var implicitCenter = DaevanionRefundRules.Evaluate(board, [2], true);
        var explicitCenter = DaevanionRefundRules.Evaluate(board, [1, 2], true);

        Assert.Equal([2], implicitCenter.RefundableNodeIds);
        Assert.Equal(implicitCenter.RefundableNodeIds, explicitCenter.RefundableNodeIds);
        Assert.False(implicitCenter.CanRefund(1));
        Assert.False(explicitCenter.CanRefund(1));
    }

    [Fact]
    public void UnknownOrForeignIdsInvalidateTheWholeActivationSet()
    {
        var board = Board((1, 0, 0), (2, 1, 0));
        var result = DaevanionRefundRules.Evaluate(board, [2, 999], true);

        Assert.Equal(DaevanionRefundStatus.InvalidActivation, result.Status);
        Assert.Empty(result.RefundableNodeIds);
        Assert.False(result.CanRefund(2));
        Assert.False(result.CanRefund(999));
        Assert.False(DaevanionRefundRules.Evaluate(board, [2], true).CanRefund(999));
    }

    [Fact]
    public void AlreadyDisconnectedActivationFailsClosed()
    {
        var board = Board((1, 0, 0), (2, 1, 0), (3, 2, 0));
        var result = DaevanionRefundRules.Evaluate(board, [3], true);

        Assert.Equal(DaevanionRefundStatus.InvalidActivation, result.Status);
        Assert.Empty(result.RefundableNodeIds);
    }

    [Fact]
    public void UnknownActivationIsNotPresentedAsACompleteEmptyList()
    {
        var board = Board((1, 0, 0), (2, 1, 0));
        foreach (var result in new[] { DaevanionRefundRules.Evaluate(board, null, true),
                     DaevanionRefundRules.Evaluate(board, [2], false) })
        {
            Assert.Equal(DaevanionRefundStatus.UnknownActivation, result.Status);
            Assert.Empty(result.RefundableNodeIds);
            Assert.False(result.CanRefund(2));
        }
        Assert.Equal(DaevanionRefundStatus.Complete, DaevanionRefundRules.Evaluate(board, [], true).Status);
    }

    [Fact]
    public void RealCatalogBoardsUseTheirCompleteNeighborGraphAndProtectTheirCenter()
    {
        foreach (var board in DaevanionRuleCatalog.Default.Boards)
        {
            var result = DaevanionRefundRules.Evaluate(board, board.Nodes.Select(row => row.Node.Id), true);
            Assert.Equal(DaevanionRefundStatus.Complete, result.Status);
            Assert.NotEmpty(result.RefundableNodeIds);
            Assert.False(result.CanRefund(board.Nodes.Single(row => row.Node.Kind == DaevanionNodeKind.Start).Node.Id));
        }
    }

    private static DaevanionBoardRules Board(params (int Id, int Row, int Column)[] positions)
    {
        var nodes = positions.Select(position => new DaevanionNode
        {
            Id = position.Id, Row = position.Row, Column = position.Column,
            Kind = position.Id == 1 ? DaevanionNodeKind.Start : DaevanionNodeKind.Stat,
            Quality = position.Id == 1 ? DaevanionNodeQuality.None : DaevanionNodeQuality.Common
        }).ToArray();
        return new(new() { Id = 1, ClassId = "Cleric", FamilyId = "nezekan", Nodes = nodes },
            DaevanionBudgetPool.Normal, 12, nodes.Select(node => new DaevanionNodeRule(node, node.Id == 1 ? 0 : 1,
                nodes.Where(other => Math.Abs(other.Row - node.Row) + Math.Abs(other.Column - node.Column) == 1)
                    .Select(other => other.Id).ToArray())).ToArray());
    }
}
