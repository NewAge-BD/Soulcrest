using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soulcrest.App.Components;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class LootTrackerPreparedReplayTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 27, 10, 0, 0, TimeSpan.Zero);
    private static LootTrackerRoutePoint Point(double x, double seconds) => new(x, 1000, At.AddSeconds(seconds), TimeSpan.FromSeconds(seconds));
    private static LootTrackerSession Session(params LootTrackerRouteSegment[] routes) => new()
    {
        Id = "prepared-example", Kind = LootTrackerSessionKind.Abyss, Status = LootTrackerSessionStatus.Completed,
        StartedAt = At, EndedAt = At.AddSeconds(20), Elapsed = TimeSpan.FromSeconds(20), RouteSegments = [.. routes]
    };
    private static LootTrackerSession GapSession => Session(new LootTrackerRouteSegment("a", "A", [Point(100, 0)]), new LootTrackerRouteSegment("a", "A", [Point(300, 10)]));

    [Fact]
    public void DropPlacementChangesRecordingFingerprintWhileItsLabelAndIconPreferenceDoNot()
    {
        var fact = new LootTrackerTimelineEvent("drop", LootTrackerTimelineEventKind.Drop, At.AddSeconds(5), TimeSpan.FromSeconds(5))
        { Loot = [new("Item:610530001", "Example item", LootTrackerLootKind.Item, 1)] };
        var source = GapSession with { TimelineVersion = 1, TimelineEvents = [fact] };
        var unplaced = LootTrackerPreparedReplay.Fingerprint(source);
        var position = new LootTrackerCombatPosition("a", "Example map", 150, 1000, At.AddSeconds(5));
        var placed = source with { TimelineEvents = [fact with { Position = position }] };
        var fingerprint = LootTrackerPreparedReplay.Fingerprint(placed);
        Assert.NotEqual(unplaced, fingerprint);
        Assert.Equal(fingerprint, LootTrackerPreparedReplay.Fingerprint(placed with
        { TimelineEvents = [fact with { Position = position with { MapName = "Translated map" },
            Loot = [fact.Loot[0] with { Name = "Translated item" }] }] }));
        Assert.NotEqual(fingerprint, LootTrackerPreparedReplay.Fingerprint(placed with
        { TimelineEvents = [fact with { Position = position with { X = 160 } }] }));
        Assert.Equal(unplaced, LootTrackerPreparedReplay.Fingerprint(source with { TimelineEvents = [fact with { Position = null }] }));
    }

    [Theory]
    [InlineData(0)] [InlineData(5)] [InlineData(10)]
    public void ConfirmedDropOnAnotherMapPreventsAnEstimatedShortcutIncludingGapBoundaries(int second)
    {
        var source = GapSession with { TimelineEvents = [new("other-map-drop", LootTrackerTimelineEventKind.Drop,
            At.AddSeconds(second), TimeSpan.FromSeconds(second)) { Loot = [new("Item:7", "Synthetic item", LootTrackerLootKind.Item, 1)],
                Position = new("b", "Other map", 120, 1000, At.AddSeconds(second)) }] };
        Assert.Empty(LootTrackerPreparedReplay.Prepare(source).Connections);
    }

    [Fact]
    public void GapProjectionInterpolatesOnlyTheDashedConnectionAndKeepsFactsChronological()
    {
        var session = GapSession with { CombatEvents = [new("future", LootTrackerCombatKind.Kill, At.AddSeconds(9), TimeSpan.FromSeconds(9))] };
        var prepared = LootTrackerPreparedReplay.Prepare(session);
        Assert.Single(prepared.Connections);
        var replay = LootTrackerSessionReplay.Build(session, TimeSpan.FromSeconds(5), prepared);
        Assert.Equal(LootTrackerReplayProvenance.GapEstimated, replay.Provenance);
        Assert.Equal(200, replay.PlayerPosition!.X); Assert.Equal(200, replay.LastPosition!.X);
        Assert.Equal("100,1000 200,1000", Assert.Single(replay.EstimatedConnections).Points);
        Assert.Empty(replay.Session.CombatEvents);
        Assert.Equal(100, Assert.Single(Assert.Single(replay.Session.RouteSegments).Points).X);
        var arrived = LootTrackerSessionReplay.Build(session, TimeSpan.FromSeconds(10), prepared);
        Assert.Equal(LootTrackerReplayProvenance.Matched, arrived.Provenance); Assert.Equal(300, arrived.PlayerPosition!.X);
        Assert.Single(arrived.Session.CombatEvents); Assert.Single(arrived.EstimatedConnections);
    }

    [Theory]
    [InlineData(0)] [InlineData(5)] [InlineData(10)]
    public void OwnAbyssDeathIncludingEitherBoundarySuppressesAnEstimatedConnection(int second)
    {
        var session = GapSession with { CombatEvents = [new("death", LootTrackerCombatKind.Death, At.AddSeconds(second), TimeSpan.FromSeconds(second))] };
        Assert.Empty(LootTrackerPreparedReplay.Prepare(session).Connections);
    }

    [Fact]
    public void ConnectionsHaveNoArbitraryTimeLimitButNeverCrossKnownOtherMaps()
    {
        var longGap = Session(new LootTrackerRouteSegment("a", "A", [Point(100, 0)]), new LootTrackerRouteSegment("a", "A", [Point(300, 10000)])) with
        { Elapsed = TimeSpan.FromSeconds(10000), EndedAt = At.AddSeconds(10000) };
        Assert.Single(LootTrackerPreparedReplay.Prepare(longGap).Connections);
        var mapChange = Session(new LootTrackerRouteSegment("a", "A", [Point(100, 0)]), new LootTrackerRouteSegment("b", "B", [Point(200, 5)]), new LootTrackerRouteSegment("a", "A", [Point(300, 10)]));
        Assert.Empty(LootTrackerPreparedReplay.Prepare(mapChange).Connections);
    }

    [Fact]
    public void HighResolutionGapCannotBridgeALowFrequencyMapChangeWithOnlyOriginalAt()
    {
        var session = Session(new LootTrackerRouteSegment("b", "B", [new(200, 1000, At.AddSeconds(5))]));
        var positions = new ReplayPositionReadResult([
            new(TimeSpan.Zero, At, "a", "A", 100, 1000, ReplayPositionProvenance.Matched, 0, 1),
            new(TimeSpan.FromSeconds(10), At.AddSeconds(10), "a", "A", 300, 1000, ReplayPositionProvenance.Flow, 1, 2)], [], "sidecar", true, false);
        Assert.Empty(LootTrackerPreparedReplay.Prepare(session, positions).Connections);
    }

    [Fact]
    public void HighResolutionGapCannotBridgeAnUntimedKnownMapBoundary()
    {
        var session = Session(new LootTrackerRouteSegment("a", "A", [Point(100, 0)]),
            new LootTrackerRouteSegment("b", "B", []), new LootTrackerRouteSegment("a", "A", [Point(300, 10)]));
        var positions = new ReplayPositionReadResult([
            new(TimeSpan.Zero, At, "a", "A", 100, 1000, ReplayPositionProvenance.Matched, 0, 1),
            new(TimeSpan.FromSeconds(10), At.AddSeconds(10), "a", "A", 300, 1000, ReplayPositionProvenance.Flow, 1, 2)], [], "sidecar", true, false);
        Assert.Empty(LootTrackerPreparedReplay.Prepare(session, positions).Connections);
    }

    [Fact]
    public void ExplicitSidecarGapSeparatesPartsEvenIfSegmentIdentifierWasUnchanged()
    {
        var positions = new ReplayPositionReadResult([
            new(TimeSpan.Zero, At, "a", "A", 100, 1000, ReplayPositionProvenance.Matched, 0, 1),
            new(TimeSpan.FromSeconds(10), At.AddSeconds(10), "a", "A", 300, 1000, ReplayPositionProvenance.Flow, 0, 2)],
            [new(TimeSpan.FromSeconds(5), ReplayPositionGapReason.QueueOverflow)], "sidecar", true, false);
        var prepared = LootTrackerPreparedReplay.Prepare(Session(), positions);
        Assert.Equal(2, prepared.Routes.Length); Assert.Single(prepared.Connections); Assert.True(prepared.CoverageLimited);
        Assert.Equal(LootTrackerReplayProvenance.GapEstimated,
            LootTrackerSessionReplay.Build(Session(), TimeSpan.FromSeconds(5), prepared).Provenance);
    }

    [Fact]
    public void HighResolutionPositionsKeepPrecisionWhileDisplayIsBoundedAndReusesCompletedParts()
    {
        const int sampleCount = 216000;
        var samples = Enumerable.Range(0, sampleCount).Select(index => new ReplayPositionSample(TimeSpan.FromSeconds(index / 60d),
            At.AddSeconds(index / 60d), "a", "A", 1000.123456789 + index / 100d, 1000.987654321,
            ReplayPositionProvenance.Flow, 0, index)).ToImmutableArray();
        var session = Session() with { Elapsed = TimeSpan.FromHours(1), EndedAt = At.AddHours(1), AbyssPoints = 123, RankPointsGained = 45 };
        var prepared = LootTrackerPreparedReplay.Prepare(session, new ReplayPositionReadResult(samples, [], "sidecar", true, false));
        Assert.Equal(sampleCount, Assert.Single(prepared.Routes).Points.Length);
        Assert.InRange(prepared.DisplayParts.Sum(part => part.Points.Length), 2, 4200);
        var first = LootTrackerSessionReplay.Build(session, TimeSpan.FromSeconds(900), prepared);
        var second = LootTrackerSessionReplay.Build(session, TimeSpan.FromSeconds(900.01), prepared);
        Assert.Same(prepared.DisplayParts[0], first.PreparedParts[0]); Assert.Same(first.PreparedParts[0], second.PreparedParts[0]);
        Assert.Equal(LootTrackerReplayProvenance.Flow, first.Provenance);
        Assert.Equal(samples[54000].X, first.PlayerPosition!.X); Assert.Equal(samples[54000].Y, first.PlayerPosition.Y);
        Assert.True(first.Session.RouteSegments.Sum(route => route.Points.Length) < 4200);
        var end = LootTrackerSessionReplay.Build(session, session.Elapsed, prepared);
        Assert.Equal(session.Elapsed, end.Elapsed); Assert.Equal(samples[^1].X, end.PlayerPosition!.X);
        Assert.Equal(session.AbyssPoints, end.Session.AbyssPoints); Assert.Equal(session.RankPointsGained, end.Session.RankPointsGained);
        Assert.Equal(sampleCount, prepared.Routes[0].Points.Length);
    }

    [Fact]
    public void OverlappingLegacyPartsChooseTheLatestActualPointEvenAcrossShorterIntermediateParts()
    {
        var session = Session(new LootTrackerRouteSegment("a", "A", [Point(100, 0), Point(500, 50), Point(1000, 100)]),
            new("a", "A", [Point(110, 10), Point(120, 20)]), new LootTrackerRouteSegment("a", "A", [Point(130, 30), Point(140, 40)])) with
        { Elapsed = TimeSpan.FromSeconds(100), EndedAt = At.AddSeconds(100) };
        var replay = LootTrackerSessionReplay.Build(session, TimeSpan.FromSeconds(50), LootTrackerPreparedReplay.Prepare(session));
        Assert.Equal(500, replay.PlayerPosition!.X); Assert.Equal(LootTrackerReplayProvenance.Matched, replay.Provenance);
    }

    [Fact]
    public void ResourceMarkerWaitsForItsOwnRecordedVisitTimeAfterTheFreshRouteObservation()
    {
        var session = Session(new LootTrackerRouteSegment("a", "A", [Point(100, 2)])) with
        { ResourceNodes = [new("node", "Resource", "a", "A", 110, 1010, 0, 0, At.AddSeconds(3))] };
        var prepared = LootTrackerPreparedReplay.Prepare(session);
        Assert.Empty(LootTrackerSessionReplay.Build(session, TimeSpan.FromSeconds(2), prepared).Session.ResourceNodes);
        Assert.Single(LootTrackerSessionReplay.Build(session, TimeSpan.FromSeconds(3), prepared).Session.ResourceNodes);
    }

    [Fact]
    public void RealOverlappingObservationWinsOverAnActiveEstimatedConnection()
    {
        var session = Session(new LootTrackerRouteSegment("a", "A", [Point(100, 0)]),
            new LootTrackerRouteSegment("a", "A", [Point(300, 10)]), new LootTrackerRouteSegment("a", "A", [Point(900, 5), Point(950, 6)]));
        var prepared = LootTrackerPreparedReplay.Prepare(session);
        Assert.Empty(prepared.Connections);
        var replay = LootTrackerSessionReplay.Build(session, TimeSpan.FromSeconds(6), prepared);
        Assert.Equal(950, replay.PlayerPosition!.X); Assert.Equal(LootTrackerReplayProvenance.Matched, replay.Provenance);
        Assert.Empty(replay.EstimatedConnections);
    }

    [Fact]
    public void ActualPointInALongerOverlappingPartWinsOverAnOtherwiseEligibleActiveConnection()
    {
        var session = Session(new LootTrackerRouteSegment("a", "A", [Point(100, 0), Point(950, 25), Point(1000, 100)]),
            new LootTrackerRouteSegment("a", "A", [Point(200, 10), Point(300, 20)]),
            new LootTrackerRouteSegment("a", "A", [Point(400, 30), Point(500, 40)])) with
        { Elapsed = TimeSpan.FromSeconds(100), EndedAt = At.AddSeconds(100) };
        var prepared = LootTrackerPreparedReplay.Prepare(session); Assert.Single(prepared.Connections);
        var replay = LootTrackerSessionReplay.Build(session, TimeSpan.FromSeconds(25), prepared);
        Assert.Equal(950, replay.PlayerPosition!.X); Assert.Equal(LootTrackerReplayProvenance.Matched, replay.Provenance);
        Assert.Empty(replay.EstimatedConnections);
    }

    [Fact]
    public void LaterConfirmedMarkerPositionWinsUsingItsPreservedElapsedDespiteWallClockJump()
    {
        var session = Session(new LootTrackerRouteSegment("a", "A", [Point(100, 10)])) with
        {
            CombatEvents = [new("kill", LootTrackerCombatKind.Kill, At.AddHours(-1), TimeSpan.FromSeconds(20),
                new("b", "B", 900, 800, At.AddHours(-1).AddSeconds(-1))) ]
        };
        var replay = LootTrackerSessionReplay.Build(session, TimeSpan.FromSeconds(20), LootTrackerPreparedReplay.Prepare(session));
        Assert.Equal("b", replay.MapId); Assert.Equal(900, replay.PlayerPosition!.X);
        Assert.Equal(TimeSpan.FromSeconds(19), replay.LastPositionElapsed); Assert.Equal(LootTrackerReplayProvenance.Matched, replay.Provenance);
    }

    [Fact]
    public void ManyOnePointPartsAndConnectionsRemainBoundedWithoutInventingCrossPartLines()
    {
        const int count = 20000;
        var samples = Enumerable.Range(0, count).Select(index => new ReplayPositionSample(TimeSpan.FromSeconds(index / 60d),
            At.AddSeconds(index / 60d), "a", "A", 100 + index / 100d, 1000, ReplayPositionProvenance.Matched, index, index)).ToImmutableArray();
        var session = Session() with { Elapsed = TimeSpan.FromHours(1), EndedAt = At.AddHours(1) };
        var prepared = LootTrackerPreparedReplay.Prepare(session, new ReplayPositionReadResult(samples, [], "sidecar", true, false));
        Assert.Equal(count, prepared.Routes.Length); Assert.Equal(count - 1, prepared.Connections.Length);
        Assert.InRange(prepared.DisplayParts.Sum(part => part.Points.Length), 1, LootTrackerPreparedReplay.DisplayPointBudgetPerMap);
        Assert.InRange(prepared.DisplayConnections.Length, 1, LootTrackerPreparedReplay.DisplayConnectionBudgetPerMap);
        Assert.All(prepared.DisplayParts, part => Assert.Single(part.Points));
        Assert.True(prepared.Matches(session, prepared.SourceFingerprint));
        var omittedIndex = Enumerable.Range(1, count - 2).First(index => !prepared.DisplayParts.Any(part => part.RouteIndex == index));
        var actual = LootTrackerSessionReplay.Build(session, samples[omittedIndex].Elapsed, prepared);
        Assert.Equal(samples[omittedIndex].X, actual.PlayerPosition!.X);
        Assert.Contains(actual.PreparedParts, part => part.RouteIndex == omittedIndex && part.Points[^1].X == samples[omittedIndex].X);
        Assert.All(actual.PreparedParts, part => Assert.Single(part.Points));
        var head = samples[count - 2].Elapsed + TimeSpan.FromTicks((samples[count - 1].Elapsed - samples[count - 2].Elapsed).Ticks / 2);
        var first = LootTrackerSessionReplay.Build(session, head, prepared);
        var second = LootTrackerSessionReplay.Build(session, head + TimeSpan.FromTicks(1), prepared);
        Assert.Equal(head, first.Elapsed); Assert.Equal(LootTrackerReplayProvenance.GapEstimated, first.Provenance);
        Assert.InRange(first.PreparedParts.Sum(part => part.Points.Length), 1, LootTrackerPreparedReplay.DisplayPointBudgetPerMap + 2);
        Assert.InRange(first.EstimatedConnections.Count, 1, LootTrackerPreparedReplay.DisplayConnectionBudgetPerMap + 1);
        Assert.Same(prepared.DisplayConnections[0].Line, first.EstimatedConnections[0]);
        Assert.Same(first.EstimatedConnections[0], second.EstimatedConnections[0]);
    }

    [Fact]
    public void OneHourWithOneSecondSegmentsUsesTheGlobalPointBudgetInsteadOfRestartingStridePerSegment()
    {
        const int count = 216000;
        var samples = Enumerable.Range(0, count).Select(index => new ReplayPositionSample(TimeSpan.FromSeconds(index / 60d),
            At.AddSeconds(index / 60d), "a", "A", 100 + index / 100d, 1000, ReplayPositionProvenance.Flow, index / 60, index)).ToImmutableArray();
        var session = Session() with { Elapsed = TimeSpan.FromHours(1), EndedAt = At.AddHours(1) };
        var prepared = LootTrackerPreparedReplay.Prepare(session, new ReplayPositionReadResult(samples, [], "sidecar", true, false));
        Assert.Equal(3600, prepared.Routes.Length); Assert.Equal(count, prepared.Routes.Sum(route => route.Points.Length));
        Assert.InRange(prepared.DisplayParts.Sum(part => part.Points.Length), 1, LootTrackerPreparedReplay.DisplayPointBudgetPerMap);
        var end = LootTrackerSessionReplay.Build(session, session.Elapsed, prepared);
        Assert.Equal(session.Elapsed, end.Elapsed); Assert.Equal(samples[^1].X, end.PlayerPosition!.X);
        Assert.InRange(end.PreparedParts.Sum(part => part.Points.Length), 1, LootTrackerPreparedReplay.DisplayPointBudgetPerMap + 2);
    }

    [Fact]
    public void UntimedLegacyRouteAndResourceMarkersOnlyAppearAtEndWithoutInventedPlayerTime()
    {
        var session = Session(new LootTrackerRouteSegment("a", "A", [new(100, 100), new(200, 200)])) with
        { Kind = LootTrackerSessionKind.Lifeskill, ResourceNodes = [new("node", "Resource", "a", "A", 210, 210, 0, 1)] };
        var prepared = LootTrackerPreparedReplay.Prepare(session);
        var before = LootTrackerSessionReplay.Build(session, session.Elapsed - TimeSpan.FromSeconds(1), prepared);
        Assert.Empty(before.Session.RouteSegments); Assert.Empty(before.Session.ResourceNodes);
        var end = LootTrackerSessionReplay.Build(session, session.Elapsed, prepared);
        Assert.Single(end.Session.ResourceNodes); Assert.Single(LootTrackerPresentation.GetValidatedResourceVisits(end.Session));
        Assert.Null(end.PlayerPosition); Assert.Null(end.LastPosition); Assert.Equal(LootTrackerReplayProvenance.Missing, end.Provenance);
    }

    [Fact]
    public void PureUntimedLegacyPathRetainsItsStaticEndViewOnAnOfflineMap()
    {
        var session = Session(new LootTrackerRouteSegment("a", "A", [new(100, 100), new(200, 200)]));
        var replay = LootTrackerSessionReplay.Build(session, session.Elapsed, LootTrackerPreparedReplay.Prepare(session));
        var map = new LootTrackerRouteMapPreview.MapDefinition("a", "A", "a/tiles/{z}_{x}_{y}.webp", 4096, 4, 512, 1, 6);
        var preview = LootTrackerRouteMapPreview.Build(replay.Session, "a", map, _ => true, replay);
        Assert.Equal(LootTrackerRouteMapPreview.Availability.Ready, preview.Status); Assert.Single(preview.Lines);
        Assert.Null(replay.PlayerPosition); Assert.Null(replay.LastPosition);
    }

    [Fact]
    public void FingerprintExcludesNamesAndViewButIncludesRawFactsAndSidecarIdentity()
    {
        var session = GapSession;
        var renamed = session with { Character = new(null, "Example"), Location = "Localized location", ProjectionViewMode = LootTrackerViewMode.Pve,
            RouteSegments = session.RouteSegments.Select(route => route with { MapName = "Localized map" }).ToImmutableArray() };
        Assert.Equal(LootTrackerPreparedReplay.Fingerprint(session, "raw"), LootTrackerPreparedReplay.Fingerprint(renamed, "raw"));
        Assert.NotEqual(LootTrackerPreparedReplay.Fingerprint(session, "raw"), LootTrackerPreparedReplay.Fingerprint(session, "other"));
        var changed = session with { RouteSegments = [new("a", "A", [Point(101, 0)])] };
        Assert.NotEqual(LootTrackerPreparedReplay.Fingerprint(session, "raw"), LootTrackerPreparedReplay.Fingerprint(changed, "raw"));
    }

    [Fact]
    public void PreparedArtifactRejectsLiveWrongSourceAndCorruptGeometry()
    {
        var session = GapSession; var prepared = LootTrackerPreparedReplay.Prepare(session);
        Assert.Throws<InvalidOperationException>(() => LootTrackerPreparedReplay.Prepare(session with { Status = LootTrackerSessionStatus.Live }));
        Assert.True(prepared.Matches(session, LootTrackerPreparedReplay.Fingerprint(session)));
        Assert.False(prepared.Matches(session, "wrong"));
        var corrupted = JsonSerializer.Deserialize<LootTrackerPreparedReplay>(JsonSerializer.Serialize(prepared).Replace("100,1000", "101,1000", StringComparison.Ordinal))!;
        Assert.False(corrupted.Matches(session, prepared.SourceFingerprint));
        Assert.False((prepared with { Routes = [prepared.Routes[0] with { MapId = null! }] }).Matches(session, prepared.SourceFingerprint));
        Assert.False((prepared with { Version = 999 }).Matches(session, prepared.SourceFingerprint));
    }

    [Fact]
    public async Task MapDistinguishesEstimatedAndFlowPositionsAndDoesNotAddSegmentDotsPerDisplayChunk()
    {
        var session = GapSession; var prepared = LootTrackerPreparedReplay.Prepare(session);
        var estimated = await Render(session, LootTrackerSessionReplay.Build(session, TimeSpan.FromSeconds(5), prepared));
        Assert.Contains("data-testid=\"replay-estimated-connector\"", estimated);
        Assert.Contains("stroke-dasharray=\"8 6\"", estimated); Assert.Contains("data-testid=\"replay-player-estimated\"", estimated);
        var points = Enumerable.Range(0, 300).Select(index => Point(100 + index, index / 20d)).ToImmutableArray();
        var many = Session(new LootTrackerRouteSegment("a", "A", points)); var manyPrepared = LootTrackerPreparedReplay.Prepare(many);
        Assert.True(manyPrepared.DisplayParts.Length > 1);
        var markup = await Render(many, LootTrackerSessionReplay.Build(many, TimeSpan.FromSeconds(15), manyPrepared));
        Assert.Equal(1, Count(markup, "class=\"lt-route-start\"")); Assert.Equal(1, Count(markup, "class=\"lt-route-end\""));
        var flow = LootTrackerPreparedReplay.Prepare(Session(), new ReplayPositionReadResult([
            new(TimeSpan.Zero, At, "a", "A", 100, 1000, ReplayPositionProvenance.Flow, 0, 1)], [], "sidecar", true, false));
        Assert.Contains("data-provenance=\"flow\"", await Render(Session(), LootTrackerSessionReplay.Build(Session(), TimeSpan.Zero, flow)));
    }

    private static int Count(string text, string value) => text.Split(value, StringSplitOptions.None).Length - 1;
    private static async Task<string> Render(LootTrackerSession session, LootTrackerSessionReplay replay)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<LootTrackerRouteMap>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { ["Session"] = session, ["Replay"] = replay,
                ["MapDataDirectory"] = Path.Combine(Path.GetTempPath(), "soulcrest-nonexistent-map-" + Guid.NewGuid().ToString("N")) }))).ToHtmlString());
    }
}
