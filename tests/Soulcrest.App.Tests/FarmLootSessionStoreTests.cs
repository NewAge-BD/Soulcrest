using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class FarmLootSessionStoreTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 26, 8, 0, 0, TimeSpan.Zero);
    private const string ProfileA = "11111111111111111111111111111111";
    private const string ProfileB = "22222222222222222222222222222222";

    [Fact]
    public void MissingStoreIsEmptyWithoutCreatingAnyFiles()
    {
        using var fixture = new Fixture();
        var read = fixture.Store.Read();
        Assert.Empty(read.Sessions); Assert.Empty(read.Issues); Assert.False(read.CleanupPending);
        Assert.False(Directory.Exists(fixture.Root));
    }

    [Fact]
    public void RestartRecoversAbsoluteTotalsOnceAndPreservesFrozenPresentationValues()
    {
        using var fixture = new Fixture();
        var first = Session(fixture.Id, 0, 80);
        fixture.Store.Append(first);
        var final = Session(fixture.Id, 30, 150) with
        {
            Status = LootTrackerSessionStatus.Completed, EndedAt = At.AddSeconds(30),
            MonsterCount = 3, SoulCount = 2, ItemCount = 4,
            Loot = [new("item:101", "Synthetic item", LootTrackerLootKind.Item, 4),
                new("soul:202", "Synthetic soul", LootTrackerLootKind.Soul, 2)],
            Mobs = [new("mob:303", "Synthetic monster", 3)],
            RouteSegments = [new("synthetic-map", "Synthetic location", [new(1, 2, At, TimeSpan.Zero), new(3, 4, At.AddSeconds(30), TimeSpan.FromSeconds(30))])],
            MinuteIncome = [new(0, null, 150)],
        };
        fixture.Store.Append(final);
        var read = new FarmLootSessionStore(fixture.Root).Read();
        Assert.Empty(read.Issues);
        var loaded = Assert.Single(read.Sessions);
        Assert.Equal(JsonSerializer.Serialize(final), JsonSerializer.Serialize(loaded));
        Assert.Equal(150, loaded.KinaUnbound);
        Assert.Equal(2, File.ReadAllLines(fixture.Journal).Length);
        Assert.Equal(JsonSerializer.Serialize(loaded), JsonSerializer.Serialize(Assert.Single(fixture.Store.Read().Sessions)));
        using var checkpoint = JsonDocument.Parse(File.ReadAllBytes(fixture.Checkpoint));
        Assert.Equal(2, checkpoint.RootElement.GetProperty("ThroughSequence").GetInt64());
        Assert.False(checkpoint.RootElement.TryGetProperty("Loot", out _));
        Assert.False(checkpoint.RootElement.TryGetProperty("RouteSegments", out _));
    }

    [Fact]
    public void LiveRecoveryEndsAtLastIncomeAndDiscardsUnobservedTailTimeAndRoute()
    {
        using var fixture = new Fixture();
        fixture.Store.Append(Session(fixture.Id, 20, 150) with
        {
            Elapsed = TimeSpan.FromMinutes(3),
            RouteSegments = [new("synthetic-map", "Synthetic location", [new(1, 2, At), new(2, 3, At.AddSeconds(20)), new(9, 9, At.AddMinutes(2))])],
        });
        var recovered = Assert.Single(new FarmLootSessionStore(fixture.Root).Read().Sessions);
        Assert.Equal(LootTrackerSessionStatus.Interrupted, recovered.Status);
        Assert.Equal(At.AddSeconds(20), recovered.EndedAt);
        Assert.Equal(TimeSpan.FromSeconds(20), recovered.Elapsed);
        Assert.Equal(2, Assert.Single(recovered.RouteSegments).Points.Length);
        Assert.False(recovered.CoverageLimited);
    }

    [Fact]
    public void RouteTailTrimRetainsRecordedPartsAndRemovesLaterMovementWithoutBridgingSegments()
    {
        using var fixture = new Fixture();
        var session = Session(fixture.Id, 10, 100) with
        {
            RouteSegments = [new("map-a", "A", [new(1, 2, At, TimeSpan.Zero), new(2, 3, At.AddSeconds(5), TimeSpan.FromSeconds(5))]),
                new("map-a", "A", [new(7, 8, At.AddSeconds(10), TimeSpan.FromSeconds(10)), new(8, 9, At.AddSeconds(12), TimeSpan.FromSeconds(12))]),
                new("map-b", "B", [new(9, 10, At.AddSeconds(20), TimeSpan.FromSeconds(20))])]
        };
        fixture.Store.Append(session);
        var recovered = Assert.Single(new FarmLootSessionStore(fixture.Root).Read().Sessions);
        Assert.Equal(2, recovered.RouteSegments.Length);
        Assert.Equal(2, recovered.RouteSegments[0].Points.Length); Assert.Single(recovered.RouteSegments[1].Points);
        var export = Assert.Single(LootTrackerRouteSaveService.Preview(recovered));
        Assert.Equal([1d, 2d], export.Stops.Select(point => point.X).ToArray());
        Assert.Throws<InvalidDataException>(() => fixture.Store.Append(session with
        { Status = LootTrackerSessionStatus.Completed, EndedAt = session.LastLootAt }));
        var finished = session with { Status = LootTrackerSessionStatus.Completed, EndedAt = session.LastLootAt,
            RouteSegments = FarmLootSessionStore.TrimRoute(session, session.LastLootAt!.Value, session.Elapsed) };
        fixture.Store.Append(finished);
        Assert.Equal(2, Assert.Single(new FarmLootSessionStore(fixture.Root).Read().Sessions).RouteSegments.Length);
    }

    [Fact]
    public void RouteJournalAppendsSmallDeltasAndReconstructsEveryPositionInOriginalOrder()
    {
        using var fixture = new Fixture();
        var points = ImmutableArray.CreateBuilder<LootTrackerRoutePoint>();
        for (var second = 0; second < 200; second++)
        {
            points.Add(new(second, second + 1, At.AddSeconds(second), TimeSpan.FromSeconds(second)));
            fixture.Store.Append(Session(fixture.Id, second, second + 1) with
            { RouteSegments = [new("map-a", "A", points.ToImmutable())] });
        }
        var lines = File.ReadAllLines(fixture.Journal);
        Assert.Equal(200, lines.Length);
        Assert.All(lines, line => Assert.True(Encoding.UTF8.GetByteCount(line) < 3000));
        Assert.True(new FileInfo(fixture.Journal).Length < 512 * 1024);
        using var last = JsonDocument.Parse(lines[^1]);
        Assert.Equal(2, last.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.Empty(last.RootElement.GetProperty("Snapshot").GetProperty("RouteSegments").EnumerateArray());
        Assert.Single(last.RootElement.GetProperty("RouteDelta").GetProperty("LastSegmentPoints").EnumerateArray());
        var recovered = Assert.Single(new FarmLootSessionStore(fixture.Root).Read().Sessions);
        Assert.Equal(points.ToArray(), Assert.Single(recovered.RouteSegments).Points.ToArray());
    }

    [Fact]
    public void LegacyAbsoluteRoutesCanContinueWithValidatedNewDeltas()
    {
        using var fixture = new Fixture();
        var first = Session(fixture.Id, 0, 10) with
        { RouteSegments = [new("map-a", "A", [new(1, 2, At, TimeSpan.Zero)])] };
        fixture.Store.Append(first);
        File.WriteAllText(fixture.Journal, JsonSerializer.Serialize(new { SchemaVersion = 1, Sequence = 1L, Snapshot = first }) + "\n");
        var next = Session(fixture.Id, 10, 20) with { RouteSegments = [first.RouteSegments[0] with
        { Points = first.RouteSegments[0].Points.Add(new(3, 4, At.AddSeconds(10), TimeSpan.FromSeconds(10))) }] };
        var store = new FarmLootSessionStore(fixture.Root); store.Append(next);
        var read = new FarmLootSessionStore(fixture.Root).Read(); Assert.Empty(read.Issues);
        Assert.Equal([1d, 3d], Assert.Single(Assert.Single(read.Sessions).RouteSegments).Points.Select(point => point.X).ToArray());
        Assert.Throws<InvalidDataException>(() => store.Append(next with { RouteSegments = [next.RouteSegments[0] with
        { Points = [new(99, 99, At, TimeSpan.Zero)] }] }));
    }

    [Fact]
    public void CorruptDeltaCannotSkipItsPrefixOrRewriteEarlierRoutePoints()
    {
        using var fixture = new Fixture();
        var first = Session(fixture.Id, 0, 10) with { RouteSegments = [new("map-a", "A", [new(1, 2, At, TimeSpan.Zero)])] };
        fixture.Store.Append(first);
        fixture.Store.Append(first with { KinaUnbound = 20 });
        var lines = File.ReadAllLines(fixture.Journal);
        lines[1] = lines[1].Replace("\"ExistingSegments\":1", "\"ExistingSegments\":2", StringComparison.Ordinal);
        File.WriteAllLines(fixture.Journal, lines);
        var read = new FarmLootSessionStore(fixture.Root).Read();
        Assert.Contains("invalid-farm-journal-record", read.Issues);
        Assert.Equal(10, Assert.Single(read.Sessions).KinaUnbound);
        Assert.Equal(1, Assert.Single(Assert.Single(read.Sessions).RouteSegments).Points[0].X);
    }

    [Fact]
    public void LimitedModeAndFarmDayPersistInJournalAndSummaryAcrossRestart()
    {
        using var fixture = new Fixture();
        var session = Session(fixture.Id, 20, 0) with
        { Mode = LootTrackerSessionMode.LimitedKina, FarmDay = LootFarmDay.Get(At), SoulCount = 1,
            Loot = [new("SoulCrystal:2", "Synthetic crystal", LootTrackerLootKind.Soul, 1)] };
        fixture.Store.Append(session);
        var recovered = Assert.Single(new FarmLootSessionStore(fixture.Root).Read().Sessions);
        Assert.Equal(session.Mode, recovered.Mode); Assert.Equal(session.FarmDay, recovered.FarmDay);
        Assert.Equal(LootTrackerSessionStatus.Interrupted, recovered.Status); Assert.Equal(1, recovered.SoulCount);
        using var checkpoint = JsonDocument.Parse(File.ReadAllBytes(fixture.Checkpoint));
        Assert.Equal((int)LootTrackerSessionMode.LimitedKina, checkpoint.RootElement.GetProperty("Mode").GetInt32());
        Assert.Equal(session.FarmDay!.Value.ToString("yyyy-MM-dd"), checkpoint.RootElement.GetProperty("FarmDay").GetString());
    }

    [Fact]
    public void OlderJournalWithoutModeAndFarmDayRemainsReadable()
    {
        using var fixture = new Fixture(); fixture.Store.Append(Session(fixture.Id, 0, 10));
        File.WriteAllText(fixture.Journal, (JsonSerializer.Serialize(new { SchemaVersion = 1, Sequence = 1L, Snapshot = Session(fixture.Id, 0, 10) }) + "\n")
            .Replace("\"Mode\":0,", "", StringComparison.Ordinal)
            .Replace("\"FarmDay\":null,", "", StringComparison.Ordinal));
        var read = new FarmLootSessionStore(fixture.Root).Read();
        Assert.Empty(read.Issues); var recovered = Assert.Single(read.Sessions);
        Assert.Equal(LootTrackerSessionMode.Normal, recovered.Mode); Assert.Null(recovered.FarmDay);
    }


    [Fact]
    public void EmptyDungeonRecoveryDoesNotInventLootOrAnElapsedRun()
    {
        using var fixture = new Fixture();
        fixture.Store.Append(Session(fixture.Id, 0, null) with { Kind = LootTrackerSessionKind.Dungeon,
            DungeonName = "Synthetic dungeon", LastLootAt = null, Elapsed = TimeSpan.FromMinutes(2) });
        var session = Assert.Single(fixture.Store.Read().Sessions);
        Assert.Equal(At, session.EndedAt); Assert.Equal(TimeSpan.Zero, session.Elapsed);
        Assert.Null(session.LastLootAt); Assert.Null(session.KinaUnbound);
    }

    [Fact]
    public void TwoProfilesAndBothSessionKindsSurviveRestartIndependently()
    {
        using var fixture = new Fixture();
        fixture.Store.Append(Session(fixture.Id, 0, 10));
        var second = Session(Guid.NewGuid().ToString("N"), 0, 20) with
        { Kind = LootTrackerSessionKind.Dungeon, Character = new(ProfileB, "Synthetic character B", 200, 50), StartedAt = At.AddDays(1), LastLootAt = At.AddDays(1) };
        fixture.Store.Append(second);
        var read = new FarmLootSessionStore(fixture.Root).Read();
        Assert.Empty(read.Issues); Assert.Equal(2, read.Sessions.Count);
        Assert.Equal(second.Id, read.Sessions[0].Id);
        Assert.Equal(ProfileA, read.Sessions[1].Character?.ProfileId);
        Assert.Equal(ProfileB, read.Sessions[0].Character?.ProfileId);
    }

    [Fact]
    public void CheckpointWriteFailureKeepsTheJournalCommitAndNextSequence()
    {
        using var fixture = new Fixture();
        var fail = false;
        var store = new FarmLootSessionStore(fixture.Root, DurableAppend, (path, value) =>
        { if (fail) throw new IOException("Synthetic checkpoint failure."); JsonFile.Save(path, value); });
        store.Append(Session(fixture.Id, 0, 10));
        var old = File.ReadAllBytes(fixture.Checkpoint); fail = true;
        Assert.Throws<IOException>(() => store.Append(Session(fixture.Id, 10, 20)));
        Assert.Equal(old, File.ReadAllBytes(fixture.Checkpoint));
        var read = new FarmLootSessionStore(fixture.Root).Read();
        Assert.Empty(read.Issues); Assert.Equal(20, Assert.Single(read.Sessions).KinaUnbound);
        fail = false; store.Append(Session(fixture.Id, 20, 30));
        Assert.Equal(3, File.ReadAllLines(fixture.Journal).Length);
        Assert.Equal(30, Assert.Single(store.Read().Sessions).KinaUnbound);
    }

    [Fact]
    public void UncertainPartialAppendStopsFurtherWritesButKeepsValidPrefixRecoverable()
    {
        using var fixture = new Fixture();
        fixture.Store.Append(Session(fixture.Id, 0, 10));
        var store = new FarmLootSessionStore(fixture.Root, (path, line) =>
        { File.AppendAllText(path, line[..(line.Length / 2)]); throw new IOException("Synthetic append failure."); }, JsonFile.Save);
        Assert.Throws<IOException>(() => store.Append(Session(fixture.Id, 10, 20)));
        Assert.Throws<IOException>(() => store.Append(Session(fixture.Id, 20, 30)));
        var read = new FarmLootSessionStore(fixture.Root).Read();
        Assert.Contains("incomplete-farm-journal-tail", read.Issues);
        Assert.Equal(10, Assert.Single(read.Sessions).KinaUnbound);
        Assert.True(Assert.Single(read.Sessions).CoverageLimited);
        Assert.Throws<InvalidDataException>(() => new FarmLootSessionStore(fixture.Root).Append(Session(fixture.Id, 30, 40)));
    }

    [Theory]
    [InlineData("not-json\n", "invalid-farm-journal-record")]
    [InlineData("{\"SchemaVersion\":999}\n", "invalid-farm-journal-record")]
    [InlineData("{\"SchemaVersion\":1", "incomplete-farm-journal-tail")]
    public void DamagedJournalTailNeverSkipsForwardOrCountsCheckpointTotals(string tail, string issue)
    {
        using var fixture = new Fixture();
        fixture.Store.Append(Session(fixture.Id, 0, 10));
        var original = File.ReadAllBytes(fixture.Journal);
        File.AppendAllText(fixture.Journal, tail);
        var bytes = File.ReadAllBytes(fixture.Journal);
        var read = fixture.Store.Read();
        Assert.Contains(issue, read.Issues); Assert.Equal(10, Assert.Single(read.Sessions).KinaUnbound);
        Assert.True(Assert.Single(read.Sessions).CoverageLimited); Assert.Equal(bytes, File.ReadAllBytes(fixture.Journal));
        Assert.True(bytes.Length > original.Length);
    }

    [Fact]
    public void CorruptionBeforeLaterValidAbsoluteStateKeepsOnlyTheContiguousPrefix()
    {
        using var fixture = new Fixture();
        fixture.Store.Append(Session(fixture.Id, 0, 10));
        File.AppendAllText(fixture.Journal, "not-json\n");
        fixture.Store.Append(Session(fixture.Id, 10, 100));
        var read = fixture.Store.Read();
        Assert.Equal(10, Assert.Single(read.Sessions).KinaUnbound); Assert.NotEmpty(read.Issues);
    }

    [Fact]
    public void DuplicateSequenceAndDuplicateJsonPropertyAreRejectedWithoutDoubleCounting()
    {
        using var duplicate = new Fixture();
        duplicate.Store.Append(Session(duplicate.Id, 0, 10));
        var line = File.ReadAllLines(duplicate.Journal)[0];
        File.AppendAllText(duplicate.Journal, line + "\n");
        Assert.Contains("invalid-farm-journal-record", duplicate.Store.Read().Issues);
        Assert.Equal(10, Assert.Single(duplicate.Store.Read().Sessions).KinaUnbound);
        using var property = new Fixture();
        property.Store.Append(Session(property.Id, 0, 10));
        File.WriteAllText(property.Journal, File.ReadAllText(property.Journal).Replace("\"SchemaVersion\":2", "\"SchemaVersion\":2,\"SchemaVersion\":2", StringComparison.Ordinal));
        Assert.Empty(property.Store.Read().Sessions); Assert.NotEmpty(property.Store.Read().Issues);
    }

    [Fact]
    public void TruncatedUtf8TailKeepsAllCompletedPrefixRecords()
    {
        using var fixture = new Fixture();
        fixture.Store.Append(Session(fixture.Id, 0, 10));
        using (var stream = new FileStream(fixture.Journal, FileMode.Append, FileAccess.Write)) stream.Write([0xF0, 0x9F]);
        var read = fixture.Store.Read();
        Assert.Contains("incomplete-farm-journal-tail", read.Issues); Assert.Equal(10, Assert.Single(read.Sessions).KinaUnbound);
    }

    [Fact]
    public void RecordLimitRejectsOversizeWriteAndOversizeTailKeepsPrefix()
    {
        using var fixture = new Fixture();
        fixture.Store.Append(Session(fixture.Id, 0, 10));
        var huge = Session(fixture.Id, 10, 20) with { Loot = Enumerable.Range(0, 2000)
            .Select(i => new LootTrackerLoot("item:" + i, new string('x', 1024), LootTrackerLootKind.Item, 1)).ToImmutableArray() };
        Assert.Throws<InvalidDataException>(() => fixture.Store.Append(huge));
        Assert.Single(File.ReadAllLines(fixture.Journal));
        File.AppendAllText(fixture.Journal, new string('x', FarmLootSessionStore.MaximumRecordBytes) + "\n");
        Assert.Contains("farm-journal-record-size-limit", fixture.Store.Read().Issues);
        Assert.Equal(10, Assert.Single(fixture.Store.Read().Sessions).KinaUnbound);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"SchemaVersion\":999}")]
    [InlineData("{\"SchemaVersion\":1,\"SessionId\":\"other\",\"ThroughSequence\":200}")]
    public void InvalidCheckpointCannotReplaceTheAuthoritativeJournal(string json)
    {
        using var fixture = new Fixture();
        fixture.Store.Append(Session(fixture.Id, 0, 10)); File.WriteAllText(fixture.Checkpoint, json);
        var read = fixture.Store.Read();
        Assert.Contains("invalid-farm-session-checkpoint", read.Issues); Assert.Equal(10, Assert.Single(read.Sessions).KinaUnbound);
    }

    [Fact]
    public void CheckpointAloneCannotInventARecoveredSession()
    {
        using var fixture = new Fixture();
        fixture.Store.Append(Session(fixture.Id, 0, 10)); File.Delete(fixture.Journal);
        Assert.Empty(new FarmLootSessionStore(fixture.Root).Read().Sessions);
    }

    [Fact]
    public void DeletingCompletedAndLiveSessionsRemovesTheirFilesAndDoesNotTouchSavedMapRoutes()
    {
        using var fixture = new Fixture();
        var saved = Path.Combine(Path.GetDirectoryName(fixture.Root)!, "saved-map-routes.json"); File.WriteAllText(saved, "synthetic route");
        fixture.Store.Append(Session(fixture.Id, 0, 10));
        var otherId = Guid.NewGuid().ToString("N");
        fixture.Store.Append(Session(otherId, 10, 20) with { Status = LootTrackerSessionStatus.Completed, EndedAt = At.AddSeconds(10) });
        Assert.False(fixture.Store.Delete(fixture.Id)); Assert.False(fixture.Store.Delete(otherId));
        Assert.Empty(new FarmLootSessionStore(fixture.Root).Read().Sessions);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Root)); Assert.Equal("synthetic route", File.ReadAllText(saved));
        Assert.Throws<InvalidOperationException>(() => fixture.Store.Append(Session(fixture.Id, 20, 30)));
        Assert.False(fixture.Store.Delete(fixture.Id));
    }

    [Fact]
    public void RenameFailureKeepsSessionVisibleAndAllowsFutureEvents()
    {
        using var fixture = new Fixture(); fixture.Store.Append(Session(fixture.Id, 0, 10));
        var store = new FarmLootSessionStore(fixture.Root, DurableAppend, JsonFile.Save,
            move: (_, _) => throw new IOException("Synthetic rename failure."));
        Assert.Throws<IOException>(() => store.Delete(fixture.Id));
        Assert.Equal(10, Assert.Single(store.Read().Sessions).KinaUnbound);
        store.Append(Session(fixture.Id, 10, 20)); Assert.Equal(20, Assert.Single(store.Read().Sessions).KinaUnbound);
    }

    [Fact]
    public void CleanupFailureAfterRenameCannotResurrectSessionAndRestartCleansTombstone()
    {
        using var fixture = new Fixture(); fixture.Store.Append(Session(fixture.Id, 0, 10));
        var store = new FarmLootSessionStore(fixture.Root, DurableAppend, JsonFile.Save,
            delete: _ => throw new IOException("Synthetic cleanup failure."));
        Assert.True(store.Delete(fixture.Id)); Assert.False(Directory.Exists(Path.GetDirectoryName(fixture.Journal)));
        var read = store.Read(); Assert.Empty(read.Sessions); Assert.True(read.CleanupPending);
        Assert.Throws<InvalidOperationException>(() => store.Append(Session(fixture.Id, 10, 20)));
        var clean = new FarmLootSessionStore(fixture.Root).Read();
        Assert.Empty(clean.Sessions); Assert.False(clean.CleanupPending); Assert.Empty(Directory.EnumerateDirectories(fixture.Root));
    }

    [Fact]
    public async Task ConcurrentAppendAndDeleteAreSerializedAndNeverRecreateDeletedSession()
    {
        using var fixture = new Fixture(); fixture.Store.Append(Session(fixture.Id, 0, 10));
        var append = Task.Run(() => Record.Exception(() => fixture.Store.Append(Session(fixture.Id, 10, 20))));
        var delete = Task.Run(() => fixture.Store.Delete(fixture.Id));
        await Task.WhenAll(append, delete);
        var appendError = await append;
        Assert.True(appendError is null or InvalidOperationException);
        Assert.Empty(new FarmLootSessionStore(fixture.Root).Read().Sessions);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("empty-id")]
    [InlineData("profile")]
    [InlineData("kind")]
    [InlineData("negative")]
    [InlineData("route")]
    [InlineData("elapsed")]
    [InlineData("status")]
    [InlineData("default-array")]
    [InlineData("gathering")]
    [InlineData("mode")]
    [InlineData("farm-day")]
    public void InvalidModelsFailBeforeCreatingFiles(string variant)
    {
        using var fixture = new Fixture();
        var snapshot = Session(fixture.Id, 0, 10);
        snapshot = variant switch
        {
            "id" => snapshot with { Id = "../escape" },
            "empty-id" => snapshot with { Id = Guid.Empty.ToString("N") },
            "profile" => snapshot with { Character = new("native-identity", "Synthetic character") },
            "kind" => snapshot with { Kind = LootTrackerSessionKind.Lifeskill },
            "negative" => snapshot with { KinaUnbound = -1 },
            "route" => snapshot with { RouteSegments = [new("synthetic", "Synthetic", [new(double.NaN, 0)])] },
            "elapsed" => snapshot with { Elapsed = TimeSpan.FromSeconds(-1) },
            "status" => snapshot with { Status = LootTrackerSessionStatus.Completed },
            "default-array" => snapshot with { Loot = default },
            "mode" => snapshot with { Mode = (LootTrackerSessionMode)123 },
            "farm-day" => snapshot with { FarmDay = LootFarmDay.Get(At).AddDays(1) },
            _ => snapshot with { FarmRouteVersion = 1 },
        };
        Assert.Throws<InvalidDataException>(() => fixture.Store.Append(snapshot)); Assert.False(Directory.Exists(fixture.Root));
    }

    [Theory]
    [InlineData("character")]
    [InlineData("started")]
    [InlineData("elapsed")]
    [InlineData("kind")]
    [InlineData("mode")]
    [InlineData("farm-day")]
    public void HistoryCannotRewriteFrozenMetadataOrTime(string variant)
    {
        using var fixture = new Fixture(); var first = Session(fixture.Id, 10, 10); fixture.Store.Append(first);
        var next = Session(fixture.Id, 20, 20);
        next = variant switch
        {
            "character" => next with { Character = new(ProfileB, "Synthetic character B") },
            "started" => next with { StartedAt = At.AddSeconds(1) },
            "elapsed" => next with { Elapsed = TimeSpan.Zero },
            "mode" => next with { Mode = LootTrackerSessionMode.LimitedKina },
            "farm-day" => next with { FarmDay = LootFarmDay.Get(At) },
            _ => next with { Kind = LootTrackerSessionKind.Dungeon },
        };
        Assert.Throws<InvalidDataException>(() => fixture.Store.Append(next)); Assert.Single(File.ReadAllLines(fixture.Journal));
    }

    [Fact]
    public void CompletedHistoryCannotBeResumedAndRejectedGuidCannotDeleteParent()
    {
        using var fixture = new Fixture();
        fixture.Store.Append(Session(fixture.Id, 10, 10) with { Status = LootTrackerSessionStatus.Completed, EndedAt = At.AddSeconds(10) });
        Assert.Throws<InvalidDataException>(() => fixture.Store.Append(Session(fixture.Id, 20, 20)));
        Assert.Throws<InvalidDataException>(() => fixture.Store.Delete("..")); Assert.Single(fixture.Store.Read().Sessions);
    }

    [Fact]
    public void StoreRefusesNetworkAndAlternateStreamPaths()
    {
        Assert.Throws<InvalidDataException>(() => new FarmLootSessionStore(@"\\synthetic.invalid\share\sessions"));
        using var fixture = new Fixture();
        Assert.Throws<InvalidDataException>(() => new FarmLootSessionStore(fixture.Root + ":stream"));
    }

    private static LootTrackerSession Session(string id, int seconds, long? kina) => new()
    {
        Id = id, Kind = LootTrackerSessionKind.Grind, Status = LootTrackerSessionStatus.Live,
        Location = "Synthetic location", StartedAt = At, LastLootAt = At.AddSeconds(seconds),
        Elapsed = TimeSpan.FromSeconds(seconds), Character = new(ProfileA, "Synthetic character A", 1000, 200),
        KinaUnbound = kina, KinaBound = null, MonsterCount = null, ItemCount = null, SoulCount = null,
    };

    private static void DurableAppend(string path, string line)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        stream.Write(Encoding.UTF8.GetBytes(line)); stream.Flush(flushToDisk: true);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _temporary = Path.Combine(Path.GetTempPath(), "Soulcrest-FarmStore-" + Guid.NewGuid().ToString("N"));
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string Root { get; }
        public string Journal => Path.Combine(Root, Id, "events-0001.jsonl");
        public string Checkpoint => Path.Combine(Root, Id, "summary-v1.json");
        public FarmLootSessionStore Store { get; }
        public Fixture() { Directory.CreateDirectory(_temporary); Root = Path.Combine(_temporary, "farm-sessions"); Store = new(Root); }
        public void Dispose() { if (Directory.Exists(_temporary)) Directory.Delete(_temporary, recursive: true); }
    }
}
