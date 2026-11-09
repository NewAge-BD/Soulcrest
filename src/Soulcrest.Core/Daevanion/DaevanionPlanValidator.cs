namespace Soulcrest.Core.Daevanion;

/// <summary>Checks persisted and solver-produced plans independently of the optimization model.</summary>
public static class DaevanionPlanValidator
{
    /// <returns>Null for a structurally valid plan, otherwise a diagnostic. Does not re-prove optimality.</returns>
    public static string? Validate(DaevanionOptimizationRequest request, DaevanionOptimizationResult result)
    {
        try
        {
            if (request is null || result is null) return "Missing plan.";
            var unavailable = DaevanionCombatScoreModel.UnavailableReason(request);
            if (unavailable is not null) return unavailable;
            if (result.Status is not (DaevanionOptimizationStatus.Optimal or DaevanionOptimizationStatus.Cancelled))
                return "No feasible optimization result.";
            if (result.ClassId != request.ClassId || result.CatalogVersion != DaevanionCatalog.Default.DataVersion
                || result.CatalogFingerprint != DaevanionRuleCatalog.Default.CatalogFingerprint
                || result.RulesVersion != DaevanionRuleCatalog.Default.Version) return "Plan data version or class mismatch.";
            var score = DaevanionScoring.Compute(request);
            var rules = DaevanionRuleCatalog.Default.ForClass(request.ClassId);
            if (result.Boards is null || result.Boards.Count != rules.Count
                || result.Boards.Select(plan => plan?.BoardId).Distinct().Count() != rules.Count) return "Incomplete board plan.";
            var nonlinear = request.Mode == DaevanionOptimizationMode.CombatScore || DaevanionCombatScoreModel.HasTargets(request);
            var expectedScores = nonlinear ? DaevanionScoring.ScoreBoards(request, result.Boards).ToDictionary(board => board.BoardId, board => board.Score) : null;
            foreach (var board in rules)
            {
                var plan = result.Boards.SingleOrDefault(plan => plan.BoardId == board.Board.Id);
                if (plan is null || plan.FamilyId != board.Board.FamilyId || plan.Pool != board.Pool
                    || plan.NodeIds is null || plan.ActivationOrder is null || plan.Edges is null) return "Invalid board identity.";
                var ids = plan.NodeIds.ToHashSet();
                if (request.CharacterLevel is { } level && level < board.MinimumLevel
                    && ids.Any(id => board.Nodes.Any(node => node.Node.Id == id && node.Node.Kind != DaevanionNodeKind.Start)))
                    return "A planned node belongs to a locked board.";
                var original = board.Board.Nodes.ToDictionary(node => node.Id);
                var start = original.Values.Single(node => node.Kind == DaevanionNodeKind.Start).Id;
                if (ids.Count != plan.NodeIds.Count || !ids.Contains(start) || ids.Any(id => !original.ContainsKey(id)))
                    return "Unknown, duplicate or missing start node.";
                if (plan.ActivationOrder.Count != ids.Count || plan.ActivationOrder.FirstOrDefault() != start
                    || !plan.ActivationOrder.ToHashSet().SetEquals(ids)) return "Invalid activation order.";
                if (plan.Edges.Count != ids.Count - 1 || plan.Edges.Distinct().Count() != plan.Edges.Count)
                    return "Invalid route tree.";
                var activated = new HashSet<int> { start };
                foreach (var id in plan.ActivationOrder.Skip(1))
                {
                    var parents = plan.Edges.Where(edge => edge.ToNodeId == id).ToArray();
                    if (parents.Length != 1 || !activated.Contains(parents[0].FromNodeId)) return "Route parent was not activated.";
                    var parent = original[parents[0].FromNodeId]; var node = original[id];
                    if (Math.Abs(parent.Row - node.Row) + Math.Abs(parent.Column - node.Column) != 1)
                        return "Nonadjacent route edge.";
                    activated.Add(id);
                }
                if (plan.Edges.Any(edge => !ids.Contains(edge.FromNodeId) || !ids.Contains(edge.ToNodeId)
                    || edge.ToNodeId == start)) return "Invalid route endpoint.";
                var cost = ids.Sum(id => original[id].Kind == DaevanionNodeKind.Start ? 0 : original[id].Quality switch
                {
                    DaevanionNodeQuality.Common => 1, DaevanionNodeQuality.Rare => 2,
                    DaevanionNodeQuality.Legend => 3, DaevanionNodeQuality.Unique => 4,
                    _ => throw new InvalidDataException("Unknown cost.")
                });
                var expectedScore = nonlinear ? expectedScores![plan.BoardId] : ids.Sum(id => score.Scores[id]) / (decimal)score.Scale;
                if (cost != plan.Cost || expectedScore != plan.Score)
                    return "Incorrect plan totals.";
            }
            foreach (var pool in Enum.GetValues<DaevanionBudgetPool>())
                if (result.Boards.Where(board => board.Pool == pool).Sum(board => board.Cost) > request.Budgets.For(pool))
                    return "Point budget exceeded.";
            var selected = result.Boards.SelectMany(board => board.NodeIds).ToHashSet();
            if (!DaevanionPoolSettings.RequiredNodeIds(request).IsSubsetOf(selected)) return "Required nodes are missing.";
            if (request.Mode == DaevanionOptimizationMode.CombatScore)
            {
                var estimate = DaevanionCombatScoreModel.Estimate(request, selected);
                if (result.ScoreModelVersion != DaevanionCombatScoreModel.ModelVersionFor(request) || result.Score != estimate.ObjectiveDelta
                    || result.EstimatedCombatScore != estimate.ObjectiveDelta || result.ActualEstimatedCombatScore != estimate.ActualDelta
                    || result.EstimatedTotalCombatScore != estimate.Total) return "Incorrect or stale estimated Combat Score.";
            }
            else if (result.ScoreModelVersion is not null || result.EstimatedCombatScore is not null
                || result.ActualEstimatedCombatScore is not null || result.EstimatedTotalCombatScore is not null)
                return "Unexpected Combat Score metadata on a priority plan.";
            return null;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException
            or KeyNotFoundException or NullReferenceException or InvalidDataException)
        {
            return "Invalid plan: " + error.Message;
        }
    }
}
