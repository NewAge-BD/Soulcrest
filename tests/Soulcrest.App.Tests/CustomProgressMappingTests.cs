using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Soulcrest.App.Components;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Combat;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class CustomProgressMappingTests
{
    private const int Friends = 403100300, Cogni = 403100400, AnimalSound = 403100600;
    private const int Forest = 403100800, Change = 403101000, Genius = 403101700;
    private static int Build => AchievementDefinitionCatalog.Default.MetadataBuild;
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch.AddDays(20000);
    private sealed record ExpectedBinding(int Id, string Signature, AchievementProgressThresholdStage[] Stages);
    private static readonly ExpectedBinding[] Expected =
    [
        new(403100300, "c2e6ef1ddd2c498ca278227fcfb27880a5b4cc758a5aba90b7b5f476809e9cf8", [new(403100301, 3), new(403100302, 10), new(403100303, 30), new(403100304, 50), new(403100305, 100), new(403100306, 150), new(403100307, 200)]),
        new(403100400, "7c345d611cc3429591b80eeddd489b8817e824c0e21cfff2cdca46df0b9cb732", [new(403100401, 3), new(403100402, 7), new(403100403, 10), new(403100404, 20), new(403100405, 30), new(403100406, 40), new(403100407, 50)]),
        new(403100600, "88388bc9cef3bf4eb1aa0870f363240efdb381d6792d112d3a87d3ec673b690f", [new(403100601, 3), new(403100602, 7), new(403100603, 10), new(403100604, 20), new(403100605, 30), new(403100606, 40), new(403100607, 50)]),
        new(403100800, "8205990efc11df4ca9328f9293ef4d8ab9f75df7ad68e7f82232a60604ce418c", [new(403100801, 3), new(403100802, 7), new(403100803, 10), new(403100804, 20), new(403100805, 30), new(403100806, 40), new(403100807, 50)]),
        new(403101000, "127ae65b0e39c2afe7577616b58642e7bdab8ccaa58af17865677a4649490b3d", [new(403101001, 3), new(403101002, 7), new(403101003, 10), new(403101004, 20), new(403101005, 30), new(403101006, 40), new(403101007, 50)]),
        new(403101700, "e8c2e3d1cd5e0e4920bc8fc8f94289abf7bfc906499e8e7e01f07a7db054ab80", [new(403101701, 5)]),
    ];

    [Fact]
    public void AllSixNewBindingsMatchThirtySixExplicitNativeStagesAndFingerprints()
    {
        Assert.Equal(6, Expected.Length);
        Assert.Equal(36, Expected.Sum(family => family.Stages.Length));
        foreach (var expected in Expected)
        {
            var threshold = AchievementProgressThresholdCatalog.Default.FindFamily(expected.Id)!;
            var definition = AchievementDefinitionCatalog.Default.Find(expected.Id)!;
            Assert.Equal(AchievementArea.Normal, definition.Area);
            Assert.Equal(AchievementRole.PlayerFacing, definition.Role);
            Assert.Equal(expected.Signature, definition.Signature);
            Assert.Equal(expected.Signature, threshold.DefinitionSignature);
            Assert.Equal(AchievementProgressCounterSemantics.CurrentState, threshold.CounterSemantics);
            Assert.True(threshold.ModelCurrentState);
            Assert.Equal(expected.Stages, threshold.Stages);
            Assert.Equal(expected.Stages.Select(stage => stage.Id), definition.StageIds);
            Assert.Equal(expected.Stages.Select(stage => stage.Id),
                CombatAchievementDisplayCatalog.Default.Find(expected.Id)!.Stages.Select(stage => stage.Id));
            Assert.Equal(CombatAchievementCounterRule.Unqualified,
                CombatAchievementProgressContractCatalog.Default.FindFamily(expected.Id)!.CounterRule);
        }
    }

    [Fact]
    public void EveryConfirmedValueAndTerminalClaimIsAvailableThroughBothPublicProjectionPaths()
    {
        var raw = ConfirmedRows();
        var direct = new AchievementRuleEvaluator().Evaluate(raw, Build, 28, 11);
        var legacy = CombatAchievementProgressProjection.Create(Snapshot(raw), Build);
        foreach (var result in new[] { direct, legacy })
        {
            Assert.Equal(6, result.Families.Count);
            AssertProgress(result, Cogni, 403100406, 32, 40, false);
            AssertProgress(result, Forest, 403100807, 44, 50, false);
            AssertProgress(result, Change, 403101007, 42, 50, false);
            AssertProgress(result, AnimalSound, 403100607, 50, 50, true);
            AssertProgress(result, Friends, 403100307, 200, 200, true);
            AssertProgress(result, Genius, 403101701, 5, 5, true);
        }
        Assert.Equal(5, raw.Achievements[Cogni].RewardedStep);
        Assert.Equal(6, raw.Achievements[Forest].RewardedStep);
        Assert.Equal(6, raw.Achievements[Change].RewardedStep);
        Assert.Equal(7, raw.Achievements[AnimalSound].RewardedStep);
        Assert.Equal(7, raw.Achievements[Friends].RewardedStep);
        Assert.Equal(1, raw.Achievements[Genius].RewardedStep);
    }

    [Theory]
    [InlineData(Cogni, 40, 403100407, 50, 403100406)]
    [InlineData(Forest, 50, 403100807, 50, 403100807)]
    [InlineData(Change, 50, 403101007, 50, 403101007)]
    [InlineData(Friends, 150, 403100307, 200, 403100306)]
    [InlineData(Genius, 5, 403101701, 5, 403101701)]
    public void ReachingGoalsSelectsOnlyTheExplicitNextOrTerminalStageWithoutClaimingAReward(int id, int count,
        int currentStage, int target, int reachedStage)
    {
        var raw = Raw((id, count, 0));
        var evaluator = new AchievementRuleEvaluator();
        var family = evaluator.Evaluate(raw, Build, 28, 11).Find(id)!;
        Assert.NotEqual(CombatKnowledgeStatus.Conflict, family.Status);
        Assert.Equal(currentStage, family.CurrentStageId);
        Assert.Equal(count, family.Find(currentStage)!.Current);
        Assert.Equal(target, family.Find(currentStage)!.Target);
        Assert.True(family.Find(reachedStage)!.RequirementsMetNow);
        Assert.True(family.Find(reachedStage)!.Completed);
        Assert.False(family.Find(reachedStage)!.Claimed);
        Assert.False(evaluator.ClaimFact(raw, id, reachedStage));
        Assert.Equal(0, raw.Achievements[id].RewardedStep);
    }

    [Fact]
    public void FullyClaimedTerminalRowsDoNotBecomePendingTrackerRewards()
    {
        var raw = ConfirmedRows();
        var owner = new AchievementProfileBinding(new string('a', 32), new string('b', 32));
        var state = new AchievementViewState
        {
            ProfileId = owner.ProfileId, Binding = owner, Source = AchievementViewSource.Live,
            MetadataBuild = Build, Generation = 28, Sequence = 11,
            Progress = new AchievementRuleEvaluator().Evaluate(raw, Build, 28, 11)
        };
        var own = new AchievementOwnSnapshot(owner, state, raw, 0);
        var resolver = new AchievementTrackerStageResolver();
        foreach (var id in new[] { AnimalSound, Friends, Genius })
        {
            var selected = resolver.Resolve(own, id);
            Assert.True(selected.AllClaimed);
            Assert.False(selected.CompletedUnclaimed);
            Assert.True(selected.Row.HasQualifiedGoal);
            Assert.False(selected.Row.HasUnclaimedRewards);
        }
        foreach (var id in new[] { Cogni, Forest, Change }) Assert.False(resolver.Resolve(own, id).AllClaimed);
        Assert.Empty(resolver.PendingFamilies(own));
    }

    [Fact]
    public void CurrentCountsCanDecreaseWithoutContradictingClaimedOrEarnedHistory()
    {
        var evaluator = new AchievementRuleEvaluator();
        var raw = Raw(Expected.Select(family => (family.Id, Count: 0, Prefix: (byte)0)).ToArray());
        var stages = Expected.Select(family => family.Stages[^1].Id).ToImmutableHashSet();
        var earned = evaluator.Evaluate(raw, Build, 28, 11, earnedStageIds: stages);
        var claimed = evaluator.Evaluate(raw, Build, 28, 12, knownClaimedStageIds: stages);
        foreach (var expected in Expected)
        {
            var stageId = expected.Stages[^1].Id;
            var earnedFamily = earned.Find(expected.Id)!;
            Assert.NotEqual(CombatKnowledgeStatus.Conflict, earnedFamily.Status);
            Assert.False(earnedFamily.Find(stageId)!.RequirementsMetNow);
            Assert.True(earnedFamily.Find(stageId)!.Completed);
            Assert.False(earnedFamily.Find(stageId)!.Claimed);
            var claimedFamily = claimed.Find(expected.Id)!;
            Assert.NotEqual(CombatKnowledgeStatus.Conflict, claimedFamily.Status);
            Assert.False(claimedFamily.Find(stageId)!.RequirementsMetNow);
            Assert.True(claimedFamily.Find(stageId)!.Completed);
            Assert.True(claimedFamily.Find(stageId)!.Claimed);
        }
        Assert.All(raw.Achievements.Values, value => Assert.Equal(0, value.Count));
        Assert.All(raw.Achievements.Values, value => Assert.Equal(0, value.RewardedStep));
    }

    [Fact]
    public void SharedPetAndGenusSnapshotsCannotCreateOrOverrideOwnAchievementCounters()
    {
        var pets = new PetProgressSnapshot(Enumerable.Range(1, 200).ToImmutableDictionary(id => id, _ => 3),
            ImmutableDictionary<int, int>.Empty)
        {
            GenusBoards = Enumerable.Range(1, 5).ToImmutableDictionary(id => (byte)id,
                id => new CharacterGenusBoard((byte)id, 10, 0, ImmutableDictionary<byte, ImmutableArray<CharacterGenusEffect>>.Empty))
        };
        var empty = Snapshot(Raw()) with
        { Pets = new(pets, NetworkCombatSourceStatus.Complete, true, At, 12, "synthetic-shared-pet-list") };
        Assert.Empty(CombatAchievementProgressProjection.Create(empty, Build).Families);
        var own = Raw((Cogni, 32, 5), (Forest, 44, 6), (Change, 42, 6), (AnimalSound, 50, 7), (Friends, 17, 0), (Genius, 1, 0));
        var populated = empty with { Achievements = empty.Achievements with { Value = own } };
        var result = CombatAchievementProgressProjection.Create(populated, Build);
        Assert.Equal(32, result.Find(Cogni)!.Find(403100406)!.Current);
        Assert.Equal(44, result.Find(Forest)!.Find(403100807)!.Current);
        Assert.Equal(17, result.Find(Friends)!.Find(403100303)!.Current);
        Assert.Equal(30, result.Find(Friends)!.Find(403100303)!.Target);
        Assert.Equal(1, result.Find(Genius)!.Find(403101701)!.Current);
        Assert.Equal(5, result.Find(Genius)!.Find(403101701)!.Target);
    }

    [Fact]
    public void GenusChildRowsDoNotSupplyAnAbsentOwnGenusGeniusCounter()
    {
        var children = Raw(new[] { 403000100, 403000200, 403000300, 403000400, 403000500 }
            .Select(id => (Id: id, Count: 1, Prefix: (byte)1)).ToArray());
        Assert.Null(new AchievementRuleEvaluator().Evaluate(children, Build, 28, 11).Find(Genius));
    }

    [Fact]
    public void UnconfirmedElyosFamiliesRemainOutsideTheseFiniteRules()
    {
        var excluded = new[]
        {
            303100300, 303100400, 303100600, 303100800, 303101000, 303101700,
            303100500, 303100700, 303100900, 303101100
        };
        Assert.All(excluded, id => Assert.Null(AchievementProgressThresholdCatalog.Default.FindFamily(id)));
        var raw = Raw(excluded.Select(id => (Id: id, Count: 50, Prefix: (byte)0)).ToArray());
        var result = new AchievementRuleEvaluator().Evaluate(raw, Build, 28, 11);
        foreach (var family in result.Families.Values)
            Assert.All(family.Stages.Values, stage => { Assert.Null(stage.Current); Assert.Null(stage.Target); });
    }

    [Fact]
    public void MissingAndNegativeRowsRemainUnknownRatherThanFallingBackToPetCountsOrZero()
    {
        var evaluator = new AchievementRuleEvaluator();
        Assert.Empty(evaluator.Evaluate(Raw(), Build, 28, 11).Families);
        var raw = Raw(Expected.Select(family => (family.Id, Count: -17, Prefix: (byte)0)).ToArray());
        var result = evaluator.Evaluate(raw, Build, 28, 12);
        foreach (var family in result.Families.Values)
            Assert.All(family.Stages.Values, stage =>
            {
                Assert.Null(stage.Current); Assert.Null(stage.Target); Assert.Null(stage.Completed);
                Assert.Null(stage.RequirementsMetNow); Assert.False(stage.Claimed);
            });
        Assert.All(raw.Achievements.Values, row => Assert.Equal(-17, row.Count));
    }

    [Fact]
    public void MetadataBuildAndEveryNewDefinitionFingerprintMustStillMatch()
    {
        var raw = ConfirmedRows();
        Assert.Empty(new AchievementRuleEvaluator().Evaluate(raw, Build + 1, 28, 11).Families);
        Assert.Empty(CombatAchievementProgressProjection.Create(Snapshot(raw), Build + 1).Families);
        foreach (var expected in Expected)
        {
            var document = JsonNode.Parse(ThresholdDocument())!;
            var family = document["families"]!.AsArray().Single(row => row!["id"]!.GetValue<int>() == expected.Id)!;
            family["definitionSignature"] = new string('f', 64);
            Assert.Throws<InvalidDataException>(() => AchievementProgressThresholdCatalog.FromJson(document.ToJsonString()));
        }
    }

    private static void AssertProgress(CombatAchievementProgressSnapshot result, int familyId, int stageId, long current,
        long target, bool claimed)
    {
        var family = result.Find(familyId)!; var stage = family.Find(stageId)!;
        Assert.NotEqual(CombatKnowledgeStatus.Conflict, family.Status);
        Assert.Equal(stageId, family.CurrentStageId);
        Assert.Equal(current, stage.Current); Assert.Equal(target, stage.Target);
        Assert.Equal(CombatAchievementProgressUnit.Count, stage.Unit);
        Assert.Equal(claimed, stage.Claimed);
        Assert.Equal(current >= target, stage.RequirementsMetNow);
        if (claimed) Assert.True(stage.Completed);
        else Assert.NotEqual<bool?>(true, stage.Completed);
    }

    private static CharacterAchievementState ConfirmedRows() => Raw(
        (Cogni, 32, 5), (Forest, 44, 6), (Change, 42, 6), (AnimalSound, 50, 7), (Friends, 200, 7), (Genius, 5, 1));

    private static CharacterAchievementState Raw(params (int Id, int Count, byte Prefix)[] rows) => new(
        rows.ToImmutableDictionary(row => row.Id, row => new CharacterAchievement(row.Count, 0, row.Prefix)),
        ImmutableDictionary<int, ImmutableArray<bool>>.Empty);

    private static CharacterCombatSnapshot Snapshot(CharacterAchievementState raw) => new()
    {
        ProfileId = "SYNTHETIC-CUSTOM-PROGRESS-PROFILE", IsFresh = true, Generation = 28, Sequence = 11,
        Achievements = new(raw, NetworkCombatSourceStatus.Complete, true, At, 11, "own-achievement-list")
    };

    private static string ThresholdDocument()
    {
        using var stream = typeof(AchievementProgressThresholdCatalog).Assembly.GetManifestResourceStream("Soulcrest.AchievementProgressThresholds.json")
            ?? throw new InvalidOperationException("Missing bundled achievement goals.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
