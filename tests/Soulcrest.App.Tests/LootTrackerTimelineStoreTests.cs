using System.Text.Json.Nodes;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class LootTrackerTimelineStoreTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 27, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void VersionFiveKeepsTimelineDeltasWithTotalsAndRecoversOnlyTheCommittedPrefixOnce()
    {
        using var f = new Fixture(); var initial = f.Session(); f.Store.Append(initial);
        var ap = Fact(LootTrackerTimelineEventKind.AbyssPoints, 1) with { Amount = 10 };
        var gain = initial with { AbyssPoints = 10, LastPresenceAt = ap.At, LastLootAt = ap.At,
            Elapsed = ap.Elapsed, TimelineEvents = [ap] };
        f.Store.Append(gain);
        var rank = Fact(LootTrackerTimelineEventKind.RankPoints, 2) with { Amount = -5 };
        f.Store.Append(gain with { RankPointsLost = 5, LastPresenceAt = rank.At, LastLootAt = rank.At,
            Elapsed = rank.Elapsed, TimelineEvents = [ap, rank] });
        var lines = File.ReadAllLines(f.Journal).Select(line => JsonNode.Parse(line)!.AsObject()).ToArray();
        Assert.All(lines, line => { Assert.Equal(5, line["SchemaVersion"]!.GetValue<int>()); Assert.Empty(line["Snapshot"]!["TimelineEvents"]!.AsArray()); });
        Assert.Equal(new[] { 0, 1, 1 }, lines.Select(line => line["TimelineEventDelta"]!.AsArray().Count));
        File.AppendAllText(f.Journal, "{\"SchemaVersion\":5");
        var read = new FarmLootSessionStore(f.Root).Read(); var session = Assert.Single(read.Sessions);
        Assert.Contains("incomplete-farm-journal-tail", read.Issues);
        Assert.Equal(10, session.AbyssPoints); Assert.Equal(5, session.RankPointsLost);
        Assert.Equal(new[] { ap.Id, rank.Id }, session.TimelineEvents.Select(row => row.Id));
    }

    [Theory]
    [InlineData("negative-ap")]
    [InlineData("default-id")]
    [InlineData("future-elapsed")]
    [InlineData("stage-down")]
    [InlineData("missing-availability")]
    [InlineData("reward-with-amount")]
    public void InvalidExactFactsCannotCommit(string invalid)
    {
        using var f = new Fixture();
        var fact = Fact(LootTrackerTimelineEventKind.AbyssPoints, 0) with { Amount = 10 };
        fact = invalid switch
        {
            "negative-ap" => fact with { Amount = -1 },
            "default-id" => fact with { Id = new Guid().ToString("N") },
            "future-elapsed" => fact with { Elapsed = TimeSpan.FromSeconds(1) },
            "stage-down" => fact with { Kind = LootTrackerTimelineEventKind.PointsStageReached, Amount = null,
                FromStageId = "rank-17", ToStageId = "rank-18" },
            "missing-availability" => fact with { Kind = LootTrackerTimelineEventKind.Availability, Amount = null,
                Metric = LootTrackerTimelineMetric.AbyssPoints },
            _ => fact with { Kind = LootTrackerTimelineEventKind.Drop, Loot = [new("Item:1", "Synthetic item", LootTrackerLootKind.Item, 1)] }
        };
        Assert.Throws<InvalidDataException>(() => f.Store.Append(f.Session() with { TimelineEvents = [fact] }));
        Assert.False(File.Exists(f.Journal));
    }

    [Fact]
    public void ExactHistoryCannotBeEditedAndVersionFiveCannotLoseItsDeltaOrDowngrade()
    {
        using var f = new Fixture();
        var fact = Fact(LootTrackerTimelineEventKind.AbyssPoints, 0) with { Amount = 10 };
        var first = f.Session() with { TimelineEvents = [fact], AbyssPoints = 10 };
        f.Store.Append(first);
        Assert.Throws<InvalidDataException>(() => f.Store.Append(first with { TimelineEvents = [fact with { Amount = 20 }] }));
        var line = File.ReadAllLines(f.Journal).Single(); var json = JsonNode.Parse(line)!.AsObject();
        json.Remove("TimelineEventDelta"); File.WriteAllText(f.Journal, json.ToJsonString() + "\n");
        Assert.Empty(new FarmLootSessionStore(f.Root).Read().Sessions);
        json = JsonNode.Parse(line)!.AsObject(); json["Sequence"] = 2; json["SchemaVersion"] = 4;
        File.WriteAllText(f.Journal, line + "\n" + json.ToJsonString() + "\n");
        var read = new FarmLootSessionStore(f.Root).Read(); Assert.Single(read.Sessions);
        Assert.Contains("invalid-farm-journal-record", read.Issues);
    }

    [Fact]
    public void AggregateLegacyHistoryDoesNotAcquireExactEventsOrOpponentMetadata()
    {
        using var f = new Fixture(); var legacy = f.Session() with { TimelineVersion = 0, AbyssPoints = 123 };
        f.Store.Append(legacy);
        var read = new FarmLootSessionStore(f.Root).Read(); Assert.Empty(read.Issues);
        var session = Assert.Single(read.Sessions); Assert.Equal(0, session.TimelineVersion); Assert.Empty(session.TimelineEvents);
        Assert.Equal(123, session.AbyssPoints);
        Assert.Throws<InvalidDataException>(() => f.Store.Append(legacy with { TimelineVersion = 1 }));
        var combat = new LootTrackerCombatEvent(Guid.NewGuid().ToString("N"), LootTrackerCombatKind.Kill, At, TimeSpan.Zero)
            with { OpponentClassId = "Assassin" };
        Assert.Throws<InvalidDataException>(() => f.Store.Append(legacy with { CombatEvents = [combat] }));
    }

    [Fact]
    public void PveCannotStoreAbyssFactsButCanonicalDropsRemainOneLocalEvent()
    {
        using var f = new Fixture();
        var pve = f.Session() with { Kind = LootTrackerSessionKind.Grind, LastPresenceAt = null, LastLootAt = At,
            AbyssPoints = null, RankPointsGained = null, RankPointsLost = null };
        Assert.Throws<InvalidDataException>(() => f.Store.Append(pve with
            { TimelineEvents = [Fact(LootTrackerTimelineEventKind.RankPoints, 0) with { Amount = 10 }] }));
        var drop = Fact(LootTrackerTimelineEventKind.Drop, 0) with { Loot = [new("Item:1", "Synthetic item", LootTrackerLootKind.Item, 2)] };
        f.Store.Append(pve with { Loot = drop.Loot, ItemCount = 2, TimelineEvents = [drop] });
        var read = new FarmLootSessionStore(f.Root).Read(); Assert.Empty(read.Issues);
        Assert.Equal(drop.Id, Assert.Single(Assert.Single(read.Sessions).TimelineEvents).Id);
    }

    private static LootTrackerTimelineEvent Fact(LootTrackerTimelineEventKind kind, int seconds) =>
        new(Guid.NewGuid().ToString("N"), kind, At.AddSeconds(seconds), TimeSpan.FromSeconds(seconds));

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "soulcrest-timeline-store-" + Guid.NewGuid().ToString("N"));
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public FarmLootSessionStore Store { get; }
        public string Journal => Path.Combine(Root, Id, "events-0001.jsonl");
        public Fixture() => Store = new(Root);
        public LootTrackerSession Session() => new()
        {
            Id = Id, Kind = LootTrackerSessionKind.Abyss, TimelineVersion = 1, Status = LootTrackerSessionStatus.Live,
            StartedAt = At, LastPresenceAt = At, Character = new("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Synthetic character"),
            AbyssPoints = 0, RankPointsGained = 0, RankPointsLost = 0
        };
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
