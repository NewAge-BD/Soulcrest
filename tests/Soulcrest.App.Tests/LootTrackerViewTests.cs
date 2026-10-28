using System.Collections.Immutable;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components;
using Soulcrest.App.Services;
using Xunit;
using MouseEventArgs = Microsoft.AspNetCore.Components.Web.MouseEventArgs;

namespace Soulcrest.App.Tests;

/// <summary>Exercises the production Razor view with inert, synthetic snapshots; no source is started.</summary>
[Collection("Settings file")]
public sealed class LootTrackerViewTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 25, 13, 30, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task AbyssSessionShowsPointMetricsWithoutReusingDailyKinaOrInventingUnknownValues(string language)
    {
        await using var fixture = new Fixture(language);
        var session = new LootTrackerSession
        {
            Id = "synthetic-abyss", Kind = LootTrackerSessionKind.Abyss, Status = LootTrackerSessionStatus.Live,
            StartedAt = At, LastPresenceAt = At.AddMinutes(20), Elapsed = TimeSpan.FromMinutes(20),
            Character = new(FirstProfileId, ExampleCharacter("A")), Location = "Abyss",
            AbyssPoints = 6_000, RankPointsGained = 500, RankPointsLost = 900,
            MinuteIncome = [new(0, null, null) { AbyssPoints = 6_000 }], KinaUnbound = 100
        };
        var html = await fixture.Render(new([session], At), sourceAvailable: true);
        Assert.Equal(language == "de" ? "6.000" : "6,000", Metric(html, "abyss-points"));
        Assert.Equal(language == "de" ? "18.000" : "18,000", Metric(html, "abyss-point-rate"));
        Assert.Equal("-400", Metric(html, "rank-points-net"));
        Assert.Contains("data-kind=\"abyss\"", html);
        Assert.DoesNotContain("Limited Kina Session", html);
        Assert.Contains(UiText.T("AP-Farmstand unbekannt"), WebUtility.HtmlDecode(html));
        await fixture.Export(await fixture.Render(new([session], At), isExample: true), "abyss");
        await fixture.SettingsPeriod(AbyssPointRatePeriod.Minute);
        html = await fixture.Render(new([session with { AbyssPoints = null, RankPointsGained = null, RankPointsLost = null }], At));
        Assert.Equal("—", Metric(html, "abyss-points"));
        Assert.Equal("—", Metric(html, "abyss-point-rate"));
        Assert.Equal("—", Metric(html, "rank-points-net"));
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task OpenWorldPointsRemainVisibleAcrossMixedAndPveViewsWithoutChangingThePlayheadOrSharedRates(string language)
    {
        await using var fixture = new Fixture(language);
        var parent = LootTrackerSessionProjectionTests.OpenWorldPointsExample();
        var html = await fixture.Render(new([parent], parent.EndedAt), isExample: true);
        Assert.Equal("20", Metric(html, "abyss-points")); Assert.Equal("20", Metric(html, "abyss-point-rate"));
        Assert.Equal("10", Metric(html, "rank-points-gained")); Assert.Equal("3", Metric(html, "rank-points-lost"));
        Assert.Equal("7", Metric(html, "rank-points-net"));
        Assert.Contains(UiText.T("Bezogen auf die gesamte Sessionzeit"), WebUtility.HtmlDecode(html));
        Assert.Contains(UiText.T("AP-Farmstand unbekannt"), WebUtility.HtmlDecode(html));
        Assert.Contains("data-timeline-layer=\"ap\"", html); Assert.Contains("data-timeline-layer=\"rp\"", html);
        Assert.Equal("20", Metric(html, "timeline-value-ap")); Assert.Equal("7", Metric(html, "timeline-value-rp"));
        Assert.DoesNotContain("data-combat-category=", html);
        await fixture.Export(html, "open-world-points-mixed");
        await fixture.Seek(TimeSpan.FromMinutes(25));
        html = await fixture.Click("data-session-view", "pve");
        Assert.Equal("25:00", Metric(html, "playhead-time"));
        Assert.Equal("20", Metric(html, "abyss-points")); Assert.Equal("20", Metric(html, "abyss-point-rate"));
        Assert.Equal("7", Metric(html, "rank-points-net"));
        Assert.Equal("—", Metric(html, "timeline-value-ap"));
        Assert.Contains("data-timeline-series=\"ap\"", html); Assert.Contains("data-timeline-series=\"rp\"", html);
        await fixture.Export(html, "open-world-points-pve");
        foreach (var excluded in new[] { "lifeskill", "abyss" })
        {
            html = await fixture.Click("data-session-view", excluded);
            Assert.Equal("25:00", Metric(html, "playhead-time"));
            Assert.DoesNotContain("data-testid=\"abyss-points\"", html);
            Assert.DoesNotContain("data-testid=\"rank-points-net\"", html);
            Assert.DoesNotContain("data-timeline-layer=\"ap\"", html); Assert.DoesNotContain("data-timeline-layer=\"rp\"", html);
        }
        html = await fixture.Render(new([LootTrackerSessionProjectionTests.Example()], parent.EndedAt), isExample: true);
        Assert.DoesNotContain("data-testid=\"abyss-points\"", html);
        Assert.DoesNotContain("data-testid=\"rank-points-net\"", html);
    }
    [Fact]
    public async Task SharedPlayheadChangesMapAndItsPrefixWhileWholeSessionStatisticsRemainComplete()
    {
        await using var fixture = new Fixture("en");
        var session = new LootTrackerSession
        {
            Id = "replay-parent", Kind = LootTrackerSessionKind.Abyss, Status = LootTrackerSessionStatus.Completed, EndedAt = At.AddSeconds(10),
            StartedAt = At, Elapsed = TimeSpan.FromSeconds(10), AbyssPoints = 100, TimelineVersion = 1,
            RouteSegments = [new("map-a", "Map A", [new(100, 100, At, TimeSpan.Zero), new(200, 200, At.AddSeconds(5), TimeSpan.FromSeconds(5))]),
                new("map-b", "Map B", [new(500, 500, At.AddSeconds(10), TimeSpan.FromSeconds(10))])],
            CombatEvents = [new("kill", LootTrackerCombatKind.Kill, At.AddSeconds(3), TimeSpan.FromSeconds(3), new("map-a", "Map A", 150, 150, At.AddSeconds(3))) { OpponentClassId = "Ranger" },
                new("death", LootTrackerCombatKind.Death, At.AddSeconds(9), TimeSpan.FromSeconds(9), new("map-b", "Map B", 490, 490, At.AddSeconds(9))) { OpponentClassId = "Templar" }]
        };
        var html = await fixture.Render(new([session], At));
        Assert.Contains("data-map-id=\"map-b\"", html);
        Assert.Equal("00:10", Metric(html, "playhead-time"));
        Assert.True(html.IndexOf("data-testid=\"session-replay-map\"", StringComparison.Ordinal) < html.IndexOf("data-testid=\"session-timeline\"", StringComparison.Ordinal));
        await fixture.Seek(TimeSpan.FromSeconds(5)); html = await fixture.CurrentMarkup();
        Assert.Contains("data-map-id=\"map-a\"", html);
        Assert.Equal("00:05", Metric(html, "playhead-time"));
        var beforeTimeline = html[..html.IndexOf("data-testid=\"session-timeline\"", StringComparison.Ordinal)];
        Assert.DoesNotContain("data-marker-kind=\"death\"", beforeTimeline);
        Assert.Contains("data-combat-category=\"Templar\"", html);
        Assert.Equal("100", Metric(html, "abyss-points"));
        await fixture.UpdateSnapshot(new([session with { Elapsed = TimeSpan.FromSeconds(15) }], At.AddSeconds(15)));
        Assert.Equal("00:05", Metric(await fixture.CurrentMarkup(), "playhead-time"));
        await fixture.FollowEnd();
        Assert.Equal("00:15", Metric(await fixture.CurrentMarkup(), "playhead-time"));
        await fixture.Seek(TimeSpan.FromSeconds(2));
        await fixture.UpdateSnapshot(new([session with { Id = "second-session", StartedAt = At.AddMinutes(1) }], At.AddMinutes(1)));
        Assert.Equal("00:10", Metric(await fixture.CurrentMarkup(), "playhead-time"));
    }

    private const string FirstProfileId = "11111111111111111111111111111111";
    private const string SecondProfileId = "22222222222222222222222222222222";
    private const string ThirdProfileId = "33333333333333333333333333333333";
    private static string ExampleCharacter(string suffix) => (UiText.Language == "de" ? "Beispielcharakter " : "Example character ") + suffix;

    internal static LootTrackerSnapshot ExampleSnapshot() => new([
        new()
        {
            Id = "sample-grind-live", Kind = LootTrackerSessionKind.Grind, Status = LootTrackerSessionStatus.Live,
            Location = "Immortal Isles", StartedAt = At.AddMinutes(-30), LastLootAt = At.AddSeconds(-25),
            Elapsed = TimeSpan.FromMinutes(30), Character = new(FirstProfileId, ExampleCharacter("A"), 1840, 235),
            KinaBound = 120_000, KinaUnbound = 360_000,
            MonsterCount = 98, ItemCount = 36, SoulCount = 17,
            Mobs = [new("sample-kuru", "Kuru Worker", 86), new("sample-other", "Sample monster", 12)],
            Loot = [new("sample-material", "Sample material", LootTrackerLootKind.Item, 32, "sample-kuru"),
                new("sample-soul", "Sample soul", LootTrackerLootKind.Soul, 17, "sample-kuru"),
                new("sample-unassigned", "Unassigned sample item", LootTrackerLootKind.Item, 4)],
            RouteSegments = [new("sample-isles", "Immortal Isles", [
                new(120, 470), new(150, 360), new(240, 310), new(300, 210), new(410, 190),
                new(500, 250), new(530, 380), new(450, 490), new(310, 520), new(200, 480)])],
            MinuteIncome = Enumerable.Range(0, 30).Select(minute => new LootTrackerIncomeBin(minute,
                3000L + minute % 3 * 1000L, 6000L + minute % 6 * 2400L)).ToImmutableArray(),
        },
        new()
        {
            Id = "sample-dungeon", Kind = LootTrackerSessionKind.Dungeon, Status = LootTrackerSessionStatus.Completed,
            Location = "Altgard", DungeonName = "Fire Temple", StartedAt = At.AddHours(-2),
            EndedAt = At.AddMinutes(-96), LastLootAt = At.AddMinutes(-97), Elapsed = TimeSpan.FromMinutes(24),
            Character = new(SecondProfileId, ExampleCharacter("B"), 2020, 280),
            KinaBound = 80_000, KinaUnbound = 160_000, MonsterCount = 42, ItemCount = 24, SoulCount = 5,
            Mobs = [new("sample-dungeon-mob", "Sample dungeon monster", 42)],
            Loot = [new("sample-dungeon-item", "Sample dungeon item", LootTrackerLootKind.Item, 24),
                new("sample-dungeon-soul", "Sample dungeon soul", LootTrackerLootKind.Soul, 5)],
            MinuteIncome = [new(0, 1000, 2000), new(8, 4000, 6000), new(16, 5000, 8000), new(23, 70_000, 144_000)],
        },
        new()
        {
            Id = "sample-grind-completed", Kind = LootTrackerSessionKind.Grind,
            Status = LootTrackerSessionStatus.Completed, Location = "Verteron", StartedAt = At.AddHours(-4),
            EndedAt = At.AddHours(-3), Elapsed = TimeSpan.FromHours(1), KinaBound = 0, KinaUnbound = 95_000,
            Character = new(FirstProfileId, ExampleCharacter("A"), 1700, 215),
            MonsterCount = 28, ItemCount = 15, SoulCount = 0,
            Mobs = [new("sample-earlier", "Sample monster", 28)],
            Loot = [new("sample-earlier-item", "Earlier sample item", LootTrackerLootKind.Item, 15)],
        },
        new()
        {
            Id = "sample-lifeskill", Kind = LootTrackerSessionKind.Lifeskill,
            Status = LootTrackerSessionStatus.Completed, Location = "Altgard", StartedAt = At.AddHours(-5),
            EndedAt = At.AddHours(-5).AddMinutes(18), Elapsed = TimeSpan.FromMinutes(18),
            Character = new(ThirdProfileId, ExampleCharacter("C"), 1780, 220),
            HarvestCount = 4, FailedHarvestCount = 2, LastLootAt = At.AddHours(-5).AddMinutes(18),
            KinaBound = 0, KinaUnbound = 0, MonsterCount = 0, ItemCount = 38, SoulCount = 0,
            Loot = [new("sample-plant-material", "Sample plant material", LootTrackerLootKind.Item, 24),
                new("sample-mineral", "Sample mineral", LootTrackerLootKind.Item, 14)],
            RouteSegments = [new("sample-altgard", "Altgard", [
                new(100, 450), new(180, 330), new(320, 260), new(420, 170), new(510, 250), new(470, 410)])],
            ResourceNodes = [new("sample-plant-one", "Sample plant", "sample-altgard", "Altgard", 100, 450, 0, 0),
                new("sample-ore-one", "Sample ore", "sample-altgard", "Altgard", 320, 260, 0, 2),
                new("sample-plant-two", "Sample plant", "sample-altgard", "Altgard", 420, 170, 0, 3),
                new("sample-ore-two", "Sample ore", "sample-altgard", "Altgard", 470, 410, 0, 5)],
        },
    ], At);

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task ProductionViewCanRenderAnEmptyUnconnectedSource(string language)
    {
        await using var fixture = new Fixture(language);
        var html = await fixture.Render(LootTrackerSnapshot.Empty);
        Assert.DoesNotContain("data-session-id=", html);
        Assert.DoesNotContain("sample-grind", html);
        await fixture.Export(html, "empty");
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task SyntheticSnapshotShowsRouteAndSeparateDungeonSessions(string language)
    {
        await using var fixture = new Fixture(language);
        var html = await fixture.Render(ExampleSnapshot(), isExample: true);
        Assert.Contains("Immortal Isles", html);
        Assert.Contains("Fire Temple", html);
        Assert.Contains("data-session-id=\"sample-grind-live\"", html);
        Assert.Contains("data-session-id=\"sample-dungeon\"", html);
        Assert.Equal(ExampleCharacter("A"), Metric(html, "character-name"));
        Assert.Equal(language == "de" ? "1.840" : "1,840", Metric(html, "combat-score"));
        Assert.Equal("235", Metric(html, "gear-score"));
        Assert.Contains(language == "de" ? "Beispiel" : "Example", html, StringComparison.OrdinalIgnoreCase);
        await fixture.Export(html, "example");
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task RenderedCharacterFilterUsesStableProfilesAndSearchIncludesCharacterNames(string language)
    {
        await using var fixture = new Fixture(language);
        await fixture.Render(ExampleSnapshot(), isExample: true);
        var first = await fixture.Change("loot-character-filter", FirstProfileId);
        Assert.Equal(2, SessionButtonCount(first));
        Assert.Contains("data-session-id=\"sample-grind-live\"", first);
        Assert.Contains("data-session-id=\"sample-grind-completed\"", first);
        Assert.DoesNotContain("data-session-id=\"sample-dungeon\"", first);

        var second = await fixture.Change("loot-character-filter", SecondProfileId);
        Assert.Equal(1, SessionButtonCount(second));
        Assert.Contains("data-session-id=\"sample-dungeon\"", second);
        Assert.Equal(ExampleCharacter("B"), Metric(second, "character-name"));
        await fixture.Change("loot-character-filter", "all");
        var searched = await fixture.Change("loot-session-search", ExampleCharacter("B").ToLowerInvariant(), "oninput");
        Assert.Equal(1, SessionButtonCount(searched));
        Assert.Contains("data-session-id=\"sample-dungeon\"", searched);
        Assert.DoesNotContain("data-session-id=\"sample-grind-live\"", searched);
    }

    [Fact]
    public async Task IdenticalCharacterNamesRemainSeparateAndLegacySessionsCanBeFiltered()
    {
        await using var fixture = new Fixture("en");
        var common = new LootTrackerSession { Location = "Sample place", StartedAt = At };
        var first = common with { Id = "first", Character = new(FirstProfileId, "Example character") };
        var second = common with { Id = "second", Character = new(SecondProfileId, "Example character") };
        var legacy = common with { Id = "legacy" };
        var invalidProfile = common with { Id = "invalid-profile", Character = new("untrusted profile", "Example unassigned") };
        await fixture.Render(new([first, second, legacy, invalidProfile], At));
        var firstOnly = await fixture.Change("loot-character-filter", FirstProfileId);
        Assert.Equal(1, SessionButtonCount(firstOnly));
        Assert.Contains("data-session-id=\"first\"", firstOnly);
        var secondOnly = await fixture.Change("loot-character-filter", SecondProfileId);
        Assert.Equal(1, SessionButtonCount(secondOnly));
        Assert.Contains("data-session-id=\"second\"", secondOnly);
        var unassigned = await fixture.Change("loot-character-filter", "unassigned");
        Assert.Equal(2, SessionButtonCount(unassigned));
        Assert.Contains("data-session-id=\"legacy\"", unassigned);
        Assert.Contains("data-session-id=\"invalid-profile\"", unassigned);
        Assert.DoesNotContain("data-session-id=\"first\"", unassigned);
        Assert.DoesNotContain("data-session-id=\"second\"", unassigned);
    }

    [Fact]
    public async Task SessionsForTheSameCharacterShowTheirOwnStartScores()
    {
        await using var fixture = new Fixture("en");
        await fixture.Render(ExampleSnapshot(), isExample: true);
        var earlier = await fixture.Click("data-session-id", "sample-grind-completed");
        Assert.Equal(ExampleCharacter("A"), Metric(earlier, "character-name"));
        Assert.Equal("1,700", Metric(earlier, "combat-score"));
        Assert.Equal("215", Metric(earlier, "gear-score"));
        var later = await fixture.Click("data-session-id", "sample-grind-live");
        Assert.Equal(ExampleCharacter("A"), Metric(later, "character-name"));
        Assert.Equal("1,840", Metric(later, "combat-score"));
        Assert.Equal("235", Metric(later, "gear-score"));
        earlier = await fixture.Click("data-session-id", "sample-grind-completed");
        Assert.Equal("1,700", Metric(earlier, "combat-score"));
        Assert.Equal("215", Metric(earlier, "gear-score"));
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task RenderedButtonsFilterAndSelectSessionsAndSearchChangesTheVisibleResults(string language)
    {
        await using var fixture = new Fixture(language);
        await fixture.Render(ExampleSnapshot(), isExample: true);
        var dungeon = await fixture.Click("data-kind", "dungeon");
        Assert.Equal(1, SessionButtonCount(dungeon));
        Assert.Contains("data-session-id=\"sample-dungeon\"", dungeon);
        Assert.Contains("Sample dungeon item", dungeon);
        Assert.Contains(language == "de" ? ">Abgeschlossen</span>" : ">Completed</span>", dungeon);
        Assert.DoesNotContain("Sample material", dungeon);
        await fixture.Export(dungeon, "dungeon");

        var all = await fixture.Click("data-kind", "all");
        Assert.Equal(4, SessionButtonCount(all));
        var selected = await fixture.Click("data-session-id", "sample-grind-completed");
        Assert.Contains("Earlier sample item", selected);
        Assert.DoesNotContain("Sample material", selected);

        var otherMonster = await fixture.Change("loot-session-search", "Sample monster", "oninput");
        Assert.Equal(2, SessionButtonCount(otherMonster));
        Assert.Contains("data-session-id=\"sample-grind-live\"", otherMonster);
        Assert.Contains("data-session-id=\"sample-grind-completed\"", otherMonster);
        var search = await fixture.Change("loot-session-search", "Kuru", "oninput");
        Assert.Equal(1, SessionButtonCount(search));
        Assert.Contains("data-session-id=\"sample-grind-live\"", search);
        Assert.Contains("Sample material", search);
        var noMatch = await fixture.Change("loot-session-search", "No matching sample", "oninput");
        Assert.Equal(0, SessionButtonCount(noMatch));
        Assert.Contains("lt-sidebar-empty", noMatch);
    }

    private static LootTrackerSession SizedSession(string id, int drops, LootTrackerSessionStatus status = LootTrackerSessionStatus.Completed, int minute = 0, string location = "Altgard") => new()
    {
        Id = id, Kind = LootTrackerSessionKind.Mixed, UnifiedVersion = 1, TimelineVersion = 1, Status = status,
        Location = location, StartedAt = At.AddMinutes(minute), Elapsed = TimeSpan.FromMinutes(5),
        Character = new(FirstProfileId, ExampleCharacter("A")), KinaUnbound = 1_000L * (drops + 1), ItemCount = drops, SoulCount = 0,
        TimelineEvents = [.. Enumerable.Range(0, drops).Select(index => new LootTrackerTimelineEvent($"{id}-{index}",
            LootTrackerTimelineEventKind.Drop, At.AddMinutes(minute).AddSeconds(index * 20), TimeSpan.FromSeconds(index * 20)))],
    };

    private static LootTrackerSnapshot SizedSnapshot() => new([
        SizedSession("large", 6, minute: 40, location: "Immortal Isles"),
        SizedSession("single-node", 1, minute: 30, location: "Verteron"),
        SizedSession("two-kills", 2, minute: 20, location: "Eltnen"),
        SizedSession("live-start", 1, LootTrackerSessionStatus.Live, minute: 50, location: "Altgard"),
        new() { Id = "legacy-unknown", Location = "Verteron", StartedAt = At.AddMinutes(10), Status = LootTrackerSessionStatus.Completed },
    ], At.AddHours(1));

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task SmallSessionFilterHidesTinySessionsPersistsAndKeepsDayTotalsComplete(string language)
    {
        await using var fixture = new Fixture(language);
        var all = await fixture.Render(SizedSnapshot(), onDelete: _ => Task.CompletedTask);
        Assert.Equal(5, SessionButtonCount(all));
        var unboundTotal = Regex.Match(all, "data-day-unbound=\"[^\"]+\">([^<]*)<").Groups[1].Value;

        var hidden = await fixture.Change("loot-hide-small-sessions", true);
        Assert.True(new SettingsService().Current.LootTrackerHideSmallSessions);
        Assert.Equal(3, SessionButtonCount(hidden));
        Assert.Contains("data-session-id=\"large\"", hidden);
        Assert.Contains("data-session-id=\"live-start\"", hidden);
        Assert.Contains("data-session-id=\"legacy-unknown\"", hidden);
        Assert.DoesNotContain("data-session-id=\"single-node\"", hidden);
        Assert.DoesNotContain("data-session-id=\"two-kills\"", hidden);
        Assert.Equal(UiText.F("{0} ausgeblendet", 2), Metric(hidden, "small-session-count"));
        Assert.Contains(UiText.F("+{0} klein", 2), WebUtility.HtmlDecode(hidden));
        Assert.Equal(unboundTotal, Regex.Match(hidden, "data-day-unbound=\"[^\"]+\">([^<]*)<").Groups[1].Value);
        Assert.Contains("class=\"lt-count\">3 / 5<", hidden);
        await fixture.Export(hidden, "small-sessions-hidden");

        var strict = await fixture.Change("loot-small-session-max", "1");
        Assert.Equal(1, new SettingsService().Current.LootTrackerSmallSessionMaxActivities);
        Assert.Equal(4, SessionButtonCount(strict));
        Assert.Contains("data-session-id=\"two-kills\"", strict);
        Assert.DoesNotContain("data-session-id=\"single-node\"", strict);

        var shown = await fixture.Change("loot-hide-small-sessions", false);
        Assert.False(new SettingsService().Current.LootTrackerHideSmallSessions);
        Assert.Equal(5, SessionButtonCount(shown));
        Assert.Equal(UiText.F("{0} klein", 1), Metric(shown, "small-session-count"));
    }

    [Fact]
    public async Task OnlySmallSessionsLeaveTheDayHeaderAndExplainTheEmptyList()
    {
        await using var fixture = new Fixture("en");
        fixture.Settings.Current.LootTrackerHideSmallSessions = true;
        var html = await fixture.Render(new([SizedSession("single-node", 1)], At));
        Assert.Equal(0, SessionButtonCount(html));
        Assert.Contains("data-day-small=", html);
        Assert.Contains("Only small sessions here (1 hidden).", html);
    }

    [Fact]
    public async Task BulkDeleteNeedsConfirmationAndDeletesOnlyFinishedSmallSessionsOfTheCurrentFilter()
    {
        await using var fixture = new Fixture("en");
        fixture.Settings.Current.LootTrackerHideSmallSessions = true;
        var snapshot = SizedSnapshot();
        var deleted = new List<string>();
        await fixture.Render(snapshot, onDelete: async id =>
        {
            deleted.Add(id);
            snapshot = snapshot with { Sessions = snapshot.Sessions.Where(row => row.Id != id).ToImmutableArray() };
            await fixture.UpdateSnapshot(snapshot);
        });
        var search = await fixture.Change("loot-session-search", "Eltnen", "oninput");
        Assert.Contains("Delete small (1)", search);
        await fixture.Change("loot-session-search", "", "oninput");
        var confirm = await fixture.Click("id", "loot-delete-small-sessions");
        Assert.Contains("Permanently delete 2 small sessions?", confirm);
        Assert.Empty(deleted);
        var cancelled = await fixture.Click("id", "loot-cancel-small-delete");
        Assert.DoesNotContain("loot-confirm-small-delete", cancelled);
        Assert.Empty(deleted);

        await fixture.Click("id", "loot-delete-small-sessions");
        var html = await fixture.Click("id", "loot-confirm-small-delete");
        Assert.Equal(["single-node", "two-kills"], deleted);
        Assert.Contains("2 small sessions deleted.", html);
        Assert.Contains("Delete small (0)", html);
        Assert.Contains("data-session-id=\"live-start\"", html);
        Assert.Contains("data-session-id=\"large\"", html);
        Assert.Contains("data-session-id=\"legacy-unknown\"", html);
    }

    [Fact]
    public async Task BulkDeleteFailureStopsAndARetryContinuesAfterTheLastDeletedSession()
    {
        await using var fixture = new Fixture("en");
        fixture.Settings.Current.LootTrackerHideSmallSessions = true;
        var deleted = new List<string>();
        var fail = true;
        await fixture.Render(SizedSnapshot(), onDelete: id =>
        {
            if (id == "two-kills" && fail) throw new IOException("synthetic storage failure");
            deleted.Add(id); return Task.CompletedTask;
        });
        await fixture.Click("id", "loot-delete-small-sessions");
        var html = await fixture.Click("id", "loot-confirm-small-delete");
        Assert.Equal(["single-node"], deleted);
        Assert.Contains("Deletion stopped after 1 of 2 sessions.", html);
        fail = false;
        html = await fixture.Click("id", "loot-confirm-small-delete");
        Assert.Equal(["single-node", "two-kills"], deleted);
        Assert.Contains("2 small sessions deleted.", html);
    }

    [Fact]
    public async Task ExampleViewsChangeTheSmallFilterLocallyAndCannotBulkDelete()
    {
        await using var fixture = new Fixture("en");
        var deleted = new List<string>();
        await fixture.Render(SizedSnapshot(), isExample: true, onDelete: id => { deleted.Add(id); return Task.CompletedTask; });
        var html = await fixture.Change("loot-hide-small-sessions", true);
        Assert.Equal(3, SessionButtonCount(html));
        Assert.False(fixture.Settings.Current.LootTrackerHideSmallSessions);
        Assert.Matches("id=\"loot-delete-small-sessions\"[^>]*disabled", html);
        html = await fixture.Click("id", "loot-delete-small-sessions");
        Assert.DoesNotContain("loot-confirm-small-delete", html);
        Assert.Empty(deleted);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task RouteThumbnailNeedsNoRenderedReplayAndReplayMapOffersZoom(string language)
    {
        await using var fixture = new Fixture(language);
        var live = await fixture.Render(ExampleSnapshot(), isExample: true);
        Assert.Contains("data-testid=\"replay-recording\"", live);
        var thumbnail = Regex.Match(live, "data-testid=\"session-route-thumbnail\".*?</svg>", RegexOptions.Singleline);
        Assert.True(thumbnail.Success);
        Assert.Contains("data-map-id=\"sample-isles\"", thumbnail.Value);
        Assert.Contains("lt-route-path", thumbnail.Value);
        Assert.DoesNotContain("data-replay-zoom", live);
        Assert.DoesNotContain("lt-route-legend", thumbnail.Value);
        await fixture.Export(live, "route-thumbnail");

        var replay = await fixture.Click("data-session-id", "sample-lifeskill");
        Assert.DoesNotContain("data-testid=\"session-route-thumbnail\"", replay);
        Assert.Contains("data-testid=\"session-replay-map\"", replay);
        Assert.Contains("data-replay-zoom=\"1\"", replay);
        Assert.Matches("data-replay-zoom-reset[^>]*disabled", replay);
        Assert.Contains(UiText.T("Hineinzoomen"), WebUtility.HtmlDecode(replay));
        await fixture.Export(replay, "replay-zoom");
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task LifeskillFilterShowsCharacterLootAndResourceVisitsInTheirRouteOrder(string language)
    {
        await using var fixture = new Fixture(language);
        await fixture.Render(ExampleSnapshot(), isExample: true);
        var html = await fixture.Click("data-kind", "lifeskill");
        Assert.Equal(1, SessionButtonCount(html));
        Assert.Contains("data-session-id=\"sample-lifeskill\"", html);
        Assert.Equal(ExampleCharacter("C"), Metric(html, "character-name"));
        Assert.Equal("220", Metric(html, "gear-score"));
        Assert.Equal("4", Metric(html, "resource-count"));
        Assert.Equal("4", Metric(html, "harvest-count"));
        Assert.Equal("2", Metric(html, "failed-harvest-count"));
        Assert.Equal("38", Metric(html, "item-count"));
        Assert.NotEqual("—", Metric(html, "item-rate"));
        Assert.DoesNotContain("data-testid=\"kina-total\"", html);
        Assert.DoesNotContain("data-testid=\"soul-count\"", html);
        Assert.Contains(language == "de" ? ">Abgeschlossen</span>" : ">Completed</span>", html);
        Assert.Contains("Sample plant material", html);
        Assert.Contains("Sample mineral", html);
        Assert.Contains(UiText.T("Ernten pro Stunde"), WebUtility.HtmlDecode(html));
        Assert.Contains(language == "de" ? "Fehlversuche" : "Failed attempts", html);
        foreach (var order in Enumerable.Range(1, 4)) Assert.Contains($"data-node-order=\"{order}\"", html);
        Assert.DoesNotContain("data-node-order=\"5\"", html);
        html = await fixture.Click("data-session-view", "lifeskill");
        Assert.Matches("id=\"loot-save-route\"[^>]*disabled", html);
        await fixture.Export(html, "lifeskill");
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task FailureOnlySessionShowsConfirmedZeroLootAndSeparateFailedAttempts(string language)
    {
        await using var fixture = new Fixture(language);
        var session = ExampleSnapshot().Sessions.Single(row => row.Kind == LootTrackerSessionKind.Lifeskill) with
        {
            Id = "sample-lifeskill-failed-only", StartedAt = At.AddMinutes(-8), EndedAt = At.AddMinutes(-5),
            Elapsed = TimeSpan.FromMinutes(3), LastLootAt = null,
            HarvestCount = 0, FailedHarvestCount = 3, ItemCount = 0, Loot = [], ResourceNodes = [],
        };
        var html = await fixture.Render(new([session], At), isExample: true);
        Assert.Equal("0", Metric(html, "harvest-count"));
        Assert.Equal("3", Metric(html, "failed-harvest-count"));
        Assert.Equal("0", Metric(html, "item-count"));
        Assert.Equal("0", Metric(html, "item-rate"));
        Assert.Equal("—", Metric(html, "resource-count"));
        Assert.Equal(language == "de" ? "Keine Sammelbeute erhalten." : "No gathering loot received.",
            Metric(html, "gathering-loot-empty"));
        Assert.DoesNotContain("lt-resource-bars", html);
        Assert.DoesNotContain("lt-loot-name", html);
        Assert.DoesNotContain("Sample plant material", html);
        Assert.DoesNotContain("Sample mineral", html);
        Assert.DoesNotContain(language == "de" ? "Zuletzt gesammelt" : "Last gathered", html);
        Assert.Contains(UiText.T("Bestätigte Beute und Sammelversuche gehören außerhalb des Abyss zu einer gemeinsamen Session. Beide verlängern ihren Timer; das Ausblenden des Overlays pausiert die Aufnahme nicht."),
            WebUtility.HtmlDecode(html));
        await fixture.Export(html, "lifeskill-failed-only");
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task LegacyLifeskillFailureCountAndUnknownLootAreNotInvented(string language)
    {
        await using var fixture = new Fixture(language);
        var session = ExampleSnapshot().Sessions.Single(row => row.Kind == LootTrackerSessionKind.Lifeskill) with
        {
            HarvestCount = null, FailedHarvestCount = null, ItemCount = null, Loot = [], LastLootAt = null,
        };
        var html = await fixture.Render(new([session], At));
        foreach (var metric in new[] { "harvest-count", "failed-harvest-count", "item-count", "item-rate" })
            Assert.Equal("—", Metric(html, metric));
        Assert.Equal(language == "de" ? "Noch keine bestätigte Sammelbeute vorhanden." : "No confirmed gathering loot is available yet.",
            Metric(html, "gathering-loot-empty"));

        html = await fixture.Render(new([session with { HarvestCount = 0, FailedHarvestCount = 0, ItemCount = 0 }], At));
        Assert.Equal("0", Metric(html, "failed-harvest-count"));
    }

    [Fact]
    public async Task MissingIncomeAndCountsStayUnknownWhileAConfirmedZeroRemainsZero()
    {
        await using var fixture = new Fixture("en");
        var unknown = new LootTrackerSession { Id = "unknown", Location = "Sample place", StartedAt = At, Elapsed = TimeSpan.FromMinutes(10) };
        var html = await fixture.Render(new([unknown], At));
        foreach (var metric in new[] { "kina-total", "kina-rate", "combat-score", "gear-score", "monster-count", "soul-count" })
            Assert.Equal("—", Metric(html, metric));

        var zero = unknown with { KinaBound = 0, KinaUnbound = 0, Character = new(FirstProfileId, "Example character", 0, 0), MonsterCount = 0, SoulCount = 0, ItemCount = 0 };
        html = await fixture.Render(new([zero], At));
        foreach (var metric in new[] { "kina-total", "kina-rate", "combat-score", "gear-score", "monster-count", "soul-count" })
            Assert.Equal("0", Metric(html, metric));
        html = await fixture.Render(new([zero with { Elapsed = TimeSpan.Zero }], At));
        Assert.Equal("—", Metric(html, "kina-rate"));
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task ConfirmedUnboundGrindIncomeIsVisibleWhileBoundRewardsRemainUnknown(string language)
    {
        await using var fixture = new Fixture(language);
        var session = new LootTrackerSession
        {
            Id = "synthetic-unbound-grind", Kind = LootTrackerSessionKind.Grind, StartedAt = At, Status = LootTrackerSessionStatus.Completed, EndedAt = At.AddMinutes(10),
            Elapsed = TimeSpan.FromMinutes(10), KinaUnbound = 2_000, KinaBound = null, CoverageLimited = true,
            MinuteIncome = [new(0, null, 100), new(1, null, 200)]
        };
        var html = await fixture.Render(new([session], At));
        Assert.Equal(language == "de" ? "2.000" : "2,000", Metric(html, "kina-total"));
        Assert.Equal(language == "de" ? "12.000" : "12,000", Metric(html, "kina-rate"));
        Assert.Equal("—", Metric(html, "kina-bound"));
        Assert.Contains(UiText.T("Ungebundene Kina"), WebUtility.HtmlDecode(html));
        Assert.Contains(UiText.T("Nur bestätigte ungebundene Kina"), WebUtility.HtmlDecode(html));
        Assert.Contains("lst-curve", html);
        Assert.Contains("lt-coverage", html);
        Assert.Null(session.KinaBound);
        Assert.Null(LootTrackerPresentation.TotalKina(session));
        await fixture.Export(html, "unbound-monster-session");
    }

    [Fact]
    public async Task ACharacterWithUnknownStartScoresNeverBorrowsALegacyOrAnotherSessionsScore()
    {
        await using var fixture = new Fixture("en");
        var legacy = new LootTrackerSession { Id = "legacy-score", Location = "Sample place", StartedAt = At, CombatScore = 900 };
        var html = await fixture.Render(new([legacy], At));
        Assert.Equal("900", Metric(html, "combat-score"));
        Assert.Equal("—", Metric(html, "gear-score"));
        var character = legacy with { Id = "unknown-character-score", Character = new(FirstProfileId, "Example character") };
        html = await fixture.Render(new([character], At));
        Assert.Equal("—", Metric(html, "combat-score"));
        Assert.Equal("—", Metric(html, "gear-score"));
    }

    [Fact]
    public async Task SparseKnownIncomeBinsRenderVisibleMarkersWithoutJoiningTheMissingMinutes()
    {
        await using var fixture = new Fixture("en");
        var dungeon = ExampleSnapshot().Sessions.Single(session => session.Id == "sample-dungeon");
        var html = await fixture.Render(new([dungeon], At), isExample: true);
        Assert.Contains("data-testid=\"timeline-legacy\"", html);
        Assert.Equal(4, Regex.Matches(html, "data-curve=\"kina\"").Count);
        Assert.Equal(4, Regex.Matches(html, "data-area=\"kina\"").Count);
        Assert.Single(Regex.Matches(html, "data-timeline-plot=\"combined\""));
        Assert.DoesNotContain("NaN", html);
        Assert.DoesNotContain("Infinity", html);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task IdleTimeoutUsesTheRenderedChangeCallbackAndInvalidInputLeavesTheSavedDurationAlone(string language)
    {
        await using var fixture = new Fixture(language);
        await fixture.Render(LootTrackerSnapshot.Empty);
        var changed = await fixture.Change("loot-idle-timeout", "7");
        Assert.Equal(7, fixture.Settings.Current.LootTrackerIdleTimeoutMinutes);
        Assert.Equal(7, new SettingsService().Current.LootTrackerIdleTimeoutMinutes);
        Assert.Contains("lt-save-feedback", changed);
        Assert.DoesNotContain("lt-save-feedback error", changed);
        foreach (var invalid in new[] { "0", "61", "not a number" })
        {
            var rejected = await fixture.Change("loot-idle-timeout", invalid);
            Assert.Equal(7, fixture.Settings.Current.LootTrackerIdleTimeoutMinutes);
            Assert.Equal(7, new SettingsService().Current.LootTrackerIdleTimeoutMinutes);
            Assert.Contains("lt-save-feedback error", rejected);
        }
    }

    [Fact]
    public async Task SessionAndLootLabelsAreEncodedAsText()
    {
        await using var fixture = new Fixture("en");
        var hostileLabel = "<img src=x onerror=alert(1)>";
        var session = new LootTrackerSession
        {
            Id = "text-only", Location = hostileLabel, StartedAt = At, Character = new(FirstProfileId, hostileLabel),
            Loot = [new("text-only-item", hostileLabel, LootTrackerLootKind.Item, 1)],
        };
        var html = await fixture.Render(new([session], At));
        Assert.DoesNotContain(hostileLabel, html);
        Assert.Contains("&lt;img", html);
        Assert.Equal(hostileLabel, Metric(html, "character-name"));
    }

    private static int SessionButtonCount(string html) => Regex.Matches(html, "data-session-id=").Count;

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task HistoricalFarmStopsRemainSeparateFromTheTravelledRoute(string language)
    {
        await using var fixture = new Fixture(language);
        var session = ExampleSnapshot().Sessions.Single(row => row.Kind == LootTrackerSessionKind.Lifeskill) with
        {
            FarmRouteVersion = 1, RouteSegments = [], ResourceNodes = [],
            GatherVisits = [new("visit-one", 0, "sample-altgard", "Altgard", 100, 450, 100, 450, At.AddHours(-5).AddMinutes(5), true, "Sample plant"),
                new("visit-middle", 0, "sample-altgard", "Altgard", 260, 350, 260, 350, At.AddHours(-5).AddMinutes(10), false, "Sample ore", GatherableId: 7002, CatalogNodeId: "sample-catalogue"),
                new("visit-last", 0, "sample-altgard", "Altgard", 420, 170, 420, 170, At.AddHours(-5).AddMinutes(15), true, "Sample plant")],
            FarmRoutePoints = [new("stop-one", 0, "sample-altgard", "Altgard", 100, 450, "Sample plant", 2, 1, 1),
                new("stop-middle", 0, "sample-altgard", "Altgard", 260, 350, "Sample ore", 1, 0, 1, "sample-catalogue"),
                new("stop-last", 0, "sample-altgard", "Altgard", 420, 170, "Sample plant", 3, 3, 0)],
        };
        var html = await fixture.Render(new([session], At));
        Assert.Equal("3", Metric(html, "resource-count"));
        Assert.Contains(UiText.T("Gelaufene Route"), WebUtility.HtmlDecode(html));
        Assert.Contains(language == "de" ? ">Sammelstellen</h3>" : ">Gathering stops</h3>", html);
        Assert.Contains("data-testid=\"legacy-route-note\"", html);
        Assert.DoesNotContain("sample-ore-two", html);
        Assert.Contains("data-farm-stop-id=\"stop-middle\"", html);
        Assert.Contains("data-node-order=\"3\"", html);
        Assert.DoesNotContain("data-node-order=\"4\"", html);
        Assert.Contains(UiText.T("Erfolge und Fehlversuche"), WebUtility.HtmlDecode(html));
        Assert.Contains(UiText.T("Besuche"), WebUtility.HtmlDecode(html));
        await fixture.Export(html, "farm-route");
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task LegacyLifeskillWalkIsMarkedAndCannotBeExported(string language)
    {
        await using var fixture = new Fixture(language);
        var session = ExampleSnapshot().Sessions.Single(row => row.Kind == LootTrackerSessionKind.Lifeskill);
        var html = await fixture.Render(new([session], At), viewMode: LootTrackerViewMode.Lifeskill);
        Assert.Contains("data-testid=\"legacy-route-note\"", html);
        Assert.Contains(UiText.T("Altaufzeichnung: Laufweg ohne gespeicherte Sammelpositionen."), WebUtility.HtmlDecode(html));
        Assert.Contains(UiText.T("Altaufzeichnungen enthalten keine exportierbaren Sammelpositionen."), WebUtility.HtmlDecode(html));
        Assert.Matches("id=\"loot-save-route\"[^>]*disabled", html);
    }

    [Fact]
    public async Task DeleteNeedsConfirmationCanBeCancelledAndUsesASiblingIconButton()
    {
        await using var fixture = new Fixture("en");
        var deleted = new List<string>();
        var html = await fixture.Render(ExampleSnapshot(), onDelete: id => { deleted.Add(id); return Task.CompletedTask; });
        Assert.Equal(4, Regex.Matches(html, "data-delete-session-id=").Count);
        Assert.DoesNotMatch("<button[^>]*data-session-id[^>]*>(?:(?!</button>).)*<button", html);
        html = await fixture.Click("data-delete-session-id", "sample-grind-live");
        Assert.Contains("End and permanently delete this live session?", html);
        Assert.Empty(deleted);
        html = await fixture.Click("data-cancel-delete-id", "sample-grind-live");
        Assert.DoesNotContain("data-confirm-delete-id=", html);
        Assert.Empty(deleted);
    }

    [Fact]
    public async Task DeletingTheSelectedRowChoosesTheNextVisibleSessionAndKeepsFilters()
    {
        await using var fixture = new Fixture("en");
        var snapshot = ExampleSnapshot();
        var deleted = new List<string>();
        await fixture.Render(snapshot, onDelete: async id =>
        {
            deleted.Add(id);
            snapshot = snapshot with { Sessions = snapshot.Sessions.Where(row => row.Id != id).ToImmutableArray() };
            await fixture.UpdateSnapshot(snapshot);
        });
        await fixture.Change("loot-character-filter", FirstProfileId);
        await fixture.Click("data-delete-session-id", "sample-grind-live");
        var html = await fixture.Click("data-confirm-delete-id", "sample-grind-live");
        Assert.Equal(["sample-grind-live"], deleted);
        Assert.Equal(1, SessionButtonCount(html));
        Assert.DoesNotContain("data-session-id=\"sample-grind-live\"", html);
        Assert.DoesNotContain("data-session-id=\"sample-dungeon\"", html);
        Assert.Matches("data-session-id=\"sample-grind-completed\"[^>]*aria-pressed", html);
        Assert.Contains("Earlier sample item", html);
        Assert.Contains("Session deleted.", html);
    }

    [Fact]
    public async Task DeleteFailureKeepsTheSessionAndOffersAVisibleRetry()
    {
        await using var fixture = new Fixture("en");
        await fixture.Render(ExampleSnapshot(), onDelete: _ => throw new IOException("synthetic storage failure"));
        await fixture.Click("data-delete-session-id", "sample-dungeon");
        var html = await fixture.Click("data-confirm-delete-id", "sample-dungeon");
        Assert.Equal(4, SessionButtonCount(html));
        Assert.Contains("data-session-id=\"sample-dungeon\"", html);
        Assert.Contains("The session could not be deleted.", html);
        Assert.Contains("data-confirm-delete-id=\"sample-dungeon\"", html);
        Assert.DoesNotContain("Session deleted.", html);
    }

    [Fact]
    public async Task DeletingTheLastVisibleSessionSelectsItsPreviousNeighbourAfterADelayedSnapshot()
    {
        await using var fixture = new Fixture("en");
        var snapshot = ExampleSnapshot();
        await fixture.Render(snapshot, onDelete: _ => Task.CompletedTask);
        await fixture.Change("loot-character-filter", FirstProfileId);
        await fixture.Click("data-session-id", "sample-grind-completed");
        await fixture.Click("data-delete-session-id", "sample-grind-completed");
        await fixture.Click("data-confirm-delete-id", "sample-grind-completed");
        await fixture.UpdateSnapshot(snapshot with
        {
            Sessions = snapshot.Sessions.Where(row => row.Id != "sample-grind-completed").ToImmutableArray(),
        });
        var html = await fixture.CurrentMarkup();
        Assert.Equal(1, SessionButtonCount(html));
        Assert.Contains("data-session-id=\"sample-grind-live\"", html);
        Assert.Contains("Sample material", html);
        Assert.DoesNotContain("Earlier sample item", html);
        Assert.DoesNotContain("data-session-id=\"sample-dungeon\"", html);
    }

    [Fact]
    public async Task ASessionSelectedDuringDeletionRemainsSelectedWhenTheSnapshotArrives()
    {
        await using var fixture = new Fixture("en");
        var snapshot = ExampleSnapshot();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await fixture.Render(snapshot, onDelete: async id =>
        {
            started.SetResult(); await finish.Task;
            await fixture.UpdateSnapshot(snapshot with
            {
                Sessions = snapshot.Sessions.Where(row => row.Id != id).ToImmutableArray(),
            });
        });
        await fixture.Click("data-delete-session-id", "sample-grind-live");
        var pending = fixture.Click("data-confirm-delete-id", "sample-grind-live");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Click("data-session-id", "sample-grind-completed");
        finish.SetResult(); await pending;
        var html = await fixture.CurrentMarkup();
        Assert.Contains("Earlier sample item", html);
        Assert.DoesNotContain("Sample dungeon item", html);
        Assert.DoesNotContain("data-session-id=\"sample-grind-live\"", html);
    }

    [Fact]
    public async Task ExampleSessionDeleteButtonsAreDisabledAndCallbacksRemainInert()
    {
        await using var fixture = new Fixture("en");
        var deleted = new List<string>();
        var html = await fixture.Render(ExampleSnapshot(), isExample: true,
            onDelete: id => { deleted.Add(id); return Task.CompletedTask; });
        Assert.Equal(4, Regex.Matches(html, "data-delete-session-id=\"[^\"]+\"[^>]*disabled").Count);
        html = await fixture.Click("data-delete-session-id", "sample-grind-live");
        Assert.DoesNotContain("data-confirm-delete-id=", html);
        Assert.Empty(deleted);
    }

    [Fact]
    public async Task DeleteStaysBusyWhileTheCallbackRunsAndCannotBeTriggeredTwice()
    {
        await using var fixture = new Fixture("en");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await fixture.Render(ExampleSnapshot(), onDelete: async _ => { calls++; started.SetResult(); await finish.Task; });
        await fixture.Click("data-delete-session-id", "sample-dungeon");
        var pendingClick = fixture.Click("data-confirm-delete-id", "sample-dungeon");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var busy = await fixture.CurrentMarkup();
        Assert.Matches("data-confirm-delete-id=\"sample-dungeon\"[^>]*disabled", busy);
        Assert.Contains("Deleting", busy);
        await fixture.Click("data-confirm-delete-id", "sample-dungeon");
        Assert.Equal(1, calls);
        finish.SetResult();
        await pendingClick;
    }

    [Fact]
    public async Task LifeskillTogglePersistsThroughItsActualRenderedCallbackAndExamplesStayInert()
    {
        await using var fixture = new Fixture("en");
        fixture.Settings.Current.LifeskillTrackingEnabled = false;
        await fixture.Render(LootTrackerSnapshot.Empty);
        var enabled = await fixture.Change("loot-lifeskill-enabled", true);
        Assert.True(fixture.Settings.Current.LifeskillTrackingEnabled);
        Assert.True(new SettingsService().Current.LifeskillTrackingEnabled);
        Assert.Contains("Lifeskill recording enabled.", enabled);
        await fixture.Change("loot-lifeskill-enabled", false);
        Assert.False(new SettingsService().Current.LifeskillTrackingEnabled);

        var example = await fixture.Render(ExampleSnapshot(), isExample: true);
        Assert.Matches("id=\"loot-lifeskill-enabled\"[^>]*disabled", example);
        await fixture.Change("loot-lifeskill-enabled", true);
        Assert.False(fixture.Settings.Current.LifeskillTrackingEnabled);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task MonsterRecordingAndItsLootOverlayPersistIndependentlyAndExamplesStayInert(string language)
    {
        await using var fixture = new Fixture(language);
        fixture.Settings.Current.MonsterLootTrackingEnabled = false;
        fixture.Settings.Current.LootOverlayEnabled = false;
        fixture.Settings.Current.LifeskillTrackingEnabled = false;
        fixture.Settings.Current.OverlayEnabled = false;
        await fixture.Render(LootTrackerSnapshot.Empty);
        var enabled = await fixture.Change("loot-monster-enabled", true);
        Assert.True(new SettingsService().Current.MonsterLootTrackingEnabled);
        Assert.False(fixture.Settings.Current.LootOverlayEnabled);
        Assert.False(fixture.Settings.Current.LifeskillTrackingEnabled);
        Assert.False(fixture.Settings.Current.OverlayEnabled);
        Assert.Contains(UiText.T("Open-World-PvE-Aufnahme eingeschaltet."), WebUtility.HtmlDecode(enabled));
        enabled = await fixture.Change("loot-overlay-enabled", true);
        Assert.True(new SettingsService().Current.LootOverlayEnabled);
        Assert.True(fixture.Settings.Current.MonsterLootTrackingEnabled);
        Assert.Contains(UiText.T("Loot-Overlay eingeschaltet."), WebUtility.HtmlDecode(enabled));
        await fixture.Change("loot-monster-enabled", false);
        Assert.False(new SettingsService().Current.MonsterLootTrackingEnabled);
        Assert.True(fixture.Settings.Current.LootOverlayEnabled);
        await fixture.Change("loot-overlay-enabled", false);
        Assert.False(new SettingsService().Current.LootOverlayEnabled);

        var example = await fixture.Render(ExampleSnapshot(), isExample: true);
        Assert.Matches("id=\"loot-monster-enabled\"[^>]*disabled", example);
        Assert.Matches("id=\"loot-overlay-enabled\"[^>]*disabled", example);
        await fixture.Change("loot-monster-enabled", true);
        await fixture.Change("loot-overlay-enabled", true);
        Assert.False(fixture.Settings.Current.MonsterLootTrackingEnabled);
        Assert.False(fixture.Settings.Current.LootOverlayEnabled);
    }

    [Fact]
    public async Task UnverifiedGatheringStatusIsVisibleAndUnknownNodeCountsAreNotInvented()
    {
        await using var fixture = new Fixture("en");
        var session = ExampleSnapshot().Sessions.Single(row => row.Kind == LootTrackerSessionKind.Lifeskill)
            with { ResourceNodes = [], HarvestCount = 2, ItemCount = 2 };
        var html = await fixture.Render(new([session], At), sourceStatus: "Sammelbeute ist noch nicht verifiziert. Lifeskill-Sessions warten auf den Netzwerkabgleich.");
        Assert.Contains("Gathering loot is not verified yet.", html);
        Assert.Equal("2", Metric(html, "harvest-count"));
        Assert.Equal("—", Metric(html, "resource-count"));
        Assert.DoesNotContain("data-node-order=", html);
    }
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task ConfirmedPrimaryLootShowsItsCountWhileUnknownNodeMappingStaysVisible(string language)
    {
        await using var fixture = new Fixture(language);
        fixture.Settings.Current.LifeskillTrackingEnabled = true;
        var session = ExampleSnapshot().Sessions.Single(row => row.Kind == LootTrackerSessionKind.Lifeskill)
            with { ResourceNodes = [], HarvestCount = 1, ItemCount = 2,
                Loot = [new("item:610660025", LifeskillItemNameCatalog.Default.Name(610660025), LootTrackerLootKind.Item, 2)] };
        var html = await fixture.Render(new([session], At), sourceAvailable: true,
            sourceStatus: "Sammelbeute wird über das Netzwerk erfasst.");
        Assert.Equal("1", Metric(html, "harvest-count"));
        Assert.Equal("2", Metric(html, "item-count"));
        Assert.Equal("—", Metric(html, "resource-count"));
        Assert.Contains(LifeskillItemNameCatalog.Default.Name(610660025), html);
        Assert.DoesNotContain("data-node-order=", html);
        Assert.Contains(language == "de" ? "Sammelbeute wird über das Netzwerk erfasst."
            : "Gathering loot is being tracked through the network.", WebUtility.HtmlDecode(html));
        await fixture.Export(html, "confirmed-primary");
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task DropTableAndKinaMetricsUseExactBundledIconsWithoutNameGuesses(string language)
    {
        await using var fixture = new Fixture(language);
        var session = ExampleSnapshot().Sessions[0] with
        {
            Loot = [
                new("Item:210350009", MonsterLootItemCatalog.Default.Name(210350009, language), LootTrackerLootKind.Item, 1),
                new("item:210350009", "Synthetic gathering label", LootTrackerLootKind.Item, 2),
                new("SoulCrystal:2", "Synthetic crystal label", LootTrackerLootKind.Soul, 3),
                new("PetSoul:1004", "Synthetic pet label", LootTrackerLootKind.Soul, 4),
                new("unsupported-key", "Chaos Helm", LootTrackerLootKind.Item, 5)
            ]
        };
        var html = await fixture.Render(new([session], At), isExample: true);
        var catalog = MonsterLootIconCatalog.Default;
        var kinaIcons = 2;
        foreach (var (path, count) in new[]
        {
            (catalog.ItemAsset(210350009), 4), (catalog.SoulCrystalAsset(2), 2),
            (catalog.PetSoulAsset(1004), 2), (catalog.ItemAsset(930100031), kinaIcons)
        })
        {
            Assert.NotNull(path);
            Assert.Matches("^loot-icons/[a-z0-9_-]+\\.png$", path);
            Assert.Equal(count, Regex.Matches(html, "src=\"" + Regex.Escape(path) + "\"").Count);
        }
        Assert.Contains("<span class=\"lt-loot-icon\" aria-hidden=\"true\"></span>", html);
        Assert.DoesNotContain("◇", html);
        Assert.DoesNotContain("✧", html);
        Assert.DoesNotContain("src=\"http", html);
        Assert.Equal(language == "de" ? "480.000" : "480,000", Metric(html, "kina-total"));
        await fixture.Export(html, "icons");
    }

    [Fact]
    public async Task GatheringQuantityBarsAndDropTableShareTheSameExactItemIcon()
    {
        await using var fixture = new Fixture("en");
        var session = ExampleSnapshot().Sessions.Single(row => row.Kind == LootTrackerSessionKind.Lifeskill) with
        {
            Loot = [new("item:210350009", "Synthetic gathering label", LootTrackerLootKind.Item, 2)]
        };
        var html = await fixture.Render(new([session], At), isExample: true);
        var path = MonsterLootIconCatalog.Default.ItemAsset(210350009);
        Assert.NotNull(path);
        Assert.Equal(2, Regex.Matches(html, "src=\"" + Regex.Escape(path) + "\"").Count);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task FarmDaysCollapseWithoutChangingSessionSelectionAndUnknownTotalsStayUnknown(string language)
    {
        await using var fixture = new Fixture(language);
        var earlier = ExampleSnapshot().Sessions.Single(row => row.Id == "sample-dungeon") with
        {
            StartedAt = new(2026, 10, 25, 6, 59, 59, TimeSpan.Zero), KinaBound = null, KinaUnbound = null,
        };
        var later = ExampleSnapshot().Sessions[0] with
        {
            StartedAt = new(2026, 10, 25, 7, 0, 0, TimeSpan.Zero), Mode = LootTrackerSessionMode.LimitedKina,
            KinaBound = null, KinaUnbound = 0, MinuteIncome = [], MonsterCount = null, ItemCount = 0, SoulCount = 2,
            Loot = [new("SoulCrystal:2", "Synthetic crystal", LootTrackerLootKind.Soul, 2)],
        };
        var html = await fixture.Render(new([earlier, later], At), isExample: true);
        Assert.Equal(2, Regex.Matches(html, "data-farm-day=").Count);
        Assert.Contains("data-farm-day=\"2026-10-24\"", html);
        Assert.Contains("data-farm-day=\"2026-10-25\"", html);
        Assert.Contains("data-day-unbound=\"2026-10-24\">&#x2014;", html);
        Assert.Contains("data-testid=\"limited-kina-mode\">Limited Kina Session", html);
        html = await fixture.Click("data-session-id", earlier.Id);
        Assert.Equal(ExampleCharacter("B"), Metric(html, "character-name"));
        html = await fixture.Click("data-toggle-farm-day", "2026-10-24");
        Assert.Matches("data-toggle-farm-day=\"2026-10-24\"[^>]*aria-expanded=\"false\"", html);
        Assert.Equal(ExampleCharacter("B"), Metric(html, "character-name"));
        html = await fixture.Click("data-session-id", later.Id);
        Assert.Equal(ExampleCharacter("A"), Metric(html, "character-name"));
        Assert.Equal(2, SessionButtonCount(html));
        await fixture.Export(html, "pve-limited");
    }

    [Fact]
    public async Task RouteButtonInspectsTheRecordedSelectedMapAndExamplesRemainInert()
    {
        await using var fixture = new Fixture("en");
        var session = ExampleSnapshot().Sessions[0] with { Location = "Immortal Isle", LocationApproximate = true, Status = LootTrackerSessionStatus.Completed, EndedAt = At };
        await fixture.Render(new([session], At));
        await fixture.Click("id", "loot-show-route-on-map");
        Assert.Equal("map", fixture.Ui.Tab);
        var focus = fixture.Ui.TakePendingLootRouteFocus();
        Assert.NotNull(focus); Assert.Equal("Immortal Isle", focus.Name);
        Assert.Equal("sample-isles", focus.MapId);
        Assert.Equal(session.RouteSegments.Select(segment => segment.MapId), focus.Segments.Select(segment => segment.MapId));
        Assert.Equal(session.RouteSegments[0].Points.ToArray(), focus.Segments[0].Points.ToArray());
        var example = await fixture.Render(new([session], At), isExample: true);
        Assert.Matches("id=\"loot-show-route-on-map\"[^>]*disabled", example);
        Assert.Contains("Approximate location", example);
        await fixture.Click("id", "loot-show-route-on-map");
        Assert.Null(fixture.Ui.TakePendingLootRouteFocus());
    }

    [Fact]
    public async Task OverlayOptionsSaveTransparencyAndAutoShowThroughRenderedControls()
    {
        await using var fixture = new Fixture("en");
        fixture.Settings.Current.LootOverlayShowOnSessionStart = false;
        fixture.Settings.Current.LootOverlayEnabled = false;
        await fixture.Render(LootTrackerSnapshot.Empty);
        await fixture.Change("loot-overlay-auto-show", true);
        await fixture.Change("loot-background-transparency", "65");
        Assert.Equal(.35, new SettingsService().Current.LootOverlayBackgroundOpacity, 10);
        Assert.True(new SettingsService().Current.LootOverlayShowOnSessionStart);
        Assert.False(fixture.Settings.Current.LootOverlayEnabled);
    }

    [Fact]
    public async Task DropOptionsUseSavedCallbacksAndNeverStartAnAudioPreview()
    {
        await using var fixture = new Fixture("en");
        fixture.Settings.Current.LootDropAlertsEnabled = true;
        fixture.Settings.Current.LootDropSoundEnabled = true;
        fixture.Settings.Current.LootDropAnimationsEnabled = true;
        fixture.Settings.Current.LootDropSoundVolume = .6;
        await fixture.Render(LootTrackerSnapshot.Empty);
        await fixture.Change("loot-drop-alerts-enabled", false);
        await fixture.Change("loot-drop-sound-enabled", false);
        var html = await fixture.Change("loot-drop-animations-enabled", false);
        Assert.Contains("lt-motion-disabled", html);
        var settings = new SettingsService().Current;
        Assert.False(settings.LootDropAlertsEnabled);
        Assert.False(settings.LootDropSoundEnabled);
        Assert.False(settings.LootDropAnimationsEnabled);
        await fixture.Change("loot-drop-sound-enabled", true);
        await fixture.Change("loot-drop-volume", "35");
        Assert.Equal(.35, new SettingsService().Current.LootDropSoundVolume);
        await fixture.Change("loot-drop-volume", "101");
        Assert.Equal(.35, new SettingsService().Current.LootDropSoundVolume);
        var example = await fixture.Render(ExampleSnapshot(), isExample: true);
        Assert.Matches("id=\"loot-drop-alerts-enabled\"[^>]*disabled", example);
        Assert.Matches("id=\"loot-drop-volume\"[^>]*disabled", example);
        await fixture.Change("loot-drop-alerts-enabled", true);
        Assert.False(fixture.Settings.Current.LootDropAlertsEnabled);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task ExceptionalLootRowsUseCatalogRarity(string language)
    {
        await using var fixture = new Fixture(language);
        const int spectralGreaves = 210230006;
        var snapshot = ExampleSnapshot().Sessions[0] with
        {
            Loot = [
                new("Item:" + spectralGreaves, MonsterLootItemCatalog.Default.Name(spectralGreaves, language), LootTrackerLootKind.Item, 1)
                {
                },
                new("Item:514310110", "Synthetic epic", LootTrackerLootKind.Item, 1),
                new("Item:310900001", "Synthetic special", LootTrackerLootKind.Item, 1),
                new("unsupported-key", "Spectral Greaves", LootTrackerLootKind.Item, 1),
            ],
        };
        var html = await fixture.Render(new([snapshot], At), isExample: true);
        Assert.Contains("lt-loot-row rarity-unique", html);
        Assert.Contains("lt-loot-row rarity-epic", html);
        Assert.Contains("lt-loot-row rarity-special", html);
        Assert.DoesNotContain("lt-loot-row rarity-mythic", html);
        Assert.Matches("class=\"lt-loot-row \" data-loot-id=\"unsupported-key\"", html);
        Assert.Equal(language == "de" ? "480.000" : "480,000", Metric(html, "kina-total"));
        await fixture.Export(html, "rare-loot");

        int[] qualityItems = [610530001, 610530005, 610530008, 610530010];
        var qualitySnapshot = snapshot with
        {
            Loot = qualityItems.Select(id => new LootTrackerLoot("Item:" + id,
                MonsterLootItemCatalog.Default.Name(id, language), LootTrackerLootKind.Item, 1)).ToImmutableArray(),
            ItemCount = 4, SoulCount = 0, KinaUnbound = 0, KinaBound = null,
            Mobs = [], MonsterCount = null, MinuteIncome = [], RouteSegments = [], Location = "Altgard",
        };
        var qualityHtml = await fixture.Render(new([qualitySnapshot], At), isExample: true);
        await fixture.Export(qualityHtml, "quality-colors");
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task ExactLootRowsRenderByRarityThenQuantityInsteadOfEncounterOrder(string language)
    {
        await using var fixture = new Fixture(language);
        int[] ids = [110160001, 110150003, 110140002, 610530001, 110160008, 110150004, 110140003, 610530005, 610530008, 210230006];
        long[] quantities = [2, 2, 1, 9, 1, 4, 3, 10, 5, 1];
        var session = ExampleSnapshot().Sessions[0] with
        {
            Loot = ids.Select((id, index) => new LootTrackerLoot("Item:" + id,
                MonsterLootItemCatalog.Default.Name(id, language), LootTrackerLootKind.Item, quantities[index])).ToImmutableArray(),
        };
        var html = await fixture.Render(new([session], At), isExample: true);
        var displayedIds = Regex.Matches(html, "data-loot-id=\"([^\"]+)\"").Select(match => match.Groups[1].Value);
        Assert.Equal(new[] { 210230006, 610530008, 110140003, 110140002, 610530005, 110150004, 110150003, 610530001, 110160001, 110160008 }
            .Select(id => "Item:" + id), displayedIds);
        Assert.Equal(ids.Select(id => "Item:" + id), session.Loot.Select(row => row.Id));
        Assert.DoesNotContain("CommonGear", html); Assert.DoesNotContain("RareGear", html); Assert.DoesNotContain("LegendGear", html);
        await fixture.Export(html, "rarity-order");
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task UnifiedViewsKeepTheSameSessionPlayheadAndFilterBeforeAggregatingItems(string language)
    {
        await using var fixture = new Fixture(language);
        var parent = LootTrackerSessionProjectionTests.Example();
        var modes = new List<LootTrackerViewMode>();
        var html = await fixture.Render(new([parent], parent.EndedAt), isExample: true,
            onViewChanged: mode => { modes.Add(mode); return Task.CompletedTask; });
        Assert.Equal(1, SessionButtonCount(html));
        Assert.Contains("data-session-id=\"" + parent.Id + "\"", html);
        Assert.Contains("data-session-view=\"mixed\" class=\"active\"", html);
        Assert.Equal("2", Metric(html, "harvest-count"));
        Assert.Equal("2", Metric(html, "harvest-rate"));
        Assert.DoesNotContain("id=\"loot-save-route\"", html);
        await fixture.Export(html, "unified-mixed");
        await fixture.Seek(TimeSpan.FromMinutes(25));
        foreach (var (mode, count) in new[] { ("pve", 2), ("lifeskill", 7), ("abyss", 0), ("mixed", 9) })
        {
            html = await fixture.Click("data-session-view", mode);
            Assert.Equal("25:00", Metric(html, "playhead-time"));
            Assert.Equal(1, SessionButtonCount(html));
            Assert.Contains("data-session-id=\"" + parent.Id + "\"", html);
            if (count > 0)
                Assert.Matches("data-loot-id=\"Item:610530005\"[\\s\\S]*?</td><td>" + count + "</td>", html);
            else Assert.DoesNotContain("data-loot-id=", html);
            Assert.Equal(mode == "lifeskill", html.Contains("id=\"loot-save-route\"", StringComparison.Ordinal));
            if (mode == "lifeskill")
            {
                Assert.Equal("2", Metric(html, "harvest-count")); // Whole-session metric, not the 25-minute replay prefix.
                Assert.Equal("2", Metric(html, "harvest-rate"));
                Assert.DoesNotContain("data-testid=\"kina-total\"", html);
                Assert.DoesNotContain("data-testid=\"abyss-points\"", html);
            }
            await fixture.Export(html, "unified-" + mode);
        }
        Assert.Equal(new[] { LootTrackerViewMode.Pve, LootTrackerViewMode.Lifeskill, LootTrackerViewMode.Abyss, LootTrackerViewMode.Mixed }, modes);
        Assert.Equal(9, Assert.Single(parent.Loot).Quantity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ViewChangeStorageFailureRestoresTheVisibleViewAndRetryClearsTheErrorWithoutMovingThePlayhead(bool accessDenied)
    {
        await using var fixture = new Fixture("en");
        fixture.Settings.SetLootTrackerViewMode(LootTrackerViewMode.Mixed);
        var parent = LootTrackerSessionProjectionTests.Example();
        var fail = true;
        var attempted = new List<LootTrackerViewMode>();
        await fixture.Render(new([parent], parent.EndedAt), isExample: true, onViewChanged: mode =>
        {
            attempted.Add(mode);
            if (fail) return Task.FromException(accessDenied ? new UnauthorizedAccessException("Synthetic denied settings save.")
                : new IOException("Synthetic failed settings save."));
            fixture.Settings.SetLootTrackerViewMode(mode);
            return Task.CompletedTask;
        });
        await fixture.Seek(TimeSpan.FromMinutes(25));
        var html = await fixture.Click("data-session-view", "lifeskill");
        Assert.Contains("data-session-view=\"mixed\" class=\"active\"", html);
        Assert.DoesNotContain("data-session-view=\"lifeskill\" class=\"active\"", html);
        Assert.Equal(LootTrackerViewMode.Mixed, fixture.Settings.Current.LootTrackerViewMode);
        Assert.Contains(UiText.T("Sessionansicht konnte nicht gespeichert werden."), WebUtility.HtmlDecode(html));
        Assert.Equal("25:00", Metric(html, "playhead-time"));
        fail = false;
        html = await fixture.Click("data-session-view", "lifeskill");
        Assert.Contains("data-session-view=\"lifeskill\" class=\"active\"", html);
        Assert.Equal(LootTrackerViewMode.Lifeskill, new SettingsService().Current.LootTrackerViewMode);
        Assert.DoesNotContain(UiText.T("Sessionansicht konnte nicht gespeichert werden."), WebUtility.HtmlDecode(html));
        Assert.Equal("25:00", Metric(html, "playhead-time"));
        Assert.Equal(new[] { LootTrackerViewMode.Lifeskill, LootTrackerViewMode.Lifeskill }, attempted);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task UnifiedAbyssViewIncludesGatheringButLifeskillViewKeepsOnlyHarvestFacts(string language)
    {
        await using var fixture = new Fixture(language);
        var parent = LootTrackerSessionProjectionTests.Example(abyss: true);
        var html = await fixture.Render(new([parent], parent.EndedAt), isExample: true, viewMode: LootTrackerViewMode.Abyss);
        Assert.Equal("20", Metric(html, "abyss-points"));
        Assert.Equal("2", Metric(html, "harvest-count"));
        Assert.Equal("2", Metric(html, "harvest-rate"));
        Assert.Contains("data-combat-category=\"unknown\"", html);
        Assert.DoesNotContain("id=\"loot-save-route\"", html);
        await fixture.Export(html, "unified-abyss-context");
        html = await fixture.Click("data-session-view", "lifeskill");
        Assert.Equal("7", Metric(html, "item-count"));
        Assert.Equal("2", Metric(html, "harvest-count"));
        Assert.Contains("id=\"loot-save-route\"", html);
        Assert.DoesNotContain("data-testid=\"abyss-points\"", html);
        Assert.DoesNotContain("data-testid=\"kina-total\"", html);
        Assert.DoesNotContain("data-combat-category=", html);
        Assert.Contains("data-map-id=\"example-map\"", html);
        await fixture.Export(html, "unified-abyss-lifeskill");
    }

    private static string Metric(string html, string testId) => WebUtility.HtmlDecode(Regex.Match(html,
        "data-testid=\"" + Regex.Escape(testId) + "\">([^<]*)<").Groups[1].Value);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _previousLanguage = UiText.Language;
        private readonly byte[]? _settingsBackup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        private readonly ServiceProvider _provider;
        private readonly HtmlRenderer _renderer;
        private readonly string _language;
        private HtmlRootComponent _root;
        private LootTrackerView? _view;

        public Fixture(string language)
        {
            _language = language;
            UiText.Language = language;
            _provider = new ServiceCollection().AddLogging().AddSingleton<SettingsService>().AddSingleton<UiState>().BuildServiceProvider();
            Settings.Current.UiLanguage = language;
            Settings.Current.NameLanguage = language;
            Settings.Current.LootTrackerIdleTimeoutMinutes = 2;
            // Most scenarios use tiny synthetic sessions; small-session tests enable the filter explicitly.
            Settings.Current.LootTrackerHideSmallSessions = false;
            UiText.Language = language;
            _renderer = new HtmlRenderer(_provider, _provider.GetRequiredService<ILoggerFactory>());
        }

        public SettingsService Settings => _provider.GetRequiredService<SettingsService>();
        public UiState Ui => _provider.GetRequiredService<UiState>();
        public Task Seek(TimeSpan elapsed) => _renderer.Dispatcher.InvokeAsync(() => ((CaptureLootTrackerView)_view!).SeekNow(elapsed));
        public Task FollowEnd() => _renderer.Dispatcher.InvokeAsync(() => ((CaptureLootTrackerView)_view!).FollowNow());
        public Task SettingsPeriod(AbyssPointRatePeriod period) => _renderer.Dispatcher.InvokeAsync(() => Settings.SetAbyssPointRatePeriod(period));

        public async Task<string> Render(LootTrackerSnapshot snapshot, bool sourceAvailable = false, bool isExample = false, string? sourceStatus = null, Func<string, Task>? onDelete = null,
            LootTrackerViewMode viewMode = LootTrackerViewMode.Mixed, Func<LootTrackerViewMode, Task>? onViewChanged = null)
        {
            _root = await _renderer.Dispatcher.InvokeAsync(() => _renderer.RenderComponentAsync<CaptureLootTrackerView>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(LootTrackerView.Snapshot)] = snapshot,
                    [nameof(LootTrackerView.SourceAvailable)] = sourceAvailable,
                    [nameof(LootTrackerView.IsExample)] = isExample,
                    [nameof(LootTrackerView.SourceStatus)] = sourceStatus,
                    [nameof(LootTrackerView.DeleteSession)] = onDelete is null ? default(EventCallback<string>) : EventCallback.Factory.Create<string>(this, onDelete),
                    [nameof(LootTrackerView.ViewMode)] = viewMode,
                    [nameof(LootTrackerView.ViewModeChanged)] = onViewChanged is null ? default(EventCallback<LootTrackerViewMode>) : EventCallback.Factory.Create<LootTrackerViewMode>(this, onViewChanged),
                    [nameof(CaptureLootTrackerView.Captured)] = (Action<LootTrackerView>)(view => _view = view),
                })));
            return await Markup();
        }

        public Task<string> Click(string attribute, string value) => Event(attribute, value, "onclick", new MouseEventArgs());
        public Task<string> Change(string id, object value, string eventName = "onchange") =>
            Event("id", id, eventName, new ChangeEventArgs { Value = value });

        public Task UpdateSnapshot(LootTrackerSnapshot snapshot) => _renderer.Dispatcher.InvokeAsync(() =>
            _view!.SetParametersAsync(ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(LootTrackerView.Snapshot)] = snapshot })));
        public Task<string> CurrentMarkup() => Markup();

#pragma warning disable BL0006
        private async Task<string> Event<TEventArgs>(string attribute, string value, string eventName, TEventArgs args)
        {
            await _renderer.Dispatcher.InvokeAsync(async () =>
            {
                using var tree = new RenderTreeBuilder();
                object target = _view!;
                if (attribute == "id" && value == "loot-show-route-on-map")
                    target = typeof(LootTrackerView).GetField("_sessionReplay", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_view)!;
                target.GetType().GetMethod("BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(target, [tree]);
                var frames = tree.GetFrames();
                var index = Enumerable.Range(0, frames.Count).Single(i =>
                    frames.Array[i].FrameType == RenderTreeFrameType.Attribute && frames.Array[i].AttributeName == attribute
                    && Equals(frames.Array[i].AttributeValue, value));
                var element = index;
                while (frames.Array[element].FrameType != RenderTreeFrameType.Element) element--;
                object? callback = null;
                for (var i = element + 1; i < frames.Count && frames.Array[i].FrameType == RenderTreeFrameType.Attribute; i++)
                    if (frames.Array[i].AttributeName == eventName) callback = frames.Array[i].AttributeValue;
                switch (callback)
                {
                    case EventCallback<TEventArgs> typed: await typed.InvokeAsync(args); break;
                    case EventCallback untyped: await untyped.InvokeAsync(args); break;
                    case MulticastDelegate handler:
                        if (handler.DynamicInvoke(handler.Method.GetParameters().Length == 0 ? [] : [args]) is Task task) await task;
                        break;
                    default: throw new InvalidOperationException($"No {eventName} callback on {attribute}={value}.");
                }
                await ((CaptureLootTrackerView)_view!).RefreshNow();
            });
            return await Markup();
        }
#pragma warning restore BL0006

        private Task<string> Markup() => _renderer.Dispatcher.InvokeAsync(_root.ToHtmlString);

        public async Task Export(string html, string state)
        {
            if (Environment.GetEnvironmentVariable("SOULCREST_LOOT_UI_PREVIEW") is not { Length: > 0 } output) return;
            Directory.CreateDirectory(output);
            var cssDirectory = Path.Combine(TestPrerequisites.RepositoryRoot, "src", "Soulcrest.App", "wwwroot", "css");
            var css = await File.ReadAllTextAsync(Path.Combine(cssDirectory, "app.css")) + "\n" +
                await File.ReadAllTextAsync(Path.Combine(cssDirectory, "loot-tracker.css"));
            html = Regex.Replace(html, "src=\"(loot-icons/[a-z0-9_-]+\\.png)\"", match =>
            {
                var path = Path.Combine(TestPrerequisites.RepositoryRoot, "src", "Soulcrest.App", "wwwroot", match.Groups[1].Value);
                return "src=\"data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(path)) + "\"";
            });
            var document = "<!doctype html><html lang=\"" + _language + "\"><head><meta charset=\"utf-8\">" +
                "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><style>" + css +
                "</style></head><body>" + html + "</body></html>";
            await File.WriteAllTextAsync(Path.Combine(output, $"loot-tracker-{state}-{_language}.html"), document);
        }

        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync();
            await _provider.DisposeAsync();
            if (_settingsBackup is null) File.Delete(AppPaths.SettingsFile);
            else File.WriteAllBytes(AppPaths.SettingsFile, _settingsBackup);
            UiText.Language = _previousLanguage;
        }
    }
}

public sealed class CaptureLootTrackerView : LootTrackerView
{
    [Parameter] public Action<LootTrackerView>? Captured { get; set; }
    protected override void OnInitialized() { base.OnInitialized(); Captured?.Invoke(this); }
    protected override LootTrackerPreparedReplay? PreparedSessionReplay(LootTrackerSession session) =>
        session.Status == LootTrackerSessionStatus.Live ? null : LootTrackerPreparedReplay.Prepare(session);
    public Task RefreshNow() => InvokeAsync(StateHasChanged);
    public Task SeekNow(TimeSpan elapsed) => InvokeAsync(() => { SeekTimeline(elapsed); StateHasChanged(); });
    public Task FollowNow() => InvokeAsync(() => { FollowTimelineEnd(); StateHasChanged(); });
}
