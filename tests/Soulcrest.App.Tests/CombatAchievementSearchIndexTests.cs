using System.Text.Json;
using Soulcrest.App.Components;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class CombatAchievementSearchIndexTests
{
    [Theory]
    [InlineData("SKY runner")]
    [InlineData("Himmelssturmer")]
    [InlineData("  sky   runner  ")]
    [InlineData("Kinah 100")]
    [InlineData("Bound Crystal")]
    [InlineData("Kristall 20")]
    [InlineData("Flugel Konigs")]
    [InlineData("King's Wing")]
    public void NamesRewardsAndExactTitleRewardsAreSearchableInBothLanguages(string query)
    {
        var index = Create();
        Assert.True(index.Matches(10, query));
        Assert.False(index.Matches(20, query));
    }

    [Fact]
    public void TokensMustMatchTheSameStageAndEmptyQueryDoesNotRestrictResults()
    {
        var index = Create();
        Assert.True(index.Matches(10, "")); Assert.True(index.Matches(20, "   "));
        Assert.False(index.Matches(10, "Kinah Crystal"));
        Assert.False(index.Matches(10, "Missing title")); // Quest reference is not an achievement reward.
        Assert.False(index.Matches(999, "runner"));
    }

    [Fact]
    public void FamilyQueriesKeepCurrentStageWhileSpecificRewardQueriesChooseTheRequiredTier()
    {
        var index = Create();
        Assert.Null(index.FindStage(10, "runner"));
        Assert.Null(index.FindStage(10, ""));
        Assert.Equal(12, index.FindStage(10, "Sky Runner II"));
        Assert.Equal(12, index.FindStage(10, "King's Wing"));
        Assert.Equal(12, index.FindStage(10, "Kristall"));
        Assert.Equal(11, index.FindStage(10, "Kinah"));
    }

    [Fact]
    public void WrongBuildCannotIntroduceTitleRelationships()
    {
        var index = Create(titleBuild: 43);
        Assert.True(index.Matches(10, "Sky Runner"));
        Assert.True(index.Matches(10, "Crystal"));
        Assert.False(index.Matches(10, "King's Wing"));
    }

    [Fact]
    public void EveryExactNativeTitleRewardCanBeFoundByItsLocalizedNames()
    {
        var display = CombatAchievementDisplayCatalog.Default;
        var titles = CombatTitleDisplayCatalog.Default;
        var unlocks = CombatTitleUnlockCatalog.Default;
        var index = new CombatAchievementSearchIndex(display, titles, unlocks);
        foreach (var title in unlocks.Titles.Values)
            foreach (var route in title.Routes.Where(route => route.Kind == "AchievementRewardItem"))
            {
                Assert.True(index.Matches(route.SourceId, titles.Find(title.Id)!.NameDe));
                Assert.True(index.Matches(route.SourceId, titles.Find(title.Id)!.NameEn));
            }
    }

    private static CombatAchievementSearchIndex Create(int titleBuild = 42)
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var rewards = new[] { new CombatAchievementDisplayReward("Kinah", "Kinah", null, 100) };
        var crystals = new[] { new CombatAchievementDisplayReward("Gebundener Kristall", "Bound Crystal", null, 20) };
        var display = CombatAchievementDisplayCatalog.FromJson(JsonSerializer.Serialize(new
        {
            schema = 1, metadataBuild = 42, referenceOnly = true, doesNotEstablishCompletionOrClaim = true,
            categories = Array.Empty<object>(), groups = Array.Empty<object>(),
            achievements = new[]
            {
                new CombatAchievementDisplayDefinition(10, null, null, "Himmelsstürmer I", "Sky Runner I", "Aufgabe", "Task", null, null, rewards)
                { Stages = [new(11, "Himmelsstürmer I", "Sky Runner I", "Aufgabe", "Task", null, null, rewards),
                    new(12, "Himmelsstürmer II", "Sky Runner Ⅱ", "Aufgabe", "Task", null, null, crystals)] },
                new CombatAchievementDisplayDefinition(20, null, null, "Schwimmer", "Swimmer", "Aufgabe", "Task", null, null, [])
                { Stages = [new(21, "Schwimmer", "Swimmer", "Aufgabe", "Task", null, null, [])] }
            }
        }, options));
        var titles = CombatTitleDisplayCatalog.FromJson(JsonSerializer.Serialize(new
        {
            schema = 2, metadataBuild = titleBuild, presentationOnly = true, doesNotEstablishOwnershipOrEffectApplicability = true,
            titles = new[] { new CombatTitleDisplayDefinition(101, "Flügel des Königs", "King's Wing", "Attack"),
                new CombatTitleDisplayDefinition(102, "Fehlender Titel", "Missing title", "Etc") }
        }, options));
        var unlocks = CombatTitleUnlockCatalog.FromJson("""
            {"schema":1,"metadataBuild":42,"referenceOnly":true,"doesNotEstablishAvailabilityOwnershipCompletionOrClaim":true,
            "titles":[{"id":101,"directGrantItemIds":[501],"routes":[{"kind":"AchievementRewardItem","sourceId":10,"stageId":12,"itemId":501,"nameDe":"Aufgabe","nameEn":"Task","conditionDe":"Aufgabe","conditionEn":"Task"}]},
            {"id":102,"directGrantItemIds":[502],"routes":[{"kind":"QuestRewardItem","sourceId":10,"stageId":null,"itemId":502,"nameDe":"Aufgabe","nameEn":"Task","conditionDe":"Aufgabe","conditionEn":"Task"}]}]}
            """);
        return new(display, titles, unlocks);
    }
}
