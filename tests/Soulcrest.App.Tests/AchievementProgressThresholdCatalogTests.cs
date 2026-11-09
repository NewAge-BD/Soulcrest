using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Soulcrest.App.Components;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class AchievementProgressThresholdCatalogTests
{
    [Fact]
    public void ProductThresholdsCoverOnlyCompleteNormalChainsAndDoNotReplaceMeasuredUnits()
    {
        var thresholds = AchievementProgressThresholdCatalog.Default;
        var definitions = AchievementDefinitionCatalog.Default;
        var measured = CombatAchievementProgressContractCatalog.Default;
        Assert.Equal(definitions.MetadataBuild, thresholds.MetadataBuild);
        Assert.Equal(496, thresholds.Families.Count);
        Assert.Equal(2048, thresholds.Families.Sum(family => family.Stages.Count));
        Assert.Equal(330, thresholds.Families.Count(family => !family.ModelCurrentState));
        var bindings = definitions.Definitions.Where(definition => definition.Area == AchievementArea.Normal
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
            thresholds.Families.Where(family => family.ModelCurrentState).Select(family => family.Id).Order());
        Assert.Null(thresholds.FindFamily(321106200));
        foreach (var family in thresholds.Families)
        {
            var definition = definitions.Find(family.Id)!;
            Assert.Equal(AchievementRole.PlayerFacing, definition.Role);
            Assert.Equal(AchievementArea.Normal, definition.Area);
            Assert.Equal(definition.Signature, family.DefinitionSignature);
            Assert.Equal(definition.StageIds, family.Stages.Select(stage => stage.Id));
            Assert.Equal(CombatAchievementCounterRule.Unqualified, measured.FindFamily(family.Id)!.CounterRule);
        }
    }

    [Fact]
    public void ExplicitNonconsecutiveMembershipAndThresholdCollectionsAreImmutable()
    {
        var catalog = Parse(Document());
        var family = Assert.Single(catalog.Families);
        Assert.Equal(new[] { 1101, 1327, 1599 }, family.Stages.Select(stage => stage.Id));
        Assert.Equal(new long[] { 10, 100, 1000 }, family.Stages.Select(stage => stage.Target));
        Assert.Same(family, catalog.FindFamily(500)); Assert.Null(catalog.FindFamily(501));
        Assert.Throws<NotSupportedException>(() => ((IList<AchievementProgressThresholdFamily>)catalog.Families).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<AchievementProgressThresholdStage>)family.Stages).Clear());
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("build")]
    [InlineData("empty")]
    [InlineData("duplicate-family")]
    [InlineData("unknown-family")]
    [InlineData("signature")]
    [InlineData("missing-stage")]
    [InlineData("extra-stage")]
    [InlineData("stage-order")]
    [InlineData("duplicate-stage")]
    [InlineData("zero-target")]
    [InlineData("negative-target")]
    [InlineData("equal-targets")]
    [InlineData("descending-targets")]
    [InlineData("null-family")]
    [InlineData("null-stages")]
    [InlineData("null-stage")]
    [InlineData("extra-field")]
    public void IncompatibleOrIncompleteMetadataNeverSuppliesProgressGoals(string variant)
    {
        var file = JsonNode.Parse(Document())!.AsObject();
        var families = file["families"]!.AsArray(); var family = families[0]!.AsObject();
        var stages = family["stages"]!.AsArray();
        switch (variant)
        {
            case "schema": file["schema"] = 2; break;
            case "build": file["metadataBuild"] = 43; break;
            case "empty": families.Clear(); break;
            case "duplicate-family": families.Add(family.DeepClone()); break;
            case "unknown-family": family["id"] = 501; break;
            case "signature": family["definitionSignature"] = new string('b', 64); break;
            case "missing-stage": stages.RemoveAt(2); break;
            case "extra-stage": stages.Add(new JsonObject { ["id"] = 1777, ["target"] = 2000 }); break;
            case "stage-order": stages[0]!["id"] = 1327; stages[1]!["id"] = 1101; break;
            case "duplicate-stage": stages[1]!["id"] = 1101; break;
            case "zero-target": stages[0]!["target"] = 0; break;
            case "negative-target": stages[0]!["target"] = -1; break;
            case "equal-targets": stages[1]!["target"] = 10; break;
            case "descending-targets": stages[1]!["target"] = 9; break;
            case "null-family": families[0] = null; break;
            case "null-stages": family["stages"] = null; break;
            case "null-stage": stages[0] = null; break;
            case "extra-field": family["unit"] = "guessed"; break;
        }
        Assert.Throws<InvalidDataException>(() => Parse(file.ToJsonString()));
    }

    [Fact]
    public void MatchingThresholdsUseRawCountAndKeepRewardClaimsIndependent()
    {
        var (definitions, display, contracts) = Metadata();
        var thresholds = AchievementProgressThresholdCatalog.FromJson(Document(), definitions, display);
        var evaluator = new AchievementRuleEvaluator(contracts, definitions: definitions, thresholds: thresholds);
        var family = evaluator.Evaluate(Raw(10, 0), 42, 1, 1).Find(500)!;
        Assert.Equal(1327, family.CurrentStageId);
        Assert.Equal(10, family.Find(1327)!.Current); Assert.Equal(100, family.Find(1327)!.Target);
        Assert.Equal(CombatAchievementProgressUnit.Count, family.Find(1327)!.Unit);
        Assert.True(family.Find(1101)!.Completed); Assert.False(family.Find(1327)!.Completed);
        Assert.All(family.Stages.Values, stage => Assert.False(stage.Claimed));
        Assert.Equal(1599, evaluator.Evaluate(Raw(750, 0), 42, 1, 2).Find(500)!.CurrentStageId);
        Assert.Equal(1599, evaluator.Evaluate(Raw(750, 1), 42, 1, 3).Find(500)!.CurrentStageId);
        Assert.Equal(1599, evaluator.Evaluate(Raw(750, 2), 42, 1, 4).Find(500)!.CurrentStageId);
    }

    [Fact]
    public void EvaluatorRechecksTheDefinitionFingerprintAndPreservesRawClaims()
    {
        var (definitions, display, contracts) = Metadata();
        var thresholds = AchievementProgressThresholdCatalog.FromJson(Document(), definitions, display);
        var changed = JsonNode.Parse(DefinitionDocument())!; changed["definitions"]![0]!["signature"] = new string('b', 64);
        var evaluator = new AchievementRuleEvaluator(contracts, definitions: AchievementDefinitionCatalog.FromJson(changed.ToJsonString()), thresholds: thresholds);
        var family = evaluator.Evaluate(Raw(750, 0), 42, 1, 1).Find(500)!;
        Assert.Equal(1101, family.CurrentStageId); Assert.Null(family.Find(1101)!.Current); Assert.Null(family.Find(1101)!.Target);
        Assert.Null(family.Find(1101)!.Completed); Assert.False(family.Find(1101)!.Claimed);
    }

    [Fact]
    public void NegativeRawValuesUseOnlyTheKnownClaimFallback()
    {
        var (definitions, display, contracts) = Metadata();
        var evaluator = new AchievementRuleEvaluator(contracts, definitions: definitions,
            thresholds: AchievementProgressThresholdCatalog.FromJson(Document(), definitions, display));
        var raw = Raw(-17, 1);
        var family = evaluator.Evaluate(raw, 42, 1, 1).Find(500)!;
        Assert.Equal(1327, family.CurrentStageId);
        Assert.Null(family.Find(1327)!.Current); Assert.Null(family.Find(1327)!.Target); Assert.Null(family.Find(1327)!.Completed);
        Assert.True(family.Find(1101)!.Claimed); Assert.Equal(-17, raw.Achievements[500].Count);
    }

    private static CharacterAchievementState Raw(int count, byte prefix) => new(
        ImmutableDictionary<int, CharacterAchievement>.Empty.Add(500, new(count, 0, prefix)),
        ImmutableDictionary<int, ImmutableArray<bool>>.Empty);
    private static AchievementProgressThresholdCatalog Parse(string document)
    {
        var (definitions, display, _) = Metadata();
        return AchievementProgressThresholdCatalog.FromJson(document, definitions, display);
    }
    private static string Document() => JsonSerializer.Serialize(new
    {
        schema = 1, metadataBuild = 42, families = new[] { new { id = 500, definitionSignature = new string('a', 64),
            stages = new[] { new { id = 1101, target = 10L }, new { id = 1327, target = 100L }, new { id = 1599, target = 1000L } } } },
    });
    private static string DefinitionDocument() => JsonSerializer.Serialize(new
    {
        schema = 1, metadataBuild = 42, definitions = new[] { new { id = 500, area = "Normal", role = "PlayerFacing", semanticGroup = "synthetic",
            resetPolicy = "None", signature = new string('a', 64), stageIds = new[] { 1101, 1327, 1599 }, reason = "synthetic" } },
    });
    private static (AchievementDefinitionCatalog Definitions, CombatAchievementDisplayCatalog Display,
        CombatAchievementProgressContractCatalog Contracts) Metadata()
    {
        var display = CombatAchievementDisplayCatalog.FromJson(JsonSerializer.Serialize(new
        {
            schema = 1, metadataBuild = 42, referenceOnly = true, doesNotEstablishCompletionOrClaim = true,
            categories = Array.Empty<object>(), groups = Array.Empty<object>(), achievements = new[] { new
            {
                id = 500, nameDe = "Synthetischer Erfolg", nameEn = "Synthetic achievement", conditionDe = "Bedingung", conditionEn = "Condition",
                rewards = Array.Empty<object>(), stages = new[] { 1101, 1327, 1599 }.Select(id => new { id,
                    nameDe = "Synthetische Stufe", nameEn = "Synthetic stage", conditionDe = "Bedingung", conditionEn = "Condition", rewards = Array.Empty<object>() }).ToArray(),
            } },
        }));
        var contracts = CombatAchievementProgressContractCatalog.FromJson(JsonSerializer.Serialize(new
        {
            schema = 2, metadataBuild = 42, contracts = new[] { new { id = "synthetic", counterRule = "Unqualified", unit = "Unknown",
                completionRule = "FromClaim", claimRule = "ContiguousRewardOrdinal", families = new[] { new { id = 500,
                    stages = new[] { 1101, 1327, 1599 }.Select(id => new { id, ordinal = id == 1101 ? 1 : id == 1327 ? 2 : 3, target = (long?)null }).ToArray() } } } },
        }), display);
        return (AchievementDefinitionCatalog.FromJson(DefinitionDocument()), display, contracts);
    }
}
