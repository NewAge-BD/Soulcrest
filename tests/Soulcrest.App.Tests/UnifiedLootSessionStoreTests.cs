using System.Collections.Immutable;
using System.Text.Json;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class UnifiedLootSessionStoreTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MissingRootHasNoSessionsOrIssues()
    {
        using var fixture = new Fixture();
        var result = fixture.Store.Read();
        Assert.Empty(result.Sessions); Assert.Empty(result.Issues); Assert.False(result.CleanupPending);
    }

    [Fact]
    public void PveAndHarvestOfTheSameItemRecoverTogetherWithTheirExactOrigins()
    {
        using var fixture = new Fixture();
        var first = Session(fixture.Id);
        fixture.Store.Append(first);
        var drops = new[] { Reward(4), Reward(7) };
        var second = first with
        {
            LastLootAt = Start.AddSeconds(2), Elapsed = TimeSpan.FromSeconds(2),
            ItemCount = 11, HarvestCount = 1, Loot = [Reward(11)],
            TimelineEvents = [Fact(LootTrackerTimelineEventKind.Drop, LootTrackerActivityOrigin.Pve, 1) with { Loot = [drops[0]] },
                Fact(LootTrackerTimelineEventKind.HarvestSuccess, LootTrackerActivityOrigin.Lifeskill, 2) with { Loot = [drops[1]] }]
        };
        fixture.Store.Append(second);
        var read = new UnifiedLootSessionStore(fixture.Root).Read();
        Assert.Empty(read.Issues);
        var recovered = Assert.Single(read.Sessions);
        Assert.Equal(LootTrackerSessionStatus.Interrupted, recovered.Status);
        Assert.Equal(11, recovered.ItemCount); Assert.Equal(1, recovered.HarvestCount);
        Assert.Equal(new[] { LootTrackerActivityOrigin.Pve, LootTrackerActivityOrigin.Lifeskill },
            recovered.TimelineEvents.Select(row => row.Origin!.Value));
        Assert.Equal(2, File.ReadAllLines(fixture.Journal).Length);
        using var checkpoint = JsonDocument.Parse(File.ReadAllBytes(fixture.Checkpoint));
        Assert.Equal(2, checkpoint.RootElement.GetProperty("ThroughSequence").GetInt32());
    }

    [Fact]
    public void AbyssPointsDeathAndFailedHarvestAreAnAtomicDeltaWithoutInventedPositions()
    {
        using var fixture = new Fixture();
        var first = Session(fixture.Id, abyss: true);
        fixture.Store.Append(first);
        var next = first with
        {
            LastPresenceAt = Start.AddSeconds(3), LastLootAt = Start.AddSeconds(3), Elapsed = TimeSpan.FromSeconds(3),
            AbyssPoints = 200, FailedHarvestCount = 1,
            CombatEvents = [new(Guid.NewGuid().ToString("N"), LootTrackerCombatKind.Death, Start.AddSeconds(2),
                TimeSpan.FromSeconds(2)) { Origin = LootTrackerActivityOrigin.Abyss }],
            TimelineEvents = [Fact(LootTrackerTimelineEventKind.AbyssPoints, LootTrackerActivityOrigin.Abyss, 1) with { Amount = 200 },
                Fact(LootTrackerTimelineEventKind.HarvestFailure, LootTrackerActivityOrigin.Lifeskill, 3)]
        };
        fixture.Store.Append(next);
        fixture.Store.Append(next with { LastPresenceAt = Start.AddSeconds(4), Elapsed = TimeSpan.FromSeconds(4) });
        var recovered = Assert.Single(new UnifiedLootSessionStore(fixture.Root).Read().Sessions);
        Assert.Equal(200, recovered.AbyssPoints); Assert.Equal(1, recovered.FailedHarvestCount);
        Assert.Null(Assert.Single(recovered.CombatEvents).Position); Assert.Empty(recovered.GatherVisits);
        Assert.Equal(2, recovered.TimelineEvents.Length);
    }

    [Fact]
    public void MixedPointIncomeRecoversWithPveOriginsWithoutCombatOrResidenceMetadata()
    {
        using var fixture = new Fixture();
        fixture.Store.Append(Session(fixture.Id));
        var ap = Fact(LootTrackerTimelineEventKind.AbyssPoints, LootTrackerActivityOrigin.Pve, 1) with { Amount = 14 };
        var gained = Fact(LootTrackerTimelineEventKind.RankPoints, LootTrackerActivityOrigin.Pve, 2) with { Amount = 20 };
        var lost = Fact(LootTrackerTimelineEventKind.RankPoints, LootTrackerActivityOrigin.Pve, 3) with { Amount = -50 };
        var snapshot = Session(fixture.Id) with
        {
            LastLootAt = Start.AddSeconds(3), Elapsed = TimeSpan.FromSeconds(3),
            AbyssPoints = 14, RankPointsGained = 20, RankPointsLost = 50,
            TimelineEvents = [ap, gained, lost],
            MinuteIncome = [new(0, null, null) { AbyssPoints = 14, RankPointsGained = 20, RankPointsLost = 50 }]
        };
        fixture.Store.Append(snapshot);
        var read = new UnifiedLootSessionStore(fixture.Root).Read();
        Assert.Empty(read.Issues); var recovered = Assert.Single(read.Sessions);
        Assert.Equal(LootTrackerSessionKind.Mixed, recovered.Kind); Assert.Equal(14, recovered.AbyssPoints);
        Assert.Equal(20, recovered.RankPointsGained); Assert.Equal(50, recovered.RankPointsLost);
        Assert.Equal(-30, LootTrackerPresentation.NetRankPoints(recovered));
        Assert.Equal(snapshot.MinuteIncome.ToArray(), recovered.MinuteIncome.ToArray());
        Assert.Equal(new[] { ap.Id, gained.Id, lost.Id }, recovered.TimelineEvents.Select(row => row.Id));
        Assert.Equal(new long?[] { 14, 20, -50 }, recovered.TimelineEvents.Select(row => row.Amount));
        Assert.All(recovered.TimelineEvents, row => Assert.Equal(LootTrackerActivityOrigin.Pve, row.Origin));
        Assert.Empty(recovered.CombatEvents); Assert.Null(recovered.LastPresenceAt);
    }

    [Theory]
    [InlineData("wrong-origin")]
    [InlineData("combat")]
    [InlineData("presence")]
    [InlineData("mismatched-total")]
    [InlineData("late-fact")]
    [InlineData("negative-ap")]
    [InlineData("outside-stage")]
    public void MixedPointSupportKeepsTheExistingProvenanceAndAtomicTotalsContractStrict(string invalid)
    {
        using var fixture = new Fixture();
        var fact = Fact(LootTrackerTimelineEventKind.AbyssPoints, LootTrackerActivityOrigin.Pve, 1) with { Amount = 14 };
        var snapshot = Session(fixture.Id) with
        {
            LastLootAt = Start.AddSeconds(3), Elapsed = TimeSpan.FromSeconds(3), AbyssPoints = 14,
            TimelineEvents = [fact], MinuteIncome = [new(0, null, null) { AbyssPoints = 14 }]
        };
        snapshot = invalid switch
        {
            "wrong-origin" => snapshot with { TimelineEvents = [fact with { Origin = LootTrackerActivityOrigin.Abyss }] },
            "combat" => snapshot with { CombatEvents = [new(Guid.NewGuid().ToString("N"), LootTrackerCombatKind.Kill,
                Start.AddSeconds(1), TimeSpan.FromSeconds(1)) { Origin = LootTrackerActivityOrigin.Abyss }] },
            "presence" => snapshot with { LastPresenceAt = Start.AddSeconds(3) },
            "mismatched-total" => snapshot with { AbyssPoints = 15 },
            "late-fact" => snapshot with { TimelineEvents = [fact with { At = Start.AddSeconds(4) }] },
            "negative-ap" => snapshot with { AbyssPoints = -14, TimelineEvents = [fact with { Amount = -14 }] },
            _ => snapshot with { TimelineEvents = [fact, Fact(LootTrackerTimelineEventKind.PointsStageReached,
                LootTrackerActivityOrigin.Pve, 2) with { FromStageId = "rank-19", ToStageId = "rank-18" }] }
        };
        Assert.Throws<InvalidDataException>(() => fixture.Store.Append(snapshot));
        Assert.False(File.Exists(fixture.Journal));
    }

    [Theory]
    [InlineData(null)] [InlineData(LootTrackerActivityOrigin.Abyss)] [InlineData((LootTrackerActivityOrigin)99)]
    public void MissingOrContradictoryOriginsCannotBeCommitted(LootTrackerActivityOrigin? origin)
    {
        using var fixture = new Fixture();
        var snapshot = Session(fixture.Id) with
        {
            ItemCount = 1, Loot = [Reward(1)], LastLootAt = Start.AddSeconds(1), Elapsed = TimeSpan.FromSeconds(1),
            TimelineEvents = [Fact(LootTrackerTimelineEventKind.Drop, LootTrackerActivityOrigin.Pve, 1) with { Origin = origin, Loot = [Reward(1)] }]
        };
        Assert.Throws<InvalidDataException>(() => fixture.Store.Append(snapshot));
        Assert.False(File.Exists(fixture.Journal));
    }

    [Fact]
    public void AggregateCountersCannotDepartFromCommittedFacts()
    {
        using var fixture = new Fixture();
        var empty = Session(fixture.Id);
        Assert.Throws<InvalidDataException>(() => fixture.Store.Append(empty with { ItemCount = 1, Loot = [Reward(1)] }));
        Assert.Throws<InvalidDataException>(() => fixture.Store.Append(empty with { HarvestCount = 1 }));
        Assert.Throws<InvalidDataException>(() => fixture.Store.Append(empty with { KinaUnbound = 13 }));
        Assert.Throws<InvalidDataException>(() => fixture.Store.Append(empty with { UnifiedVersion = 0 }));
    }

    [Fact]
    public void ReachingTheKinaLimitChangesModeWithoutChangingIdentityOrRewritingFacts()
    {
        using var fixture = new Fixture();
        var first = Session(fixture.Id);
        fixture.Store.Append(first);
        var capped = first with { Mode = LootTrackerSessionMode.LimitedKina };
        fixture.Store.Append(capped);
        Assert.Throws<InvalidDataException>(() => fixture.Store.Append(first));
        var recovered = Assert.Single(fixture.Store.Read().Sessions);
        Assert.Equal(first.Id, recovered.Id); Assert.Equal(first.StartedAt, recovered.StartedAt);
        Assert.Equal(LootTrackerSessionMode.LimitedKina, recovered.Mode);
    }

    [Fact]
    public void FactsAreAppendOnlyAndTerminalSessionsNeverResume()
    {
        using var fixture = new Fixture();
        var first = Session(fixture.Id) with
        {
            KinaUnbound = 10, Elapsed = TimeSpan.FromSeconds(1), LastLootAt = Start.AddSeconds(1),
            TimelineEvents = [Fact(LootTrackerTimelineEventKind.KinaUnbound, LootTrackerActivityOrigin.Pve, 1) with { Amount = 10 }]
        };
        fixture.Store.Append(first);
        Assert.Throws<InvalidDataException>(() => fixture.Store.Append(first with
        { KinaUnbound = 11, TimelineEvents = [first.TimelineEvents[0] with { Amount = 11 }] }));
        fixture.Store.Append(first with { Status = LootTrackerSessionStatus.Completed, EndedAt = first.LastLootAt });
        Assert.Throws<InvalidDataException>(() => fixture.Store.Append(first));
    }

    [Fact]
    public void IncompleteTailKeepsOnlyTheLastCompleteTransaction()
    {
        using var fixture = new Fixture();
        fixture.Store.Append(Session(fixture.Id));
        File.AppendAllText(fixture.Journal, "{\"SchemaVersion\":1");
        var read = new UnifiedLootSessionStore(fixture.Root).Read();
        Assert.NotEmpty(read.Issues); Assert.True(Assert.Single(read.Sessions).CoverageLimited);
        Assert.Equal(0, Assert.Single(read.Sessions).ItemCount);
    }

    [Fact]
    public void CheckpointFailureNeverAddsCheckpointTotalsDuringRecovery()
    {
        using var fixture = new Fixture();
        var fail = false;
        var store = new UnifiedLootSessionStore(fixture.Root, Append, (path, value) =>
        { if (fail) throw new IOException("Synthetic checkpoint failure."); JsonFile.Save(path, value); });
        var first = Session(fixture.Id); store.Append(first); fail = true;
        var next = first with
        {
            KinaUnbound = 12, Elapsed = TimeSpan.FromSeconds(1), LastLootAt = Start.AddSeconds(1),
            TimelineEvents = [Fact(LootTrackerTimelineEventKind.KinaUnbound, LootTrackerActivityOrigin.Pve, 1) with { Amount = 12 }]
        };
        Assert.Throws<IOException>(() => store.Append(next));
        var recovered = Assert.Single(new UnifiedLootSessionStore(fixture.Root).Read().Sessions);
        Assert.Equal(12, recovered.KinaUnbound); Assert.Single(recovered.TimelineEvents);
    }

    [Fact]
    public void UncertainAppendBlocksFurtherWritesAndDeletionDoesNotResurrect()
    {
        using var fixture = new Fixture();
        var store = new UnifiedLootSessionStore(fixture.Root,
            (_, _) => throw new IOException("Synthetic append failure."), (_, _) => { });
        var failedId = Guid.NewGuid().ToString("N");
        Assert.Throws<IOException>(() => store.Append(Session(failedId)));
        Assert.Throws<IOException>(() => store.Append(Session(failedId)));
        fixture.Store.Append(Session(fixture.Id));
        Assert.False(fixture.Store.Delete(fixture.Id));
        Assert.Empty(new UnifiedLootSessionStore(fixture.Root).Read().Sessions);
        Assert.Throws<InvalidOperationException>(() => fixture.Store.Append(Session(fixture.Id)));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RecoveryPreservesCommittedIncomeWhenMonotonicDurationExceedsTheSourceTimestampSpan(bool abyss)
    {
        using var fixture = new Fixture();
        var elapsed = TimeSpan.FromSeconds(1.01);
        var session = Session(fixture.Id, abyss) with
        {
            LastLootAt = Start.AddSeconds(1), LastPresenceAt = abyss ? Start.AddSeconds(1) : null,
            Elapsed = elapsed, ItemCount = 4, Loot = [Reward(4)],
            TimelineEvents = [Fact(LootTrackerTimelineEventKind.Drop,
                abyss ? LootTrackerActivityOrigin.Abyss : LootTrackerActivityOrigin.Pve, 1) with
                { Elapsed = elapsed, Loot = [Reward(4)] }]
        };
        fixture.Store.Append(session);
        var recovered = Assert.Single(new UnifiedLootSessionStore(fixture.Root).Read().Sessions);
        Assert.Equal(elapsed, recovered.Elapsed); Assert.Single(recovered.TimelineEvents);
        Assert.Equal(4, Assert.Single(recovered.Loot).Quantity);
        Assert.Equal(4, Assert.Single(LootTrackerSessionProjection.Project(recovered,
            abyss ? LootTrackerViewMode.Abyss : LootTrackerViewMode.Pve).Loot).Quantity);
    }

    [Fact]
    public void MinuteIncomeIsJournaledAsChangedBinsAndRecoversExactly()
    {
        using var fixture = new Fixture();
        var session = Session(fixture.Id, abyss: true);
        fixture.Store.Append(session);
        for (var minute = 0; minute < 120; minute++) session = AppendAbyssMinute(fixture, session, minute);
        var lines = File.ReadAllLines(fixture.Journal);
        using (var last = JsonDocument.Parse(lines[^1]))
        {
            Assert.Equal(0, last.RootElement.GetProperty("Snapshot").GetProperty("MinuteIncome").GetArrayLength());
            Assert.Equal(1, last.RootElement.GetProperty("MinuteIncomeDelta").GetArrayLength());
        }
        var recovered = Assert.Single(new UnifiedLootSessionStore(fixture.Root).Read().Sessions);
        Assert.Equal(session.MinuteIncome.ToArray(), recovered.MinuteIncome.ToArray());
        Assert.Equal(1200, recovered.AbyssPoints);
    }

    [Fact]
    public void RecordsWithAFullIncomeListRemainReadable()
    {
        using var fixture = new Fixture();
        var session = Session(fixture.Id, abyss: true);
        fixture.Store.Append(session);
        for (var minute = 0; minute < 3; minute++) session = AppendAbyssMinute(fixture, session, minute);
        var lines = File.ReadAllLines(fixture.Journal);
        // Rewrite the last record in the original layout: the whole list in the snapshot, no delta.
        var node = System.Text.Json.Nodes.JsonNode.Parse(lines[^1])!.AsObject();
        node.Remove("MinuteIncomeDelta");
        node["Snapshot"]!["MinuteIncome"] = JsonSerializer.SerializeToNode(session.MinuteIncome);
        lines[^1] = node.ToJsonString();
        File.WriteAllText(fixture.Journal, string.Join("\n", lines) + "\n");
        var read = new UnifiedLootSessionStore(fixture.Root).Read();
        Assert.Empty(read.Issues);
        Assert.Equal(session.MinuteIncome.ToArray(), Assert.Single(read.Sessions).MinuteIncome.ToArray());
    }

    private static LootTrackerSession AppendAbyssMinute(Fixture fixture, LootTrackerSession session, int minute)
    {
        var at = TimeSpan.FromMinutes(minute) + TimeSpan.FromSeconds(1);
        var next = session with
        {
            LastPresenceAt = Start + at, Elapsed = at, AbyssPoints = (session.AbyssPoints ?? 0) + 10,
            MinuteIncome = session.MinuteIncome.Add(new LootTrackerIncomeBin(minute, null, null) { AbyssPoints = 10 }),
            TimelineEvents = session.TimelineEvents.Add(new LootTrackerTimelineEvent(Guid.NewGuid().ToString("N"),
                LootTrackerTimelineEventKind.AbyssPoints, Start + at, at) { Origin = LootTrackerActivityOrigin.Abyss, Amount = 10 })
        };
        fixture.Store.Append(next);
        return next;
    }

    [Fact]
    public void SessionPathsRemainLocalGuidsAndCannotEscapeTheStore()
    {
        using var fixture = new Fixture();
        Assert.Throws<InvalidDataException>(() => fixture.Store.Append(Session("../escape")));
        Assert.Throws<InvalidDataException>(() => fixture.Store.Delete("../escape"));
    }

    private static LootTrackerTimelineEvent Fact(LootTrackerTimelineEventKind kind, LootTrackerActivityOrigin origin, int seconds) =>
        new(Guid.NewGuid().ToString("N"), kind, Start.AddSeconds(seconds), TimeSpan.FromSeconds(seconds)) { Origin = origin };
    private static LootTrackerLoot Reward(long count) => new("Item:410010001", "Synthetic resource", LootTrackerLootKind.Item, count);
    private static LootTrackerSession Session(string id, bool abyss = false) => new()
    {
        Id = id, UnifiedVersion = 1, TimelineVersion = 1,
        Kind = abyss ? LootTrackerSessionKind.Abyss : LootTrackerSessionKind.Mixed, Status = LootTrackerSessionStatus.Live,
        Character = new("00000000-0000-0000-0000-000000000123", "Synthetic character"),
        StartedAt = Start, LastLootAt = Start, LastPresenceAt = abyss ? Start : null,
        Elapsed = TimeSpan.Zero, ItemCount = 0, SoulCount = 0, HarvestCount = 0, FailedHarvestCount = 0,
        KinaUnbound = 0, AbyssPoints = abyss ? 0 : null
    };
    private static void Append(string path, string text) => File.AppendAllText(path, text);
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "Soulcrest-unified-" + Guid.NewGuid().ToString("N"));
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string Journal => Path.Combine(Root, Id, "events-0001.jsonl");
        public string Checkpoint => Path.Combine(Root, Id, "summary-v1.json");
        public UnifiedLootSessionStore Store { get; }
        public Fixture() => Store = new(Root);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
