using System.Collections.Immutable;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class AbyssSessionMarkerPresentationTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CatalogueVisitsGroupOnlyByExactNodeAndMapWithoutChangingWalkOrDistance()
    {
        var session = Example();
        var before = session with { GatherVisits = [], CombatEvents = [] };
        var markers = LootTrackerSessionMarkers.All(session);
        var odyle = markers.Where(row => row.Kind == LootTrackerSessionMarkerKind.Odyle).ToArray();
        Assert.Equal(4, odyle.Length);
        Assert.Equal(new[] { At.AddMinutes(1), At.AddMinutes(3) }, odyle.Single(row => row.Id == "visit-1").Times);
        Assert.Single(odyle.Single(row => row.Id == "visit-other-node").Times);
        Assert.Single(odyle.Single(row => row.Id == "visit-unresolved-1").Times);
        Assert.Single(odyle.Single(row => row.Id == "visit-unresolved-2").Times);
        Assert.Equal(LootTrackerPresentation.DisplayedRouteSegments(before), LootTrackerPresentation.DisplayedRouteSegments(session));
        Assert.Empty(session.FarmRoutePoints);
        Assert.Equal(0, session.FarmRouteVersion);
        Assert.Equal(LootTrackerCharts.MapRoute(before, "sample-map", 8192), LootTrackerCharts.MapRoute(session, "sample-map", 8192));
        Assert.Equal(4, LootTrackerSessionMarkers.All(session with { Kind = LootTrackerSessionKind.Grind })
            .Count(row => row.Kind == LootTrackerSessionMarkerKind.Gathering));
    }

    [Fact]
    public void MissingInvalidAndOtherMapPositionsNeverBecomeGuessedPins()
    {
        var session = Example() with
        {
            CombatEvents = [new("unplaced", LootTrackerCombatKind.Death, At, TimeSpan.Zero),
                new("invalid", LootTrackerCombatKind.Kill, At, TimeSpan.Zero, new("sample-map", "Map", double.NaN, 30, At)),
                new("other", LootTrackerCombatKind.Kill, At, TimeSpan.Zero, new("other-map", "Other", 50, 50, At))],
        };
        Assert.Single(LootTrackerSessionMarkers.All(session), row => row.Kind == LootTrackerSessionMarkerKind.Kill);
        Assert.DoesNotContain(LootTrackerCharts.MapMarkers(session, "sample-map", 8192), row => row.Data.Kind != LootTrackerSessionMarkerKind.Odyle);
        Assert.Contains(LootTrackerSessionMarkers.Maps(session), row => row.MapId == "other-map");
        Assert.Empty(LootTrackerCharts.MapMarkers(session, "sample-map", 5));
        Assert.Equal(3, session.CombatEvents.Length);
    }

    [Fact]
    public void MarkerOnlySessionHasPreviewMapSelectionAndInspectionButNoNavigationSegments()
    {
        var session = Example() with { RouteSegments = [] };
        var map = new LootTrackerRouteMapPreview.MapDefinition("sample-map", "Map", "sample-map/tiles/{z}_{x}_{y}.webp", 8192, 5, 512, 1, 6);
        var preview = LootTrackerRouteMapPreview.Build(session, map.Id, map, _ => true);
        Assert.Equal(LootTrackerRouteMapPreview.Availability.Ready, preview.Status);
        Assert.Empty(preview.Lines);
        Assert.Equal(6, preview.Markers.Count);
        Assert.All(preview.Markers, row =>
        {
            Assert.InRange(row.Data.X, preview.View.X, preview.View.X + preview.View.Width);
            Assert.InRange(row.Data.Y, preview.View.Y, preview.View.Y + preview.View.Height);
        });
        var focus = Assert.IsType<LootTrackerMapFocus>(LootTrackerMapFocus.Create(session));
        Assert.Equal("sample-map", focus.MapId);
        Assert.Empty(focus.Segments);
        Assert.Equal(6, focus.Markers.Count);
        Assert.Empty(LootTrackerCharts.Route(session, map.Id));
        Assert.Equal(6, LootTrackerCharts.Markers(session, map.Id).Count);
        Assert.Single(LootTrackerSessionMarkers.Maps(session));
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task ActualComponentShowsDistinctPinsFiltersAndUnplacedEventsWithoutInventedLines(string language)
    {
        var previous = UiText.Language;
        try
        {
            UiText.Language = language;
            var session = Example() with { RouteSegments = [] };
            session = session with { CombatEvents = session.CombatEvents.Add(new("unplaced", LootTrackerCombatKind.Death,
                At.AddMinutes(7), TimeSpan.FromMinutes(7))) };
            var html = await Render(session);
            Assert.Contains("data-marker-kind=\"kill\"", html);
            Assert.Contains("data-marker-kind=\"death\"", html);
            Assert.Equal(4, html.Split("data-marker-kind=\"odyle\"").Length - 1);
            Assert.Equal(3, html.Split("type=\"checkbox\"").Length - 1);
            Assert.Contains(System.Net.WebUtility.HtmlEncode(UiText.T("Ohne Kartenposition")), html);
            Assert.DoesNotContain("<polyline", html);
            Assert.DoesNotContain("lt-visual-empty", html);
        }
        finally { UiText.Language = previous; }
    }

    [Fact]
    public void RankVisibilityRequiresFreshCurrentWorldAuthorityButNotAnActiveSession()
    {
        var progress = new AbyssProgressSnapshot("11111111111111111111111111111111", null, null, 100, null, At, true)
        { BindingGeneration = 5 };
        var world = new WorldContextSnapshot(WorldContextStatus.Confirmed, WorldContextKind.Instance,
            20, null, "Abyss", null, "Abyss", "Abyss", 1, At.AddDays(-1), "synthetic") { BindingGeneration = 5 };
        Assert.True(Project(world).WorldContextVisible);
        Assert.False(Project(world).SessionActive);
        foreach (var status in new[] { WorldContextStatus.Unknown, WorldContextStatus.Stale, WorldContextStatus.Transitioning })
            Assert.False(Project(world with { Status = status }).WorldContextVisible);
        Assert.False(Project(world with { BindingGeneration = 6 }).WorldContextVisible);
        Assert.False(Project(world with { ObservedAt = default(DateTimeOffset) }).WorldContextVisible);
        Assert.False(Project(world with { ObservedAt = DateTimeOffset.MaxValue }).WorldContextVisible);
        Assert.False(Project(world with { Category = "OpenWorld" }).WorldContextVisible);
        Assert.False(Project(null).WorldContextVisible);

        Soulcrest.App.Overlay.AbyssRankOverlaySnapshot Project(WorldContextSnapshot? current) =>
            AbyssRankOverlaySource.ProjectRank(progress, [], true, world: current, trackingAvailable: true);
    }

    internal static LootTrackerSession Example() => new()
    {
        Id = "synthetic-abyss-markers", Kind = LootTrackerSessionKind.Abyss, Location = "Abyss",
        StartedAt = At, Elapsed = TimeSpan.FromMinutes(20),
        RouteSegments = [new("sample-map", "Map", [new(100, 200), new(250, 220), new(350, 400)])],
        CombatEvents = [new("kill", LootTrackerCombatKind.Kill, At.AddMinutes(2), TimeSpan.FromMinutes(2), new("sample-map", "Map", 220, 210, At.AddMinutes(2))),
            new("death", LootTrackerCombatKind.Death, At.AddMinutes(5), TimeSpan.FromMinutes(5), new("sample-map", "Map", 340, 350, At.AddMinutes(5)))],
        GatherVisits = [Visit("visit-1", 1, "node-1"), Visit("visit-2", 3, "node-1"), Visit("visit-other-node", 4, "node-2"),
            Visit("visit-unresolved-1", 6, null), Visit("visit-unresolved-2", 7, null)],
    };
    private static LootTrackerGatherVisit Visit(string id, int minute, string? node) =>
        new(id, 0, "sample-map", "Map", 180, 230, 180, 230, At.AddMinutes(minute), true, "Odyle", CatalogNodeId: node);

    private static async Task<string> Render(LootTrackerSession session)
    {
        await using var provider = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<LootTrackerRouteMap>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            { [nameof(LootTrackerRouteMap.Session)] = session, [nameof(LootTrackerRouteMap.MapId)] = "sample-map" }))).ToHtmlString());
    }
}
