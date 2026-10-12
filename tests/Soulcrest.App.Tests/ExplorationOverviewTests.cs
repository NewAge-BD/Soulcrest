using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class ExplorationOverviewTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly MapInfo[] Maps =
        [new("altgard", "Altgard", "Asmodian"), new("verteron", "Verteron", "Elyos"), new("elthen", "Eltnen", "Elyos")];
    private static readonly ExplorationPlace[] Places =
    [
        new("lost", "altgard", "dungeon", 0, "Lost Ruin Entrance", null, 160, 100),
        new("twisted", "altgard", "dungeon", 1, "Twisted Pit Entrance", null, 102, 100),
        new("rift", "altgard", "dungeon", 2, "Rift Fissure Entrance", null, 103, 100),
        new("city", "altgard", "dungeon", 3, "Ruins of the Ancient City of Ru", null, 101, 100),
        new("vein", "altgard", "dungeon", 4, "Hidden Vein Cave Entrance", "Eingang zur Versteckten Erzaderhöhle", 130, 140),
        new("den", "altgard", "stronghold", 5, "Graverobber Den", "Grabräuberversteck", 110, 100),
        new("stall", "altgard", "kibelisk", 6, "Shulak Street Stall", null, 100, 125),
        new("invalid", "altgard", "stronghold", 7, "Synthetic invalid-coordinate row", null, double.NaN, 100),
        new("other-map", "verteron", "dungeon", 0, "Distorted Cave Entrance", null, 100, 100),
        new("other-kind", "altgard", "pets", 0, "Synthetic unrelated category", null, 100, 100),
    ];

    private static ExplorationTrackingSnapshot Tracking(string map = "altgard", bool worldMap = false) =>
        new(true, true, false, worldMap, false, false, new(map, 100, 100, Now), new(map, worldMap, Now));
    private static ExplorationMapSelection Select(ExplorationTrackingSnapshot tracking, string? previous = "altgard",
        string? fallback = "verteron", string? pinned = null) =>
        ExplorationOverview.SelectMap(Maps, previous, fallback, pinned, tracking, Now);
    private static ExplorationOverviewSnapshot Overview(IReadOnlySet<string>? done = null, PlayerPosition? position = null,
        string search = "", string kind = "all", string status = "all") =>
        ExplorationOverview.Build(Places, done ?? new HashSet<string>(), "altgard", "en", position, search, kind, status);

    [Fact]
    public void AutomaticSelectionFollowsARecentlyDetectedZoneChange()
    {
        var changed = Select(Tracking("verteron"));
        Assert.Equal("verteron", changed.MapId);
        Assert.Equal("detected", changed.Source);
        Assert.False(changed.WorldMap);
        var returned = Select(Tracking("altgard"), previous: changed.MapId);
        Assert.Equal("altgard", returned.MapId);
        Assert.Equal("detected", returned.Source);
    }

    [Fact]
    public void ManualPinSurvivesDetectionUntilItIsReleased()
    {
        var tracking = Tracking("verteron");
        var pinned = Select(tracking, pinned: "altgard");
        Assert.Equal("altgard", pinned.MapId);
        Assert.Equal("pinned", pinned.Source);
        Assert.Equal("verteron", Select(tracking, pinned: null).MapId);
    }

    [Fact]
    public void UnknownPinCannotDisplaceAValidDetectedMap()
    {
        var selected = Select(Tracking("verteron"), pinned: "missing");
        Assert.Equal("verteron", selected.MapId);
        Assert.Equal("detected", selected.Source);
    }

    [Theory]
    [InlineData(-5, true)]
    [InlineData(-5.001, false)]
    [InlineData(.001, false)]
    public void DetectedMapMustBeRecentAndNeverFromTheFuture(double ageSeconds, bool accepted)
    {
        var tracking = Tracking("verteron");
        tracking = tracking with { DetectedMap = tracking.DetectedMap! with { At = Now.AddSeconds(ageSeconds) } };
        var selected = Select(tracking);
        Assert.Equal(accepted ? "verteron" : "altgard", selected.MapId);
        Assert.Equal(accepted ? "detected" : "previous", selected.Source);
    }

    [Theory]
    [InlineData("stopped")]
    [InlineData("missing")]
    [InlineData("paused")]
    [InlineData("instance")]
    [InlineData("esc")]
    public void UnavailableOrHiddenTrackingCannotChangeTheSelectedMap(string condition)
    {
        var tracking = Tracking("verteron");
        tracking = condition switch
        {
            "stopped" => tracking with { Running = false },
            "missing" => tracking with { Found = false },
            "paused" => tracking with { Paused = true },
            "instance" => tracking with { InInstance = true },
            _ => tracking with { EscMenuOpen = true },
        };
        var selected = Select(tracking);
        Assert.Equal("altgard", selected.MapId);
        Assert.Equal("previous", selected.Source);
        Assert.Null(ExplorationOverview.FreshPosition(tracking, Now));
    }

    [Fact]
    public void FreshWorldMapSelectionDoesNotPretendThePlayerMovedThere()
    {
        var tracking = Tracking("verteron", worldMap: true);
        var selected = Select(tracking);
        Assert.Equal("verteron", selected.MapId);
        Assert.Equal("detected", selected.Source);
        Assert.True(selected.WorldMap);
        Assert.Null(ExplorationOverview.FreshPosition(tracking, Now));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DetectionFromADifferentMapDisplayModeCannotBeReused(bool worldMap)
    {
        var tracking = Tracking("verteron", worldMap) with { WorldMapOpen = !worldMap };
        Assert.Equal("altgard", Select(tracking).MapId);
        Assert.Null(ExplorationOverview.FreshPosition(tracking, Now));
    }

    [Fact]
    public void MissingOrUnknownDetectionUsesPreviousThenFallbackThenFirstMap()
    {
        var tracking = Tracking("unknown");
        Assert.Equal("elthen", Select(tracking, previous: "elthen").MapId);
        Assert.Equal("verteron", Select(tracking, previous: "unknown").MapId);
        Assert.Equal("altgard", Select(tracking, previous: null, fallback: "unknown").MapId);
        Assert.Equal("previous", Select(tracking).Source);
        Assert.Equal("altgard", Select(tracking with { DetectedMap = null, Position = Tracking("verteron").Position }).MapId);
        Assert.Equal("", ExplorationOverview.SelectMap([], null, null, null, tracking, Now).MapId);
    }

    [Fact]
    public void FreshPositionRequiresAMatchingRecentDetection()
    {
        var tracking = Tracking();
        Assert.Equal(tracking.Position, ExplorationOverview.FreshPosition(tracking, Now));
        Assert.Null(ExplorationOverview.FreshPosition(tracking with { DetectedMap = null }, Now));
        Assert.Null(ExplorationOverview.FreshPosition(tracking with { Position = Tracking("verteron").Position }, Now));
        Assert.Null(ExplorationOverview.FreshPosition(tracking with { Position = null }, Now));
    }

    [Theory]
    [InlineData(-5, true)]
    [InlineData(-5.001, false)]
    [InlineData(.001, false)]
    public void PositionAndDetectionEachNeedIndependentFreshness(double ageSeconds, bool accepted)
    {
        var tracking = Tracking();
        var oldPosition = tracking with { Position = tracking.Position! with { At = Now.AddSeconds(ageSeconds) } };
        var oldDetection = tracking with { DetectedMap = tracking.DetectedMap! with { At = Now.AddSeconds(ageSeconds) } };
        Assert.Equal(accepted, ExplorationOverview.FreshPosition(oldPosition, Now) is not null);
        Assert.Equal(accepted, ExplorationOverview.FreshPosition(oldDetection, Now) is not null);
    }

    [Theory]
    [InlineData(double.NaN, 100)]
    [InlineData(100, double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity, 100)]
    public void NonFinitePlayerCoordinatesDoNotProduceDistances(double x, double y)
    {
        var tracking = Tracking() with { Position = new("altgard", x, y, Now) };
        Assert.Null(ExplorationOverview.FreshPosition(tracking, Now));
        var snapshot = Overview(position: tracking.Position);
        Assert.All(snapshot.Rows, row => Assert.Null(row.Distance));
        Assert.Null(snapshot.Next);
    }

    [Fact]
    public void OverviewMeasuresStraightLineDistanceInMapPixels()
    {
        var snapshot = Overview(position: new("altgard", 100, 100, Now));
        Assert.Equal(50d, snapshot.Rows.Single(row => row.Place.Id == "vein").Distance);
        Assert.Equal(25d, snapshot.Rows.Single(row => row.Place.Id == "stall").Distance);
        Assert.Null(snapshot.Rows.Single(row => row.Place.Id == "invalid").Distance);
    }

    [Fact]
    public void NoPositionOrAPositionOnAnotherMapClearsDistanceAndRecommendation()
    {
        foreach (var position in new PlayerPosition?[] { null, new("verteron", 100, 100, Now) })
        {
            var snapshot = Overview(position: position);
            Assert.All(snapshot.Rows, row => Assert.Null(row.Distance));
            Assert.Null(snapshot.Next);
        }
    }

    [Fact]
    public void RecommendationSkipsCompletedLockedAndInvalidPlaces()
    {
        var snapshot = Overview(new HashSet<string> { "den" }, new("altgard", 100, 100, Now));
        Assert.Equal("stall", snapshot.Next?.Place.Id);
        Assert.True(snapshot.Rows.Single(row => row.Place.Id == "city").Locked);
        Assert.True(snapshot.Rows.Single(row => row.Place.Id == "twisted").Locked);
        Assert.Null(snapshot.Rows.Single(row => row.Place.Id == "invalid").Distance);
    }

    [Fact]
    public void CompletingTheFirstDungeonUnlocksTheNearestNextChainStep()
    {
        var before = Overview(position: new("altgard", 100, 100, Now), kind: "dungeon");
        Assert.Equal("vein", before.Next?.Place.Id);
        var after = Overview(new HashSet<string> { "lost" }, new("altgard", 100, 100, Now), kind: "dungeon");
        Assert.Equal("twisted", after.Next?.Place.Id);
        Assert.Empty(after.Rows.Single(row => row.Place.Id == "twisted").Missing);
        Assert.Equal(["twisted"], after.Rows.Single(row => row.Place.Id == "rift").Missing.Select(place => place.Id));
    }

    [Fact]
    public void CategoryControlsRecommendationWhileSearchAndStatusOnlyFilterTheChecklist()
    {
        var done = new HashSet<string> { "lost" };
        var baseline = Overview(done, new("altgard", 100, 100, Now), kind: "dungeon");
        var filtered = Overview(done, new("altgard", 100, 100, Now), search: "no matching place", kind: "dungeon", status: "done");
        Assert.Empty(filtered.Rows);
        Assert.Equal("twisted", baseline.Next?.Place.Id);
        Assert.Equal(baseline.Next?.Place.Id, filtered.Next?.Place.Id);
        Assert.Equal("den", Overview(done, new("altgard", 100, 100, Now), kind: "stronghold").Next?.Place.Id);
        Assert.Equal("stall", Overview(done, new("altgard", 100, 100, Now), kind: "kibelisk").Next?.Place.Id);
    }

    [Fact]
    public void FiltersDoNotChangeMapOrCategoryProgressCounts()
    {
        var done = new HashSet<string> { "lost", "den", "other-map", "other-kind", "unknown" };
        var baseline = Overview(done);
        var filtered = Overview(done, search: "no matching place", kind: "dungeon", status: "locked");
        Assert.Equal("altgard", filtered.MapId);
        Assert.Equal(8, baseline.Total);
        Assert.Equal(2, baseline.Completed);
        Assert.Equal(baseline.Total, filtered.Total);
        Assert.Equal(baseline.Completed, filtered.Completed);
        Assert.Equal(baseline.Categories, filtered.Categories);
        Assert.Equal(new ExplorationCategorySummary("dungeon", 1, 5), filtered.Categories.Single(category => category.Kind == "dungeon"));
        Assert.Equal(new ExplorationCategorySummary("stronghold", 1, 2), filtered.Categories.Single(category => category.Kind == "stronghold"));
        Assert.Equal(new ExplorationCategorySummary("kibelisk", 0, 1), filtered.Categories.Single(category => category.Kind == "kibelisk"));
    }

    [Fact]
    public void OpenChecklistIncludesLockedPlacesWhileLockedFilterIsExclusive()
    {
        var done = new HashSet<string> { "den" };
        var open = Overview(done, status: "open");
        var locked = Overview(done, status: "locked");
        Assert.Contains(open.Rows, row => row.Place.Id == "twisted" && row.Locked);
        Assert.Contains(open.Rows, row => row.Place.Id == "stall" && !row.Locked);
        Assert.DoesNotContain(open.Rows, row => row.Done);
        Assert.All(locked.Rows, row => Assert.True(row.Locked));
        Assert.Equal(new[] { "city", "rift", "twisted" }, locked.Rows.Select(row => row.Place.Id).Order());
        Assert.Equal(["den"], Overview(done, status: "done").Rows.Select(row => row.Place.Id));
    }

    [Fact]
    public void ACompletedDungeonKeepsItsPrerequisiteInformationWithoutBeingShownAsLocked()
    {
        var done = new HashSet<string> { "rift" };
        var row = Overview(done).Rows.Single(row => row.Place.Id == "rift");
        Assert.True(row.Done);
        Assert.False(row.Locked);
        Assert.Equal(new[] { "lost", "twisted" }, row.Missing.Select(place => place.Id).Order());
        Assert.DoesNotContain(Overview(done, status: "locked").Rows, item => item.Place.Id == "rift");
    }

    [Theory]
    [InlineData("HIDDEN", "vein")]
    [InlineData("Erzaderhöhle", "vein")]
    [InlineData("hidden Erzaderhöhle", "vein")]
    [InlineData("  hidden   Erzaderhöhle  ", "vein")]
    [InlineData("GRABRÄUBER", "den")]
    public void SearchMatchesBothClientLanguagesAndRequiresEveryTerm(string search, string expected)
    {
        Assert.Equal([expected], Overview(search: search).Rows.Select(row => row.Place.Id));
        Assert.Empty(Overview(search: search + " unmatchedterm").Rows);
    }

    [Fact]
    public void DuplicatePlaceIdsAndUnrelatedKindsCannotInflateCounts()
    {
        var places = Places.Concat([Places[0], Places[4] with { X = 999 }]).ToArray();
        var snapshot = ExplorationOverview.Build(places, new HashSet<string> { "lost" }, "altgard", "en");
        Assert.Equal(8, snapshot.Total);
        Assert.Equal(1, snapshot.Completed);
        Assert.Equal(8, snapshot.Rows.Select(row => row.Place.Id).Distinct().Count());
        Assert.DoesNotContain(snapshot.Rows, row => row.Place.Id is "other-map" or "other-kind");
    }

    [Fact]
    public void EmptyMapStillShowsTheThreeCategoryCounters()
    {
        var snapshot = ExplorationOverview.Build([], new HashSet<string>(), "altgard", "en");
        Assert.Equal(0, snapshot.Total);
        Assert.Equal(0, snapshot.Completed);
        Assert.Equal(new[] { "dungeon", "kibelisk", "stronghold" }, snapshot.Categories.Select(category => category.Kind).Order());
        Assert.All(snapshot.Categories, category => { Assert.Equal(0, category.Total); Assert.Equal(0, category.Completed); });
        Assert.Empty(snapshot.Rows);
        Assert.Null(snapshot.Next);
    }
}
