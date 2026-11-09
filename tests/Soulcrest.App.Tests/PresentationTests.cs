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
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Soulcrest.Core.Combat;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class PresentationTests
{
    private const int Build = 42, Family = 500, First = 1101, Middle = 1327, Last = 1599;
    private static readonly DateTimeOffset At = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OnlyTheExplicitStageUsesRequirementPresentationAndCountIsTheDefault()
    {
        var metadata = Metadata();
        var catalog = Parse(Document(), metadata);
        var family = catalog.FindFamily(Family)!;
        Assert.Equal(AchievementProgressCounterSemantics.CurrentState, family.CounterSemantics);
        Assert.Equal(new[] { First, Middle, Last }, family.Stages.Select(stage => stage.Id));
        Assert.Equal(new long[] { 10, 100, 1000 }, family.Stages.Select(stage => stage.Target));
        Assert.Equal(new[] { AchievementProgressPresentation.Count, AchievementProgressPresentation.Count,
            AchievementProgressPresentation.Requirement }, family.Stages.Select(stage => stage.Presentation));
        var document = Document();
        document["families"]![0]!.AsObject().Remove("counterSemantics");
        document["families"]![0]!["stages"]![2]!.AsObject().Remove("presentation");
        var cumulative = Parse(document, metadata).FindFamily(Family)!;
        Assert.Equal(AchievementProgressCounterSemantics.Cumulative, cumulative.CounterSemantics);
        Assert.All(cumulative.Stages, stage => Assert.Equal(AchievementProgressPresentation.Count, stage.Presentation));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("unknown")]
    [InlineData("lowercase")]
    [InlineData("number")]
    [InlineData("boolean")]
    [InlineData("cumulative")]
    [InlineData("omitted-semantics")]
    [InlineData("family-presentation")]
    [InlineData("build")]
    [InlineData("signature")]
    [InlineData("missing-stage")]
    [InlineData("reordered-stage")]
    public void InvalidOrUnboundPresentationNeverEntersTheThresholdCatalog(string variant)
    {
        var document = Document(); var family = document["families"]![0]!;
        var stage = family["stages"]![2]!;
        switch (variant)
        {
            case "null": stage["presentation"] = null; break;
            case "unknown": stage["presentation"] = "Boolean"; break;
            case "lowercase": stage["presentation"] = "requirement"; break;
            case "number": stage["presentation"] = 1; break;
            case "boolean": stage["presentation"] = true; break;
            case "cumulative": family["counterSemantics"] = "Cumulative"; break;
            case "omitted-semantics": family.AsObject().Remove("counterSemantics"); break;
            case "family-presentation": family["presentation"] = "Requirement"; break;
            case "build": document["metadataBuild"] = Build + 1; break;
            case "signature": family["definitionSignature"] = new string('b', 64); break;
            case "missing-stage": family["stages"]!.AsArray().RemoveAt(0); break;
            case "reordered-stage": family["stages"]![0]!["id"] = Middle; family["stages"]![1]!["id"] = First; break;
            default: throw new ArgumentOutOfRangeException(nameof(variant));
        }
        Assert.Throws<InvalidDataException>(() => Parse(document, Metadata()));
    }

    [Theory]
    [InlineData(750, 0)]
    [InlineData(1000, 1)]
    [InlineData(1500, 1)]
    public void RequirementChangesOnlyThePresentationAndNeverTheNativeStageOrFacts(int count, int display)
    {
        var metadata = Metadata(); var raw = Raw(count, 0);
        var requirement = Evaluator(metadata).Evaluate(raw, Build, 1, 1).Find(Family)!;
        var countDocument = Document(); countDocument["families"]![0]!["stages"]![2]!.AsObject().Remove("presentation");
        var numeric = Evaluator(metadata, countDocument).Evaluate(raw, Build, 1, 1).Find(Family)!;
        Assert.Equal(numeric.CurrentStageId, requirement.CurrentStageId);
        Assert.Equal(Last, requirement.CurrentStageId);
        Assert.Equal(numeric.Status, requirement.Status);
        foreach (var id in new[] { First, Middle, Last })
        {
            var before = numeric.Find(id)!; var after = requirement.Find(id)!;
            Assert.Equal(before.Current, after.Current); Assert.Equal(before.Target, after.Target);
            Assert.Equal(before.Completed, after.Completed); Assert.Equal(before.Claimed, after.Claimed);
            Assert.Equal(before.RequirementsMetNow, after.RequirementsMetNow);
            Assert.Equal(CombatAchievementProgressUnit.Count, after.Unit);
            if (id != Last) { Assert.Null(after.DisplayCurrent); Assert.Null(after.DisplayTarget); }
        }
        Assert.Equal(count, requirement.Find(Last)!.Current); Assert.Equal(1000, requirement.Find(Last)!.Target);
        Assert.Equal((decimal)display, requirement.Find(Last)!.DisplayCurrent);
        Assert.Equal(1m, requirement.Find(Last)!.DisplayTarget);
        Assert.False(requirement.Find(Last)!.Claimed);
        Assert.Equal(count, raw.Achievements[Family].Count); Assert.Equal(0, raw.Achievements[Family].RewardedStep);
        Assert.Equal(long.MaxValue, raw.Achievements[Family].CompleteTime);
    }

    [Fact]
    public void ADecreaseCanDisplayZeroWhileTheEarnedAndClaimedFactsRemainTrue()
    {
        var evaluator = Evaluator(Metadata());
        var before = evaluator.Evaluate(Raw(1000, 3), Build, 1, 1).Find(Family)!;
        var after = evaluator.Evaluate(Raw(750, 0), Build, 1, 2,
            earnedStageIds: ImmutableHashSet.Create(Last), knownClaimedStageIds: ImmutableHashSet.Create(Last)).Find(Family)!;
        Assert.True(before.Find(Last)!.RequirementsMetNow);
        var stage = after.Find(Last)!;
        Assert.Equal(Last, after.CurrentStageId); Assert.NotEqual(CombatKnowledgeStatus.Conflict, after.Status);
        Assert.False(stage.RequirementsMetNow); Assert.True(stage.Completed); Assert.True(stage.Claimed);
        Assert.Equal(750, stage.Current); Assert.Equal(1000, stage.Target);
        Assert.Equal(0m, stage.DisplayCurrent); Assert.Equal(1m, stage.DisplayTarget);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrNegativeCountersHaveNoDisplayPairAndCannotEraseAnIndependentClaim(bool missing)
    {
        var evaluator = Evaluator(Metadata());
        var raw = missing ? new CharacterAchievementState(ImmutableDictionary<int, CharacterAchievement>.Empty,
            ImmutableDictionary<int, ImmutableArray<bool>>.Empty) : Raw(-17, 0);
        var family = evaluator.Evaluate(raw, Build, 1, 1, knownClaimedStageIds: ImmutableHashSet.Create(Last)).Find(Family)!;
        var stage = family.Find(Last)!;
        Assert.Null(stage.Current); Assert.Null(stage.Target); Assert.Null(stage.DisplayCurrent); Assert.Null(stage.DisplayTarget);
        Assert.True(stage.Completed); Assert.True(stage.Claimed); Assert.Null(stage.RequirementsMetNow);
        Assert.Empty(evaluator.Evaluate(raw, Build + 1, 1, 1).Families);
    }

    [Fact]
    public void ADefinitionMismatchCannotGrantTheRequirementDisplayThroughALoadedCatalog()
    {
        var metadata = Metadata(); var thresholds = Parse(Document(), metadata);
        var definitions = DefinitionDocument(); definitions["definitions"]![0]!["signature"] = new string('b', 64);
        var evaluator = new AchievementRuleEvaluator(metadata.Contracts,
            definitions: AchievementDefinitionCatalog.FromJson(definitions.ToJsonString()), thresholds: thresholds);
        var family = evaluator.Evaluate(Raw(750, 0), Build, 1, 1).Find(Family)!;
        Assert.All(family.Stages.Values, stage => { Assert.Null(stage.Current); Assert.Null(stage.Target);
            Assert.Null(stage.DisplayCurrent); Assert.Null(stage.DisplayTarget); });
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0, null)]
    [InlineData(null, 1)]
    [InlineData(-1, 1)]
    [InlineData(0, 0)]
    [InlineData(0, -1)]
    public async Task IncompleteOrInvalidDisplayPairsFallBackTogetherInBrowserAndOverlay(int? current, int? target)
    {
        var row = Tracker(current, target);
        Assert.False(row.HasValidDisplayPair); Assert.Equal(750m, row.PresentationCurrent); Assert.Equal(1000m, row.PresentationTarget);
        Assert.Equal(.75, row.ProgressFraction); Assert.Equal("750 / 1000", AchievementOverlayRenderer.Counter(row, "en"));
        var html = await RenderBrowser(current, target);
        Assert.Contains("aria-valuenow=\"750\"", html); Assert.Contains("aria-valuemax=\"1000\"", html);
        Assert.Contains("width:75%", html); Assert.Contains(">750</strong>", html);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task BooleanPresentationDrivesTextBarAndAriaWhileRawAttributesRemainIntact(string language)
    {
        var html = await RenderBrowser(0, 1, language);
        Assert.Contains("data-achievement-current-value=\"750\"", html);
        Assert.Contains("data-achievement-target=\"1000\"", html);
        Assert.Contains(">0</strong>", html); Assert.Contains("aria-valuenow=\"0\"", html);
        Assert.Contains("aria-valuemax=\"1\"", html); Assert.Contains("width:0%", html);
        Assert.DoesNotContain("width:75%", html);
        var row = Tracker(0, 1);
        Assert.Equal(0, row.ProgressFraction); Assert.Equal("0 / 1", AchievementOverlayRenderer.Counter(row, language));
        Assert.Equal(750, row.Current); Assert.Equal(1000, row.Target);
    }

    [Fact]
    public void DisplayValuesCannotSupplyAMissingOrNegativeRawGoal()
    {
        foreach (var row in new[] { Tracker(1, 1) with { Current = null }, Tracker(1, 1) with { Current = -1 },
            Tracker(1, 1) with { Target = null }, Tracker(1, 1) with { Target = 0 } })
        {
            Assert.False(row.HasQualifiedGoal); Assert.Null(row.PresentationCurrent); Assert.Null(row.PresentationTarget);
            Assert.Null(row.ProgressFraction); Assert.Equal("Progress unknown", AchievementOverlayRenderer.Counter(row, "en"));
        }
    }

    [Theory]
    [InlineData("en", 6000, 6000, "100 h / 100 h")]
    [InlineData("de", 6000, 6000, "100 Std. / 100 Std.")]
    [InlineData("en", 10, 30, "10 min / 30 min")]
    [InlineData("de", 10, 30, "10 Min. / 30 Min.")]
    public void ExistingMinuteUnitsUseTheSameLosslessDisplayAsTheBrowser(string language, int current, int target, string expected)
    {
        var row = new AchievementTrackerRow { Current = current, Target = target, Unit = CombatAchievementProgressUnit.Minutes };
        Assert.Equal(expected, AchievementOverlayRenderer.Counter(row, language));
        Assert.Equal(CombatAchievementsPresentation.ProgressValue(current, row.Unit, language) + " / "
            + CombatAchievementsPresentation.ProgressValue(target, row.Unit, language), AchievementOverlayRenderer.Counter(row, language));
        Assert.Equal(current, row.Current); Assert.Equal(target, row.Target); Assert.Equal(CombatAchievementProgressUnit.Minutes, row.Unit);
    }

    private static AchievementTrackerRow Tracker(int? current, int? target) => new()
    {
        Current = 750, Target = 1000, DisplayCurrent = current, DisplayTarget = target, Unit = CombatAchievementProgressUnit.Count,
    };

    private static async Task<string> RenderBrowser(int? current, int? target, string language = "en")
    {
        var previous = UiText.Language; UiText.Language = language;
        try
        {
            var metadata = Metadata();
            var facts = new CombatAchievementStageProgress(Last, 750, 1000, null, false)
            { DisplayCurrent = current, DisplayTarget = target, Unit = CombatAchievementProgressUnit.Count };
            var state = new AchievementViewState
            {
                ProfileId = "SYNTHETIC-PRESENTATION", Source = AchievementViewSource.Live, MetadataBuild = Build,
                Generation = 1, Sequence = 1, ObservedAt = At,
                Progress = new(1, 1, ImmutableDictionary<int, CombatAchievementFamilyProgress>.Empty.Add(Family,
                    new(Family, Last, ImmutableDictionary<int, CombatAchievementStageProgress>.Empty.Add(Last, facts)))),
                ObservedFamilyIds = ImmutableHashSet.Create(Family), ObservedValues = ImmutableDictionary<int, int>.Empty.Add(Family, 750),
            };
            var services = new ServiceCollection(); services.AddLogging();
            await using var provider = services.BuildServiceProvider();
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            return await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var root = await renderer.RenderComponentAsync<CombatAchievementsView>(ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(CombatAchievementsView.Snapshot)] = new CharacterCombatSnapshot { ProfileId = state.ProfileId, IsFresh = true, Generation = 1 },
                    [nameof(CombatAchievementsView.Catalog)] = metadata.Display,
                    [nameof(CombatAchievementsView.Definitions)] = metadata.Definitions,
                    [nameof(CombatAchievementsView.AchievementState)] = state,
                }));
                var html = WebUtility.HtmlDecode(root.ToHtmlString());
                var card = Regex.Match(html, "<article\\b[^>]*data-achievement-card=\"500\"[^>]*>.*?</article>", RegexOptions.Singleline).Value;
                Assert.NotEmpty(card); return card;
            });
        }
        finally { UiText.Language = previous; }
    }

    private static CharacterAchievementState Raw(int count, byte prefix) => new(
        ImmutableDictionary<int, CharacterAchievement>.Empty.Add(Family, new(count, long.MaxValue, prefix)),
        ImmutableDictionary<int, ImmutableArray<bool>>.Empty);

    private static AchievementProgressThresholdCatalog Parse(JsonObject document,
        (AchievementDefinitionCatalog Definitions, CombatAchievementDisplayCatalog Display, CombatAchievementProgressContractCatalog Contracts) metadata)
        => AchievementProgressThresholdCatalog.FromJson(document.ToJsonString(), metadata.Definitions, metadata.Display);

    private static AchievementRuleEvaluator Evaluator(
        (AchievementDefinitionCatalog Definitions, CombatAchievementDisplayCatalog Display, CombatAchievementProgressContractCatalog Contracts) metadata,
        JsonObject? document = null) => new(metadata.Contracts, definitions: metadata.Definitions, thresholds: Parse(document ?? Document(), metadata));

    private static JsonObject Document() => JsonNode.Parse(JsonSerializer.Serialize(new
    {
        schema = 1, metadataBuild = Build, families = new[] { new { id = Family, definitionSignature = new string('a', 64),
            counterSemantics = "CurrentState", stages = new object[] { new { id = First, target = 10L },
                new { id = Middle, target = 100L, presentation = "Count" }, new { id = Last, target = 1000L, presentation = "Requirement" } } } },
    }))!.AsObject();

    private static JsonObject DefinitionDocument() => JsonNode.Parse(JsonSerializer.Serialize(new
    {
        schema = 1, metadataBuild = Build, definitions = new[] { new { id = Family, area = "Normal", role = "PlayerFacing", semanticGroup = "synthetic",
            resetPolicy = "None", signature = new string('a', 64), stageIds = new[] { First, Middle, Last }, reason = "synthetic" } },
    }))!.AsObject();

    private static (AchievementDefinitionCatalog Definitions, CombatAchievementDisplayCatalog Display, CombatAchievementProgressContractCatalog Contracts) Metadata()
    {
        var display = CombatAchievementDisplayCatalog.FromJson(JsonSerializer.Serialize(new
        {
            schema = 1, metadataBuild = Build, referenceOnly = true, doesNotEstablishCompletionOrClaim = true,
            categories = Array.Empty<object>(), groups = Array.Empty<object>(), achievements = new[] { new
            {
                id = Family, nameDe = "Synthetischer Erfolg", nameEn = "Synthetic achievement", conditionDe = "Bedingung", conditionEn = "Condition",
                rewards = Array.Empty<object>(), stages = new[] { First, Middle, Last }.Select(id => new { id,
                    nameDe = "Synthetische Stufe", nameEn = "Synthetic stage", conditionDe = "Bedingung", conditionEn = "Condition", rewards = Array.Empty<object>() }).ToArray(),
            } },
        }));
        var contracts = CombatAchievementProgressContractCatalog.FromJson(JsonSerializer.Serialize(new
        {
            schema = 2, metadataBuild = Build, contracts = new[] { new { id = "synthetic", counterRule = "Unqualified", unit = "Unknown",
                completionRule = "FromClaim", claimRule = "ContiguousRewardOrdinal", families = new[] { new { id = Family,
                    stages = new[] { First, Middle, Last }.Select((id,index) => new { id, ordinal = index + 1, target = (long?)null }).ToArray() } } } },
        }), display);
        return (AchievementDefinitionCatalog.FromJson(DefinitionDocument().ToJsonString()), display, contracts);
    }
}
