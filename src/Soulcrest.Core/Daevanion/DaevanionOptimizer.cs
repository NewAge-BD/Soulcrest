using System.Diagnostics;
using Google.OrTools.Sat;

namespace Soulcrest.Core.Daevanion;

/// <summary>Exact offline rooted connected-subgraph optimization; no fixed search time limit.</summary>
public sealed partial class DaevanionOptimizer : IDaevanionOptimizer
{
    public Task<DaevanionOptimizationResult> OptimizeAsync(DaevanionOptimizationRequest request,
        IProgress<DaevanionOptimizationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Freeze lists before dispatch: edits in the UI cannot change an in-flight model.
        DaevanionOptimizationRequest snapshot;
        try { snapshot = DaevanionPoolSettings.Freeze(request); }
        catch (ArgumentException error)
        { return Task.FromResult(Result(request.ClassId, DaevanionOptimizationStatus.Invalid, [], error.Message)); }
        return Task.Run(() => Optimize(snapshot, progress, cancellationToken), CancellationToken.None);
    }

    private static DaevanionOptimizationResult Optimize(DaevanionOptimizationRequest request,
        IProgress<DaevanionOptimizationProgress>? progress, CancellationToken token)
    {
        try
        {
            var score = DaevanionScoring.Compute(request);
            var unavailable = DaevanionCombatScoreModel.UnavailableReason(request);
            if (unavailable is not null)
                return Result(request.ClassId, DaevanionOptimizationStatus.Unavailable, [],
                    unavailable);
            var rules = DaevanionRuleCatalog.Default.ForClass(request.ClassId);
            if (request.CharacterLevel is { } level)
            {
                var mandatory = DaevanionPoolSettings.RequiredNodeIds(request);
                var locked = rules.Where(board => level < board.MinimumLevel).ToArray();
                if (locked.Any(board => board.Nodes.Any(node => mandatory.Contains(node.Node.Id))))
                    return Result(request.ClassId, DaevanionOptimizationStatus.Infeasible, [],
                        "A required node belongs to a board that is locked at the observed character level.");
                // Keep all six roots in the result while excluding every unavailable purchase.
                rules = rules.Select(board => level >= board.MinimumLevel ? board : board with
                {
                    Nodes = Array.AsReadOnly(board.Nodes.Where(node => node.Node.Kind == DaevanionNodeKind.Start)
                        .Select(node => node with { NeighborIds = Array.Empty<int>() }).ToArray())
                }).ToArray();
            }
            var result = request.Mode == DaevanionOptimizationMode.CombatScore || DaevanionCombatScoreModel.HasTargets(request)
                ? OptimizeGlobal(request, rules, score, progress, token)
                : OptimizeGraphs(request.ClassId, rules, score, request.Budgets, progress, token,
                    DaevanionPoolSettings.RequiredNodeIds(request));
            if (result.Status == DaevanionOptimizationStatus.Infeasible
                || result.Status == DaevanionOptimizationStatus.Cancelled && result.Boards.Count == 0) return result;
            var error = DaevanionPlanValidator.Validate(request, result);
            return error is null ? result : Result(request.ClassId, DaevanionOptimizationStatus.Invalid, [], error);
        }
        catch (Exception error) when (error is ArgumentException or OverflowException or InvalidDataException)
        {
            return Result(request.ClassId, DaevanionOptimizationStatus.Invalid, [], error.Message);
        }
    }

