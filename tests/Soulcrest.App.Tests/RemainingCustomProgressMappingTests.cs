using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Soulcrest.App.Components;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Combat;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class RemainingCustomProgressMappingTests
{
    private const int Legion = 403100100, Friends = 403100200, FriendsLast = 403100206;
    private static int Build => AchievementDefinitionCatalog.Default.MetadataBuild;
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch.AddDays(20000);
    private sealed record ExpectedBinding(int Id, string Signature, AchievementProgressThresholdStage[] Stages);
    private static readonly ExpectedBinding[] Expected =
    [
        new(403100100, "7af4c7303ec9626418782fc26cdf4660ef1ecee564af4c65bf2ab45e398d406c", [new(403100101, 1)]),
        new(403100200, "23e4ae173e0acea4c9531e7df1c055e045f7169a8cbe00fb32ead51fa77b2e30", [new(403100201, 1), new(403100202, 3), new(403100203, 5), new(403100204, 10), new(403100205, 15), new(403100206, 20)]),
        new(403100500, "1357133e2939eddbacfdb0df6d277ad52adbae953ca96cc969fd3f42b18ea7b5", [new(403100501, 1), new(403100502, 3), new(403100503, 5), new(403100504, 10), new(403100505, 15), new(403100506, 20)]),
        new(403100700, "705ee552d53e292fbd7dfb3fc36c12070c804bebdf5f45d00d71050e23fb5efc", [new(403100701, 1), new(403100702, 3), new(403100703, 5), new(403100704, 10), new(403100705, 15), new(403100706, 20)]),
        new(403100900, "fd181c289bbdb1c1ea1901b7d1d5e29e687185fc9cf7cc7ed71154f4828400b9", [new(403100901, 1), new(403100902, 3), new(403100903, 5), new(403100904, 10), new(403100905, 15), new(403100906, 20)]),
        new(403101100, "fbcfbfcd35d737856837905d8734310a396eb2fd45071e343a78e21bd7ba1fce", [new(403101101, 1), new(403101102, 3), new(403101103, 5), new(403101104, 10), new(403101105, 15), new(403101106, 20)]),
        new(403101200, "1853d7a0b8e8789fb739d508b3bca67feb193aa524f26546134ecd88ac5c4178", [new(403101201, 3), new(403101202, 5), new(403101203, 7), new(403101204, 10)]),
        new(403101300, "5a4a9a5215e855083163642fca42e6a331098365f70083bb1b050c656a927678", [new(403101301, 3), new(403101302, 5), new(403101303, 7), new(403101304, 10)]),
        new(403101400, "9b6d10afd7184b55175247003dc5cf01c934bcfc7dca7c34d7256f918a7ddb22", [new(403101401, 3), new(403101402, 5), new(403101403, 7), new(403101404, 10)]),
        new(403101500, "81c728e87c798ac43f7b903313000de74e818fcd076562a69fd63f1b00e1fb49", [new(403101501, 3), new(403101502, 5), new(403101503, 7), new(403101504, 10)]),
        new(403101600, "5501e81b9a8c30a64fcc223044033a927c03ad9893d62b3389a1ec731f81c183", [new(403101601, 3), new(403101602, 5), new(403101603, 7), new(403101604, 10)]),
    ];
    private static readonly int[] LevelThree = [403100500, 403100700, 403100900, 403101100];
    private static readonly int[] Insight = [403101200, 403101300, 403101400, 403101500, 403101600];

    [Fact]
    public void ElevenFiniteBindingsMatchExactlyFiftyOneNativeStagesAndFingerprints()
    {
        Assert.Equal(11, Expected.Length);
        Assert.Equal(51, Expected.Sum(family => family.Stages.Length));
        foreach (var expected in Expected)
        {
            var threshold = AchievementProgressThresholdCatalog.Default.FindFamily(expected.Id)!;
            var definition = AchievementDefinitionCatalog.Default.Find(expected.Id)!;
            Assert.Equal(AchievementArea.Normal, definition.Area);
            Assert.Equal(AchievementRole.PlayerFacing, definition.Role);
            Assert.Equal(expected.Signature, definition.Signature);
            Assert.Equal(expected.Signature, threshold.DefinitionSignature);
            Assert.Equal(expected.Stages, threshold.Stages);
            Assert.Equal(expected.Stages.Select(stage => stage.Id), definition.StageIds);
            Assert.Equal(expected.Stages.Select(stage => stage.Id),
                CombatAchievementDisplayCatalog.Default.Find(expected.Id)!.Stages.Select(stage => stage.Id));
            Assert.True(threshold.ModelCurrentState);
            Assert.Equal(AchievementProgressCounterSemantics.CurrentState, threshold.CounterSemantics);
            Assert.Equal(CombatAchievementCounterRule.Unqualified,
                CombatAchievementProgressContractCatalog.Default.FindFamily(expected.Id)!.CounterRule);
        }
    }

    [Fact]
    public void DefaultEvaluatorAndLegacyProjectionKeepAllElevenConfirmedValuesAndClaims()
    {
        var raw = ConfirmedRows();
        foreach (var result in new[]
        {
            new AchievementRuleEvaluator().Evaluate(raw, Build, 4, 11),
            CombatAchievementProgressProjection.Create(Snapshot(raw), Build)
        })
        {
            Assert.Equal(11, result.Families.Count);
            foreach (var id in LevelThree) AssertTerminal(result, id, 20, true);
            foreach (var id in Insight) AssertTerminal(result, id, 10, true);
            AssertTerminal(result, Legion, 1, true);
            var friend = result.Find(Friends)!;
            Assert.Equal(FriendsLast, friend.CurrentStageId);
            Assert.Equal(16, friend.Find(FriendsLast)!.Current);
            Assert.Equal(20, friend.Find(FriendsLast)!.Target);
            Assert.False(friend.Find(FriendsLast)!.RequirementsMetNow);
            Assert.False(friend.Find(FriendsLast)!.Claimed);
            Assert.Null(friend.Find(FriendsLast)!.Completed);
            Assert.NotEqual(CombatKnowledgeStatus.Conflict, friend.Status);
        }
        foreach (var id in LevelThree) Assert.Equal(6, raw.Achievements[id].RewardedStep);
        foreach (var id in Insight) Assert.Equal(4, raw.Achievements[id].RewardedStep);
        Assert.Equal(1, raw.Achievements[Legion].RewardedStep);
        Assert.Equal(5, raw.Achievements[Friends].RewardedStep);
    }

    [Theory]
    [InlineData(403100500, 15, 403100506, 20)]
    [InlineData(403101200, 7, 403101204, 10)]
    [InlineData(Friends, 15, FriendsLast, 20)]
    [InlineData(Friends, 20, FriendsLast, 20)]
    [InlineData(Legion, 1, 403100101, 1)]
    public void GoalsChooseOnlyTheExplicitNextOrTerminalStageAndNeverClaimIt(int id, int count, int expectedStage, int target)
    {
        var raw = Raw((id, count, 0));
        var evaluator = new AchievementRuleEvaluator();
        var family = evaluator.Evaluate(raw, Build, 4, 11).Find(id)!;
        Assert.Equal(expectedStage, family.CurrentStageId);
        var facts = family.Find(expectedStage)!;
        Assert.Equal(count, facts.Current); Assert.Equal(target, facts.Target);
        Assert.Equal(CombatAchievementProgressUnit.Count, facts.Unit);
        Assert.Equal(count >= target, facts.RequirementsMetNow);
        Assert.False(facts.Claimed);
        Assert.False(evaluator.ClaimFact(raw, id, expectedStage));
        Assert.Equal(0, raw.Achievements[id].RewardedStep);
    }

    [Fact]
    public void FriendRowsAreAbsoluteReportedValuesWithoutAnAcquisitionMechanismOrMonotoneAssumption()
    {
        var evaluator = new AchievementRuleEvaluator();
        var before = evaluator.Evaluate(Raw((Friends, 16, 5)), Build, 4, 11).Find(Friends)!;
        var afterRaw = Raw((Friends, 14, 5));
        var after = evaluator.Evaluate(afterRaw, Build, 4, 12).Find(Friends)!;
        Assert.Equal(16, before.Find(FriendsLast)!.Current);
        Assert.Equal(14, after.Find(FriendsLast)!.Current);
        Assert.Equal(20, after.Find(FriendsLast)!.Target);
        Assert.NotEqual(CombatKnowledgeStatus.Conflict, after.Status);
        Assert.False(after.Find(403100205)!.RequirementsMetNow);
        Assert.True(after.Find(403100205)!.Completed);
        Assert.True(after.Find(403100205)!.Claimed);
        Assert.False(after.Find(FriendsLast)!.Claimed);
        Assert.Equal(14, afterRaw.Achievements[Friends].Count);
        Assert.Equal(5, afterRaw.Achievements[Friends].RewardedStep);
    }

    [Fact]
    public void LearnedCompletionAndPermanentClaimsSurviveLowerCurrentValuesWithoutInventingRawCounters()
    {
        var evaluator = new AchievementRuleEvaluator();
        var raw = Raw(Expected.Select(family => (family.Id, Count: 0, Prefix: (byte)0)).ToArray());
        var finalStages = Expected.Select(family => family.Stages[^1].Id).ToImmutableHashSet();
        var earned = evaluator.Evaluate(raw, Build, 4, 11, earnedStageIds: finalStages);
        var claimed = evaluator.Evaluate(raw, Build, 4, 12, knownClaimedStageIds: finalStages);
        var missing = evaluator.Evaluate(Raw(), Build, 4, 13, knownClaimedStageIds: finalStages);
        foreach (var expected in Expected)
        {
            var stage = expected.Stages[^1].Id;
            Assert.NotEqual(CombatKnowledgeStatus.Conflict, earned.Find(expected.Id)!.Status);
            Assert.False(earned.Find(expected.Id)!.Find(stage)!.RequirementsMetNow);
            Assert.True(earned.Find(expected.Id)!.Find(stage)!.Completed);
            Assert.False(earned.Find(expected.Id)!.Find(stage)!.Claimed);
            Assert.NotEqual(CombatKnowledgeStatus.Conflict, claimed.Find(expected.Id)!.Status);
            Assert.False(claimed.Find(expected.Id)!.Find(stage)!.RequirementsMetNow);
            Assert.True(claimed.Find(expected.Id)!.Find(stage)!.Completed);
            Assert.True(claimed.Find(expected.Id)!.Find(stage)!.Claimed);
            Assert.True(missing.Find(expected.Id)!.Find(stage)!.Claimed);
            Assert.Null(missing.Find(expected.Id)!.Find(stage)!.Current);
            Assert.Null(missing.Find(expected.Id)!.Find(stage)!.Target);
        }
        Assert.All(raw.Achievements.Values, row => Assert.Equal(0, row.Count));
    }

    [Fact]
    public void CompletedTerminalBaselinesDoNotCreateUnclaimedTrackerRewards()
    {
        var raw = ConfirmedRows();
        var binding = new AchievementProfileBinding(new string('a', 32), new string('b', 32));
        var state = new AchievementViewState
        {
            Binding = binding, ProfileId = binding.ProfileId, Source = AchievementViewSource.Live,
            MetadataBuild = Build, Generation = 4, Sequence = 11,
            Progress = new AchievementRuleEvaluator().Evaluate(raw, Build, 4, 11)
        };
        var own = new AchievementOwnSnapshot(binding, state, raw, 0);
        var resolver = new AchievementTrackerStageResolver();
        foreach (var expected in Expected.Where(family => family.Id != Friends))
        {
            var row = resolver.Resolve(own, expected.Id);
            Assert.True(row.AllClaimed); Assert.False(row.CompletedUnclaimed);
            Assert.True(row.Row.HasQualifiedGoal); Assert.False(row.Row.HasUnclaimedRewards);
        }
        Assert.False(resolver.Resolve(own, Friends).AllClaimed);
        Assert.Empty(resolver.PendingFamilies(own));
    }

    [Fact]
    public void SharedPetAndInsightStateNeverProvidesMissingOwnPetGuildOrFriendCounters()
    {
        var pets = new PetProgressSnapshot(Enumerable.Range(1, 200).ToImmutableDictionary(id => id, _ => 3),
            ImmutableDictionary<int, int>.Empty)
        {
            GenusBoards = Enumerable.Range(1, 5).ToImmutableDictionary(id => (byte)id,
                id => new CharacterGenusBoard((byte)id, 10, 0, ImmutableDictionary<byte, ImmutableArray<CharacterGenusEffect>>.Empty))
        };
        var snapshot = Snapshot(Raw()) with
        { Pets = new(pets, NetworkCombatSourceStatus.Complete, true, At, 12, "synthetic-shared-pet-list") };
        Assert.Empty(CombatAchievementProgressProjection.Create(snapshot, Build).Families);
        var own = Raw(Expected.Select(family => (family.Id, Count: 0, Prefix: (byte)0)).ToArray());
        snapshot = snapshot with { Achievements = snapshot.Achievements with { Value = own } };
        var result = CombatAchievementProgressProjection.Create(snapshot, Build);
        Assert.Equal(11, result.Families.Count);
        foreach (var family in result.Families.Values)
        {
            var current = family.Find(family.CurrentStageId!.Value)!;
            Assert.Equal(0, current.Current); Assert.False(current.RequirementsMetNow);
            Assert.Null(current.Completed); Assert.False(current.Claimed);
        }
    }

    [Fact]
    public void MissingNegativeAndUnqualifiedSourcesDoNotTurnIntoZerosOrExternalFallbacks()
    {
        var evaluator = new AchievementRuleEvaluator();
        Assert.Empty(evaluator.Evaluate(Raw(), Build, 4, 11).Families);
        var negative = Raw(Expected.Select(family => (family.Id, Count: -17, Prefix: (byte)0)).ToArray());
        var result = evaluator.Evaluate(negative, Build, 4, 12);
        foreach (var family in result.Families.Values)
            Assert.All(family.Stages.Values, stage =>
            {
                Assert.Null(stage.Current); Assert.Null(stage.Target); Assert.Null(stage.RequirementsMetNow);
                Assert.Null(stage.Completed); Assert.False(stage.Claimed);
            });
        var stale = Snapshot(ConfirmedRows());
        stale = stale with { Achievements = stale.Achievements with { IsFresh = false, Status = NetworkCombatSourceStatus.Stale } };
        Assert.Empty(CombatAchievementProgressProjection.Create(stale, Build).Families);
        Assert.All(negative.Achievements.Values, row => Assert.Equal(-17, row.Count));
    }

    [Fact]
    public void EveryElyosCounterpartRemainsOutsideTheNewFiniteScope()
    {
        var excluded = new[]
        {
            303100100, 303100200, 303100500, 303100700, 303100900,
            303101100, 303101200, 303101300, 303101400, 303101500, 303101600
        };
        Assert.All(excluded, id => Assert.Null(AchievementProgressThresholdCatalog.Default.FindFamily(id)));
        var raw = Raw(excluded.Select(id => (Id: id, Count: 20, Prefix: (byte)0)).ToArray());
        var result = new AchievementRuleEvaluator().Evaluate(raw, Build, 4, 11);
        foreach (var family in result.Families.Values)
            Assert.All(family.Stages.Values, stage => { Assert.Null(stage.Current); Assert.Null(stage.Target); });
    }

    [Fact]
    public void MetadataBuildAndEveryDefinitionFingerprintStillGuardTheGoals()
    {
        var raw = ConfirmedRows();
        Assert.Empty(new AchievementRuleEvaluator().Evaluate(raw, Build + 1, 4, 11).Families);
        Assert.Empty(CombatAchievementProgressProjection.Create(Snapshot(raw), Build + 1).Families);
        foreach (var expected in Expected)
        {
            var document = JsonNode.Parse(ThresholdDocument())!;
            var family = document["families"]!.AsArray().Single(row => row!["id"]!.GetValue<int>() == expected.Id)!;
            family["definitionSignature"] = new string('f', 64);
            Assert.Throws<InvalidDataException>(() => AchievementProgressThresholdCatalog.FromJson(document.ToJsonString()));
        }
    }

    private static void AssertTerminal(CombatAchievementProgressSnapshot result, int familyId, long count, bool claimed)
    {
        var expected = Expected.Single(family => family.Id == familyId);
        var final = expected.Stages[^1]; var family = result.Find(familyId)!; var facts = family.Find(final.Id)!;
        Assert.NotEqual(CombatKnowledgeStatus.Conflict, family.Status);
        Assert.Equal(final.Id, family.CurrentStageId);
        Assert.Equal(count, facts.Current); Assert.Equal(final.Target, facts.Target);
        Assert.Equal(CombatAchievementProgressUnit.Count, facts.Unit);
        Assert.True(facts.RequirementsMetNow); Assert.True(facts.Completed); Assert.Equal(claimed, facts.Claimed);
    }

    private static CharacterAchievementState ConfirmedRows() => Raw(
        (Legion, 1, 1), (Friends, 16, 5),
        (403100500, 20, 6), (403100700, 20, 6), (403100900, 20, 6), (403101100, 20, 6),
        (403101200, 10, 4), (403101300, 10, 4), (403101400, 10, 4), (403101500, 10, 4), (403101600, 10, 4));

    private static CharacterAchievementState Raw(params (int Id, int Count, byte Prefix)[] rows) => new(
        rows.ToImmutableDictionary(row => row.Id, row => new CharacterAchievement(row.Count, 0, row.Prefix)),
        ImmutableDictionary<int, ImmutableArray<bool>>.Empty);

    private static CharacterCombatSnapshot Snapshot(CharacterAchievementState raw) => new()
    {
        ProfileId = "SYNTHETIC-REMAINING-CUSTOM-PROFILE", IsFresh = true, Generation = 4, Sequence = 11,
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
