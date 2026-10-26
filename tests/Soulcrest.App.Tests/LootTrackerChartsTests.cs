using System.Globalization;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class LootTrackerChartsTests
{
    [Fact]
    public void UnrepresentableScaleNeverProducesNonFiniteResourceMarkers()
    {
        var session = new LootTrackerSession
        {
            RouteSegments = [new("map", "Sample map", [new(0, 0), new(1e-308, 0)])],
            ResourceNodes = [new("node", "Sample resource", "map", "Sample map", 1e-308, 0, 0, 1)],
        };
        Assert.Empty(LootTrackerCharts.Route(session, "map"));
        Assert.Empty(LootTrackerCharts.ResourceNodes(session, "map"));
    }

    [Fact]
    public void InterruptedRouteSegmentsAreNeverConnected()
    {
        var session = new LootTrackerSession
        {
            RouteSegments =
            [
                new("isles", "Immortal Isles", [new(0, 0), new(10, 10)]),
                new("isles", "Immortal Isles", [new(90, 90), new(100, 100)]),
            ],
        };
        var route = LootTrackerCharts.Route(session, "isles");
        Assert.Equal(2, route.Count);
        Assert.All(route, line => Assert.Equal(2, Points(line.Points).Length));
        Assert.True(Parse(route[0].EndX) < Parse(route[1].StartX));
        Assert.True(Parse(route[0].EndY) < Parse(route[1].StartY));
    }

    [Fact]
    public void MapFilterExcludesOtherMapsFromBothRouteAndProjectionBounds()
    {
        var session = new LootTrackerSession
        {
            RouteSegments =
            [
                new("isles", "Immortal Isles", [new(0, 0), new(100, 0)]),
                new("other", "Other map", [new(-100000, -100000), new(100000, 100000)]),
            ],
        };
        var line = Assert.Single(LootTrackerCharts.Route(session, "isles"));
        Assert.Equal(30, Parse(line.StartX));
        Assert.Equal(410, Parse(line.EndX));
        Assert.Equal(80, Parse(line.StartY));
        Assert.Equal(80, Parse(line.EndY));
        Assert.Empty(LootTrackerCharts.Route(session, "missing"));
        Assert.Empty(LootTrackerCharts.Route(session, null));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1d)]
    public void InvalidMapCoordinateSplitsTheRouteInsteadOfBridgingTheGap(double invalid)
    {
        var session = new LootTrackerSession
        {
            RouteSegments = [new("isles", "Immortal Isles",
                [new(0, 0), new(10, 10), new(invalid, 15), new(20, 20), new(30, 30)])],
        };
        var route = LootTrackerCharts.Route(session, "isles");
        Assert.Equal(2, route.Count);
        Assert.All(route, line =>
        {
            var points = Points(line.Points);
            Assert.Equal(2, points.Length);
            Assert.All(points, point => Assert.True(double.IsFinite(point.X) && double.IsFinite(point.Y)));
        });
        Assert.True(Parse(route[0].EndX) < Parse(route[1].StartX));
    }

    [Fact]
    public void NegativePlayerMapYSplitsTheRouteAndRejectsItsResourceVisit()
    {
        var session = new LootTrackerSession
        {
            RouteSegments = [new("isles", "Immortal Isles",
                [new(0, 0), new(10, 10), new(15, -1), new(20, 20), new(30, 30)])],
            ResourceNodes = [new("invalid", "Example resource", "isles", "Immortal Isles", 15, 15, 0, 2)],
        };
        var route = LootTrackerCharts.Route(session, "isles");
        Assert.Equal(2, route.Count);
        Assert.All(route, line => Assert.Equal(2, Points(line.Points).Length));
        Assert.True(Parse(route[0].EndX) < Parse(route[1].StartX));
        Assert.Empty(LootTrackerCharts.ResourceNodes(session, "isles"));
    }

    [Fact]
    public void ResourceNodeOrderIsGlobalAndMapFilterPreservesSeparateRouteSegments()
    {
        var session = new LootTrackerSession
        {
            Kind = LootTrackerSessionKind.Lifeskill,
            RouteSegments =
            [
                new("isles", "Immortal Isles", [new(0, 0), new(100, 0)]),
                new("other", "Other map", [new(100000, 100000), new(200000, 200000)]),
                new("isles", "Immortal Isles", [new(200, 0), new(300, 0)]),
            ],
            ResourceNodes =
            [
                new("first", "Example resource", "isles", "Immortal Isles", 50, 0, 0, 0),
                new("second", "Other resource", "other", "Other map", 150000, 150000, 1, 0),
                new("third", "Example resource", "isles", "Immortal Isles", 250, 0, 2, 0),
            ],
        };
        var isles = LootTrackerCharts.ResourceNodes(session, "isles");
        Assert.Equal(new[] { "first", "third" }, isles.Select(node => node.NodeId));
        Assert.Equal(new[] { 1, 3 }, isles.Select(node => node.Order));
        Assert.Equal(2, Assert.Single(LootTrackerCharts.ResourceNodes(session, "other")).Order);
        var route = LootTrackerCharts.Route(session, "isles");
        Assert.Equal(2, route.Count);
        Assert.Equal(30, Parse(route[0].StartX));
        Assert.Equal(410, Parse(route[1].EndX));
        Assert.True(Parse(route[0].EndX) < Parse(route[1].StartX));
        Assert.All(isles, node => Assert.InRange(Parse(node.X), 30, 410));
        Assert.Empty(LootTrackerCharts.ResourceNodes(session, "missing"));
        Assert.Empty(LootTrackerCharts.ResourceNodes(session, null));
    }

    [Fact]
    public void CatalogueNodeAndPlayerPositionsUseCommonBoundsWithoutMovingThePlayerTrack()
    {
        var session = new LootTrackerSession
        {
            Kind = LootTrackerSessionKind.Lifeskill,
            RouteSegments = [new("isles", "Immortal Isles", [new(100, 100), new(200, 100)])],
            ResourceNodes = [new("node", "Example resource", "isles", "Immortal Isles", 300, 100, 0, 0)],
        };
        var line = Assert.Single(LootTrackerCharts.Route(session, "isles"));
        var node = Assert.Single(LootTrackerCharts.ResourceNodes(session, "isles"));
        Assert.Equal(30, Parse(line.StartX));
        Assert.Equal(220, Parse(line.EndX));
        Assert.Equal(410, Parse(node.X));
        Assert.Equal(80, Parse(node.Y));
        Assert.Equal(80, Parse(line.StartY));
        Assert.Equal(80, Parse(line.EndY));
        Assert.Equal(100, session.RouteSegments[0].Points[0].X);
        Assert.Equal(300, session.ResourceNodes[0].X);
    }

    [Fact]
    public void AppendingARecordedPointAndResourceVisitKeepsThePreviousSnapshotUnchanged()
    {
        var original = new LootTrackerSession
        {
            Kind = LootTrackerSessionKind.Lifeskill,
            RouteSegments = [new("isles", "Immortal Isles", [new(100, 100)])],
            ResourceNodes = [new("first", "Example resource", "isles", "Immortal Isles", 110, 100, 0, 0)],
        };
        var extended = original with
        {
            RouteSegments = [original.RouteSegments[0] with { Points = original.RouteSegments[0].Points.Add(new(200, 100)) }],
            ResourceNodes = original.ResourceNodes.Add(new("second", "Other resource", "isles", "Immortal Isles", 210, 100, 0, 1)),
        };
        Assert.Single(original.RouteSegments[0].Points);
        Assert.Single(original.ResourceNodes);
        Assert.Single(LootTrackerCharts.ResourceNodes(original, "isles"));
        Assert.Equal(2, extended.RouteSegments[0].Points.Length);
        Assert.Equal(new[] { "first", "second" }, LootTrackerCharts.ResourceNodes(extended, "isles").Select(node => node.NodeId));
        Assert.Equal(new[] { 1, 2 }, LootTrackerCharts.ResourceNodes(extended, "isles").Select(node => node.Order));
        Assert.Equal(2, Points(Assert.Single(LootTrackerCharts.Route(extended, "isles")).Points).Length);
    }

    [Fact]
    public void PositiveNativeMapYProjectsDownwards()
    {
        var session = new LootTrackerSession
        {
            RouteSegments = [new("isles", "Immortal Isles", [new(40, 0), new(40, 100)])],
        };
        var line = Assert.Single(LootTrackerCharts.Route(session, "isles"));
        Assert.Equal(Parse(line.StartX), Parse(line.EndX));
        Assert.True(Parse(line.EndY) > Parse(line.StartY));
    }

    [Fact]
    public void RoutePreservesAspectRatioWithinItsViewport()
    {
        var session = new LootTrackerSession
        {
            RouteSegments = [new("isles", "Immortal Isles", [new(0, 0), new(200, 100)])],
        };
        var line = Assert.Single(LootTrackerCharts.Route(session, "isles"));
        var dx = Parse(line.EndX) - Parse(line.StartX);
        var dy = Parse(line.EndY) - Parse(line.StartY);
        Assert.Equal(2, dx / dy, 6);
        Assert.InRange(dx, 0, 380);
        Assert.InRange(dy, 0, 130);
        Assert.Equal(220, (Parse(line.StartX) + Parse(line.EndX)) / 2, 6);
        Assert.Equal(80, (Parse(line.StartY) + Parse(line.EndY)) / 2, 6);
    }

    [Fact]
    public void SinglePositionRemainsACenteredPointWithoutInventedMovement()
    {
        var session = new LootTrackerSession
        {
            RouteSegments = [new("isles", "Immortal Isles", [new(3920.5, 245.25)])],
        };
        var line = Assert.Single(LootTrackerCharts.Route(session, "isles"));
        var point = Assert.Single(Points(line.Points));
        Assert.Equal((220d, 80d), point);
        Assert.Equal(line.StartX, line.EndX);
        Assert.Equal(line.StartY, line.EndY);
    }

    [Fact]
    public void MissingAndUnknownIncomeMinutesSplitTheSeriesWithoutZeroInterpolation()
    {
        var session = new LootTrackerSession
        {
            MinuteIncome = [new(0, 0, 10), new(1, null, 0), new(2, 0, 20), new(4, 0, 40)],
        };
        var lines = LootTrackerCharts.Income(session);
        Assert.Equal(3, lines.Count);
        Assert.All(lines, line =>
        {
            var point = Assert.Single(Points(line.Points));
            Assert.True(point.Y < 135); // Every supplied known value was positive.
            Assert.Equal(point.X, Parse(line.LastX));
            Assert.Equal(point.Y, Parse(line.LastY));
            var area = Points(line.Area);
            Assert.Equal(3, area.Length);
            Assert.Equal(point.X, area[0].X);
            Assert.Equal(point.X, area[^1].X);
        });
    }

    [Theory]
    [InlineData(long.MaxValue, 1)]
    [InlineData(-1, 5)]
    [InlineData(5, -1)]
    public void OverflowAndNegativeIncomeRemainAGap(long bound, long unbound)
    {
        var session = new LootTrackerSession
        {
            MinuteIncome = [new(0, 0, 10), new(1, bound, unbound), new(2, 0, 20)],
        };
        var lines = LootTrackerCharts.Income(session);
        Assert.Equal(2, lines.Count);
        Assert.All(lines, line => Assert.Single(Points(line.Points)));
    }

    [Fact]
    public void KnownZeroIsRetainedAndUnorderedConsecutiveBinsAreSorted()
    {
        var session = new LootTrackerSession
        {
            MinuteIncome = [new(2, 0, 20), new(0, 0, 0), new(1, 0, 10)],
        };
        var line = Assert.Single(LootTrackerCharts.Income(session));
        var points = Points(line.Points);
        Assert.Equal(3, points.Length);
        Assert.Equal(135, points[0].Y);
        Assert.Equal(25, points[^1].Y);
        Assert.Equal(points[^1].X, Parse(line.LastX));
        Assert.Equal(points[^1].Y, Parse(line.LastY));
        Assert.True(points[0].X < points[1].X && points[1].X < points[2].X);
    }

    [Fact]
    public void MissingOrEntirelyInvalidObservationsProduceNoRouteOrIncome()
    {
        Assert.Empty(LootTrackerCharts.Route(new LootTrackerSession(), "isles"));
        Assert.Empty(LootTrackerCharts.Income(new LootTrackerSession()));
        Assert.Empty(LootTrackerCharts.Route(new LootTrackerSession
        {
            RouteSegments = [new("isles", "Immortal Isles", [new(0, double.NaN)])],
        }, "isles"));
        Assert.Empty(LootTrackerCharts.Income(new LootTrackerSession
        {
            MinuteIncome = [new(-1, 0, 2), new(0, null, 5), new(1, long.MaxValue, 1)],
        }));
    }

    [Fact]
    public void SvgCoordinatesAreInvariantUnderGermanDecimalFormatting()
    {
        var session = new LootTrackerSession
        {
            MinuteIncome = [new(0, 0, 1), new(1, 0, 2), new(2, 0, 3)],
            RouteSegments = [new("isles", "Immortal Isles", [new(0, 0), new(3, 2)])],
        };
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            var income = LootTrackerCharts.Income(session);
            var route = LootTrackerCharts.Route(session, "isles");
            Assert.Contains(".", Assert.Single(income).Points);
            Assert.Contains(".", Assert.Single(route).Points);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal(income, LootTrackerCharts.Income(session));
            Assert.Equal(route, LootTrackerCharts.Route(session, "isles"));
            Assert.All(Points(Assert.Single(LootTrackerCharts.Income(session)).Points),
                point => Assert.True(double.IsFinite(point.X) && double.IsFinite(point.Y)));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private static double Parse(string value) => double.Parse(value, CultureInfo.InvariantCulture);

    private static (double X, double Y)[] Points(string value) => value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Select(point => point.Split(','))
        .Select(pair => (Parse(pair[0]), Parse(pair[1])))
        .ToArray();
}
