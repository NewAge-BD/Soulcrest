using System.Collections.Immutable;
using Soulcrest.App.Services;
using Soulcrest.Core.Daevanion;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class DaevanionDetectedSkillTests : IDisposable
{
    private const int SkillId = 17060000;
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch.AddDays(20000);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "soulcrest-daevanion-skills-" + Guid.NewGuid().ToString("N"));
    private string StorePath => Path.Combine(_directory, "plans.json");

    private DaevanionPlanService Create()
    {
        var service = new DaevanionPlanService(StorePath, new EmptyOptimizer());
        Assert.True(service.SetClass("Cleric"));
        return service;
    }

    private static OwnSkillStateSnapshot Detected(int level = 15, int? board = 2, string profile = "synthetic-profile") => new(
        profile, "Cleric", ImmutableDictionary<int, OwnSkillLevel>.Empty.Add(SkillId,
            new(SkillId, level, level - (board ?? 0), board is { } bonus ? new(bonus, 0, 0, 0, 0) : null)),
        true, true, At, 1, 1, "synthetic-own-skill-list", 12345, At);

    private static DaevanionSkillState Skill(DaevanionPlanService service)
    {
        var draft = service.Snapshot.Draft;
        return (draft.CombatContext ?? DaevanionCombatScoreModel.CreateContext(draft.ClassId))
            .Skills.Single(skill => skill.SkillId == SkillId);
    }

    [Fact]
    public void QualifiedLevelsAndBoardComponentsFillInputsWithoutGuessingMissingSkillsOrAbsoluteCp()
    {
        using var service = Create();
        service.ObserveDetectedSkills(Detected());
        Assert.Equal(15, Skill(service).CurrentLevel);
        Assert.Equal(2, Skill(service).CurrentBoardBonus);
        Assert.True(service.Snapshot.CanApplyDetectedSkills);
        Assert.Equal(0, service.Snapshot.ManualSkillOverrideCount);
        Assert.All(service.Snapshot.Draft.CombatContext!.Skills.Where(skill => skill.SkillId != SkillId),
            skill => { Assert.Null(skill.CurrentLevel); Assert.Null(skill.CurrentBoardBonus); });
        Assert.Null(service.Snapshot.Draft.CombatContext.BaselineCombatPower);
        service.ObserveDetectedSkills(Detected(16, null));
        Assert.Equal(16, Skill(service).CurrentLevel);
        Assert.Null(Skill(service).CurrentBoardBonus);
    }

    [Fact]
    public void ManualLevelsSurviveIncomingSnapshotsAndRestartButTargetsDoNotDisableAutomaticLevels()
    {
        using (var service = Create())
        {
            service.ObserveDetectedSkills(Detected());
            var draft = service.Snapshot.Draft;
            Assert.True(service.UpdateDraft(draft with { CombatContext = draft.CombatContext! with
            {
                Skills = draft.CombatContext.Skills.Select(skill => skill.SkillId == SkillId
                    ? skill with { TargetLevel = 16 } : skill).ToArray()
            } }));
            Assert.Equal(0, service.Snapshot.ManualSkillOverrideCount);
            service.ObserveDetectedSkills(Detected(17, 3));
            Assert.Equal(17, Skill(service).CurrentLevel);
            Assert.Equal(16, Skill(service).TargetLevel);
            draft = service.Snapshot.Draft;
            Assert.True(service.UpdateDraft(draft with { CombatContext = draft.CombatContext! with
            {
                Skills = draft.CombatContext.Skills.Select(skill => skill.SkillId == SkillId
                    ? skill with { CurrentLevel = 18 } : skill).ToArray()
            } }));
            Assert.Equal(1, service.Snapshot.ManualSkillOverrideCount);
        }
        using var restored = Create();
        restored.ObserveDetectedSkills(Detected(20, 4));
        Assert.Equal(18, Skill(restored).CurrentLevel);
        Assert.Equal(16, Skill(restored).TargetLevel);
        Assert.Equal(1, restored.Snapshot.ManualSkillOverrideCount);
        Assert.True(restored.ClearSkillOverrides());
        Assert.Equal(20, Skill(restored).CurrentLevel);
        Assert.Equal(4, Skill(restored).CurrentBoardBonus);
        Assert.Equal(16, Skill(restored).TargetLevel);
        Assert.Equal(0, restored.Snapshot.ManualSkillOverrideCount);
    }

    [Fact]
    public void MissingEntrySourceClearsAutomaticLevelsAndExplicitApplyRequiresFreshMatchingClass()
    {
        using var service = Create();
        service.ObserveDetectedSkills(Detected());
        service.ObserveDetectedSkills(Detected() with { IsKnown = false, IsFresh = false,
            Skills = ImmutableDictionary<int, OwnSkillLevel>.Empty });
        Assert.Null(Skill(service).CurrentLevel); Assert.Null(Skill(service).CurrentBoardBonus);
        Assert.False(service.ApplyDetectedSkills());
        service.ObserveDetectedSkills(Detected(19, 4, "synthetic-second-profile"));
        Assert.Equal(19, Skill(service).CurrentLevel);
        Assert.True(service.SetClass("Assassin"));
        Assert.False(service.Snapshot.CanApplyDetectedSkills);
        Assert.False(service.ApplyDetectedSkills());
        Assert.Equal("Assassin", service.Snapshot.Draft.ClassId);
    }

    [Fact]
    public void RestartCannotReusePreviouslyAutomaticLevelsWithoutNewOwnEvidence()
    {
        using (var service = Create())
        {
            service.ObserveDetectedSkills(Detected());
            Assert.Equal(15, Skill(service).CurrentLevel);
        }
        var original = File.ReadAllBytes(StorePath);
        using var restored = Create();
        Assert.Null(Skill(restored).CurrentLevel);
        Assert.Null(Skill(restored).CurrentBoardBonus);
        Assert.False(restored.Snapshot.CanApplyDetectedSkills);
        Assert.Equal(original, File.ReadAllBytes(StorePath));
    }

    [Fact]
    public async Task AutomaticChangesInvalidateTheDraftButDoNotRewriteANamedRouteOrItsRestoredInputs()
    {
        using var service = Create();
        service.ObserveDetectedSkills(Detected());
        await service.CalculateAsync();
        Assert.NotNull(service.Snapshot.Result);
        Assert.True(service.SaveRoute("Frozen skill inputs"));
        var route = Assert.Single(service.Snapshot.Routes);
        service.ObserveDetectedSkills(Detected(19, 4));
        Assert.Null(service.Snapshot.Result);
        Assert.Equal(15, route.Request.CombatContext!.Skills.Single(skill => skill.SkillId == SkillId).CurrentLevel);
        Assert.True(service.LoadRoute(route.Id));
        var anotherSkill = DaevanionCatalog.Default.Skills.First(skill => skill.ClassId == "Cleric" && skill.Id != SkillId).Id;
        var fuller = Detected(20, 4) with { Skills = Detected(20, 4).Skills.Add(anotherSkill,
            new(anotherSkill, 10, 10, new(0, 0, 0, 0, 0))) };
        service.ObserveDetectedSkills(fuller);
        Assert.Equal(15, Skill(service).CurrentLevel);
        Assert.Null(service.Snapshot.Draft.CombatContext!.Skills.Single(skill => skill.SkillId == anotherSkill).CurrentLevel);
        Assert.NotNull(service.Snapshot.Result);
        Assert.True(service.ApplyDetectedSkills());
        Assert.Equal(20, Skill(service).CurrentLevel);
        Assert.Null(service.Snapshot.Result);
    }

    [Fact]
    public async Task LostSkillAuthorityInvalidatesAutomaticInputsEvenWhenTheStoreCannotBeWritten()
    {
        using var service = Create();
        service.ObserveDetectedSkills(Detected());
        await service.CalculateAsync();
        Assert.NotNull(service.Snapshot.Result);
        File.Delete(StorePath);
        Directory.CreateDirectory(StorePath);
        service.ObserveDetectedSkills(OwnSkillStateSnapshot.Empty);
        Assert.Null(service.Snapshot.Result);
        Assert.Null(Skill(service).CurrentLevel);
        Assert.Null(Skill(service).CurrentBoardBonus);
        Assert.True(Directory.Exists(StorePath));
        Assert.False(service.ApplyDetectedSkills());
    }

    private sealed class EmptyOptimizer : IDaevanionOptimizer
    {
        public Task<DaevanionOptimizationResult> OptimizeAsync(DaevanionOptimizationRequest request,
            IProgress<DaevanionOptimizationProgress>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(new DaevanionOptimizationResult
            {
                ClassId = request.ClassId, CatalogVersion = DaevanionCatalog.Default.DataVersion,
                CatalogFingerprint = DaevanionRuleCatalog.Default.CatalogFingerprint,
                RulesVersion = DaevanionRuleCatalog.Default.Version, Status = DaevanionOptimizationStatus.Optimal,
                Boards = DaevanionRuleCatalog.Default.ForClass(request.ClassId).Select(board =>
                {
                    var root = board.Nodes.Single(node => node.Node.Kind == DaevanionNodeKind.Start).Node.Id;
                    return new DaevanionBoardPlan { BoardId = board.Board.Id, FamilyId = board.Board.FamilyId,
                        Pool = board.Pool, NodeIds = [root], ActivationOrder = [root] };
                }).ToArray()
            });
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
