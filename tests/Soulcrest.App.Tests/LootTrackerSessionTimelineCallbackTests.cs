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
using Microsoft.JSInterop;
using Soulcrest.App.Components;
using Soulcrest.App.Services;
using Xunit;
using MouseEventArgs = Microsoft.AspNetCore.Components.Web.MouseEventArgs;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class LootTrackerSessionTimelineCallbackTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private static LootTrackerSession Session => new()
    {
        Id = "synthetic-timeline-callback", Kind = LootTrackerSessionKind.Abyss, TimelineVersion = 1,
        StartedAt = At, Elapsed = TimeSpan.FromMinutes(3), AbyssPoints = 100,
        TimelineEvents =
        [
            new("ready", LootTrackerTimelineEventKind.Availability, At, TimeSpan.Zero)
                { Metric = LootTrackerTimelineMetric.AbyssPoints, Available = true },
            new("ap", LootTrackerTimelineEventKind.AbyssPoints, At.AddSeconds(20), TimeSpan.FromSeconds(20)) { Amount = 100 },
            new("stage", LootTrackerTimelineEventKind.PointsStageReached, At.AddMinutes(1), TimeSpan.FromMinutes(1))
                { FromStageId = "rank-19", ToStageId = "rank-18" }
        ],
        CombatEvents = Enumerable.Range(0, 3).Select(index => new LootTrackerCombatEvent("kill-" + index,
            LootTrackerCombatKind.Kill, At.AddSeconds(10 + index * .2), TimeSpan.FromSeconds(10 + index * .2))).ToImmutableArray()
    };

    private static LootTrackerSession CombinedSession(LootTrackerViewMode view) => Session with
    {
        Id = "synthetic-combined-plot", ProjectionViewMode = view, UnifiedVersion = 1,
        AbyssPoints = 1000, RankPointsGained = 25, RankPointsLost = 75, KinaBound = 100000, KinaUnbound = 200000,
        ItemCount = 12, SoulCount = 3, CombatEvents = [],
        TimelineEvents =
        [
            new("ap-ready", LootTrackerTimelineEventKind.Availability, At, TimeSpan.Zero) { Metric = LootTrackerTimelineMetric.AbyssPoints, Available = true },
            new("rp-ready", LootTrackerTimelineEventKind.Availability, At, TimeSpan.Zero) { Metric = LootTrackerTimelineMetric.RankPoints, Available = true },
            new("bound-ready", LootTrackerTimelineEventKind.Availability, At, TimeSpan.Zero) { Metric = LootTrackerTimelineMetric.KinaBound, Available = true },
            new("unbound-ready", LootTrackerTimelineEventKind.Availability, At, TimeSpan.Zero) { Metric = LootTrackerTimelineMetric.KinaUnbound, Available = true },
            new("bound-gain", LootTrackerTimelineEventKind.KinaBound, At.AddSeconds(10), TimeSpan.FromSeconds(10)) { Amount = 100000 },
            new("ap-gain", LootTrackerTimelineEventKind.AbyssPoints, At.AddSeconds(20), TimeSpan.FromSeconds(20)) { Amount = 1000 },
            new("rp-gain", LootTrackerTimelineEventKind.RankPoints, At.AddSeconds(20), TimeSpan.FromSeconds(20)) { Amount = 25 },
            new("unbound-gain", LootTrackerTimelineEventKind.KinaUnbound, At.AddSeconds(30), TimeSpan.FromSeconds(30)) { Amount = 200000 },
            new("quantity-drop", LootTrackerTimelineEventKind.Drop, At.AddSeconds(40), TimeSpan.FromSeconds(40))
                { Loot = [new("synthetic-item", "Synthetic item", LootTrackerLootKind.Item, 12), new("synthetic-soul", "Synthetic soul", LootTrackerLootKind.Soul, 3)] },
            new("rp-loss", LootTrackerTimelineEventKind.RankPoints, At.AddSeconds(60), TimeSpan.FromSeconds(60)) { Amount = -75 }
        ]
    };

    [Theory]
    [InlineData(LootTrackerViewMode.Mixed)]
    [InlineData(LootTrackerViewMode.Abyss)]
    public async Task OneCombinedPlotDrawsThreeSignedIncomeCurvesAndAreasWithQuantityBarsAndIndividualScales(LootTrackerViewMode view)
    {
        await using var fixture = new Fixture(); var source = CombinedSession(view);
        await fixture.Render(source); var html = await fixture.Update(source.Elapsed, playing: false);
        Assert.Single(Regex.Matches(html, "data-timeline-plot=\"[^\"]+\""));
        Assert.Contains("data-timeline-plot=\"combined\"", html);
        var svg = MainSvg(html);
        Assert.Equal(3, Regex.Matches(svg, "data-curve=\"(?:ap|rp|kina)\"").Count);
        Assert.Equal(3, Regex.Matches(svg, "data-area=\"(?:ap|rp|kina)\"").Count);
        foreach (var key in new[] { "ap", "rp", "kina" })
        {
            Assert.Contains("data-series-scale=\"" + key + "\"", html);
            Assert.Contains("data-curve=\"" + key + "\"", svg);
            Assert.Contains("data-area=\"" + key + "\"", svg);
        }
        foreach (var key in new[] { "items", "souls" })
        {
            Assert.Contains("data-bar=\"" + key + "\"", svg);
            Assert.DoesNotContain("data-curve=\"" + key + "\"", svg);
            Assert.Contains("data-series-scale=\"" + key + "\"", html);
        }
        foreach (var (key, minimum, maximum) in new[] { ("ap", "0", "1000"), ("rp", "-50", "25"),
            ("kina", "0", "300000"), ("items", "0", "12"), ("souls", "0", "3") })
        {
            var scale = Scale(html, key);
            Assert.Contains("data-known=\"true\"", scale);
            Assert.Contains("data-min=\"" + minimum + "\"", scale);
            Assert.Contains("data-max=\"" + maximum + "\"", scale);
            var visible = WebUtility.HtmlDecode(Regex.Replace(scale, "<[^>]+>", ""));
            Assert.Contains(long.Parse(minimum, System.Globalization.CultureInfo.InvariantCulture).ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("en-US")), visible);
            Assert.Contains(long.Parse(maximum, System.Globalization.CultureInfo.InvariantCulture).ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("en-US")), visible);
        }
        Assert.Contains("data-testid=\"timeline-value-rp\">-50</strong>", html);
        Assert.Contains("data-testid=\"timeline-value-kina\">300,000</strong>", html);
        Assert.Contains("data-testid=\"timeline-value-items\">12</strong>", html);
        Assert.DoesNotContain("NaN", html); Assert.DoesNotContain("Infinity", html);
        Assert.Equal(10, source.TimelineEvents.Length); Assert.Equal(12, source.ItemCount);
    }

    [Fact]
    public async Task CombinedLayerToggleRemovesItsCurveAndAreaWhileViewSwitchKeepsPlaybackAndPlayhead()
    {
        await using var fixture = new Fixture(); var source = CombinedSession(LootTrackerViewMode.Mixed);
        await fixture.Render(source); await fixture.Update(TimeSpan.FromSeconds(90), playing: true);
        var html = await fixture.ChangeAttribute("data-timeline-layer", "rp", false);
        var svg = MainSvg(html);
        Assert.DoesNotContain("data-curve=\"rp\"", svg); Assert.DoesNotContain("data-area=\"rp\"", svg);
        Assert.Contains("data-curve=\"ap\"", svg); Assert.Contains("data-area=\"kina\"", svg);
        Assert.Contains("data-bar=\"items\"", svg);
        Assert.Contains("data-testid=\"playhead-time\">01:30", html); Assert.True(fixture.IsPlaying);
        html = await fixture.Project(source with { ProjectionViewMode = LootTrackerViewMode.Abyss });
        Assert.Contains("data-curve=\"rp\"", MainSvg(html));
        Assert.Contains("data-area=\"rp\"", MainSvg(html));
        Assert.Contains("data-testid=\"playhead-time\">01:30", html); Assert.True(fixture.IsPlaying);
        html = await fixture.Project(source);
        Assert.DoesNotContain("data-curve=\"rp\"", MainSvg(html));
        Assert.DoesNotContain("data-area=\"rp\"", MainSvg(html));
        Assert.Contains("data-testid=\"playhead-time\">01:30", html); Assert.True(fixture.IsPlaying);
        Assert.Null(fixture.LastSeek); Assert.Equal(0, fixture.PlayCount);
    }

    [Fact]
    public async Task CombinedUnknownIncomeScaleDoesNotDrawAFabricatedCurveOrArea()
    {
        await using var fixture = new Fixture(); var source = CombinedSession(LootTrackerViewMode.Mixed);
        source = source with { RankPointsGained = null, RankPointsLost = null,
            TimelineEvents = source.TimelineEvents.Where(row => row.Kind != LootTrackerTimelineEventKind.RankPoints)
                .Select(row => row.Metric == LootTrackerTimelineMetric.RankPoints ? row with { Available = false } : row).ToImmutableArray() };
        await fixture.Render(source); var html = await fixture.Update(source.Elapsed, playing: false);
        var svg = MainSvg(html);
        Assert.DoesNotContain("data-curve=\"rp\"", svg); Assert.DoesNotContain("data-area=\"rp\"", svg);
        Assert.Contains("data-series-scale=\"rp\"", html);
        var scale = Scale(html, "rp");
        Assert.Contains("data-known=\"false\"", scale);
        Assert.DoesNotContain("data-min=", scale); Assert.DoesNotContain("data-max=", scale);
        Assert.Contains(UiText.T("Unbekannt"), WebUtility.HtmlDecode(Regex.Replace(scale, "<[^>]+>", "")));
        Assert.Contains("data-testid=\"timeline-value-rp\">—</strong>", WebUtility.HtmlDecode(html));
        Assert.Contains("data-curve=\"kina\"", svg); Assert.Contains("data-bar=\"items\"", svg);
    }

    private static string MainSvg(string html)
    {
        var match = Regex.Match(html, "<svg\\b[^>]*data-testid=\"timeline-values-plot\"[^>]*>");
        Assert.True(match.Success, "The production timeline must render one combined values SVG.");
        var start = match.Index;
        var end = html.IndexOf("</svg>", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return html[start..(end + 6)];
    }
    private static string Scale(string html, string key)
    {
        var match = Regex.Match(html, "<small\\b[^>]*data-series-scale=\"" + Regex.Escape(key) + "\"[^>]*>[\\s\\S]*?</small>");
        Assert.True(match.Success, "Every visible series must expose its own scale or unknown state.");
        return match.Value;
    }

    [Fact]
    public async Task RenderedRangeInputSeeksClampsAndRejectsNonFiniteInput()
    {
        await using var fixture = new Fixture();
        await fixture.Render(Session);
        var html = await fixture.Change("loot-timeline-playhead", "25.5", "oninput");
        Assert.Equal(TimeSpan.FromSeconds(25.5), fixture.LastSeek);
        Assert.Contains("data-testid=\"playhead-time\">00:25", html);
        await fixture.Change("loot-timeline-playhead", "-5", "oninput");
        Assert.Equal(TimeSpan.Zero, fixture.LastSeek);
        await fixture.Change("loot-timeline-playhead", "999", "oninput");
        Assert.Equal(TimeSpan.FromMinutes(3), fixture.LastSeek);
        var before = fixture.SeekCount;
        await fixture.Change("loot-timeline-playhead", "NaN", "oninput");
        Assert.Equal(before, fixture.SeekCount);
    }

    [Fact]
    public async Task DenseEventButtonSeeksAndRevealsEveryResultAndJumpClearsItsSelection()
    {
        await using var fixture = new Fixture();
        var html = await fixture.Render(Session);
        Assert.Contains(">3</button>", html);
        html = await fixture.Click("data-event-group", "kill-0");
        Assert.Equal(TimeSpan.FromSeconds(10), fixture.LastSeek);
        var selection = html[html.IndexOf("class=\"lt-timeline-selection\"", StringComparison.Ordinal)..];
        Assert.Equal(3, Regex.Matches(selection, "<li>").Count);
        html = await fixture.Click("id", "loot-timeline-end");
        Assert.Equal(1, fixture.JumpCount);
        Assert.DoesNotContain("class=\"lt-timeline-selection\"", html);
        Assert.Contains("data-testid=\"playhead-time\">03:00", html);
    }

    [Fact]
    public async Task RenderedModeChangePersistsIndependentlyFromTheOverlayPointRate()
    {
        await using var fixture = new Fixture();
        fixture.Settings.SetAbyssPointRatePeriod(AbyssPointRatePeriod.Minute);
        await fixture.Render(Session);
        var html = await fixture.Change("loot-timeline-mode", "PerMinute");
        var saved = new SettingsService().Current;
        Assert.Equal(LootTimelineValueMode.PerMinute, saved.LootTimelineValueMode);
        Assert.Equal(AbyssPointRatePeriod.Minute, saved.AbyssPointRatePeriod);
        Assert.Contains(UiText.T("Timeline-Einstellung gespeichert."), WebUtility.HtmlDecode(html));
        await fixture.Change("loot-timeline-mode", "Cumulative");
        Assert.Equal(LootTimelineValueMode.Cumulative, new SettingsService().Current.LootTimelineValueMode);
    }

    [Fact]
    public async Task RenderedModeChangeReportsWriteFailureAndKeepsThePriorMode()
    {
        await using var fixture = new Fixture();
        await fixture.Render(Session);
        var previous = File.ReadAllBytes(AppPaths.SettingsFile);
        var changes = 0;
        fixture.Settings.Changed += () => changes++;
        File.Delete(AppPaths.SettingsFile); Directory.CreateDirectory(AppPaths.SettingsFile);
        try
        {
            var html = await fixture.Change("loot-timeline-mode", "PerMinute");
            Assert.Equal(LootTimelineValueMode.Cumulative, fixture.Settings.Current.LootTimelineValueMode);
            Assert.Equal(0, changes);
            Assert.Contains("lt-timeline-feedback error", html);
            Assert.Contains(UiText.T("Timeline-Einstellung konnte nicht gespeichert werden."), WebUtility.HtmlDecode(html));
        }
        finally { Directory.Delete(AppPaths.SettingsFile); File.WriteAllBytes(AppPaths.SettingsFile, previous); }
    }

    [Fact]
    public async Task SameImmutableSessionRebuildsLocalizedEventLabelsAfterLanguageChange()
    {
        await using var fixture = new Fixture();
        var session = Session;
        var html = await fixture.Render(session);
        var english = UiText.F("Punkte-Stufe erreicht: {0}", AbyssRankCatalog.Default.Stages.Single(stage => stage.Id == "rank-18").Name("en"));
        Assert.Contains(english, WebUtility.HtmlDecode(html));
        await fixture.Click("data-event-group", "stage");
        html = await fixture.ChangeLanguage("de");
        Assert.Contains("Punkte-Stufe erreicht: " + AbyssRankCatalog.Default.Stages.Single(stage => stage.Id == "rank-18").Name("de"), WebUtility.HtmlDecode(html));
        Assert.DoesNotContain(english, WebUtility.HtmlDecode(html));
        Assert.DoesNotContain("class=\"lt-timeline-selection\"", html);
    }

    [Fact]
    public async Task LayersPersistPerProjectedViewAndWriteFailureKeepsVisibleSelection()
    {
        await using var fixture = new Fixture();
        var session = Session with { ProjectionViewMode = LootTrackerViewMode.Abyss };
        await fixture.Render(session);
        await fixture.ChangeAttribute("data-timeline-layer", "ap", false);
        Assert.DoesNotContain("ap", new SettingsService().GetLootTimelineLayers(LootTrackerViewMode.Abyss));
        Assert.Contains("ap", new SettingsService().GetLootTimelineLayers(LootTrackerViewMode.Mixed));
        var previous = File.ReadAllBytes(AppPaths.SettingsFile);
        var changes = 0; fixture.Settings.Changed += () => changes++;
        File.Delete(AppPaths.SettingsFile); Directory.CreateDirectory(AppPaths.SettingsFile);
        try
        {
            var html = await fixture.ChangeAttribute("data-timeline-layer", "rp", false);
            Assert.Contains("rp", fixture.Settings.GetLootTimelineLayers(LootTrackerViewMode.Abyss));
            Assert.Contains("data-timeline-series=\"rp\"", html);
            Assert.Contains("lt-timeline-feedback error", html); Assert.Equal(0, changes);
        }
        finally { Directory.Delete(AppPaths.SettingsFile); File.WriteAllBytes(AppPaths.SettingsFile, previous); }
    }

    [Fact]
    public async Task FocusedTrackZoomAndHeadKeyboardHaveIndependentMeaningAndSpaceTogglesPlayback()
    {
        await using var fixture = new Fixture(); await fixture.Render(Session);
        var html = await fixture.Key("loot-timeline-track", "+");
        Assert.DoesNotContain("data-viewport-from=\"0\"", html);
        Assert.Null(fixture.LastSeek);
        await fixture.Key("loot-timeline-track", "ArrowRight");
        Assert.Null(fixture.LastSeek);
        await fixture.Change("loot-timeline-playhead", "90", "oninput");
        await fixture.Key("loot-timeline-head-grip", "ArrowRight");
        Assert.Equal(TimeSpan.FromSeconds(91), fixture.LastSeek);
        await fixture.Key("loot-timeline-track", " "); Assert.Equal(1, fixture.PlayCount);
        await fixture.Key("loot-timeline-track", "Home");
        html = await fixture.Key("loot-timeline-head-grip", "End");
        Assert.Equal(Session.Elapsed, fixture.LastSeek);
        Assert.Contains("data-viewport-from=\"0\"", html);
        Assert.Contains("data-testid=\"timeline-overview\"", html);
    }

    [Fact]
    public async Task BackgroundMovementBelowFourPixelsSeeksButExactlyFourPixelsOnlyPans()
    {
        await using var fixture = new Fixture(); await fixture.Render(Session);
        await fixture.Pointer("onpointerdown", 250); await fixture.Pointer("onpointermove", 253); await fixture.Pointer("onpointerup", 253);
        Assert.Equal(TimeSpan.FromSeconds(45.54), fixture.LastSeek);
        var before = fixture.SeekCount;
        await fixture.Key("loot-timeline-track", "+");
        var html = await fixture.Pointer("onpointerdown", 250);
        var initialFrom = ViewportFrom(html);
        await fixture.Pointer("onpointermove", 246); html = await fixture.Pointer("onpointerup", 246);
        Assert.Equal(before, fixture.SeekCount);
        Assert.True(ViewportFrom(html) > initialFrom);
    }

    [Fact]
    public async Task ManualPanStopsAutoFollowUntilPlaybackStartsAgainAndHeadOnlyTickKeepsCachedPaths()
    {
        await using var fixture = new Fixture(); await fixture.Render(Session);
        await fixture.Key("loot-timeline-track", "+");
        var html = await fixture.Update(TimeSpan.FromSeconds(90), playing: true);
        var paths = fixture.CachedPlots;
        html = await fixture.Update(TimeSpan.FromSeconds(91), playing: true);
        Assert.Same(paths, fixture.CachedPlots);
        await fixture.Key("loot-timeline-track", "ArrowLeft");
        var panned = ViewportFrom(await fixture.MarkupNow());
        html = await fixture.Update(TimeSpan.FromSeconds(179), playing: true);
        Assert.Equal(panned, ViewportFrom(html));
        await fixture.Update(TimeSpan.FromSeconds(179), playing: false);
        html = await fixture.Update(TimeSpan.FromSeconds(179), playing: true);
        Assert.True(ViewportFrom(html) > panned);
    }

    [Fact]
    public async Task LootSelectorUsesActualRewardsAndExactTablePaginatesTwentyFiveRowsWithoutDroppingFacts()
    {
        await using var fixture = new Fixture();
        var loot = new LootTrackerLoot("synthetic-ore", "Synthetic ore", LootTrackerLootKind.Item, 2);
        var session = Session with { Loot = [], TimelineEvents = Enumerable.Range(1, 31).Select(index =>
            new LootTrackerTimelineEvent("drop-" + index, LootTrackerTimelineEventKind.Drop, At.AddSeconds(index), TimeSpan.FromSeconds(index))
                { Loot = [loot] }).ToImmutableArray(), CombatEvents = [] };
        await fixture.Render(session);
        var html = await fixture.Click("id", "loot-timeline-loot-toggle");
        Assert.Contains("data-timeline-item=\"synthetic-ore\"", html);
        html = await fixture.Click("id", "loot-timeline-exact-toggle");
        var table = html[html.IndexOf("<tbody>", StringComparison.Ordinal)..html.IndexOf("</tbody>", StringComparison.Ordinal)];
        Assert.Equal(25, Regex.Matches(table, "<tr>").Count);
        html = await fixture.Click("id", "loot-timeline-exact-next");
        table = html[html.IndexOf("<tbody>", StringComparison.Ordinal)..html.IndexOf("</tbody>", StringComparison.Ordinal)];
        Assert.Equal(6, Regex.Matches(table, "<tr>").Count);
        html = await fixture.ChangeAttribute("data-timeline-item", "synthetic-ore", false);
        Assert.DoesNotContain("data-loot-marker=\"synthetic-ore\"", html);
        Assert.DoesNotContain("<tr><td>", html);
        Assert.Equal(31, session.TimelineEvents.Length);
    }

    [Fact]
    public async Task LegacyExactTableKeepsPartialUnknownRankIncomeUnknown()
    {
        await using var fixture = new Fixture();
        await fixture.Render(Session with { TimelineVersion = 0, TimelineEvents = [], CombatEvents = [],
            MinuteIncome = [new(0, null, null) { RankPointsGained = 50, RankPointsLost = null }] });
        var html = WebUtility.HtmlDecode(await fixture.Click("id", "loot-timeline-exact-toggle"));
        Assert.Contains("—</td>", html);
        Assert.DoesNotContain(">50</td>", html);
    }

    [Fact]
    public async Task ExactKinaRowsKeepBoundAndUnboundAmountsAndSourceStatesDistinct()
    {
        await using var fixture = new Fixture();
        await fixture.Render(Session with { Kind = LootTrackerSessionKind.Grind, ProjectionViewMode = LootTrackerViewMode.Pve,
            CombatEvents = [], KinaBound = 100, KinaUnbound = 50, TimelineEvents =
            [
                new("bound-ready", LootTrackerTimelineEventKind.Availability, At, TimeSpan.Zero)
                    { Metric = LootTrackerTimelineMetric.KinaBound, Available = true },
                new("unbound-unknown", LootTrackerTimelineEventKind.Availability, At, TimeSpan.Zero)
                    { Metric = LootTrackerTimelineMetric.KinaUnbound, Available = false },
                new("bound-gain", LootTrackerTimelineEventKind.KinaBound, At.AddSeconds(10), TimeSpan.FromSeconds(10)) { Amount = 100 },
                new("unbound-gain", LootTrackerTimelineEventKind.KinaUnbound, At.AddSeconds(20), TimeSpan.FromSeconds(20)) { Amount = 50 }
            ] });
        var html = WebUtility.HtmlDecode(await fixture.Click("id", "loot-timeline-exact-toggle"));
        var table = html[html.IndexOf("<tbody>", StringComparison.Ordinal)..html.IndexOf("</tbody>", StringComparison.Ordinal)];
        var rows = Regex.Matches(table, "<tr>.*?</tr>", RegexOptions.Singleline).Select(match => match.Value).ToArray();
        Assert.Contains(rows, row => row.Contains(UiText.T("Gebundene Kina")) && row.Contains("100 Kina"));
        Assert.Contains(rows, row => row.Contains(UiText.T("Ungebundene Kina")) && row.Contains("50 Kina"));
        Assert.Contains(rows, row => row.Contains(UiText.T("Gebundene Kina")) && row.Contains(UiText.T("Quellenstatus")) && row.Contains(UiText.T("Bestätigt")));
        Assert.Contains(rows, row => row.Contains(UiText.T("Ungebundene Kina")) && row.Contains(UiText.T("Quellenstatus")) && row.Contains(UiText.T("Unbekannt")));
        Assert.DoesNotContain(UiText.T("Kina gesamt"), table);
    }

    [Fact]
    public async Task VisuallyBoundedDenseMarkersKeepEveryCountAndLargeQuantityAccessible()
    {
        await using var fixture = new Fixture();
        var session = Session with { CombatEvents = Enumerable.Range(0, 123).Select(index => new LootTrackerCombatEvent("dense-" + index,
                LootTrackerCombatKind.Kill, At.AddSeconds(10).AddMilliseconds(index), TimeSpan.FromSeconds(10).Add(TimeSpan.FromMilliseconds(index)))).ToImmutableArray(),
            TimelineEvents = [new("large-drop", LootTrackerTimelineEventKind.Drop, At.AddSeconds(10), TimeSpan.FromSeconds(10))
                { Loot = [new("synthetic-large", "Synthetic large reward", LootTrackerLootKind.Item, 123456789)] }] };
        var html = WebUtility.HtmlDecode(await fixture.Render(session));
        Assert.Contains(">123</button>", html);
        Assert.Contains("title=\"Synthetic large reward ×123,456,789\"", html);
        html = WebUtility.HtmlDecode(await fixture.Click("data-loot-marker", "synthetic-large"));
        var details = html[html.IndexOf("class=\"lt-timeline-selection\"", StringComparison.Ordinal)..];
        Assert.Contains("123,456,789", details);
        html = await fixture.Click("data-event-group", "dense-0");
        details = html[html.IndexOf("class=\"lt-timeline-selection\"", StringComparison.Ordinal)..];
        Assert.Equal(123, Regex.Matches(details, "<li>").Count);
    }

    [Fact]
    public async Task FailureCountAndFailureEventLayersHaveDistinctVisibleLabels()
    {
        await using var fixture = new Fixture();
        var html = WebUtility.HtmlDecode(await fixture.Render(Session with { Kind = LootTrackerSessionKind.Lifeskill,
            ProjectionViewMode = LootTrackerViewMode.Lifeskill, CombatEvents = [], FailedHarvestCount = 1,
            TimelineEvents = [new("failed", LootTrackerTimelineEventKind.HarvestFailure, At.AddSeconds(10), TimeSpan.FromSeconds(10))] }));
        Assert.Contains(UiText.T("Fehlversuche (Anzahl)"), html);
        Assert.Contains(UiText.T("Fehlversuche (Ereignisse)"), html);
        Assert.NotEqual(LootTrackerTimelineLayers.All.Single(row => row.Id == "failure").Label,
            LootTrackerTimelineLayers.All.Single(row => row.Id == "failed").Label);
    }

    private static double ViewportFrom(string html) => double.Parse(Regex.Match(html, "data-viewport-from=\"([^\"]+)\"").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task ResourceCheckboxesProjectUnplacedHarvestCurvesMarkersAndExactLootWithoutChangingTheSession()
    {
        await using var fixture = new Fixture(); var session = LootTrackerTimelineLayoutTests.ResourceSession();
        await fixture.Render(session);
        var html = WebUtility.HtmlDecode(await fixture.Click("id", "loot-timeline-resources-toggle"));
        Assert.Contains("data-timeline-resource=\"resource:7001\"", html);
        Assert.Contains("data-timeline-resource=\"resource:7002\"", html);
        Assert.Contains("data-timeline-resource=\"name:Synthetic herb\"", html);
        Assert.Contains("data-timeline-resource=\"unknown\"", html);
        await fixture.ChangeAttribute("data-timeline-resource", "resource:7002", false);
        await fixture.ChangeAttribute("data-timeline-resource", "name:Synthetic herb", false);
        await fixture.ChangeAttribute("data-timeline-resource", "unknown", false);
        html = await fixture.Update(session.Elapsed, playing: false);
        Assert.Contains("data-testid=\"timeline-value-success\">1</strong>", html);
        Assert.Contains("data-testid=\"timeline-value-failure\">1</strong>", html);
        Assert.Contains("data-testid=\"timeline-value-items\">9</strong>", html);
        Assert.Contains("data-event-group=\"a-success\"", html);
        Assert.Contains("data-event-group=\"a-failure\"", html);
        Assert.DoesNotContain("data-event-group=\"b-success\"", html);
        Assert.DoesNotContain("data-loot-marker=\"ore-b\"", html);
        Assert.Contains("data-loot-marker=\"pve-item\"", html);
        html = WebUtility.HtmlDecode(await fixture.Click("id", "loot-timeline-exact-toggle"));
        var table = html[html.IndexOf("<tbody>", StringComparison.Ordinal)..html.IndexOf("</tbody>", StringComparison.Ordinal)];
        Assert.DoesNotContain("ore-b", table); Assert.DoesNotContain("herb", table);
        Assert.Contains("PvE item", table); Assert.Contains("ore-a", table);
        Assert.Equal(17, session.ItemCount); Assert.Equal(3, session.HarvestCount); Assert.Equal(8, session.TimelineEvents.Length);
        await fixture.ChangeAttribute("data-timeline-resource", "resource:7001", false);
        html = await fixture.Update(session.Elapsed, playing: false);
        Assert.Contains("data-testid=\"timeline-value-success\">0</strong>", html);
        Assert.Contains("data-testid=\"timeline-value-failure\">0</strong>", html);
        Assert.Contains("data-testid=\"timeline-value-items\">7</strong>", html);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FastPointerUpWhileBoundsArePendingUsesItsDownMeasurementAndCannotCaptureLate(bool head)
    {
        var js = new DeferredTimelineJavaScript();
        await using var fixture = new Fixture(js); await fixture.Render(Session);
        var down = fixture.Pointer("onpointerdown", 350, head: head);
        await js.WaitForBounds();
        var up = fixture.Pointer("onpointerup", 350, head: head);
        await js.ReleaseRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        js.CompleteBounds(500, 100);
        await Task.WhenAll(down, up);
        Assert.Equal(TimeSpan.FromSeconds(90), fixture.LastSeek);
        Assert.Equal(1, fixture.SeekCount); Assert.Equal(0, js.Captures);
        await fixture.Pointer("onpointermove", 400, head: head);
        Assert.Equal(1, fixture.SeekCount);
    }

    [Theory]
    [InlineData("onpointercancel")]
    [InlineData("onlostpointercapture")]
    public async Task CanceledPendingPointerDoesNotReactivateAndANewGenerationCanStillCapture(string cancelEvent)
    {
        var js = new DeferredTimelineJavaScript();
        await using var fixture = new Fixture(js); await fixture.Render(Session);
        var old = fixture.Pointer("onpointerdown", 350); await js.WaitForBounds();
        await fixture.Pointer(cancelEvent, 350);
        js.CompleteBounds(500, 100); await old;
        Assert.Equal(0, fixture.SeekCount); Assert.Equal(0, js.Captures);
        var next = fixture.Pointer("onpointerdown", 350); await js.WaitForBounds();
        js.CompleteBounds(500, 100); await next;
        Assert.Equal(1, js.Captures);
        await fixture.Pointer("onpointerup", 350);
        Assert.Equal(TimeSpan.FromSeconds(90), fixture.LastSeek);
    }

    [Fact]
    public async Task ForeignPointerEventsCannotEndOrReplaceThePendingGesture()
    {
        var js = new DeferredTimelineJavaScript();
        await using var fixture = new Fixture(js); await fixture.Render(Session);
        var down = fixture.Pointer("onpointerdown", 350); await js.WaitForBounds();
        await fixture.Pointer("onpointerdown", 450, pointer: 8);
        await fixture.Pointer("onpointerup", 450, pointer: 8);
        var up = fixture.Pointer("onpointerup", 350);
        await js.ReleaseRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        js.CompleteBounds(500, 100); await Task.WhenAll(down, up);
        Assert.Equal(1, fixture.SeekCount); Assert.Equal(TimeSpan.FromSeconds(90), fixture.LastSeek);
        Assert.Equal(0, js.Captures);
    }

    [Fact]
    public async Task DisposingDuringPointerMeasurementPreventsAnyLateSeekOrCapture()
    {
        var js = new DeferredTimelineJavaScript();
        await using var fixture = new Fixture(js); await fixture.Render(Session);
        var down = fixture.Pointer("onpointerdown", 350); await js.WaitForBounds();
        await fixture.DisposeComponent(); js.CompleteBounds(500, 100); await down;
        Assert.Equal(0, fixture.SeekCount); Assert.Equal(0, js.Captures);
    }

    private sealed class DeferredTimelineJavaScript : IJSRuntime
    {
        private readonly Queue<TaskCompletionSource<double[]>> _bounds = new();
        private readonly SemaphoreSlim _requested = new(0);
        public TaskCompletionSource ReleaseRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Captures { get; private set; }
        public async Task WaitForBounds() => Assert.True(await _requested.WaitAsync(TimeSpan.FromSeconds(2)));
        public void CompleteBounds(double width, double left) => _bounds.Dequeue().SetResult([width, left]);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "soulcrestLootTimeline.bounds")
            {
                var completion = new TaskCompletionSource<double[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                _bounds.Enqueue(completion); _requested.Release();
                return (TValue)(object)await completion.Task.WaitAsync(cancellationToken);
            }
            if (identifier == "soulcrestLootTimeline.capture") Captures++;
            if (identifier == "soulcrestLootTimeline.release") ReleaseRequested.TrySetResult();
            return default!;
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly byte[]? _settingsBackup = File.Exists(AppPaths.SettingsFile) ? File.ReadAllBytes(AppPaths.SettingsFile) : null;
        private readonly string _oldLanguage = UiText.Language;
        private readonly ServiceProvider _provider;
        private readonly HtmlRenderer _renderer;
        private HtmlRootComponent _root;
        private CaptureLootTrackerSessionTimeline? _component;
        private LootTrackerSession? _session;
        public SettingsService Settings => _provider.GetRequiredService<SettingsService>();
        public TimeSpan? LastSeek { get; private set; }
        public int SeekCount { get; private set; }
        public int JumpCount { get; private set; }
        public int PlayCount { get; private set; }
        public bool IsPlaying => _component!.IsPlaying;
        public object? CachedPlots => typeof(LootTrackerSessionTimeline).GetField("_plots", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_component);
        public Fixture(IJSRuntime? javaScript = null)
        {
            JsonFile.Save(AppPaths.SettingsFile, new AppSettings { UiLanguage = "en", NameLanguage = "en" });
            var services = new ServiceCollection().AddLogging().AddSingleton<SettingsService>();
            if (javaScript is not null) services.AddSingleton(javaScript);
            _provider = services.BuildServiceProvider();
            _renderer = new HtmlRenderer(_provider, _provider.GetRequiredService<ILoggerFactory>());
            _ = Settings; UiText.Language = "en";
        }
        public async Task<string> Render(LootTrackerSession session)
        {
            _session = session;
            _root = await _renderer.Dispatcher.InvokeAsync(() => _renderer.RenderComponentAsync<CaptureLootTrackerSessionTimeline>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(LootTrackerSessionTimeline.Session)] = session,
                    [nameof(LootTrackerSessionTimeline.Elapsed)] = TimeSpan.Zero,
                    [nameof(LootTrackerSessionTimeline.Seek)] = EventCallback.Factory.Create<TimeSpan>(this, Seek),
                    [nameof(LootTrackerSessionTimeline.JumpToEnd)] = EventCallback.Factory.Create(this, Jump),
                    [nameof(LootTrackerSessionTimeline.TogglePlayback)] = EventCallback.Factory.Create(this, () => PlayCount++),
                    [nameof(CaptureLootTrackerSessionTimeline.Captured)] = (Action<CaptureLootTrackerSessionTimeline>)(component => _component = component)
                })));
            return await Markup();
        }
        private Task Seek(TimeSpan elapsed)
        {
            LastSeek = elapsed; SeekCount++;
            return _component!.SetParametersAsync(ParameterView.FromDictionary(new Dictionary<string, object?>
            { [nameof(LootTrackerSessionTimeline.Elapsed)] = elapsed, [nameof(LootTrackerSessionTimeline.FollowEnd)] = false }));
        }
        private Task Jump()
        {
            JumpCount++;
            return _component!.SetParametersAsync(ParameterView.FromDictionary(new Dictionary<string, object?>
            { [nameof(LootTrackerSessionTimeline.Elapsed)] = _session!.Elapsed, [nameof(LootTrackerSessionTimeline.FollowEnd)] = true }));
        }
        public async Task<string> ChangeLanguage(string language)
        {
            await _renderer.Dispatcher.InvokeAsync(async () =>
            {
                UiText.Language = language;
                await _component!.SetParametersAsync(ParameterView.FromDictionary(new Dictionary<string, object?>
                { [nameof(LootTrackerSessionTimeline.Session)] = _session }));
            });
            return await Markup();
        }
        public Task<string> Click(string attribute, string value) => Event(attribute, value, "onclick", new MouseEventArgs());
        public Task<string> Change(string id, object value, string eventName = "onchange") => Event("id", id, eventName, new ChangeEventArgs { Value = value });
        public Task<string> ChangeAttribute(string attribute, string value, object checkedValue) => Event(attribute, value, "onchange", new ChangeEventArgs { Value = checkedValue });
        public Task<string> Key(string id, string key) => Event("id", id, "onkeydown", new KeyboardEventArgs { Key = key });
        public Task<string> Pointer(string eventName, double x, bool head = false, long pointer = 7) => Event("id", head ? "loot-timeline-head-grip" : "loot-timeline-track", eventName, new PointerEventArgs { PointerId = pointer, Button = 0, ClientX = x });
        public Task DisposeComponent() => _renderer.Dispatcher.InvokeAsync(async () => await _component!.DisposeAsync());
        public Task<string> MarkupNow() => Markup();
        public async Task<string> Project(LootTrackerSession session)
        {
            _session = session;
            await _renderer.Dispatcher.InvokeAsync(() => _component!.SetParametersAsync(ParameterView.FromDictionary(new Dictionary<string, object?>
            { [nameof(LootTrackerSessionTimeline.Session)] = session })));
            return await Markup();
        }
        public async Task<string> Update(TimeSpan elapsed, bool playing)
        {
            await _renderer.Dispatcher.InvokeAsync(() => _component!.SetParametersAsync(ParameterView.FromDictionary(new Dictionary<string, object?>
            { [nameof(LootTrackerSessionTimeline.Elapsed)] = elapsed, [nameof(LootTrackerSessionTimeline.IsPlaying)] = playing })));
            return await Markup();
        }
        private Task<string> Markup() => _renderer.Dispatcher.InvokeAsync(_root.ToHtmlString);
