using System.Text.Json;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Bundled title upgrades run only in isolated libraries and never touch local progress.</summary>
public sealed class DungeonTitleUpgradeTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "Soulcrest-dungeon-titles-" + Guid.NewGuid().ToString("N"));
    private string Library => Path.Combine(_folder, "routes.soulroute");
    private string Legacy => Path.Combine(_folder, "routes.json");

    public DungeonTitleUpgradeTests() => Directory.CreateDirectory(_folder);
    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static List<SavedRoute> Bundled()
    {
        using var stream = typeof(RouteFile).Assembly.GetManifestResourceStream("Soulcrest.LevelingRoutes.soulroute")!;
        return RouteFile.Read(stream);
    }

    private static Dictionary<string, (string Previous, string Current)> Titles()
    {
        using var stream = typeof(RouteFile).Assembly.GetManifestResourceStream("Soulcrest.DungeonTitleUpgrades.json")!;
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("stops").EnumerateArray().ToDictionary(
            entry => entry.GetProperty("id").GetString()!,
            entry => (entry.GetProperty("previousTitle").GetString()!, entry.GetProperty("title").GetString()!));
    }

    private static List<SavedRoute> PreviousBundle()
    {
        var titles = Titles();
        return Bundled().Select(route => route with
        {
            Stops = route.Stops.Select((stop, index) => titles.TryGetValue(route.Id + ":" + index, out var title)
                ? stop with { Name = title.Previous } : stop).ToArray()
        }).ToList();
    }

    private static void AssertRoute(SavedRoute expected, SavedRoute actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Category, actual.Category);
        Assert.Equal(expected.Repeat, actual.Repeat);
        Assert.Equal(expected.Color, actual.Color);
        Assert.Equal(expected.Stops, actual.Stops);
    }

    [Fact]
    public void ReviewedBundleNamesAllVerifiedDungeonStopsWithoutChangingTheirShape()
    {
        var before = PreviousBundle();
        var current = Bundled();
        var titles = Titles();
        Assert.Equal(55, titles.Count);
        Assert.Equal(55, titles.Values.Count(title => title.Previous != title.Current));
        Assert.Equal(55, current.Sum(route => route.Stops.Count(stop => stop.Kind == "Locations · Sealed Dungeon")));
        Assert.All(current.SelectMany(route => route.Stops).Where(stop => stop.Kind == "Locations · Sealed Dungeon"),
            stop => Assert.NotEmpty(stop.Name));
        for (var i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i].Stops.Count, current[i].Stops.Count);
            for (var j = 0; j < before[i].Stops.Count; j++)
                Assert.Equal(before[i].Stops[j] with { Name = current[i].Stops[j].Name }, current[i].Stops[j]);
        }
        Assert.Equal("Urgent Pursuit", current[0].Stops[37].Name); // Coloured quest station stays a quest title.
        Assert.Equal("Imminent Threat", current[1].Stops[25].Name);
        Assert.Equal("Illusion Cavaliers", current[5].Stops[40].Name);
        Assert.Equal("Crash Site Rendezvous", current[0].Stops[9].Name); // A source NPC, not a dungeon target.
        Assert.Equal("Dead Men Tell No Tales", current[0].Stops[53].Name); // No exact marker evidence.
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(true, "")]
    [InlineData(false, "Stop")]
    [InlineData(true, "Stop")]
    public void OriginalLibrariesUpgradeTitlesOnceAndLeaveProgressDocumentsUntouched(bool legacy, string placeholder)
    {
        var original = PreviousBundle();
        var titles = Titles();
        original = original.Select(route => route with
        {
            Stops = route.Stops.Select((stop, index) => titles.ContainsKey(route.Id + ":" + index)
                ? stop with { Name = placeholder } : stop).ToArray()
        }).ToList();
        if (legacy) JsonFile.Save(Legacy, original);
        else RouteFile.Save(Library, original);
        var progressPath = Path.Combine(_folder, "leveling-progress.json");
        var targetsPath = Path.Combine(_folder, "targets.json");
        File.WriteAllText(progressPath, """{"characters":{"profile-1":{"activeRouteId":"373a3fb90c9596817acb7e16b5d2d581","routes":{"373a3fb90c9596817acb7e16b5d2d581":11}}}}""");
        File.WriteAllText(targetsPath, """[{"id":"live-10","routeId":"373a3fb90c9596817acb7e16b5d2d581","stopIndex":10,"name":"","completed":true}]""");
        var progressBytes = File.ReadAllBytes(progressPath);
        var targetBytes = File.ReadAllBytes(targetsPath);

        RouteFile.EnsureDefaultLibrary(Library, Legacy);
        var upgraded = RouteFile.Load(Library);
        var current = Bundled();
        Assert.Equal(current.Count, upgraded.Count);
        for (var i = 0; i < current.Count; i++) AssertRoute(current[i], upgraded[i]);
        Assert.Equal(progressBytes, File.ReadAllBytes(progressPath));
        Assert.Equal(targetBytes, File.ReadAllBytes(targetsPath));
        Assert.False(File.Exists(Legacy));
        var libraryBytes = File.ReadAllBytes(Library);
        RouteFile.EnsureDefaultLibrary(Library, Legacy);
        Assert.Equal(libraryBytes, File.ReadAllBytes(Library));
    }

    [Fact]
    public void UserTitlesAndRouteMetadataRemainWhileOtherOriginalTitlesUpgrade()
    {
        var original = PreviousBundle();
        var stops = original[0].Stops.ToArray();
        stops[10] = stops[10] with { Name = "My cave note" };
        stops[22] = stops[22] with { Name = "Stop" };
        stops[0] = stops[0] with { Name = "My quest note" };
        original[0] = original[0] with { Name = "My episode route", Color = "#123456", Repeat = true, Stops = stops };
        RouteFile.Save(Library, original);

        RouteFile.EnsureDefaultLibrary(Library, Legacy);
        var result = RouteFile.Load(Library)[0];
        Assert.Equal("My episode route", result.Name);
        Assert.Equal("#123456", result.Color);
        Assert.True(result.Repeat);
        Assert.Equal("My cave note", result.Stops[10].Name);
        Assert.Equal("My quest note", result.Stops[0].Name);
        Assert.Equal("Trickster's Den", result.Stops[22].Name);
        for (var i = 0; i < stops.Length; i++)
            Assert.Equal(stops[i] with { Name = result.Stops[i].Name }, result.Stops[i]);
    }

    [Theory]
    [InlineData("moved")]
    [InlineData("reordered")]
    [InlineData("removed")]
    [InlineData("added")]
    [InlineData("kind")]
    [InlineData("icon")]
    [InlineData("pet")]
    [InlineData("color")]
    public void ChangedRouteShapesOptOutOfIndexBasedTitleUpgrades(string edit)
    {
        var original = PreviousBundle();
        var stops = original[0].Stops.ToList();
        switch (edit)
        {
            case "moved": stops[10] = stops[10] with { X = stops[10].X + 1 }; break;
            case "reordered": (stops[10], stops[22]) = (stops[22], stops[10]); break;
            case "removed": stops.RemoveAt(10); break;
            case "added": stops.Add(stops[10]); break;
            case "kind": stops[10] = stops[10] with { Kind = "Locations · My Dungeon" }; break;
            case "icon": stops[10] = stops[10] with { Icon = "icons/custom.png" }; break;
            case "pet": stops[10] = stops[10] with { PetId = "pet-custom" }; break;
            case "color": stops[10] = stops[10] with { Color = "#a78bfa" }; break;
        }
        original[0] = original[0] with { Stops = stops };
        RouteFile.Save(Library, original);
        RouteFile.EnsureDefaultLibrary(Library, Legacy);
        AssertRoute(original[0], RouteFile.Load(Library)[0]);
    }

    [Fact]
    public void CustomRouteIdsNeverParticipateInDefaultTitleUpgrades()
    {
        var own = PreviousBundle()[0] with { Id = "own-route" };
        RouteFile.Save(Library, new[] { own });
        RouteFile.EnsureDefaultLibrary(Library, Legacy);
        AssertRoute(own, RouteFile.Load(Library)[0]);
    }

    [Fact]
    public void LegacyTitleAliasesAreBoundToTheExactRouteStopAndReviewedNames()
    {
        const string route = "373a3fb90c9596817acb7e16b5d2d581";
        Assert.False(RouteFile.MatchesPreviousDungeonTitle(route, 37, "Shaded Cave", "Urgent Pursuit"));
        Assert.True(RouteFile.MatchesPreviousDungeonTitle(route, 10, "Treasure Hunter's Residence", ""));
        Assert.True(RouteFile.MatchesPreviousDungeonTitle(route, 10, "Treasure Hunter's Residence", "Stop"));
        Assert.False(RouteFile.MatchesPreviousDungeonTitle("own-route", 10, "Treasure Hunter's Residence", ""));
        Assert.False(RouteFile.MatchesPreviousDungeonTitle(route, 11, "Treasure Hunter's Residence", ""));
        Assert.False(RouteFile.MatchesPreviousDungeonTitle(route, 10, "Treasure Hunter's Residence", "User note"));
        Assert.False(RouteFile.MatchesPreviousDungeonTitle(route, 10, "Other dungeon", ""));
    }
}
