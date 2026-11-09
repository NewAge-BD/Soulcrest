using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class CombatTitleUnlockTests
{
    private const string Entry = "{\"id\":42,\"directGrantItemIds\":[123],\"routes\":[{\"kind\":\"AchievementRewardItem\",\"sourceId\":7,\"stageId\":8,\"itemId\":123,\"nameDe\":\"Beispielerfolg\",\"nameEn\":\"Example achievement\",\"conditionDe\":\"Schließe drei Quests ab\",\"conditionEn\":\"Complete three quests\"}]}";
    private static string Document(string entries) => "{\"schema\":1,\"metadataBuild\":26425393,\"referenceOnly\":true,"
        + "\"doesNotEstablishAvailabilityOwnershipCompletionOrClaim\":true,\"titles\":[" + entries + "]}";

    [Fact]
    public void AllNativeTitleIdsHaveIndependentRewardReferencesWithConsistentItemGrants()
    {
        var catalog = CombatTitleUnlockCatalog.Default;
        Assert.Equal(CombatTitleDisplayCatalog.Default.MetadataBuild, catalog.MetadataBuild);
        Assert.Equal(1517, catalog.Titles.Count);
        Assert.Equal(CombatTitleDisplayCatalog.Default.Titles.Keys.Order(), catalog.Titles.Keys.Order());
        Assert.Equal(1477, catalog.Titles.Values.Sum(title => title.DirectGrantItemIds.Count));
        var routes = catalog.Titles.Values.SelectMany(title => title.Routes).ToArray();
        Assert.Equal(1690, routes.Length);
        Assert.Equal(342, routes.Count(route => route.Kind == "AchievementRewardItem"));
        Assert.Equal(134, routes.Count(route => route.Kind == "QuestRewardItem"));
        Assert.Equal(584, routes.Count(route => route.Kind == "SeasonRankingRewardItem"));
        Assert.Equal(630, routes.Count(route => route.Kind == "RankingCatalogReference"));
        foreach (var route in routes.Where(route => route.Kind == "AchievementRewardItem"))
        {
            var family = CombatAchievementDisplayCatalog.Default.Find(route.SourceId);
            Assert.NotNull(family);
            Assert.Contains(family.Stages, stage => stage.Id == route.StageId);
        }
        Assert.Equal(40, catalog.Titles.Values.Count(title => title.DirectGrantItemIds.Count == 0));
        Assert.Equal(472, catalog.Titles.Values.Count(title => title.DirectGrantItemIds.Count > 0 && title.Routes.Count == 0));
        Assert.All(catalog.Titles.Values, title => Assert.All(title.Routes, route => Assert.Contains(route.ItemId, title.DirectGrantItemIds)));
        Assert.Null(catalog.Find(999));
    }

    [Fact]
    public void RewardReferencesAreFrozenAndDoNotProvideLiveCompletionOrClaimFields()
    {
        var title = CombatTitleUnlockCatalog.FromJson(Document(Entry)).Find(42)!;
        Assert.Throws<NotSupportedException>(() => ((IList<int>)title.DirectGrantItemIds).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<CombatTitleUnlockRoute>)title.Routes).Clear());
        Assert.Equal(7, Assert.Single(title.Routes).SourceId);
        foreach (var field in new[] { "owned", "complete", "claimed", "current", "available" })
            Assert.Throws<InvalidDataException>(() => CombatTitleUnlockCatalog.FromJson(Document(Entry.Replace("\"id\":42", "\"" + field + "\":true,\"id\":42", StringComparison.Ordinal))));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("bad-item")]
    [InlineData("unknown-kind")]
    [InlineData("missing-stage")]
    [InlineData("bad-source")]
    [InlineData("markup")]
    [InlineData("semantics")]
    [InlineData("build")]
    public void InvalidOrUnqualifiedReferencesFailClosed(string variant)
    {
        var json = Document(Entry);
        json = variant switch
        {
            "duplicate" => Document(Entry + "," + Entry),
            "bad-item" => json.Replace("\"itemId\":123", "\"itemId\":456", StringComparison.Ordinal),
            "unknown-kind" => json.Replace("AchievementRewardItem", "CompleteAchievementGrantsTitle", StringComparison.Ordinal),
            "missing-stage" => json.Replace("\"stageId\":8", "\"stageId\":null", StringComparison.Ordinal),
            "bad-source" => json.Replace("\"sourceId\":7", "\"sourceId\":0", StringComparison.Ordinal),
            "markup" => json.Replace("Example achievement", "<b>Example</b>", StringComparison.Ordinal),
            "semantics" => json.Replace("\"referenceOnly\":true", "\"referenceOnly\":false", StringComparison.Ordinal),
            "build" => json.Replace("\"metadataBuild\":26425393", "\"metadataBuild\":0", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(variant))
        };
        Assert.Throws<InvalidDataException>(() => CombatTitleUnlockCatalog.FromJson(json));
    }

    [Theory]
    [InlineData("en", "Achievement reward", "Example achievement", "Complete three quests")]
    [InlineData("de", "Erfolgsbelohnung", "Beispielerfolg", "Schließe drei Quests ab")]
    public async Task NativeUnlockSourceIsCollapsedLocalizedAndNeverClaimsOwnership(string language, string kind, string name, string condition)
    {
        var previous = UiText.Language; UiText.Language = language;
        try
        {
            var html = await Render(42, 26425393, CombatTitleUnlockCatalog.FromJson(Document(Entry)));
            Assert.Contains("data-title-unlock-status=\"Referenced\"", html);
            Assert.Contains("<details", html); Assert.DoesNotContain(" open", html);
            Assert.Contains(kind, html); Assert.Contains(name, html); Assert.Contains(condition, html);
            Assert.DoesNotContain("data-owned-title", html); Assert.DoesNotContain("data-achievement-current", html);
            Assert.Contains(UiText.T("Die Belohnung liefert einen Titelgegenstand. Erst dessen Benutzung schaltet den Titel frei."), html);
            var mismatched = await Render(42, 26429009, CombatTitleUnlockCatalog.FromJson(Document(Entry)));
            Assert.Contains("data-title-unlock-status=\"Unknown\"", mismatched);
            Assert.DoesNotContain(name, mismatched);
        }
        finally { UiText.Language = previous; }
    }

    [Theory]
    [InlineData("ItemOnly", "{\"id\":42,\"directGrantItemIds\":[123],\"routes\":[]}")]
    [InlineData("Unknown", "{\"id\":42,\"directGrantItemIds\":[],\"routes\":[]}")]
    public async Task ItemUseOnlyAndUnknownAcquisitionStayDistinct(string status, string entry)
    {
        var html = await Render(42, 26425393, CombatTitleUnlockCatalog.FromJson(Document(entry)));
        Assert.Contains("data-title-unlock-status=\"" + status + "\"", html);
        Assert.DoesNotContain("data-title-unlock-source=", html);
    }

    private static async Task<string> Render(int titleId, int build, CombatTitleUnlockCatalog catalog)
    {
        var services = new ServiceCollection(); services.AddLogging();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<CombatTitleUnlockDetails>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(CombatTitleUnlockDetails.TitleId)] = titleId, [nameof(CombatTitleUnlockDetails.MetadataBuild)] = build,
                [nameof(CombatTitleUnlockDetails.Catalog)] = catalog
            })));
        return WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
    }
}