    internal static DaevanionOptimizationResult OptimizeGraphs(string classId,
        IReadOnlyList<DaevanionBoardRules> rules, DaevanionScoreModel score, DaevanionBudgets budgets,
        IProgress<DaevanionOptimizationProgress>? progress, CancellationToken token, IReadOnlySet<int>? requiredNodeIds = null)
    {
        var clock = Stopwatch.StartNew();
        using var publication = new ProgressPublisher(progress, clock);
        var plans = rules.ToDictionary(board => board.Board.Id, board => BuildPlan(board,
            new HashSet<int> { board.Nodes.Single(node => node.Node.Kind == DaevanionNodeKind.Start).Node.Id }, score));
        var required = requiredNodeIds ?? new HashSet<int>();
        var seeds = new Dictionary<DaevanionBudgetPool, HashSet<int>>();
        IReadOnlyList<DaevanionBoardPlan> CompletePlans()
        {
            var all = plans.Values.ToArray();
            if (Enum.GetValues<DaevanionBudgetPool>().Any(pool => all.Where(plan => plan.Pool == pool).Sum(plan => plan.Cost) > budgets.For(pool))) return [];
            return required.IsSubsetOf(all.SelectMany(plan => plan.NodeIds).ToHashSet()) ? all : [];
        }
        void Publish(DaevanionBudgetPool pool, string stage, bool force = false, double? bound = null)
        {
            var complete = CompletePlans();
            publication.Publish(new() { Pool = pool, Stage = stage, Elapsed = clock.Elapsed,
                BestScore = complete.Count == 0 ? null : complete.Where(plan => plan.Pool == pool).Sum(plan => plan.Score),
                BestBound = bound, Boards = complete }, force);
        }
        foreach (var pool in Enum.GetValues<DaevanionBudgetPool>())
        {
            if (token.IsCancellationRequested) return Result(classId, DaevanionOptimizationStatus.Cancelled, CompletePlans());
            var boards = rules.Where(board => board.Pool == pool).ToArray();
            if (boards.Length == 0) continue;
            if (boards.SelectMany(board => board.Nodes).Where(node => required.Contains(node.Node.Id)).Sum(node => node.Cost) > budgets.For(pool))
                return Result(classId, DaevanionOptimizationStatus.Infeasible, [], $"Die Pflichtknoten im Punktepool {pool} überschreiten das Budget.");
            var seed = Seed(boards, score, budgets.For(pool), required, token);
            seeds.Add(pool, seed);
            foreach (var board in boards) plans[board.Board.Id] = BuildPlan(board, seed, score);
        }
        foreach (var pool in Enum.GetValues<DaevanionBudgetPool>())
        {
            if (token.IsCancellationRequested) return Result(classId, DaevanionOptimizationStatus.Cancelled, CompletePlans());
            var boards = rules.Where(board => board.Pool == pool).ToArray();
            if (boards.Length == 0) continue;
            var seed = seeds[pool];
            var seedFeasible = required.Where(id => boards.Any(board => board.Nodes.Any(node => node.Node.Id == id))).All(seed.Contains);
            var model = new CpModel();
            var variables = new Dictionary<int, BoolVar>();
            var allVariables = new List<IntVar>();
            var nodes = boards.SelectMany(board => board.Nodes).ToArray();
            foreach (var node in nodes)
            {
                var variable = model.NewBoolVar("n" + node.Node.Id);
                variables.Add(node.Node.Id, variable); allVariables.Add(variable);
                if (seedFeasible) model.AddHint(variable, seed.Contains(node.Node.Id));
                if (required.Contains(node.Node.Id)) model.Add(variable == 1);
            }
            foreach (var board in boards) AddConnectivity(model, board, variables, budgets.For(pool),
                seedFeasible ? plans[board.Board.Id] : null, allVariables);
            var all = nodes.Select(node => variables[node.Node.Id]).ToArray();
            var utility = LinearExpr.WeightedSum(all, nodes.Select(node => score.Scores[node.Node.Id]));
            var ignored = LinearExpr.WeightedSum(all, nodes.Select(node => score.Ignored[node.Node.Id]));
            var cost = LinearExpr.WeightedSum(all, nodes.Select(node => (long)node.Cost));
            model.Add(cost <= budgets.For(pool));
            var invalid = model.Validate();
            if (invalid.Length > 0) throw new InvalidDataException("Invalid optimization model: " + invalid);
            foreach (var stage in new[] { "Score", "Ignore", "Cost" })
            {
                if (token.IsCancellationRequested) return Result(classId, DaevanionOptimizationStatus.Cancelled, CompletePlans());
                if (stage == "Score") model.Maximize(utility);
                else model.Minimize(stage == "Ignore" ? ignored : cost);
                using var solver = new CpSolver { StringParameters = "num_search_workers:1 random_seed:0 log_search_progress:false "
                    + "absolute_gap_limit:0 relative_gap_limit:0 max_number_of_conflicts:9223372036854775807" };
                using var callback = new Incumbent(variables, selected =>
                {
                    foreach (var board in boards) plans[board.Board.Id] = BuildPlan(board, selected, score);
                    Publish(pool, stage);
                }, token);
                Publish(pool, stage, force: true);
                // Bounds refer to this pool's current objective, in displayed score/ignore units or point costs.
                solver.SetBestBoundCallback(bound => publication.UpdateBound(stage == "Cost" ? bound : bound / score.Scale));
                // Polling also covers cancellation immediately before native Solve creates its wrapper.
                var gate = new object(); var finished = false;
                using var cancellationPoll = new Timer(_ =>
                {
                    if (!token.IsCancellationRequested) return;
                    lock (gate) if (!finished) solver.StopSearch();
                }, null, 0, 50);
                CpSolverStatus status;
                try { status = solver.Solve(model, callback); }
                finally
                {
                    lock (gate) finished = true;
                    solver.ClearBestBoundCallback();
                }
                if (status is CpSolverStatus.Optimal or CpSolverStatus.Feasible)
                {
                    var selected = variables.Where(entry => solver.BooleanValue(entry.Value)).Select(entry => entry.Key).ToHashSet();
                    foreach (var board in boards) plans[board.Board.Id] = BuildPlan(board, selected, score);
                }
                Publish(pool, stage, force: true, bound: status is CpSolverStatus.Optimal or CpSolverStatus.Feasible
                    ? solver.BestObjectiveBound / (stage == "Cost" ? 1 : (double)score.Scale) : null);
                if (token.IsCancellationRequested || status == CpSolverStatus.Unknown)
                    return Result(classId, DaevanionOptimizationStatus.Cancelled, CompletePlans());
                if (status == CpSolverStatus.Infeasible)
                    return Result(classId, DaevanionOptimizationStatus.Infeasible, [],
                        $"Die Pflichtknoten und ihre Verbindungen im Punktepool {pool} sind mit dem Budget nicht erreichbar.");
                if (status != CpSolverStatus.Optimal)
                    throw new InvalidDataException("Optimization did not prove an optimum: " + status);
                var objective = stage == "Score" ? utility : stage == "Ignore" ? ignored : cost;
                model.Add(objective == solver.Value(objective));
                model.ClearHints();
                foreach (var variable in allVariables) model.AddHint(variable, solver.Value(variable));
            }
        }
        return Result(classId, DaevanionOptimizationStatus.Optimal, CompletePlans());
    }

