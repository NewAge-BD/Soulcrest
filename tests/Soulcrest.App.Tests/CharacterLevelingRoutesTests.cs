using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class CharacterLevelingRoutesTests : IDisposable
{
    private readonly string _profilePath = Path.Combine(AppPaths.DataDirectory, "leveling-progress.json");
    private readonly string _explorationPath = Path.Combine(AppPaths.DataDirectory, "leveling-test-exploration.json");

    public CharacterLevelingRoutesTests()
    {
        Cleanup();
        var stops = Enumerable.Range(0, 8).Select(i => new RouteStop("altgard", 100 + i * 100, 200, $"Quest {i}", "NPCs", null,
            Color: LevelingRouteStyle.MainQuest)).ToList();
        RouteFile.Save(AppPaths.RoutesFile, new[]
        {
            new SavedRoute("first", "First route", stops, Category: LevelingRouteStyle.Category),
            new SavedRoute("second", "Second route", stops, Category: LevelingRouteStyle.Category),
            new SavedRoute("ordinary", "Own route", stops, Color: "#38bdf8")
        });
    }

    public void Dispose() => Cleanup();

    private void Cleanup()
    {
        foreach (var path in new[] { AppPaths.TargetsFile, AppPaths.RoutesFile, AppPaths.LegacyRoutesFile, _profilePath, _explorationPath }) File.Delete(path);
    }

    private ExplorationService Exploration() => new(_explorationPath, []);

    [Fact]
    public void LegacyLibraryConversionKeepsBothCharactersAtTheirSavedStop()
    {
        var exploration = Exploration();
        exploration.Rename("Alice");
        var alice = exploration.ActiveId;
        using (var service = new MapTargetsService(exploration))
        {
            service.StartRoute("first");
            for (var i = 0; i < 3; i++) service.AdvanceRoute("first");
            exploration.Add("Bob");
            service.StartRoute("first");
            service.AdvanceRoute("first");
        }
        var before = RouteFile.Load(AppPaths.RoutesFile);
        JsonFile.Save(AppPaths.LegacyRoutesFile, before);
        File.Delete(AppPaths.RoutesFile);

        using var reloaded = new MapTargetsService(exploration);
        Assert.False(File.Exists(AppPaths.LegacyRoutesFile));
        Assert.True(File.Exists(AppPaths.RoutesFile));
        Assert.Equal(before.Select(r => r.Id), reloaded.Routes.Select(r => r.Id));
        Assert.Equal(before.SelectMany(r => r.Stops), reloaded.Routes.SelectMany(r => r.Stops));
        Assert.Equal(new RouteProgress(1, 8), reloaded.GetRouteProgress("first"));
        exploration.Select(alice);
        Assert.Equal(new RouteProgress(3, 8), reloaded.GetRouteProgress("first"));
        Assert.Equal(3, reloaded.VisibleTargets.First(t => !t.Completed).StopIndex);
    }

    [Fact]
    public void TwoCharactersKeepIndependentPositionsAndActiveRoutes()
    {
        var exploration = Exploration();
        exploration.Rename("Alice");
        var alice = exploration.ActiveId;
        using var service = new MapTargetsService(exploration);
        service.StartRoute("first");
        service.AdvanceRoute("first");
        service.AdvanceRoute("first");
        service.StartRoute("second");
        service.AdvanceRoute("second");
        Assert.Equal(new RouteProgress(2, 8), service.GetSavedRouteProgress("first"));
        exploration.Add("Bob");
        var bob = exploration.ActiveId;
        Assert.Null(service.ActiveLevelingRouteId);
        Assert.Null(service.GetSavedRouteProgress("first"));
        Assert.Empty(service.VisibleTargets);
        service.StartRoute("first");
        service.AdvanceRoute("first");
        Assert.All(service.Targets, t => Assert.Equal(bob, t.CharacterId));
        exploration.Select(alice);
        Assert.Equal("second", service.ActiveLevelingRouteId);
        Assert.Equal(new RouteProgress(1, 8), service.GetRouteProgress("second"));
        service.StartRoute("first");
        Assert.Equal(new RouteProgress(2, 8), service.GetRouteProgress("first"));
        service.RewindRoute("first");
        Assert.Equal(new RouteProgress(1, 8), service.GetSavedRouteProgress("first"));
        exploration.Select(bob);
        Assert.Equal(new RouteProgress(1, 8), service.GetRouteProgress("first"));
        service.Remove(service.VisibleTargets.First(t => t.StopIndex == 3).Id);
        Assert.Equal(new RouteProgress(4, 8), service.GetSavedRouteProgress("first"));
        exploration.Select(alice);
        Assert.Equal(new RouteProgress(1, 8), service.GetRouteProgress("first"));
    }

    [Fact]
    public void ClearHidesRoutesAndRestartResetsOnlyTheSelectedCharacterAndRoute()
    {
        var exploration = Exploration();
        exploration.Rename("Alice");
        var alice = exploration.ActiveId;
        using var service = new MapTargetsService(exploration);
        service.StartRoute("first");
        for (var i = 0; i < 5; i++) service.AdvanceRoute("first");
        service.StartRoute("second");
        service.AdvanceRoute("second");
        service.Clear();
        Assert.Null(service.GetRouteProgress("second"));
        Assert.Null(service.ActiveLevelingRouteId);
        Assert.Equal(new RouteProgress(1, 8), service.GetSavedRouteProgress("second"));
        service.StartRoute("first");
        Assert.Equal(new RouteProgress(5, 8), service.GetRouteProgress("first"));
        exploration.Add("Bob");
        var bob = exploration.ActiveId;
        service.StartRoute("first");
        service.AdvanceRoute("first");
        exploration.Select(alice);
        service.RestartRoute("first");
        Assert.Equal(new RouteProgress(0, 8), service.GetSavedRouteProgress("first"));
        Assert.Equal(new RouteProgress(1, 8), service.GetSavedRouteProgress("second"));
        exploration.Select(bob);
        Assert.Equal(new RouteProgress(1, 8), service.GetSavedRouteProgress("first"));
    }

    [Fact]
    public void InitialDefaultIdentityAndLegacyOwnershipPersistAcrossAppRestarts()
    {
        using (var legacy = new MapTargetsService())
        {
            legacy.StartRoute("first");
            for (var i = 0; i < 6; i++) legacy.AdvanceRoute("first");
        }
        var exploration = Exploration();
        var first = exploration.ActiveId;
        Assert.False(File.Exists(_explorationPath));
        using (var service = new MapTargetsService(exploration))
        {
            Assert.True(File.Exists(_explorationPath));
            Assert.True(File.Exists(_profilePath));
            Assert.Equal(new RouteProgress(6, 8), service.GetSavedRouteProgress("first"));
            exploration.Add("Other");
            Assert.Null(service.ActiveLevelingRouteId);
            Assert.Null(service.GetSavedRouteProgress("first"));
        }
        exploration = Exploration();
        using var reloaded = new MapTargetsService(exploration);
        Assert.Null(reloaded.GetSavedRouteProgress("first"));
        exploration.Select(first);
        Assert.Equal(new RouteProgress(6, 8), reloaded.GetRouteProgress("first"));
        reloaded.RewindRoute("first");
        Assert.Equal(new RouteProgress(5, 8), reloaded.GetSavedRouteProgress("first"));
    }

    [Fact]
    public void FinishedRouteStaysAvailableForBackAndItsSavedPositionSurvivesReload()
    {
        var exploration = Exploration();
        using (var service = new MapTargetsService(exploration))
        {
            service.StartRoute("first");
            for (var i = 0; i < 8; i++) service.AdvanceRoute("first");
        }
        using var loaded = new MapTargetsService(Exploration());
        Assert.Equal("first", loaded.ActiveLevelingRouteId);
        Assert.Equal(new RouteProgress(8, 8), loaded.GetSavedRouteProgress("first"));
        Assert.Empty(loaded.ActiveRoutes);
        Assert.Equal(3, loaded.VisibleTargets.Count);
        loaded.RewindRoute("first");
        Assert.Equal(7, loaded.Targets[0].StopIndex);
        loaded.Clear();
        loaded.StartRoute("first");
        Assert.Equal(new RouteProgress(7, 8), loaded.GetRouteProgress("first"));
    }

    [Fact]
    public void RenameAndAutomaticCharacterSelectionUseStableIdentity()
    {
        var exploration = Exploration();
        var initial = exploration.ActiveId;
        using var service = new MapTargetsService(exploration);
        service.StartRoute("first");
        service.AdvanceRoute("first");
        Assert.Equal(CharacterChoice.Adopted, exploration.UseCharacter("Alice"));
        Assert.Equal(initial, exploration.ActiveId);
        exploration.Rename("Alice Renamed");
        Assert.Equal(new RouteProgress(1, 8), service.GetRouteProgress("first"));
        Assert.Equal(CharacterChoice.Created, exploration.UseCharacter("Bob"));
        Assert.Null(service.ActiveLevelingRouteId);
        service.StartRoute("second");
        service.AdvanceRoute("second");
        Assert.Equal(CharacterChoice.Selected, exploration.UseCharacter("Alice Renamed"));
        Assert.Equal("first", service.ActiveLevelingRouteId);
        Assert.Equal(new RouteProgress(1, 8), service.GetRouteProgress("first"));
        Assert.Null(service.GetSavedRouteProgress("second"));
    }

    [Fact]
    public void CharacterSwitchPreservesOrdinaryMarksAndArrivalCannotUpdateAnotherCharacter()
    {
        var exploration = Exploration();
        exploration.Rename("Alice");
        var alice = exploration.ActiveId;
        using var service = new MapTargetsService(exploration);
        service.StartRoute("ordinary");
        service.SetRouteRepeat("ordinary", true);
        var ordinaryIds = service.Targets.Select(t => t.Id).ToArray();
        service.StartRoute("first");
        Assert.True(service.Routes.Single(r => r.Id == "ordinary").Repeat);
        var oldHead = service.Targets.First(t => t.Leveling).Id;
        service.Arrive(oldHead);
        exploration.Add("Bob");
        Assert.Equal(ordinaryIds, service.Targets.Select(t => t.Id));
        service.StartRoute("first");
        service.Arrive(oldHead);
        Assert.Equal(new RouteProgress(0, 8), service.GetRouteProgress("first"));
        exploration.Select(alice);
        Assert.Equal(new RouteProgress(1, 8), service.GetRouteProgress("first"));
        Assert.Equal(ordinaryIds, service.Targets.Where(t => !t.Leveling).Select(t => t.Id));
        service.StartRoute("ordinary");
        Assert.Null(service.ActiveLevelingRouteId);
        Assert.Equal(new RouteProgress(1, 8), service.GetSavedRouteProgress("first"));
    }

    [Fact]
    public void DeletingRouteRemovesEveryCharactersSavedPositionButKeepsOtherRoutes()
    {
        var exploration = Exploration();
        exploration.Rename("Alice");
        var alice = exploration.ActiveId;
        using var service = new MapTargetsService(exploration);
        service.StartRoute("first");
        service.AdvanceRoute("first");
        service.StartRoute("second");
        service.AdvanceRoute("second");
        exploration.Add("Bob");
        service.StartRoute("first");
        service.AdvanceRoute("first");
        service.DeleteRoute("first");
        Assert.Null(service.GetSavedRouteProgress("first"));
        exploration.Select(alice);
        Assert.Null(service.GetSavedRouteProgress("first"));
        Assert.Equal(new RouteProgress(1, 8), service.GetSavedRouteProgress("second"));
        Assert.Equal("second", service.ActiveLevelingRouteId);
    }

    [Fact]
    public void StopRetainsSavedPositionAcrossReloadAndKeepsOtherMarksAndRepeatSettings()
    {
        var exploration = Exploration();
        string[] ordinaryIds;
        string extraId;
        using (var service = new MapTargetsService(exploration))
        {
            service.StartRoute("ordinary");
            service.SetRouteRepeat("ordinary", true);
            ordinaryIds = service.Targets.Select(t => t.Id).ToArray();
            service.StartRoute("first");
            service.SetRouteRepeat("first", true);
            for (var i = 0; i < 5; i++) service.AdvanceRoute("first");
            service.Toggle("altgard", 950, 300, "Own marker", "Waypoint", null, chain: true);
            var extra = service.Targets.Single(t => t.Name == "Own marker");
            extraId = extra.Id;
            Assert.NotNull(extra.After);
            var changed = 0;
            service.Changed += () => changed++;

            service.StopRoute("first");

            Assert.Equal(1, changed);
            Assert.Null(service.ActiveLevelingRouteId);
            Assert.Null(service.GetRouteProgress("first"));
            Assert.Equal(new RouteProgress(5, 8), service.GetSavedRouteProgress("first"));
            Assert.Equal(ordinaryIds.Append(extraId), service.Targets.Select(t => t.Id));
            Assert.Null(service.Targets.Single(t => t.Id == extraId).After);
            Assert.True(service.Routes.Single(r => r.Id == "ordinary").Repeat);
            Assert.False(service.Routes.Single(r => r.Id == "first").Repeat);
            service.AdvanceRoute("first");
            service.RewindRoute("first");
            service.StopRoute("first");
            Assert.Equal(1, changed);
            Assert.Equal(new RouteProgress(5, 8), service.GetSavedRouteProgress("first"));
        }

        using var reloaded = new MapTargetsService(Exploration());
        Assert.Null(reloaded.ActiveLevelingRouteId);
        Assert.Equal(new RouteProgress(5, 8), reloaded.GetSavedRouteProgress("first"));
        Assert.Equal(ordinaryIds.Append(extraId), reloaded.Targets.Select(t => t.Id));
        reloaded.StartRoute("first");
        Assert.Equal(new RouteProgress(5, 8), reloaded.GetRouteProgress("first"));
        Assert.Equal(5, reloaded.Targets.First(t => t.Leveling).StopIndex);
        Assert.Equal(ordinaryIds.Append(extraId), reloaded.Targets.Where(t => !t.Leveling).Select(t => t.Id));
    }

    [Fact]
    public void StopChangesOnlyTheSelectedCharactersActiveRouteAndRetainsBothPositions()
    {
        var exploration = Exploration();
        exploration.Rename("Alice");
        var alice = exploration.ActiveId;
        string bob;
        using (var service = new MapTargetsService(exploration))
        {
            service.StartRoute("first");
            for (var i = 0; i < 2; i++) service.AdvanceRoute("first");
            exploration.Add("Bob");
            bob = exploration.ActiveId;
            service.StartRoute("first");
            for (var i = 0; i < 4; i++) service.AdvanceRoute("first");
            service.StopRoute("first");
            Assert.Equal(new RouteProgress(4, 8), service.GetSavedRouteProgress("first"));
            exploration.Select(alice);
            Assert.Equal("first", service.ActiveLevelingRouteId);
            Assert.Equal(new RouteProgress(2, 8), service.GetRouteProgress("first"));
            exploration.Select(bob);
            Assert.Null(service.ActiveLevelingRouteId);
            Assert.Equal(new RouteProgress(4, 8), service.GetSavedRouteProgress("first"));
        }

        exploration = Exploration();
        using var reloaded = new MapTargetsService(exploration);
        Assert.Null(reloaded.ActiveLevelingRouteId);
        Assert.Equal(new RouteProgress(4, 8), reloaded.GetSavedRouteProgress("first"));
        exploration.Select(alice);
        Assert.Equal(new RouteProgress(2, 8), reloaded.GetRouteProgress("first"));
    }

    [Fact]
    public void StopRemovesCompletedHistoryWithoutResettingItsSavedPosition()
    {
        using (var service = new MapTargetsService(Exploration()))
        {
            service.StartRoute("first");
            for (var i = 0; i < 8; i++) service.AdvanceRoute("first");
            Assert.NotEmpty(service.VisibleTargets);
            service.StopRoute("first");
            Assert.Empty(service.VisibleTargets);
            Assert.Null(service.ActiveLevelingRouteId);
        }

        using var reloaded = new MapTargetsService(Exploration());
        Assert.Empty(reloaded.VisibleTargets);
        Assert.Equal(new RouteProgress(8, 8), reloaded.GetSavedRouteProgress("first"));
        reloaded.StartRoute("first");
        Assert.Equal(new RouteProgress(8, 8), reloaded.GetRouteProgress("first"));
        reloaded.RewindRoute("first");
        Assert.Equal(new RouteProgress(7, 8), reloaded.GetSavedRouteProgress("first"));
    }

    [Theory]
    [InlineData("Verified main quest", "NPCs · Regional Quest", LevelingRouteStyle.MainQuest)]
    [InlineData("", "NPCs · Hero Quest", LevelingRouteStyle.RegionalQuest)]
    public void ObjectiveClassificationSurvivesTitleCleanup(string name, string kind, string color) =>
        Assert.Equal(color, LevelingRouteStyle.ColorOf(new RouteStop("altgard", 100, 100, name, kind, null, Color: color)));
}
