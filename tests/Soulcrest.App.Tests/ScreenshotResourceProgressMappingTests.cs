using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Soulcrest.App.Components;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Combat;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class ScreenshotResourceProgressMappingTests
{
    private static int Build => AchievementDefinitionCatalog.Default.MetadataBuild;
    private sealed record ExpectedStage(int Id, long Target,
        AchievementProgressPresentation Presentation = AchievementProgressPresentation.Count);
    private sealed record ExpectedBinding(int Id, string Signature, ExpectedStage[] Stages);
    private sealed record ExpectedSample(int Id, int StageId, int Count, byte Prefix, int DisplayCurrent, int DisplayTarget);

    private static readonly ExpectedBinding[] Expected =
    [
        new(400100100, "5e46716c4762237108beb157c906fa025347831042e2068779c9e75d40cf161e", [new(400100101, 1), new(400100102, 2), new(400100103, 3), new(400100104, 4), new(400100105, 5), new(400100106, 6, AchievementProgressPresentation.Requirement)]),
        new(401100400, "a5f27cfb40e4e1f58ab10198a0462fa5f1b5c82bf882a429cd07e149428d4004", [new(401100401, 50), new(401100402, 100), new(401100403, 300), new(401100404, 500), new(401100405, 1000), new(401100406, 1500), new(401100407, 2000), new(401100408, 3000)]),
        new(420100200, "15a03f704ea35b30f4334dcb5c360efb40734e4c9505fed7f6ed3360aa22985b", [new(420100201, 2)]),
        new(420100600, "cd2989d659d85d16153f73688192a05a93926826a3069e5e7326fb34e347a253", [new(420100601, 5), new(420100602, 10), new(420100603, 15), new(420100604, 20), new(420100605, 25), new(420100606, 30)]),
        new(420101300, "393cc9ad211266e903b86d7e3d0320bd35d3e2e921a58916ad0a55e4d26a73e4", [new(420101301, 10), new(420101302, 30), new(420101303, 60), new(420101304, 120), new(420101305, 180), new(420101306, 300)]),
        new(420101400, "1393beea617148a249346f536e44d8e8d258bf77bc7ba0752113bb4a4b755a00", [new(420101401, 10), new(420101402, 30), new(420101403, 60), new(420101404, 120), new(420101405, 180), new(420101406, 300)]),
        new(440101400, "272f4b35ed5f59fcafc89a79a69ccbfc49a4078c20c290df963f805c1546ccca", [new(440101401, 3), new(440101402, 7), new(440101403, 12), new(440101404, 18), new(440101405, 25)]),
        new(440101600, "897256e87b7e6a393e8e67435499ee5dd9b064cab780e35a84b07486cfaae21d", [new(440101601, 3)]),
        new(440101700, "7530ae6f1c7ce58920d08280b390fe8503a63d42e6e5d1b9e988b16722bff168", [new(440101701, 3)]),
        new(440101800, "fbe70bb45da26f7f2abfb12cad81802844e87aad94066ed73eee8b06d77426c2", [new(440101801, 5), new(440101802, 20), new(440101803, 50), new(440101804, 100), new(440101805, 150), new(440101806, 200), new(440101807, 250)]),
        new(440102300, "849f2e9850b7f2185e3f7708d82a41a5cafbb72a9449b3fd84f31ff7dcd0fb74", [new(440102301, 1), new(440102302, 3), new(440102303, 5), new(440102304, 7), new(440102305, 9), new(440102306, 10)]),
        new(440102400, "e542819e3e213a91a6eed842e5028c813ba75cf97328a4c63390ff5c28f9daee", [new(440102401, 1), new(440102402, 2), new(440102403, 3), new(440102404, 4), new(440102405, 5), new(440102406, 6)]),
        new(440102500, "604ff89a0b62f7c2b975e0b3f26557c199ce2accda7ee5cc39eee6a0f0aa8a74", [new(440102501, 1), new(440102502, 2), new(440102503, 3), new(440102504, 4), new(440102505, 5), new(440102506, 6)]),
        new(440102600, "14378cbb591b32bfcbaf332996113b909ef9597a3f2d013419c44bb721afbb69", [new(440102601, 1), new(440102602, 3), new(440102603, 5), new(440102604, 7), new(440102605, 9), new(440102606, 11), new(440102607, 13)]),
        new(440102700, "be4ac5fd4370ccc92de179d037e4d9bd77b2e79a95ca3bba9b22d2874990a0ae", [new(440102701, 1), new(440102702, 2), new(440102703, 3), new(440102704, 4), new(440102705, 5), new(440102706, 6)]),
        new(441100700, "dcc00f176419dd0973adce460c8ef27f33cbe6c71baa6c1f1760850813df8daa", [new(441100701, 100000), new(441100702, 500000), new(441100703, 1000000), new(441100704, 5000000), new(441100705, 10000000), new(441100706, 50000000)]),
        new(443100100, "6dce8bdb5daaa3cb48a103feabaabf00fd6e8fe2df260cc62e708fb5c5207ea4", [new(443100101, 1)]),
        new(443100200, "5bb04636706d03bdfd97d85cd351feaa89adf173f5513900c2d1dfd9bf08cf68", [new(443100201, 1)]),
        new(443100300, "edd45a64193752425fcef3d28df82f82c2539c7dd5bfd20f59eebd451b71f972", [new(443100301, 1), new(443100302, 2), new(443100303, 3, AchievementProgressPresentation.Requirement)]),
        new(443100400, "69cfee932b28b83f996c5db44c15c18fa25467e9c6b3a4bf82fc381740ac5343", [new(443100401, 1), new(443100402, 2), new(443100403, 3), new(443100404, 4), new(443100405, 5, AchievementProgressPresentation.Requirement)]),
        new(444100100, "8e2b4e6b78e3fa7407df9884f63a8f57c44ee2f2b08e5636b7f2142668e05414", [new(444100101, 50), new(444100102, 100)]),
        new(445100100, "d3873b19b34332af517b53360aefe23f02235247559d623953ba06afda074b6c", [new(445100101, 50), new(445100102, 100)]),
        new(445100400, "adc4679b29804a67cab7ea8d3f1569f231449b2425eb5d88160b800cd4ed8b54", [new(445100401, 50), new(445100402, 100)]),
        new(445100700, "fbe3879d21d10dc731bdcf4e8a788323cbe14ce38b76178be0080c1677109046", [new(445100701, 50), new(445100702, 100)]),
        new(445101000, "1a514655daec604a24906657afa55ff2b2ebba47c4652d642e1216b8466120e4", [new(445101001, 50), new(445101002, 100)]),
        new(445101300, "9332a09a4e1444b319629f75ad34caa2966e1237a93a94e9022cd7cda53677d5", [new(445101301, 50), new(445101302, 100)]),
    ];

    private static readonly ExpectedSample[] Samples =
    [
        new(400100100, 400100106, 6, 6, 1, 1),
        new(401100400, 401100408, 3000, 8, 3000, 3000),
        new(420100200, 420100201, 2, 1, 2, 2),
        new(420100600, 420100606, 30, 6, 30, 30),
        new(420101300, 420101306, 300, 6, 300, 300),
        new(420101400, 420101406, 211, 5, 211, 300),
        new(440101400, 440101405, 19, 4, 19, 25),
        new(440101600, 440101601, 3, 1, 3, 3),
        new(440101700, 440101701, 2, 0, 2, 3),
        new(440101800, 440101807, 250, 7, 250, 250),
        new(440102300, 440102302, 2, 1, 2, 3),
        new(440102400, 440102405, 4, 4, 4, 5),
        new(440102500, 440102502, 1, 1, 1, 2),
        new(440102600, 440102601, 0, 0, 0, 1),
        new(440102700, 440102701, 0, 0, 0, 1),
        new(441100700, 441100704, 4083700, 3, 4083700, 5000000),
        new(443100100, 443100101, 0, 0, 0, 1),
        new(443100200, 443100201, 1, 1, 1, 1),
        new(443100300, 443100303, 3, 3, 1, 1),
        new(443100400, 443100405, 5, 5, 1, 1),
        new(444100100, 444100102, 100, 2, 100, 100),
        new(445100100, 445100101, 41, 0, 41, 50),
        new(445100400, 445100401, 8, 0, 8, 50),
        new(445100700, 445100702, 100, 2, 100, 100),
        new(445101000, 445101002, 100, 2, 100, 100),
        new(445101300, 445101302, 56, 1, 56, 100),
    ];

    private static readonly int[] ExcludedElyos =
    [
        300100100, 301100400, 320100200, 320100600, 320101300, 320101400,
        340101400, 340101600, 340101700, 340101800, 340102300, 340102400,
        340102500, 340102600, 340102700, 341100700, 343100100, 343100200,
        343100300, 343100400, 344100100, 345100100, 345100400, 345100700,
        345101000, 345101300
    ];

    [Fact]
    public void TwentySixFiniteFamiliesMatchOneHundredSixNativeStagesAndOnlyThreeRequirementPresentations()
    {
        var catalog = AchievementProgressThresholdCatalog.Default;
        Assert.Equal(496, catalog.Families.Count);
        Assert.Equal(2048, catalog.Families.Sum(family => family.Stages.Count));
        Assert.Equal(330, catalog.Families.Count(family => !family.ModelCurrentState));
        Assert.Equal(166, catalog.Families.Count(family => family.ModelCurrentState));
        Assert.Equal(26, Expected.Length);
        Assert.Equal(106, Expected.Sum(family => family.Stages.Length));
        Assert.Equal(Expected.Select(family => family.Id), Samples.Select(sample => sample.Id));
        Assert.Equal(26, Expected.Select(family => family.Id).Distinct().Count());

        foreach (var expected in Expected)
        {
            var actual = catalog.FindFamily(expected.Id)!;
            var definition = AchievementDefinitionCatalog.Default.Find(expected.Id)!;
            Assert.Equal(AchievementArea.Normal, definition.Area);
            Assert.Equal(AchievementRole.PlayerFacing, definition.Role);
            Assert.Equal(expected.Signature, definition.Signature);
            Assert.Equal(expected.Signature, actual.DefinitionSignature);
            Assert.Equal(AchievementProgressCounterSemantics.CurrentState, actual.CounterSemantics);
            Assert.Equal(expected.Stages.Select(stage => stage.Id), definition.StageIds);
            Assert.Equal(expected.Stages.Select(stage => stage.Id),
                CombatAchievementDisplayCatalog.Default.Find(expected.Id)!.Stages.Select(stage => stage.Id));
            Assert.Equal(expected.Stages.Select(stage => (stage.Id, stage.Target, stage.Presentation)),
                actual.Stages.Select(stage => (stage.Id, stage.Target, stage.Presentation)));
            Assert.Equal(CombatAchievementCounterRule.Unqualified,
                CombatAchievementProgressContractCatalog.Default.FindFamily(expected.Id)!.CounterRule);
        }

        var requirementStages = Expected.SelectMany(family => catalog.FindFamily(family.Id)!.Stages)
            .Where(stage => stage.Presentation == AchievementProgressPresentation.Requirement).Select(stage => stage.Id).Order();
        Assert.Equal(new[] { 400100106, 443100303, 443100405 }, requirementStages);
    }

    [Fact]
    public void EveryScreenshotSampleKeepsItsOwnRawValueWhileTheEvaluatorProjectionAndTrackerMatchItsDisplay()
    {
        var raw = ConfirmedRows();
        foreach (var result in new[]
        {
            new AchievementRuleEvaluator().Evaluate(raw, Build, 1, 1),
            CombatAchievementProgressProjection.Create(Snapshot(raw), Build)
        })
        {
            Assert.Equal(26, result.Families.Count);
            var owner = new AchievementProfileBinding(new string('a', 32), new string('b', 32));
            var state = new AchievementViewState
            {
                Binding = owner, ProfileId = owner.ProfileId, Source = AchievementViewSource.Live,
                MetadataBuild = Build, Generation = 1, Sequence = 1, Progress = result
            };
            var own = new AchievementOwnSnapshot(owner, state, raw, 0);
            var resolver = new AchievementTrackerStageResolver();
            foreach (var sample in Samples)
            {
                CompareRawAndDisplayUnitCount(result.Find(sample.Id)!, sample);
                var row = resolver.Resolve(own, sample.Id).Row;
                var expected = Expected.Single(family => family.Id == sample.Id).Stages.Single(stage => stage.Id == sample.StageId);
                Assert.Equal(sample.StageId, row.StageId);
                Assert.Equal(sample.Count, row.RawValue);
                Assert.Equal(sample.Count, row.Current);
                Assert.Equal(expected.Target, row.Target);
                Assert.Equal(CombatAchievementProgressUnit.Count, row.Unit);
                Assert.Equal((decimal)sample.DisplayCurrent, row.PresentationCurrent);
                Assert.Equal((decimal)sample.DisplayTarget, row.PresentationTarget);
                Assert.Equal(sample.Count, raw.Achievements[sample.Id].Count);
                Assert.Equal(sample.Prefix, raw.Achievements[sample.Id].RewardedStep);
            }
        }
    }

    [Theory]
    [InlineData(400100100, 400100106, 6)]
    [InlineData(443100300, 443100303, 3)]
    [InlineData(443100400, 443100405, 5)]
    public void RequirementPresentationPreservesTheNativeGoalRawCounterAndIndependentHistory(int id, int stageId, int goal)
    {
        var evaluator = new AchievementRuleEvaluator();
        foreach (var count in new[] { goal - 1, goal })
        {
            var raw = Raw((id, count, 0));
            var family = evaluator.Evaluate(raw, Build, 1, 1).Find(id)!;
            var facts = family.Find(stageId)!;
            Assert.Equal(stageId, family.CurrentStageId);
            Assert.Equal(count, facts.Current);
            Assert.Equal(goal, facts.Target);
            Assert.Equal(count >= goal ? 1m : 0m, facts.DisplayCurrent);
            Assert.Equal(1m, facts.DisplayTarget);
            Assert.Equal(CombatAchievementProgressUnit.Count, facts.Unit);
            Assert.Equal(count >= goal, facts.RequirementsMetNow);
            Assert.False(facts.Claimed);
            Assert.False(evaluator.ClaimFact(raw, id, stageId));
            if (count >= goal) Assert.True(facts.Completed);
            else Assert.Null(facts.Completed);
            Assert.Equal(count, raw.Achievements[id].Count);
            Assert.Equal(0, raw.Achievements[id].RewardedStep);
        }

        var history = ImmutableHashSet.Create(stageId);
        var lowerRaw = Raw((id, goal - 1, 0));
        var lower = evaluator.Evaluate(lowerRaw, Build, 1, 2,
            earnedStageIds: history, knownClaimedStageIds: history).Find(id)!;
        var preserved = lower.Find(stageId)!;
        Assert.NotEqual(CombatKnowledgeStatus.Conflict, lower.Status);
        Assert.Equal(goal - 1, preserved.Current);
        Assert.Equal(goal, preserved.Target);
        Assert.Equal(0m, preserved.DisplayCurrent);
        Assert.Equal(1m, preserved.DisplayTarget);
        Assert.False(preserved.RequirementsMetNow);
        Assert.True(preserved.Completed);
        Assert.True(preserved.Claimed);
        Assert.Equal(goal - 1, lowerRaw.Achievements[id].Count);
        Assert.Equal(0, lowerRaw.Achievements[id].RewardedStep);

        var contract = AchievementProgressThresholdCatalog.Default.FindFamily(id)!;
        foreach (var previous in contract.Stages.Where(stage => stage.Id != stageId))
            Assert.Equal(AchievementProgressPresentation.Count, previous.Presentation);
    }

    [Fact]
    public void MasteryAndSwimOrGlideStayOnTheReportedCountScaleWithoutExperienceOrMinuteContracts()
    {
        var result = new AchievementRuleEvaluator().Evaluate(ConfirmedRows(), Build, 1, 1);
        var mastery = new[] { 444100100, 445100100, 445100400, 445100700, 445101000, 445101300 };
        foreach (var id in mastery.Concat(new[] { 420101300, 420101400 }))
        {
            var contract = CombatAchievementProgressContractCatalog.Default.FindFamily(id)!;
            Assert.Equal(CombatAchievementCounterRule.Unqualified, contract.CounterRule);
            Assert.NotEqual(CombatAchievementProgressUnit.Minutes, contract.Unit);
            Assert.NotEqual(CombatAchievementProgressUnit.Experience, contract.Unit);
            var family = result.Find(id)!;
            var facts = family.Find(family.CurrentStageId!.Value)!;
            Assert.Equal(CombatAchievementProgressUnit.Count, facts.Unit);
            Assert.Null(facts.DisplayCurrent);
            Assert.Null(facts.DisplayTarget);
        }
        Assert.Equal(41, result.Find(445100100)!.Find(445100101)!.Current);
        Assert.Equal(50, result.Find(445100100)!.Find(445100101)!.Target);
        Assert.Equal(8, result.Find(445100400)!.Find(445100401)!.Current);
        Assert.Equal(56, result.Find(445101300)!.Find(445101302)!.Current);
        Assert.Equal(100, result.Find(445101300)!.Find(445101302)!.Target);
        Assert.Equal(300, result.Find(420101300)!.Find(420101306)!.Current);
        Assert.Equal(211, result.Find(420101400)!.Find(420101406)!.Current);
        Assert.Equal(300, result.Find(420101400)!.Find(420101406)!.Target);
    }

    [Fact]
    public void MissingNegativeAndExplicitZeroRowsCannotBeSuppliedOrOverriddenByExtraStatsOrCollections()
    {
        var populated = SnapshotWithOtherDomains(Raw());
        Assert.Empty(CombatAchievementProgressProjection.Create(populated, Build).Families);
        Assert.Empty(new AchievementRuleEvaluator().Evaluate(Raw(), Build, 1, 1).Families);

        var negative = Raw(Expected.Select(family => (family.Id, Count: -17, Prefix: (byte)0)).ToArray());
        var negativeSnapshot = populated with { Achievements = populated.Achievements with { Value = negative } };
        foreach (var result in new[]
        {
            new AchievementRuleEvaluator().Evaluate(negative, Build, 1, 1),
            CombatAchievementProgressProjection.Create(negativeSnapshot, Build)
        })
        {
            Assert.Equal(26, result.Families.Count);
            foreach (var family in result.Families.Values)
                Assert.All(family.Stages.Values, stage =>
                {
                    Assert.Null(stage.Current); Assert.Null(stage.Target);
                    Assert.Null(stage.DisplayCurrent); Assert.Null(stage.DisplayTarget);
                    Assert.Null(stage.RequirementsMetNow); Assert.Null(stage.Completed); Assert.False(stage.Claimed);
                });
        }
        Assert.All(negative.Achievements.Values, value => Assert.Equal(-17, value.Count));

        var zeros = Raw(Expected.Select(family => (family.Id, Count: 0, Prefix: (byte)0)).ToArray());
        var zeroResult = CombatAchievementProgressProjection.Create(
            populated with { Achievements = populated.Achievements with { Value = zeros } }, Build);
        Assert.Equal(26, zeroResult.Families.Count);
        foreach (var expected in Expected)
        {
            var family = zeroResult.Find(expected.Id)!;
            Assert.Equal(expected.Stages[0].Id, family.CurrentStageId);
            var current = family.Find(family.CurrentStageId!.Value)!;
            Assert.Equal(0, current.Current);
            Assert.Equal(expected.Stages[0].Target, current.Target);
            Assert.False(current.RequirementsMetNow);
            Assert.Null(current.Completed);
            Assert.False(current.Claimed);
        }
        var own = ConfirmedRows();
        var ownResult = CombatAchievementProgressProjection.Create(
            populated with { Achievements = populated.Achievements with { Value = own } }, Build);
        foreach (var sample in Samples) CompareRawAndDisplayUnitCount(ownResult.Find(sample.Id)!, sample);
    }

    [Theory]
    [InlineData("stale-own")]
    [InlineData("stale-domain")]
    [InlineData("missing-domain")]
    [InlineData("unbound")]
    [InlineData("wrong-source")]
    [InlineData("missing-observation")]
    [InlineData("unknown-generation")]
    public void OtherDomainsCannotRepairAnUnqualifiedOwnAchievementSnapshot(string variant)
    {
        var snapshot = SnapshotWithOtherDomains(ConfirmedRows());
        snapshot = variant switch
        {
            "stale-own" => snapshot with { IsFresh = false },
            "stale-domain" => snapshot with { Achievements = snapshot.Achievements with
                { IsFresh = false, Status = NetworkCombatSourceStatus.Stale } },
            "missing-domain" => snapshot with { Achievements = snapshot.Achievements with { Value = null } },
            "unbound" => snapshot with { ProfileId = null },
            "wrong-source" => snapshot with { Achievements = snapshot.Achievements with { Source = "synthetic-other-domain" } },
            "missing-observation" => snapshot with { Achievements = snapshot.Achievements with { ObservedAt = null } },
            "unknown-generation" => snapshot with { Generation = 0 },
            _ => throw new ArgumentOutOfRangeException(nameof(variant))
        };
        Assert.Empty(CombatAchievementProgressProjection.Create(snapshot, Build).Families);
    }

    [Fact]
    public void ExplicitOtherFactionDefinitionsDoNotBorrowTheseNumericOrPresentationRules()
    {
        Assert.Equal(26, ExcludedElyos.Length);
        Assert.All(ExcludedElyos, id => Assert.Null(AchievementProgressThresholdCatalog.Default.FindFamily(id)));
        var raw = Raw(ExcludedElyos.Select(id => (Id: id, Count: 100, Prefix: (byte)0)).ToArray());
        foreach (var result in new[]
        {
            new AchievementRuleEvaluator().Evaluate(raw, Build, 1, 1),
            CombatAchievementProgressProjection.Create(Snapshot(raw), Build)
        })
        {
            foreach (var expected in Expected) Assert.Null(result.Find(expected.Id));
            foreach (var id in ExcludedElyos)
                Assert.All(result.Find(id)!.Stages.Values, stage =>
                {
                    Assert.Null(stage.Current); Assert.Null(stage.Target);
                    Assert.Null(stage.DisplayCurrent); Assert.Null(stage.DisplayTarget);
                    Assert.Null(stage.Completed); Assert.False(stage.Claimed);
                });
        }
    }

    [Fact]
    public void MetadataBuildAndEveryFiniteDefinitionSignatureStillGuardTheBindings()
    {
        var raw = ConfirmedRows();
        Assert.Empty(new AchievementRuleEvaluator().Evaluate(raw, Build + 1, 1, 1).Families);
        Assert.Empty(CombatAchievementProgressProjection.Create(Snapshot(raw), Build + 1).Families);
        foreach (var expected in Expected)
        {
            var document = JsonNode.Parse(ThresholdDocument())!;
            var family = document["families"]!.AsArray().Single(row => row!["id"]!.GetValue<int>() == expected.Id)!;
            family["definitionSignature"] = new string('f', 64);
            Assert.Throws<InvalidDataException>(() => AchievementProgressThresholdCatalog.FromJson(document.ToJsonString()));
        }
    }

    private static void CompareRawAndDisplayUnitCount(CombatAchievementFamilyProgress family, ExpectedSample sample)
    {
        var binding = Expected.Single(expected => expected.Id == sample.Id);
        var expected = binding.Stages.Single(stage => stage.Id == sample.StageId);
        var ordinal = Array.FindIndex(binding.Stages, stage => stage.Id == sample.StageId) + 1;
        var facts = family.Find(sample.StageId)!;
        Assert.NotEqual(CombatKnowledgeStatus.Conflict, family.Status);
        Assert.Equal(sample.StageId, family.CurrentStageId);
        Assert.Equal(sample.Count, facts.Current);
        Assert.Equal(expected.Target, facts.Target);
        Assert.Equal(CombatAchievementProgressUnit.Count, facts.Unit);
        Assert.Equal(sample.Count >= expected.Target, facts.RequirementsMetNow);
        Assert.Equal(sample.Prefix >= ordinal, facts.Claimed);
        if (sample.Prefix >= ordinal || sample.Count >= expected.Target) Assert.True(facts.Completed);
        else Assert.Null(facts.Completed);
        if (expected.Presentation == AchievementProgressPresentation.Requirement)
        {
            Assert.Equal((decimal)sample.DisplayCurrent, facts.DisplayCurrent);
            Assert.Equal((decimal)sample.DisplayTarget, facts.DisplayTarget);
        }
        else
        {
            Assert.Null(facts.DisplayCurrent);
            Assert.Null(facts.DisplayTarget);
        }
        Assert.Equal((decimal)sample.DisplayCurrent, facts.DisplayCurrent ?? (decimal?)facts.Current);
        Assert.Equal((decimal)sample.DisplayTarget, facts.DisplayTarget ?? (decimal?)facts.Target);
    }

    private static CharacterAchievementState ConfirmedRows() => Raw(
        Samples.Select(sample => (sample.Id, sample.Count, sample.Prefix)).ToArray());

    private static CharacterAchievementState Raw(params (int Id, int Count, byte Prefix)[] rows) => new(
        rows.ToImmutableDictionary(row => row.Id, row => new CharacterAchievement(row.Count, 0, row.Prefix)),
        ImmutableDictionary<int, ImmutableArray<bool>>.Empty);

    private static CharacterCombatSnapshot Snapshot(CharacterAchievementState raw) => new()
    {
        ProfileId = "SYNTHETIC-RESOURCE-PROGRESS", IsFresh = true, Generation = 1, Sequence = 1,
        Achievements = new(raw, NetworkCombatSourceStatus.Complete, true, DateTimeOffset.UnixEpoch, 1, "own-achievement-list")
    };

    private static CharacterCombatSnapshot SnapshotWithOtherDomains(CharacterAchievementState raw) => Snapshot(raw) with
    {
        CharacterLevel = 45, CurrentCombatScore = 3000,
        NativeStats = new(ImmutableDictionary<ushort, int>.Empty.Add(1, 999999),
            NetworkCombatSourceStatus.Complete, true, DateTimeOffset.UnixEpoch, 1, "synthetic-stats"),
        TitleOwnership = new(Enumerable.Range(1, 500).ToImmutableHashSet(),
            NetworkCombatSourceStatus.Complete, true, DateTimeOffset.UnixEpoch, 1, "synthetic-titles"),
        WingOwnership = new(new CharacterWingOwnership(Enumerable.Range(1, 50).ToImmutableDictionary(id => id, _ => (byte)1)),
            NetworkCombatSourceStatus.Complete, true, DateTimeOffset.UnixEpoch, 1, "synthetic-wings"),
        Pets = new(new PetProgressSnapshot(Enumerable.Range(1, 200).ToImmutableDictionary(id => id, _ => 3),
            ImmutableDictionary<int, int>.Empty), NetworkCombatSourceStatus.Complete, true,
            DateTimeOffset.UnixEpoch, 1, "synthetic-pets")
    };

    private static string ThresholdDocument()
    {
        using var stream = typeof(AchievementProgressThresholdCatalog).Assembly
            .GetManifestResourceStream("Soulcrest.AchievementProgressThresholds.json")
            ?? throw new InvalidOperationException("Missing bundled achievement goals.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

