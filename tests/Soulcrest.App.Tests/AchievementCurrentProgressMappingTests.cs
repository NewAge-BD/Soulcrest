using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Combat;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class AchievementCurrentProgressMappingTests
{
    private const int Nezekan = 401100500, Zikel = 401100600, Vaizel = 401100700;
    private const int Triniel = 401100800, Rare = 401101100;
    private const int Ariel = 401100900, Azphel = 401101000, Epic = 401101200, Unique = 401101300;
    private const int TrinielFourth = 401100804, TrinielFifth = 401100805;
    private const int RareFourth = 401101104, RareFifth = 401101105;
    private static readonly DateTimeOffset At = new(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);
    private static int Build => AchievementDefinitionCatalog.Default.MetadataBuild;

    [Fact]
    public void OnlyConfirmedNativeFamiliesUseCurrentStateAndEveryGoalHasItsExplicitStage()
    {
        var catalog = AchievementProgressThresholdCatalog.Default;
        var bindings = AchievementDefinitionCatalog.Default.Definitions.Where(definition => definition.Area == AchievementArea.Normal
            && definition.Role == AchievementRole.PlayerFacing && definition.SemanticGroup == "object-interactions"
            && CombatAchievementDisplayCatalog.Default.Find(definition.Id)?.GroupId == 400000302).Select(definition => definition.Id).ToArray();
        Assert.Equal(61, bindings.Length);
        Assert.Equal(new[] { 400100100, 401100400, 401100500, 401100600, 401100700, 401100800, 401100900, 401101000,
            401101100, 401101200, 401101300, 403100100, 403100200, 403100300, 403100400, 403100500,
            403100600, 403100700, 403100800, 403100900, 403101000, 403101100, 403101200, 403101300,
            403101400, 403101500, 403101600, 403101700, 420100100, 420100200, 420100600, 420100800,
            420101300, 420101400, 421106200, 422100200, 422100300, 422100400, 422100500, 422100600,
            422100700, 422100800, 422100900, 422101000, 430100300, 430100400, 430100500, 430100600,
            430100700, 430100800, 431100300, 431100400, 431100500, 431100600, 431100700, 431100800,
            432100300, 432100400, 432100500, 432100600, 432100700, 432100800, 433100300, 433100400,
            433100500, 433100600, 433100700, 433100800, 434100300, 434100400, 434100500, 434100600,
            434100700, 434100800, 434101300, 435100300, 435100400, 435100500, 435100600, 435100700,
            435100800, 436100200, 436100300, 436102100, 436102200, 440101400, 440101600, 440101700,
            440101800, 440102300, 440102400, 440102500, 440102600, 440102700, 441100700, 443100100,
            443100200, 443100300, 443100400, 444100100, 445100100, 445100400, 445100700, 445101000,
            445101300 }.Concat(bindings).Order(),
            catalog.Families.Where(family => family.ModelCurrentState).Select(family => family.Id).Order());
        Assert.Equal(330, catalog.Families.Count(family => family.CounterSemantics == AchievementProgressCounterSemantics.Cumulative));
        var triniel = catalog.FindFamily(Triniel)!;
        var rare = catalog.FindFamily(Rare)!;
        Assert.Equal(AchievementProgressCounterSemantics.CurrentState, triniel.CounterSemantics);
        Assert.Equal(AchievementProgressCounterSemantics.CurrentState, rare.CounterSemantics);
        Assert.Equal(new[] { 401100801, 401100802, 401100803, TrinielFourth, TrinielFifth }, triniel.Stages.Select(stage => stage.Id));
        Assert.Equal(new long[] { 20, 40, 60, 80, 100 }, triniel.Stages.Select(stage => stage.Target));
        Assert.Equal(new[] { 401101101, 401101102, 401101103, RareFourth, RareFifth }, rare.Stages.Select(stage => stage.Id));
        Assert.Equal(new long[] { 5, 10, 20, 40, 60 }, rare.Stages.Select(stage => stage.Target));
        foreach (var family in catalog.Families.Where(family => family.ModelCurrentState))
        {
            Assert.Equal(AchievementProgressCounterSemantics.CurrentState, family.CounterSemantics);
            var definition = AchievementDefinitionCatalog.Default.Find(family.Id)!;
            Assert.Equal(definition.Signature, family.DefinitionSignature);
            Assert.Equal(definition.StageIds, family.Stages.Select(stage => stage.Id));
            Assert.Equal(CombatAchievementCounterRule.Unqualified,
                CombatAchievementProgressContractCatalog.Default.FindFamily(family.Id)!.CounterRule);
        }
        Assert.Null(catalog.FindFamily(301100800));
        Assert.Null(catalog.FindFamily(301101100));
        Assert.Null(catalog.FindFamily(301100500));
        Assert.Null(catalog.FindFamily(301100600));
        Assert.Null(catalog.FindFamily(301100700));
        Assert.Null(catalog.FindFamily(301100900));
        Assert.Null(catalog.FindFamily(301101000));
        Assert.Null(catalog.FindFamily(301101200));
        Assert.Null(catalog.FindFamily(301101300));
        Assert.Null(catalog.FindFamily(321106200));
        Assert.DoesNotContain(catalog.Families.Where(family => family.ModelCurrentState),
            family => CombatAchievementDisplayCatalog.Default.Find(family.Id)?.GroupId == 300000302);
        Assert.Equal("achievement-rules-v3", AchievementRuleEvaluator.Version);
    }

    [Fact]
    public void RemainingConfirmedFamiliesKeepTheirExactNativeGoalsAndDefinitionBinding()
    {
        var expected = new[]
        {
            (Id: Ariel, Signature: "bce3f6b4c3aee18bafc86d2df2b0e9170a3029e1302a6a372ad5350a7cf495bc",
                Stages: new[] { 401100901, 401100902, 401100903, 401100904, 401100905, 401100906 }, Goals: new long[] { 20, 40, 60, 80, 100, 130 }),
            (Id: Azphel, Signature: "56a2ba629b1e88581660827998f00de225731aa0837b5e7702e22832f77c86f1",
                Stages: new[] { 401101001, 401101002, 401101003, 401101004, 401101005, 401101006 }, Goals: new long[] { 20, 40, 60, 80, 100, 130 }),
            (Id: Epic, Signature: "7a7084e9fee4208e7c2b4342239a29a8dc3d459710206a9aa1b9499be10ad26f",
                Stages: new[] { 401101201, 401101202, 401101203, 401101204, 401101205 }, Goals: new long[] { 5, 10, 20, 40, 60 }),
            (Id: Unique, Signature: "3e93c27dfe72d01fb579df7abf113f508ee620401927991280985794e0493971",
                Stages: new[] { 401101301, 401101302, 401101303, 401101304 }, Goals: new long[] { 4, 8, 12, 20 })
        };
        foreach (var item in expected)
        {
            var family = AchievementProgressThresholdCatalog.Default.FindFamily(item.Id)!;
            Assert.True(family.ModelCurrentState);
            Assert.Equal(item.Signature, family.DefinitionSignature);
            Assert.Equal(item.Stages, family.Stages.Select(stage => stage.Id));
            Assert.Equal(item.Goals, family.Stages.Select(stage => stage.Target));
            Assert.Equal(AchievementDefinitionCatalog.Default.Find(item.Id)!.StageIds, family.Stages.Select(stage => stage.Id));
            Assert.Equal(CombatAchievementDisplayCatalog.Default.Find(item.Id)!.Stages.Select(stage => stage.Id), family.Stages.Select(stage => stage.Id));
        }
    }

    [Theory]
    [InlineData(Nezekan, "fa8d2ab19df998a93dafbb3f2a2217585e98c14842f8b750231610341fa327e5", 401100501, 401100502, 401100503, 401100504)]
    [InlineData(Zikel, "9716c0846befb8175601f3af21237a21c2ec053f5e04422762160a8634584a3d", 401100601, 401100602, 401100603, 401100604)]
    [InlineData(Vaizel, "00935597fcc5506143fbee37dce93cca9bbd27b43602447b46393234dc57d56f", 401100701, 401100702, 401100703, 401100704)]
    public void ConfirmedBoardFamiliesKeepTheirFourNativeGoalsAndExactDefinitionBinding(int id, string signature,
        int first, int second, int third, int fourth)
    {
        var family = AchievementProgressThresholdCatalog.Default.FindFamily(id)!;
        Assert.True(family.ModelCurrentState);
        Assert.Equal(signature, family.DefinitionSignature);
        Assert.Equal(new[] { first, second, third, fourth }, family.Stages.Select(stage => stage.Id));
        Assert.Equal(new long[] { 20, 40, 60, 80 }, family.Stages.Select(stage => stage.Target));
        Assert.Equal(AchievementDefinitionCatalog.Default.Find(id)!.StageIds, family.Stages.Select(stage => stage.Id));
        Assert.Equal(CombatAchievementDisplayCatalog.Default.Find(id)!.Stages.Select(stage => stage.Id), family.Stages.Select(stage => stage.Id));
    }

    [Theory]
    [InlineData(Nezekan, 66, 3, 401100504, 80, false, false)]
    [InlineData(Zikel, 66, 3, 401100604, 80, false, false)]
    [InlineData(Vaizel, 61, 3, 401100704, 80, false, false)]
    [InlineData(Triniel, 77, 3, TrinielFourth, 80, false, false)]
    [InlineData(Ariel, 80, 4, 401100905, 100, false, false)]
    [InlineData(Azphel, 30, 1, 401101002, 40, false, false)]
    [InlineData(Rare, 40, 4, RareFifth, 60, false, false)]
    [InlineData(Epic, 50, 4, 401101205, 60, false, false)]
    [InlineData(Unique, 20, 4, 401101304, 20, true, true)]
    public void PublicEvaluatorAndLegacyProjectionShowTheConfirmedCurrentGoals(int id, int count, byte prefix,
        int expectedStage, int expectedTarget, bool requirementsMet, bool claimed)
    {
        var raw = Raw((id, count, prefix));
        var direct = new AchievementRuleEvaluator().Evaluate(raw, Build, 7, 11).Find(id)!;
        var legacy = CombatAchievementProgressProjection.Create(Snapshot(raw), Build).Find(id)!;
        foreach (var family in new[] { direct, legacy })
        {
            Assert.NotEqual(CombatKnowledgeStatus.Conflict, family.Status);
            Assert.Equal(expectedStage, family.CurrentStageId);
            var current = family.Find(expectedStage)!;
            Assert.Equal(count, current.Current); Assert.Equal(expectedTarget, current.Target);
            Assert.Equal(requirementsMet, current.RequirementsMetNow); Assert.Equal(claimed, current.Claimed);
            if (requirementsMet) Assert.True(current.Completed);
            else Assert.NotEqual<bool?>(true, current.Completed);
            Assert.All(family.Stages.Values, stage => Assert.Equal(CombatAchievementProgressUnit.Count, stage.Unit));
        }
        Assert.Equal(count, raw.Achievements[id].Count);
        Assert.Equal(prefix, raw.Achievements[id].RewardedStep);
        Assert.Equal(long.MaxValue, raw.Achievements[id].CompleteTime);
    }

    [Fact]
    public void ArielAdvancesFromTheFifthGoalToTheExplicitSixthGoalWithoutClaiming()
    {
        var evaluator = new AchievementRuleEvaluator();
        var before = evaluator.Evaluate(Raw((Ariel, 80, 4)), Build, 7, 11).Find(Ariel)!;
        var raw = Raw((Ariel, 100, 4));
        var after = evaluator.Evaluate(raw, Build, 7, 12).Find(Ariel)!;
        Assert.Equal(401100905, before.CurrentStageId);
        Assert.Equal(100, before.Find(401100905)!.Target);
        Assert.Equal(401100906, after.CurrentStageId);
        Assert.Equal(100, after.Find(401100906)!.Current);
        Assert.Equal(130, after.Find(401100906)!.Target);
        Assert.True(after.Find(401100905)!.Completed);
        Assert.True(after.Find(401100905)!.RequirementsMetNow);
        Assert.False(after.Find(401100905)!.Claimed);
        Assert.False(evaluator.ClaimFact(raw, Ariel, 401100905));
        Assert.False(after.Find(401100906)!.RequirementsMetNow);
        Assert.Equal(4, raw.Achievements[Ariel].RewardedStep);
    }

    [Theory]
    [InlineData(Ariel, 80, 79, 4, 401100904, 401100905, 100)]
    [InlineData(Azphel, 30, 19, 1, 401101001, 401101002, 40)]
    [InlineData(Epic, 50, 39, 4, 401101204, 401101205, 60)]
    [InlineData(Unique, 20, 19, 4, 401101304, 401101304, 20)]
    public void RemainingCurrentValuesCanDecreaseBelowAClaimedGoalWithoutContradictingTheClaim(int id,
        int beforeCount, int afterCount, byte prefix, int claimedStage, int currentStage, int target)
    {
        var evaluator = new AchievementRuleEvaluator();
        var before = evaluator.Evaluate(Raw((id, beforeCount, prefix)), Build, 7, 11).Find(id)!;
        var afterRaw = Raw((id, afterCount, prefix));
        var after = evaluator.Evaluate(afterRaw, Build, 7, 12).Find(id)!;
        Assert.NotEqual(CombatKnowledgeStatus.Conflict, before.Status);
        Assert.NotEqual(CombatKnowledgeStatus.Conflict, after.Status);
        Assert.Equal(currentStage, after.CurrentStageId);
        Assert.Equal(afterCount, after.Find(currentStage)!.Current);
        Assert.Equal(target, after.Find(currentStage)!.Target);
        Assert.True(after.Find(claimedStage)!.Claimed);
        Assert.True(after.Find(claimedStage)!.Completed);
        Assert.False(after.Find(claimedStage)!.RequirementsMetNow);
        Assert.Equal(afterCount, afterRaw.Achievements[id].Count);
        Assert.Equal(prefix, afterRaw.Achievements[id].RewardedStep);
    }

    [Fact]
    public void FullyClaimedUniqueProgressHidesTheTrackerRowAndPreservesTheFavoriteOnDisk()
    {
        var root = Path.Combine(Path.GetTempPath(), "soulcrest-current-goal-favorite-" + Guid.NewGuid().ToString("N"));
        var owner = new AchievementProfileBinding(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var favorites = new AchievementFavoriteService(root);
            var source = new TrackerSource(owner);
            favorites.Toggle(owner, Unique);
            source.Publish(Raw((Unique, 20, 3)), NetworkAchievementObservationKind.Baseline);
            using var tracker = new AchievementTrackerService(source, favorites);
            tracker.Tick();
            var row = Assert.Single(tracker.Snapshot.Rows);
            Assert.Equal(Unique, row.FamilyId); Assert.Equal(401101304, row.StageId);
            Assert.Equal(20, row.Current); Assert.Equal(20, row.Target);
            Assert.True(row.IsFavorite); Assert.True(row.HasUnclaimedRewards);

            source.Publish(Raw((Unique, 20, 4)), NetworkAchievementObservationKind.Reward);
            tracker.Tick();

            Assert.Empty(tracker.Snapshot.Rows);
            Assert.True(favorites.Get(owner).Contains(Unique));
            using var reloaded = new AchievementFavoriteService(root);
            Assert.True(reloaded.Get(owner).Contains(Unique));
        }
        finally
        {
            var path = Path.GetFullPath(root);
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), path, StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    [Theory]
    [InlineData(Nezekan, 401100504, 401100505)]
    [InlineData(Zikel, 401100604, 401100605)]
    [InlineData(Vaizel, 401100704, 401100705)]
    public void ReachingEightyKeepsTheNativeTerminalFourthStageAndNeverInventsAFifth(int id, int fourth, int absentFifth)
    {
        var evaluator = new AchievementRuleEvaluator();
        var raw = Raw((id, 80, 3));
        var complete = evaluator.Evaluate(raw, Build, 7, 11).Find(id)!;
        Assert.Equal(4, complete.Stages.Count);
        Assert.Equal(fourth, complete.CurrentStageId);
        Assert.Null(complete.Find(absentFifth));
        Assert.Equal(80, complete.Find(fourth)!.Current);
        Assert.Equal(80, complete.Find(fourth)!.Target);
        Assert.True(complete.Find(fourth)!.RequirementsMetNow);
        Assert.True(complete.Find(fourth)!.Completed);
        Assert.False(complete.Find(fourth)!.Claimed);
        Assert.False(evaluator.ClaimFact(raw, id, fourth));

        var decreased = evaluator.Evaluate(Raw((id, 79, 4)), Build, 7, 12,
            earnedStageIds: ImmutableHashSet.Create(fourth)).Find(id)!;
        Assert.Equal(fourth, decreased.CurrentStageId);
        Assert.Null(decreased.Find(absentFifth));
        Assert.NotEqual(CombatKnowledgeStatus.Conflict, decreased.Status);
        Assert.Equal(79, decreased.Find(fourth)!.Current);
        Assert.Equal(80, decreased.Find(fourth)!.Target);
        Assert.False(decreased.Find(fourth)!.RequirementsMetNow);
        Assert.True(decreased.Find(fourth)!.Completed);
        Assert.True(decreased.Find(fourth)!.Claimed);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(39)]
    public void ACurrentValueDecreaseDoesNotContradictPreviouslyClaimedOrEarnedStages(int currentCount)
    {
        var evaluator = new AchievementRuleEvaluator();
        var beforeRaw = Raw((Rare, 42, 4));
        var afterRaw = Raw((Rare, currentCount, 4));
        var before = evaluator.Evaluate(beforeRaw, Build, 7, 11).Find(Rare)!;
        var after = evaluator.Evaluate(afterRaw, Build, 7, 12,
            earnedStageIds: ImmutableHashSet.Create(RareFourth)).Find(Rare)!;
        Assert.NotEqual(CombatKnowledgeStatus.Conflict, before.Status);
        Assert.NotEqual(CombatKnowledgeStatus.Conflict, after.Status);
        Assert.Equal(RareFifth, after.CurrentStageId);
        Assert.Equal(currentCount, after.Find(RareFifth)!.Current);
        Assert.Equal(60, after.Find(RareFifth)!.Target);
        Assert.True(after.Find(RareFourth)!.Claimed);
        Assert.True(after.Find(RareFourth)!.Completed);
        Assert.Equal(currentCount >= 40, after.Find(RareFourth)!.RequirementsMetNow);
        Assert.Equal(42, beforeRaw.Achievements[Rare].Count);
        Assert.Equal(currentCount, afterRaw.Achievements[Rare].Count);
        Assert.Equal(4, afterRaw.Achievements[Rare].RewardedStep);
    }

    [Fact]
    public void ReachingEightyMovesToTheExplicitHundredGoalWithoutClaimingTheFourthReward()
    {
        var evaluator = new AchievementRuleEvaluator();
        var before = evaluator.Evaluate(Raw((Triniel, 77, 3)), Build, 7, 11).Find(Triniel)!;
        var raw = Raw((Triniel, 80, 3));
        var after = evaluator.Evaluate(raw, Build, 7, 12).Find(Triniel)!;
        Assert.Equal(TrinielFourth, before.CurrentStageId);
        Assert.False(before.Find(TrinielFourth)!.RequirementsMetNow);
        Assert.Equal(TrinielFifth, after.CurrentStageId);
        Assert.True(after.Find(TrinielFourth)!.RequirementsMetNow);
        Assert.True(after.Find(TrinielFourth)!.Completed);
        Assert.False(after.Find(TrinielFourth)!.Claimed);
        Assert.False(evaluator.ClaimFact(raw, Triniel, TrinielFourth));
        Assert.Equal(80, after.Find(TrinielFifth)!.Current);
        Assert.Equal(100, after.Find(TrinielFifth)!.Target);
        Assert.False(after.Find(TrinielFifth)!.RequirementsMetNow);
        Assert.Equal(3, raw.Achievements[Triniel].RewardedStep);
    }

    [Fact]
    public void PermanentClaimHistoryAndEarnedHistoryStayIndependentOfCurrentRequirementsAndRawPrefix()
    {
        var raw = Raw((Triniel, 77, 0));
        var evaluator = new AchievementRuleEvaluator();
        var earned = evaluator.Evaluate(raw, Build, 7, 11,
            earnedStageIds: ImmutableHashSet.Create(TrinielFourth)).Find(Triniel)!;
        Assert.True(earned.Find(TrinielFourth)!.Completed);
        Assert.False(earned.Find(TrinielFourth)!.RequirementsMetNow);
        Assert.False(earned.Find(TrinielFourth)!.Claimed);
        Assert.NotEqual(CombatKnowledgeStatus.Conflict, earned.Status);
        var claimed = evaluator.Evaluate(raw, Build, 7, 12,
            knownClaimedStageIds: ImmutableHashSet.Create(TrinielFourth)).Find(Triniel)!;
        Assert.True(claimed.Find(TrinielFourth)!.Claimed);
        Assert.True(claimed.Find(TrinielFourth)!.Completed);
        Assert.False(claimed.Find(TrinielFourth)!.RequirementsMetNow);
        Assert.Equal(TrinielFifth, claimed.CurrentStageId);
        Assert.Equal(100, claimed.Find(TrinielFifth)!.Target);
        Assert.NotEqual(CombatKnowledgeStatus.Conflict, claimed.Status);
        Assert.Equal("permanent", evaluator.QualifiedPeriodId(Triniel));
        Assert.Equal(0, raw.Achievements[Triniel].RewardedStep);
    }

    [Theory]
    [InlineData(Nezekan, 401100504)]
    [InlineData(Zikel, 401100604)]
    [InlineData(Vaizel, 401100704)]
    [InlineData(Ariel, 401100905)]
    [InlineData(Azphel, 401101002)]
    [InlineData(Triniel, TrinielFourth)]
    [InlineData(Rare, RareFourth)]
    [InlineData(Epic, 401101205)]
    [InlineData(Unique, 401101304)]
    public void MissingAndNegativeCountersRemainUnknownWithoutErasingIndependentClaims(int id, int claimedStage)
    {
        var evaluator = new AchievementRuleEvaluator();
        Assert.Null(evaluator.Evaluate(Raw(), Build, 7, 11).Find(id));
        var negative = Raw((id, -17, 0));
        var unknown = evaluator.Evaluate(negative, Build, 7, 12).Find(id)!;
        Assert.All(unknown.Stages.Values, stage => { Assert.Null(stage.Current); Assert.Null(stage.Target); });
        var claimed = evaluator.Evaluate(negative, Build, 7, 13,
            knownClaimedStageIds: ImmutableHashSet.Create(claimedStage)).Find(id)!;
        Assert.True(claimed.Find(claimedStage)!.Claimed);
        Assert.True(claimed.Find(claimedStage)!.Completed);
        Assert.All(claimed.Stages.Values, stage => { Assert.Null(stage.Current); Assert.Null(stage.Target); });
        Assert.Equal(-17, negative.Achievements[id].Count);
    }

    [Fact]
    public void ABuildMismatchCannotGrantTheNewGoalsThroughEitherPublicEntryPoint()
    {
        var raw = Raw((Nezekan, 66, 3), (Zikel, 66, 3), (Vaizel, 61, 3), (Triniel, 77, 3),
            (Ariel, 80, 4), (Azphel, 30, 1), (Rare, 40, 4), (Epic, 50, 4), (Unique, 20, 4));
        Assert.Empty(new AchievementRuleEvaluator().Evaluate(raw, Build + 1, 7, 11).Families);
        Assert.Empty(CombatAchievementProgressProjection.Create(Snapshot(raw), Build + 1).Families);
    }

    [Fact]
    public void TheEvaluatorRechecksTheNativeDefinitionSignatureInsteadOfTrustingTheLoadedThresholdObject()
    {
        var document = JsonNode.Parse(Embedded("Soulcrest.AchievementDefinitionScopes.json"))!;
        var row = document["definitions"]!.AsArray().Single(item => item!["id"]!.GetValue<int>() == Triniel)!;
        row["signature"] = new string('f', 64);
        var changed = AchievementDefinitionCatalog.FromJson(document.ToJsonString());
        var evaluator = new AchievementRuleEvaluator(CombatAchievementProgressContractCatalog.Default,
            definitions: changed, thresholds: AchievementProgressThresholdCatalog.Default);
        var family = evaluator.Evaluate(Raw((Triniel, 77, 3)), Build, 7, 11).Find(Triniel)!;
        Assert.Equal(TrinielFourth, family.CurrentStageId);
        Assert.Null(family.Find(TrinielFourth)!.Current);
        Assert.Null(family.Find(TrinielFourth)!.Target);
        Assert.False(family.Find(TrinielFourth)!.Claimed);
    }

    [Theory]
    [InlineData("unknown-mode")]
    [InlineData("lowercase-mode")]
    [InlineData("null-mode")]
    [InlineData("numeric-mode")]
    [InlineData("boolean-mode")]
    [InlineData("empty-mode")]
    [InlineData("build")]
    [InlineData("signature")]
    [InlineData("missing-stage")]
    [InlineData("reordered-stages")]
    [InlineData("zero-goal")]
    public void MalformedModesOrUnboundNativeGoalsCannotEnterTheThresholdCatalog(string variant)
    {
        var document = NativeThresholdDocument(Triniel);
        var row = document["families"]![0]!;
        switch (variant)
        {
            case "unknown-mode": row["counterSemantics"] = "BestValue"; break;
            case "lowercase-mode": row["counterSemantics"] = "currentstate"; break;
            case "null-mode": row["counterSemantics"] = null; break;
            case "numeric-mode": row["counterSemantics"] = 1; break;
            case "boolean-mode": row["counterSemantics"] = true; break;
            case "empty-mode": row["counterSemantics"] = ""; break;
            case "build": document["metadataBuild"] = Build + 1; break;
            case "signature": row["definitionSignature"] = new string('f', 64); break;
            case "missing-stage": row["stages"]!.AsArray().RemoveAt(0); break;
            case "reordered-stages": row["stages"]![0]!["id"] = 401100802; row["stages"]![1]!["id"] = 401100801; break;
            case "zero-goal": row["stages"]![0]!["target"] = 0; break;
            default: throw new ArgumentOutOfRangeException(nameof(variant));
        }
        Assert.Throws<InvalidDataException>(() => AchievementProgressThresholdCatalog.FromJson(document.ToJsonString()));
    }

    [Fact]
    public void ExistingThresholdDocumentsWithoutAModeKeepTheCumulativeDefault()
    {
        var old = AchievementProgressThresholdCatalog.Default.Families.First(family => !family.ModelCurrentState);
        var document = NativeThresholdDocument(old.Id);
        document["families"]![0]!.AsObject().Remove("counterSemantics");
        var parsed = AchievementProgressThresholdCatalog.FromJson(document.ToJsonString()).FindFamily(old.Id)!;
        Assert.Equal(AchievementProgressCounterSemantics.Cumulative, parsed.CounterSemantics);
        Assert.False(parsed.ModelCurrentState);
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("unbound")]
    [InlineData("wrong-source")]
    [InlineData("unknown-generation")]
    [InlineData("conflict")]
    public void LegacyProjectionStillRejectsUnqualifiedLiveSources(string variant)
    {
        var snapshot = Snapshot(Raw((Triniel, 77, 3), (Rare, 40, 4)));
        snapshot = variant switch
        {
            "stale" => snapshot with { Achievements = snapshot.Achievements with { IsFresh = false, Status = NetworkCombatSourceStatus.Stale } },
            "unbound" => snapshot with { ProfileId = "" },
            "wrong-source" => snapshot with { Achievements = snapshot.Achievements with { Source = "synthetic-unqualified" } },
            "unknown-generation" => snapshot with { Generation = 0 },
            "conflict" => snapshot with { Achievements = snapshot.Achievements with { Status = NetworkCombatSourceStatus.Conflict } },
            _ => throw new ArgumentOutOfRangeException(nameof(variant))
        };
        Assert.Empty(CombatAchievementProgressProjection.Create(snapshot, Build).Families);
    }

    [Theory]
    [InlineData("de", Nezekan, 66, 3, 401100504, 80)]
    [InlineData("en", Nezekan, 66, 3, 401100504, 80)]
    [InlineData("de", Zikel, 66, 3, 401100604, 80)]
    [InlineData("en", Zikel, 66, 3, 401100604, 80)]
    [InlineData("de", Vaizel, 61, 3, 401100704, 80)]
    [InlineData("en", Vaizel, 61, 3, 401100704, 80)]
    [InlineData("de", Triniel, 77, 3, TrinielFourth, 80)]
    [InlineData("en", Triniel, 77, 3, TrinielFourth, 80)]
    [InlineData("de", Rare, 40, 4, RareFifth, 60)]
    [InlineData("en", Rare, 40, 4, RareFifth, 60)]
    [InlineData("de", Ariel, 80, 4, 401100905, 100)]
    [InlineData("en", Ariel, 80, 4, 401100905, 100)]
    [InlineData("de", Azphel, 30, 1, 401101002, 40)]
    [InlineData("en", Azphel, 30, 1, 401101002, 40)]
    [InlineData("de", Epic, 50, 4, 401101205, 60)]
    [InlineData("en", Epic, 50, 4, 401101205, 60)]
    [InlineData("de", Unique, 20, 4, 401101304, 20)]
    [InlineData("en", Unique, 20, 4, 401101304, 20)]
    public async Task TheProductionBrowserRendersRegularCurrentCountersAndGoalsWithoutReportedBlocks(string language,
        int id, int count, byte prefix, int expectedStage, int expectedTarget)
    {
        var previous = UiText.Language; UiText.Language = language;
        try
        {
            var raw = Raw((id, count, prefix));
            var snapshot = Snapshot(raw);
            var state = new AchievementViewState
            {
                ProfileId = snapshot.ProfileId, Source = AchievementViewSource.Live, Generation = snapshot.Generation,
                Sequence = snapshot.Sequence, MetadataBuild = Build, ObservedAt = At,
                Progress = new AchievementRuleEvaluator().Evaluate(raw, Build, snapshot.Generation, snapshot.Sequence),
                ObservedFamilyIds = raw.Achievements.Keys.ToImmutableHashSet(),
                ObservedValues = raw.Achievements.ToImmutableDictionary(pair => pair.Key, pair => pair.Value.Count)
            };
            var services = new ServiceCollection(); services.AddLogging();
            await using var provider = services.BuildServiceProvider();
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            var html = await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var root = await renderer.RenderComponentAsync<CombatAchievementsView>(ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(CombatAchievementsView.Snapshot)] = snapshot,
                    [nameof(CombatAchievementsView.Catalog)] = DisplayCatalog(),
                    [nameof(CombatAchievementsView.AchievementState)] = state
                }));
                return WebUtility.HtmlDecode(root.ToHtmlString());
            });
            AssertCard(html, id, expectedStage, count, expectedTarget);
            Assert.DoesNotContain("ca-reported", html);
            Assert.DoesNotContain("data-achievement-reported", html);
            Assert.DoesNotContain("Reported value", html);
            Assert.DoesNotContain("Gemeldeter Wert", html);
            Assert.DoesNotContain("ca-card-conflict", html);
        }
        finally { UiText.Language = previous; }
    }

    private static void AssertCard(string html, int id, int stage, int current, int target)
    {
        var card = Regex.Match(html, "<article\\b[^>]*data-achievement-card=\"" + id + "\"[^>]*>.*?</article>", RegexOptions.Singleline).Value;
        Assert.NotEmpty(card);
        Assert.Contains("data-achievement-stage=\"" + stage + "\"", card);
        Assert.Contains("data-achievement-current-value=\"" + current + "\"", card);
        Assert.Contains("data-achievement-target=\"" + target + "\"", card);
        Assert.Contains("data-achievement-progress-unit=\"Count\"", card);
        Assert.Contains("class=\"ca-progress ca-progress-known\"", card);
        Assert.Contains("aria-valuenow=\"" + current + "\"", card);
        Assert.Contains("aria-valuemax=\"" + target + "\"", card);
    }

    private static CharacterAchievementState Raw(params (int Id, int Count, byte Prefix)[] rows) => new(
        rows.ToImmutableDictionary(row => row.Id, row => new CharacterAchievement(row.Count, long.MaxValue, row.Prefix)),
        ImmutableDictionary<int, ImmutableArray<bool>>.Empty);

    private static CharacterCombatSnapshot Snapshot(CharacterAchievementState raw) => new()
    {
        ProfileId = "SYNTHETIC-CURRENT-GOAL-PROFILE", IsFresh = true, Generation = 7, Sequence = 11,
        Achievements = new(raw, NetworkCombatSourceStatus.Complete, true, At, 11, "own-achievement-list")
    };

    private static string Embedded(string name)
    {
        using var stream = typeof(AchievementProgressThresholdCatalog).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("Missing bundled achievement test data.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static JsonObject NativeThresholdDocument(int familyId)
    {
        var document = JsonNode.Parse(Embedded("Soulcrest.AchievementProgressThresholds.json"))!.AsObject();
        var family = document["families"]!.AsArray().Single(row => row!["id"]!.GetValue<int>() == familyId)!.DeepClone();
        document["families"] = new JsonArray(family);
        return document;
    }

    private static CombatAchievementDisplayCatalog DisplayCatalog()
    {
        var native = CombatAchievementDisplayCatalog.Default;
        var rows = new[] { native.Find(Nezekan)!, native.Find(Zikel)!, native.Find(Vaizel)!, native.Find(Triniel)!,
            native.Find(Ariel)!, native.Find(Azphel)!, native.Find(Rare)!, native.Find(Epic)!, native.Find(Unique)! };
        var groups = rows.Select(row => row.GroupId).OfType<int>().ToHashSet();
        return CombatAchievementDisplayCatalog.FromJson(JsonSerializer.Serialize(new
        {
            schema = 1, metadataBuild = native.MetadataBuild, referenceOnly = true, doesNotEstablishCompletionOrClaim = true,
            categories = native.Categories, groups = native.Groups.Where(group => groups.Contains(group.Id)).ToArray(), achievements = rows
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    private sealed class TrackerSource(AchievementProfileBinding binding) : IAchievementTrackerSource
    {
        private readonly Guid _instance = Guid.NewGuid();
        private long _sequence;
        public AchievementOwnSnapshot CurrentOwnSnapshot { get; private set; } = AchievementOwnSnapshot.Empty;
        public event Action<AchievementChangeBatch>? AchievementObserved;

        internal void Publish(CharacterAchievementState raw, NetworkAchievementObservationKind kind)
        {
            var sequence = ++_sequence;
            var timestamp = TimeProvider.System.GetTimestamp();
            var state = new AchievementViewState
            {
                Binding = binding, ProfileId = binding.ProfileId, Source = AchievementViewSource.Live,
                MetadataBuild = Build, Generation = 7, Sequence = sequence, ObservedAt = At,
                Progress = new AchievementRuleEvaluator().Evaluate(raw, Build, 7, sequence),
                ObservedFamilyIds = raw.Achievements.Keys.ToImmutableHashSet(),
                ObservedValues = raw.Achievements.ToImmutableDictionary(pair => pair.Key, pair => pair.Value.Count)
            };
            CurrentOwnSnapshot = new(binding, state, raw, timestamp) { SourceInstanceId = _instance };
            AchievementObserved?.Invoke(new(CurrentOwnSnapshot, kind, [], [], false, _instance, sequence, 7, timestamp));
        }
    }
}
