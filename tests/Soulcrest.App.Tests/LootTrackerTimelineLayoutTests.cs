using System.Globalization;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class LootTrackerTimelineLayoutTests
{
    private static TimeSpan At(double seconds) => TimeSpan.FromSeconds(seconds);
    private static LootTrackerTimelineViewport View(double seconds = 120)
    { var view = new LootTrackerTimelineViewport(); view.UpdateDuration(At(seconds)); return view; }
    private static LootTrackerTimeline.Series Series(string key, params (double Seconds, long? Value)[] samples) =>
        new(key, key, samples.Select(row => new LootTrackerTimeline.Sample(At(row.Seconds), row.Value)).ToArray());

    internal static LootTrackerSession ResourceSession()
    {
        var at = DateTimeOffset.UnixEpoch;
        LootTrackerTimelineEvent Harvest(string id, int seconds, int? resource, bool success, string? item = null, long quantity = 0) =>
            new(id, success ? LootTrackerTimelineEventKind.HarvestSuccess : LootTrackerTimelineEventKind.HarvestFailure, at.AddSeconds(seconds), At(seconds))
            { Origin = LootTrackerActivityOrigin.Lifeskill, GatherableId = resource,
                Loot = item is null ? [] : [new(item, item, LootTrackerLootKind.Item, quantity)] };
        return new() { Id = "synthetic-resource-filter", Kind = LootTrackerSessionKind.Mixed, ProjectionViewMode = LootTrackerViewMode.Mixed,
            UnifiedVersion = 1, TimelineVersion = 1, StartedAt = at, Elapsed = At(180), ItemCount = 17, HarvestCount = 3, FailedHarvestCount = 3,
            SoulCount = 0, KinaUnbound = 100,
            TimelineEvents = [Harvest("a-success", 10, 7001, true, "ore-a", 2), Harvest("a-failure", 20, 7001, false),
                Harvest("b-success", 30, 7002, true, "ore-b", 3), Harvest("b-failure", 40, 7002, false),
                Harvest("named-success", 50, null, true, "herb", 5) with
                    { GatherVisit = new("named-visit", 0, "sample-map", "Example map", 100, 100, 100, 100, at.AddSeconds(50), true, "Synthetic herb") },
                Harvest("unknown-failure", 60, null, false),
                new("pve-drop", LootTrackerTimelineEventKind.Drop, at.AddSeconds(70), At(70))
                    { Origin = LootTrackerActivityOrigin.Pve, Loot = [new("pve-item", "PvE item", LootTrackerLootKind.Item, 7)] },
                new("gain", LootTrackerTimelineEventKind.KinaUnbound, at.AddSeconds(80), At(80))
                    { Origin = LootTrackerActivityOrigin.Pve, Amount = 100 }] };
    }

    [Fact]
    public void ResourceChoicesUseUnplacedStaticIdsThenConfirmedNamesAndKeepUnknownSeparate()
    {
        var choices = LootTrackerTimelineResources.Choices(ResourceSession());
        Assert.Equal(4, choices.Count);
        Assert.Equal(2, choices.Single(row => row.Key == "resource:7001").Attempts);
        Assert.Null(choices.Single(row => row.Key == "resource:7001").Name);
        Assert.Contains(choices, row => row.Key == "name:Synthetic herb" && row.Name == "Synthetic herb");
        Assert.Equal("unknown", choices[^1].Key);
    }

    [Fact]
    public void ResourceProjectionFiltersSuccessFailureAndRewardsButPreservesOtherOriginsAndSourceTotals()
    {
        var source = ResourceSession();
        var selected = LootTrackerTimelineResources.Project(source, new HashSet<string> { "resource:7001" });
        Assert.Equal(new[] { "a-success", "a-failure", "pve-drop", "gain" }, selected.TimelineEvents.Select(row => row.Id));
        var series = LootTrackerTimeline.Build(selected, LootTimelineValueMode.Cumulative);
        Assert.Equal(1, series.Single(row => row.Key == "success").ValueAt(source.Elapsed));
        Assert.Equal(1, series.Single(row => row.Key == "failure").ValueAt(source.Elapsed));
        Assert.Equal(9, series.Single(row => row.Key == "items").ValueAt(source.Elapsed));
        Assert.Equal(100, series.Single(row => row.Key == "kina").ValueAt(source.Elapsed));
        Assert.Equal(9, LootTrackerTimelineLayout.LootMoments(selected).SelectMany(row => row.Items).Sum(row => row.Quantity));
        Assert.Equal(8, source.TimelineEvents.Length); Assert.Equal(17, source.ItemCount); Assert.Equal(3, source.HarvestCount);
        var unknown = LootTrackerTimelineResources.Project(source, new HashSet<string> { "unknown" });
        Assert.Contains(unknown.TimelineEvents, row => row.Id == "unknown-failure");
        Assert.DoesNotContain(unknown.TimelineEvents, row => row.Id == "a-success");
        Assert.Contains(unknown.TimelineEvents, row => row.Id == "pve-drop");
    }

    [Fact]
    public void LegacyUnattributedAggregatesStayUnknownWhenResourceSelectionIsEmpty()
    {
        var legacy = ResourceSession() with { TimelineVersion = 0, TimelineEvents = [], HarvestCount = 9, FailedHarvestCount = 3 };
        var choice = Assert.Single(LootTrackerTimelineResources.Choices(legacy));
        Assert.Equal("unknown", choice.Key); Assert.Null(choice.Attempts);
        var projected = LootTrackerTimelineResources.Project(legacy, new HashSet<string>());
        Assert.Same(legacy, projected);
        Assert.Null(LootTrackerTimeline.Build(projected, LootTimelineValueMode.Cumulative).Single(row => row.Key == "success").ValueAt(legacy.Elapsed));
    }

    [Fact]
    public void ViewportKeepsElapsedClockAndClampsZoomToOneSecondOrOneHundredTwentyTimes()
    {
        var view = View(30);
        view.ZoomBy(1000);
        Assert.Equal(30, view.Zoom); Assert.Equal(At(1), view.Window);
        Assert.Equal(At(15), view.At(.5));
        view.Pan(1000); Assert.Equal(At(30), view.To);
        view.Pan(-1000); Assert.Equal(TimeSpan.Zero, view.From);
        Assert.Equal(view.From, view.At(-5)); Assert.Equal(view.To, view.At(9));
        view.Reset(); Assert.Equal(At(30), view.To); Assert.Equal(1, view.Zoom);
        view.UpdateDuration(At(3600)); view.ZoomBy(1000);
        Assert.Equal(120, view.Zoom); Assert.Equal(At(30), view.Window);
    }

    [Fact]
    public void PanUsesMeasuredPixelWidthAndFollowMovesOnlyAnOffscreenHead()
    {
        var view = View(); view.ZoomBy(4);
        Assert.Equal(At(45), view.From);
        view.PanPixels(100, 200); Assert.Equal(At(60), view.From);
        view.EnsureVisible(At(80)); Assert.Equal(At(60), view.From);
        view.EnsureVisible(At(120)); Assert.Equal(At(90), view.From);
        Assert.All(view.Ticks(), tick => Assert.InRange(tick, view.From, view.To));
        view.ZoomBy(double.NaN); view.Pan(double.PositiveInfinity); view.PanPixels(1, 0);
        Assert.Equal(At(90), view.From);
        view.UpdateDuration(TimeSpan.Zero); Assert.Equal(TimeSpan.Zero, view.At(.5)); Assert.Equal(1, view.Zoom);
        view.UpdateDuration(TimeSpan.MaxValue, reset: true);
        Assert.Equal(TimeSpan.MaxValue, view.At(1)); Assert.Equal(TimeSpan.Zero, view.From);
    }

    [Fact]
    public void AllValuesShareOnePlotWithIndependentSignedScalesAndSilverStyleLineAreas()
    {
        var plots = LootTrackerTimelineLayout.Plots([
            Series("ap", (0, 0), (20, 10000), (40, null), (80, 12000), (120, 12000)),
            Series("rp", (0, 0), (30, 2), (70, -7), (120, -7)),
            Series("kina", (0, 0), (120, 1000000)),
            Series("items", (0, 0), (120, 3)), Series("success", (0, 0), (120, 5))], View());
        var points = Assert.Single(plots); Assert.Equal("combined", points.Key);
        var ap = points.Curves.Single(row => row.Series.Key == "ap");
        var rp = points.Curves.Single(row => row.Series.Key == "rp");
        Assert.Equal("left", ap.Axis); Assert.Equal("right", rp.Axis);
        Assert.Equal(12000, ap.Maximum); Assert.Equal(-7, rp.Minimum); Assert.Equal(2, rp.Maximum);
        Assert.Equal(2, ap.Paths.Count); Assert.Equal(2, ap.Areas.Count); Assert.InRange(rp.Zero, 20, 150);
        Assert.All(points.Curves.Where(row => row.Series.Key is "ap" or "rp" or "kina"), row =>
        { Assert.True(row.IsLine); Assert.NotEmpty(row.Areas); Assert.Empty(row.Bars); });
        Assert.All(points.Curves.Where(row => row.Series.Key is "items" or "success"), row =>
        { Assert.False(row.IsLine); Assert.Empty(row.Paths); Assert.Empty(row.Areas); Assert.NotEmpty(row.Bars); });
        Assert.Equal(1000000, points.Curves.Single(row => row.Series.Key == "kina").Maximum);
        Assert.Equal(3, points.Curves.Single(row => row.Series.Key == "items").Maximum);
        Assert.Equal(5, points.Curves.Single(row => row.Series.Key == "success").Maximum);
    }

    [Fact]
    public void MonetaryGeometryConnectsDistinctConfirmedTimesWithoutChangingRecordedSteps()
    {
        var source = Series("kina", (0, 0), (60, 0), (60, 50), (120, 50), (120, 100));
        var samples = source.Samples.ToArray();
        var curve = Assert.Single(Assert.Single(LootTrackerTimelineLayout.Plots([source], View())).Curves);
        Assert.Equal("0,150 500,85 1000,20", Assert.Single(curve.Paths));
        Assert.Equal("0,150 0,150 500,85 1000,20 1000,150", Assert.Single(curve.Areas));
        Assert.Equal(0, source.ValueAt(At(30))); Assert.Equal(50, source.ValueAt(At(90)));
        Assert.Equal(samples, source.Samples);
    }

    [Fact]
    public void ZoomClipsLinearGeometryAtWindowEdgesWithoutChangingSourceValues()
    {
        var source = Series("ap", (0, 0), (120, 12000));
        var viewport = View(); viewport.ZoomBy(4);
        var curve = Assert.Single(Assert.Single(LootTrackerTimelineLayout.Plots([source], viewport)).Curves);
        Assert.Equal(At(45), viewport.From); Assert.Equal(At(75), viewport.To);
        Assert.Equal(7500, curve.Maximum); Assert.Equal("0,72 1000,20", Assert.Single(curve.Paths));
        Assert.Equal(0, source.ValueAt(At(60))); Assert.Equal(2, source.Samples.Count);
    }

    [Fact]
    public void UnknownIntervalsNeverAcquireAConnectingCurveOrAreaWhenZoomed()
    {
        var source = Series("rp", (0, 0), (40, 40), (40, null), (80, null), (80, -10), (120, 20));
        var full = Assert.Single(Assert.Single(LootTrackerTimelineLayout.Plots([source], View())).Curves);
        Assert.Equal(2, full.Paths.Count); Assert.Equal(2, full.Areas.Count);
        var viewport = View(); viewport.ZoomBy(4);
        var gap = Assert.Single(Assert.Single(LootTrackerTimelineLayout.Plots([source], viewport)).Curves);
        Assert.False(gap.HasValues); Assert.Empty(gap.Paths); Assert.Empty(gap.Areas);
        Assert.Equal(0, gap.ScaleMinimum); Assert.Equal(0, gap.ScaleMaximum);
        viewport.Pan(1);
        var recovery = Assert.Single(Assert.Single(LootTrackerTimelineLayout.Plots([source], viewport)).Curves);
        Assert.True(recovery.HasValues); Assert.Single(recovery.Paths);
        Assert.StartsWith("166.667,", recovery.Paths[0]);
    }

    [Fact]
    public void AvailabilityBoundaryAtOneTimestampStillSeparatesConfirmedSegments()
    {
        var source = Series("ap", (0, 0), (40, 40), (40, null), (40, 50), (120, 100));
        var curve = Assert.Single(Assert.Single(LootTrackerTimelineLayout.Plots([source], View())).Curves);
        Assert.Equal(2, curve.Paths.Count); Assert.Equal(2, curve.Areas.Count);
        Assert.EndsWith("333.333,98", curve.Paths[0]); Assert.StartsWith("333.333,85", curve.Paths[1]);
    }

    [Fact]
    public void UnknownAndConfirmedZeroHaveDistinctScaleMetadata()
    {
        var curves = Assert.Single(LootTrackerTimelineLayout.Plots([
            Series("ap", (0, null), (120, null)), Series("kina", (0, 0), (120, 0)),
            Series("souls", (0, null), (120, null)), Series("items", (0, 0), (120, 0))], View())).Curves;
        foreach (var key in new[] { "ap", "souls" })
        {
            var unknown = curves.Single(row => row.Series.Key == key);
            Assert.False(unknown.HasValues); Assert.Empty(unknown.Paths); Assert.Empty(unknown.Areas); Assert.Empty(unknown.Bars);
            Assert.Equal(0, unknown.ScaleMinimum); Assert.Equal(0, unknown.ScaleMaximum);
        }
        foreach (var key in new[] { "kina", "items" })
        {
            var zero = curves.Single(row => row.Series.Key == key);
            Assert.True(zero.HasValues); Assert.Equal(0, zero.ScaleMinimum); Assert.Equal(0, zero.ScaleMaximum);
            Assert.Equal(1, zero.Maximum);
        }
    }

    [Fact]
    public void QuantityBarsUseSharedFixedBinsButIndependentScalesAndKeepTerminalFacts()
    {
        var curves = Assert.Single(LootTrackerTimelineLayout.Plots([
            Series("items", (0, 0), (60, 3), (120, 9)), Series("success", (0, 0), (60, 1), (120, 2))], View())).Curves;
        var items = curves.Single(row => row.Series.Key == "items"); var successes = curves.Single(row => row.Series.Key == "success");
        Assert.InRange(items.Bars.Count, 2, 98);
        Assert.Equal(items.Bars.Select(row => (row.From, row.To)), successes.Bars.Select(row => (row.From, row.To)));
        Assert.Equal(9, items.Maximum); Assert.Equal(2, successes.Maximum);
        Assert.Equal(9, items.Bars[^1].Value); Assert.Equal(2, successes.Bars[^1].Value);
        Assert.All(items.Bars, bar => { Assert.True(bar.To > bar.From); Assert.InRange(bar.X, 0, 1000);
            Assert.InRange(bar.X + bar.Width, 0, 1000); Assert.InRange(bar.Y, 20, 150); Assert.InRange(bar.Height, 0, 130); });
        var viewport = View(); viewport.ZoomBy(2); viewport.Pan(.1);
        var shifted = Assert.Single(Assert.Single(LootTrackerTimelineLayout.Plots([items.Series], viewport)).Curves).Bars;
        Assert.All(shifted.Skip(1), bar => Assert.Equal(0, bar.From.TotalSeconds % 1));
    }

    [Fact]
    public void QuantityBinsContainingActualUnknownSpansAreOmittedWithoutFillingTheGap()
    {
        var source = Series("items", (0, 0), (20, 3), (40, 3), (40, null), (80, null), (80, 5), (120, 6));
        var curve = Assert.Single(Assert.Single(LootTrackerTimelineLayout.Plots([source], View())).Curves);
        Assert.All(curve.Bars, bar => Assert.True(bar.To <= At(40) || bar.From >= At(80)));
        Assert.Contains(curve.Bars, bar => bar.To == At(40)); Assert.Contains(curve.Bars, bar => bar.From == At(80));
        Assert.Equal(6, curve.ScaleMaximum); Assert.Equal(6, curve.Bars[^1].Value);
    }

    [Fact]
    public void SignedLongExtremesAndZeroOrMaximumDurationProduceFiniteBoundedGeometry()
    {
        var source = Series("rp", (0, long.MinValue), (60, 0), (120, long.MaxValue));
        var curve = Assert.Single(Assert.Single(LootTrackerTimelineLayout.Plots([source], View())).Curves);
        Assert.Equal(long.MinValue, curve.ScaleMinimum); Assert.Equal(long.MaxValue, curve.ScaleMaximum);
        Assert.All(curve.Paths.Concat(curve.Areas).SelectMany(path => path.Split(' ')).SelectMany(point => point.Split(',')), value =>
            Assert.True(double.IsFinite(double.Parse(value, CultureInfo.InvariantCulture))));
        Assert.InRange(curve.Zero, 20, 150);
        var empty = Assert.Single(Assert.Single(LootTrackerTimelineLayout.Plots([Series("items", (0, 0))], View(0))).Curves);
        Assert.Empty(empty.Bars); Assert.True(empty.HasValues);
        var maximum = new LootTrackerTimelineViewport(); maximum.UpdateDuration(TimeSpan.MaxValue);
        var large = new LootTrackerTimeline.Series("items", "items", [new(TimeSpan.Zero, 0), new(TimeSpan.MaxValue, long.MaxValue)]);
        var bars = Assert.Single(Assert.Single(LootTrackerTimelineLayout.Plots([large], maximum)).Curves).Bars;
        Assert.InRange(bars.Count, 1, 98); Assert.Equal(long.MaxValue, bars[^1].Value);
        Assert.All(bars, bar => { Assert.True(double.IsFinite(bar.X)); Assert.True(double.IsFinite(bar.Width)); Assert.True(double.IsFinite(bar.Height)); });
        Assert.Empty(LootTrackerTimelineLayout.Plots([], View(0)));
    }

    [Fact]
    public void ZoomedCurveBoundsUseVisibleValuesWithoutFlatteningNegativeValues()
    {
        var view = View(); view.ZoomBy(4); view.Pan(-100);
        var curve = LootTrackerTimelineLayout.Plots([Series("rp", (0, -10), (30, 10), (100, 1000000))], view).Single().Curves.Single();
        Assert.Equal(-10, curve.Minimum); Assert.Equal(10, curve.Maximum);
        Assert.Equal(85, curve.Zero);
        Assert.Equal(150, LootTrackerTimelineLayout.Y(-10, -10, 10));
        Assert.Equal(20, LootTrackerTimelineLayout.Y(10, -10, 10));
    }

    [Fact]
    public void EventClustersDependOnRealPixelsAndZoomAndPreserveEverySourceEvent()
    {
        var events = Enumerable.Range(0, 3).Select(index => new LootTrackerTimeline.Event("e" + index, "kill", At(index * 3), DateTimeOffset.UnixEpoch, "Kill")).ToArray();
        var view = View();
        var narrow = LootTrackerTimelineLayout.ClusterEvents(events, view, 100);
        Assert.Single(narrow); Assert.Equal(3, narrow.Single().Events.Count);
        Assert.Equal(3, LootTrackerTimelineLayout.ClusterEvents(events, view, 2000).Count);
        view.ZoomBy(10); view.Pan(-100);
        Assert.Equal(3, LootTrackerTimelineLayout.ClusterEvents(events, view, 200).Count);
    }

    [Fact]
    public void LootClustersSumOnlySelectedItemsAndKeepEveryExactMoment()
    {
        var moments = new[] {
            new LootTrackerTimelineLayout.LootMoment("a", At(10), DateTimeOffset.UnixEpoch, [new("Item:1", "One", 2, null), new("Item:2", "Two", 8, null)]),
            new LootTrackerTimelineLayout.LootMoment("b", At(11), DateTimeOffset.UnixEpoch, [new("Item:1", "One", 4, null)]) };
        var groups = LootTrackerTimelineLayout.ClusterLoot(moments, View(), 200, new HashSet<string> { "Item:1" });
        var group = Assert.Single(groups);
        Assert.Equal(2, group.Moments.Count); Assert.Equal(6, Assert.Single(group.Items).Quantity);
        Assert.Equal(2, group.Moments[0].Items.Single().Quantity);
        Assert.Equal(4, group.Moments[1].Items.Single().Quantity);
        Assert.Empty(LootTrackerTimelineLayout.ClusterLoot(moments, View(), 200, new HashSet<string>()));
    }

    [Fact]
    public void PixelClustersAlsoMergeMarkersWhoseRenderedRectanglesMeetAtTheRightEdge()
    {
        var view = View(100);
        var events = new[] { new LootTrackerTimeline.Event("near", "kill", At(85), DateTimeOffset.UnixEpoch, "Kill"),
            new LootTrackerTimeline.Event("end", "kill", At(100), DateTimeOffset.UnixEpoch, "Kill") };
        Assert.Single(LootTrackerTimelineLayout.ClusterEvents(events, view, 200));
        var moments = events.Select(row => new LootTrackerTimelineLayout.LootMoment(row.Id, row.Elapsed, row.At,
            [new("Item:1", "One", 1, null)])).ToArray();
        var cluster = Assert.Single(LootTrackerTimelineLayout.ClusterLoot(moments, view, 200, new HashSet<string> { "Item:1" }));
        Assert.Equal(2, cluster.Items.Single().Quantity);
    }

    [Fact]
    public void ViewModeProjectionControlsValidLayersRatherThanPhysicalSessionKind()
    {
        var session = new LootTrackerSession { Kind = LootTrackerSessionKind.Abyss, ProjectionViewMode = LootTrackerViewMode.Lifeskill };
        var layers = LootTrackerTimelineLayers.Available(session, [Series("items", (0, 0)), Series("success", (0, 0))]);
        Assert.Contains(layers, row => row.Id == "gather"); Assert.DoesNotContain(layers, row => row.Id == "rank");
        Assert.Equal(LootTrackerTimelineLayers.All.Count, LootTrackerTimelineLayers.Normalize(null).Length);
        Assert.Empty(LootTrackerTimelineLayers.Normalize([]));
        Assert.Equal(["ap", "rp"], LootTrackerTimelineLayers.Normalize(["rp", "unknown", "ap", "rp"]));
    }
}
