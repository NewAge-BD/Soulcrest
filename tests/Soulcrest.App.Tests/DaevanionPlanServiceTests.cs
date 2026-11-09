using System.Text.Json;
using Soulcrest.App.Services;
using Soulcrest.Core.Daevanion;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class DaevanionPlanServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Soulcrest-Daevanion-" + Guid.NewGuid().ToString("N"));
    private string StorePath => Path.Combine(_directory, "plans.json");
    private static string ClassId => DaevanionCatalog.Default.Classes[0].Id;

    [Fact]
    public async Task NamedRoutesAndActiveClassBindingSurviveRestartWithoutReplacingSavedRouteOnEdit()
    {
        string id;
        using (var service = new DaevanionPlanService(StorePath, new EmptyOptimizer()))
        {
            await service.CalculateAsync();
            Assert.True(service.SaveRoute("PvE"));
            id = Assert.Single(service.Snapshot.Routes).Id;
            Assert.True(service.ActivateRoute(id));
            Assert.True(service.UpdateDraft(service.Snapshot.Draft with { Budgets = new(23, 7, 9) }));
            Assert.Null(service.Snapshot.Result);
            Assert.Equal(0, service.GetActiveRoute(ClassId)!.Request.Budgets.Normal);
            Assert.Equal(23, service.Snapshot.Draft.Budgets.Normal);
        }
        using var restored = new DaevanionPlanService(StorePath, new EmptyOptimizer());
        Assert.Equal(id, restored.Snapshot.ActiveRouteId);
        Assert.Equal(23, restored.Snapshot.Draft.Budgets.Normal);
        Assert.Equal(id, restored.GetActiveRoute(ClassId)!.Id);
        Assert.Null(restored.GetActiveRoute(DaevanionCatalog.Default.Classes[1].Id));
        Assert.True(restored.LoadRoute(id));
        Assert.Equal(0, restored.Snapshot.Draft.Budgets.Normal);
        Assert.NotNull(restored.Snapshot.Result);
        Assert.True(restored.DeleteRoute(id));
        Assert.Null(restored.GetActiveRoute(ClassId));
        Assert.Null(restored.Snapshot.ActiveRouteId);
    }

    [Fact]
    public async Task LateResultAndProgressAfterInputChangeCannotReplaceNewDraft()
    {
        var optimizer = new DelayedOptimizer();
        using var service = new DaevanionPlanService(StorePath, optimizer);
        var pending = service.CalculateAsync();
        await optimizer.Started.Task;
        var initial = optimizer.Request!;
        Assert.True(service.UpdateDraft(service.Snapshot.Draft with { Budgets = new(17, 0, 0) }));
        optimizer.Progress!.Report(new() { BestScore = 9999 });
        optimizer.Complete.SetResult(EmptyResult(initial));
        await pending;
        Assert.Null(service.Snapshot.Result);
        Assert.Null(service.Snapshot.Progress);
        Assert.False(service.Snapshot.IsBusy);
        Assert.Equal(17, service.Snapshot.Draft.Budgets.Normal);
        Assert.True(optimizer.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task FailedAtomicWriteDoesNotActivateOrReplaceAnInMemoryRoute()
    {
        using var service = new DaevanionPlanService(StorePath, new EmptyOptimizer());
        await service.CalculateAsync();
        Assert.True(service.SaveRoute("Saved"));
        var id = Assert.Single(service.Snapshot.Routes).Id;
        File.Delete(StorePath);
        Directory.CreateDirectory(StorePath);
        Assert.False(service.ActivateRoute(id));
        Assert.Null(service.GetActiveRoute(ClassId));
        Assert.False(service.SaveRoute("Changed", id));
        Assert.Equal("Saved", Assert.Single(service.Snapshot.Routes).Name);
        Assert.Contains("nicht gespeichert", service.Snapshot.Status);
    }

    [Theory]
    [InlineData("{invalid")]
    [InlineData("{\"Version\":99}")]
    public void UnreadableOrFutureStoreIsNeverOverwritten(string bytes)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(StorePath, bytes);
        using var service = new DaevanionPlanService(StorePath, new EmptyOptimizer());
        Assert.False(service.UpdateDraft(service.Snapshot.Draft with { Budgets = new(1, 0, 0) }));
        Assert.Equal(bytes, File.ReadAllText(StorePath));
        Assert.Empty(service.Snapshot.Routes);
    }

    [Theory]
    [InlineData("CatalogFingerprint", "old-fingerprint")]
    [InlineData("RulesVersion", "2026-10-31-v1")]
    public async Task StaleCatalogRouteIsPreservedButCannotBeShownInOverlay(string property, string oldValue)
    {
        string id;
        using (var service = new DaevanionPlanService(StorePath, new EmptyOptimizer()))
        {
            await service.CalculateAsync();
            Assert.True(service.SaveRoute("Old"));
            id = Assert.Single(service.Snapshot.Routes).Id;
            Assert.True(service.ActivateRoute(id));
        }
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(StorePath))!;
        json["Routes"]![0]![property] = oldValue;
        File.WriteAllText(StorePath, json.ToJsonString());
        using var restored = new DaevanionPlanService(StorePath, new EmptyOptimizer());
        Assert.Single(restored.Snapshot.Routes);
        Assert.Null(restored.GetActiveRoute(ClassId));
        Assert.Null(restored.Snapshot.ActiveRouteId);
        Assert.False(restored.ActivateRoute(id));
        Assert.True(restored.LoadRoute(id));
        Assert.Null(restored.Snapshot.Result);
        Assert.Contains("neu berechnet", restored.Snapshot.Status);
    }

    [Fact]
    public async Task ManualMeasurementIsContextBoundAndDoesNotCreateACombatScoreModel()
    {
        var board = DaevanionCatalog.Default.Boards.First(item => item.ClassId == ClassId);
        var node = board.Nodes.First(item => item.Kind == DaevanionNodeKind.Stat);
        using (var service = new DaevanionPlanService(StorePath, new EmptyOptimizer()))
        {
            Assert.True(service.AddMeasurement(new()
            {
                ClassId = ClassId, BoardId = board.Id, NodeId = node.Id,
                GameBuild = "test-build", CharacterLevel = 45, Before = 1000, After = 1012, Restored = 1000,
                Context = "Fixed gear and buffs; synthetic measurement."
            }));
            Assert.Equal(12, Assert.Single(service.Snapshot.Measurements).Delta);
            Assert.False(service.AddMeasurement(new() { ClassId = ClassId, BoardId = board.Id, NodeId = -1 }));
            Assert.True(service.UpdateDraft(service.Snapshot.Draft with { Mode = DaevanionOptimizationMode.CombatScore }));
            await service.CalculateAsync();
            Assert.Equal(DaevanionOptimizationStatus.Unavailable, service.Snapshot.Result!.Status);
            Assert.False(service.SaveRoute("Unproven"));
        }
        using var restored = new DaevanionPlanService(StorePath, new EmptyOptimizer());
        var measurement = Assert.Single(restored.Snapshot.Measurements);
        Assert.Equal("test-build", measurement.GameBuild);
        Assert.True(restored.DeleteMeasurement(measurement.Id));
    }

    [Fact]
    public async Task ResultsExposedToUiCannotMutatePersistedActiveRoute()
    {
        using var service = new DaevanionPlanService(StorePath, new EmptyOptimizer());
        await service.CalculateAsync();
        Assert.True(service.SaveRoute("Immutable"));
        var route = Assert.Single(service.Snapshot.Routes);
        Assert.True(service.ActivateRoute(route.Id));
        Assert.Throws<NotSupportedException>(() => ((IList<int>)route.Result.Boards[0].NodeIds).Add(-1));
        Assert.Throws<NotSupportedException>(() => ((IList<DaevanionPriority>)route.Request.Priorities).Clear());
        Assert.NotNull(service.GetActiveRoute(ClassId));
    }

    [Fact]
    public void OversizedMeasurementCannotMakeAnExistingStoreUnreadableOnRestart()
    {
        var board = DaevanionCatalog.Default.Boards.First(item => item.ClassId == ClassId);
        var node = board.Nodes.First(item => item.Kind == DaevanionNodeKind.Stat);
        var sample = new DaevanionCombatScoreMeasurement { ClassId = ClassId, BoardId = board.Id, NodeId = node.Id,
            GameBuild = "test-build", CharacterLevel = 45, Before = 1000, After = 1012, Context = "Stable synthetic baseline." };
        using (var service = new DaevanionPlanService(StorePath, new EmptyOptimizer(), maximumStoreBytes: 4096))
        {
            Assert.True(service.AddMeasurement(sample));
            var original = File.ReadAllBytes(StorePath);
            Assert.False(service.AddMeasurement(sample with { Id = Guid.NewGuid().ToString("N"), Context = new('ä', 4096) }));
            Assert.Equal(original, File.ReadAllBytes(StorePath));
            Assert.Single(service.Snapshot.Measurements);
            Assert.Contains("Planspeicher ist voll", service.Snapshot.Status);
        }
        using var restored = new DaevanionPlanService(StorePath, new EmptyOptimizer());
        Assert.Equal(sample.Id, Assert.Single(restored.Snapshot.Measurements).Id);
    }

    [Fact]
    public async Task InvalidOptimizerOutputIsRejectedAndNeverSaved()
    {
        var optimizer = new DelayedOptimizer();
        using var service = new DaevanionPlanService(StorePath, optimizer);
        var pending = service.CalculateAsync();
        await optimizer.Started.Task;
        var result = EmptyResult(optimizer.Request!);
        optimizer.Complete.SetResult(result with { Boards = result.Boards.Select(board => board with { Cost = 900 }).ToArray() });
        await pending;
        Assert.Null(service.Snapshot.Result);
        Assert.False(service.SaveRoute("Invalid"));
        Assert.Empty(service.Snapshot.Routes);
    }

    [Fact]
    public async Task EmptyOptimalResultIsRejectedRatherThanReportedAsSuccess()
    {
        var optimizer = new DelayedOptimizer();
        using var service = new DaevanionPlanService(StorePath, optimizer);
        var pending = service.CalculateAsync();
        await optimizer.Started.Task;
        optimizer.Complete.SetResult(EmptyResult(optimizer.Request!) with { Boards = [] });
        await pending;
        Assert.Null(service.Snapshot.Result);
        Assert.Contains("nicht berechnet", service.Snapshot.Status);
    }

    [Theory]
    [InlineData(DaevanionOptimizationStatus.Infeasible)]
    [InlineData(DaevanionOptimizationStatus.Cancelled)]
    public async Task NoFeasibleRequiredRouteHasClearStatusAndCannotBeSaved(DaevanionOptimizationStatus status)
    {
        var optimizer = new DelayedOptimizer();
        using var service = new DaevanionPlanService(StorePath, optimizer);
        var draft = service.Snapshot.Draft;
        var preferences = DaevanionPoolSettings.ForPool(draft, DaevanionBudgetPool.Ariel);
        Assert.True(service.UpdateDraft(DaevanionPoolSettings.WithPool(draft, preferences with
        {
            Priorities = preferences.Priorities.Select(row => row with { Required = true }).ToArray()
        })));
        var pending = service.CalculateAsync();
        await optimizer.Started.Task;
        optimizer.Complete.SetResult(EmptyResult(optimizer.Request!) with { Status = status, Boards = [] });
        await pending;
        Assert.Equal(status, service.Snapshot.Result!.Status);
        Assert.Empty(service.Snapshot.Result.Boards);
        Assert.Contains(status == DaevanionOptimizationStatus.Infeasible ? "must-have" : "keine vollständige Route", service.Snapshot.Status);
        Assert.False(service.SaveRoute("Unfulfilled requirements"));
    }

    [Theory]
    [InlineData(DaevanionOptimizationStatus.Optimal)]
    [InlineData(DaevanionOptimizationStatus.Cancelled)]
    public async Task ResultWithoutEveryRequiredNodeCannotBecomeASavedOrActiveRoute(DaevanionOptimizationStatus status)
    {
        var draft = DaevanionPlanningDefaults.CreateRequest(ClassId);
        var ariel = DaevanionPoolSettings.ForPool(draft, DaevanionBudgetPool.Ariel);
        var request = DaevanionPoolSettings.WithPool(draft with { Budgets = new(0, 232, 0) }, ariel with
        {
            Priorities = ariel.Priorities.Select(row => row.Key == ariel.Priorities[0].Key ? row with { Required = true } : row).ToArray()
        });
        var optimizer = new DelayedOptimizer();
        using var candidate = new DaevanionPlanService(StorePath, optimizer);
        Assert.True(candidate.UpdateDraft(request));
        var pending = candidate.CalculateAsync();
        await optimizer.Started.Task;
        optimizer.Complete.SetResult(EmptyResult(request) with { Status = status });
        await pending;
        Assert.Null(candidate.Snapshot.Result);
        Assert.False(candidate.SaveRoute("Missing required nodes"));
        Assert.Empty(candidate.Snapshot.Routes);
    }

    [Fact]
    public async Task EquivalentInputsPreserveAnAlreadyComputedResult()
    {
        using var service = new DaevanionPlanService(StorePath, new EmptyOptimizer());
        await service.CalculateAsync();
        var result = service.Snapshot.Result;
        Assert.NotNull(result);
        Assert.True(service.UpdateDraft(service.Snapshot.Draft with { Priorities = service.Snapshot.Draft.Priorities.ToArray() }));
        Assert.Same(result, service.Snapshot.Result);
    }

    [Fact]
    public async Task PoolPreferencesPersistIndependentlyAndSavedRouteKeepsItsOwnSnapshot()
    {
        string routeId;
        var initial = DaevanionPlanningDefaults.CreateRequest(ClassId);
        var normal = DaevanionPoolSettings.ForPool(initial, DaevanionBudgetPool.Normal);
        var ariel = DaevanionPoolSettings.ForPool(initial, DaevanionBudgetPool.Ariel);
        var azphel = DaevanionPoolSettings.ForPool(initial, DaevanionBudgetPool.Azphel);
        var sharedKey = ariel.Priorities.Select(row => row.Key).Intersect(azphel.Priorities.Select(row => row.Key)).First();
        var arielRows = ariel.Priorities.OrderByDescending(row => row.Key == sharedKey)
            .Select(row => row with { Ignored = row.Key == sharedKey }).ToArray();
        ariel = ariel with { Weights = new(37, 100, 100), Priorities = arielRows };
        azphel = azphel with { Weights = new(62, 100, 100), Priorities = azphel.Priorities.Reverse().ToArray() };
        var request = DaevanionPoolSettings.WithPool(DaevanionPoolSettings.WithPool(initial, ariel), azphel);
        using (var service = new DaevanionPlanService(StorePath, new EmptyOptimizer()))
        {
            Assert.True(service.UpdateDraft(request));
            arielRows[0] = arielRows[0] with { Ignored = false };
            var storedAriel = DaevanionPoolSettings.ForPool(service.Snapshot.Draft, DaevanionBudgetPool.Ariel);
            Assert.True(storedAriel.Priorities.Single(row => row.Key == sharedKey).Ignored);
            Assert.False(DaevanionPoolSettings.ForPool(service.Snapshot.Draft, DaevanionBudgetPool.Azphel)
                .Priorities.Single(row => row.Key == sharedKey).Ignored);
            await service.CalculateAsync();
            Assert.True(service.SaveRoute("Separate pools"));
            var route = Assert.Single(service.Snapshot.Routes);
            routeId = route.Id;
            Assert.True(service.ActivateRoute(routeId));
            Assert.Throws<NotSupportedException>(() => ((IList<DaevanionPoolPreferences>)route.Request.PoolPreferences).Clear());
            Assert.Throws<NotSupportedException>(() => ((IList<DaevanionPriority>)route.Request.PoolPreferences[0].Priorities).Clear());
            Assert.True(service.UpdateDraft(DaevanionPoolSettings.WithPool(service.Snapshot.Draft,
                storedAriel with { Weights = storedAriel.Weights with { Stats = 12 } })));
            Assert.Null(service.Snapshot.Result);
            Assert.Equal(37, DaevanionPoolSettings.ForPool(service.GetActiveRoute(ClassId)!.Request, DaevanionBudgetPool.Ariel).Weights.Stats);
        }
        using var restored = new DaevanionPlanService(StorePath, new EmptyOptimizer());
        Assert.Equal(normal.Priorities, DaevanionPoolSettings.ForPool(restored.Snapshot.Draft, DaevanionBudgetPool.Normal).Priorities);
        Assert.Equal(12, DaevanionPoolSettings.ForPool(restored.Snapshot.Draft, DaevanionBudgetPool.Ariel).Weights.Stats);
        Assert.Equal(azphel.Priorities, DaevanionPoolSettings.ForPool(restored.Snapshot.Draft, DaevanionBudgetPool.Azphel).Priorities);
        Assert.Equal(62, DaevanionPoolSettings.ForPool(restored.Snapshot.Draft, DaevanionBudgetPool.Azphel).Weights.Stats);
        Assert.True(restored.LoadRoute(routeId));
        Assert.Equal(37, DaevanionPoolSettings.ForPool(restored.Snapshot.Draft, DaevanionBudgetPool.Ariel).Weights.Stats);
        Assert.NotNull(restored.Snapshot.Result);
    }

    [Fact]
    public async Task EquivalentExplicitPoolPreferencesDoNotDiscardAComputedResult()
    {
        using var service = new DaevanionPlanService(StorePath, new EmptyOptimizer());
        await service.CalculateAsync();
        var result = service.Snapshot.Result;
        var draft = service.Snapshot.Draft;
        Assert.True(service.UpdateDraft(draft with
        {
            PoolPreferences = Enum.GetValues<DaevanionBudgetPool>().Select(pool => DaevanionPoolSettings.ForPool(draft, pool)).ToArray()
        }));
        Assert.Same(result, service.Snapshot.Result);
    }

    [Fact]
    public void RequiredFlagsSurviveRestartAndRemainIndependentForSharedStats()
    {
        using (var service = new DaevanionPlanService(StorePath, new EmptyOptimizer()))
        {
            var draft = service.Snapshot.Draft;
            var ariel = DaevanionPoolSettings.ForPool(draft, DaevanionBudgetPool.Ariel);
            Assert.True(service.UpdateDraft(DaevanionPoolSettings.WithPool(draft, ariel with
            {
                Priorities = ariel.Priorities.Select(row => row.Key == "HPMax" ? row with { Required = true } : row).ToArray()
            })));
        }
        using var restored = new DaevanionPlanService(StorePath, new EmptyOptimizer());
        foreach (var pool in Enum.GetValues<DaevanionBudgetPool>())
        {
            var row = DaevanionPoolSettings.ForPool(restored.Snapshot.Draft, pool).Priorities.Single(row => row.Key == "HPMax");
            Assert.Equal(pool == DaevanionBudgetPool.Ariel, row.Required);
            Assert.False(row.Ignored);
        }
    }

    [Fact]
    public void LegacyGlobalPrioritiesAreFilteredIntoEachPoolWithoutLosingOrderOrIgnore()
    {
        Directory.CreateDirectory(_directory);
        var initial = DaevanionPlanningDefaults.CreateRequest(ClassId);
        var rows = initial.Priorities.Reverse().Select((row, index) => row with { Ignored = index % 3 == 0 }).ToArray();
        var legacy = initial with { Priorities = rows, Weights = new(41, 29, 17), Budgets = new(13, 7, 5) };
        var document = JsonSerializer.SerializeToNode(new
        {
            Version = 1, SelectedClassId = ClassId,
            Drafts = new Dictionary<string, DaevanionOptimizationRequest> { [ClassId] = legacy },
            Routes = Array.Empty<DaevanionSavedRoute>(), ActiveRoutes = new Dictionary<string, string>(),
            Measurements = Array.Empty<DaevanionCombatScoreMeasurement>()
        })!;
        document["Drafts"]![ClassId]!.AsObject().Remove("PoolPreferences");
        var json = document.ToJsonString();
        File.WriteAllText(StorePath, json);
        using var service = new DaevanionPlanService(StorePath, new EmptyOptimizer());
        foreach (var pool in Enum.GetValues<DaevanionBudgetPool>())
        {
            var keys = DaevanionPoolSettings.CreateDefault(ClassId, pool).Priorities.Select(row => (row.Kind, row.Key)).ToHashSet();
            var actual = DaevanionPoolSettings.ForPool(service.Snapshot.Draft, pool);
            Assert.Equal(rows.Where(row => keys.Contains((row.Kind, row.Key))), actual.Priorities);
            Assert.Equal(legacy.Weights, actual.Weights);
        }
        Assert.Equal(legacy.Budgets, service.Snapshot.Draft.Budgets);
        Assert.Equal(json, File.ReadAllText(StorePath));
    }

    [Theory]
    [InlineData(false, "weight")]
    [InlineData(true, "weight")]
    [InlineData(false, "budget")]
    [InlineData(true, "missing-pools")]
    [InlineData(false, "conflicting-flags")]
    public async Task MalformedPoolSettingsPreserveStoreInsteadOfResettingOtherPreferences(bool savedRoute, string invalid)
    {
        using (var service = new DaevanionPlanService(StorePath, new EmptyOptimizer()))
        {
            var draft = service.Snapshot.Draft;
            var ariel = DaevanionPoolSettings.ForPool(draft, DaevanionBudgetPool.Ariel);
            Assert.True(service.UpdateDraft(DaevanionPoolSettings.WithPool(draft, ariel with { Weights = new(43) })));
            await service.CalculateAsync();
            Assert.True(service.SaveRoute("Preserve original"));
        }
        var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(StorePath))!;
        var request = savedRoute ? document["Routes"]![0]!["Request"]! : document["Drafts"]![ClassId]!;
        if (invalid == "weight") request["PoolPreferences"]![0]!["Weights"]!["Stats"] = 101;
        else if (invalid == "budget") request["Budgets"]!["Normal"] = -1;
        else if (invalid == "conflicting-flags")
        {
            request["PoolPreferences"]![0]!["Priorities"]![0]!["Ignored"] = true;
            request["PoolPreferences"]![0]!["Priorities"]![0]!["Required"] = true;
        }
        else request["PoolPreferences"] = null;
        var original = document.ToJsonString();
        File.WriteAllText(StorePath, original);
        using var restored = new DaevanionPlanService(StorePath, new EmptyOptimizer());
        Assert.Contains("Datei bleibt unverändert", restored.Snapshot.Status);
        Assert.False(restored.UpdateDraft(restored.Snapshot.Draft with { Budgets = new(1, 2, 3) }));
        Assert.Equal(original, File.ReadAllText(StorePath));
    }

    [Fact]
    public async Task OldDraftPrioritiesAreReconciledWithoutHidingSavedRoutes()
    {
        using (var service = new DaevanionPlanService(StorePath, new EmptyOptimizer()))
        {
            Assert.True(service.UpdateDraft(service.Snapshot.Draft with { Budgets = new(17, 3, 5) }));
            await service.CalculateAsync();
            Assert.True(service.SaveRoute("Preserved"));
        }
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(StorePath))!;
        var rows = json["Drafts"]![ClassId]!["Priorities"]!.AsArray();
        rows.RemoveAt(0);
        rows.Add(System.Text.Json.Nodes.JsonNode.Parse("{\"Kind\":0,\"Key\":\"removed-old-stat\",\"Ignored\":false}"));
        File.WriteAllText(StorePath, json.ToJsonString());
        using var restored = new DaevanionPlanService(StorePath, new EmptyOptimizer());
        Assert.Single(restored.Snapshot.Routes);
        Assert.Equal(17, restored.Snapshot.Draft.Budgets.Normal);
        Assert.DoesNotContain(restored.Snapshot.Draft.Priorities, row => row.Key == "removed-old-stat");
        Assert.Equal(DaevanionPlanningDefaults.CreateRequest(ClassId).Priorities.Count, restored.Snapshot.Draft.Priorities.Count);
        Assert.True(restored.UpdateDraft(restored.Snapshot.Draft with { Budgets = new(18, 3, 5) }));
    }

    private static DaevanionOptimizationResult EmptyResult(DaevanionOptimizationRequest request) => new()
    {
        ClassId = request.ClassId, CatalogVersion = DaevanionCatalog.Default.DataVersion,
        CatalogFingerprint = DaevanionRuleCatalog.Default.CatalogFingerprint,
        RulesVersion = DaevanionRuleCatalog.Default.Version, Status = DaevanionOptimizationStatus.Optimal,
        Boards = DaevanionRuleCatalog.Default.ForClass(request.ClassId).Select(board =>
        {
            var root = board.Board.Nodes.Single(node => node.Kind == DaevanionNodeKind.Start).Id;
            return new DaevanionBoardPlan { BoardId = board.Board.Id, FamilyId = board.Board.FamilyId, Pool = board.Pool,
                NodeIds = [root], ActivationOrder = [root] };
        }).ToArray()
    };

    private sealed class EmptyOptimizer : IDaevanionOptimizer
    {
        public Task<DaevanionOptimizationResult> OptimizeAsync(DaevanionOptimizationRequest request,
            IProgress<DaevanionOptimizationProgress>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(request.Mode == DaevanionOptimizationMode.CombatScore
                ? new DaevanionOptimizationResult { Status = DaevanionOptimizationStatus.Unavailable, ClassId = request.ClassId }
                : EmptyResult(request));
    }

    private sealed class DelayedOptimizer : IDaevanionOptimizer
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<DaevanionOptimizationResult> Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DaevanionOptimizationRequest? Request { get; private set; }
        public IProgress<DaevanionOptimizationProgress>? Progress { get; private set; }
        public CancellationToken Token { get; private set; }
        public Task<DaevanionOptimizationResult> OptimizeAsync(DaevanionOptimizationRequest request,
            IProgress<DaevanionOptimizationProgress>? progress = null, CancellationToken cancellationToken = default)
        { Request = request; Progress = progress; Token = cancellationToken; Started.SetResult(); return Complete.Task; }
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
