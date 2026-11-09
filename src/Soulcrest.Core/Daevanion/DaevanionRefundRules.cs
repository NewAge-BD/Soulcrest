namespace Soulcrest.Core.Daevanion;

public enum DaevanionRefundStatus { UnknownActivation, InvalidActivation, Complete }

/// <summary>Single-refund choices for the supplied activation snapshot, not evidence of current game state.</summary>
public sealed record DaevanionRefundAvailability(DaevanionRefundStatus Status, IReadOnlyList<int> RefundableNodeIds)
{
    public bool CanRefund(int nodeId) => Status == DaevanionRefundStatus.Complete && RefundableNodeIds.Contains(nodeId);
}

/// <summary>Removing one purchased node must leave every other active node connected to the fixed center.</summary>
public static class DaevanionRefundRules
{
    /// <param name="activationIsComplete">True only when the caller has the complete activation set for this board.
    /// Character freshness or an old plan alone does not establish this.</param>
    /// <remarks>The start node is implicit and cannot be refunded. Only active nodes can carry a connection;
    /// all catalog neighbor edges count, including alternative paths absent from a plan's route tree.</remarks>
    public static DaevanionRefundAvailability Evaluate(DaevanionBoardRules board,
        IEnumerable<int>? activeNodeIds, bool activationIsComplete)
    {
        ArgumentNullException.ThrowIfNull(board);
        if (!activationIsComplete || activeNodeIds is null)
            return new(DaevanionRefundStatus.UnknownActivation, []);

        var nodes = board.Nodes.ToDictionary(row => row.Node.Id);
        var roots = board.Nodes.Where(row => row.Node.Kind == DaevanionNodeKind.Start).ToArray();
        var active = activeNodeIds.ToHashSet();
        if (roots.Length != 1 || active.Any(id => !nodes.ContainsKey(id)))
            return new(DaevanionRefundStatus.InvalidActivation, []);

        var root = roots[0].Node.Id;
        active.Add(root);
        if (!ConnectedWithout(null)) return new(DaevanionRefundStatus.InvalidActivation, []);

        var refundable = active.Where(id => id != root && ConnectedWithout(id)).Order().ToArray();
        return new(DaevanionRefundStatus.Complete, Array.AsReadOnly(refundable));

        bool ConnectedWithout(int? removed)
        {
            var reached = new HashSet<int> { root };
            var queue = new Queue<int>();
            queue.Enqueue(root);
            while (queue.TryDequeue(out var id))
                foreach (var next in nodes[id].NeighborIds)
                    if (next != removed && active.Contains(next) && reached.Add(next)) queue.Enqueue(next);
            return reached.Count == active.Count - (removed.HasValue ? 1 : 0);
        }
    }
}
