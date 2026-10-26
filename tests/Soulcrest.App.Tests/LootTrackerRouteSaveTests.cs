using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class LootTrackerRouteSaveTests
{
    [Fact]
    public void PreviewPreservesOriginalPixelsAndReturnsSeparateContinuousPartsWithoutClosingTheLoop()
    {
        var session = new LootTrackerSession
        {
            RouteSegments = [
                new("altgard", "Altgard", [new(10.25, 20.5), new(10.25, 20.5), new(30.75, 40.125), new(10.25, 20.5)]),
                new("altgard", "Altgard", [new(100, 200), new(300, 400)]),
                new("verteron", "Verteron", [new(500, 600), new(700, 800)]),
            ],
        };
        var parts = LootTrackerRouteSaveService.Preview(session);
        Assert.Equal(3, parts.Count);
        Assert.Equal(["altgard", "altgard", "verteron"], parts.Select(part => part.MapId));
        Assert.Equal([(10.25, 20.5), (30.75, 40.125), (10.25, 20.5)],
            parts[0].Stops.Select(stop => (stop.X, stop.Y)));
        Assert.Equal([(100.0, 200.0), (300.0, 400.0)], parts[1].Stops.Select(stop => (stop.X, stop.Y)));
        Assert.All(parts.SelectMany(part => part.Stops), stop =>
        {
            Assert.Equal("Waypoint", stop.Kind);
            Assert.Equal("", stop.Name);
            Assert.Null(stop.PetId);
        });
    }

    [Theory]
    [InlineData(double.NaN, 0)]
    [InlineData(double.PositiveInfinity, 0)]
    [InlineData(0, double.NegativeInfinity)]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    public void InvalidSamplesSeparateThePathAndNeverCreateAShortcut(double x, double y)
    {
        var session = new LootTrackerSession
        {
            RouteSegments = [new("altgard", "Altgard", [new(10, 20), new(30, 40), new(x, y), new(50, 60), new(70, 80)])],
        };
        var parts = LootTrackerRouteSaveService.Preview(session);
        Assert.Equal(2, parts.Count);
        Assert.Equal([(10.0, 20.0), (30.0, 40.0)], parts[0].Stops.Select(stop => (stop.X, stop.Y)));
        Assert.Equal([(50.0, 60.0), (70.0, 80.0)], parts[1].Stops.Select(stop => (stop.X, stop.Y)));
    }

    [Fact]
    public void NoPathIsInventedFromAnEmptyMapOnePositionOrRepeatedStandingSamples()
    {
        var session = new LootTrackerSession
        {
            Location = "Altgard", Mobs = [new("synthetic-mob", "Sample monster", 100)],
            RouteSegments = [new("altgard", "Altgard", []), new("altgard", "Altgard", [new(10, 20)]),
                new("altgard", "Altgard", [new(30, 40), new(30, 40)]),
                new("", "Unknown map", [new(50, 60), new(70, 80)])],
        };
        Assert.Empty(LootTrackerRouteSaveService.Preview(session));
        Assert.Empty(LootTrackerRouteSaveService.Preview(new()));
    }

    [Fact]
    public void OnlySourceBoundResourceNamesAnnotateOriginalPlayerPointsAcrossPreservedGaps()
    {
        var session = new LootTrackerSession
        {
            Kind = LootTrackerSessionKind.Grind,
            RouteSegments = [new("altgard", "Altgard", [new(10, 20), new(30, 40), new(double.NaN, 0), new(50, 60), new(70, 80)])],
            ResourceNodes = [
                new("synthetic-herb", "Sample herb", "altgard", "Altgard", 31, 42, 0, 1),
                new("synthetic-ore", "Sample ore", "altgard", "Altgard", 50, 60, 0, 3),
                new("synthetic-nearby", "Unbound nearby node", "altgard", "Altgard", 30.001, 40, 0, -1),
                new("synthetic-foreign", "Other map node", "verteron", "Verteron", 30, 40, 0, 1),
                new("synthetic-dangling", "Unbound node", "altgard", "Altgard", 70, 80, 0, 99),
            ],
        };

        var parts = LootTrackerRouteSaveService.Preview(session);

        Assert.Equal(2, parts.Count);
        Assert.Equal(["", "Sample herb"], parts[0].Stops.Select(stop => stop.Name));
        Assert.Equal(["Sample ore", ""], parts[1].Stops.Select(stop => stop.Name));
        Assert.Equal([(10.0, 20.0), (30.0, 40.0)], parts[0].Stops.Select(stop => (stop.X, stop.Y)));
        Assert.Equal([(50.0, 60.0), (70.0, 80.0)], parts[1].Stops.Select(stop => (stop.X, stop.Y)));
        Assert.All(parts.SelectMany(part => part.Stops), stop => Assert.Equal("Waypoint", stop.Kind));
    }

    [Fact]
    public void MultipleConfirmedNodeNamesAtTheSameStandingPositionDoNotInventATravelledRoute()
    {
        var session = new LootTrackerSession
        {
            Kind = LootTrackerSessionKind.Lifeskill,
            RouteSegments = [new("altgard", "Altgard", [new(10, 20), new(10, 20)])],
            ResourceNodes = [new("synthetic-herb", "Sample herb", "altgard", "Altgard", 10, 20, 0, 0),
                new("synthetic-ore", "Sample ore", "altgard", "Altgard", 10, 20, 0, 1)],
        };
        Assert.Empty(LootTrackerRouteSaveService.Preview(session));
        Assert.Empty(LootTrackerRouteSaveService.Preview(session with { RouteSegments = [] }));
    }

    [Fact]
    public void SavingAddsInactiveRoutesAndPreservesTheExistingRouteAndActiveTargetBytes()
    {
        using var fixture = new LibraryFixture();
        using var targets = new MapTargetsService();
        targets.Toggle("altgard", 1000, 2000, "Existing A", "Waypoint", null);
        targets.Toggle("altgard", 1100, 2100, "Existing B", "Waypoint", null, chain: true);
        var existing = targets.SaveRoute("altgard", "Existing route")!;
        var previousTargets = targets.Targets.ToArray();
        var previousTargetBytes = File.ReadAllBytes(AppPaths.TargetsFile);
        var changes = 0; targets.Changed += () => changes++;
        var service = new LootTrackerRouteSaveService(targets);

        var saved = service.Save(PathSession(), "  Successful session  ");

        var route = Assert.Single(saved);
        Assert.Equal("Successful session", route.Name);
        Assert.False(route.Repeat);
        Assert.False(route.IsLeveling);
        Assert.Null(route.Category);
        Assert.Equal(2, route.Stops.Count);
        Assert.Equal(previousTargets, targets.Targets);
        Assert.Equal(previousTargetBytes, File.ReadAllBytes(AppPaths.TargetsFile));
        Assert.Equal([existing.Id], targets.ActiveRoutes);
        Assert.Equal(1, changes);
        var restored = RouteFile.Load(AppPaths.RoutesFile);
        Assert.Equal([existing.Id, route.Id], restored.Select(row => row.Id));
        Assert.Equal(route.Stops, restored[1].Stops);
    }

    [Fact]
    public void SeparatePartsSaveAsSeparatelyNamedRoutesAndDeliberateResaveCreatesIndependentCopies()
    {
        using var fixture = new LibraryFixture();
        using var targets = new MapTargetsService();
        var service = new LootTrackerRouteSaveService(targets);
        var session = PathSession() with { RouteSegments = [.. PathSession().RouteSegments,
            new("verteron", "Verteron", [new(50, 60), new(70, 80)])] };

        var first = service.Save(session, "Sample farming route");
        var second = service.Save(session, "Sample farming route");

        Assert.Equal(["Sample farming route · 1/2 · Altgard", "Sample farming route · 2/2 · Verteron"],
            first.Select(route => route.Name));
        Assert.Equal(4, targets.Routes.Count);
        Assert.Equal(4, targets.Routes.Select(route => route.Id).Distinct().Count());
        Assert.Empty(targets.Targets);
        Assert.Empty(targets.ActiveRoutes);
        Assert.False(File.Exists(AppPaths.TargetsFile));
        Assert.NotEqual(first[0].Id, second[0].Id);
    }

    [Fact]
    public void InvalidRouteBatchDoesNotAlterMemoryOrReplaceTheExistingLibrary()
    {
        using var fixture = new LibraryFixture();
        using var targets = new MapTargetsService();
        var first = Assert.Single(new LootTrackerRouteSaveService(targets).Save(PathSession(), "Existing"));
        var original = File.ReadAllBytes(AppPaths.RoutesFile);
        var changes = 0; targets.Changed += () => changes++;

        Assert.Throws<InvalidDataException>(() => targets.AddSavedRoutes([
            new("valid-new-id", "Valid new route", [new("altgard", 1, 2, "", "Waypoint", null)]),
            first with { Name = "Duplicate identifier" },
        ]));

        Assert.Equal(first.Id, Assert.Single(targets.Routes).Id);
        Assert.Equal(original, File.ReadAllBytes(AppPaths.RoutesFile));
        Assert.Equal(0, changes);
    }

    [Fact]
    public void FailedPersistenceDoesNotPublishOrRetainAnUnsavedRoute()
    {
        using var fixture = new LibraryFixture();
        using var targets = new MapTargetsService();
        var changes = 0; targets.Changed += () => changes++;
        Directory.CreateDirectory(AppPaths.RoutesFile);
        try
        {
            var error = Record.Exception(() => new LootTrackerRouteSaveService(targets).Save(PathSession(), "Cannot save"));
            Assert.True(error is IOException or UnauthorizedAccessException);
            Assert.Empty(targets.Routes);
            Assert.Empty(targets.Targets);
            Assert.Equal(0, changes);
        }
        finally { Directory.Delete(AppPaths.RoutesFile); }
    }

    [Fact]
    public void EmptyNameOrUnusableRouteLeavesTheLibraryUntouchedAndSavedStopsAreDetached()
    {
        using var fixture = new LibraryFixture();
        using var targets = new MapTargetsService();
        var service = new LootTrackerRouteSaveService(targets);
        Assert.Throws<InvalidDataException>(() => service.Save(PathSession(), "  "));
        Assert.Throws<InvalidDataException>(() => service.Save(new(), "No path"));
        Assert.Empty(targets.Routes);
        Assert.False(File.Exists(AppPaths.RoutesFile));

        RouteStop[] supplied = [new("altgard", 1, 2, "", "Waypoint", null), new("altgard", 3, 4, "", "Waypoint", null)];
        var added = targets.AddSavedRoutes([new("synthetic-detached", "Copied route", supplied)]);
        supplied[0] = supplied[0] with { X = 900 };
        Assert.Equal(1, Assert.Single(targets.Routes).Stops[0].X);
        var readOnlyStops = Assert.IsAssignableFrom<IList<RouteStop>>(added[0].Stops);
        Assert.Throws<NotSupportedException>(() => readOnlyStops[0] = supplied[0]);
        var readOnlyRoutes = Assert.IsAssignableFrom<IList<SavedRoute>>(added);
        Assert.Throws<NotSupportedException>(() => readOnlyRoutes.Clear());
    }

    private static LootTrackerSession PathSession() => new()
    {
        RouteSegments = [new("altgard", "Altgard", [new(10.25, 20.5), new(30.75, 40.125)])],
    };

    /// <summary>The test module redirects AppPaths to a temporary folder before its first use.</summary>
    private sealed class LibraryFixture : IDisposable
    {
        private readonly Dictionary<string, byte[]?> _backups;
        internal LibraryFixture()
        {
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(AppPaths.DataDirectory), StringComparison.OrdinalIgnoreCase);
            _backups = new[] { AppPaths.RoutesFile, AppPaths.LegacyRoutesFile, AppPaths.TargetsFile }
                .ToDictionary(path => path, path => File.Exists(path) ? File.ReadAllBytes(path) : null);
            foreach (var path in _backups.Keys) File.Delete(path);
        }
        public void Dispose()
        {
            foreach (var (path, bytes) in _backups)
                if (bytes is null) File.Delete(path); else File.WriteAllBytes(path, bytes);
        }
    }
}
