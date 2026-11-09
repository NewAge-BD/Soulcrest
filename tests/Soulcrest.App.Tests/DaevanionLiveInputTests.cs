using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Combat;
using Soulcrest.Core.Daevanion;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class DaevanionLiveInputTests
{
    private const string ClassId = "Cleric";
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch.AddDays(20000);

    [Fact]
    public void CapacityAndSpentAloneNeverBecomeAnOwnedBudget()
    {
        var character = Ready() with { PointPools = Pools().Select(pool => pool with
            { Free = null, Total = null, Spent = 17, Capacity = 840, Status = NetworkCombatSourceStatus.Partial }).ToArray() };
        var result = Resolve(character);
        Assert.False(result.Ready); Assert.Contains(result.MissingRequirements, text => text.Contains("Normal", StringComparison.Ordinal));
        Assert.Equal(0, result.Request.Budgets.Normal); // Placeholder remains blocked by MissingRequirements.
    }

    [Fact]
    public void CompleteFreeAndSpentAreSummedAndExplicitZeroIsAConfirmedBudget()
    {
        var character = Ready() with { PointPools = Pools().Select(pool => pool.Pool == DaevanionBudgetPool.Normal
            ? pool with { Total = null, Free = 5, Spent = 12 } : pool with { Total = 0, Free = 0, Spent = 0 }).ToArray() };
        var result = Resolve(character);
        Assert.True(result.Ready, string.Join("; ", result.MissingRequirements));
        Assert.Equal(new DaevanionBudgets(17, 0, 0), result.Request.Budgets);
    }

    [Theory]
    [InlineData("missing")] [InlineData("partial")] [InlineData("stale")] [InlineData("conflict")]
    [InlineData("negative-free")] [InlineData("negative-spent")] [InlineData("negative-total")]
    [InlineData("sum-conflict")] [InlineData("over-capacity")] [InlineData("sum-overflow")]
    public void UnconfirmedNegativeAndConflictingPoolsCannotEnableCalculation(string variant)
    {
        var values = Pools().ToArray(); var normal = values[0];
        normal = variant switch
        {
            "partial" => normal with { Status = NetworkCombatSourceStatus.Partial },
            "stale" => normal with { Status = NetworkCombatSourceStatus.Stale },
            "conflict" => normal with { Status = NetworkCombatSourceStatus.Conflict },
            "negative-free" => normal with { Free = -1 },
            "negative-spent" => normal with { Spent = -1 },
            "negative-total" => normal with { Total = -1 },
            "sum-conflict" => normal with { Free = 5, Spent = 12, Total = 18 },
            "over-capacity" => normal with { Total = 841 },
            "sum-overflow" => normal with { Total = null, Free = int.MaxValue, Spent = int.MaxValue },
            _ => normal
        };
        values[0] = normal;
        var result = Resolve(Ready() with { PointPools = variant == "missing" ? values.Skip(1).ToArray() : values });
        Assert.False(result.Ready); Assert.Contains(result.MissingRequirements, text => text.Contains("Normal", StringComparison.Ordinal));
    }

    [Fact]
    public void OwnClassAndLevelGatePoolsAndLockedPoolsDoNotRequireInventedWallets()
    {
        var preferences = DaevanionPlanningDefaults.CreateRequest(ClassId);
        var character = Ready() with { CharacterLevel = 20, PointPools = Pools().Where(pool => pool.Pool == DaevanionBudgetPool.Normal).ToArray() };
        var result = DaevanionLiveInputResolver.Resolve(preferences, character);
        Assert.True(result.Ready, string.Join("; ", result.MissingRequirements));
        Assert.Equal(20, result.Request.CharacterLevel); Assert.Equal(new DaevanionBudgets(17, 0, 0), result.Request.Budgets);
        Assert.False(Resolve(character with { ClassId = "Assassin" }).Ready);
        Assert.False(Resolve(character with { CharacterLevel = null }).Ready);
        Assert.False(Resolve(character with { IsFresh = false }).Ready);
        Assert.False(Resolve(character with { ProfileId = null }).Ready);
        Assert.True(Resolve(character with { CharacterLevel = 5, PointPools = [] }).Ready);
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)]
    public void NonpositiveCharacterLevelCannotTurnAllUnknownPoolsIntoLockedZeroBudgets(int level)
    {
        var result = Resolve(Ready() with { CharacterLevel = level, PointPools = [] });
        Assert.False(result.Ready);
    }

    [Fact(Skip = PendingOwnWeights.Reason)]
    public void LegacyManualBaselineIsIgnoredWhileAllTargetsRemainPreferences()
    {
        var preferences = DaevanionPlanningDefaults.CreateRequest(ClassId) with
        {
            Mode = DaevanionOptimizationMode.CombatScore, CharacterLevel = 88, Budgets = new(999, 999, 999),
            CombatContext = DaevanionCombatScoreModel.CreateContext(ClassId) with
            {
                BaselineCombatPower = 999999,
                GeneralStats = [new("FixingDamage", 555555, 99)],
                Skills = DaevanionCombatScoreModel.CreateContext(ClassId).Skills.Select(row => row with
                    { CurrentLevel = 30, CurrentBoardBonus = 6, TargetLevel = 18 }).ToArray()
            }
        };
        var graph = new CombatSourceGraph { ClassId = ClassId, Status = CombatKnowledgeStatus.Complete,
            Sources = [new() { Id = "synthetic-live-source", Kind = CombatSourceKind.Base, Status = CombatKnowledgeStatus.Complete }] };
        var result = DaevanionLiveInputResolver.Resolve(preferences, Ready() with
            { PointPools = Pools().Select(pool => pool with { Total = 0 }).ToArray(), Graph = graph });
        Assert.True(result.Ready, string.Join("; ", result.MissingRequirements));
        Assert.Equal(45, result.Request.CharacterLevel); Assert.Equal(new DaevanionBudgets(), result.Request.Budgets);
        var context = result.Request.CombatContext!;
        Assert.Null(context.BaselineCombatPower); Assert.Empty(context.GeneralStats); Assert.Equal(12345, context.CurrentCombatPower);
        Assert.Equal("synthetic-live-source", Assert.Single(context.SourceGraph!.Sources).Id);
        Assert.All(context.Skills, row => { Assert.Equal(14, row.CurrentLevel); Assert.Equal(2, row.CurrentBoardBonus); Assert.Equal(18, row.TargetLevel); });
    }

    [Fact]
    public void MissingLiveGraphCannotFallBackToHistoricalManualBaselinesEvenAtZeroBudget()
    {
        var preferences = DaevanionPlanningDefaults.CreateRequest(ClassId) with
        {
            Mode = DaevanionOptimizationMode.CombatScore,
            CombatContext = DaevanionCombatScoreModel.CreateContext(ClassId) with
            { BaselineCombatPower = 777, GeneralStats = [new("FixingDamage", 1234, 0)] }
        };
        var result = DaevanionLiveInputResolver.Resolve(preferences, Ready() with
            { PointPools = Pools().Select(pool => pool with { Total = 0 }).ToArray(), Graph = null });
        Assert.False(result.Ready); Assert.Null(result.Request.CombatContext!.SourceGraph);
        Assert.Null(result.Request.CombatContext.BaselineCombatPower); Assert.Empty(result.Request.CombatContext.GeneralStats);
    }

    [Fact]
    public void LiveSkillActionsCannotPersistAutomaticInputsAndEditsKeepOnlyPreferences()
    {
        using var fixture = new Fixture(); fixture.Plans.ObserveCharacter(Ready());
        fixture.Plans.ObserveDetectedSkills(new("local-profile", ClassId, Ready().Skills, true, true, At, 7, 1, "synthetic", 12345, At));
        var original = File.ReadAllBytes(fixture.Path);
        Assert.False(fixture.Plans.ApplyDetectedSkills()); Assert.False(fixture.Plans.ClearSkillOverrides());
        Assert.Equal(original, File.ReadAllBytes(fixture.Path));
        var request = fixture.Plans.Snapshot.Draft;
        request = request with
        {
            CharacterLevel = 99, Budgets = new(999, 999, 999),
            CombatContext = request.CombatContext! with
            {
                BaselineCombatPower = 888888, CurrentCombatPower = 777777,
                Skills = request.CombatContext.Skills.Select(row => row with
                    { CurrentLevel = 30, CurrentBoardBonus = 7, TargetLevel = 18 }).ToArray()
            }
        };
        Assert.True(fixture.Plans.UpdateDraft(request));
        Assert.Equal(45, fixture.Plans.Snapshot.Draft.CharacterLevel);
        Assert.Equal(new DaevanionBudgets(17, 3, 4), fixture.Plans.Snapshot.Draft.Budgets);
        Assert.All(fixture.Plans.Snapshot.Draft.CombatContext!.Skills, row =>
        { Assert.Equal(14, row.CurrentLevel); Assert.Equal(2, row.CurrentBoardBonus); Assert.Equal(18, row.TargetLevel); });
        AssertHistoricalDraft(fixture.Path, 18);
    }

    [Fact]
    public async Task LoadingLiveRouteCopiesPreferencesAndPreservesItsFrozenInputsAndHistory()
    {
        using var fixture = new Fixture(); fixture.Plans.ObserveCharacter(Ready());
        var request = fixture.Plans.Snapshot.Draft;
        Assert.True(fixture.Plans.UpdateDraft(request with { CombatContext = request.CombatContext! with
            { Skills = request.CombatContext.Skills.Select(row => row with { TargetLevel = 18 }).ToArray() } }));
        await fixture.Plans.CalculateAsync(); Assert.True(fixture.Plans.SaveRoute("Synthetic frozen route"));
        var route = Assert.Single(fixture.Plans.Snapshot.Routes); var original = JsonSerializer.Serialize(route);
        Assert.True(fixture.Plans.ActivateRoute(route.Id));
        var next = Ready() with { CharacterLevel = 46, Sequence = 2,
            Skills = Levels(20, 4), PointPools = Pools().Select(pool => pool with { Total = 5 }).ToArray() };
        fixture.Plans.ObserveCharacter(next);
        Assert.True(fixture.Plans.LoadRoute(route.Id));
        Assert.Null(fixture.Plans.Snapshot.Result);
        Assert.Equal(46, fixture.Plans.Snapshot.Draft.CharacterLevel); Assert.Equal(new DaevanionBudgets(5, 5, 5), fixture.Plans.Snapshot.Draft.Budgets);
        Assert.All(fixture.Plans.Snapshot.Draft.CombatContext!.Skills, row =>
        { Assert.Equal(20, row.CurrentLevel); Assert.Equal(4, row.CurrentBoardBonus); Assert.Equal(18, row.TargetLevel); });
        Assert.Equal(original, JsonSerializer.Serialize(Assert.Single(fixture.Plans.Snapshot.Routes)));
        Assert.Equal(original, JsonSerializer.Serialize(fixture.Plans.GetActiveRoute(ClassId)));
        AssertHistoricalDraft(fixture.Path, 18);
    }

    [Fact]
    public async Task SourceLossInvalidatesResultEvenWhenAtomicStoreWritesFail()
    {
        using var fixture = new Fixture(); fixture.Plans.ObserveCharacter(Ready());
        await fixture.Plans.CalculateAsync(); Assert.NotNull(fixture.Plans.Snapshot.Result);
        File.Delete(fixture.Path); Directory.CreateDirectory(fixture.Path);
        fixture.Plans.ObserveCharacter(CharacterCombatSnapshot.Empty);
        Assert.Null(fixture.Plans.Snapshot.Result); Assert.False(fixture.Plans.Snapshot.CanCalculate);
        Assert.NotEmpty(fixture.Plans.Snapshot.MissingRequirements); Assert.True(Directory.Exists(fixture.Path));
        await fixture.Plans.CalculateAsync(); Assert.Null(fixture.Plans.Snapshot.Result);
    }

    [Fact]
    public async Task SourceLossCancelsInFlightSolveAndRejectsLateProgressAndResult()
    {
        var optimizer = new DelayedOptimizer();
        using var fixture = new Fixture(optimizer); fixture.Plans.ObserveCharacter(Ready());
        var pending = fixture.Plans.CalculateAsync(); await optimizer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        File.Delete(fixture.Path); Directory.CreateDirectory(fixture.Path);
        fixture.Plans.ObserveCharacter(CharacterCombatSnapshot.Empty);
        optimizer.Progress!.Report(new() { BestScore = 999 });
        optimizer.Complete.SetResult(EmptyResult(optimizer.Request!)); await pending;
        Assert.True(optimizer.Token.IsCancellationRequested);
        Assert.Null(fixture.Plans.Snapshot.Result); Assert.Null(fixture.Plans.Snapshot.Progress);
        Assert.False(fixture.Plans.Snapshot.IsBusy); Assert.False(fixture.Plans.Snapshot.CanCalculate);
    }

    [Fact]
    public async Task AttachedCharacterServiceRevokesAConfirmedLockedPoolPlanOnNetworkStop()
    {
        using var fixture = new Fixture();
        var own = new OwnCharacterContext(new(101, 501, 300, "SyntheticIdentity") { ClassId = ClassId }, 7) { ObservedAt = At };
        fixture.Source.CombatSnapshot = new() { Character = own, ClassId = ClassId, CharacterLevel = 5, Generation = 7, Sequence = 1 };
        fixture.Source.Raise();
        Assert.True(fixture.Plans.Snapshot.CanCalculate);
        await fixture.Plans.CalculateAsync(); Assert.NotNull(fixture.Plans.Snapshot.Result);
        File.Delete(fixture.Path); Directory.CreateDirectory(fixture.Path);
        fixture.Source.Running = false; fixture.Source.Raise();
        Assert.Null(fixture.Plans.Snapshot.Result); Assert.False(fixture.Plans.Snapshot.CanCalculate);
    }

    [Theory]
    [InlineData("2026-10-31-v2")]
    [InlineData("2026-11-02-v3")]
    public async Task QualifiedLegacyRoutesRemainOverlayUsableWithoutRewritingTheirFrozenRequestOrResult(string version)
    {
        using var fixture = new Fixture();
        var id = await SaveLegacyRoute(fixture, version);
        var original = FrozenRouteBytes(fixture.Path, id);
        using var restored = new DaevanionPlanService(fixture.Path, new EmptyOptimizer());
        restored.AttachCharacterSource(fixture.Character);
        var loaded = Assert.Single(restored.Snapshot.Routes);
        Assert.Equal(version, loaded.RulesVersion); Assert.Equal(version, loaded.Result.RulesVersion);
        Assert.Null(loaded.Request.CharacterLevel); Assert.Null(loaded.Request.CombatContext?.SourceGraph);
        Assert.True(restored.IsCompatible(loaded));
        var active = Assert.IsType<DaevanionSavedRoute>(restored.GetActiveRoute(ClassId));
        Assert.Equal(id, active.Id); Assert.Equal(version, active.Result.RulesVersion);
        Assert.True(restored.DeactivateRoute(ClassId)); Assert.True(restored.ActivateRoute(id));
        Assert.Equal(id, restored.GetActiveRoute(ClassId)!.Id);
        Assert.Equal(original, FrozenRouteBytes(fixture.Path, id));
        Assert.Equal(version, Assert.Single(restored.Snapshot.Routes).Result.RulesVersion);
        Assert.Equal(version, restored.GetActiveRoute(ClassId)!.RulesVersion);
    }

    [Theory]
    [InlineData("2026-10-31-v2", "score")]
    [InlineData("2026-10-31-v2", "cost")]
    [InlineData("2026-11-02-v3", "score")]
    [InlineData("2026-11-02-v3", "cost")]
    public async Task LegacyCompatibilityStillRejectsCorruptedFrozenScoresAndCosts(string version, string corruption)
    {
        using var fixture = new Fixture();
        var id = await SaveLegacyRoute(fixture, version, corruption);
        var original = File.ReadAllBytes(fixture.Path);
        using var restored = new DaevanionPlanService(fixture.Path, new EmptyOptimizer());
        var loaded = Assert.Single(restored.Snapshot.Routes);
        Assert.False(restored.IsCompatible(loaded)); Assert.False(restored.ActivateRoute(id));
        Assert.Null(restored.GetActiveRoute(ClassId));
        Assert.Equal(original, File.ReadAllBytes(fixture.Path));
    }

    [Fact]
    public async Task V1RoutesRemainPreservedButIncompatible()
    {
        using var fixture = new Fixture();
        var id = await SaveLegacyRoute(fixture, "2026-10-31-v1");
        var original = File.ReadAllBytes(fixture.Path);
        using var restored = new DaevanionPlanService(fixture.Path, new EmptyOptimizer());
        Assert.False(restored.IsCompatible(Assert.Single(restored.Snapshot.Routes)));
        Assert.False(restored.ActivateRoute(id)); Assert.Null(restored.GetActiveRoute(ClassId));
        Assert.Equal(original, File.ReadAllBytes(fixture.Path));
    }

    private static async Task<string> SaveLegacyRoute(Fixture fixture, string version, string? corruption = null)
    {
        fixture.Plans.ObserveCharacter(Ready());
        await fixture.Plans.CalculateAsync(); Assert.True(fixture.Plans.SaveRoute("Synthetic legacy route"));
        var id = Assert.Single(fixture.Plans.Snapshot.Routes).Id; Assert.True(fixture.Plans.ActivateRoute(id));
        fixture.Plans.Dispose();
        var store = JsonNode.Parse(File.ReadAllBytes(fixture.Path))!;
        var route = store["Routes"]!.AsArray().Single(item => item!["Id"]!.GetValue<string>() == id)!;
        route["RulesVersion"] = version; route["Result"]!["RulesVersion"] = version;
        route["Request"]!["CharacterLevel"] = null;
        Assert.Null(route["Request"]!["CombatContext"]!["SourceGraph"]);
        if (corruption is not null) route["Result"]!["Boards"]![0]![corruption == "score" ? "Score" : "Cost"] = 1;
        File.WriteAllText(fixture.Path, store.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return id;
    }

    private static byte[] FrozenRouteBytes(string path, string id)
    {
        using var json = JsonDocument.Parse(File.ReadAllBytes(path));
        var route = json.RootElement.GetProperty("Routes").EnumerateArray().Single(item => item.GetProperty("Id").GetString() == id);
        return JsonSerializer.SerializeToUtf8Bytes(new[] { route.GetProperty("Request"), route.GetProperty("Result") });
    }

    private static DaevanionLiveInputResolution Resolve(CharacterCombatSnapshot character) =>
        DaevanionLiveInputResolver.Resolve(DaevanionPlanningDefaults.CreateRequest(ClassId), character);
    private static CharacterCombatSnapshot Ready() => new()
    {
        ProfileId = "local-profile", ClassId = ClassId, CharacterLevel = 45, CurrentCombatScore = 12345, IsFresh = true,
        Generation = 7, Sequence = 1, ObservedAt = At, PointPools = Pools(), SkillStatus = NetworkCombatSourceStatus.Complete, Skills = Levels(14, 2)
    };
    private static ImmutableDictionary<int, OwnSkillLevel> Levels(int total, int board) => DaevanionCatalog.Default.Skills
        .Where(skill => skill.ClassId == ClassId).ToImmutableDictionary(skill => skill.Id,
            skill => new OwnSkillLevel(skill.Id, (byte)total, (byte)(total - board), new((byte)board, 0, 0, 0, 0)));
    private static CharacterCombatPointPoolSnapshot[] Pools() =>
    [
        new(DaevanionBudgetPool.Normal, Total: 17, Capacity: 840, Status: NetworkCombatSourceStatus.Complete),
        new(DaevanionBudgetPool.Ariel, Total: 3, Capacity: 232, Status: NetworkCombatSourceStatus.Complete),
        new(DaevanionBudgetPool.Azphel, Total: 4, Capacity: 232, Status: NetworkCombatSourceStatus.Complete)
    ];
    private static void AssertHistoricalDraft(string path, int target)
    {
        using var json = JsonDocument.Parse(File.ReadAllBytes(path));
        var draft = json.RootElement.GetProperty("Drafts").GetProperty(ClassId);
        Assert.Equal(44, draft.GetProperty("CharacterLevel").GetInt32());
        Assert.Equal(91, draft.GetProperty("Budgets").GetProperty("Normal").GetInt32());
        var context = draft.GetProperty("CombatContext"); Assert.Equal(777, context.GetProperty("BaselineCombatPower").GetInt32());
        Assert.Equal(JsonValueKind.Null, context.GetProperty("CurrentCombatPower").ValueKind);
        Assert.Equal(JsonValueKind.Null, context.GetProperty("SourceGraph").ValueKind);
        Assert.All(context.GetProperty("Skills").EnumerateArray(), row =>
        { Assert.Equal(3, row.GetProperty("CurrentLevel").GetInt32()); Assert.Equal(0, row.GetProperty("CurrentBoardBonus").GetInt32()); Assert.Equal(target, row.GetProperty("TargetLevel").GetInt32()); });
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "soulcrest-live-plan-" + Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(_directory, "plans.json");
        internal Source Source { get; } = new();
        internal OwnSkillStateService Skills { get; }
        internal CharacterCombatStateService Character { get; }
        internal DaevanionPlanService Plans { get; }
        internal Fixture(IDaevanionOptimizer? optimizer = null)
        {
            Skills = new(Source, _ => new("local-profile", "Unused"));
            Character = new(Source, Skills, _ => new("local-profile", "Unused"));
            Plans = new(Path, optimizer ?? new EmptyOptimizer()); Assert.True(Plans.SetClass(ClassId));
            var history = Plans.Snapshot.Draft with
            {
                CharacterLevel = 44, Budgets = new(91, 4, 5),
                CombatContext = DaevanionCombatScoreModel.CreateContext(ClassId) with
                {
                    BaselineCombatPower = 777,
                    Skills = DaevanionCombatScoreModel.CreateContext(ClassId).Skills.Select(row => row with { CurrentLevel = 3, CurrentBoardBonus = 0 }).ToArray()
                }
            };
            Assert.True(Plans.UpdateDraft(history)); Plans.AttachSkillSource(Skills); Plans.AttachCharacterSource(Character);
        }
        public void Dispose() { Plans.Dispose(); Character.Dispose(); Skills.Dispose(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    }
    private sealed class Source : INetworkCharacterCombatSource, INetworkSkillSource
    {
        public bool Running { get; set; } = true;
        public NetworkCharacterCombatSnapshot CombatSnapshot { get; set; } = NetworkCharacterCombatSnapshot.Empty;
        public NetworkSkillSnapshot SkillSnapshot { get; set; } = NetworkSkillSnapshot.Empty;
        public event Action? Changed;
        public event Action? OwnCharacterChanged { add { } remove { } }
        internal void Raise() => Changed?.Invoke();
    }
    private static DaevanionOptimizationResult EmptyResult(DaevanionOptimizationRequest request) => new()
    {
        ClassId = request.ClassId, CatalogVersion = DaevanionCatalog.Default.DataVersion,
        CatalogFingerprint = DaevanionRuleCatalog.Default.CatalogFingerprint, RulesVersion = DaevanionRuleCatalog.Default.Version,
        Status = DaevanionOptimizationStatus.Optimal,
        Boards = DaevanionRuleCatalog.Default.ForClass(request.ClassId).Select(board =>
        {
            var root = board.Nodes.Single(node => node.Node.Kind == DaevanionNodeKind.Start).Node.Id;
            return new DaevanionBoardPlan { BoardId = board.Board.Id, FamilyId = board.Board.FamilyId, Pool = board.Pool, NodeIds = [root], ActivationOrder = [root] };
        }).ToArray()
    };
    private sealed class EmptyOptimizer : IDaevanionOptimizer
    {
        public Task<DaevanionOptimizationResult> OptimizeAsync(DaevanionOptimizationRequest request,
            IProgress<DaevanionOptimizationProgress>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(EmptyResult(request));
    }
    private sealed class DelayedOptimizer : IDaevanionOptimizer
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<DaevanionOptimizationResult> Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal DaevanionOptimizationRequest? Request { get; private set; }
        internal IProgress<DaevanionOptimizationProgress>? Progress { get; private set; }
        internal CancellationToken Token { get; private set; }
        public Task<DaevanionOptimizationResult> OptimizeAsync(DaevanionOptimizationRequest request,
            IProgress<DaevanionOptimizationProgress>? progress = null, CancellationToken cancellationToken = default)
        { Request = request; Progress = progress; Token = cancellationToken; Started.SetResult(); return Complete.Task; }
    }
}
