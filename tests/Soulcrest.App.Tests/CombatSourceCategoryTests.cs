using System.Collections.Immutable;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Combat;
using Soulcrest.Core.Daevanion;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class CombatSourceCategoryTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("wings", "")]
    [InlineData("gear", "Gear|Enchantment|EquipmentSet|Soulbind|Magicstone|Godstone|Arcana")]
    [InlineData("pets", "")]
    [InlineData("genus-insight", "")]
    [InlineData("titles", "")]
    [InlineData("achievements", "")]
    [InlineData("pantheon", "")]
    public async Task ProductionPanelSelectsOnlyItsSourceKindsAndNeverRebrandsMonolithAsPantheon(string section, string expected)
    {
        await using var fixture = new RendererFixture();
        var root = await fixture.Render(section, Snapshot());
        var html = await fixture.Html(root);
        var actual = Regex.Matches(html, "data-source-report-kind=\"([^\"]+)\"").Select(match => match.Groups[1].Value);
        Assert.Equal(expected.Split('|', StringSplitOptions.RemoveEmptyEntries).Order(), actual.Order());
        Assert.Contains("data-combat-source-category=\"" + section + "\"", html);
        Assert.DoesNotContain("data-source-report-kind=\"Monolith\"", html);
        Assert.DoesNotContain("data-source-report-kind=\"Daevanion\"", html);
        Assert.DoesNotContain("NEVER-RENDER-PROFILE", html);
        Assert.DoesNotContain("NEVER-RENDER-SOURCE-IDENTITY", html);
        Assert.DoesNotContain("918273645546372819", html);
        var inputs = Regex.Matches(html, "<input\\b[^>]*>").Select(match => match.Value).ToArray();
        if (section == "achievements")
        {
            var search = Assert.Single(inputs);
            Assert.Contains("type=\"search\"", search);
            Assert.Contains("data-achievement-search", search);
        }
        else Assert.Empty(inputs);
        if (section == "titles")
        {
            Assert.Contains("data-combat-titles", html);
            Assert.Contains("data-title-category-filter", html);
            Assert.DoesNotContain("data-source-model-section", html);
            Assert.DoesNotContain("cw-source-technical", html);
        }
        else if (section == "wings")
        {
            Assert.Contains("data-combat-wings", html); Assert.Contains("data-wing-passive-filter", html);
            Assert.DoesNotContain("data-source-model-section", html); Assert.DoesNotContain("cw-source-technical", html);
        }
        else if (section == "pets")
        {
            Assert.Contains("data-combat-pets", html); Assert.Contains("data-open-main-pets", html);
            Assert.DoesNotContain("data-source-model-section", html); Assert.DoesNotContain("cw-source-technical", html);
        }
        else if (section == "genus-insight")
        {
            Assert.Contains("data-combat-genus", html); Assert.Contains("data-genus-options", html);
            Assert.DoesNotContain("data-source-model-section", html); Assert.DoesNotContain("cw-source-technical", html);
        }
        else if (section == "achievements")
        {
            Assert.Contains("data-combat-achievements", html); Assert.Contains("data-achievement-category-filter", html);
            Assert.DoesNotContain("data-source-model-section", html); Assert.DoesNotContain("cw-source-technical", html);
            Assert.DoesNotContain("data-source-achievement", html); Assert.DoesNotContain("aria-valuenow", html);
        }
        else Assert.DoesNotContain("<button", html);
        if (section == "pantheon")
        {
            Assert.DoesNotContain("data-source-model-section", html);
            Assert.Contains(UiText.T("Pantheon gehört zum Gildenausbau. Ein eigener bestätigter Ausbauzustand ist noch nicht zugeordnet."), html);
        }
    }

    [Theory]
    [InlineData("gear", "data-source-equipment-entry")]
    [InlineData("genus-insight", "data-source-genus-option")]
    [InlineData("achievements", "data-source-achievement")]
    [InlineData("titles", "data-owned-title=")]
    public async Task StaleDomainsDoNotExposeTheirOldRecordsOrLossesAsCurrent(string section, string recordMarker)
    {
        var snapshot = Snapshot();
        snapshot = snapshot with
        {
            Equipment = snapshot.Equipment with { IsFresh = false }, Pets = snapshot.Pets with { IsFresh = false },
            Titles = snapshot.Titles with { IsFresh = false }, Achievements = snapshot.Achievements with { IsFresh = false },
            TitleOwnership = snapshot.TitleOwnership with { IsFresh = false }
        };
        await using var fixture = new RendererFixture();
        var html = await fixture.Html(await fixture.Render(section, snapshot));
        Assert.Contains("data-source-observation-status=\"Stale\"", html);
        Assert.Contains(section == "genus-insight" ? "data-genus-current=\"false\"" : "data-source-awaiting-current", html);
        Assert.DoesNotContain(recordMarker, html);
        Assert.DoesNotContain("data-source-report-kind", html);
    }

    [Theory]
    [InlineData("de", "Modellverlust ohne Quelle")]
    [InlineData("en", "Model loss without source")]
    public async Task NoCombatScoreModelLossIsShownWithoutQualifiedWeights(string language, string label)
    {
        // User decision 2026-11-09: the product determines no combat score yet and bundles no community model.
        var previous = UiText.Language; UiText.Language = language;
        try
        {
            await using var fixture = new RendererFixture();
            var snapshot = Snapshot();
            snapshot = snapshot with { Breakdown = snapshot.Breakdown! with { Status = CombatKnowledgeStatus.Partial } };
            var html = await fixture.Html(await fixture.Render("gear", snapshot));
            Assert.DoesNotContain(label, html);
            Assert.DoesNotContain("data-source-model-loss", html);
        }
        finally { UiText.Language = previous; }
    }

    [Fact]
    public async Task PetsShowsGlobalSummaryNotCharacterRawRowsAndGenusUsesOnlyItsSelectedPanel()
    {
        await using var fixture = new RendererFixture();
        var snapshot = Snapshot();
        var pets = await fixture.Html(await fixture.Render("pets", snapshot));
        Assert.Contains("data-combat-pets", pets);
        Assert.Equal(new[] { "cogni", "fera", "natura", "varian" }, Regex.Matches(pets, "data-pet-category=\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value));
        Assert.DoesNotContain("data-source-pet-level", pets);
        Assert.DoesNotContain("data-source-model-section", pets);
        Assert.DoesNotContain("data-source-genus-option", pets);

        var genus = await fixture.Html(await fixture.Render("genus-insight", snapshot));
        Assert.Contains("data-genus-applied-stat=\"FixingDamage\"", genus);
        Assert.DoesNotContain("data-source-pet-level", genus);
        Assert.DoesNotContain("data-source-model-section", genus);
        Assert.Null(CombatSourceCategoryPresentation.OptionValue(19, 3, CombatNativeValueDomain.EquipmentOptions));
        Assert.Null(CombatSourceCategoryPresentation.OptionValue(19, 3, CombatNativeValueDomain.GenusEffects));
    }

    [Fact]
    public async Task ParameterChangesRefreshCurrentListsAndRemoveFactsAfterOwnAuthorityIsLost()
    {
        await using var fixture = new RendererFixture();
        Host? host = null;
        var root = await fixture.RenderHost("pets", Snapshot(), value => host = value);
        Assert.Contains("data-combat-pets", await fixture.Html(root));
        await fixture.Renderer.Dispatcher.InvokeAsync(() => host!.Update("genus-insight", Snapshot()));
        var genus = await fixture.Html(root);
        Assert.Contains("data-genus-applied-stat=\"FixingDamage\"", genus); Assert.DoesNotContain("data-source-pet-level", genus);
        await fixture.Renderer.Dispatcher.InvokeAsync(() => host!.Update("genus-insight", Snapshot() with { IsFresh = false }));
        var expired = await fixture.Html(root);
        Assert.Contains("data-genus-current=\"false\"", expired);
        Assert.DoesNotContain("data-genus-applied-stat=", expired);
        Assert.DoesNotContain("data-source-genus-option", expired);
        Assert.DoesNotContain("data-source-report-kind", expired);
    }

    [Fact]
    public void UnknownDomainsStayUnknownAndExplicitCompleteEmptyListsRemainUsable()
    {
        var empty = CharacterCombatSnapshot.Empty with { IsFresh = true };
        Assert.Equal(NetworkCombatSourceStatus.Unknown, CombatSourceCategoryPresentation.ObservationStatus(empty, "pets"));
        Assert.False(CombatSourceCategoryPresentation.IsCurrent(empty, empty.Pets));
        Assert.Empty(CombatSourceCategoryPresentation.Reports(Snapshot(), "pantheon"));
        Assert.False(CombatSourceCategoryPresentation.Includes("pantheon", CombatSourceKind.Monolith));
        var snapshot = Snapshot();
        var noItems = snapshot.Equipment with { Value = new CharacterEquipmentState([]) };
        Assert.True(CombatSourceCategoryPresentation.IsCurrent(snapshot, noItems));
        var report = snapshot.Breakdown!.Sources.Single(source => source.Kind == CombatSourceKind.Genus);
        Assert.Equal(42m, CombatSourceCategoryPresentation.ModelLoss(snapshot, "genus-insight", report));
        Assert.Null(CombatSourceCategoryPresentation.ModelLoss(snapshot with { IsFresh = false }, "genus-insight", report));
        Assert.Null(CombatSourceCategoryPresentation.ModelLoss(snapshot, "titles", report));
    }

    [Fact]
    public async Task HistoricalGlobalPetCollectionRemainsVisibleWithoutAnyCharacterSnapshot()
    {
        await using var fixture = new RendererFixture();
        var collection = CombatPetsPresentation.Create(new PetCollectionSnapshot(Array.AsReadOnly(new[]
        {
            new PetCollectionEntry("synthetic-cogni", "cogni", 3, true, false, false, At),
            new PetCollectionEntry("synthetic-fera", "fera", 2, true, false, false, At),
            new PetCollectionEntry("synthetic-natura", "natura", 1, true, false, false, At),
            new PetCollectionEntry("synthetic-varian", "varian", 0, true, false, false, At)
        })));
        var html = await fixture.Html(await fixture.Render("pets", CharacterCombatSnapshot.Empty, petCollection: collection));
        Assert.Contains("data-pet-summary-source=\"Saved\"", html);
        Assert.Contains("data-pet-saved-stand", html);
        Assert.Contains("data-pet-effect-stat=\"HPMax|Points\"", html);
        Assert.Contains("data-pet-effect-value=\"30\"", html);
        Assert.Equal(4, Regex.Matches(html, "data-pet-category=\"").Count);
        Assert.DoesNotContain("data-source-pet-level", html);
        Assert.DoesNotContain("data-source-genus-option", html);
        Assert.DoesNotContain("data-source-model-section", html);
    }

    [Theory]
    [InlineData("Live", true, true)]
    [InlineData("Saved", true, false)]
    [InlineData("Live", false, false)]
    public async Task GenericActivityIsRecognizedInTheHeaderWithoutAQualifiedCounter(string source, bool sameProfile, bool recognized)
    {
        await using var fixture = new RendererFixture();
        var previous = UiText.Language; UiText.Language = "en";
        try
        {
            var state = new AchievementViewState
            {
                Source = Enum.Parse<AchievementViewSource>(source),
                MetadataBuild = CombatAchievementDisplayCatalog.Default.MetadataBuild,
                ProfileId = sameProfile ? Snapshot().ProfileId : "SYNTHETIC-OTHER-PROFILE",
                Activities = ImmutableDictionary<int, AchievementActivity>.Empty.Add(444100500, new(444100500, 1, 2, At)),
            };
            var html = await fixture.Html(await fixture.Render("achievements", Snapshot(), state));
            var header = html[..html.IndexOf("</header>", StringComparison.Ordinal)];
            Assert.Equal(recognized, header.Contains("Progress updated", StringComparison.Ordinal));
        }
        finally { UiText.Language = previous; }
    }

    private static CharacterCombatSnapshot Snapshot()
    {
        var detail = new CharacterEquipmentDetail(4, 0, 0, 1, 0, 35, false, 2,
            [new(9001, 19, 1)], 2, [9002], [], [new(19, 3)], [], [new(17060000, 1)], [], [], [], 0);
        var equipment = new CharacterEquipmentState([new CharacterEquipmentItem(999999, detail) { InstanceKey = 918273645546372819 }]);
        var genus = new CharacterGenusBoard(6, 3, 0, ImmutableDictionary<byte, ImmutableArray<CharacterGenusEffect>>.Empty
            .Add(0, [new(1, 1, 19, 77, false, true), new(2, 1, 19, 88, false, true)])
            .Add(1, [new(0, 1, 19, 3, true, true)]));
        var pets = new PetProgressSnapshot(ImmutableDictionary<int, int>.Empty.Add(1, 2).Add(2, 3),
            ImmutableDictionary<int, int>.Empty.Add(1, 0))
            { GenusBoards = ImmutableDictionary<byte, CharacterGenusBoard>.Empty.Add(1, genus), SelectedGenusPanel = 1,
                AcquiredCollections = ImmutableHashSet<int>.Empty.Add(7001) };
        return new()
        {
            ProfileId = "NEVER-RENDER-PROFILE", IsFresh = true, ClassId = "Cleric", Generation = 7, Sequence = 1,
            Equipment = Domain(equipment), Pets = Domain(pets) with { Source = "own-pet-genus-list" },
            Titles = Domain(new CharacterTitleState(ImmutableHashSet<int>.Empty.Add(7001).Add(7002),
                ImmutableDictionary<byte, int>.Empty.Add(1, 7001).Add(2, 0))),
            TitleOwnership = Domain(ImmutableHashSet.Create(7001, 7002)),
            Achievements = Domain(new CharacterAchievementState(ImmutableDictionary<int, CharacterAchievement>.Empty.Add(8001, new(7, 0, 2)),
                ImmutableDictionary<int, ImmutableArray<bool>>.Empty.Add(8101, [true, false]))),
            Breakdown = new()
            {
                Status = CombatKnowledgeStatus.Complete,
                Sources = Enum.GetValues<CombatSourceKind>().Select(kind => new CombatSourceReport
                {
                    Kind = kind, SourceId = "NEVER-RENDER-SOURCE-IDENTITY", Status = CombatKnowledgeStatus.Complete, ModelLoss = 42,
                    Effects = [new("HPMax", 10, Application: CombatApplication.Confirmed)],
                    Skills = [new(17060000, DaevanionSkillKind.Active, 1, CombatApplication.Confirmed)],
                    StatContributions = [new("HPMax", 100, 110, 10)]
                }).ToArray()
            }
        };
    }

    private static NetworkCombatDomain<T> Domain<T>(T value) where T : class => new(value, NetworkCombatSourceStatus.Complete, true, At, 1, "synthetic-source");

    private sealed class RendererFixture : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        internal HtmlRenderer Renderer { get; }
        internal RendererFixture()
        {
            var services = new ServiceCollection(); services.AddLogging(); _services = services.BuildServiceProvider();
            Renderer = new(_services, _services.GetRequiredService<ILoggerFactory>());
        }
        internal Task<HtmlRootComponent> Render(string section, CharacterCombatSnapshot snapshot, AchievementViewState? achievementState = null,
            CombatPetsSnapshot? petCollection = null) => Renderer.Dispatcher.InvokeAsync(() =>
            Renderer.RenderComponentAsync<CombatSourceCategoryView>(ParameterView.FromDictionary(new Dictionary<string, object?>
                { [nameof(CombatSourceCategoryView.Section)] = section, [nameof(CombatSourceCategoryView.Snapshot)] = snapshot,
                  [nameof(CombatSourceCategoryView.AchievementState)] = achievementState,
                  [nameof(CombatSourceCategoryView.GenusState)] = GenusInsightProjection.Create(snapshot),
                  [nameof(CombatSourceCategoryView.PetCollection)] = petCollection ?? CombatPetsSnapshot.Empty })));
        internal Task<HtmlRootComponent> RenderHost(string section, CharacterCombatSnapshot snapshot, Action<Host> capture) => Renderer.Dispatcher.InvokeAsync(() =>
            Renderer.RenderComponentAsync<Host>(ParameterView.FromDictionary(new Dictionary<string, object?>
                { [nameof(Host.Section)] = section, [nameof(Host.Snapshot)] = snapshot, [nameof(Host.Capture)] = capture })));
        internal async Task<string> Html(HtmlRootComponent root) => WebUtility.HtmlDecode(await Renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
        public async ValueTask DisposeAsync() { await Renderer.DisposeAsync(); await _services.DisposeAsync(); }
    }

    private sealed class Host : ComponentBase
    {
        [Parameter] public string Section { get; set; } = "pets";
        [Parameter] public CharacterCombatSnapshot Snapshot { get; set; } = CharacterCombatSnapshot.Empty;
        [Parameter] public Action<Host>? Capture { get; set; }
        protected override void OnInitialized() => Capture?.Invoke(this);
        internal void Update(string section, CharacterCombatSnapshot snapshot) { Section = section; Snapshot = snapshot; StateHasChanged(); }
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<CombatSourceCategoryView>(0); builder.AddAttribute(1, nameof(CombatSourceCategoryView.Section), Section);
            builder.AddAttribute(2, nameof(CombatSourceCategoryView.Snapshot), Snapshot);
            builder.AddAttribute(3, nameof(CombatSourceCategoryView.GenusState), GenusInsightProjection.Create(Snapshot)); builder.CloseComponent();
        }
    }
}
