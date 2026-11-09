using System.Collections.Immutable;
using Soulcrest.App.Components;
using Soulcrest.Core.Combat;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class CombatAchievementDisplayCatalogTests
{
    private const string Category = "{\"id\":\"Growth\",\"nameDe\":\"Wachstum\",\"nameEn\":\"Progress\"}";
    private const string Group = "{\"id\":7,\"categoryId\":\"Growth\",\"nameDe\":\"Aufträge\",\"nameEn\":\"Quests\"}";
    private const string Reward = "{\"nameDe\":\"Beispielbelohnung\",\"nameEn\":\"Example reward\",\"iconPath\":null,\"quantity\":5}";
    private const string Stage = "{\"id\":421,\"nameDe\":\"Katalogstufe\",\"nameEn\":\"Catalogue stage\",\"conditionDe\":\"Aufträge erfüllen\",\"conditionEn\":\"Complete duties\",\"nativeTarget\":null,\"rewards\":[" + Reward + "]}";
    private const string Entry = "{\"id\":42,\"categoryId\":\"Growth\",\"groupId\":7,\"nameDe\":\"Beispielerfolg\",\"nameEn\":\"Example achievement\",\"conditionDe\":\"Aufträge erfüllen\",\"conditionEn\":\"Complete duties\",\"nativeTarget\":3,\"rewards\":[" + Reward + "],\"stages\":[" + Stage + "],\"unknownStageIds\":[422]}";
    private static string Document(string entry = Entry, string groups = Group, string categories = Category) =>
        "{\"schema\":1,\"metadataBuild\":26425393,\"referenceOnly\":true,\"doesNotEstablishCompletionOrClaim\":true,\"categories\":["
        + categories + "],\"groups\":[" + groups + "],\"achievements\":[" + entry + "]}";

    [Fact]
    public void EmbeddedReferenceCataloguePreservesEveryAchievementAndStageWithoutGuessingTargets()
    {
        var catalog = CombatAchievementDisplayCatalog.Default;
        Assert.Equal(26425393, catalog.MetadataBuild);
        Assert.Equal(5, catalog.Categories.Count); Assert.Equal(473, catalog.Groups.Count); Assert.Equal(6428, catalog.Achievements.Count);
        Assert.Equal(new[] { "Progress", "Challenge", "Exploration", "Dungeon", "Resource" }, catalog.Categories.Select(category => category.NameEn));
        Assert.Equal(13961, catalog.Achievements.Sum(row => row.Stages.Count));
        Assert.Equal(3355, catalog.Achievements.Sum(row => row.UnknownStageIds.Count));
        var stepIds = catalog.Achievements.SelectMany(row => row.Stages.Select(stage => stage.Id).Concat(row.UnknownStageIds)).ToArray();
        Assert.Equal(17316, stepIds.Distinct().Count());
        Assert.All(catalog.Achievements, row => { Assert.Null(row.NativeTarget); Assert.All(row.Stages, stage => Assert.Null(stage.NativeTarget)); });
        Assert.Equal(2417, catalog.Achievements.Count(row => !row.HasNativeName));
        Assert.Equal(134, catalog.Groups.Count(row => !row.HasNativeName));
        Assert.Equal("Challenge", catalog.Groups.Single(group => group.Id == 400000201).CategoryId);
        Assert.Equal("Adventure", catalog.Groups.Single(group => group.Id == 400000301).CategoryId);
        Assert.Equal("Asmodians", catalog.Groups.Single(group => group.Id == 400000201).ScopeEn);
        Assert.Equal("Asmodier", catalog.Groups.Single(group => group.Id == 400000201).ScopeDe);
        Assert.Null(catalog.Find(0)); Assert.Null(catalog.Find(-1)); Assert.Null(catalog.Find(int.MaxValue));
    }

    [Fact]
    public void DefinitionsAndEveryNestedListAreFrozenAndNativeIdsStayDistinct()
    {
        var second = Entry.Replace("\"id\":42,", "\"id\":43,", StringComparison.Ordinal)
            .Replace("\"id\":421,", "\"id\":431,", StringComparison.Ordinal).Replace("[422]", "[432]", StringComparison.Ordinal);
        var catalog = CombatAchievementDisplayCatalog.FromJson(Document(Entry + "," + second));
        Assert.Equal(2, catalog.Achievements.Count); Assert.NotSame(catalog.Find(42), catalog.Find(43));
        Assert.Equal(catalog.Find(42)!.NameEn, catalog.Find(43)!.NameEn);
        Frozen(catalog.Categories); Frozen(catalog.Groups); Frozen(catalog.Achievements);
        var row = catalog.Find(42)!;
        Frozen(row.Rewards); Frozen(row.Stages); Frozen(row.UnknownStageIds); Frozen(row.Stages[0].Rewards);
        Assert.Equal("Example achievement", row.NameEn); Assert.Equal("Catalogue stage", row.Stages[0].NameEn);
        Assert.Equal(3, row.NativeTarget); Assert.Null(row.Stages[0].NativeTarget);
    }

    [Fact]
    public void ReferenceTargetsAndRewardsCannotPromoteObservedCountersIntoCompletionClaimOrCombatEffects()
    {
        var catalog = CombatAchievementDisplayCatalog.FromJson(Document());
        var observed = new CharacterAchievementState(ImmutableDictionary<int, CharacterAchievement>.Empty.Add(42, new(999, 0, 0)),
            ImmutableDictionary<int, ImmutableArray<bool>>.Empty);
        var graph = CombatSourceGraphBuilder.Build(new() { ClassId = "Cleric", Achievements = new(observed, CombatKnowledgeStatus.Complete) });
        var source = Assert.Single(graph.Sources, source => source.Kind == CombatSourceKind.Achievement);
        Assert.Equal(CombatKnowledgeStatus.Unknown, source.Status); Assert.Empty(source.Effects); Assert.Empty(source.Skills);
        Assert.Equal(new CharacterAchievement(999, 0, 0), observed.Achievements[42]);
        Assert.Equal(3, catalog.Find(42)!.NativeTarget); Assert.Equal(5, catalog.Find(42)!.Rewards[0].Quantity);
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("build")]
    [InlineData("reference")]
    [InlineData("claims")]
    [InlineData("extra-live-field")]
    [InlineData("raw-candidate-field")]
    [InlineData("duplicate-category")]
    [InlineData("duplicate-group")]
    [InlineData("duplicate-achievement")]
    [InlineData("category-fk")]
    [InlineData("group-fk")]
    [InlineData("category-group-mismatch")]
    [InlineData("invalid-id")]
    [InlineData("blank-name")]
    [InlineData("markup")]
    [InlineData("external-icon")]
    [InlineData("traversal-icon")]
    [InlineData("negative-target")]
    [InlineData("negative-reward")]
    [InlineData("null-rewards")]
    [InlineData("null-stages")]
    [InlineData("duplicate-stage")]
    [InlineData("unknown-stage-overlap")]
    [InlineData("invalid-unknown-stage")]
    public void MalformedOrOverclaimingCatalogueCannotEnterTheDisplayLookup(string variant)
    {
        var json = Document();
        json = variant switch
        {
            "schema" => json.Replace("\"schema\":1", "\"schema\":2", StringComparison.Ordinal),
            "build" => json.Replace("\"metadataBuild\":26425393", "\"metadataBuild\":0", StringComparison.Ordinal),
            "reference" => json.Replace("\"referenceOnly\":true", "\"referenceOnly\":false", StringComparison.Ordinal),
            "claims" => json.Replace("\"doesNotEstablishCompletionOrClaim\":true", "\"doesNotEstablishCompletionOrClaim\":false", StringComparison.Ordinal),
            "extra-live-field" => json.Replace("\"id\":42,", "\"completed\":true,\"id\":42,", StringComparison.Ordinal),
            "raw-candidate-field" => json.Replace("\"id\":42,", "\"stepIndexCandidate\":1,\"id\":42,", StringComparison.Ordinal),
            "duplicate-category" => Document(categories: Category + "," + Category),
            "duplicate-group" => Document(groups: Group + "," + Group),
            "duplicate-achievement" => Document(Entry + "," + Entry),
            "category-fk" => json.Replace("\"categoryId\":\"Growth\"", "\"categoryId\":\"Future\"", StringComparison.Ordinal),
            "group-fk" => json.Replace("\"groupId\":7", "\"groupId\":8", StringComparison.Ordinal),
            "category-group-mismatch" => Document(Entry.Replace("\"categoryId\":\"Growth\"", "\"categoryId\":null", StringComparison.Ordinal)),
            "invalid-id" => json.Replace("\"id\":42", "\"id\":0", StringComparison.Ordinal),
            "blank-name" => json.Replace("Example achievement", " ", StringComparison.Ordinal),
            "markup" => json.Replace("Example achievement", "<b>Example</b>", StringComparison.Ordinal),
            "external-icon" => json.Replace("\"iconPath\":null", "\"iconPath\":\"https://example.invalid/icon.png\"", StringComparison.Ordinal),
            "traversal-icon" => json.Replace("\"iconPath\":null", "\"iconPath\":\"loot-icons/../secret.png\"", StringComparison.Ordinal),
            "negative-target" => json.Replace("\"nativeTarget\":3", "\"nativeTarget\":-1", StringComparison.Ordinal),
            "negative-reward" => json.Replace("\"quantity\":5", "\"quantity\":-1", StringComparison.Ordinal),
            "null-rewards" => json.Replace("\"rewards\":[" + Reward + "]", "\"rewards\":null", StringComparison.Ordinal),
            "null-stages" => json.Replace("\"stages\":[" + Stage + "]", "\"stages\":null", StringComparison.Ordinal),
            "duplicate-stage" => json.Replace("\"stages\":[" + Stage + "]", "\"stages\":[" + Stage + "," + Stage + "]", StringComparison.Ordinal),
            "unknown-stage-overlap" => json.Replace("[422]", "[421]", StringComparison.Ordinal),
            "invalid-unknown-stage" => json.Replace("[422]", "[0]", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(variant))
        };
        Assert.Throws<InvalidDataException>(() => CombatAchievementDisplayCatalog.FromJson(json));
    }

    private static void Frozen<T>(IReadOnlyList<T> list) =>
        Assert.Throws<NotSupportedException>(() => Assert.IsAssignableFrom<IList<T>>(list).Clear());
}