    private static void AddConnectivity(CpModel model, DaevanionBoardRules board,
        IReadOnlyDictionary<int, BoolVar> variables, int budget, DaevanionBoardPlan? seed, List<IntVar> allVariables)
    {
        var root = board.Nodes.Single(node => node.Node.Kind == DaevanionNodeKind.Start).Node.Id;
        model.Add(variables[root] == 1);
        var byId = board.Nodes.ToDictionary(node => node.Node.Id);
        var distances = board.Nodes.ToDictionary(node => node.Node.Id, _ => int.MaxValue);
        distances[root] = 0;
        var queue = new PriorityQueue<int, int>(); queue.Enqueue(root, 0);
        while (queue.TryDequeue(out var id, out var distance))
        {
            if (distance != distances[id]) continue;
            foreach (var next in byId[id].NeighborIds)
            {
                var candidate = distance + byId[next].Cost;
                if (candidate >= distances[next]) continue;
                distances[next] = candidate; queue.Enqueue(next, candidate);
            }
        }
        foreach (var node in board.Nodes)
            if (distances[node.Node.Id] > budget) model.Add(variables[node.Node.Id] == 0);
        var affordable = board.Nodes.Where(node => node.Node.Id != root && distances[node.Node.Id] <= budget)
            .OrderBy(node => node.Cost).ToArray();
        var capacity = 0; long cheapest = 0;
        foreach (var node in affordable)
        {
            cheapest += node.Cost;
            if (cheapest > budget) break;
            capacity++;
        }
        var incoming = board.Nodes.ToDictionary(node => node.Node.Id, _ => new List<IntVar>());
        var outgoing = board.Nodes.ToDictionary(node => node.Node.Id, _ => new List<IntVar>());
        var hintFlows = new Dictionary<(int, int), int>();
        if (seed is not null)
        {
            var subtree = seed.NodeIds.ToDictionary(id => id, _ => 1);
            var parents = seed.Edges.ToDictionary(edge => edge.ToNodeId, edge => edge.FromNodeId);
            foreach (var id in Enumerable.Reverse(seed.ActivationOrder.Skip(1)))
            { hintFlows[(parents[id], id)] = subtree[id]; subtree[parents[id]] += subtree[id]; }
        }
        foreach (var node in board.Nodes)
            foreach (var next in node.NeighborIds)
            {
                var flow = model.NewIntVar(0, capacity, $"f{node.Node.Id}_{next}");
                allVariables.Add(flow);
                if (seed is not null) model.AddHint(flow, hintFlows.GetValueOrDefault((node.Node.Id, next)));
                model.Add(flow <= capacity * variables[node.Node.Id]);
                model.Add(flow <= capacity * variables[next]);
                outgoing[node.Node.Id].Add(flow); incoming[next].Add(flow);
            }
        foreach (var node in board.Nodes)
            if (node.Node.Id == root)
            {
                model.Add(LinearExpr.Sum(incoming[root]) == 0);
                model.Add(LinearExpr.Sum(outgoing[root]) - LinearExpr.Sum(incoming[root]) ==
                    LinearExpr.Sum(board.Nodes.Where(other => other.Node.Id != root).Select(other => variables[other.Node.Id])));
            }
            else
            {
                model.Add(LinearExpr.Sum(incoming[node.Node.Id]) - LinearExpr.Sum(outgoing[node.Node.Id]) == variables[node.Node.Id]);
                model.Add(LinearExpr.Sum(outgoing[node.Node.Id]) <= Math.Max(0, capacity - 1) * variables[node.Node.Id]);
                model.AddBoolOr(node.NeighborIds.Select(id => (ILiteral)variables[id]).Append(variables[node.Node.Id].Not()));
            }
    }