#pragma warning disable BL0006
        private async Task<string> Event<TEventArgs>(string attribute, string value, string eventName, TEventArgs args)
        {
            await _renderer.Dispatcher.InvokeAsync(async () =>
            {
                using var builder = new RenderTreeBuilder();
                typeof(LootTrackerSessionTimeline).GetMethod("BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(_component, [builder]);
                var frames = builder.GetFrames();
                var index = Enumerable.Range(0, frames.Count).Single(current => frames.Array[current].FrameType == RenderTreeFrameType.Attribute
                    && frames.Array[current].AttributeName == attribute && Equals(frames.Array[current].AttributeValue, value));
                var element = index;
                while (frames.Array[element].FrameType != RenderTreeFrameType.Element) element--;
                object? callback = null;
                for (var current = element + 1; current < frames.Count && frames.Array[current].FrameType == RenderTreeFrameType.Attribute; current++)
                    if (frames.Array[current].AttributeName == eventName) callback = frames.Array[current].AttributeValue;
                switch (callback)
                {
                    case EventCallback<TEventArgs> typed: await typed.InvokeAsync(args); break;
                    case EventCallback untyped: await untyped.InvokeAsync(args); break;
                    case MulticastDelegate handler:
                        if (handler.DynamicInvoke(handler.Method.GetParameters().Length == 0 ? [] : [args]) is Task task) await task;
                        break;
                    default: throw new InvalidOperationException("Rendered timeline control has no matching callback.");
                }
                await _component!.RefreshNow();
            });
            return await Markup();
        }
#pragma warning restore BL0006
        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync(); await _provider.DisposeAsync();
            if (_settingsBackup is null) File.Delete(AppPaths.SettingsFile); else File.WriteAllBytes(AppPaths.SettingsFile, _settingsBackup);
            UiText.Language = _oldLanguage;
        }
    }
}

public sealed class CaptureLootTrackerSessionTimeline : LootTrackerSessionTimeline
{
    [Parameter] public Action<CaptureLootTrackerSessionTimeline>? Captured { get; set; }
    protected override void OnInitialized() { base.OnInitialized(); Captured?.Invoke(this); }
    public Task RefreshNow() => InvokeAsync(StateHasChanged);
}
