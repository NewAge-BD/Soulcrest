using System.Text.Json;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class LifeskillSessionStoreTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FailureOnlySchemaTwoJournalRecoversCountsAndTrimsAfterItsLastAttempt()
    {
        using var fixture = new Fixture(schemaVersion: 2);
        fixture.Failed(0);
        fixture.Position(0);
        fixture.Store.Checkpoint(fixture.State);
        fixture.Failed(40);
        fixture.Position(40);
        fixture.Position(60);
        var loaded = fixture.Store.Read();
        Assert.Empty(loaded.Issues);
        var session = Assert.Single(loaded.Sessions);
        Assert.Equal(2, session.FailedHarvestCount);
        Assert.Equal(0, session.HarvestCount);
        Assert.Equal(0, session.ItemCount);
        Assert.Empty(session.Loot);
        Assert.Null(session.LastLootAt);
        Assert.Equal(At.AddSeconds(40), session.EndedAt);
        Assert.Equal(TimeSpan.FromSeconds(40), session.Elapsed);
        Assert.Equal(2, Assert.Single(session.RouteSegments).Points.Length);
        Assert.Equal(LootTrackerSessionStatus.Interrupted, session.Status);
        fixture.Store.Checkpoint(fixture.State);
        var checkpoint = JsonSerializer.Deserialize<LifeskillCheckpoint>(
            File.ReadAllText(Path.Combine(fixture.Root, fixture.Id, "summary-v1.json")))!;
        Assert.Equal(2, checkpoint.SchemaVersion);
        Assert.Equal(2, checkpoint.FailedHarvestCount);
        Assert.Equal(2, Assert.Single(fixture.Store.Read().Sessions).FailedHarvestCount);
    }

    [Fact]
    public void NewSuccessfulSessionsKnowZeroFailuresButLegacyCountsRemainUnknown()
    {
        using var current = new Fixture(schemaVersion: 2);
        current.Harvest(1, 0);
        Assert.Equal(0, Assert.Single(current.Store.Read().Sessions).FailedHarvestCount);
        using var legacy = new Fixture();
        legacy.Harvest(1, 0);
        legacy.Store.Checkpoint(legacy.State);
        Assert.Null(Assert.Single(legacy.Store.Read().Sessions).FailedHarvestCount);
        Assert.False(legacy.State.CanApply(new() { Sequence = legacy.State.Sequence + 1,
            Kind = LifeskillEventKind.Failed, At = At }));
    }

    [Fact]
    public void OldNumericJournalKindsRemainCompatibleWithoutAnInventedFailureCount()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Journal, """
            {"SchemaVersion":1,"Sequence":1,"Kind":0,"At":"2026-10-25T12:00:00+00:00","Elapsed":"00:00:00","Character":{"ProfileId":"11111111111111111111111111111111","Name":"Example character A"}}
            {"SchemaVersion":1,"Sequence":2,"Kind":1,"At":"2026-10-25T12:00:10+00:00","Elapsed":"00:00:10","Items":[{"Id":"item:101","Name":"Example item","Kind":0,"Quantity":2}]}
            {"SchemaVersion":1,"Sequence":3,"Kind":4,"At":"2026-10-25T12:00:10+00:00","Elapsed":"00:00:10"}
            """ + "\n");
        var loaded = fixture.Store.Read();
        Assert.Empty(loaded.Issues);
        var session = Assert.Single(loaded.Sessions);
        Assert.Equal(1, session.HarvestCount);
        Assert.Equal(2, session.ItemCount);
        Assert.Null(session.FailedHarvestCount);
        Assert.Equal(LootTrackerSessionStatus.Completed, session.Status);
        Assert.Equal(TimeSpan.FromSeconds(10), session.Elapsed);
    }

    [Fact]
    public void FailureRecordsCannotCarryItemsRegressAttemptsOrRepeatTheirSequence()
    {
        using var fixture = new Fixture(schemaVersion: 2);
        fixture.Failed(10);
        var next = new LifeskillSessionEvent { SchemaVersion = 2, Sequence = fixture.State.Sequence + 1,
            Kind = LifeskillEventKind.Failed, At = At.AddSeconds(20), Elapsed = TimeSpan.FromSeconds(20) };
        Assert.False(fixture.State.CanApply(next with { Items = [new("item:101", "Example item", LootTrackerLootKind.Item, 1)] }));
        Assert.False(fixture.State.CanApply(next with { Elapsed = TimeSpan.Zero }));
        Assert.False(fixture.State.CanApply(next with { Sequence = fixture.State.Sequence }));
        Assert.True(fixture.State.CanApply(next));
        File.AppendAllText(fixture.Journal, File.ReadAllLines(fixture.Journal)[^1] + "\n");
        var loaded = fixture.Store.Read();
        Assert.Contains("invalid-journal-record", loaded.Issues);
        Assert.Equal(1, Assert.Single(loaded.Sessions).FailedHarvestCount);
    }

    [Fact]
    public void JournalReplayDoesNotAddTheCheckpointTotalsAgain()
    {
        using var fixture = new Fixture();
        fixture.Harvest(3, 0);
        fixture.Store.Checkpoint(fixture.State);
        fixture.Harvest(2, 10); // Durable journal is newer than the checkpoint.
        var loaded = fixture.Store.Read();
        var session = Assert.Single(loaded.Sessions);
        Assert.Empty(loaded.Issues);
        Assert.Equal(2, session.HarvestCount);
        Assert.Equal(5, session.ItemCount);
        Assert.Equal(5, Assert.Single(session.Loot).Quantity);
        Assert.Equal(LootTrackerSessionStatus.Interrupted, session.Status);
        Assert.Equal(TimeSpan.FromSeconds(10), session.Elapsed);
        Assert.Equal(At.AddSeconds(10), session.EndedAt);
        var reread = Assert.Single(fixture.Store.Read().Sessions);
        Assert.Equal(session.Id, reread.Id);
        Assert.Equal(session.ItemCount, reread.ItemCount);
        Assert.Equal(session.HarvestCount, reread.HarvestCount);
    }

    [Fact]
    public void CompletedSessionRetainsItsFrozenCharacterAndDoesNotNeedRecoveryInterruption()
    {
        using var fixture = new Fixture();
        fixture.Harvest(2, 0);
        fixture.Add(new() { Kind = LifeskillEventKind.Completed, At = At, Elapsed = TimeSpan.Zero });
        fixture.Store.Checkpoint(fixture.State);
        var session = Assert.Single(fixture.Store.Read().Sessions);
        Assert.Equal(LootTrackerSessionStatus.Completed, session.Status);
        Assert.Equal("Example character A", session.Character?.Name);
        Assert.Equal(1000, session.Character?.CombatScore);
        Assert.Equal(200, session.Character?.GearScore);
        Assert.False(session.CoverageLimited);
    }

    [Fact]
    public void PartialFinalLineIsNotCountedAndIsReportedWithoutChangingOriginalBytes()
    {
        using var fixture = new Fixture();
        fixture.Harvest(3, 0);
        File.AppendAllText(fixture.Journal, "{\"Sequence\":3,\"Kind\":1");
        var before = File.ReadAllBytes(fixture.Journal);
        var loaded = fixture.Store.Read();
        Assert.Contains("incomplete-journal-tail", loaded.Issues);
        var session = Assert.Single(loaded.Sessions);
        Assert.Equal(3, session.ItemCount);
        Assert.Equal(1, session.HarvestCount);
        Assert.True(session.CoverageLimited);
        Assert.Equal(before, File.ReadAllBytes(fixture.Journal));
    }

    [Fact]
    public void CorruptionInTheMiddleDoesNotSkipAheadAndClaimTheFollowingLoot()
    {
        using var fixture = new Fixture();
        fixture.Harvest(3, 0);
        File.AppendAllText(fixture.Journal, "not-json\n");
        fixture.Harvest(7, 10);
        var loaded = fixture.Store.Read();
        Assert.NotEmpty(loaded.Issues);
        var session = Assert.Single(loaded.Sessions);
        Assert.Equal(3, session.ItemCount);
        Assert.Equal(1, session.HarvestCount);
        Assert.True(session.CoverageLimited);
    }

    [Fact]
    public void ATruncatedUtf8TailPreservesEveryCompletedPrefixRecord()
    {
        using var fixture = new Fixture();
        fixture.Harvest(3, 0);
        using (var stream = new FileStream(fixture.Journal, FileMode.Append, FileAccess.Write))
            stream.Write([0xF0, 0x9F]); // First two bytes of an unfinished four-byte UTF-8 character.
        var loaded = fixture.Store.Read();
        Assert.Contains("incomplete-journal-tail", loaded.Issues);
        Assert.Equal(3, Assert.Single(loaded.Sessions).ItemCount);
    }

    [Fact]
    public void OversizedRecordsStopAtTheValidPrefixWithAnExplicitIssue()
    {
        using var fixture = new Fixture();
        fixture.Harvest(3, 0);
        File.AppendAllText(fixture.Journal, new string('x', LifeskillSessionStore.MaximumRecordBytes + 1) + "\n");
        var loaded = fixture.Store.Read();
        Assert.Contains("journal-record-size-limit", loaded.Issues);
        Assert.Equal(3, Assert.Single(loaded.Sessions).ItemCount);
    }

    [Fact]
    public void ARepeatedJournalSequenceCannotCountTheSameEntryTwice()
    {
        using var fixture = new Fixture();
        fixture.Harvest(3, 0);
        var lines = File.ReadAllLines(fixture.Journal);
        File.AppendAllText(fixture.Journal, lines[^1] + "\n");
        var loaded = fixture.Store.Read();
        Assert.Contains("invalid-journal-record", loaded.Issues);
        Assert.Equal(3, Assert.Single(loaded.Sessions).ItemCount);
    }

    [Fact]
    public void UnsupportedSchemaAndCheckpointAheadOfJournalAreVisible()
    {
        using var fixture = new Fixture();
        fixture.Harvest(3, 0);
        fixture.Store.Checkpoint(fixture.State);
        var path = Path.Combine(fixture.Root, fixture.Id, "summary-v1.json");
        var saved = JsonSerializer.Deserialize<LifeskillCheckpoint>(File.ReadAllText(path))!;
        File.WriteAllText(path, JsonSerializer.Serialize(saved with { ThroughSequence = 500 }));
        var loaded = fixture.Store.Read();
        Assert.Contains("invalid-session-checkpoint", loaded.Issues);
        File.AppendAllText(fixture.Journal, JsonSerializer.Serialize(new LifeskillSessionEvent
            { SchemaVersion = 2, Sequence = 3, Kind = LifeskillEventKind.Harvested }) + "\n");
        Assert.Contains("invalid-journal-record", fixture.Store.Read().Issues);
    }

    [Fact]
    public void SmallCheckpointOmitsRoutePointsAndJournalOmitsNetworkIdentifiersAndTargets()
    {
        using var fixture = new Fixture();
        fixture.Harvest(3, 0);
        for (var index = 0; index < 50; index++)
            fixture.Add(new() { Kind = LifeskillEventKind.Position, At = At.AddSeconds(index),
                Elapsed = TimeSpan.FromSeconds(index), MapId = "sample-map", MapName = "Sample map", X = index, Y = index });
        fixture.Store.Checkpoint(fixture.State);
        var checkpoint = File.ReadAllText(Path.Combine(fixture.Root, fixture.Id, "summary-v1.json"));
        Assert.DoesNotContain("Route", checkpoint);
        Assert.DoesNotContain("Points", checkpoint);
        Assert.True(checkpoint.Length < 1500);
        var journal = File.ReadAllText(fixture.Journal);
        foreach (var forbidden in new[] { "TargetKey", "EntityKey", "ServerId", "NetworkCharacterId", "Generation", "Stream", "Endpoint" })
            Assert.DoesNotContain(forbidden, journal);
    }

    [Fact]
    public void RecoveryTrimsUnharvestedTrailingRouteAndKeepsHardSegmentBoundaries()
    {
        using var fixture = new Fixture();
        fixture.Harvest(1, 0);
        fixture.Position(0);
        fixture.Add(new() { Kind = LifeskillEventKind.Gap, At = At.AddSeconds(1) });
        fixture.Harvest(1, 2);
        fixture.Position(2);
        fixture.Position(3);
        var session = Assert.Single(fixture.Store.Read().Sessions);
        Assert.Equal(2, session.RouteSegments.Length);
        Assert.Single(session.RouteSegments[0].Points);
        Assert.Single(session.RouteSegments[1].Points);
        Assert.Equal(TimeSpan.FromSeconds(2), session.RouteSegments[1].Points[0].Elapsed);
        Assert.True(session.CoverageLimited);
    }

    [Fact]
    public void StoreRejectsAPathInsteadOfTreatingItAsASessionId()
    {
        using var fixture = new Fixture();
        Assert.Throws<InvalidDataException>(() => fixture.Store.Append("../other", new()));
    }

    [Fact]
    public void SchemaThreeReplaysFrozenVisitsAndRouteWithoutAnyCatalogueLookup()
    {
        using var fixture = new Fixture(schemaVersion: 3);
        var first = new LootTrackerGatherVisit(Guid.NewGuid().ToString("N"), 0, "sample-map", "Sample map",
            9, 10, 10, 10, At, true, "Example node", 8192, 7001, "node-1");
        fixture.Add(new() { Kind = LifeskillEventKind.Harvested, At = At,
            Items = [new("item:101", "Example item", LootTrackerLootKind.Item, 2)], GatherVisit = first });
        fixture.Add(new() { Kind = LifeskillEventKind.Failed, At = At.AddSeconds(2), Elapsed = TimeSpan.FromSeconds(2),
            GatherVisit = first with { Id = Guid.NewGuid().ToString("N"), At = At.AddSeconds(2), Succeeded = false, OriginalX = 12 } });
        fixture.Add(new() { Kind = LifeskillEventKind.Gap, At = At.AddSeconds(3) });
        fixture.Add(new() { Kind = LifeskillEventKind.Failed, At = At.AddSeconds(4), Elapsed = TimeSpan.FromSeconds(4),
            GatherVisit = first with { Id = Guid.NewGuid().ToString("N"), Section = 1, At = At.AddSeconds(4), Succeeded = false } });
        fixture.Store.Checkpoint(fixture.State);
        var live = fixture.State.Snapshot();
        var saved = Assert.Single(fixture.Store.Read().Sessions);
        Assert.Equal(1, saved.FarmRouteVersion);
        Assert.Empty(saved.RouteSegments);
        Assert.Equal(3, saved.GatherVisits.Length);
        Assert.Equal(2, saved.FarmRoutePoints.Length);
        Assert.Equal(2, saved.FarmRoutePoints[0].VisitCount);
        Assert.Equal(1, saved.FarmRoutePoints[0].FailureCount);
        Assert.Equal(live.GatherVisits.ToArray(), saved.GatherVisits.ToArray());
        Assert.Equal(live.FarmRoutePoints.ToArray(), saved.FarmRoutePoints.ToArray());
        Assert.Equal(2, saved.ItemCount);
        Assert.Equal(2, saved.FailedHarvestCount);
        var checkpoint = File.ReadAllText(Path.Combine(fixture.Root, fixture.Id, "summary-v1.json"));
        Assert.DoesNotContain("GatherVisit", checkpoint);
        Assert.DoesNotContain("FarmRoute", checkpoint);
        var savedAgain = Assert.Single(fixture.Store.Read().Sessions);
        Assert.Equal(saved.GatherVisits.ToArray(), savedAgain.GatherVisits.ToArray());
    }

    [Fact]
    public void RepeatedSnapshotsReuseTheBuiltFarmRouteUntilAnotherVisitChangesIt()
    {
        using var fixture = new Fixture(schemaVersion: 3);
        var visit = new LootTrackerGatherVisit(Guid.NewGuid().ToString("N"), 0, "sample-map", "Sample map",
            10, 10, 10, 10, At, false, "Example place");
        fixture.Add(new() { Kind = LifeskillEventKind.Failed, At = At, GatherVisit = visit });
        var first = fixture.State.Snapshot();
        var second = fixture.State.Snapshot(TimeSpan.FromSeconds(5));
        Assert.True(first.FarmRoutePoints == second.FarmRoutePoints);
        fixture.Add(new() { Kind = LifeskillEventKind.Failed, At = At,
            GatherVisit = visit with { Id = Guid.NewGuid().ToString("N") } });
        var third = fixture.State.Snapshot();
        Assert.False(first.FarmRoutePoints == third.FarmRoutePoints);
        Assert.Equal(2, Assert.Single(third.FarmRoutePoints).VisitCount);
    }

    [Fact]
    public void SchemaThreeRejectsMalformedOrRepeatedVisitButAllowsUnknownPosition()
    {
        using var fixture = new Fixture(schemaVersion: 3);
        var visit = new LootTrackerGatherVisit(Guid.NewGuid().ToString("N"), 0, "sample-map", "Sample map",
            10, 10, 10, 10, At, false, "Example place");
        var attempt = new LifeskillSessionEvent { SchemaVersion = 3, Sequence = 2, Kind = LifeskillEventKind.Failed,
            At = At, GatherVisit = visit };
        Assert.True(fixture.State.CanApply(attempt));
        Assert.False(fixture.State.CanApply(attempt with { GatherVisit = visit with { Section = 1 } }));
        Assert.False(fixture.State.CanApply(attempt with { GatherVisit = visit with { Succeeded = true } }));
        Assert.False(fixture.State.CanApply(attempt with { GatherVisit = visit with { CatalogNodeId = "node-1", GatherableId = null } }));
        Assert.False(fixture.State.CanApply(attempt with { GatherVisit = visit with { X = double.NaN } }));
        Assert.False(fixture.State.CanApply(attempt with { GatherVisit = visit with { OriginalX = 9000 } }));
        Assert.False(fixture.State.CanApply(attempt with { GatherVisit = visit with { At = At.AddSeconds(1) } }));
        fixture.Add(attempt);
        Assert.False(fixture.State.CanApply(attempt with { Sequence = 3 }));
        Assert.True(fixture.State.CanApply(attempt with { Sequence = 3, GatherVisit = null }));
        Assert.False(fixture.State.CanApply(new() { SchemaVersion = 3, Sequence = 3,
            Kind = LifeskillEventKind.Position, At = At, MapId = "sample-map", X = 10, Y = 10 }));
    }

    [Fact]
    public void DeleteRenamesBeforeCleanupAndIgnoredTombstonesAreRetriedOnRead()
    {
        using var fixture = new Fixture(schemaVersion: 3);
        fixture.Failed(0);
        fixture.Store.Checkpoint(fixture.State);
        var failingCleanup = new LifeskillSessionStore(fixture.Root,
            (path, line) => File.AppendAllText(path, line), (path, value) => JsonFile.Save(path, value),
            delete: _ => throw new IOException("Synthetic cleanup failure."));
        Assert.True(failingCleanup.Delete(fixture.Id));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, fixture.Id)));
        var tombstone = Assert.Single(Directory.GetDirectories(fixture.Root));
        Assert.StartsWith(".deleted-", Path.GetFileName(tombstone));
        var pending = failingCleanup.Read();
        Assert.Empty(pending.Sessions);
        Assert.True(pending.CleanupPending);
        Assert.Empty(pending.Issues);
        var cleaned = fixture.Store.Read();
        Assert.False(cleaned.CleanupPending);
        Assert.Empty(cleaned.Sessions);
        Assert.Empty(Directory.GetDirectories(fixture.Root));
    }

    [Fact]
    public void FailedDeleteRenameLeavesJournalAndSummaryUnchanged()
    {
        using var fixture = new Fixture();
        fixture.Harvest(1, 0);
        fixture.Store.Checkpoint(fixture.State);
        var before = File.ReadAllBytes(fixture.Journal);
        var store = new LifeskillSessionStore(fixture.Root,
            (path, line) => File.AppendAllText(path, line), (path, value) => JsonFile.Save(path, value),
            move: (_, _) => throw new IOException("Synthetic rename failure."));
        Assert.Throws<IOException>(() => store.Delete(fixture.Id));
        Assert.Equal(before, File.ReadAllBytes(fixture.Journal));
        Assert.Equal(fixture.Id, Assert.Single(store.Read().Sessions).Id);
    }

    [Theory]
    [InlineData("../other")]
    [InlineData("..\\other")]
    [InlineData("11111111-1111-1111-1111-111111111111")]
    [InlineData("11111111111111111111111111111111/extra")]
    [InlineData("")]
    public void DeleteRejectsNonCanonicalIdsBeforeAnyIo(string id)
    {
        using var fixture = new Fixture();
        var calls = 0;
        var store = new LifeskillSessionStore(fixture.Root,
            (_, _) => calls++, (_, _) => calls++, (_, _) => calls++, _ => calls++);
        Assert.Throws<InvalidDataException>(() => store.Delete(id));
        Assert.Equal(0, calls);
        Assert.True(Directory.Exists(Path.Combine(fixture.Root, fixture.Id)));
    }

    [Fact]
    public void StoreRejectsUncRootsAndLeavesUnrecognizedTombstoneDirectoriesAlone()
    {
        Assert.Throws<InvalidDataException>(() => new LifeskillSessionStore(@"\\example.invalid\share\sessions"));
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".deleted-unrelated"));
        fixture.Store.Read();
        Assert.True(Directory.Exists(Path.Combine(fixture.Root, ".deleted-unrelated")));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void JournalAttemptsSupplyExactTimelineFactsWithoutRewritingTheJournal(int schemaVersion)
    {
        using var fixture = new Fixture(schemaVersion);
        fixture.Harvest(2, 10);
        if (schemaVersion >= 2) fixture.Failed(12);
        var before = File.ReadAllBytes(fixture.Journal);
        var recovered = Assert.Single(fixture.Store.Read().Sessions);
        Assert.Equal(1, recovered.TimelineVersion);
        var harvest = recovered.TimelineEvents[0];
        Assert.Equal(LootTrackerTimelineEventKind.HarvestSuccess, harvest.Kind);
        Assert.Equal(TimeSpan.FromSeconds(10), harvest.Elapsed); Assert.Equal(At.AddSeconds(10), harvest.At);
        Assert.Equal(2, Assert.Single(harvest.Loot).Quantity); Assert.Null(harvest.GatherVisit);
        if (schemaVersion >= 2) Assert.Equal(LootTrackerTimelineEventKind.HarvestFailure, recovered.TimelineEvents[1].Kind);
        Assert.Equal(before, File.ReadAllBytes(fixture.Journal));
        Assert.Equal(recovered.TimelineEvents.Select(row => row.Id), Assert.Single(fixture.Store.Read().Sessions).TimelineEvents.Select(row => row.Id));
    }

    [Fact]
    public void SchemaFourMovementBoundariesDoNotAlterTheSeparateFarmRouteSections()
    {
        using var fixture = new Fixture(4);
        fixture.Add(new() { Kind = LifeskillEventKind.Position, At = At, X = 10, Y = 10,
            MapId = "sample-map", StartsMovementSegment = true });
        fixture.Harvest(1, 0);
        fixture.Add(new() { Kind = LifeskillEventKind.Position, At = At.AddSeconds(5), Elapsed = TimeSpan.FromSeconds(5),
            X = 20, Y = 20, MapId = "sample-map", StartsMovementSegment = true });
        fixture.Failed(5);
        fixture.Position(6);
        var recovered = Assert.Single(fixture.Store.Read().Sessions);
        Assert.Equal(2, recovered.RouteSegments.Length);
        Assert.All(recovered.RouteSegments, segment => Assert.Single(segment.Points));
        Assert.Equal(TimeSpan.FromSeconds(5), recovered.Elapsed);
        Assert.Equal(0, fixture.State.RouteSection); Assert.Empty(recovered.FarmRoutePoints);
        Assert.Equal(2, recovered.TimelineEvents.Length);
    }

    [Fact]
    public void SchemaFourJournalGapSeparatesMovementEvenWithoutAnExplicitNextSegmentFlag()
    {
        using var fixture = new Fixture(4);
        fixture.Position(0); fixture.Harvest(1, 0);
        fixture.Add(new() { Kind = LifeskillEventKind.Gap, At = At.AddSeconds(1), Elapsed = TimeSpan.FromSeconds(1) });
        fixture.Position(2); fixture.Harvest(1, 2);
        var recovered = Assert.Single(fixture.Store.Read().Sessions);
        Assert.Equal(2, recovered.RouteSegments.Length);
        Assert.All(recovered.RouteSegments, segment => Assert.Single(segment.Points));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "soulcrest-lifeskill-store-" + Guid.NewGuid().ToString("N"));
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string Journal => Path.Combine(Root, Id, "events-0001.jsonl");
        public LifeskillSessionStore Store { get; }
        public LifeskillSessionState State { get; }
        public Fixture(int schemaVersion = 1)
        {
            Store = new(Root); State = new(Id);
            Add(new() { SchemaVersion = schemaVersion, Kind = LifeskillEventKind.Started, At = At,
                Character = new("11111111111111111111111111111111", "Example character A", 1000, 200) });
        }
        public void Failed(int second) => Add(new() { Kind = LifeskillEventKind.Failed,
            At = At.AddSeconds(second), Elapsed = TimeSpan.FromSeconds(second) });
        public void Harvest(long quantity, int second) => Add(new() { Kind = LifeskillEventKind.Harvested,
            At = At.AddSeconds(second), Elapsed = TimeSpan.FromSeconds(second),
            Items = [new("item:101", "Example item", LootTrackerLootKind.Item, quantity)] });
        public void Position(int second) => Add(new() { Kind = LifeskillEventKind.Position,
            At = At.AddSeconds(second), Elapsed = TimeSpan.FromSeconds(second),
            MapId = "sample-map", MapName = "Sample map", X = second, Y = second });
        public void Add(LifeskillSessionEvent entry)
        {
            var next = entry with { Sequence = State.Sequence + 1,
                SchemaVersion = State.Started ? State.SchemaVersion : entry.SchemaVersion };
            Assert.True(State.TryApply(next));
            Store.Append(Id, next);
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    }
}