    /// <summary>A valid incumbent/hint only. CP-SAT still proves all three objectives.</summary>
    private static HashSet<int> Seed(IReadOnlyList<DaevanionBoardRules> boards, DaevanionScoreModel score, int budget,
        IReadOnlySet<int> required, CancellationToken token)
    {
        var selected = boards.Select(board => board.Nodes.Single(node => node.Node.Kind == DaevanionNodeKind.Start).Node.Id).ToHashSet();
        var remaining = budget;
        var requiredHere = boards.SelectMany(board => board.Nodes).Select(node => node.Node.Id).Where(required.Contains).ToHashSet();
        while (remaining > 0 || !requiredHere.IsSubsetOf(selected))
        {
            if (token.IsCancellationRequested) break;
            var needsRequired = !requiredHere.IsSubsetOf(selected);
            List<int>? best = null; long bestValue = 0; var bestCost = 1;
            foreach (var board in boards)
            {
                var nodes = board.Nodes.ToDictionary(node => node.Node.Id);
                var distances = nodes.Keys.ToDictionary(id => id, _ => int.MaxValue);
                var parents = new Dictionary<int, int>(); var queue = new PriorityQueue<int, (int Cost, int Id)>();
                foreach (var id in nodes.Keys.Where(selected.Contains)) { distances[id] = 0; queue.Enqueue(id, (0, id)); }
                while (queue.TryDequeue(out var id, out var priority))
                {
                    if (priority.Cost != distances[id]) continue;
                    foreach (var next in nodes[id].NeighborIds)
                    {
                        var candidate = priority.Cost + (selected.Contains(next) ? 0 : nodes[next].Cost);
                        if (candidate >= distances[next] || candidate > remaining) continue;
                        distances[next] = candidate; parents[next] = id; queue.Enqueue(next, (candidate, next));
                    }
                }
                foreach (var id in nodes.Keys.Where(id => !selected.Contains(id) && distances[id] <= remaining
                    && (!needsRequired || requiredHere.Contains(id))))
                {
                    var path = new List<int>(); var at = id;
                    while (!selected.Contains(at)) { path.Add(at); at = parents[at]; }
                    var value = needsRequired ? path.Count(requiredHere.Contains) : path.Sum(next => score.Scores[next]);
                    var cost = distances[id];
                    if (value <= 0 || best is not null && value * bestCost <= bestValue * cost) continue;
                    best = path; bestValue = value; bestCost = cost;
                }
            }
            if (best is null) break;
            selected.UnionWith(best); remaining -= bestCost;
        }
        return selected;
    }

