using System.Diagnostics;
using Google.OrTools.Sat;

namespace Soulcrest.Core.Daevanion;

public sealed partial class DaevanionOptimizer
{
    internal static DaevanionOptimizationResult OptimizeGlobal(DaevanionOptimizationRequest request,
        IReadOnlyList<DaevanionBoardRules> rules, DaevanionScoreModel linearScore,
        IProgress<DaevanionOptimizationProgress>? progress, CancellationToken token, IReadOnlySet<int>? requiredOverride = null)
    {
        var clock = Stopwatch.StartNew();
        using var publication = new ProgressPublisher(progress, clock);
        var required = requiredOverride ?? DaevanionPoolSettings.RequiredNodeIds(request);
        var selected = rules.Select(board => board.Nodes.Single(node => node.Node.Kind == DaevanionNodeKind.Start).Node.Id).ToHashSet();
        var nodes = rules.SelectMany(board => board.Nodes).ToArray();
        IReadOnlyList<DaevanionBoardPlan> Plans()
        {
            if (!required.IsSubsetOf(selected)) return [];
            var plans = rules.Select(board => BuildPlan(board, selected, linearScore)).ToArray();
            if (Enum.GetValues<DaevanionBudgetPool>().Any(pool => plans.Where(board => board.Pool == pool).Sum(board => board.Cost) > request.Budgets.For(pool))) return [];
            return DaevanionScoring.ScoreBoards(request, plans);
        }
        DaevanionOptimizationResult Finish(DaevanionOptimizationStatus status, string? message = null)
        {
            IReadOnlyList<DaevanionBoardPlan> plans = status == DaevanionOptimizationStatus.Infeasible ? [] : Plans();
            var result = Result(request.ClassId, status, plans, message);
            if (request.Mode != DaevanionOptimizationMode.CombatScore || plans.Count == 0) return result;
            var estimate = DaevanionCombatScoreModel.Estimate(request, plans.SelectMany(board => board.NodeIds));
            return result with { ScoreModelVersion = DaevanionCombatScoreModel.ModelVersionFor(request),
                EstimatedCombatScore = estimate.ObjectiveDelta, ActualEstimatedCombatScore = estimate.ActualDelta,
                EstimatedTotalCombatScore = estimate.Total };
        }
        void Publish(string stage, bool force = false, double? bound = null)
        {
            var plans = Plans();
            publication.Publish(new() { GlobalObjective = true, Stage = stage, Elapsed = clock.Elapsed,
                BestScore = plans.Count == 0 ? null : plans.Sum(board => board.Score), BestBound = bound, Boards = plans }, force);
        }

        // Check every pool before starting a potentially long proof in any one of them.
        foreach (var pool in Enum.GetValues<DaevanionBudgetPool>())
            if (rules.Where(board => board.Pool == pool).SelectMany(board => board.Nodes)
                .Where(node => required.Contains(node.Node.Id)).Sum(node => node.Cost) > request.Budgets.For(pool))
                return Finish(DaevanionOptimizationStatus.Infeasible, $"Required nodes exceed the {pool} point budget.");
        foreach (var pool in Enum.GetValues<DaevanionBudgetPool>())
        {
            if (token.IsCancellationRequested) return Finish(DaevanionOptimizationStatus.Cancelled);
            selected.UnionWith(Seed(rules.Where(board => board.Pool == pool).ToArray(), linearScore, request.Budgets.For(pool), required, token));
        }
        if (token.IsCancellationRequested) return Finish(DaevanionOptimizationStatus.Cancelled);
        Publish("Score", force: true);

        var model = new CpModel();
        var variables = new Dictionary<int, BoolVar>();
        var allVariables = new List<IntVar>();
        var seedFeasible = Plans().Count == rules.Count;
        foreach (var node in nodes)
        {
            var variable = model.NewBoolVar("n" + node.Node.Id);
            variables.Add(node.Node.Id, variable); allVariables.Add(variable);
            if (seedFeasible) model.AddHint(variable, selected.Contains(node.Node.Id));
            if (required.Contains(node.Node.Id)) model.Add(variable == 1);
        }
        foreach (var board in rules)
            AddConnectivity(model, board, variables, request.Budgets.For(board.Pool),
                seedFeasible ? BuildPlan(board, selected, linearScore) : null, allVariables);
        foreach (var pool in Enum.GetValues<DaevanionBudgetPool>())
        {
            var poolNodes = rules.Where(board => board.Pool == pool).SelectMany(board => board.Nodes).ToArray();
            model.Add(LinearExpr.WeightedSum(poolNodes.Select(node => variables[node.Node.Id]), poolNodes.Select(node => (long)node.Cost))
                <= request.Budgets.For(pool));
        }
        var all = nodes.Select(node => variables[node.Node.Id]).ToArray();
        LinearExpr utility = LinearExpr.WeightedSum(all, nodes.Select(node => linearScore.Scores[node.Node.Id]));
        var ignored = LinearExpr.WeightedSum(all, nodes.Select(node => linearScore.Ignored[node.Node.Id]));
        var cost = LinearExpr.WeightedSum(all, nodes.Select(node => (long)node.Cost));
        var excessTerms = new List<LinearExpr>();
        var cpTerms = new List<LinearExpr>();
        long cpUpperBound = 0;
        var sourceObjective = request.Mode == DaevanionOptimizationMode.CombatScore && request.CombatContext?.SourceGraph is not null;
        if (request.Mode == DaevanionOptimizationMode.CombatScore && !sourceObjective)
            AddStatEstimate(model, request, nodes, variables, allVariables, cpTerms, ref cpUpperBound);
        foreach (var group in nodes.SelectMany(node => node.Node.Effects.Where(effect => effect.SkillId is not null)
            .Select(effect => (NodeId: node.Node.Id, Effect: effect))).GroupBy(entry => entry.Effect.SkillId!.Value))
        {
            var maximum = group.Sum(entry => entry.Effect.LevelIncrement);
            var bonus = model.NewIntVar(0, maximum, "skillBonus" + group.Key); allVariables.Add(bonus);
            model.Add(bonus == LinearExpr.WeightedSum(group.Select(entry => variables[entry.NodeId]), group.Select(entry => (long)entry.Effect.LevelIncrement)));
            var state = request.CombatContext?.Skills.SingleOrDefault(state => state.SkillId == group.Key);
            var baseline = DaevanionCombatScoreModel.BaseLevel(state);
            var cap = state?.TargetLevel is { } target && baseline is { } start ? Math.Max(0, target - start) : maximum;
            if (state?.TargetLevel is not null)
            {
                var used = model.NewIntVar(0, Math.Min(maximum, cap), "usedSkillBonus" + group.Key); allVariables.Add(used);
                model.AddMinEquality(used, new LinearExpr[] { bonus, LinearExpr.Constant(cap) });
                excessTerms.Add(bonus - used);
                if (request.Mode == DaevanionOptimizationMode.Priority)
                {
                    var weight = DaevanionScoring.SkillUnitScore(request, group.Key, linearScore);
                    if (weight > 0) utility -= weight * (bonus - used);
                }
            }
            if (request.Mode != DaevanionOptimizationMode.CombatScore || sourceObjective) continue;
            if (baseline is not { } baseLevel)
            {
                // Availability checked every reachable effect; unknown unreachable skills must remain absent.
                model.Add(bonus == 0); continue;
            }
            var kind = DaevanionCatalog.Default.GetSkill(group.Key)!.Kind;
            long SkillValue(int level) => DaevanionCombatScoreModel.SkillScore(kind, level)
                ?? throw new ArgumentException("No qualified skill weighting is available.");
            var values = Enumerable.Range(0, maximum + 1).Select(count => SkillValue(baseLevel + Math.Min(count, cap))
                - SkillValue(baseLevel)).ToArray();
            var score = model.NewIntVar(0, values.Max(), "skillCP" + group.Key); allVariables.Add(score);
            model.AddElement(bonus, values, score);
            cpTerms.Add(score * DaevanionCombatScoreModel.Precision);
            cpUpperBound = checked(cpUpperBound + values.Max());
        }
        var excess = LinearExpr.Sum(excessTerms);
        var scale = request.Mode == DaevanionOptimizationMode.CombatScore ? 1 : linearScore.Scale;
        if (request.Mode == DaevanionOptimizationMode.CombatScore)
        {
            if (sourceObjective)
                utility = new CombatGraphSatCompiler(model, allVariables, Soulcrest.Core.Combat.CombatEffectCatalog.Default)
                    .Compile(request, nodes, variables, DaevanionCombatScoreModel.PossibleNodeIds(request));
            else
            {
            var rounded = model.NewIntVar(0, checked(cpUpperBound + 1), "roundedCPGain"); allVariables.Add(rounded);
            model.AddDivisionEquality(rounded, LinearExpr.Sum(cpTerms) + DaevanionCombatScoreModel.Precision / 2,
                DaevanionCombatScoreModel.Precision);
            utility = rounded;
            }
        }
        var invalid = model.Validate();
        if (invalid.Length != 0) throw new InvalidDataException("Invalid global optimization model: " + invalid);
        var stages = DaevanionCombatScoreModel.HasTargets(request)
            ? new[] { "Score", "Excess", "Ignore", "Cost" } : new[] { "Score", "Ignore", "Cost" };
        foreach (var stage in stages)
        {
            if (token.IsCancellationRequested) return Finish(DaevanionOptimizationStatus.Cancelled);
            var objective = stage switch { "Score" => utility, "Excess" => excess, "Ignore" => ignored, _ => cost };
            if (stage == "Score") model.Maximize(objective); else model.Minimize(objective);
            using var solver = new CpSolver { StringParameters = "num_search_workers:1 random_seed:0 log_search_progress:false "
                + "absolute_gap_limit:0 relative_gap_limit:0 max_number_of_conflicts:9223372036854775807" };
            using var callback = new Incumbent(variables, incumbent => { selected = incumbent.ToHashSet(); Publish(stage); }, token);
            Publish(stage, force: true);
            var boundScale = stage == "Score" ? scale : stage == "Ignore" ? linearScore.Scale : 1;
            solver.SetBestBoundCallback(bound => publication.UpdateBound(bound / boundScale));
            var gate = new object(); var finished = false;
            using var cancellationPoll = new Timer(_ =>
            {
                if (!token.IsCancellationRequested) return;
                lock (gate) if (!finished) solver.StopSearch();
            }, null, 0, 50);
            CpSolverStatus status;
            try { status = solver.Solve(model, callback); }
            finally { lock (gate) finished = true; solver.ClearBestBoundCallback(); }
            if (status is CpSolverStatus.Optimal or CpSolverStatus.Feasible)
                selected = variables.Where(entry => solver.BooleanValue(entry.Value)).Select(entry => entry.Key).ToHashSet();
            Publish(stage, force: true, bound: status is CpSolverStatus.Optimal or CpSolverStatus.Feasible ? solver.BestObjectiveBound / boundScale : null);
            if (token.IsCancellationRequested || status == CpSolverStatus.Unknown) return Finish(DaevanionOptimizationStatus.Cancelled);
            if (status == CpSolverStatus.Infeasible) return Finish(DaevanionOptimizationStatus.Infeasible,
                "Required nodes and their connections cannot be reached within the point budgets.");
            if (status != CpSolverStatus.Optimal) throw new InvalidDataException("Global optimization did not prove an optimum: " + status);
            model.Add(objective == solver.Value(objective));
            model.ClearHints();
            foreach (var variable in allVariables) model.AddHint(variable, solver.Value(variable));
        }
        return Finish(DaevanionOptimizationStatus.Optimal);
    }

