using System.Drawing;
using Soulcrest.App.Services;
using Soulcrest.Core.Text;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class ExplorationScanSessionTests
{
    private static readonly ExplorationPlace[] Places =
    [
        new("ishalgen-cave", "ishalgen", "dungeon", 1, "Synthetic Shared Cavern Entrance", "Eingang zur Synthetischen Höhle"),
        new("ishalgen-fort", "ishalgen", "stronghold", 2, "Synthetic Shared Camp", "Synthetisches Lager"),
        new("ishalgen-beacon", "ishalgen", "kibelisk", 3, "Synthetic Shared Beacon", "Synthetische Wegstation"),
        new("ishalgen-ridge", "ishalgen", "kibelisk", 4, "Synthetic Shared Ridge", "Synthetischer Grat"),
        new("altgard-cave", "altgard", "dungeon", 5, "Synthetic Shared Cavern Entrance", "Eingang zur Synthetischen Höhle"),
        new("altgard-fort", "altgard", "stronghold", 6, "Synthetic Shared Camp", "Synthetisches Lager"),
        new("altgard-beacon", "altgard", "kibelisk", 7, "Synthetic Shared Beacon", "Synthetische Wegstation"),
        new("altgard-ridge", "altgard", "kibelisk", 8, "Synthetic Shared Ridge", "Synthetischer Grat"),
        new("abyss-fort", "abyss", "stronghold", 9, "Synthetic Abyss Camp", null),
        new("ignored-pet", "altgard", "pet", 10, "Synthetic Unrelated Place", null),
    ];
    private static readonly ExplorationLayout Layout = ExplorationLayout.ForWindow(new Size(2559, 1439));

    [Fact]
    public void InitialStateHasNoMapOrInheritedEvidenceAndDoesNotWriteProgress()
    {
        using var fixture = new Fixture();
        var before = File.ReadAllBytes(fixture.Path);
        var session = new ExplorationScanSession(fixture.Exploration);
        Assert.Null(session.Map);
        Assert.Empty(session.MapTitles);
        Assert.Empty(session.Candidates);
        Assert.Null(session.Kibelisks);
        AssertFresh(session);
        Assert.Equal(before, File.ReadAllBytes(fixture.Path));
    }

    [Fact]
    public void InitialIshalgenAltgardAndReturnAlwaysRebuildOnlyTheMatchingMapCatalog()
    {
        using var fixture = new Fixture();
        var session = new ExplorationScanSession(fixture.Exploration);
        foreach (var map in new[] { "ishalgen", "altgard", "ishalgen" })
        {
            Assert.True(session.SwitchMap(map));
            Assert.Equal(map, session.Map);
            Assert.Equal(fixture.Exploration.MapTitles(map), session.MapTitles);
            Assert.Equal(Places.Where(place => place.Map == map && place.Kind is "dungeon" or "stronghold"), session.Candidates);
            Assert.Equal(2, session.Candidates.Length);
            Assert.All(session.Candidates, place => Assert.Equal(map, place.Map));
            Assert.NotNull(session.Kibelisks);
            AssertFresh(session);
        }
    }

    [Fact]
    public void RepeatingTheSameMapRetainsItsVotesReadinessAndCollectedState()
    {
        using var fixture = new Fixture();
        var session = new ExplorationScanSession(fixture.Exploration);
        session.SwitchMap("ishalgen");
        var page = EndPage("ishalgen-cave");
        Assert.Empty(session.Confirmation.Observe(page));
        session.Kibelisks!.Observe(GermanKibeliskPage());
        session.Seen.Add("ishalgen-cave");
        session.FinishedKinds.Add("stronghold");
        session.FullKinds.Add("stronghold");
        session.KibeliskSettled = 1;
        var confirmation = session.Confirmation;
        var kibelisks = session.Kibelisks;
        var candidates = session.Candidates;
        var titles = session.MapTitles;
        var seen = session.Seen;
        Assert.False(session.SwitchMap("ishalgen"));
        Assert.Same(confirmation, session.Confirmation);
        Assert.Same(kibelisks, session.Kibelisks);
        Assert.Same(candidates, session.Candidates);
        Assert.Same(titles, session.MapTitles);
        Assert.Same(seen, session.Seen);
        Assert.Contains("ishalgen-cave", session.Seen);
        Assert.Contains("stronghold", session.FinishedKinds);
        Assert.Contains("stronghold", session.FullKinds);
        Assert.Equal(1, session.KibeliskSettled);
        Assert.Equal(new[] { "ishalgen-cave" }, session.Confirmation.Observe(page));
        Assert.True(session.Confirmation.Finished);
        session.Kibelisks.Observe(GermanKibeliskPage());
        Assert.True(session.Kibelisks.Complete);
        Assert.Equal(new[] { "ishalgen-beacon" }, session.Kibelisks.Result().Bound);
    }

    [Fact]
    public void SwitchingMapsDropsAllCategoryCompletionAndKibeliskEvidenceIncludingOnReturn()
    {
        using var fixture = new Fixture();
        var session = new ExplorationScanSession(fixture.Exploration);
        session.SwitchMap("ishalgen");
        Prime(session, "ishalgen-cave");
        var oldConfirmation = session.Confirmation;
        var oldKibelisks = session.Kibelisks;
        var oldSeen = session.Seen;
        var oldFinished = session.FinishedKinds;
        var oldFull = session.FullKinds;
        Assert.True(session.SwitchMap("altgard"));
        AssertFresh(session);
        Assert.NotSame(oldConfirmation, session.Confirmation);
        Assert.NotSame(oldKibelisks, session.Kibelisks);
        Assert.NotSame(oldSeen, session.Seen);
        Assert.NotSame(oldFinished, session.FinishedKinds);
        Assert.NotSame(oldFull, session.FullKinds);
        Assert.Empty(session.Kibelisks!.Confirmed);
        Assert.False(session.Kibelisks.Ended);
        Prime(session, "altgard-cave");
        Assert.True(session.SwitchMap("ishalgen"));
        AssertFresh(session);
        Assert.NotSame(oldConfirmation, session.Confirmation);
        Assert.NotSame(oldKibelisks, session.Kibelisks);
        Assert.Empty(session.Confirmation.Observe(EndPage("ishalgen-cave")));
        Assert.False(session.Confirmation.Finished);
        Assert.Empty(session.Kibelisks!.Observe(GermanKibeliskPage()));
        Assert.False(session.Kibelisks.Complete);
    }

    [Theory]
    [InlineData("dungeon", "Versiegelter Dungeon", "cave")]
    [InlineData("stronghold", "Garnison", "fort")]
    public void OneHundredPercentNeedsTwoFreshObservationsAfterEveryMapSwitch(string kind, string heading, string suffix)
    {
        using var fixture = new Fixture();
        var session = new ExplorationScanSession(fixture.Exploration);
        using var capture = new Bitmap(Layout.CaptureBounds.Width, Layout.CaptureBounds.Height);
        session.SwitchMap("ishalgen");
        var first = FullPage(session, capture, heading);
        Assert.Equal(kind, first.Kind);
        Assert.True(first.Full);
        Assert.Empty(session.Confirmation.Observe(first));
        Assert.False(session.Confirmation.FullConfirmed);
        session.SwitchMap("altgard");
        var second = FullPage(session, capture, heading);
        Assert.Empty(session.Confirmation.Observe(second));
        Assert.False(session.Confirmation.FullConfirmed);
        Assert.False(session.Confirmation.Finished);
        Assert.Equal(new[] { "altgard-" + suffix }, session.Confirmation.Observe(second));
        Assert.True(session.Confirmation.FullConfirmed);
        Assert.True(session.Confirmation.Finished);
        session.FullKinds.Add(kind);
        session.FinishedKinds.Add(kind);
        session.SwitchMap("ishalgen");
        Assert.Empty(session.FullKinds);
        Assert.Empty(session.FinishedKinds);
        Assert.Empty(session.Confirmation.Observe(FullPage(session, capture, heading)));
        Assert.False(session.Confirmation.FullConfirmed);
        Assert.Equal(new[] { "ishalgen-" + suffix }, session.Confirmation.Observe(first));
    }

    [Fact]
    public void ListEndAndReadNamesCannotSurviveAChangeAwayAndBack()
    {
        using var fixture = new Fixture();
        var session = new ExplorationScanSession(fixture.Exploration);
        session.SwitchMap("ishalgen");
        var page = EndPage("ishalgen-cave");
        session.Confirmation.Observe(page);
        session.Confirmation.Observe(page);
        Assert.True(session.Confirmation.Finished);
        Assert.Contains("ishalgen-cave", session.Confirmation.Read);
        session.SwitchMap("altgard");
        Assert.False(session.Confirmation.Finished);
        Assert.Empty(session.Confirmation.Read);
        session.SwitchMap("ishalgen");
        Assert.Empty(session.Confirmation.Observe(page));
        Assert.False(session.Confirmation.Finished);
        Assert.Empty(session.Confirmation.Read);
        Assert.Equal(new[] { "ishalgen-cave" }, session.Confirmation.Observe(page));
        Assert.True(session.Confirmation.Finished);
    }

    [Fact]
    public void GermanKibeliskStatusesWithIdenticalNamesAndNumbersNeedFreshVotesForTheNewMap()
    {
        using var fixture = new Fixture();
        var session = new ExplorationScanSession(fixture.Exploration);
        var page = GermanKibeliskPage();
        Assert.Equal(new[] { true, false }, page.Rows.Select(row => row.Bound));
        Assert.True(page.End);
        session.SwitchMap("ishalgen");
        session.Kibelisks!.Observe(page); // one vote cannot migrate to the next map
        session.SwitchMap("altgard");
        Assert.Empty(session.Kibelisks!.Observe(page));
        Assert.Empty(session.Kibelisks.Confirmed);
        Assert.Empty(session.Kibelisks.Result().Bound);
        Assert.Empty(session.Kibelisks.Result().Unbound);
        Assert.False(session.Kibelisks.Ended);
        Assert.False(session.Kibelisks.Complete);
        session.Kibelisks.Observe(page);
        var result = session.Kibelisks.Result();
        Assert.Equal(new[] { "altgard-beacon" }, result.Bound);
        Assert.Equal(new[] { "altgard-ridge" }, result.Unbound);
        Assert.Empty(result.Unknown);
        Assert.Empty(result.Open);
        Assert.True(session.Kibelisks.Ended);
        Assert.True(session.Kibelisks.Complete);
        session.SwitchMap("ishalgen");
        Assert.Empty(session.Kibelisks!.Observe(page));
        Assert.False(session.Kibelisks.Ended);
        Assert.Empty(session.Kibelisks.Result().Bound);
    }

    [Fact]
    public void MapsWithoutKibelisksDoNotInheritAnOldCompleteKibeliskList()
    {
        using var fixture = new Fixture();
        var session = new ExplorationScanSession(fixture.Exploration);
        session.SwitchMap("altgard");
        Prime(session, "altgard-cave");
        session.SwitchMap("abyss");
        Assert.Null(session.Kibelisks);
        Assert.Single(session.Candidates);
        Assert.Equal("abyss-fort", session.Candidates[0].Id);
        AssertFresh(session);
    }

    [Fact]
    public void SessionSwitchesNeverSelectACharacterOrRewriteTheirAlreadySavedExploration()
    {
        using var fixture = new Fixture();
        var exploration = fixture.Exploration;
        var first = exploration.ActiveId;
        exploration.SetDone(first, ["ishalgen-cave", "ishalgen-beacon"]);
        exploration.Add("Synthetic Second Explorer");
        var second = exploration.ActiveId;
        exploration.SetDone(second, ["altgard-fort"]);
        var before = File.ReadAllBytes(fixture.Path);
        var session = new ExplorationScanSession(exploration);
        foreach (var map in new[] { "ishalgen", "altgard", "ishalgen" })
        {
            session.SwitchMap(map);
            Prime(session, map + "-cave");
            Assert.Equal(second, exploration.ActiveId);
            Assert.Equal(new[] { "altgard-fort" }, exploration.Completed);
            Assert.Equal(before, File.ReadAllBytes(fixture.Path));
        }
        var reloaded = new ExplorationService(fixture.Path, Places);
        Assert.Equal(second, reloaded.ActiveId);
        Assert.Equal(new[] { "altgard-fort" }, reloaded.Completed);
        reloaded.Select(first);
        Assert.Equal(new[] { "ishalgen-beacon", "ishalgen-cave" }, reloaded.Completed.Order());
    }

    [Fact]
    public void AnInterruptionBetweenGermanKibeliskFramesRequiresTwoNewReadings()
    {
        using var fixture = new Fixture();
        var session = new ExplorationScanSession(fixture.Exploration);
        session.SwitchMap("ishalgen");
        var page = GermanKibeliskPage();
        session.Kibelisks!.Observe(page);
        var priorConfirmation = session.Confirmation;
        session.Confirmation.Observe(EndPage("ishalgen-cave"));
        session.ResetPending();
        Assert.NotSame(priorConfirmation, session.Confirmation);
        Assert.Empty(session.Confirmation.Observe(EndPage("ishalgen-cave")));
        Assert.False(session.Confirmation.Finished);
        Assert.Empty(session.Kibelisks.Observe(page));
        Assert.Empty(session.Kibelisks.Confirmed);
        Assert.False(session.Kibelisks.Ended);
        Assert.False(session.Kibelisks.Complete);
        session.Kibelisks.Observe(page);
        Assert.Equal(new[] { "ishalgen-beacon" }, session.Kibelisks.Result().Bound);
        Assert.Equal(new[] { "ishalgen-ridge" }, session.Kibelisks.Result().Unbound);
        Assert.True(session.Kibelisks.Ended);
    }

    [Fact]
    public void PendingResetRetainsConfirmedRowsEndAndCollectedMapState()
    {
        using var fixture = new Fixture();
        var session = new ExplorationScanSession(fixture.Exploration);
        session.SwitchMap("ishalgen");
        Prime(session, "ishalgen-cave");
        var kibelisks = session.Kibelisks!;
        var candidates = session.Candidates;
        var titles = session.MapTitles;
        session.ResetPending();
        Assert.Equal("ishalgen", session.Map);
        Assert.Same(kibelisks, session.Kibelisks);
        Assert.Same(candidates, session.Candidates);
        Assert.Same(titles, session.MapTitles);
        Assert.Equal(2, kibelisks.Confirmed.Count);
        Assert.True(kibelisks.Ended);
        Assert.True(kibelisks.Complete);
        Assert.Equal(new[] { "ishalgen-beacon" }, kibelisks.Result().Bound);
        Assert.Equal(new[] { "ishalgen-ridge" }, kibelisks.Result().Unbound);
        Assert.Contains("ishalgen-cave", session.Seen);
        Assert.Equal(new[] { "dungeon", "stronghold" }, session.FinishedKinds.Order());
        Assert.Equal(new[] { "dungeon" }, session.FullKinds);
        Assert.Equal(2, session.KibeliskSettled);
        Assert.Empty(session.Confirmation.Read);
        Assert.False(session.Confirmation.Finished);
        Assert.False(session.Confirmation.FullConfirmed);
        // Existing status stays trusted, but a new status needs two fresh readings.
        var changed = GermanKibeliskPage() with
        {
            Rows = GermanKibeliskPage().Rows.Select(row => row.Number == 1 ? row with { Bound = false } : row).ToArray(),
        };
        Assert.Empty(kibelisks.Observe(changed));
        Assert.True(kibelisks.Confirmed[1].Bound);
        kibelisks.Observe(changed);
        Assert.False(kibelisks.Confirmed[1].Bound);
    }

    [Fact]
    public void AnInterruptedKibeliskEndVoteCannotUncheckUndiscoveredPlacesAfterOneNewFrame()
    {
        ExplorationPlace[] places = [.. Places,
            new("ishalgen-unlisted", "ishalgen", "kibelisk", 11, "Synthetic Unlisted Beacon", null)];
        using var fixture = new Fixture(places);
        var session = new ExplorationScanSession(fixture.Exploration);
        session.SwitchMap("ishalgen");
        var notAtEnd = GermanKibeliskPage() with { End = false };
        var atEnd = GermanKibeliskPage();
        var kibelisks = session.Kibelisks!;
        kibelisks.Observe(notAtEnd);
        kibelisks.Observe(notAtEnd);
        Assert.Equal(2, kibelisks.Confirmed.Count);
        Assert.False(kibelisks.Ended);
        kibelisks.Observe(atEnd); // only the first observation of the empty list end
        Assert.False(kibelisks.Ended);
        session.ResetPending();
        kibelisks.Observe(atEnd);
        Assert.False(kibelisks.Ended);
        Assert.DoesNotContain("ishalgen-unlisted", kibelisks.Result().Unbound);
        kibelisks.Observe(atEnd);
        Assert.True(kibelisks.Ended);
        Assert.Contains("ishalgen-unlisted", kibelisks.Result().Unbound);
    }

    [Fact]
    public void KibeliskOnlyResetOnACategorySwitchDoesNotResetDungeonConfirmation()
    {
        using var fixture = new Fixture();
        var session = new ExplorationScanSession(fixture.Exploration);
        session.SwitchMap("ishalgen");
        var confirmation = session.Confirmation;
        var dungeon = EndPage("ishalgen-cave");
        confirmation.Observe(dungeon);
        session.Kibelisks!.Observe(GermanKibeliskPage());
        session.Kibelisks.ResetPending();
        Assert.Same(confirmation, session.Confirmation);
        Assert.Equal(new[] { "ishalgen-cave" }, confirmation.Observe(dungeon));
        Assert.True(confirmation.Finished);
        Assert.Empty(session.Kibelisks.Observe(GermanKibeliskPage()));
        Assert.Empty(session.Kibelisks.Confirmed);
    }

    private static void AssertFresh(ExplorationScanSession session)
    {
        Assert.Empty(session.Seen);
        Assert.Empty(session.FinishedKinds);
        Assert.Empty(session.FullKinds);
        Assert.Equal(0, session.KibeliskSettled);
        Assert.Empty(session.Confirmation.Read);
        Assert.False(session.Confirmation.FullConfirmed);
        Assert.False(session.Confirmation.Finished);
    }

    private static void Prime(ExplorationScanSession session, string id)
    {
        var page = EndPage(id);
        session.Confirmation.Observe(page);
        session.Confirmation.Observe(page);
        session.Seen.Add(id);
        session.FinishedKinds.UnionWith(["dungeon", "stronghold"]);
        session.FullKinds.Add("dungeon");
        session.KibeliskSettled = 2;
        session.Kibelisks!.Observe(GermanKibeliskPage());
        session.Kibelisks.Observe(GermanKibeliskPage());
        Assert.True(session.Kibelisks.Complete);
    }

    private static ExplorationScanService.Page EndPage(string id) => new([id], [], true, "dungeon", false,
        [new(new OcrLine("Synthetic Cavern", 40, 400, 250, 20), id, true)]);

    private static ExplorationScanService.Page FullPage(ExplorationScanSession session, Bitmap capture, string heading) =>
        ExplorationList.Read([new("Karte: " + session.Map, 85, 30, 270, 40), new(heading, 120, 220, 310, 30),
            new("100 %", 365, 270, 65, 25)], session.Candidates, capture, Layout, session.Map!, session.MapTitles);

    private static KibeliskPage GermanKibeliskPage() => KibeliskList.Read(
        [new("1. Synthetische Wegstation", 48, 40, 300, 19), new("Bindung abgeschlossen", 48, 70, 300, 19),
         new("2. Synthetischer Grat", 48, 126, 300, 19), new("Bindung nicht abgeschlossen", 48, 156, 300, 19)], 1000);

    private sealed class Fixture : IDisposable
    {
        private readonly string _folder = System.IO.Path.Combine(AppPaths.DataDirectory, "exploration-scan-session-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(_folder, "exploration.json");
        public ExplorationService Exploration { get; }
        public Fixture(IReadOnlyList<ExplorationPlace>? places = null)
        {
            Exploration = new ExplorationService(Path, places ?? Places);
            Exploration.Rename("Synthetic Explorer");
        }
        public void Dispose() => Directory.Delete(_folder, true);
    }
}