    internal static DaevanionBoardPlan BuildPlan(DaevanionBoardRules rules, IReadOnlySet<int> selected, DaevanionScoreModel score)
    {
        var root = rules.Nodes.Single(node => node.Node.Kind == DaevanionNodeKind.Start).Node.Id;
        var byId = rules.Nodes.ToDictionary(node => node.Node.Id);
        var order = new List<int> { root }; var seen = new HashSet<int> { root }; var edges = new List<DaevanionPlanEdge>();
        for (var index = 0; index < order.Count; index++)
            foreach (var next in byId[order[index]].NeighborIds.Where(selected.Contains)
                .OrderBy(id => byId[id].Node.Row).ThenBy(id => byId[id].Node.Column).ThenBy(id => id))
                if (seen.Add(next)) { order.Add(next); edges.Add(new(order[index], next)); }
        var ids = rules.Nodes.Where(node => selected.Contains(node.Node.Id)).Select(node => node.Node.Id).ToArray();
        if (!selected.Contains(root) || ids.Length != order.Count) throw new InvalidDataException("Disconnected plan.");
        return new() { BoardId = rules.Board.Id, FamilyId = rules.Board.FamilyId, Pool = rules.Pool,
            NodeIds = ids, ActivationOrder = order.ToArray(), Edges = edges.ToArray(),
            Cost = ids.Sum(id => byId[id].Cost), Score = ids.Sum(id => score.Scores[id]) / (decimal)score.Scale };
    }

    private static DaevanionOptimizationResult Result(string classId, DaevanionOptimizationStatus status,
        IReadOnlyList<DaevanionBoardPlan> boards, string? message = null) => new()
    {
        Status = status, ClassId = classId, CatalogVersion = DaevanionCatalog.Default.DataVersion,
        CatalogFingerprint = DaevanionRuleCatalog.Default.CatalogFingerprint,
        RulesVersion = DaevanionRuleCatalog.Default.Version, Boards = boards, Message = message
    };

    private sealed class Incumbent(IReadOnlyDictionary<int, BoolVar> variables, Action<IReadOnlySet<int>> publish,
        CancellationToken token) : CpSolverSolutionCallback
    {
        private long _last;
        public override void OnSolutionCallback()
        {
            if (token.IsCancellationRequested) { StopSearch(); return; }
            var now = Stopwatch.GetTimestamp();
            if (_last != 0 && Stopwatch.GetElapsedTime(_last, now) < TimeSpan.FromMilliseconds(200)) return;
            _last = now;
            publish(variables.Where(entry => BooleanValue(entry.Value)).Select(entry => entry.Key).ToHashSet());
        }
    }

    /// <summary>Only immutable published snapshots cross into the heartbeat thread.</summary>
    private sealed class ProgressPublisher : IDisposable
    {
        private readonly object _gate = new();
        private readonly IProgress<DaevanionOptimizationProgress>? _progress;
        private readonly Stopwatch _clock;
        private readonly Timer _heartbeat;
        private DaevanionOptimizationProgress? _latest;
        private long _lastReport;
        private bool _disposed;

        internal ProgressPublisher(IProgress<DaevanionOptimizationProgress>? progress, Stopwatch clock)
        {
            _progress = progress; _clock = clock;
            _heartbeat = new(_ => Heartbeat(), null, 1000, 1000);
        }

        internal void Publish(DaevanionOptimizationProgress snapshot, bool force = false)
        {
            lock (_gate)
            {
                if (_disposed) return;
                if (snapshot.BestBound is null && _latest?.Pool == snapshot.Pool && _latest.Stage == snapshot.Stage)
                    snapshot = snapshot with { BestBound = _latest.BestBound };
                _latest = snapshot;
                Report(force);
            }
        }

        internal void UpdateBound(double bound)
        {
            if (!double.IsFinite(bound)) return;
            lock (_gate)
            {
                if (_disposed || _latest is null) return;
                _latest = _latest with { BestBound = bound, Elapsed = _clock.Elapsed };
                Report(false);
            }
        }

        private void Heartbeat()
        {
            lock (_gate)
            {
                if (_disposed || _latest is null) return;
                _latest = _latest with { Elapsed = _clock.Elapsed };
                Report(false);
            }
        }

        // Called under _gate so disposal cannot race a late timer publication.
        private void Report(bool force)
        {
            var now = Stopwatch.GetTimestamp();
            if (!force && _lastReport != 0 && Stopwatch.GetElapsedTime(_lastReport, now) < TimeSpan.FromMilliseconds(200)) return;
            _lastReport = now;
            try { if (_latest is not null) _progress?.Report(_latest); }
            catch (Exception error) { Trace.TraceWarning("Daevanion progress observer: {0}", error.GetType().Name); }
        }

        public void Dispose()
        {
            lock (_gate) { _disposed = true; _latest = null; }
            _heartbeat.Dispose();
        }
    }
}