    private static void AddStatEstimate(CpModel model, DaevanionOptimizationRequest request,
        IReadOnlyList<DaevanionNodeRule> nodes, IReadOnlyDictionary<int, BoolVar> variables,
        List<IntVar> allVariables, List<LinearExpr> terms, ref long upperBound)
    {
        var precision = DaevanionCombatScoreModel.Precision;
        static long Exact(decimal value) => value == decimal.Truncate(value)
            ? checked((long)value) : throw new ArgumentException("The estimate exceeds its supported decimal precision.");
        foreach (var group in nodes.SelectMany(node => node.Node.Effects.Where(effect => effect.StatId is not null)
            .Select(effect => (NodeId: node.Node.Id, Effect: effect))).GroupBy(entry => entry.Effect.StatId!, StringComparer.Ordinal))
        {
            var coefficient = DaevanionCombatScoreModel.Coefficient(group.Key);
            if (coefficient == 0) continue;
            var rawAmount = group.Sum(entry => DaevanionCombatScoreModel.Amount(entry.Effect));
            if (DaevanionCombatScoreModel.NeedsBaseline(group.Key))
            {
                var baseline = request.CombatContext?.GeneralStats.SingleOrDefault(row => row.StatId == group.Key);
                if (baseline?.Value is not { } before || baseline.IncreasePercent is not { } increase)
                {
                    // Only individually unreachable nodes may have missing snapshot inputs.
                    model.Add(LinearExpr.Sum(group.Select(entry => variables[entry.NodeId])) == 0); continue;
                }
                var factor = 1 + increase / 100;
                var beforeRounded = DaevanionCombatScoreModel.RoundTotal(group.Key, before);
                var afterMaximum = DaevanionCombatScoreModel.RoundTotal(group.Key, before + rawAmount * factor);
                var total = model.NewIntVar(Exact(beforeRounded), Exact(afterMaximum), "total" + group.Key); allVariables.Add(total);
                var numerator = LinearExpr.WeightedSum(group.Select(entry => variables[entry.NodeId]),
                    group.Select(entry => Exact(DaevanionCombatScoreModel.Amount(entry.Effect) * factor * precision))) + Exact(before * precision);
                if (group.Key is "HPMax" or "MPMax") numerator += precision - 1;
                model.AddDivisionEquality(total, numerator, precision);
                terms.Add((total - Exact(beforeRounded)) * Exact(coefficient * precision));
                upperBound = checked(upperBound + Exact(decimal.Ceiling((afterMaximum - beforeRounded) * coefficient)));
            }
            else
            {
                terms.Add(LinearExpr.WeightedSum(group.Select(entry => variables[entry.NodeId]),
                    group.Select(entry => Exact(DaevanionCombatScoreModel.Amount(entry.Effect) * coefficient * precision))));
                upperBound = checked(upperBound + Exact(decimal.Ceiling(rawAmount * coefficient)));
            }
        }
    }
}
