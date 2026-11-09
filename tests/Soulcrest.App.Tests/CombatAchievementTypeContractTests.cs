using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Soulcrest.App.Components;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Combat;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class CombatAchievementTypeContractTests
{
    [Fact]
    public void ContractsValidateExplicitNativeMembershipAndFreezeEveryCollection()
    {
        var catalog = Parse();
        Assert.Equal(42, catalog.MetadataBuild);
        var type = Assert.Single(catalog.Contracts);
        Assert.Equal(CombatAchievementCounterRule.Cumulative, type.CounterRule);
        Assert.Equal(CombatAchievementCompletionRule.AtLeastTarget, type.CompletionRule);
        Assert.Equal(CombatAchievementClaimRule.FirstRewardOrdinal, type.ClaimRule);
        Assert.Same(type, catalog.FindFamily(500)); Assert.Null(catalog.FindFamily(501));
        Assert.Equal(new[] { 1101, 1327, 1599 }, Assert.Single(type.Families).Stages.Select(stage => stage.Id));
        Assert.Throws<NotSupportedException>(() => ((IList<CombatAchievementTypeContract>)catalog.Contracts).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<CombatAchievementFamilyContract>)type.Families).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<CombatAchievementStageContract>)type.Families[0].Stages).Clear());
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("build")]
    [InlineData("counter")]
    [InlineData("counter-number")]
    [InlineData("unit")]
    [InlineData("completion")]
    [InlineData("claim")]
    [InlineData("extra")]
    [InlineData("id")]
    [InlineData("duplicate-type")]
    [InlineData("duplicate-family")]
    [InlineData("duplicate-family-across-types")]
    [InlineData("unknown-family")]
    [InlineData("wrong-stage-family")]
    [InlineData("unknown-stage")]
    [InlineData("duplicate-stage")]
    [InlineData("ordinal")]
    [InlineData("zero-target")]
    [InlineData("target-order")]
    [InlineData("missing-stages")]
    [InlineData("no-contracts")]
    public void InvalidVersionsTokensForeignKeysAndDuplicatesFailClosed(string variant)
    {
        var file = JsonNode.Parse(Document())!.AsObject();
        var type = file["contracts"]![0]!; var family = type["families"]![0]!; var stages = family["stages"]!.AsArray();
        switch (variant)
        {
            case "schema": file["schema"] = 1; break;
            case "build": file["metadataBuild"] = 43; break;
            case "counter": type["counterRule"] = "StageLocal"; break;
            case "counter-number": type["counterRule"] = "0"; break;
            case "unit": type["unit"] = "Seconds"; break;
            case "completion": type["completionRule"] = "CompleteTimePositive"; break;
            case "claim": type["claimRule"] = "RewardByteIsAlwaysOrdinal"; break;
            case "extra": type["owned"] = true; break;
            case "id": type["id"] = "bad id"; break;
            case "duplicate-type": file["contracts"]!.AsArray().Add(type.DeepClone()); break;
            case "duplicate-family": type["families"]!.AsArray().Add(family.DeepClone()); break;
            case "duplicate-family-across-types":
                var second = type.DeepClone(); second["id"] = "other-type"; file["contracts"]!.AsArray().Add(second); break;
            case "unknown-family": family["id"] = 9999; break;
            case "wrong-stage-family": stages[0]!["id"] = 2201; break;
            case "unknown-stage": stages[0]!["id"] = 9999; break;
            case "duplicate-stage": stages[1]!["id"] = 1101; break;
            case "ordinal": stages[0]!["ordinal"] = 2; break;
            case "zero-target": stages[0]!["target"] = 0; break;
            case "target-order": stages[1]!["target"] = 5; break;
            case "missing-stages": family["stages"] = null; break;
            case "no-contracts": file["contracts"] = new JsonArray(); break;
            default: throw new ArgumentOutOfRangeException(nameof(variant));
        }
        Assert.Throws<InvalidDataException>(() => CombatAchievementProgressContractCatalog.FromJson(file.ToJsonString(), Display()));
    }

    [Fact]
    public void KnownButUnlabelledNativeStageIdsAreValidForeignKeysAndInputBoundsAreEnforced()
    {
        var file = JsonNode.Parse(Document())!;
        file["contracts"]![0]!["families"]![0]!["stages"]![2]!["id"] = 1980;
        Assert.Equal(1980, Assert.Single(Parse(file.ToJsonString()).Contracts).Families[0].Stages[2].Id);
        Assert.Throws<InvalidDataException>(() => Parse(new string(' ', 16000001)));
        Assert.Throws<InvalidDataException>(() => CombatAchievementProgressContractCatalog.FromJson(null!, Display()));
        var many = JsonNode.Parse(Document())!; var template = many["contracts"]![0]!.DeepClone();
        many["contracts"] = new JsonArray(Enumerable.Range(0, 257).Select(index =>
        { var copy = template.DeepClone(); copy["id"] = "type-" + index; return copy; }).ToArray());
        Assert.Throws<InvalidDataException>(() => Parse(many.ToJsonString()));
    }

    [Theory]
    [InlineData("Count", CombatAchievementProgressUnit.Count)]
    [InlineData("Level", CombatAchievementProgressUnit.Level)]
    [InlineData("Minutes", CombatAchievementProgressUnit.Minutes)]
    public void QualifiedCounterAndCompletionUseDeclaredUnitsIndependentlyOfClaimScope(string unit,
        CombatAchievementProgressUnit expected)
    {
        var contracts = Parse(Document(unit, "Unqualified"));
        var result = CombatAchievementProgressProjection.Create(Snapshot(20, 255), 42, contracts);
        var family = Assert.IsType<CombatAchievementFamilyProgress>(result.Find(500));
        Assert.Equal(1599, family.CurrentStageId);
        Assert.All(family.Stages.Values, stage => { Assert.Equal(20, stage.Current); Assert.Equal(expected, stage.Unit); Assert.Null(stage.Claimed); });
        Assert.True(family.Find(1101)!.Completed); Assert.True(family.Find(1327)!.Completed); Assert.False(family.Find(1599)!.Completed);
    }

    [Fact]
    public void AQualifiedContiguousClaimRuleIsTypeScopedAndCannotLeakIntoFirstRewardContracts()
    {
        var first = CombatAchievementProgressProjection.Create(Snapshot(20, 2), 42, Parse());
        Assert.Equal(1599, first.Find(500)!.CurrentStageId); Assert.All(first.Find(500)!.Stages.Values, stage => Assert.Null(stage.Claimed));
        var contiguous = CombatAchievementProgressProjection.Create(Snapshot(20, 2), 42, Parse(ContiguousCounterDocument()));
        Assert.Equal(1599, contiguous.Find(500)!.CurrentStageId);
        Assert.True(contiguous.Find(500)!.Find(1101)!.Claimed); Assert.True(contiguous.Find(500)!.Find(1327)!.Claimed);
        Assert.False(contiguous.Find(500)!.Find(1599)!.Claimed);
        var conflict = CombatAchievementProgressProjection.Create(Snapshot(4, 1), 42, Parse()).Find(500)!;
        Assert.Equal(CombatKnowledgeStatus.Conflict, conflict.Status); Assert.Equal(1327, conflict.CurrentStageId);
        Assert.True(conflict.Find(1101)!.Claimed); Assert.Null(conflict.Find(1101)!.Completed);
    }

    [Fact]
    public void IndependentContractsCanApplyDifferentUnitsAndClaimRulesToTheirOwnFamilies()
    {
        var file = JsonNode.Parse(Document())!;
        var second = file["contracts"]![0]!.DeepClone(); second["id"] = "duration-type"; second["unit"] = "Minutes"; second["claimRule"] = "Unqualified";
        second["families"]![0]!["id"] = 600;
        var nativeIds = new[] { 2201, 2402, 2603 };
        for (var i = 0; i < nativeIds.Length; i++) second["families"]![0]!["stages"]![i]!["id"] = nativeIds[i];
        file["contracts"]!.AsArray().Add(second);
        var contracts = Parse(file.ToJsonString()); var snapshot = Snapshot(5, 1);
        snapshot = snapshot with { Achievements = snapshot.Achievements with { Value = snapshot.Achievements.Value! with
            { Achievements = snapshot.Achievements.Value!.Achievements.Add(600, new(125, long.MaxValue, 255)) } } };
        var projection = CombatAchievementProgressProjection.Create(snapshot, 42, contracts);
        Assert.Equal(2, projection.Families.Count);
        Assert.Equal(1327, projection.Find(500)!.CurrentStageId);
        Assert.Equal(CombatAchievementProgressUnit.Count, projection.Find(500)!.Find(1101)!.Unit);
        Assert.Equal(2603, projection.Find(600)!.CurrentStageId);
        Assert.Equal(125, projection.Find(600)!.Find(2201)!.Current);
        Assert.Equal(CombatAchievementProgressUnit.Minutes, projection.Find(600)!.Find(2201)!.Unit);
        Assert.All(projection.Find(600)!.Stages.Values, stage => Assert.Null(stage.Claimed));
    }

    [Fact]
    public void NewTypeContractsRetainSourceGapProfileVersionAndMissingRowGuards()
    {
        var contracts = Parse(Document("Minutes", "Unqualified")); var snapshot = Snapshot(0, 0);
        Assert.Equal(0, CombatAchievementProgressProjection.Create(snapshot, 42, contracts).Find(500)!.Find(1101)!.Current);
        foreach (var invalid in new[]
        {
            snapshot with { IsFresh = false }, snapshot with { ProfileId = null }, snapshot with { Generation = 0 },
            snapshot with { Achievements = snapshot.Achievements with { IsFresh = false } },
            snapshot with { Achievements = snapshot.Achievements with { Status = NetworkCombatSourceStatus.Conflict } },
            snapshot with { Achievements = snapshot.Achievements with { Source = "unqualified" } },
            snapshot with { Achievements = snapshot.Achievements with { ObservedAt = null } },
            snapshot with { Achievements = snapshot.Achievements with { Sequence = 0 } },
            Snapshot(-1, 0), snapshot with { Achievements = snapshot.Achievements with { Value = new(
                ImmutableDictionary<int, CharacterAchievement>.Empty, ImmutableDictionary<int, ImmutableArray<bool>>.Empty) } }
        }) Assert.Empty(CombatAchievementProgressProjection.Create(invalid, 42, contracts).Families);
        Assert.Empty(CombatAchievementProgressProjection.Create(snapshot, 43, contracts).Families);
    }

    [Fact]
    public void PositiveClaimPrefixSelectsTheExplicitFirstUnclaimedStageWithoutInterpretingCounter()
    {
        var projection = CombatAchievementProgressProjection.Create(Snapshot(-99, 2), 42, Parse(ClaimDocument()));
        var family = Assert.IsType<CombatAchievementFamilyProgress>(projection.Find(500));
        Assert.Equal(1599, family.CurrentStageId); Assert.Equal(CombatKnowledgeStatus.Partial, family.Status);
        foreach (var id in new[] { 1101, 1327 }) { Assert.True(family.Find(id)!.Completed); Assert.True(family.Find(id)!.Claimed); }
        foreach (var id in new[] { 1599, 1980 }) { Assert.Null(family.Find(id)!.Completed); Assert.False(family.Find(id)!.Claimed); }
        Assert.All(family.Stages.Values, stage => { Assert.Null(stage.Current); Assert.Null(stage.Target); Assert.Equal(CombatAchievementProgressUnit.Unknown, stage.Unit); });
    }

    [Theory]
    [InlineData(5)]
    [InlineData(255)]
    public void OutOfScopeClaimPrefixDoesNotCreateFacts(byte raw)
    {
        Assert.Empty(CombatAchievementProgressProjection.Create(Snapshot(123, raw), 42, Parse(ClaimDocument())).Families);
    }

    [Fact]
    public void ExplicitZeroPrefixSelectsTheFirstNativeStageWithoutInventingACounterGoalOrCompletion()
    {
        var family = CombatAchievementProgressProjection.Create(Snapshot(123, 0), 42, Parse(ClaimDocument())).Find(500)!;
        Assert.Equal(1101, family.CurrentStageId);
        Assert.All(family.Stages.Values, stage =>
        {
            Assert.Null(stage.Current); Assert.Null(stage.Target); Assert.Null(stage.Completed);
            Assert.False(stage.Claimed); Assert.Equal(CombatAchievementProgressUnit.Unknown, stage.Unit);
        });
    }

    [Fact]
    public void ClaimOnlyFirstRewardContractStopsAtItsIndependentlyQualifiedPrefix()
    {
        var contracts = Parse(ClaimDocument("FirstRewardOrdinal"));
        Assert.True(CombatAchievementProgressProjection.Create(Snapshot(-1, 1), 42, contracts).Find(500)!.Find(1101)!.Claimed);
        Assert.Empty(CombatAchievementProgressProjection.Create(Snapshot(123, 2), 42, contracts).Families);
    }

    [Theory]
    [InlineData("unknown-unit-counter")]
    [InlineData("counter-from-claim")]
    [InlineData("claim-only-count-unit")]
    [InlineData("claim-only-unqualified-claim")]
    [InlineData("claim-only-short-chain")]
    [InlineData("claim-only-counter-target")]
    [InlineData("contiguous-counter-short-chain")]
    public void IncompatibleRuleCombinationsAndTruncatedClaimChainsCannotQualifyFacts(string variant)
    {
        var file = JsonNode.Parse(variant.StartsWith("claim-only", StringComparison.Ordinal) ? ClaimDocument() : variant == "contiguous-counter-short-chain" ? ContiguousCounterDocument() : Document())!;
        var type = file["contracts"]![0]!;
        switch (variant)
        {
            case "unknown-unit-counter": type["unit"] = "Unknown"; break;
            case "counter-from-claim": type["completionRule"] = "FromClaim"; break;
            case "claim-only-count-unit": type["unit"] = "Count"; break;
            case "claim-only-unqualified-claim": type["claimRule"] = "Unqualified"; break;
            case "claim-only-short-chain":
            case "contiguous-counter-short-chain": type["families"]![0]!["stages"]!.AsArray().RemoveAt(3); break;
            case "claim-only-counter-target": type["families"]![0]!["stages"]![0]!["target"] = 5; break;
            default: throw new ArgumentOutOfRangeException(nameof(variant));
        }
        Assert.Throws<InvalidDataException>(() => Parse(file.ToJsonString()));
    }

    [Fact]
    public void AShortFirstRewardContractDoesNotPretendItsLastKnownStageIsTheWholeFamilyCurrentStage()
    {
        var file = JsonNode.Parse(Document())!; var stages = file["contracts"]![0]!["families"]![0]!["stages"]!.AsArray();
        stages.RemoveAt(2); stages.RemoveAt(1);
        var family = CombatAchievementProgressProjection.Create(Snapshot(5, 1), 42, Parse(file.ToJsonString())).Find(500)!;
        Assert.Null(family.CurrentStageId); Assert.True(family.Find(1101)!.Claimed);
    }

    private static string ContiguousCounterDocument()
    {
        var file = JsonNode.Parse(Document(claim: "ContiguousRewardOrdinal"))!;
        file["contracts"]![0]!["families"]![0]!["stages"]!.AsArray().Add(JsonSerializer.SerializeToNode(new { id = 1980, ordinal = 4, target = 100 }));
        return file.ToJsonString();
    }

    private static string ClaimDocument(string claim = "ContiguousRewardOrdinal")
    {
        var file = JsonNode.Parse(Document(claim: claim))!;
        var type = file["contracts"]![0]!;
        type["counterRule"] = "Unqualified"; type["completionRule"] = "FromClaim"; type["unit"] = "Unknown";
        var stages = type["families"]![0]!["stages"]!.AsArray();
        foreach (var stage in stages) stage!["target"] = null;
        stages.Add(JsonSerializer.SerializeToNode(new { id = 1980, ordinal = 4, target = (long?)null }));
        return file.ToJsonString();
    }

    private static CombatAchievementProgressContractCatalog Parse(string? json = null) =>
        CombatAchievementProgressContractCatalog.FromJson(json ?? Document(), Display());
    private static string Document(string unit = "Count", string claim = "FirstRewardOrdinal") => JsonSerializer.Serialize(new
    {
        schema = 2, metadataBuild = 42, contracts = new[] { new { id = "example-type", counterRule = "Cumulative", unit,
            completionRule = "AtLeastTarget", claimRule = claim, families = new[] { new { id = 500,
                stages = new[] { new { id = 1101, ordinal = 1, target = 5 }, new { id = 1327, ordinal = 2, target = 20 }, new { id = 1599, ordinal = 3, target = 50 } } } } } }
    });
    private static CombatAchievementDisplayCatalog Display()
    {
        CombatAchievementDisplayDefinition Definition(int id, int[] stages) => new(id, null, null, "Beispiel", "Example", "Bedingung", "Condition", null, null, [])
            { Stages = stages.Select(stage => new CombatAchievementDisplayStage(stage, "Stufe", "Stage", "Bedingung", "Condition", null, null, [])).ToArray(),
                UnknownStageIds = id == 500 ? [1980] : [] };
        return CombatAchievementDisplayCatalog.FromJson(JsonSerializer.Serialize(new
        {
            schema = 1, metadataBuild = 42, referenceOnly = true, doesNotEstablishCompletionOrClaim = true,
            categories = Array.Empty<CombatAchievementCategoryDefinition>(), groups = Array.Empty<CombatAchievementGroupDefinition>(),
            achievements = new[] { Definition(500, [1101, 1327, 1599]), Definition(600, [2201, 2402, 2603]) }
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }
    private static CharacterCombatSnapshot Snapshot(int count, byte rewarded) => new()
    {
        ProfileId = "SYNTHETIC-TYPE-PROFILE", IsFresh = true, Generation = 7, Sequence = 12,
        Achievements = new(new(ImmutableDictionary<int, CharacterAchievement>.Empty.Add(500, new(count, long.MaxValue, rewarded)),
            ImmutableDictionary<int, ImmutableArray<bool>>.Empty), NetworkCombatSourceStatus.Complete, true,
            new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero), 11, "own-achievement-list")
    };
}
