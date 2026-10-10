using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class EpisodeContinuationTests : IDisposable
{
    private readonly string _profilePath = Path.Combine(AppPaths.DataDirectory, "leveling-progress.json");
    private readonly string _explorationPath = Path.Combine(AppPaths.DataDirectory, "episode-test-exploration.json");
    private readonly Dictionary<string, byte[]?> _originalFiles = [];

    public EpisodeContinuationTests()
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        foreach (var path in new[] { AppPaths.TargetsFile, AppPaths.RoutesFile, _profilePath, _explorationPath })
        {
            _originalFiles.Add(path, File.Exists(path) ? File.ReadAllBytes(path) : null);
            File.Delete(path);
        }
        SaveRoutes(Episode("a2", "Asmodian", 2), Episode("a3", "Asmodian", 3), Episode("a4", "Asmodian", 4),
            Episode("e2", "Elyos", 2), Episode("e3", "Elyos", 3),
            Episode("ordinary", "Asmodian", 3) with
            {
                Name = "Own route", Category = null,
                Stops = Enumerable.Range(0, 3).Select(i => new RouteStop("altgard", 5000 + i * 100, 200, $"Own stop {i}", "Waypoint", null)).ToArray()
            });
    }

    public void Dispose()
    {
        foreach (var (path, contents) in _originalFiles)
        {
            if (contents is null) File.Delete(path);
            else File.WriteAllBytes(path, contents);
        }
    }

    private static SavedRoute Episode(string id, string faction, int episode, bool repeat = false) => new(id,
        $"{faction} · Episode {episode} · Level 10–16",
        Enumerable.Range(0, 3).Select(i => new RouteStop("altgard", 100 + i * 100, 200, $"Quest {i}", "NPCs", null,
            Color: LevelingRouteStyle.MainQuest)).ToArray(), Repeat: repeat, Category: LevelingRouteStyle.Category);

    private static void SaveRoutes(params SavedRoute[] routes) => RouteFile.Save(AppPaths.RoutesFile, routes);
    private ExplorationService Exploration() => new(_explorationPath, []);

    private void SeedProgress(params (string Character, string? Active, Dictionary<string, int> Routes)[] profiles) =>
        JsonFile.Save(_profilePath, new
        {
            LegacyAdopted = true,
            Characters = profiles.ToDictionary(p => p.Character, p => new { ActiveRouteId = p.Active, p.Routes })
        });

    [Theory]
    [InlineData("advance")]
    [InlineData("arrival")]
    [InlineData("remove")]
    [InlineData("toggle")]
    public void CompletingEpisodeStartsItsSameFactionSuccessorAtTheSavedCursorAndPreservesOwnMarks(string action)
    {
        var exploration = Exploration();
        exploration.Rename("Alice");
        var alice = exploration.ActiveId;
        SeedProgress((alice, null, new() { ["a2"] = 2, ["a3"] = 1 }));
        using (var service = new MapTargetsService(exploration))
        {
            service.StartRoute("ordinary");
            service.SetRouteRepeat("ordinary", true);
            var ordinary = service.Targets.Select(t => t.Id).ToArray();
            service.StartRoute("a2");
            var last = service.Targets.Single(t => t.RouteId == "a2");
            service.Toggle("altgard", 9999, 9999, "Own marker", "Waypoint", null, chain: true);
            var extra = service.Targets.Single(t => t.Name == "Own marker").Id;
            var changed = 0;
            service.Changed += () => changed++;

            switch (action)
            {
                case "advance": service.AdvanceRoute("a2"); break;
                case "arrival": service.Arrive(last.Id); break;
                case "remove": service.Remove(last.Id); break;
                case "toggle": Assert.False(service.Toggle(last.MapId, last.X, last.Y, last.Name, last.Kind, last.Icon)); break;
            }

            Assert.Equal("a3", service.ActiveLevelingRouteId);
            Assert.Equal(new RouteProgress(3, 3), service.GetSavedRouteProgress("a2"));
            Assert.Equal(new RouteProgress(1, 3), service.GetRouteProgress("a3"));
            Assert.All(service.Targets.Where(t => t.Leveling), t => Assert.Equal(alice, t.CharacterId));
            Assert.Equal(ordinary.Append(extra), service.Targets.Where(t => !t.Leveling).Select(t => t.Id));
            Assert.Null(service.Targets.Single(t => t.Id == extra).After);
            Assert.True(service.Routes.Single(r => r.Id == "ordinary").Repeat);
            Assert.Equal(1, changed);
            service.Arrive(last.Id);
            service.AdvanceRoute("a2");
            Assert.Equal(1, changed);
        }

        using var reloaded = new MapTargetsService(Exploration());
        Assert.Equal("a3", reloaded.ActiveLevelingRouteId);
        Assert.Equal(new RouteProgress(1, 3), reloaded.GetRouteProgress("a3"));
        Assert.Equal(new RouteProgress(3, 3), reloaded.GetSavedRouteProgress("a2"));
        reloaded.RewindRoute("a3");
        Assert.Equal(new RouteProgress(0, 3), reloaded.GetSavedRouteProgress("a3"));
        Assert.Equal("a3", reloaded.ActiveLevelingRouteId);
    }

    [Fact]
    public void AlreadyCompletedSuccessorsAreSkippedUntilTheFirstUnfinishedEpisode()
    {
        var exploration = Exploration();
        exploration.Rename("Alice");
        SeedProgress((exploration.ActiveId, null, new() { ["a2"] = 2, ["a3"] = 3, ["a4"] = 2 }));
        using var service = new MapTargetsService(exploration);
        service.StartRoute("a2");
        service.AdvanceRoute("a2");
        Assert.Equal("a4", service.ActiveLevelingRouteId);
        Assert.Equal(new RouteProgress(2, 3), service.GetRouteProgress("a4"));
        Assert.Equal(new RouteProgress(3, 3), service.GetSavedRouteProgress("a3"));
        Assert.Null(service.GetSavedRouteProgress("e3"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrAmbiguousNextEpisodeRetainsTheCompletedRouteInsteadOfGuessing(bool ambiguous)
    {
        var routes = new List<SavedRoute> { Episode("a2", "Asmodian", 2), Episode("a4", "Asmodian", 4), Episode("e3", "Elyos", 3) };
        if (ambiguous) routes.AddRange([Episode("first-a3", "Asmodian", 3), Episode("second-a3", "Asmodian", 3)]);
        SaveRoutes(routes.ToArray());
        using var service = new MapTargetsService(Exploration());
        service.StartRoute("a2");
        for (var i = 0; i < 3; i++) service.AdvanceRoute("a2");
        Assert.Equal("a2", service.ActiveLevelingRouteId);
        Assert.Equal(new RouteProgress(3, 3), service.GetRouteProgress("a2"));
        Assert.All(service.VisibleTargets, t => Assert.True(t.Completed));
        service.RewindRoute("a2");
        Assert.Equal(new RouteProgress(2, 3), service.GetSavedRouteProgress("a2"));
    }

    [Fact]
    public void LastEpisodeRetainsHistoryAndNeverRepeatsOrChangesFaction()
    {
        SaveRoutes(Episode("a6", "Asmodian", 6, repeat: true), Episode("e7", "Elyos", 7));
        using (var service = new MapTargetsService(Exploration()))
        {
            service.StartRoute("a6");
            for (var i = 0; i < 3; i++) service.AdvanceRoute("a6");
            Assert.Equal("a6", service.ActiveLevelingRouteId);
            Assert.Empty(service.Targets);
            Assert.Equal(new RouteProgress(3, 3), service.GetSavedRouteProgress("a6"));
        }
        using var reloaded = new MapTargetsService(Exploration());
        Assert.Equal("a6", reloaded.ActiveLevelingRouteId);
        reloaded.RewindRoute("a6");
        Assert.Equal(new RouteProgress(2, 3), reloaded.GetRouteProgress("a6"));
    }

    [Fact]
    public void LoadingOrSelectingCompletedEpisodeDoesNotAdvanceUntilANewCompletion()
    {
        var exploration = Exploration();
        exploration.Rename("Alice");
        SeedProgress((exploration.ActiveId, "a2", new() { ["a2"] = 3 }));
        using var service = new MapTargetsService(exploration);
        Assert.Equal("a2", service.ActiveLevelingRouteId);
        service.StartRoute("a2");
        service.AdvanceRoute("a2");
        Assert.Equal("a2", service.ActiveLevelingRouteId);
        service.RewindRoute("a2");
        service.AdvanceRoute("a2");
        Assert.Equal("a3", service.ActiveLevelingRouteId);
        service.RestartRoute("a2");
        Assert.Equal("a2", service.ActiveLevelingRouteId);
        Assert.Equal(new RouteProgress(0, 3), service.GetRouteProgress("a2"));
    }

    [Fact]
    public void EpisodeContinuationUsesOnlyTheSelectedCharactersSavedPositions()
    {
        var exploration = Exploration();
        exploration.Rename("Alice");
        var alice = exploration.ActiveId;
        exploration.Add("Bob");
        var bob = exploration.ActiveId;
        exploration.Select(alice);
        SeedProgress((alice, "a2", new() { ["a2"] = 2, ["a3"] = 1 }),
            (bob, "a2", new() { ["a2"] = 1, ["a3"] = 2 }));
        using var service = new MapTargetsService(exploration);
        var oldAliceTarget = service.Targets.Single().Id;
        service.AdvanceRoute("a2");
        Assert.Equal(new RouteProgress(1, 3), service.GetRouteProgress("a3"));
        exploration.Select(bob);
        service.Arrive(oldAliceTarget);
        Assert.Equal("a2", service.ActiveLevelingRouteId);
        Assert.Equal(new RouteProgress(1, 3), service.GetRouteProgress("a2"));
        service.AdvanceRoute("a2");
        service.AdvanceRoute("a2");
        Assert.Equal("a3", service.ActiveLevelingRouteId);
        Assert.Equal(new RouteProgress(2, 3), service.GetRouteProgress("a3"));
        Assert.All(service.Targets, t => Assert.Equal(bob, t.CharacterId));
        exploration.Select(alice);
        Assert.Equal(new RouteProgress(1, 3), service.GetRouteProgress("a3"));
    }

    [Theory]
    [InlineData("Asmodian · Episode 2", "Asmodian", 2)]
    [InlineData("Elyos · Episode 6 · Level 40–45", "Elyos", 6)]
    public void EpisodeParserAcceptsTheImportedNamingFormat(string name, string faction, int episode)
    {
        Assert.True(LevelingRouteStyle.TryGetEpisode(Episode("test", "Asmodian", 2) with { Name = name }, out var actualFaction, out var actualEpisode));
        Assert.Equal(faction, actualFaction);
        Assert.Equal(episode, actualEpisode);
    }

    [Theory]
    [InlineData("Asmodian 1/8 - Level 10-17")]
    [InlineData("Asmodian · Episode 0")]
    [InlineData("Asmodian · Episode 03")]
    [InlineData("asmodian · Episode 3")]
    [InlineData("Asmodian · Episode 3\n")]
    [InlineData("Asmodian · Episode 3 · Level 18-22")]
    [InlineData("Elyos · Episode 999999999999999999999")]
    [InlineData("Other · Episode 3")]
    public void EpisodeParserRejectsLegacyOrAmbiguousNames(string name) =>
        Assert.False(LevelingRouteStyle.TryGetEpisode(Episode("test", "Asmodian", 2) with { Name = name }, out _, out _));

    [Fact]
    public void AnOrdinaryRouteWithAnEpisodeNameDoesNotJoinTheLevelingSequence() =>
        Assert.False(LevelingRouteStyle.TryGetEpisode(Episode("test", "Asmodian", 2) with { Category = null }, out _, out _));
}
