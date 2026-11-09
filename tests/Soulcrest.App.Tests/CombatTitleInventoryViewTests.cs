using System.Collections.Immutable;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class CombatTitleInventoryViewTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("de", "Himmelsbekleidet", "Illusionserkenner")]
    [InlineData("en", "Draped in Sky", "Discerner of Illusions")]
    public async Task OwnedIdsRenderLocalizedNamesAndIndependentEquippedSlots(string language, string first, string second)
    {
        await WithLanguage(language, async () =>
        {
            var snapshot = Snapshot([11010001, 11010002, 999999], (1, 11010001), (2, 11010001), (3, 0));
            var html = await Render(snapshot);
            Assert.Contains("data-owned-title-count=\"3\"", html);
            Assert.Equal(3, Regex.Matches(html, "data-owned-title=\"").Count);
            Assert.Equal(3, Regex.Matches(html, "data-title-slot=\"").Count);
            Assert.Equal(2, Regex.Matches(html, "data-equipped-title=\"11010001\"").Count);
            Assert.Contains("data-equipped-title=\"0\"", html);
            Assert.Contains(first, Row(html, 11010001)); Assert.Contains(second, Row(html, 11010002));
            Assert.Contains(UiText.T("Nicht zugeordneter Titel"), Row(html, 999999));
            Assert.DoesNotContain("data-owned-title=\"0\"", html);
            Assert.DoesNotContain("/ 318", html); Assert.DoesNotContain("1517", html);
            Assert.DoesNotContain("TITLE-PRIVATE-PROFILE", html);
            Assert.Contains("data-source-observed-at=\"2026-11-06T12:00:00.0000000+00:00\"", html);
            var ids = Regex.Matches(html, "data-owned-title=\"(\\d+)\"").Select(match => int.Parse(match.Groups[1].Value)).ToArray();
            Assert.Equal(new[] { 11010002, 11010001, 999999 }, ids); // Rare, Common, unknown; the palette is unchanged.
        });
    }

    [Fact]
    public async Task EquippedTitleDoesNotInventAnAdditionalOwnedEntry()
    {
        var html = await Render(Snapshot([11010001], (1, 11010002)));
        Assert.Contains("data-owned-title-count=\"1\"", html);
        Assert.Contains("data-equipped-title=\"11010002\"", html);
        Assert.DoesNotContain("data-owned-title=\"11010002\"", html);
        Assert.Single(Regex.Matches(html, "data-owned-title=\"").Cast<Match>());
    }

    [Theory]
    [InlineData("snapshot")]
    [InlineData("domain")]
    [InlineData("conflict")]
    [InlineData("unknown")]
    public async Task NonCurrentSourcesDoNotExposeOldTitleNamesOrCounts(string variant)
    {
        var snapshot = Snapshot([11010001], (1, 11010001));
        snapshot = variant switch
        {
            "snapshot" => snapshot with { IsFresh = false },
            "domain" => snapshot with { Titles = snapshot.Titles with { IsFresh = false },
                TitleOwnership = snapshot.TitleOwnership with { IsFresh = false } },
            "conflict" => snapshot with { Titles = snapshot.Titles with { Status = NetworkCombatSourceStatus.Conflict },
                TitleOwnership = snapshot.TitleOwnership with { Status = NetworkCombatSourceStatus.Conflict } },
            "unknown" => snapshot with { Titles = NetworkCombatDomain<CharacterTitleState>.Unknown,
                TitleOwnership = NetworkCombatDomain<ImmutableHashSet<int>>.Unknown },
            _ => throw new ArgumentOutOfRangeException(nameof(variant))
        };
        var html = await Render(snapshot);
        Assert.Contains("data-source-awaiting-current", html);
        Assert.DoesNotContain("data-owned-title-count", html); Assert.DoesNotContain("data-owned-title=", html);
        Assert.DoesNotContain("data-equipped-title", html); Assert.DoesNotContain("data-source-observed-at=", html);
        Assert.DoesNotContain("Draped in Sky", html); Assert.DoesNotContain("Himmelsbekleidet", html);
    }

    [Fact]
    public async Task CurrentCharacterOwnershipRemainsVisibleWhenItsEquippedSlotsAreStale()
    {
        var snapshot = Snapshot([11010001, 11010002], (1, 11010001));
        snapshot = snapshot with { Generation = 8, ClassId = "Sorcerer",
            Titles = snapshot.Titles with { IsFresh = false, Status = NetworkCombatSourceStatus.Stale } };
        var html = await Render(snapshot);
        Assert.Contains("data-owned-title-count=\"2\"", html);
        Assert.Contains("data-owned-title=\"11010001\"", html);
        Assert.Contains("data-owned-title=\"11010002\"", html);
        Assert.Contains("data-title-slots-awaiting-current", html);
        Assert.DoesNotContain("data-equipped-title", html);
        Assert.DoesNotContain("data-title-slots-observed-at=", html);
        Assert.Contains("data-source-observed-at=\"2026-11-06T12:00:00.0000000+00:00\"", html);
    }

    [Fact]
    public async Task CurrentSlotFactsDoNotReviveAnUnknownOwnershipDomain()
    {
        var snapshot = Snapshot([11010001], (1, 11010001));
        snapshot = snapshot with { TitleOwnership = NetworkCombatDomain<ImmutableHashSet<int>>.Unknown };
        var html = await Render(snapshot);
        Assert.Contains("data-equipped-title=\"11010001\"", html);
        Assert.DoesNotContain("data-owned-title-count", html);
        Assert.DoesNotContain("data-owned-title=", html);
        Assert.DoesNotContain("data-source-observed-at=", html);
        Assert.Contains("data-title-slots-observed-at=", html);
    }

    [Fact]
    public async Task ExplicitCompleteEmptyInventoryIsZeroWhileUnknownInventoryIsNot()
    {
        var html = await Render(Snapshot([]));
        Assert.Contains("data-owned-title-count=\"0\"", html);
        Assert.DoesNotContain("data-owned-title=", html);
        Assert.DoesNotContain("data-equipped-title", html);
        var unknown = await Render(CharacterCombatSnapshot.Empty with { IsFresh = true });
        Assert.DoesNotContain("data-owned-title-count", unknown);
    }

    [Fact]
    public async Task NativeFilterCallbacksIntersectCategoryAndOriginWithoutChangingEquippedSlots()
    {
        await WithLanguage("en", async () =>
        {
            await using var fixture = new InteractiveFixture();
            var html = await fixture.Render(Snapshot([1, 2, 3, 4, 5], (1, 3), (2, 2), (3, 4)), FilterCatalog());
            Assert.Equal(4, Regex.Matches(html, "data-title-category-filter=").Count);
            Assert.Equal(7, Regex.Matches(html, "data-title-origin-filter=").Count);
            Assert.DoesNotContain("data-source-model-section", html); Assert.DoesNotContain("Technische Details", html);
            Assert.Equal(new[] { 1, 2, 3, 4, 5 }, OwnedIds(html).Order());
            var slots = EquippedIds(html);

            html = await fixture.Click("data-title-category-filter", "Attack");
            Assert.Equal(new[] { 1, 3 }, OwnedIds(html).Order());
            html = await fixture.Click("data-title-origin-filter", "Quest");
            Assert.Equal(new[] { 1 }, OwnedIds(html));
            Assert.Equal(slots, EquippedIds(html));
            Assert.Contains("data-owned-title-count=\"5\"", html);
            Assert.Contains("data-title-visible-count=\"1\"", html);

            html = await fixture.Click("data-title-category-filter", "All");
            Assert.Equal(new[] { 1, 2 }, OwnedIds(html).Order());
            Assert.Contains("data-title-origin=\"Quest\"", html);
            Assert.Equal(slots, EquippedIds(html));
            await fixture.Update(Snapshot([2, 3, 4, 5], (1, 3), (2, 2), (3, 4)));
            html = await fixture.Html();
            Assert.Equal(new[] { 2 }, OwnedIds(html));
            Assert.Contains("data-owned-title-count=\"4\"", html);
        });
    }

    [Fact]
    public async Task NoneAndUnmappedMetadataStayInAllAndExplicitZeroEffectsRemainVisible()
    {
        await WithLanguage("en", async () =>
        {
            await using var fixture = new InteractiveFixture();
            var html = await fixture.Render(Snapshot([1, 4, 5, 999999]), FilterCatalog());
            Assert.Equal(new[] { 1, 4, 5, 999999 }, OwnedIds(html).Order());
            Assert.Contains("data-title-effect-value=\"0\">0", Row(html, 1));
            Assert.Contains("data-title-effects-status=\"Unknown\"", Row(html, 999999));
            html = await fixture.Click("data-title-category-filter", "Etc");
            Assert.Equal(new[] { 4 }, OwnedIds(html));
            html = await fixture.Click("data-title-origin-filter", "Quest");
            Assert.Empty(OwnedIds(html));
            Assert.Contains("data-title-visible-count=\"0\"", html);
            Assert.Contains("data-owned-title-count=\"4\"", html);
            Assert.Contains("No confirmed titles in this selection.", html);
        });
    }

    [Fact]
    public void UnqualifiedEffectUnitsDoNotBecomeNumericZeroAndLocalizedPercentagesPreserveTheirValue()
    {
        Assert.Equal("—", CombatTitlesPresentation.EffectValue(new("Unknown", "Unbekannt", "Unknown", 0, null), "en"));
        Assert.Null(CombatTitlesPresentation.MachineValue(new("Unknown", "Unbekannt", "Unknown", 5, null)));
        Assert.Equal("4,5 %", CombatTitlesPresentation.EffectValue(new("PvEAmplifyDamage", "PvE", "PvE", 4.5m, "Percent"), "de"));
        Assert.Equal("4.5 %", CombatTitlesPresentation.EffectValue(new("PvEAmplifyDamage", "PvE", "PvE", 4.5m, "Percent"), "en"));
    }

    [Fact]
    public async Task MissingOwnedEffectClickExcludesOwnedCatalogTitlesAndResetReturnsToObservedOwnership()
    {
        await WithLanguage("en", async () =>
        {
            await using var fixture = new InteractiveFixture();
            var html = await fixture.Render(Snapshot([1, 2], (1, 1), (2, 2), (3, 0)), FilterCatalog());
            var slots = EquippedIds(html);
            Assert.Contains("data-missing-passive-total=\"Unknown\"", html);
            Assert.Contains("Missing owned effects", html);
            Assert.DoesNotContain("Missing passive effects", html);
            Assert.Contains("data-owned-passive-value=\"0\"", html);
            await fixture.Click("data-title-category-filter", "Defense");
            await fixture.Click("data-title-origin-filter", "Quest");
            html = await fixture.Click("data-title-passive-filter", "HPMax|Points");
            Assert.Contains("data-title-category=\"All\"", html); Assert.Contains("data-title-origin=\"All\"", html);
            Assert.Contains("data-title-roster-scope=\"CatalogPreview\"", html);
            Assert.Equal(new[] { 3, 4, 5 }, CatalogIds(html).Order());
            Assert.Empty(OwnedIds(html));
            Assert.Equal(3, Regex.Matches(html, "data-title-ownership=\"NotOwned\"").Count);
            Assert.Equal(slots, EquippedIds(html));
            Assert.Contains("3 catalog variants shown", html);
            Assert.DoesNotContain("5 of 2", html);
            Assert.Contains("data-title-matched-passive=\"HPMax|Points\"", html);
            html = await fixture.Click("data-title-clear-passive-filter", "clear");
            Assert.Contains("data-title-roster-scope=\"ObservedOwned\"", html);
            Assert.Equal(new[] { 1, 2 }, CatalogIds(html).Order());
            Assert.DoesNotContain("data-title-passive-selection", html);
        });
    }

    [Fact]
    public async Task PartialOwnershipNeverClassifiesUnobservedCatalogVariantsAsUnownedOrShowsAnExactPassiveSum()
    {
        await WithLanguage("en", async () =>
        {
            await using var fixture = new InteractiveFixture();
            var snapshot = Snapshot([1, 2]);
            snapshot = snapshot with { TitleOwnership = snapshot.TitleOwnership with { Status = NetworkCombatSourceStatus.Partial } };
            var html = await fixture.Render(snapshot, FilterCatalog());
            Assert.Contains("data-owned-passive-status=\"Partial\"", html);
            Assert.DoesNotContain("data-owned-passive-value=\"0\"", html);
            html = await fixture.Click("data-title-passive-filter", "HPMax|Points");
            Assert.Empty(OwnedIds(html));
            Assert.Equal(new[] { 3, 4, 5 }, CatalogIds(html).Order());
            Assert.DoesNotContain("data-title-ownership=\"NotOwned\"", html);
            Assert.Equal(3, Regex.Matches(html, "data-title-ownership=\"Unknown\"").Count);
        });
    }

    [Fact]
    public async Task MatchingCollectionCandidateShowsOnlyItsEstimatedMissingBonusesAndKeepsNativeOwnershipExact()
    {
        await WithLanguage("en", async () =>
        {
            await using var fixture = new InteractiveFixture();
            var catalog = EstimatedCatalog();
            var html = await fixture.Render(Snapshot([1, 4], (1, 1), (2, 0), (3, 4)), catalog, MiniEstimator());
            Assert.Contains("data-title-estimate-status=\"Estimated\"", html);
            Assert.Contains("data-estimated-title-total=\"4\"", html);
            Assert.Contains("data-estimated-missing-title-count=\"2\"", html);
            Assert.Contains("data-owned-title-count=\"2\"", html);
            Assert.Contains("data-missing-passive-estimated-value=\"50\"", html);
            Assert.Equal(new[] { 1, 2, 3, 4 }, CatalogIds(html).Order());
            Assert.Equal(new[] { 1, 4 }, OwnedIds(html).Order());
            Assert.Equal(2, Regex.Matches(html, "data-title-ownership=\"NotOwned\"").Count);
            var slots = EquippedIds(html);
            html = await fixture.Click("data-title-passive-filter", "HPMax|Points");
            Assert.Equal(new[] { 2, 3 }, CatalogIds(html).Order());
            Assert.Empty(OwnedIds(html));
            Assert.DoesNotContain("data-title-id=\"5\"", html);
            Assert.Equal(slots, EquippedIds(html));
            html = await fixture.Click("data-title-category-filter", "Attack");
            Assert.Equal(new[] { 3 }, CatalogIds(html));
            html = await fixture.Click("data-title-origin-filter", "Quest");
            Assert.Empty(CatalogIds(html));
            Assert.Equal(slots, EquippedIds(html));
            html = await fixture.Click("data-title-clear-passive-filter", "clear");
            Assert.Contains("data-title-category=\"All\"", html); Assert.Contains("data-title-origin=\"All\"", html);
            Assert.Contains("data-title-roster-scope=\"EstimatedRoster\"", html);
            Assert.Equal(new[] { 1, 2, 3, 4 }, CatalogIds(html).Order());
        });
    }

    [Theory]
    [InlineData("build")]
    [InlineData("missing-label")]
    public async Task EstimateCannotEscapeMetadataVersionOrCoverageChecks(string variant)
    {
        await using var fixture = new InteractiveFixture();
        var estimator = MiniEstimator(variant == "build" ? 42 : 26425393, variant == "missing-label");
        var html = await fixture.Render(Snapshot([1, 4]), EstimatedCatalog(), estimator);
        Assert.Contains("data-title-estimate-status=\"Unavailable\"", html);
        Assert.Contains("data-missing-passive-total=\"Unknown\"", html);
        Assert.DoesNotContain("data-missing-passive-estimated-value=", html);
        Assert.DoesNotContain("data-estimated-title-total=", html);
        Assert.Equal(new[] { 1, 4 }, CatalogIds(html).Order());
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task SavedCharacterOwnershipIsHistoricalAndDoesNotRestoreLiveCountsOrEquippedSlots(string language)
    {
        await WithLanguage(language, async () =>
        {
            var snapshot = Snapshot([11010001], (1, 11010001)) with { IsFresh = false };
            var saved = Saved([11010001, 11010002]);
            var html = await Render(snapshot, saved);
            Assert.Contains("data-title-ownership-source=\"Saved\"", html);
            Assert.Contains("data-saved-title-count=\"2\"", html);
            Assert.Equal(2, Regex.Matches(html, "data-saved-owned-title=").Count);
            Assert.Contains("data-owned-passive-source=\"Saved\"", html);
            Assert.Contains("data-saved-title-observed-at=\"2026-11-05T12:00:00.0000000+00:00\"", html);
            Assert.Contains(UiText.T("Gespeicherter Titelstand"), html);
            if (language == "en") Assert.DoesNotContain("Saved schedule", html);
            Assert.DoesNotContain("data-owned-title-count", html); Assert.Empty(OwnedIds(html));
            Assert.DoesNotContain("data-equipped-title", html); Assert.DoesNotContain("data-source-observed-at=", html);
            Assert.DoesNotContain(saved.ProfileId, html); Assert.DoesNotContain(saved.Source, html);
            Assert.Contains(UiText.T("Titelbesitz und ausgerüstete Effekte sind charaktergebunden."), html);
            Assert.DoesNotContain("character-independent", html); Assert.DoesNotContain("charakterübergreifend", html);
        });
    }

    [Fact]
    public async Task FreshCompleteOwnershipIncludingConfirmedZeroTakesPrecedenceOverSavedOwnership()
    {
        var saved = Saved([11010002]);
        var html = await Render(Snapshot([11010001]), saved);
        Assert.Contains("data-title-ownership-source=\"Live\"", html);
        Assert.Equal(new[] { 11010001 }, OwnedIds(html));
        Assert.DoesNotContain("data-saved-title-count", html); Assert.DoesNotContain("data-title-saved-stand", html);
        html = await Render(Snapshot([]), saved);
        Assert.Contains("data-owned-title-count=\"0\"", html);
        Assert.DoesNotContain("data-saved-owned-title", html);
    }

    [Fact]
    public async Task PartialLiveOwnershipCanShowMatchingSavedStandWithoutPromotingItToLive()
    {
        await using var fixture = new InteractiveFixture();
        var snapshot = Snapshot([1]);
        snapshot = snapshot with { TitleOwnership = snapshot.TitleOwnership with { Status = NetworkCombatSourceStatus.Partial } };
        var saved = Saved([1, 4]);
        var html = await fixture.Render(snapshot, EstimatedCatalog(), MiniEstimator(), saved);
        Assert.Contains("data-title-ownership-source=\"Saved\"", html);
        Assert.Contains("data-saved-title-count=\"2\"", html);
        Assert.Contains("data-missing-passive-estimated-value=\"50\"", html);
        Assert.DoesNotContain("data-owned-title-count", html); Assert.Empty(OwnedIds(html));
        html = await fixture.Click("data-title-passive-filter", "HPMax|Points");
        Assert.Equal(new[] { 2, 3 }, CatalogIds(html).Order());
        Assert.Equal(2, Regex.Matches(html, "data-title-ownership=\"SavedNotOwned\"").Count);
        Assert.DoesNotContain("data-saved-owned-title", html);
        Assert.Equal(NetworkCombatSourceStatus.Partial, snapshot.TitleOwnership.Status);
        Assert.Equal(new[] { 1 }, snapshot.TitleOwnership.Value!.Order());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavedOwnershipForAnotherKnownProfileIsNeverShown(bool isFresh)
    {
        var snapshot = CharacterCombatSnapshot.Empty with { ProfileId = "different-profile", IsFresh = isFresh };
        var html = await Render(snapshot, Saved([11010001]));
        Assert.Contains("data-title-ownership-source=\"Unknown\"", html);
        Assert.DoesNotContain("data-saved-title-count", html); Assert.DoesNotContain("data-title-saved-stand", html);
        Assert.DoesNotContain("data-saved-owned-title", html); Assert.Empty(CatalogIds(html));
        Assert.Contains("data-source-awaiting-current", html);
    }

    [Fact]
    public async Task SelectedSavedProfileIsReadableWithoutOwnBindingAndSwitchingProfilesResetsFilters()
    {
        await using var fixture = new InteractiveFixture();
        var html = await fixture.Render(CharacterCombatSnapshot.Empty, EstimatedCatalog(), MiniEstimator(), Saved([1, 4], "saved-one"));
        Assert.Contains("data-saved-title-count=\"2\"", html);
        html = await fixture.Click("data-title-passive-filter", "HPMax|Points");
        Assert.Equal(new[] { 2, 3 }, CatalogIds(html).Order());
        await fixture.Click("data-title-category-filter", "Attack");
        await fixture.Click("data-title-origin-filter", "Dungeon");
        await fixture.Update(CharacterCombatSnapshot.Empty, Saved([2, 4], "saved-two"));
        html = await fixture.Html();
        Assert.DoesNotContain("data-title-passive-selection", html);
        Assert.Contains("data-title-category=\"All\"", html); Assert.Contains("data-title-origin=\"All\"", html);
        Assert.Equal(new[] { 1, 2, 3, 4 }, CatalogIds(html).Order());
        Assert.Contains("data-saved-owned-title=\"2\"", html); Assert.Contains("data-saved-owned-title=\"4\"", html);
        Assert.DoesNotContain("data-saved-owned-title=\"1\"", html);
        Assert.DoesNotContain("saved-one", html); Assert.DoesNotContain("saved-two", html);
        await fixture.Update(CharacterCombatSnapshot.Empty);
        html = await fixture.Html();
        Assert.DoesNotContain("data-saved-title-count", html); Assert.Empty(CatalogIds(html));
    }

    [Fact]
    public async Task UnboundIntervalPreservesFiltersWithoutPreservingFreshOwnershipWhenTheSameProfileReturns()
    {
        await using var fixture = new InteractiveFixture();
        var snapshot = Snapshot([1, 4], (1, 1));
        await fixture.Render(snapshot, EstimatedCatalog(), MiniEstimator());
        await fixture.Click("data-title-passive-filter", "HPMax|Points");
        await fixture.Click("data-title-category-filter", "Attack");
        var html = await fixture.Click("data-title-origin-filter", "Dungeon");
        Assert.Equal(new[] { 3 }, CatalogIds(html));

        await fixture.Update(CharacterCombatSnapshot.Empty);
        html = await fixture.Html();
        Assert.Contains("data-title-passive-selection=\"HPMax|Points\"", html);
        Assert.Contains("data-title-category=\"Attack\"", html); Assert.Contains("data-title-origin=\"Dungeon\"", html);
        Assert.Contains("data-title-ownership-source=\"Unknown\"", html);
        Assert.Contains("data-title-estimate-status=\"Unavailable\"", html);
        Assert.DoesNotContain("data-owned-title-count", html); Assert.Empty(OwnedIds(html)); Assert.Empty(EquippedIds(html));
        Assert.Equal(new[] { 3 }, CatalogIds(html));
        Assert.Contains("data-title-ownership=\"Unknown\"", html);

        await fixture.Update(snapshot);
        html = await fixture.Html();
        Assert.Contains("data-title-passive-selection=\"HPMax|Points\"", html);
        Assert.Contains("data-title-category=\"Attack\"", html); Assert.Contains("data-title-origin=\"Dungeon\"", html);
        Assert.Contains("data-title-ownership-source=\"Live\"", html);
        Assert.Contains("data-title-estimate-status=\"Estimated\"", html);
        Assert.Equal(new[] { 3 }, CatalogIds(html));
        Assert.Equal(new[] { 1 }, EquippedIds(html));
    }

    [Fact]
    public async Task ConfirmedDifferentProfileAfterAnUnboundIntervalResetsAllTitleFilters()
    {
        await using var fixture = new InteractiveFixture();
        await fixture.Render(Snapshot([1, 4]), EstimatedCatalog(), MiniEstimator());
        await fixture.Click("data-title-passive-filter", "HPMax|Points");
        await fixture.Click("data-title-category-filter", "Attack");
        await fixture.Click("data-title-origin-filter", "Dungeon");
        await fixture.Update(CharacterCombatSnapshot.Empty);
        var html = await fixture.Html();
        Assert.Contains("data-title-passive-selection=\"HPMax|Points\"", html);

        await fixture.Update(Snapshot([2, 4]) with { ProfileId = "different-profile" });
        html = await fixture.Html();
        Assert.DoesNotContain("data-title-passive-selection", html);
        Assert.Contains("data-title-category=\"All\"", html); Assert.Contains("data-title-origin=\"All\"", html);
        Assert.Equal(new[] { 1, 2, 3, 4 }, CatalogIds(html).Order());
        Assert.Equal(new[] { 2, 4 }, OwnedIds(html).Order());
    }

    [Fact]
    public async Task UnknownOwnershipPreviewNeverClaimsThatUnobservedTitlesAreMissing()
    {
        await using var fixture = new InteractiveFixture();
        var html = await fixture.Render(CharacterCombatSnapshot.Empty, FilterCatalog());
        html = await fixture.Click("data-title-passive-filter", "HPMax|Points");
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, CatalogIds(html).Order());
        Assert.Equal(5, Regex.Matches(html, "data-title-ownership=\"Unknown\"").Count);
        Assert.DoesNotContain("data-title-ownership=\"NotOwned\"", html);
        Assert.Contains("data-missing-passive-total=\"Unknown\"", html);
        Assert.Empty(OwnedIds(html));
    }

    [DiagnosticTheory]
    [Trait("Category", "Diagnostic")]
    [InlineData("de")]
    [InlineData("en")]
    public async Task ExportAnonymousProductionTitlePanel(string language)
    {
        var folder = Path.GetFullPath(TestPrerequisites.RequiredValue("SOULCREST_TITLE_UI_EXPORT"));
        var repository = TestPrerequisites.RepositoryRoot;
        Assert.StartsWith(Path.GetFullPath(Path.Combine(repository, "output", "social-media")) + Path.DirectorySeparatorChar,
            folder, StringComparison.OrdinalIgnoreCase);
        await WithLanguage(language, async () =>
        {
            var catalog = CombatTitleDisplayCatalog.Default;
            using var rosterStream = typeof(CombatTitleRosterEstimate).Assembly.GetManifestResourceStream("Soulcrest.CombatTitleRosterEstimate.json")!;
            using var rosterDocument = JsonDocument.Parse(rosterStream);
            var group = rosterDocument.RootElement.GetProperty("groups").EnumerateArray().Single(row => row.GetProperty("key").GetString() == "dark");
            var blocked = new[] { 12010061, 22010061 };
            var conditional = group.GetProperty("conditionalOwnedIds").EnumerateArray().Select(row => row.GetInt32()).Where(id => !blocked.Contains(id)).Order().ToArray();
            var baseline = group.GetProperty("baseIds").EnumerateArray().Select(row => row.GetInt32()).Where(id => !blocked.Contains(id)).Order().ToList();
            baseline.Add(conditional[0]); // Synthetic replacement of a game-title/private-name homonym; all IDs stay inside one candidate group.
            var examples = new[] { "Attack", "Defense", "Etc" }.Select(category => baseline
                .Where(id => catalog.Find(id) is { } title && title.EquipCategory == category && title.EquippedEffects?.Count >= 2)
                .Order().First()).ToArray();
            var ids = baseline.Order().Take(266).Concat(examples).Distinct().ToList();
            foreach (var id in baseline.Order()) if (ids.Count < 269 && !ids.Contains(id)) ids.Add(id);
            ids.Add(conditional[1]);
            var estimator = CombatTitleRosterEstimate.FromJson(JsonSerializer.Serialize(new
            {
                schema = 1, metadataBuild = catalog.MetadataBuild, qualification = "Candidate", establishesNativeAvailability = false,
                groups = new[] { new { key = "synthetic", baseIds = baseline.ToArray(), conditionalOwnedIds = new[] { conditional[1] } } }
            }));
            var estimate = estimator.Estimate(ids, true);
            Assert.Equal(270, ids.Count); Assert.Equal(318, estimate.TotalCount); Assert.Equal(48, estimate.MissingIds.Count);
            Assert.DoesNotContain(estimate.RosterIds, id => blocked.Contains(id));
            var html = await RenderTitlePanel(Snapshot(ids, (1, examples[0]), (2, examples[1]), (3, examples[2])), catalog, estimator);
            var wwwroot = Path.Combine(repository, "src", "Soulcrest.App", "wwwroot");
            var css = string.Join("\n", new[] { "css/app.css", "combat-score-calculator.css" }
                .Select(file => File.ReadAllText(Path.Combine(wwwroot, file))));
            Directory.CreateDirectory(folder);
            var baseUri = new Uri(wwwroot + Path.DirectorySeparatorChar).AbsoluteUri;
            File.WriteAllText(Path.Combine(folder, "titles-" + language + ".html"),
                "<!doctype html><html lang=\"" + language + "\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">"
                + "<base href=\"" + WebUtility.HtmlEncode(baseUri) + "\"><style>" + css
                + "\nhtml,body{height:auto}body{padding:30px}</style></head><body>" + html + "</body></html>");
        });
    }

    private static string Row(string html, int id) => Regex.Match(html,
        "<article\\b[^>]*data-owned-title=\"" + id + "\"[^>]*>.*?</article>", RegexOptions.Singleline).Value;
    private static int[] OwnedIds(string html) => Regex.Matches(html, "data-owned-title=\"(\\d+)\"")
        .Select(match => int.Parse(match.Groups[1].Value)).ToArray();
    private static int[] EquippedIds(string html) => Regex.Matches(html, "data-equipped-title=\"(\\d+)\"")
        .Select(match => int.Parse(match.Groups[1].Value)).ToArray();
    private static int[] CatalogIds(string html) => Regex.Matches(html, "data-title-id=\"(\\d+)\"")
        .Select(match => int.Parse(match.Groups[1].Value)).ToArray();

    private static CombatTitleDisplayCatalog FilterCatalog()
    {
        CombatTitleDisplayDefinition Title(int id, string name, string category, string origin) => new(id, name, name, category)
        {
            Origin = origin, Grade = "Common", EquippedEffects = [],
            OwnedEffects = [new("HPMax", "LP", "HP", 0, "Points")]
        };
        return CombatTitleDisplayCatalog.FromJson(JsonSerializer.Serialize(new
        {
            schema = 2, metadataBuild = 26425393, presentationOnly = true, doesNotEstablishOwnershipOrEffectApplicability = true,
            titles = new[] { Title(1, "Alpha", "Attack", "Quest"), Title(2, "Beta", "Defense", "Quest"),
                Title(3, "Gamma", "Attack", "Dungeon"), Title(4, "Delta", "Etc", "Item"), Title(5, "Epsilon", "None", "Growth") }
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    private static CombatTitleDisplayCatalog EstimatedCatalog() => CombatTitleDisplayCatalog.FromJson(JsonSerializer.Serialize(new
    {
        schema = 2, metadataBuild = 26425393, presentationOnly = true, doesNotEstablishOwnershipOrEffectApplicability = true,
        titles = FilterCatalog().Titles.Values.Select(title => title with { OwnedEffects = [new("HPMax", "LP", "HP", title.Id * 10, "Points")] })
    }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    private static CombatTitleRosterEstimate MiniEstimator(int build = 26425393, bool missingLabel = false) =>
        CombatTitleRosterEstimate.FromJson(JsonSerializer.Serialize(new
        {
            schema = 1, metadataBuild = build, qualification = "Candidate", establishesNativeAvailability = false,
            groups = new[] { new { key = "example", baseIds = missingLabel ? new[] { 1, 2, 3, 999999 } : new[] { 1, 2, 3 }, conditionalOwnedIds = new[] { 4, 5 } } }
        }));

    private static CharacterCombatSnapshot Snapshot(IEnumerable<int> owned, params (byte Slot, int Id)[] equipped) => new()
    {
        ProfileId = "TITLE-PRIVATE-PROFILE", IsFresh = true, ClassId = "Cleric", Generation = 7, Sequence = 1,
        TitleOwnership = new(owned.ToImmutableHashSet(), NetworkCombatSourceStatus.Complete, true, At, 1, "own-title-list"),
        Titles = new(new(owned.ToImmutableHashSet(), equipped.ToImmutableDictionary(row => row.Slot, row => row.Id)),
            NetworkCombatSourceStatus.Complete, true, At, 1, "own-title-list")
    };

    private static ProfileTitleSnapshot Saved(IEnumerable<int> owned, string profileId = "TITLE-PRIVATE-PROFILE") =>
        new(profileId, owned.ToImmutableHashSet(), At.AddDays(-1), "TITLE-PRIVATE-HISTORY-SOURCE");

    private static async Task WithLanguage(string language, Func<Task> action)
    {
        var previous = UiText.Language; UiText.Language = language;
        try { await action(); } finally { UiText.Language = previous; }
    }

    private static async Task<string> Render(CharacterCombatSnapshot snapshot, ProfileTitleSnapshot? saved = null)
    {
        var services = new ServiceCollection(); services.AddLogging();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<CombatSourceCategoryView>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(CombatSourceCategoryView.Section)] = "titles", [nameof(CombatSourceCategoryView.Snapshot)] = snapshot,
                [nameof(CombatSourceCategoryView.SavedTitles)] = saved
            })));
        return WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
    }

    private static async Task<string> RenderTitlePanel(CharacterCombatSnapshot snapshot, CombatTitleDisplayCatalog catalog,
        CombatTitleRosterEstimate estimator)
    {
        var services = new ServiceCollection(); services.AddLogging();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<CombatTitlesView>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(CombatTitlesView.Snapshot)] = snapshot, [nameof(CombatTitlesView.Catalog)] = catalog,
                [nameof(CombatTitlesView.RosterEstimator)] = estimator
            })));
        return WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
    }

    private sealed class InteractiveFixture : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly HtmlRenderer _renderer;
        private HtmlRootComponent _root;
        private CapturedTitles? _view;
        public InteractiveFixture()
        {
            var services = new ServiceCollection(); services.AddLogging(); _provider = services.BuildServiceProvider();
            _renderer = new(_provider, _provider.GetRequiredService<ILoggerFactory>());
        }
        public async Task<string> Render(CharacterCombatSnapshot snapshot, CombatTitleDisplayCatalog catalog, CombatTitleRosterEstimate? estimator = null, ProfileTitleSnapshot? saved = null)
        {
            var parameters = new Dictionary<string, object?>
            {
                [nameof(CapturedTitles.Snapshot)] = snapshot, [nameof(CapturedTitles.Catalog)] = catalog,
                [nameof(CapturedTitles.SavedTitles)] = saved,
                [nameof(CapturedTitles.Captured)] = (Action<CapturedTitles>)(view => _view = view)
            };
            if (estimator is not null) parameters[nameof(CapturedTitles.RosterEstimator)] = estimator;
            _root = await _renderer.Dispatcher.InvokeAsync(() => _renderer.RenderComponentAsync<CapturedTitles>(ParameterView.FromDictionary(parameters)));
            return await Html();
        }
        public Task<string> Html() => _renderer.Dispatcher.InvokeAsync(() => WebUtility.HtmlDecode(_root.ToHtmlString()));
        public Task<string> Click(string attribute, string value) => _renderer.Dispatcher.InvokeAsync(async () =>
        { await _view!.Click(attribute, value); return WebUtility.HtmlDecode(_root.ToHtmlString()); });
        public Task Update(CharacterCombatSnapshot snapshot, ProfileTitleSnapshot? saved = null) => _renderer.Dispatcher.InvokeAsync(() => _view!.Update(snapshot, saved));
        public async ValueTask DisposeAsync() { await _renderer.DisposeAsync(); await _provider.DisposeAsync(); }
    }

    public sealed class CapturedTitles : CombatTitlesView
    {
        [Parameter] public Action<CapturedTitles>? Captured { get; set; }
        protected override void OnInitialized() => Captured?.Invoke(this);
        internal void Update(CharacterCombatSnapshot snapshot, ProfileTitleSnapshot? saved) { Snapshot = snapshot; SavedTitles = saved; OnParametersSet(); StateHasChanged(); }
#pragma warning disable BL0006 // Native keyboard and pointer activation share the production button callback.
        public async Task Click(string attribute, string value)
        {
            using var tree = new RenderTreeBuilder();
            typeof(CombatTitlesView).GetMethod("BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(this, [tree]);
            var frames = tree.GetFrames();
            var index = Enumerable.Range(0, frames.Count).Single(i => frames.Array[i].FrameType == RenderTreeFrameType.Attribute
                && frames.Array[i].AttributeName == attribute && Equals(frames.Array[i].AttributeValue, value));
            while (frames.Array[index].FrameType != RenderTreeFrameType.Element) index--;
            var callback = Enumerable.Range(index + 1, frames.Array[index].ElementSubtreeLength - 1).Select(i => frames.Array[i])
                .Single(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "onclick").AttributeValue;
            if (callback is EventCallback<Microsoft.AspNetCore.Components.Web.MouseEventArgs> typed) await typed.InvokeAsync(new());
            else if (callback is EventCallback untyped) await untyped.InvokeAsync();
            else if (callback is Action synchronous) synchronous();
            else if (callback is Func<Task> asynchronous) await asynchronous();
            else throw new InvalidOperationException("Unexpected production title-filter callback.");
            StateHasChanged();
        }
#pragma warning restore BL0006
    }
}
