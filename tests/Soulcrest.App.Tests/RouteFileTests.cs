using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Portable route files use isolated folders, never the user's AppData library.</summary>
public sealed class RouteFileTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "Soulcrest-route-file-" + Guid.NewGuid().ToString("N"));
    private string Library => Path.Combine(_folder, "routes.soulroute");
    private string Legacy => Path.Combine(_folder, "routes.json");

    public RouteFileTests() => Directory.CreateDirectory(_folder);
    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static SavedRoute[] Routes() =>
    [
        new("manual-id", "Sapphire Farm",
        [
            new("altgard", 4111.25, 2222.5, "Sapphire", "Resources · Gem", "icons/sapphire.png", Color: "#abcdef"),
            new("altgard", 4111.25, 2222.5, "Repeat visit", "Pets · Fera", null, "pet-123")
        ], true, "#38bdf8"),
        new("episode-id", "Asmodian · Episode 6 · Level 40–45",
        [
            new("altgard", 100.125, 200.875, "Daybreak Society", "NPCs · Hero Quest", "icons/quest.png", Color: LevelingRouteStyle.MainQuest),
            new("altgard", 100.125, 200.875, "", "NPCs", null, Color: LevelingRouteStyle.RegionalQuest),
            new("altgard", 300, 400, "Kibelisk", "Locations · Kibelisk", null, Color: LevelingRouteStyle.Teleport),
            new("altgard", 500, 600, "Sealed Dungeon", "Locations · Sealed Dungeon", null, Color: LevelingRouteStyle.Exploration),
            new("altgard", 700, 800, "", "Waypoint", null, Color: LevelingRouteStyle.Exploration)
        ], false, "#facc15", LevelingRouteStyle.Category)
    ];

    private static void AssertRoutes(IReadOnlyList<SavedRoute> expected, IReadOnlyList<SavedRoute> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Id, actual[i].Id);
            Assert.Equal(expected[i].Name, actual[i].Name);
            Assert.Equal(expected[i].Category, actual[i].Category);
            Assert.Equal(expected[i].Repeat, actual[i].Repeat);
            Assert.Equal(expected[i].Color, actual[i].Color);
            Assert.Equal(expected[i].Stops, actual[i].Stops);
        }
    }

    private static JsonObject Document()
    {
        using var stream = new MemoryStream();
        RouteFile.Write(stream, Routes());
        return JsonNode.Parse(stream.ToArray())!.AsObject();
    }

    [Fact]
    public void MixedManualAndEpisodeRoutesRoundTripEveryDomainValue()
    {
        var routes = Routes();
        RouteFile.Save(Library, routes);
        AssertRoutes(routes, RouteFile.Load(Library));
        using var json = JsonDocument.Parse(File.ReadAllText(Library));
        Assert.Equal("soulcrest.routes", json.RootElement.GetProperty("format").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("map-pixels", json.RootElement.GetProperty("coordinates").GetString());

        var entries = json.RootElement.GetProperty("routes");
        Assert.Equal("custom", entries[0].GetProperty("stops")[0].GetProperty("objective").GetString());
        var episode = entries[1].GetProperty("episode");
        Assert.Equal("asmodian", episode.GetProperty("faction").GetString());
        Assert.Equal(6, episode.GetProperty("number").GetInt32());
        Assert.Equal(40, episode.GetProperty("minLevel").GetInt32());
        Assert.Equal(45, episode.GetProperty("maxLevel").GetInt32());
        Assert.Equal(new[] { "main-quest", "regional-quest", "teleport", "exploration", "waypoint" },
            entries[1].GetProperty("stops").EnumerateArray().Select(stop => stop.GetProperty("objective").GetString()));
        Assert.Equal(Enumerable.Range(0, 5).Select(index => "episode-id:" + index),
            entries[1].GetProperty("stops").EnumerateArray().Select(stop => stop.GetProperty("id").GetString()));
        Assert.False(entries[1].GetProperty("stops")[0].TryGetProperty("name", out _));
        Assert.Equal("", entries[1].GetProperty("stops")[1].GetProperty("title").GetString());
    }

    [Fact]
    public void StreamImportAndExportLeaveTheCallersStreamOpen()
    {
        using var stream = new MemoryStream();
        RouteFile.Write(stream, Routes());
        Assert.True(stream.CanWrite);
        stream.Position = 0;
        AssertRoutes(Routes(), RouteFile.Read(stream));
        Assert.True(stream.CanRead);
    }

    [Fact]
    public void LegacyMigrationPreservesProgressKeysStopIndicesAndNativeColors()
    {
        var routes = Routes();
        JsonFile.Save(Legacy, routes);
        // These local progress documents are deliberately outside the portable route format.
        var progress = Path.Combine(_folder, "leveling-progress.json");
        var targets = Path.Combine(_folder, "targets.json");
        File.WriteAllText(progress, """{"characters":{"character-1":{"activeRouteId":"episode-id","routes":{"episode-id":3}}}}""");
        File.WriteAllText(targets, """[{"id":"live-3","routeId":"episode-id","stopIndex":3,"completed":false,"x":500,"y":600}]""");
        var progressBytes = File.ReadAllBytes(progress);
        var targetBytes = File.ReadAllBytes(targets);

        AssertRoutes(routes, RouteFile.LoadLibrary(Library, Legacy));
        Assert.False(File.Exists(Legacy));
        Assert.True(File.Exists(Library));
        Assert.Equal(progressBytes, File.ReadAllBytes(progress));
        Assert.Equal(targetBytes, File.ReadAllBytes(targets));
        var loaded = RouteFile.LoadLibrary(Library, Legacy);
        Assert.Equal("Sealed Dungeon", loaded.Single(route => route.Id == "episode-id").Stops[3].Name);
        Assert.Equal(LevelingRouteStyle.Exploration, loaded[1].Stops[3].Color);
    }

    [Fact]
    public void ExistingNewLibraryWinsWithoutReadingOrDeletingTheOldOne()
    {
        var route = Routes()[0];
        RouteFile.Save(Library, new[] { route });
        File.WriteAllText(Legacy, "This is deliberately not valid JSON.");
        var legacyBytes = File.ReadAllBytes(Legacy);
        AssertRoutes(new[] { route }, RouteFile.LoadLibrary(Library, Legacy));
        Assert.Equal(legacyBytes, File.ReadAllBytes(Legacy));
    }

    [Fact]
    public void MissingLibraryStaysAbsentUntilTheBundledRoutesAreSeeded()
    {
        Assert.Empty(RouteFile.LoadLibrary(Library, Legacy));
        Assert.False(File.Exists(Library));
        Assert.False(File.Exists(Legacy));
        RouteFile.EnsureDefaultLibrary(Library, Legacy);
        Assert.Equal(10, RouteFile.LoadLibrary(Library, Legacy).Count(r => r.IsLeveling));
    }

    [Fact]
    public void AnExistingLibraryGetsTheBundledRoutesOnceAndDeletedOnesStayDeleted()
    {
        // Review 2026-10-10: an existing routes.json skipped the bundled leveling routes for good.
        JsonFile.Save(Legacy, new[] { Routes()[0] });
        RouteFile.EnsureDefaultLibrary(Library, Legacy);
        var routes = RouteFile.LoadLibrary(Library, Legacy);
        Assert.Equal("manual-id", routes[0].Id); // the user's own route stays first and unchanged
        Assert.Equal(10, routes.Count(r => r.IsLeveling));

        var deleted = routes.First(r => r.IsLeveling).Id;
        RouteFile.Save(Library, routes.Where(r => r.Id != deleted).ToList());
        RouteFile.EnsureDefaultLibrary(Library, Legacy);
        Assert.DoesNotContain(RouteFile.LoadLibrary(Library, Legacy), r => r.Id == deleted);
    }

    [Fact]
    public void AnEmptyLegacyLibraryMigratesWithoutSeedingEpisodes()
    {
        File.WriteAllText(Legacy, "[]");
        Assert.Empty(RouteFile.LoadLibrary(Library, Legacy));
        Assert.False(File.Exists(Legacy));
        Assert.Empty(RouteFile.Load(Library));
    }

    [Fact]
    public void ANewUnknownVersionNeverFallsBackToOrOverwritesLegacyData()
    {
        var document = Document();
        document["version"] = 999;
        File.WriteAllText(Library, document.ToJsonString());
        JsonFile.Save(Legacy, Routes());
        var newBytes = File.ReadAllBytes(Library);
        var oldBytes = File.ReadAllBytes(Legacy);
        // Never a failed start: the unknown file is set aside unchanged, the old library is not touched.
        Assert.Empty(RouteFile.LoadLibrary(Library, Legacy));
        var setAside = Assert.Single(Directory.GetFiles(_folder, "routes.soulroute.defekt-*"));
        Assert.Equal(newBytes, File.ReadAllBytes(setAside));
        Assert.Equal(oldBytes, File.ReadAllBytes(Legacy));
    }

    [Fact]
    public void CorruptLegacyDataStaysUntouchedAndDoesNotCreateANewLibrary()
    {
        File.WriteAllText(Legacy, "[{broken");
        var oldBytes = File.ReadAllBytes(Legacy);
        Assert.Empty(RouteFile.LoadLibrary(Library, Legacy)); // never a failed start
        Assert.False(File.Exists(Library));
        var setAside = Assert.Single(Directory.GetFiles(_folder, "routes.json.defekt-*"));
        Assert.Equal(oldBytes, File.ReadAllBytes(setAside));
    }

    [Theory]
    [InlineData("format")]
    [InlineData("coordinates")]
    [InlineData("routes")]
    [InlineData("null-route")]
    [InlineData("duplicate-route")]
    [InlineData("route-id")]
    [InlineData("route-name")]
    [InlineData("blank-route-name")]
    [InlineData("repeat")]
    [InlineData("stops")]
    [InlineData("null-stop")]
    [InlineData("duplicate-stop")]
    [InlineData("stop-id")]
    [InlineData("mismatched-stop-id")]
    [InlineData("map")]
    [InlineData("title")]
    [InlineData("kind")]
    [InlineData("blank-kind")]
    [InlineData("x")]
    [InlineData("y")]
    [InlineData("negative-x")]
    [InlineData("negative-y")]
    [InlineData("objective")]
    [InlineData("episode")]
    public void UntrustedRouteDocumentsRejectMissingOrAmbiguousValues(string invalid)
    {
        var document = Document();
        var routes = document["routes"]!.AsArray();
        var route = routes[0]!.AsObject();
        var stops = route["stops"]!.AsArray();
        var stop = stops[0]!.AsObject();
        switch (invalid)
        {
            case "format": document["format"] = "another.routes"; break;
            case "coordinates": document["coordinates"] = "world-coordinates"; break;
            case "routes": document.Remove("routes"); break;
            case "null-route": routes[0] = null; break;
            case "duplicate-route": routes.Add(route.DeepClone()); break;
            case "route-id": route.Remove("id"); break;
            case "route-name": route.Remove("name"); break;
            case "blank-route-name": route["name"] = "  "; break;
            case "repeat": route.Remove("repeat"); break;
            case "stops": route.Remove("stops"); break;
            case "null-stop": stops[0] = null; break;
            case "duplicate-stop": stops.Add(stop.DeepClone()); break;
            case "stop-id": stop.Remove("id"); break;
            case "mismatched-stop-id": stop["id"] = "other-route:123"; break;
            case "map": stop.Remove("map"); break;
            case "title": stop.Remove("title"); break;
            case "kind": stop.Remove("kind"); break;
            case "blank-kind": stop["kind"] = "  "; break;
            case "x": stop.Remove("x"); break;
            case "y": stop.Remove("y"); break;
            case "negative-x": stop["x"] = -1; break;
            case "negative-y": stop["y"] = -1; break;
            case "objective": stop["objective"] = "monster-branch"; break;
            case "episode": routes[1]!["episode"]!["faction"] = "unknown"; break;
        }
        File.WriteAllText(Library, document.ToJsonString());
        var bytes = File.ReadAllBytes(Library);
        Assert.Throws<InvalidDataException>(() => RouteFile.Load(Library));
        Assert.Equal(bytes, File.ReadAllBytes(Library));
        Assert.Single(Directory.GetFiles(_folder)); // no silent .defekt/.bak or overwritten file
    }

    [Fact]
    public void BundledPythonPackageSeedsOnceAndDoesNotRestoreDeletedRoutes()
    {
        RouteFile.EnsureDefaultLibrary(Library, Legacy);
        var routes = RouteFile.Load(Library);
        Assert.Equal(10, routes.Count);
        Assert.Equal(867, routes.Sum(route => route.Stops.Count));
        Assert.All(routes, route => Assert.True(route.IsLeveling));
        Assert.DoesNotContain(routes.SelectMany(route => route.Stops), LevelingRouteStyle.IsTrace);
        Assert.Equal(409, routes.Sum(route => route.Stops.Count(stop => stop.Name.Length == 0)));
        var firstBytes = File.ReadAllBytes(Library);
        RouteFile.EnsureDefaultLibrary(Library, Legacy);
        Assert.Equal(firstBytes, File.ReadAllBytes(Library));

        // The package is emitted by the offline Python converter and must remain compatible with
        // both directions of the C# serializer, without renumbering stops or changing native data.
        using var bundled = typeof(RouteFile).Assembly.GetManifestResourceStream("Soulcrest.LevelingRoutes.soulroute");
        Assert.NotNull(bundled);
        var imported = RouteFile.Read(bundled!);
        AssertRoutes(routes, imported);
        using var exported = new MemoryStream();
        RouteFile.Write(exported, imported);
        exported.Position = 0;
        AssertRoutes(imported, RouteFile.Read(exported));

        var deletedId = routes[0].Id;
        RouteFile.Save(Library, routes.Skip(1).ToArray());
        var editedBytes = File.ReadAllBytes(Library);
        RouteFile.EnsureDefaultLibrary(Library, Legacy);
        Assert.Equal(editedBytes, File.ReadAllBytes(Library));
        Assert.Equal(9, RouteFile.Load(Library).Count);
        Assert.DoesNotContain(RouteFile.Load(Library), route => route.Id == deletedId);
    }

    [Theory]
    [InlineData("objective")]
    [InlineData("faction")]
    [InlineData("number")]
    [InlineData("minLevel")]
    [InlineData("maxLevel")]
    [InlineData("manual-episode")]
    public void DerivedMetadataCannotContradictAuthoritativeRouteFields(string invalid)
    {
        var document = Document();
        var routes = document["routes"]!.AsArray();
        var episode = routes[1]!["episode"]!.AsObject();
        switch (invalid)
        {
            case "objective": routes[1]!["stops"]![0]!["objective"] = "regional-quest"; break;
            case "faction": episode["faction"] = "elyos"; break;
            case "number": episode["number"] = 7; break;
            case "minLevel": episode["minLevel"] = 39; break;
            case "maxLevel": episode["maxLevel"] = 46; break;
            case "manual-episode": routes[0]!["episode"] = episode.DeepClone(); break;
        }
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(document.ToJsonString()));
        Assert.Throws<InvalidDataException>(() => RouteFile.Read(stream));
    }

    [Fact]
    public void MissingOptionalEpisodeMetadataIsReconstructedFromTheRouteName()
    {
        var document = Document();
        document["routes"]![1]!.AsObject().Remove("episode");
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(document.ToJsonString()));
        AssertRoutes(Routes(), RouteFile.Read(stream));
    }

    [Fact]
    public void LegacyArraysAreAcceptedOnlyAtTheExplicitLegacyPath()
    {
        JsonFile.Save(Library, Routes());
        Assert.Throws<InvalidDataException>(() => RouteFile.Load(Library));
    }

    [Fact]
    public void NonFiniteCoordinatesCannotReplaceAnExistingLibrary()
    {
        RouteFile.Save(Library, Routes());
        var before = File.ReadAllBytes(Library);
        var invalid = Routes()[0] with { Stops = new[] { Routes()[0].Stops[0] with { X = double.NaN } } };
        Assert.Throws<InvalidDataException>(() => RouteFile.Save(Library, new[] { invalid }));
        Assert.Equal(before, File.ReadAllBytes(Library));

        var json = Document().ToJsonString().Replace("4111.25", "1e999", StringComparison.Ordinal);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        Assert.Throws<InvalidDataException>(() => RouteFile.Read(stream));
    }
}

