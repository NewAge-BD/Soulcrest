using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Soulcrest.App.Components;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Combat;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class ExplorationAggregateProgressMappingTests
{
    private const int BindingVeteran = 421106200, BindingStage = 421106201;
    private const int WhiteWings = 420100100, WhiteWingsStage = 420100101;
    private const int Monolith = 420100800, MonolithFourth = 420100804, MonolithFifth = 420100805;
    private static int Build => AchievementDefinitionCatalog.Default.MetadataBuild;
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch.AddDays(20000);
    private sealed record ExpectedBinding(int Id, string Signature, AchievementProgressThresholdStage[] Stages);
    private static readonly ExpectedBinding[] Expected =
    [
        new(421106200, "cb8d6260274151341cb8f7e6bf14929ee0f4a8a3e4ec742331df322dedf80009", [new(421106201, 61)]),
        new(420100100, "97ab2b831c33032571584d332948c0aed02f4c7c00a7bd95fea5c6c1301556d1", [new(420100101, 2)]),
        new(420100800, "8f55c7242191e7f7062042fa6fa3d51cc3e61490f0536687982153de0b6dce15", [new(420100801, 3), new(420100802, 5), new(420100803, 10), new(420100804, 15), new(420100805, 20)]),
    ];

    [Fact]
    public void TheThreeConfirmedFamiliesBindExactlySevenExplicitNativeStagesAndSignatures()
    {
        Assert.Equal(3, Expected.Length);
        Assert.Equal(7, Expected.Sum(family => family.Stages.Length));
        foreach (var expected in Expected)
        {
            var actual = AchievementProgressThresholdCatalog.Default.FindFamily(expected.Id)!;
            var definition = AchievementDefinitionCatalog.Default.Find(expected.Id)!;
            var display = CombatAchievementDisplayCatalog.Default.Find(expected.Id)!;
            Assert.Equal(expected.Signature, definition.Signature);
            Assert.Equal(expected.Signature, actual.DefinitionSignature);
            Assert.Equal(AchievementArea.Normal, definition.Area);
            Assert.Equal(AchievementRole.PlayerFacing, definition.Role);
            Assert.Equal(AchievementProgressCounterSemantics.CurrentState, actual.CounterSemantics);
            Assert.True(actual.ModelCurrentState);
            Assert.Equal(expected.Stages.Select(stage => stage.Id), definition.StageIds);
            Assert.Equal(expected.Stages.Select(stage => stage.Id), display.Stages.Select(stage => stage.Id));
            Assert.Equal(expected.Stages, actual.Stages);
            Assert.Equal(CombatAchievementCounterRule.Unqualified,
                CombatAchievementProgressContractCatalog.Default.FindFamily(expected.Id)!.CounterRule);
        }
    }

    [Fact]
    public void DefaultEvaluatorAndLegacyProjectionUseTheConfirmedRawValuesAndIndependentClaims()
    {
        var raw = Raw((BindingVeteran, 61, 1), (WhiteWings, 1, 0), (Monolith, 16, 4));
        var direct = new AchievementRuleEvaluator().Evaluate(raw, Build, 28, 11);
        var legacy = CombatAchievementProgressProjection.Create(Snapshot(raw), Build);
        foreach (var result in new[] { direct, legacy })
        {
            Assert.Equal(3, result.Families.Count);
            AssertProgress(result, BindingVeteran, BindingStage, 61, 61, true, true);
            AssertProgress(result, WhiteWings, WhiteWingsStage, 1, 2, false, false);
            AssertProgress(result, Monolith, MonolithFifth, 16, 20, false, false);
            Assert.True(result.Find(Monolith)!.Find(MonolithFourth)!.Claimed);
            Assert.True(result.Find(Monolith)!.Find(MonolithFourth)!.Completed);
        }
        Assert.Equal(1, raw.Achievements[BindingVeteran].RewardedStep);
        Assert.Equal(0, raw.Achievements[WhiteWings].RewardedStep);
        Assert.Equal(4, raw.Achievements[Monolith].RewardedStep);
        Assert.All(raw.Achievements.Values, row => Assert.Equal(0, row.CompleteTime));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public void WhiteWingsHasOneExplicitTwoGoalStageWithoutAnInventedNextStageOrClaim(int count, bool met)
    {
        var raw = Raw((WhiteWings, count, 0));
        var evaluator = new AchievementRuleEvaluator();
        var result = evaluator.Evaluate(raw, Build, 28, 11);
        AssertProgress(result, WhiteWings, WhiteWingsStage, count, 2, met, false);
        var family = result.Find(WhiteWings)!;
        Assert.Single(family.Stages);
        Assert.Equal(WhiteWingsStage, family.CurrentStageId);
        Assert.Null(family.Find(420100102));
        if (met) Assert.True(family.Find(WhiteWingsStage)!.Completed);
        else Assert.Null(family.Find(WhiteWingsStage)!.Completed);
        Assert.False(evaluator.ClaimFact(raw, WhiteWings, WhiteWingsStage));
        Assert.Equal(0, raw.Achievements[WhiteWings].RewardedStep);
    }

    [Theory]
    [InlineData(15, false)]
    [InlineData(16, false)]
    [InlineData(20, true)]
    public void MonolithFifteenAndSixteenSelectTwentyWhileTwentyRemainsTheUnclaimedTerminalStage(int count, bool met)
    {
        var raw = Raw((Monolith, count, 4));
        var evaluator = new AchievementRuleEvaluator();
        var result = evaluator.Evaluate(raw, Build, 28, 11);
        AssertProgress(result, Monolith, MonolithFifth, count, 20, met, false);
        var family = result.Find(Monolith)!;
        Assert.Equal(5, family.Stages.Count);
        Assert.True(family.Find(MonolithFourth)!.RequirementsMetNow);
        Assert.True(family.Find(MonolithFourth)!.Completed);
        Assert.True(family.Find(MonolithFourth)!.Claimed);
        Assert.Null(family.Find(420100806));
        Assert.False(evaluator.ClaimFact(raw, Monolith, MonolithFifth));
        if (met) Assert.True(family.Find(MonolithFifth)!.Completed);
        else Assert.Null(family.Find(MonolithFifth)!.Completed);
        Assert.Equal(4, raw.Achievements[Monolith].RewardedStep);
    }

    [Theory]
    [InlineData(BindingVeteran, BindingStage, 61, 60)]
    [InlineData(WhiteWings, WhiteWingsStage, 2, 1)]
    [InlineData(Monolith, MonolithFifth, 20, 16)]
    public void CurrentRequirementsCanFallWithoutErasingIndependentlyEarnedCompletion(int familyId, int stageId,
        int beforeCount, int afterCount)
    {
        var evaluator = new AchievementRuleEvaluator();
        var before = evaluator.Evaluate(Raw((familyId, beforeCount, 0)), Build, 28, 11).Find(familyId)!.Find(stageId)!;
        Assert.True(before.RequirementsMetNow); Assert.True(before.Completed); Assert.False(before.Claimed);
        var raw = Raw((familyId, afterCount, 0));
        var after = evaluator.Evaluate(raw, Build, 28, 12, earnedStageIds: ImmutableHashSet.Create(stageId)).Find(familyId)!;
        Assert.NotEqual(CombatKnowledgeStatus.Conflict, after.Status);
        Assert.Equal(afterCount, after.Find(stageId)!.Current);
        Assert.False(after.Find(stageId)!.RequirementsMetNow);
        Assert.True(after.Find(stageId)!.Completed); Assert.False(after.Find(stageId)!.Claimed);
        Assert.Equal(0, raw.Achievements[familyId].RewardedStep);
    }

    [Fact]
    public void ExplicitPermanentClaimsSurviveAZeroCurrentCounterAndRegressedRawPrefix()
    {
        var raw = Raw((BindingVeteran, 0, 0), (WhiteWings, 0, 0), (Monolith, 0, 0));
        var claimed = ImmutableHashSet.Create(BindingStage, WhiteWingsStage, MonolithFifth);
        var evaluator = new AchievementRuleEvaluator();
        var result = evaluator.Evaluate(raw, Build, 28, 11, knownClaimedStageIds: claimed);
        foreach (var pair in new[] { (BindingVeteran, BindingStage), (WhiteWings, WhiteWingsStage), (Monolith, MonolithFifth) })
        {
            var family = result.Find(pair.Item1)!; var facts = family.Find(pair.Item2)!;
            Assert.NotEqual(CombatKnowledgeStatus.Conflict, family.Status);
            Assert.False(facts.RequirementsMetNow); Assert.True(facts.Completed); Assert.True(facts.Claimed);
            Assert.Equal("permanent", evaluator.QualifiedPeriodId(pair.Item1));
        }
        Assert.All(raw.Achievements.Values, row => Assert.Equal(0, row.RewardedStep));
    }

    [Fact]
    public void MissingAndNegativeRowsRemainUnknownWithoutInventingZeroOrNeverCompletedFacts()
    {
        var evaluator = new AchievementRuleEvaluator();
        Assert.Empty(evaluator.Evaluate(Raw(), Build, 28, 11).Families);
        var raw = Raw(Expected.Select(family => (family.Id, Count: -17, Prefix: (byte)0)).ToArray());
        var result = evaluator.Evaluate(raw, Build, 28, 12);
        foreach (var family in result.Families.Values)
            Assert.All(family.Stages.Values, stage =>
            {
                Assert.Null(stage.Current); Assert.Null(stage.Target);
                Assert.Null(stage.RequirementsMetNow); Assert.Null(stage.Completed); Assert.False(stage.Claimed);
            });
        Assert.All(raw.Achievements.Values, row => Assert.Equal(-17, row.Count));
    }

    [Fact]
    public void ExplicitAggregateCountersAreNotReconstructedFromOtherAchievementRows()
    {
        var childIds = AchievementDefinitionCatalog.Default.Definitions.Where(definition => definition.Area == AchievementArea.Normal
            && definition.Role == AchievementRole.PlayerFacing && definition.SemanticGroup == "object-interactions"
            && CombatAchievementDisplayCatalog.Default.Find(definition.Id)?.GroupId == 400000302)
            .Select(definition => definition.Id).ToArray();
        Assert.Equal(61, childIds.Length);
        var children = Raw(childIds.Concat(new[] { 420000100, 420000200 })
            .Select(id => (Id: id, Count: 1, Prefix: (byte)1)).ToArray());
        var evaluator = new AchievementRuleEvaluator();
        var missingParents = evaluator.Evaluate(children, Build, 28, 11);
        Assert.Null(missingParents.Find(BindingVeteran)); Assert.Null(missingParents.Find(WhiteWings));
        var withOwnCounter = children with
        { Achievements = children.Achievements.Add(WhiteWings, new CharacterAchievement(1, 0, 0)) };
        var whiteWings = evaluator.Evaluate(withOwnCounter, Build, 28, 12).Find(WhiteWings)!;
        Assert.Equal(1, whiteWings.Find(WhiteWingsStage)!.Current);
        Assert.Equal(2, whiteWings.Find(WhiteWingsStage)!.Target);
        Assert.False(whiteWings.Find(WhiteWingsStage)!.RequirementsMetNow);
    }

    [Fact]
    public void GenericDependenciesAndTheElyosSelfCycleCannotAcquireTheseFiniteRawCountRules()
    {
        var thresholds = AchievementProgressThresholdCatalog.Default;
        var definitions = AchievementDefinitionCatalog.Default;
        var qualifiedDependencies = thresholds.Families.Where(family =>
            definitions.Find(family.Id)?.SemanticGroup == "achievement-dependencies").Select(family => family.Id).Order();
        var expectedDependencies = new[] { 400100100, 403101700, 420100100, 420100200, 421106200, 422100200, 422100300, 422100400, 422100500, 422100600, 422100700, 422100800, 422100900, 422101000, 430100300, 430100400, 431100300, 431100400, 432100300, 432100400, 433100300, 433100400, 434100300, 434100400, 435100300, 435100400, 436100200, 436102100, 440101600, 440101700, 440102300, 440102400, 440102500, 440102600, 440102700, 443100200, 443100300, 443100400 };
        Assert.Equal(expectedDependencies, qualifiedDependencies);
        var elyos = new[] { 320100100, 320100800, 321106200 };
        Assert.All(elyos, id => Assert.Null(thresholds.FindFamily(id)));
        Assert.Equal("achievement-dependencies", definitions.Find(321106200)!.SemanticGroup);
        var generic = definitions.Definitions.Where(definition => definition.Area == AchievementArea.Normal
            && definition.Role == AchievementRole.PlayerFacing && definition.SemanticGroup == "achievement-dependencies"
            && !expectedDependencies.Contains(definition.Id)).Select(definition => definition.Id).ToArray();
        Assert.NotEmpty(generic);
        Assert.All(generic, id => Assert.Null(thresholds.FindFamily(id)));
        var result = new AchievementRuleEvaluator().Evaluate(Raw(generic.Concat(elyos).Distinct()
            .Select(id => (Id: id, Count: 61, Prefix: (byte)0)).ToArray()), Build, 28, 11);
        foreach (var family in result.Families.Values)
            Assert.All(family.Stages.Values, stage => { Assert.Null(stage.Current); Assert.Null(stage.Target); });
    }

    [Fact]
    public void BuildAndFingerprintGuardsRemainActiveForTheThreeNewFamilies()
    {
        var raw = Raw((BindingVeteran, 61, 1), (WhiteWings, 1, 0), (Monolith, 16, 4));
        Assert.Empty(new AchievementRuleEvaluator().Evaluate(raw, Build + 1, 28, 11).Families);
        Assert.Empty(CombatAchievementProgressProjection.Create(Snapshot(raw), Build + 1).Families);
        var wrongBuild = JsonNode.Parse(EmbeddedThresholds())!;
        wrongBuild["metadataBuild"] = Build + 1;
        Assert.Throws<InvalidDataException>(() => AchievementProgressThresholdCatalog.FromJson(wrongBuild.ToJsonString()));
        foreach (var expected in Expected)
        {
            var changed = JsonNode.Parse(EmbeddedThresholds())!;
            var family = changed["families"]!.AsArray().Single(row => row!["id"]!.GetValue<int>() == expected.Id)!;
            family["definitionSignature"] = new string('f', 64);
            Assert.Throws<InvalidDataException>(() => AchievementProgressThresholdCatalog.FromJson(changed.ToJsonString()));
        }
    }

    private static void AssertProgress(CombatAchievementProgressSnapshot result, int familyId, int stageId, long count,
        long target, bool met, bool claimed)
    {
        var family = result.Find(familyId)!; var stage = family.Find(stageId)!;
        Assert.NotEqual(CombatKnowledgeStatus.Conflict, family.Status);
        Assert.Equal(stageId, family.CurrentStageId);
        Assert.Equal(count, stage.Current); Assert.Equal(target, stage.Target);
        Assert.Equal(CombatAchievementProgressUnit.Count, stage.Unit);
        Assert.Equal(met, stage.RequirementsMetNow); Assert.Equal(claimed, stage.Claimed);
    }

    private static CharacterAchievementState Raw(params (int Id, int Count, byte Prefix)[] rows) => new(
        rows.ToImmutableDictionary(row => row.Id, row => new CharacterAchievement(row.Count, 0, row.Prefix)),
        ImmutableDictionary<int, ImmutableArray<bool>>.Empty);

    private static CharacterCombatSnapshot Snapshot(CharacterAchievementState raw) => new()
    {
        ProfileId = "SYNTHETIC-EXPLORATION-AGGREGATE-PROFILE", IsFresh = true, Generation = 28, Sequence = 11,
        Achievements = new(raw, NetworkCombatSourceStatus.Complete, true, At, 11, "own-achievement-list")
    };

    private static string EmbeddedThresholds()
    {
        using var stream = typeof(AchievementProgressThresholdCatalog).Assembly.GetManifestResourceStream("Soulcrest.AchievementProgressThresholds.json")
            ?? throw new InvalidOperationException("Missing bundled achievement goals.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
