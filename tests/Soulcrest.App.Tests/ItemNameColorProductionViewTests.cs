using System.Collections.Immutable;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components;
using Soulcrest.App.Components.OverlayKit;
using Soulcrest.App.Network;
using Soulcrest.App.OverlayEditorKit;
using Soulcrest.App.Services;
using Soulcrest.Core.Combat;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pantheon;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Production rendering only: anonymous snapshots, bundled icons and manual example prices.</summary>
[Collection("Settings file")]
public sealed class ItemNameColorProductionViewTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
    private const int GearItemId = 110120003;

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task CharacterGearAndCombatSourcesUseExactItemColorsAndKeepUnknownItemsNeutral(string language)
    {
        await using var fixture = new Fixture(language);
        var snapshot = GearSnapshot();
        var gear = await fixture.Render<CombatSourceCategoryView>(new()
        {
            [nameof(CombatSourceCategoryView.Section)] = "gear", [nameof(CombatSourceCategoryView.Snapshot)] = snapshot
        });
        Assert.Contains("data-source-equipment-entry", gear);
        AssertItemName(gear, CombatSourceCategoryPresentation.ItemLabel(GearItemId), ItemNamePresentation.ForItem(GearItemId));
        AssertItemName(gear, CombatSourceCategoryPresentation.ItemLabel(1), ItemNameStyle.Neutral);
        var source = await fixture.Render<ExampleCombatSources>(new() { [nameof(ExampleCombatSources.ExampleState)] = snapshot });
        Assert.Contains("data-source-id=\"gear:0:0:" + GearItemId + "\"", source);
        AssertItemName(source, CombatScoreCalculatorPresentation.SourceTitle(snapshot.Breakdown!.Sources[0], 0), ItemNamePresentation.ForItem(GearItemId));
        AssertItemName(source, CombatScoreCalculatorPresentation.SourceTitle(snapshot.Breakdown.Sources[1], 1), ItemNameStyle.Neutral);
        Assert.DoesNotContain("item-colors-fixture", gear + source);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task NativeWingAndPantheonNamesUseTheirOwnQualifiedGradeOrItemIdentity(string language)
    {
        await using var fixture = new Fixture(language);
        var wing = WingExample();
        var wings = await fixture.Render<CombatWingsView>(WingParameters());
        AssertItemName(wings, CombatWingsPresentation.Name(CombatWingDisplayCatalog.Default, wing.Id, UiText.Language),
            ItemNamePresentation.ForGrade(wing.Grade));
        var decor = DecorExample();
        var pantheon = await fixture.Render<CombatPantheonView>(PantheonParameters());
        AssertItemName(pantheon, decor.Name(language), ItemNamePresentation.ForItem(decor.ItemId!.Value));
        Assert.Contains("data-pantheon-slot=", pantheon);
        Assert.Contains("role=\"tooltip\"", pantheon);
    }

    [Fact]
    public void BundledAchievementIdentityProjectionProvidesExactItemIdsWithoutRelabellingRewards()
    {
        var rewards = AchievementRewards().Where(reward => reward.ItemId is not null).ToArray();
        Assert.NotEmpty(rewards);
        Assert.All(rewards, reward => Assert.True(reward.ItemId > 0));
        Assert.Contains(rewards, reward => ItemNamePresentation.ForItem(reward.ItemId!.Value).IsKnown);
        Assert.All(rewards, reward =>
        {
            Assert.False(string.IsNullOrWhiteSpace(reward.NameEn));
            Assert.False(string.IsNullOrWhiteSpace(reward.NameDe));
        });
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task AchievementRewardTooltipColorsTheProjectedItemAndLeavesUnresolvedIdentityNeutral(string language)
    {
        await using var fixture = new Fixture(language);
        var reward = RewardExample();
        var html = await fixture.Render<CombatAchievementsView>(AchievementParameters());
        var decoded = WebUtility.HtmlDecode(html);
        var tooltip = Regex.Match(decoded,
            "<span(?=[^>]*id=\"ca-reward-9101-0\")(?=[^>]*role=\"tooltip\")[^>]*>.*?</span>.*?</span>",
            RegexOptions.Singleline).Value;
        Assert.NotEmpty(tooltip);
        AssertItemName(tooltip, CombatAchievementsPresentation.Label(reward.NameDe, reward.NameEn, UiText.Language),
            ItemNamePresentation.ForItem(reward.ItemId!.Value));
        AssertItemName(html, language == "de" ? "Unbekannte Belohnung" : "Unresolved reward", ItemNameStyle.Neutral);
        Assert.Contains("aria-describedby=\"ca-reward-9101-0\"", html);
    }

    [Fact]
    public void ItemColorDiagnosisNeedsOnlyExistingSettingsAndDoesNotChangeThem()
    {
        var settings = new SettingsService();
        var before = JsonSerializer.Serialize(settings.Current);
        var provider = new SettingsOnlyProvider(settings);
        var contributions = DiagnosticContributors.Create(provider);
        Assert.Empty(provider.Reads);
        var contribution = Assert.Single(contributions, row => row.FeatureIds.Contains("item-name-colors"));
        var result = contribution.Collect("item-name-colors");
        Assert.Equal(DiagnosticStatus.Healthy, result.Status);
        Assert.Equal(9452, result.Facts["knownItems"]);
        Assert.Equal(7, result.Facts["knownGrades"]);
        Assert.Equal(true, result.Facts["unknownItemsStayNeutral"]);
        Assert.Equal(false, result.Facts["changesBusinessRarity"]);
        Assert.NotEmpty(provider.Reads);
        Assert.All(provider.Reads, type => Assert.Equal(typeof(SettingsService), type));
        Assert.Equal(before, JsonSerializer.Serialize(settings.Current));
        var absent = DiagnosticContributors.Create(new DiagnosticServiceRegistry())
            .Single(row => row.FeatureIds.Contains("item-name-colors")).Collect("item-name-colors");
        Assert.Equal(DiagnosticStatus.Unavailable, absent.Status);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task LootViewAndActiveOverlayWidgetsShareForegroundsWithoutChangingStoredNames(string language)
    {
        await using var fixture = new Fixture(language);
        var loot = await fixture.Render<LootTrackerView>(LootParameters(language));
        AssertItemName(loot, MonsterLootItemCatalog.Default.Name(GearItemId, language), ItemNamePresentation.ForItem(GearItemId));
        AssertItemName(loot, language == "de" ? "Gespeicherter unbekannter Fund" : "Saved unknown drop", ItemNameStyle.Neutral);
        var overlay = await fixture.Render<OverlayPlacementRenderSurface>(new()
        {
            [nameof(OverlayPlacementRenderSurface.Model)] = OverlayExample(language)
        });
        Assert.Contains("overlay-widget-drop-grid", overlay);
        Assert.Contains("overlay-widget-rare-notice", overlay);
        AssertItemName(overlay, MonsterLootItemCatalog.Default.Name(GearItemId, language), ItemNamePresentation.ForItem(GearItemId));
        AssertItemName(overlay, language == "de" ? "Gespeicherter unbekannter Fund" : "Saved unknown drop", ItemNameStyle.Neutral);
    }

    [DiagnosticTheory]
    [Trait("Category", "Diagnostic")]
    [InlineData("de")]
    [InlineData("en")]
    public async Task ExportAnonymousProductionItemColorViews(string language)
    {
        var repository = TestPrerequisites.RepositoryRoot;
        var output = Path.GetFullPath(TestPrerequisites.RequiredValue("SOULCREST_ITEM_COLORS_UI_EXPORT"));
        var allowed = Path.GetFullPath(Path.Combine(repository, "output", "social-media", "2026-11-11-item-colors"));
        Assert.StartsWith(allowed + Path.DirectorySeparatorChar,
            Path.TrimEndingDirectorySeparator(output) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        var wwwroot = Path.Combine(repository, "src", "Soulcrest.App", "wwwroot");
        var links = Regex.Matches(await File.ReadAllTextAsync(Path.Combine(wwwroot, "index.html")),
            "<link[^>]+href=\"([^\"]+\\.css)\"").Select(match => match.Groups[1].Value).ToArray();
        Assert.Contains("css/item-controls.css", links);
        Assert.Contains("css/item-name-colors.css", links);
        var css = string.Join("\n", links.Distinct()
            .Select(path => File.ReadAllText(Path.Combine(wwwroot, path))));
        var placementCss = await File.ReadAllTextAsync(Path.Combine(wwwroot, "overlay-kit", "placement-render.css"));
        Directory.CreateDirectory(output);
        await using var fixture = new Fixture(language);
        var pages = new List<(string Name, string Html, bool Fixed)>
        {
            ("character-gear", await fixture.Render<CombatSourceCategoryView>(new()
            {
                [nameof(CombatSourceCategoryView.Section)] = "gear", [nameof(CombatSourceCategoryView.Snapshot)] = GearSnapshot()
            }), false),
            ("combat-sources", await fixture.Render<ExampleCombatSources>(new() { [nameof(ExampleCombatSources.ExampleState)] = GearSnapshot() }), false),
            ("wings", await fixture.Render<CombatWingsView>(WingParameters()), false),
            ("pantheon", await fixture.Render<CombatPantheonView>(PantheonParameters()), false),
            ("achievement-rewards", await fixture.Render<CombatAchievementsView>(AchievementParameters()), false),
            ("loot-tracker", await fixture.Render<LootTrackerView>(LootParameters(language)), false),
            ("overlay-items", await fixture.Render<OverlayPlacementRenderSurface>(new()
            {
                [nameof(OverlayPlacementRenderSurface.Model)] = OverlayExample(language)
            }), false)
        };
        foreach (var (name, state) in CraftingDesignProductionViewTests.CreatePreferenceExamples(language))
            pages.Add(("crafting-" + name, await fixture.Render<CraftingCalculatorView>(new()
            {
                [nameof(CraftingCalculatorView.Catalog)] = Soulcrest.Core.Crafting.CraftingCatalog.Default,
                [nameof(CraftingCalculatorView.State)] = state
            }), true));
        foreach (var theme in AppThemeDefinition.All)
            foreach (var page in pages)
            {
                var html = "<!doctype html><html lang=\"" + language + "\" data-app-theme=\"" + theme.Id
                    + "\" style=\"" + theme.CssVariables + "\"><head><meta charset=\"utf-8\">"
                    + "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><base href=\""
                    + WebUtility.HtmlEncode(new Uri(wwwroot + Path.DirectorySeparatorChar).AbsoluteUri)
                    + "\"><style>" + css + (page.Name == "overlay-items" ? "\n" + placementCss : "")
                    + "\nhtml,body,#app{margin:0;width:100%;min-height:100%;background:var(--bg);color:var(--text)}"
                    + (page.Fixed ? "html,body,#app,.item-colors-viewport{height:100vh;min-height:0;overflow:hidden}"
                        : ".item-colors-viewport{padding:20px;box-sizing:border-box;min-height:100vh}")
                    + "</style></head><body data-app-theme=\"" + theme.Id + "\"><div id=\"app\"><main class=\"item-colors-viewport\">"
                    + page.Html + "</main></div></body></html>";
                await File.WriteAllTextAsync(Path.Combine(output, page.Name + "-" + theme.Id + "-" + language + ".html"), html);
            }
    }

    private static void AssertItemName(string html, string label, ItemNameStyle style)
    {
        var spans = Regex.Matches(WebUtility.HtmlDecode(html),
            "<span(?=[^>]*data-item-name=\"true\")[^>]*>(?<label>.*?)</span>", RegexOptions.Singleline)
            .Where(match => match.Groups["label"].Value.Trim() == label).Select(match => match.Value).ToArray();
        Assert.NotEmpty(spans);
        foreach (var span in spans)
            if (style.CssColor is { } css) Assert.Contains("color:" + css, span);
            else
            {
                Assert.DoesNotContain("style=", span);
                Assert.DoesNotContain("data-item-grade=", span);
            }
    }

    private static CharacterCombatSnapshot GearSnapshot()
    {
        var detail = new CharacterEquipmentDetail(0, 0, 0, 0, 0, 50, false, 0, [], 0, [], [], [], [], [], [], [], [], 0);
        return new()
        {
            ProfileId = "item-colors-fixture", IsFresh = true, Generation = 1, ObservedAt = At,
            Equipment = new(new([new(GearItemId, detail), new(1, detail)]), NetworkCombatSourceStatus.Complete, true, At, 1, "fixture"),
            Breakdown = new() { Sources = [new() { SourceId = "gear:0:0:" + GearItemId, Kind = CombatSourceKind.Gear,
                Status = CombatKnowledgeStatus.Complete }, new() { SourceId = "gear:0:1:1", Kind = CombatSourceKind.Gear,
                Status = CombatKnowledgeStatus.Partial }] }
        };
    }

    private static CombatWingDisplayEntry WingExample() => CombatWingDisplayCatalog.Default.Entries.Values
        .First(wing => wing.Race == "Light" && wing.Grade == "Unique");
    private static Dictionary<string, object?> WingParameters()
    {
        var wing = WingExample();
        return new()
        {
            [nameof(CombatWingsView.Faction)] = Factions.Elyos,
            [nameof(CombatWingsView.Snapshot)] = new CharacterCombatSnapshot
            {
                IsFresh = true, Generation = 1,
                WingOwnership = new(new(ImmutableDictionary<int, byte>.Empty.Add(wing.Id, 0)), NetworkCombatSourceStatus.Complete, true, At, 1, "fixture"),
                EquippedWing = new(new(wing.Id, null, null), NetworkCombatSourceStatus.Complete, true, At, 1, "fixture")
            }
        };
    }

    private static PantheonDecor DecorExample() => PantheonCatalog.Default.Items.First(item => item.IncludesFaction("asmodian")
        && item.Category == PantheonDecorCategory.Colossus && item.ItemId is { } id && ItemNamePresentation.ForItem(id).IsKnown);
    private static Dictionary<string, object?> PantheonParameters()
    {
        var item = DecorExample();
        return new()
        {
            [nameof(CombatPantheonView.Faction)] = Factions.Asmodian,
            [nameof(CombatPantheonView.Snapshot)] = new PantheonSnapshot
            {
                SourceKind = PantheonSourceKind.Saved, ObservedAt = At, IsOwnershipKnown = true,
                OwnedIds = ImmutableHashSet.Create(item.Id), Slots = [new(17, item.Category, true, item.Id)]
            }
        };
    }

    private static IEnumerable<CombatAchievementDisplayReward> AchievementRewards() => CombatAchievementDisplayCatalog.Default.Achievements
        .SelectMany(row => row.Rewards.Concat(row.Stages.SelectMany(stage => stage.Rewards)));
    private static CombatAchievementDisplayReward RewardExample() => AchievementRewards()
        .First(reward => reward.ItemId is { } id && ItemNamePresentation.ForItem(id).IsKnown);
    private static Dictionary<string, object?> AchievementParameters()
    {
        var reward = RewardExample();
        var catalog = CombatAchievementDisplayCatalog.FromJson(JsonSerializer.Serialize(new
        {
            schema = 1, metadataBuild = 26425393, referenceOnly = true, doesNotEstablishCompletionOrClaim = true,
            categories = new[] { new CombatAchievementCategoryDefinition("Growth", "Wachstum", "Progress") },
            groups = Array.Empty<CombatAchievementGroupDefinition>(),
            achievements = new[] { new CombatAchievementDisplayDefinition(9101, "Growth", null, "Beispielerfolg", "Example achievement",
                "Beispielbedingung", "Example condition", null, 1,
                [reward, new("Unbekannte Belohnung", "Unresolved reward", null, 1)]) }
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        return new() { [nameof(CombatAchievementsView.Catalog)] = catalog };
    }

    private static Dictionary<string, object?> LootParameters(string language)
    {
        var rows = new[]
        {
            new LootTrackerLoot("Item:" + GearItemId, MonsterLootItemCatalog.Default.Name(GearItemId, language), LootTrackerLootKind.Item, 1),
            new LootTrackerLoot("Item:1", language == "de" ? "Gespeicherter unbekannter Fund" : "Saved unknown drop", LootTrackerLootKind.Item, 2)
        };
        var session = new LootTrackerSession
        {
            Id = "example-color-session", Kind = LootTrackerSessionKind.Grind, Status = LootTrackerSessionStatus.Live,
            StartedAt = At.AddMinutes(-20), LastLootAt = At, Elapsed = TimeSpan.FromMinutes(20),
            Location = language == "de" ? "Beispielgebiet" : "Example area", MonsterCount = 42,
            ItemCount = 3, KinaUnbound = 150000, Loot = rows.ToImmutableArray(),
            MinuteIncome = [new(0, 30000, 0), new(10, 120000, 0)]
        };
        return new() { [nameof(LootTrackerView.Snapshot)] = new LootTrackerSnapshot([session], At),
            [nameof(LootTrackerView.IsExample)] = true, [nameof(LootTrackerView.SourceAvailable)] = true };
    }

    private static OverlayPlacementRenderState OverlayExample(string language)
    {
        var name = MonsterLootItemCatalog.Default.Name(GearItemId, language);
        var icon = MonsterLootIconCatalog.Default.ItemAsset(GearItemId);
        OverlayLootItem[] drops = [new("Item:" + GearItemId, name, "1", icon, true, 1, Rarity: LootItemRarity.Unique),
            new("Item:1", language == "de" ? "Gespeicherter unbekannter Fund" : "Saved unknown drop", "2", null, true, 2,
                Rarity: LootItemRarity.Unique)];
        var window = new OverlayInstance { Name = "Soulcrest", Settings = new OverlaySettings
        {
            Width = 700, Height = 420, ShowBorder = true, BackgroundOpacity = .95,
            Widgets = [OverlayCatalog.CreateWidget("drop-grid", 16, 16) with { Width = 660, Height = 240, ItemView = "list" },
                OverlayCatalog.CreateWidget("rare-notice", 16, 275) with { Width = 660, Height = 100 }]
        }};
        var snapshot = OverlaySnapshot.DemoFor(language) with
        {
            Drops = drops, ItemCatalog = drops, SessionActive = true, TrackingAvailable = true, ItemAnimationsEnabled = false,
            RareNotice = new("example-notice", name, 1, "1", icon, LootItemRarity.Unique, 1)
            { LootKey = "Item:" + GearItemId }
        };
        var model = new OverlayPlacementRenderState(pauseAnimations: true);
        model.Publish(window, snapshot);
        return model;
    }

    public sealed class ExampleCombatSources : CombatScoreCalculatorView
    {
        [Parameter] public CharacterCombatSnapshot ExampleState { get; set; } = CharacterCombatSnapshot.Empty;
        protected override void OnInitialized()
        {
            base.OnInitialized();
            typeof(CombatScoreCalculatorView).GetField("State", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(this, ExampleState);
            typeof(CombatScoreCalculatorView).GetField("_tab", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(this, "sources");
        }
    }

    private sealed class SettingsOnlyProvider(SettingsService settings) : IServiceProvider
    {
        internal List<Type> Reads { get; } = [];
        public object? GetService(Type serviceType)
        {
            Reads.Add(serviceType);
            return serviceType == typeof(SettingsService) ? settings : null;
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _language = UiText.Language;
        private readonly ServiceProvider _provider;
        private readonly HtmlRenderer _renderer;
        private readonly OwnSkillStateService _skills;
        private readonly CharacterCombatStateService _character;
        internal Fixture(string language)
        {
            UiText.Language = language;
            var settings = new SettingsService();
            settings.Current.UiLanguage = settings.Current.NameLanguage = language;
            // Settings construction applies its persisted language; the anonymous render owns this explicit locale.
            UiText.Language = language;
            settings.Current.LootTrackingEnabled = settings.Current.OnlineMarketPricesEnabled = false;
            var source = new CombatWorkspaceViewTests.Source { Running = false };
            _skills = new(source, _ => null);
            _character = new(source, _skills, _ => null);
            _provider = new ServiceCollection().AddLogging().AddSingleton(settings).AddSingleton(_character).BuildServiceProvider();
            _renderer = new(_provider, _provider.GetRequiredService<ILoggerFactory>());
        }
        internal Task<string> Render<T>(Dictionary<string, object?> parameters) where T : IComponent =>
            _renderer.Dispatcher.InvokeAsync(async () => (await _renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters))).ToHtmlString());
        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync();
            await _provider.DisposeAsync();
            _character.Dispose();
            _skills.Dispose();
            UiText.Language = _language;
        }
    }
}
