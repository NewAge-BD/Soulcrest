using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed partial class UnifiedLootTrackerServiceTests
{
    [Fact]
    public async Task UtcJitterTailStaysProvisionalWhileHarvestPauseResumeAndRecoveryRemainDurable()
    {
        await using var f = new Fixture();
        f.WorldOffset = TimeSpan.FromSeconds(-1);
        f.Position = () => new(new("sample-map", 10, 20, f.Clock.GetUtcNow()) { ContextGeneration = 1 },
            1, f.WorldGeneration, f.Source.OwnCharacter!.Generation, f.Clock.GetUtcNow(), 30, 1);
        await f.Ready(); f.Source.Drop(7, 1, 1); await f.Ready();
        var jitter = TimeSpan.FromTicks(277_000);
        f.Clock.Advance(TimeSpan.FromSeconds(10) + jitter); await f.Ready();
        var factAt = f.Clock.GetUtcNow() - jitter;
        typeof(UnifiedLootTrackerService).GetMethod("Gathered", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(f.Service,
            [new NetworkGathering(factAt, 1, f.Source.OwnCharacter!, 123, [new(610620013, 2)])]);
        await f.Ready();
        var live = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(3, live.ItemCount); Assert.Equal(1, live.HarvestCount);
        Assert.Contains(live.RouteSegments.SelectMany(segment => segment.Points), point => point.At > factAt);
        var stored = Assert.Single(new UnifiedLootSessionStore(f.SessionRoot).Read().Sessions);
        Assert.All(stored.RouteSegments.SelectMany(segment => segment.Points), point => Assert.True(point.At <= factAt));
        Assert.Equal(3, stored.ItemCount);

        f.Clock.Advance(TimeSpan.FromSeconds(20)); await f.Ready();
        Assert.True(Assert.Single(f.Service.Snapshot.Sessions).IsPaused);
        Assert.Null(f.Service.RecordingFault);
        f.Clock.Advance(TimeSpan.FromSeconds(5)); f.Source.Harvest(2, new NetworkGatheringItem(610620013, 1)); await f.Ready();
        var resumed = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(live.Id, resumed.Id); Assert.False(resumed.IsPaused);
        Assert.Equal(4, resumed.ItemCount); Assert.Equal(2, resumed.HarvestCount);
        Assert.False(f.Service.StatusIsError);
        var recovered = Assert.Single(new UnifiedLootSessionStore(f.SessionRoot).Read().Sessions);
        Assert.Equal(resumed.ItemCount, recovered.ItemCount);
        Assert.Equal(resumed.HarvestCount, recovered.HarvestCount);
        Assert.Equal(resumed.TimelineEvents.Select(row => (row.Id, row.Kind, row.At, row.Elapsed)),
            recovered.TimelineEvents.Select(row => (row.Id, row.Kind, row.At, row.Elapsed)));
    }

    [Fact]
    public async Task QueuedElapsedJitterCannotCommitWalkingPastHarvestOrBreakTheFollowingFailedAttempt()
    {
        await using var f = new Fixture();
        f.WorldOffset = TimeSpan.FromSeconds(-1);
        f.Position = () => new(new("sample-map", 10, 20, f.Clock.GetUtcNow()) { ContextGeneration = 1 },
            1, f.WorldGeneration, f.Source.OwnCharacter!.Generation, f.Clock.GetUtcNow(), 30, 1);
        await f.Ready(); f.Source.Drop(7, 1, 1); await f.Ready();
        f.Clock.Advance(TimeSpan.FromSeconds(10), fireTimers: false);
        f.BeforeBind = () => f.Clock.Advance(TimeSpan.FromTicks(152_074));
        f.Source.Harvest(1, new NetworkGatheringItem(610620013, 2)); await f.Ready();
        var live = Assert.Single(f.Service.Snapshot.Sessions);
        var fact = Assert.Single(live.TimelineEvents, row => row.Kind == LootTrackerTimelineEventKind.HarvestSuccess);
        Assert.Contains(live.RouteSegments.SelectMany(segment => segment.Points), point => point.Elapsed > fact.Elapsed);
        var stored = Assert.Single(new UnifiedLootSessionStore(f.SessionRoot).Read().Sessions);
        Assert.All(stored.RouteSegments.SelectMany(segment => segment.Points), point => Assert.True(point.Elapsed <= fact.Elapsed));
        f.Clock.Advance(TimeSpan.FromSeconds(1), fireTimers: false);
        f.Source.Failed(2); await f.Ready();
        var failed = Assert.Single(f.Service.Snapshot.Sessions);
        Assert.Equal(3, failed.ItemCount); Assert.Equal(1, failed.HarvestCount); Assert.Equal(1, failed.FailedHarvestCount);
        Assert.Null(f.Service.RecordingFault); Assert.False(f.Service.StatusIsError);
        var recovered = Assert.Single(new UnifiedLootSessionStore(f.SessionRoot).Read().Sessions);
        // The live publication includes the current unpaused grace interval. Recovery owns only
        // the committed productive clock, which a failed attempt must not advance.
        Assert.Equal(fact.Elapsed, recovered.Elapsed); Assert.Equal(1, recovered.FailedHarvestCount);
        Assert.Equal(fact.Elapsed, Assert.Single(recovered.TimelineEvents,
            row => row.Kind == LootTrackerTimelineEventKind.HarvestFailure).Elapsed);
        Assert.Equal(3, recovered.ItemCount);
    }

    [Fact]
    public async Task AnOverflowHasItsOwnFirstFaultAndCannotBeMisreportedAsDiskFailure()
    {
        await using var f = new Fixture(); await f.Ready();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        f.BeforeAppend = () => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Synthetic writer wait timed out."); };
        f.Source.Harvest(1, new NetworkGatheringItem(610620013, 1));
        try
        {
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5))));
            for (var i = 0; i < 4_500; i++) f.Source.Notify();
        }
        finally { release.Set(); }
        await f.Ready();
        var fault = Assert.IsType<LootRecordingFaultSnapshot>(f.Service.RecordingFault);
        Assert.Equal(LootRecordingFaultStage.CommandQueue, fault.Stage);
        Assert.Equal(LootRecordingFaultKind.QueueOverflow, fault.Kind);
        Assert.Contains("Ereigniswarteschlange", f.Service.Status);
        f.FailAppend = true; f.Source.Harvest(2, new NetworkGatheringItem(610620013, 1)); await f.Ready();
        Assert.Same(fault, f.Service.RecordingFault);
    }

    [Fact]
    public async Task AnAppendFaultRetainsOnlyAnonymousFirstErrorFactsAndTheOriginalExceptionClass()
    {
        await using var f = new Fixture(); await f.Ready(); f.FailAppend = true;
        f.Source.Harvest(1, new NetworkGatheringItem(610620013, 1)); await f.Ready();
        var fault = Assert.IsType<LootRecordingFaultSnapshot>(f.Service.RecordingFault);
        Assert.Equal(LootRecordingFaultStage.JournalAppend, fault.Stage);
        Assert.Equal(LootRecordingFaultKind.Io, fault.Kind);
        Assert.Equal(nameof(IOException), fault.ExceptionType);
        Assert.Equal(new IOException().HResult, fault.HResult);
        Assert.Equal(f.Clock.GetUtcNow(), fault.At);
        Assert.Contains("Schreibfehler", f.Service.Status);
        var json = JsonSerializer.Serialize(fault);
        Assert.DoesNotContain(f.Root, json); Assert.DoesNotContain("Synthetic character", json);
        Assert.DoesNotContain("Synthetic append failure", json); Assert.DoesNotContain("ProfileId", json);
    }
}

public sealed partial class UnifiedLootSessionStoreTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void LegacyJitterTailCanOnlyBeTrimmedAtAnExactPauseOrNonproductiveFact(bool utcJitter, bool pause)
    {
        using var f = new Fixture();
        var previous = LegacyJitterSession(f.Id, utcJitter);
        f.Store.Append(previous);
        var prefix = File.ReadAllText(f.Journal);
        var next = previous with
        {
            IsPaused = pause, RouteSegments = UnifiedLootSessionStore.TrimRoute(previous, previous.LastLootAt!.Value, previous.Elapsed),
            FailedHarvestCount = pause ? 0 : 1,
            TimelineEvents = pause ? previous.TimelineEvents : previous.TimelineEvents.Add(
                Fact(LootTrackerTimelineEventKind.HarvestFailure, LootTrackerActivityOrigin.Lifeskill, 10))
        };
        f.Store.Append(next);
        Assert.StartsWith(prefix, File.ReadAllText(f.Journal));
        Assert.Single(next.RouteSegments[0].Points);
        var read = new UnifiedLootSessionStore(f.Root).Read();
        Assert.Empty(read.Issues);
        var restored = Assert.Single(read.Sessions);
        Assert.Equal(next.FailedHarvestCount, restored.FailedHarvestCount);
        Assert.Equal(next.TimelineEvents.Select(row => (row.Id, row.Kind)), restored.TimelineEvents.Select(row => (row.Id, row.Kind)));
        var reward = Reward(2);
        var resumed = next with
        {
            IsPaused = false, LastLootAt = Start.AddSeconds(12), Elapsed = TimeSpan.FromSeconds(11),
            Loot = [reward], ItemCount = 2, HarvestCount = 1,
            TimelineEvents = next.TimelineEvents.Add(new(Guid.NewGuid().ToString("N"), LootTrackerTimelineEventKind.HarvestSuccess,
                Start.AddSeconds(12), TimeSpan.FromSeconds(11)) { Origin = LootTrackerActivityOrigin.Lifeskill, Loot = [reward] }),
            RouteSegments = next.RouteSegments.Add(new("test-map", "Map", [new(20, 20, Start.AddSeconds(12), TimeSpan.FromSeconds(11))])
                { StartsAfterPause = pause })
        };
        f.Store.Append(resumed);
        var recovered = Assert.Single(new UnifiedLootSessionStore(f.Root).Read().Sessions);
        Assert.Equal(2, recovered.ItemCount); Assert.Equal(1, recovered.HarvestCount);
        Assert.Equal(pause ? 0 : 1, recovered.FailedHarvestCount);
        Assert.Equal(2, recovered.RouteSegments.Length);
    }

    [Theory]
    [InlineData("arbitrary-live")]
    [InlineData("changed-point")]
    [InlineData("advanced-clock")]
    [InlineData("productive-event")]
    public void LiveTailCompatibilityNeverAllowsUnrelatedHistoryRewrites(string invalid)
    {
        using var f = new Fixture(); var previous = LegacyJitterSession(f.Id, true); f.Store.Append(previous);
        var next = previous with { IsPaused = true,
            RouteSegments = UnifiedLootSessionStore.TrimRoute(previous, previous.LastLootAt!.Value, previous.Elapsed) };
        next = invalid switch
        {
            "arbitrary-live" => next with { IsPaused = false },
            "changed-point" => next with { RouteSegments = [next.RouteSegments[0] with { Points = [next.RouteSegments[0].Points[0] with { X = 999 }] }] },
            "advanced-clock" => next with { Elapsed = previous.Elapsed + TimeSpan.FromTicks(1) },
            _ => next with { Loot = [Reward(1)], ItemCount = 1, HarvestCount = 1,
                TimelineEvents = next.TimelineEvents.Add(Fact(LootTrackerTimelineEventKind.HarvestSuccess,
                    LootTrackerActivityOrigin.Lifeskill, 10) with { Loot = [Reward(1)] }) }
        };
        var before = File.ReadAllBytes(f.Journal);
        Assert.Throws<InvalidDataException>(() => f.Store.Append(next));
        Assert.Equal(before, File.ReadAllBytes(f.Journal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReaderRejectsAForgedReplacementWithoutThePauseOrNonproductiveTransition(bool pause)
    {
        using var f = new Fixture(); var previous = LegacyJitterSession(f.Id, true); f.Store.Append(previous);
        var next = previous with { IsPaused = pause, RouteSegments = UnifiedLootSessionStore.TrimRoute(previous,
            previous.LastLootAt!.Value, previous.Elapsed), FailedHarvestCount = pause ? 0 : 1,
            TimelineEvents = pause ? previous.TimelineEvents : previous.TimelineEvents.Add(
                Fact(LootTrackerTimelineEventKind.HarvestFailure, LootTrackerActivityOrigin.Lifeskill, 10)) };
        f.Store.Append(next);
        var lines = File.ReadAllLines(f.Journal);
        var json = JsonNode.Parse(lines[^1])!;
        json["Snapshot"]!["IsPaused"] = false;
        json["Snapshot"]!["FailedHarvestCount"] = 0;
        json["TimelineEventDelta"] = new JsonArray();
        lines[^1] = json.ToJsonString(); File.WriteAllText(f.Journal, string.Join("\n", lines) + "\n");
        var read = new UnifiedLootSessionStore(f.Root).Read();
        Assert.Contains("invalid-unified-journal-record", read.Issues);
        Assert.Equal(0, Assert.Single(read.Sessions).FailedHarvestCount);
    }

    [Theory]
    [InlineData(false, LootRecordingFaultStage.JournalAppend)]
    [InlineData(true, LootRecordingFaultStage.Checkpoint)]
    public void StorageStagesAnnotateTheOriginalExceptionWithoutExposingItsPrivateMessage(bool checkpoint, LootRecordingFaultStage expectedStage)
    {
        using var f = new Fixture(); var failure = new IOException("Synthetic private journal path and identity");
        var store = new UnifiedLootSessionStore(f.Root, (path, text) =>
        { if (!checkpoint) throw failure; File.AppendAllText(path, text); }, (_, _) => throw failure);
        var thrown = Assert.Throws<IOException>(() => store.Append(Session(f.Id)));
        Assert.Same(failure, thrown);
        var fault = LootRecordingFaults.Create(thrown, LootRecordingFaultStage.Activity, Start);
        Assert.Equal(expectedStage, fault.Stage); Assert.Equal(LootRecordingFaultKind.Io, fault.Kind);
        Assert.DoesNotContain(failure.Message, JsonSerializer.Serialize(fault));
    }

    [Theory]
    [InlineData("null-loot")]
    [InlineData("null-fact")]
    public void MalformedLegacyReplacementReturnsAReadIssueInsteadOfThrowingAnUnexpectedNullReference(string malformed)
    {
        using var f = new Fixture(); var previous = LegacyJitterSession(f.Id, true); f.Store.Append(previous);
        f.Store.Append(previous with { IsPaused = true,
            RouteSegments = UnifiedLootSessionStore.TrimRoute(previous, previous.LastLootAt!.Value, previous.Elapsed) });
        var lines = File.ReadAllLines(f.Journal); var json = JsonNode.Parse(lines[^1])!;
        if (malformed == "null-loot") json["Snapshot"]!["Loot"] = null;
        else json["TimelineEventDelta"] = new JsonArray((JsonNode?)null);
        lines[^1] = json.ToJsonString(); File.WriteAllText(f.Journal, string.Join("\n", lines) + "\n");
        var read = new UnifiedLootSessionStore(f.Root).Read();
        Assert.Contains("invalid-unified-journal-record", read.Issues);
        Assert.Single(read.Sessions);
    }

    private static LootTrackerSession LegacyJitterSession(string id, bool utcJitter) => Session(id) with
    {
        LastLootAt = Start.AddSeconds(10), Elapsed = TimeSpan.FromSeconds(10),
        RouteSegments = [new("test-map", "Map", [new(10, 10, Start.AddSeconds(1), TimeSpan.FromSeconds(1)),
            new(11, 11, Start.AddSeconds(utcJitter ? 10.0277 : 9.9), TimeSpan.FromSeconds(utcJitter ? 9.9 : 10.0152074))])]
    };
}
