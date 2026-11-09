using System.Collections.Immutable;
using Soulcrest.App.Components;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Combat;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class ScreenshotAchievementMappingTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 9, 1, 0, 0, TimeSpan.Zero);
    private static int Build => AchievementDefinitionCatalog.Default.MetadataBuild;
    private sealed record ExpectedStage(int Id, long Target, AchievementProgressPresentation Presentation);
    private sealed record ExpectedBinding(int Id, string Signature, ExpectedStage[] Stages);
    private sealed record ExcludedBinding(int Id, int FirstStage);

    // Explicit public catalogue memberships, independent of source file locations or naming patterns.
    private static readonly ExpectedBinding[] Expected =
    [
        new(422100200, "32be3069487ef02751adb5cefe75f60758c49c4306ec1d39f8c2f4fefc6de5d4", [new(422100201, 1, AchievementProgressPresentation.Count), new(422100202, 2, AchievementProgressPresentation.Count), new(422100203, 3, AchievementProgressPresentation.Count)]),
        new(422100300, "ccfef648f7111a79fae8459f8604d9627c178ff0dd16723dbef6c129f233e2ce", [new(422100301, 1, AchievementProgressPresentation.Count), new(422100302, 2, AchievementProgressPresentation.Count), new(422100303, 3, AchievementProgressPresentation.Count)]),
        new(422100400, "c56b9dffe74b3ca9ff74ec5d6349baad84160e9a4ca42d03fa8da318a785d72a", [new(422100401, 1, AchievementProgressPresentation.Count), new(422100402, 2, AchievementProgressPresentation.Count), new(422100403, 3, AchievementProgressPresentation.Count)]),
        new(422100500, "c50c9aaa4bc4fe9f19231f063c5ab6e2c45f10a9d5d378b824c0f606a8946e89", [new(422100501, 1, AchievementProgressPresentation.Count), new(422100502, 2, AchievementProgressPresentation.Count), new(422100503, 3, AchievementProgressPresentation.Count)]),
        new(422100600, "6e8c681b9ca87c83090b9ae43187acdded485eaa016ed740f4ef3b95913a1e60", [new(422100601, 1, AchievementProgressPresentation.Count), new(422100602, 2, AchievementProgressPresentation.Count), new(422100603, 3, AchievementProgressPresentation.Count)]),
        new(422100700, "49650e5929f90924fb28d5c2ae7312f393162e6444195d60a18ddd491722bb8b", [new(422100701, 1, AchievementProgressPresentation.Count), new(422100702, 2, AchievementProgressPresentation.Count), new(422100703, 3, AchievementProgressPresentation.Count)]),
        new(422100800, "7d8c53b27665f849ff17908cd9d2c7b3f880823b2e2200ca4eb41d491913e29c", [new(422100801, 1, AchievementProgressPresentation.Count), new(422100802, 2, AchievementProgressPresentation.Count), new(422100803, 3, AchievementProgressPresentation.Count)]),
        new(422100900, "a97554533b7a3938dcb972e5afeac09e931846ba344383049a8af5705f324ce6", [new(422100901, 1, AchievementProgressPresentation.Count), new(422100902, 2, AchievementProgressPresentation.Count), new(422100903, 3, AchievementProgressPresentation.Count)]),
        new(422101000, "bb68efec2d438a9ef96ed7acf4e7e5d0d5f7311df8bc5efda453f8fe72657b48", [new(422101001, 1, AchievementProgressPresentation.Count), new(422101002, 2, AchievementProgressPresentation.Count), new(422101003, 3, AchievementProgressPresentation.Count)]),
        new(430100300, "ae6b676a1bfd3e735c722f43b76282713daff2966c8393dee605a15b226c98e6", [new(430100301, 1, AchievementProgressPresentation.Count), new(430100302, 2, AchievementProgressPresentation.Requirement)]),
        new(430100400, "98e308369e13be5d94d269e11408680f4433fcf929e32228863c299340d3c6de", [new(430100401, 1, AchievementProgressPresentation.Count), new(430100402, 2, AchievementProgressPresentation.Count), new(430100403, 3, AchievementProgressPresentation.Count)]),
        new(430100500, "30c366b6caefc0700b2ef80dd37b22c3b6de68903a7b084d92acfbd495dd8282", [new(430100501, 1, AchievementProgressPresentation.Count)]),
        new(430100600, "422570bc4ec5536350f86e0024d1e2995c9e8adb88fff388a34fd4f521879f55", [new(430100601, 1, AchievementProgressPresentation.Count)]),
        new(430100700, "da3468f0b92fab0dd877c003404161885644d01b5ebe8322a7d2fc171202c4a8", [new(430100701, 1, AchievementProgressPresentation.Count)]),
        new(430100800, "c7e88f11e04ec2fb2a7094d40f31ba5a3307aca6333bf92e5adb599726f757f1", [new(430100801, 1, AchievementProgressPresentation.Count)]),
        new(431100300, "6a2644e78f507d893adbb82858ac7faee1f0764ea6b59fc03cae3a49c463a536", [new(431100301, 1, AchievementProgressPresentation.Count), new(431100302, 2, AchievementProgressPresentation.Requirement)]),
        new(431100400, "d52b35fcac7a7824aa79eda21bf046dedb4b3bbd86801dc03aa5fcdfeccacfa4", [new(431100401, 1, AchievementProgressPresentation.Count), new(431100402, 2, AchievementProgressPresentation.Count), new(431100403, 3, AchievementProgressPresentation.Requirement)]),
        new(431100500, "7532ebc6b3564829dad4a6542b608b680536a50c2d5e7de7861d4b40ea09cec8", [new(431100501, 1, AchievementProgressPresentation.Count)]),
        new(431100600, "60c9326ae2c1278ddd4647070ca96ae0e6f510e0852b47c7717454b302d72524", [new(431100601, 1, AchievementProgressPresentation.Count)]),
        new(431100700, "af2bc52d25a62facc329f6b68f262ac625547673c002833d552cbe6cbf6108b2", [new(431100701, 1, AchievementProgressPresentation.Count)]),
        new(431100800, "aa5fe1b8c8584a4c78da49afc6c7cb5208a92eb97cb2dda3771459fcb53064a9", [new(431100801, 1, AchievementProgressPresentation.Count)]),
        new(432100300, "24f986d30d19a7afa4e347aa9b53f8145e9d352aca4f4a28afe80a1f60e7a181", [new(432100301, 1, AchievementProgressPresentation.Count), new(432100302, 2, AchievementProgressPresentation.Requirement)]),
        new(432100400, "2ee963cc58cea35f9f0cbc83e43af8f5d9d6fce0e550b9e28fbbcd160b91e530", [new(432100401, 1, AchievementProgressPresentation.Count), new(432100402, 2, AchievementProgressPresentation.Count), new(432100403, 3, AchievementProgressPresentation.Requirement)]),
        new(432100500, "9c671e2c4604b27027620c0516822e88134e79fb29760844b0d5e335a5eec752", [new(432100501, 1, AchievementProgressPresentation.Count)]),
        new(432100600, "3ba851e94f216d85e2357076a1c4a1bd5567ea190f58bcc15b20a387effdbd54", [new(432100601, 1, AchievementProgressPresentation.Count)]),
        new(432100700, "f4e31c52e3a4467db5900343bc8cc18293e2c21f467f4a0bbfe7f0b027b547e0", [new(432100701, 1, AchievementProgressPresentation.Count)]),
        new(432100800, "b2ed212ca8a9928e3e7a397cff390d82032b8631198f85e369938acef4689ade", [new(432100801, 1, AchievementProgressPresentation.Count)]),
        new(433100300, "ab5dc1f25c37f27f070cc040f75fe6277850ca83cb2c627217f289ebefe756f5", [new(433100301, 1, AchievementProgressPresentation.Count), new(433100302, 2, AchievementProgressPresentation.Requirement)]),
        new(433100400, "3fb41313a04db67c72ebf342d5fa85c4192ca6586fe95902d8b58669ea7d305a", [new(433100401, 1, AchievementProgressPresentation.Count), new(433100402, 2, AchievementProgressPresentation.Count), new(433100403, 3, AchievementProgressPresentation.Requirement)]),
        new(433100500, "d3a86a34c1b2a900a288c5bdb70f48ea610bb531c3efc57fc2e6865cd730388e", [new(433100501, 1, AchievementProgressPresentation.Count)]),
        new(433100600, "64311bed5d1f56419d9a2d899975bd4310674c6288f68d42d6c86651a5064c7e", [new(433100601, 1, AchievementProgressPresentation.Count)]),
        new(433100700, "bf8cf73de0417b24c1740a01d7770c09bf7ec48eba6a2a94a0cb45708c2006c7", [new(433100701, 1, AchievementProgressPresentation.Count)]),
        new(433100800, "918ce6e6c5c38deea1ef04c707bb3fa157cbf457a9aebcd68c2f2789ba8abe2c", [new(433100801, 1, AchievementProgressPresentation.Count)]),
        new(434100300, "0df85546aed6fc3af7de73b8a429e00e27f4341f8c01ebc0c7b1692ee4d51ccc", [new(434100301, 1, AchievementProgressPresentation.Count), new(434100302, 2, AchievementProgressPresentation.Requirement)]),
        new(434100400, "89b6ce1edd698dde5f364e78d0bdf09665f39e658081649dfc8d1843deb8ecdc", [new(434100401, 1, AchievementProgressPresentation.Count), new(434100402, 2, AchievementProgressPresentation.Count), new(434100403, 3, AchievementProgressPresentation.Requirement)]),
        new(434100500, "27b56b600c0197fc50359d435b9e2124da436467cb84a200c48c33ba8ac6ec03", [new(434100501, 1, AchievementProgressPresentation.Count)]),
        new(434100600, "c6d3bf3e32d34b7872d3375d6bcfe2c87eeaae61e683e4d649157ca91309b3f0", [new(434100601, 1, AchievementProgressPresentation.Count)]),
        new(434100700, "f9b2d4558a36a74fc9a27611b767b29ed9c7707e8b1fe28f90153319667dc157", [new(434100701, 1, AchievementProgressPresentation.Count)]),
        new(434100800, "b839753e03794f95942bc351d78e5ebb04891ab6465739e91683592aac1c8a55", [new(434100801, 1, AchievementProgressPresentation.Count)]),
        new(434101300, "a8fb0509548db288ad2e192bf7debc40fbca20f302dd3651233dbaf8503c9465", [new(434101301, 1, AchievementProgressPresentation.Count)]),
        new(435100300, "fff4e7680bb6c8198385d26e60e2fe4b8a138a4276b31fae268b3730852cbb8f", [new(435100301, 1, AchievementProgressPresentation.Count), new(435100302, 2, AchievementProgressPresentation.Requirement)]),
        new(435100400, "fee34a3a26fd15cd2798b4f8e28e9b8429fca77dafa1faab562bbf872f738b45", [new(435100401, 1, AchievementProgressPresentation.Count), new(435100402, 2, AchievementProgressPresentation.Count), new(435100403, 3, AchievementProgressPresentation.Requirement)]),
        new(435100500, "6ad50e5e33cb4895b7bb887d102c42408801eb55615afb7942dfe2b0c3f69bca", [new(435100501, 1, AchievementProgressPresentation.Count)]),
        new(435100600, "7456ffc20ee85658b07ad2982afb197696ca09361b1b51229c186954ad06356c", [new(435100601, 1, AchievementProgressPresentation.Count)]),
        new(435100700, "5edcee885f6563176b0857e9a23d6ea19c217523d8106e9b4117b9d95549131c", [new(435100701, 1, AchievementProgressPresentation.Count)]),
        new(435100800, "9cb6b067f8bb342092b7dbef2b5f39a360c4b4f8f15c94571802d554895161fb", [new(435100801, 1, AchievementProgressPresentation.Count)]),
        new(436100200, "98f101a675f6d35f47eaf5a5aca85023daa5c9c375521fec750cbbfe2c1eabb7", [new(436100201, 1, AchievementProgressPresentation.Count), new(436100202, 2, AchievementProgressPresentation.Count), new(436100203, 3, AchievementProgressPresentation.Count)]),
        new(436100300, "8661ac7f562a90bfb7ac67aacd405623c524fb59db7a22c17ec882e0fba0b28f", [new(436100301, 1, AchievementProgressPresentation.Count)]),
        new(436102100, "e657a7c5f2306fd3ce275f615afd759b013cd93b25fd1cbb36d57e6bd248035a", [new(436102101, 1, AchievementProgressPresentation.Count), new(436102102, 2, AchievementProgressPresentation.Count), new(436102103, 3, AchievementProgressPresentation.Count)]),
        new(436102200, "d3111b10a95df78358c564622f0af947d736744e8b3f122edadc35bbbd0dbde8", [new(436102201, 1, AchievementProgressPresentation.Count)]),
    ];
    private static readonly ExcludedBinding[] ExcludedElyos =
    [
        new(322100200, 322100201),
        new(322100300, 322100301),
        new(322100400, 322100401),
        new(322100500, 322100501),
        new(322100600, 322100601),
        new(322100700, 322100701),
        new(322100800, 322100801),
        new(322100900, 322100901),
        new(322101000, 322101001),
        new(330100300, 330100301),
        new(330100400, 330100401),
        new(330100500, 330100501),
        new(330100600, 330100601),
        new(330100700, 330100701),
        new(330100800, 330100801),
        new(331100300, 331100301),
        new(331100400, 331100401),
        new(331100500, 331100501),
        new(331100600, 331100601),
        new(331100700, 331100701),
        new(331100800, 331100801),
        new(332100300, 332100301),
        new(332100400, 332100401),
        new(332100500, 332100501),
        new(332100600, 332100601),
        new(332100700, 332100701),
        new(332100800, 332100801),
        new(333100300, 333100301),
        new(333100400, 333100401),
        new(333100500, 333100501),
        new(333100600, 333100601),
        new(333100700, 333100701),
        new(333100800, 333100801),
        new(334100300, 334100301),
        new(334100400, 334100401),
        new(334100500, 334100501),
        new(334100600, 334100601),
        new(334100700, 334100701),
        new(334100800, 334100801),
        new(334101300, 334101301),
        new(335100300, 335100301),
        new(335100400, 335100401),
        new(335100500, 335100501),
        new(335100600, 335100601),
        new(335100700, 335100701),
        new(335100800, 335100801),
        new(336100200, 336100201),
        new(336100300, 336100301),
        new(336102100, 336102101),
        new(336102200, 336102201),
    ];
    private static readonly int[] ReferenceIds = [422000100, 422000200, 422000300, 422000400, 422000500, 422000600, 422000700, 422000800, 422000900, 422001000, 422001100, 422001200, 422001300, 422001310, 422001400, 422001500, 422001600, 422001700, 422001800, 422001900, 422002000, 422002100, 422002200, 422002300, 422002400, 422002500, 422002600, 430000100, 430000200, 430000300, 430000400, 430000500, 431000100, 431000200, 431000300, 431000400, 431000500, 432000100, 432000200, 432000300, 432000400, 432000500, 433000100, 433000200, 433000300, 433000400, 433000500, 434000100, 434000200, 434000300, 434000400, 434000500, 435000100, 435000200, 435000300, 435000400, 435000500, 436000100, 436000200, 436000300, 436001600, 436001700, 436001800];

    public static IEnumerable<object[]> BindingCases() => Expected.Select(binding => new object[]
    {
        binding.Id, binding.Signature, binding.Stages.Select(stage => stage.Id).ToArray(),
        binding.Stages.Select(stage => stage.Target).ToArray(),
        binding.Stages.Where(stage => stage.Presentation == AchievementProgressPresentation.Requirement).Select(stage => stage.Id).ToArray()
    });
    public static IEnumerable<object[]> RequirementCases() => Expected.SelectMany(binding => binding.Stages
        .Where(stage => stage.Presentation == AchievementProgressPresentation.Requirement)
        .Select(stage => new object[] { binding.Id, stage.Id, checked((int)stage.Target) }));
    public static IEnumerable<object[]> SingleStageCases() => Expected.Where(binding => binding.Stages.Length == 1)
        .Select(binding => new object[] { binding.Id, binding.Stages[0].Id });
    public static IEnumerable<object[]> ElyosCases() => ExcludedElyos.Select(binding => new object[] { binding.Id, binding.FirstStage });

    [Fact]
    public void TheFiftyExplicitFamiliesHaveNinetyStagesAndExactlyElevenStageScopedRequirementFlags()
    {
        Assert.Equal(50, Expected.Length);
        Assert.Equal(90, Expected.Sum(binding => binding.Stages.Length));
        Assert.Equal(50, Expected.Select(binding => binding.Id).Distinct().Count());
        Assert.Equal(90, Expected.SelectMany(binding => binding.Stages).Select(stage => stage.Id).Distinct().Count());
        var thresholds = AchievementProgressThresholdCatalog.Default;
        var display = CombatAchievementDisplayCatalog.Default;
        var factions = CombatAchievementFactionCatalog.Default;
        var actual = thresholds.Families.Where(family => family.ModelCurrentState
            && factions.FindFamily(family.Id) == CombatAchievementFaction.Asmodian
            && display.Find(family.Id) is { } shown && (shown.CategoryId == "Dungeon" || shown.GroupId == 400000303)).ToArray();
        Assert.Equal(Expected.Select(binding => binding.Id).Order(), actual.Select(family => family.Id).Order());
        var flags = actual.SelectMany(family => family.Stages)
            .Where(stage => stage.Presentation == AchievementProgressPresentation.Requirement).Select(stage => stage.Id).Order();
        Assert.Equal(new[] { 430100302, 431100302, 431100403, 432100302, 432100403, 433100302,
            433100403, 434100302, 434100403, 435100302, 435100403 }, flags);
    }

    [Theory]
    [MemberData(nameof(BindingCases))]
    public void EveryBindingPreservesItsExactFingerprintCompleteMembershipAndNativeRawTargets(int id, string signature,
        int[] stageIds, long[] targets, int[] requirementStages)
    {
        var family = AchievementProgressThresholdCatalog.Default.FindFamily(id);
        Assert.NotNull(family);
        Assert.Equal(AchievementProgressCounterSemantics.CurrentState, family.CounterSemantics);
        Assert.Equal(signature, family.DefinitionSignature);
        Assert.Equal(stageIds, family.Stages.Select(stage => stage.Id));
        Assert.Equal(targets, family.Stages.Select(stage => stage.Target));
        var definition = AchievementDefinitionCatalog.Default.Find(id)!;
        Assert.Equal(AchievementArea.Normal, definition.Area);
        Assert.Equal(AchievementRole.PlayerFacing, definition.Role);
        Assert.Equal(signature, definition.Signature);
        Assert.Equal(stageIds, definition.StageIds);
        Assert.Equal(stageIds, CombatAchievementDisplayCatalog.Default.Find(id)!.Stages.Select(stage => stage.Id));
        Assert.Equal(CombatAchievementCounterRule.Unqualified,
            CombatAchievementProgressContractCatalog.Default.FindFamily(id)!.CounterRule);
        Assert.Equal(requirementStages, family.Stages.Where(stage => stage.Presentation == AchievementProgressPresentation.Requirement)
            .Select(stage => stage.Id));
        Assert.All(family.Stages.Where(stage => !requirementStages.Contains(stage.Id)),
            stage => Assert.Equal(AchievementProgressPresentation.Count, stage.Presentation));
    }

    [Theory]
    [MemberData(nameof(RequirementCases))]
    public void RequirementDisplayKeepsTheRawAggregateGoalAndNeverManufacturesAClaim(int id, int terminalStage, int rawGoal)
    {
        var raw = Raw((id, rawGoal, 0));
        var direct = new AchievementRuleEvaluator().Evaluate(raw, Build, 7, 11).Find(id)!;
        var legacy = CombatAchievementProgressProjection.Create(Snapshot(raw), Build).Find(id)!;
        foreach (var family in new[] { direct, legacy })
        {
            Assert.Equal(terminalStage, family.CurrentStageId);
            var stage = family.Find(terminalStage)!;
            Assert.Equal(rawGoal, stage.Current); Assert.Equal(rawGoal, stage.Target);
            Assert.Equal(1m, stage.DisplayCurrent); Assert.Equal(1m, stage.DisplayTarget);
            Assert.True(stage.RequirementsMetNow); Assert.True(stage.Completed);
            Assert.False(stage.Claimed);
            Assert.Equal(CombatAchievementProgressUnit.Count, stage.Unit);
            Assert.NotEqual(CombatKnowledgeStatus.Conflict, family.Status);
            Assert.All(family.Stages.Values, value => Assert.False(value.Claimed));
        }
        Assert.Equal(rawGoal, raw.Achievements[id].Count);
        Assert.Equal(0, raw.Achievements[id].RewardedStep);
        Assert.Equal(long.MaxValue, raw.Achievements[id].CompleteTime);
    }

    [Theory]
    [MemberData(nameof(RequirementCases))]
    public void RequirementDisplayBelowItsTargetStaysUnknownAndDoesNotAlterTheRawStageResolver(int id, int terminalStage, int rawGoal)
    {
        var evaluator = new AchievementRuleEvaluator();
        var progress = evaluator.Evaluate(Raw((id, rawGoal - 1, 0)), Build, 7, 11).Find(id)!;
        Assert.Equal(terminalStage, progress.CurrentStageId);
        var stage = progress.Find(terminalStage)!;
        Assert.Equal(rawGoal - 1, stage.Current); Assert.Equal(rawGoal, stage.Target);
        Assert.Equal(0m, stage.DisplayCurrent); Assert.Equal(1m, stage.DisplayTarget);
        Assert.False(stage.RequirementsMetNow); Assert.Null(stage.Completed); Assert.False(stage.Claimed);
        var zero = evaluator.Evaluate(Raw((id, 0, 0)), Build, 7, 12).Find(id)!;
        var first = AchievementProgressThresholdCatalog.Default.FindFamily(id)!.Stages[0];
        Assert.NotEqual(terminalStage, first.Id);
        Assert.Equal(first.Id, zero.CurrentStageId);
        Assert.Equal(1, zero.Find(first.Id)!.Target);
        Assert.Null(zero.Find(first.Id)!.DisplayCurrent); Assert.Null(zero.Find(first.Id)!.DisplayTarget);
    }

    [Fact]
    public void KraoConquestSpeedrunRetainsTheObservedThreeByThreeCountPresentation()
    {
        const int id = 430100400, stageId = 430100403;
        var contract = AchievementProgressThresholdCatalog.Default.FindFamily(id)!;
        Assert.All(contract.Stages, stage => Assert.Equal(AchievementProgressPresentation.Count, stage.Presentation));
        var family = new AchievementRuleEvaluator().Evaluate(Raw((id, 3, 3)), Build, 7, 11).Find(id)!;
        Assert.Equal(stageId, family.CurrentStageId);
        var stage = family.Find(stageId)!;
        Assert.Equal(3, stage.Current); Assert.Equal(3, stage.Target);
        Assert.Null(stage.DisplayCurrent); Assert.Null(stage.DisplayTarget);
        Assert.True(stage.Completed); Assert.True(stage.Claimed);
    }

    [Theory]
    [InlineData(436100200, 436100201, 436100203)]
    [InlineData(436102100, 436102101, 436102103)]
    public void LudraShowsItsFirstZeroByOneStageWithoutNormalizingUnobservedLaterTiers(int id, int firstStage, int lastStage)
    {
        var contract = AchievementProgressThresholdCatalog.Default.FindFamily(id)!;
        Assert.Equal(3, contract.Stages.Count);
        Assert.Equal(new long[] { 1, 2, 3 }, contract.Stages.Select(stage => stage.Target));
        Assert.All(contract.Stages, stage => Assert.Equal(AchievementProgressPresentation.Count, stage.Presentation));
        var evaluator = new AchievementRuleEvaluator();
        var current = evaluator.Evaluate(Raw((id, 0, 0)), Build, 7, 11).Find(id)!;
        Assert.Equal(firstStage, current.CurrentStageId);
        Assert.Equal(0, current.Find(firstStage)!.Current); Assert.Equal(1, current.Find(firstStage)!.Target);
        Assert.Null(current.Find(firstStage)!.Completed); Assert.False(current.Find(firstStage)!.Claimed);
        var later = evaluator.Evaluate(Raw((id, 3, 0)), Build, 7, 12).Find(id)!;
        Assert.Equal(lastStage, later.CurrentStageId);
        Assert.Equal(3, later.Find(lastStage)!.Current); Assert.Equal(3, later.Find(lastStage)!.Target);
        Assert.Null(later.Find(lastStage)!.DisplayCurrent); Assert.Null(later.Find(lastStage)!.DisplayTarget);
    }

    [Theory]
    [MemberData(nameof(SingleStageCases))]
    public void SingleGoalZeroIsAnUnknownCompletionAndClaimsRemainIndependentOfRequirements(int id, int stageId)
    {
        var evaluator = new AchievementRuleEvaluator();
        var zero = evaluator.Evaluate(Raw((id, 0, 0)), Build, 7, 11).Find(id)!.Find(stageId)!;
        Assert.Equal(0, zero.Current); Assert.Equal(1, zero.Target);
        Assert.False(zero.RequirementsMetNow); Assert.Null(zero.Completed); Assert.False(zero.Claimed);
        Assert.Null(zero.DisplayCurrent); Assert.Null(zero.DisplayTarget);
        var positive = evaluator.Evaluate(Raw((id, 1, 0)), Build, 7, 12).Find(id)!.Find(stageId)!;
        Assert.True(positive.RequirementsMetNow); Assert.True(positive.Completed); Assert.False(positive.Claimed);
        var claimed = evaluator.Evaluate(Raw((id, 0, 1)), Build, 7, 13).Find(id)!;
        Assert.NotEqual(CombatKnowledgeStatus.Conflict, claimed.Status);
        Assert.False(claimed.Find(stageId)!.RequirementsMetNow);
        Assert.True(claimed.Find(stageId)!.Completed); Assert.True(claimed.Find(stageId)!.Claimed);
    }

    [Theory]
    [InlineData(435100400, 435100403, 3)]
    [InlineData(435100500, 435100501, 1)]
    [InlineData(435100600, 435100601, 1)]
    [InlineData(434101300, 434101301, 1)]
    public void AnUnobservedFooterOrOpaqueClockCannotTurnAnUnclaimedPrefixIntoAClaim(int id, int stageId, int count)
    {
        var raw = Raw((id, count, 0));
        var evaluator = new AchievementRuleEvaluator();
        var stage = evaluator.Evaluate(raw, Build, 7, 11).Find(id)!.Find(stageId)!;
        Assert.True(stage.Completed);
        Assert.False(stage.Claimed);
        Assert.False(evaluator.ClaimFact(raw, id, stageId));
    }

    [Fact]
    public void InternalReferenceRowsNeverSupplyAMissingPlayerFacingFamilyOrClaim()
    {
        Assert.Equal(63, ReferenceIds.Length);
        var raw = Raw(ReferenceIds.Select(id => (id, 1, (byte)0)).ToArray());
        var direct = new AchievementRuleEvaluator().Evaluate(raw, Build, 7, 11);
        var legacy = CombatAchievementProgressProjection.Create(Snapshot(raw), Build);
        foreach (var binding in Expected)
        {
            Assert.Null(direct.Find(binding.Id));
            Assert.Null(legacy.Find(binding.Id));
        }
    }

    [Theory]
    [MemberData(nameof(ElyosCases))]
    public void UnmeasuredFactionCounterpartsDoNotBorrowTheseOwnNumericOrPresentationRules(int id, int firstStage)
    {
        Assert.Null(AchievementProgressThresholdCatalog.Default.FindFamily(id));
        var raw = Raw((id, 3, 0));
        var direct = new AchievementRuleEvaluator().Evaluate(raw, Build, 7, 11).Find(id)!;
        var legacy = CombatAchievementProgressProjection.Create(Snapshot(raw), Build).Find(id)!;
        foreach (var family in new[] { direct, legacy })
        {
            var stage = family.Find(firstStage)!;
            Assert.Null(stage.Current); Assert.Null(stage.Target);
            Assert.Null(stage.DisplayCurrent); Assert.Null(stage.DisplayTarget);
            Assert.Null(stage.Completed); Assert.False(stage.Claimed);
        }
    }

    [Fact]
    public void ExplicitHistoryMayRetainEarnedFactsButCannotInventAMissingCurrentCounter()
    {
        const int familyId = 435100500, stageId = 435100501;
        var evaluator = new AchievementRuleEvaluator();
        Assert.Null(evaluator.Evaluate(Raw(), Build, 7, 11).Find(familyId));
        var history = ImmutableHashSet.Create(stageId);
        var family = evaluator.Evaluate(Raw(), Build, 7, 12, earnedStageIds: history, knownClaimedStageIds: history).Find(familyId)!;
        var stage = family.Find(stageId)!;
        Assert.Null(stage.Current); Assert.Null(stage.Target);
        Assert.Null(stage.DisplayCurrent); Assert.Null(stage.DisplayTarget);
        Assert.True(stage.Completed); Assert.True(stage.Claimed);
    }

    [Theory]
    [InlineData("stale-character")]
    [InlineData("stale-domain")]
    [InlineData("unbound")]
    [InlineData("wrong-source")]
    [InlineData("missing-time")]
    [InlineData("unknown-generation")]
    [InlineData("conflict")]
    public void DisplayBindingsCannotBypassTheQualifiedOwnSourceBoundary(string variant)
    {
        var raw = Raw(Expected.Select(binding => (binding.Id, checked((int)binding.Stages[^1].Target), (byte)0)).ToArray());
        var snapshot = Snapshot(raw);
        snapshot = variant switch
        {
            "stale-character" => snapshot with { IsFresh = false },
            "stale-domain" => snapshot with { Achievements = snapshot.Achievements with { IsFresh = false, Status = NetworkCombatSourceStatus.Stale } },
            "unbound" => snapshot with { ProfileId = "" },
            "wrong-source" => snapshot with { Achievements = snapshot.Achievements with { Source = "synthetic-unqualified" } },
            "missing-time" => snapshot with { Achievements = snapshot.Achievements with { ObservedAt = null } },
            "unknown-generation" => snapshot with { Generation = 0 },
            "conflict" => snapshot with { Achievements = snapshot.Achievements with { Status = NetworkCombatSourceStatus.Conflict } },
            _ => throw new ArgumentOutOfRangeException(nameof(variant))
        };
        Assert.Empty(CombatAchievementProgressProjection.Create(snapshot, Build).Families);
    }

    private static CharacterAchievementState Raw(params (int Id, int Count, byte Prefix)[] rows) => new(
        rows.ToImmutableDictionary(row => row.Id, row => new CharacterAchievement(row.Count, long.MaxValue, row.Prefix)),
        ImmutableDictionary<int, ImmutableArray<bool>>.Empty);
    private static CharacterCombatSnapshot Snapshot(CharacterAchievementState raw) => new()
    {
        ProfileId = "SYNTHETIC-SCREENSHOT-ACHIEVEMENT-PROFILE", IsFresh = true, Generation = 7, Sequence = 11,
        Achievements = new(raw, NetworkCombatSourceStatus.Complete, true, At, 11, "own-achievement-list")
    };
}
