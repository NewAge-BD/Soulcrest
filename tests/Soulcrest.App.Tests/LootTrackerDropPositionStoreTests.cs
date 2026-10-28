using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class LootTrackerDropPositionStoreTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public void OptionalDropPositionRoundTripsWithoutUpgradingTheJournalSchema(bool unified, bool placed)
    {
        using var fixture = new Fixture();
        var session = Session(fixture.Id, unified, placed ? new("sample-map", "Synthetic map", 10.123456789, 20.987654321, At.AddSeconds(1)) : null);
        fixture.Append(session, unified);
        var restored = fixture.Read(unified);
        Assert.Equal(session.TimelineEvents[0].Position, Assert.Single(restored.TimelineEvents).Position);
        Assert.Equal(1, restored.ItemCount); Assert.Equal(1, Assert.Single(restored.Loot).Quantity);
        var text = File.ReadAllText(fixture.Journal);
        Assert.Contains(unified ? "\"SchemaVersion\":1" : "\"SchemaVersion\":5", text);
        if (!placed) Assert.DoesNotContain("\"Position\"", text);
    }

    [Theory]
    [InlineData(false, "negative")] [InlineData(true, "negative")]
    [InlineData(false, "nonfinite")] [InlineData(true, "nonfinite")]
    [InlineData(false, "map")] [InlineData(true, "map")]
    [InlineData(false, "name")] [InlineData(true, "name")]
    [InlineData(false, "stale")] [InlineData(true, "stale")]
    [InlineData(false, "future")] [InlineData(true, "future")]
    [InlineData(false, "missing-time")] [InlineData(true, "missing-time")]
    public void InvalidDropEvidenceCannotEnterEitherJournal(bool unified, string variant)
    {
        using var fixture = new Fixture();
        var position = new LootTrackerCombatPosition("sample-map", "Synthetic map", 10, 20, At.AddSeconds(1));
        position = variant switch
        {
            "negative" => position with { X = -1 }, "nonfinite" => position with { Y = double.PositiveInfinity },
            "map" => position with { MapId = "" }, "name" => position with { MapName = "invalid\nname" },
            "stale" => position with { ObservedAt = At.AddSeconds(-2) }, "future" => position with { ObservedAt = At.AddSeconds(2) },
            _ => position with { ObservedAt = default }
        };
        Assert.Throws<InvalidDataException>(() => fixture.Append(Session(fixture.Id, unified, position), unified));
        Assert.False(File.Exists(fixture.Journal));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DropPositionCannotBeAttachedToAnUnrelatedAvailabilityFact(bool unified)
    {
        using var fixture = new Fixture();
        var session = Session(fixture.Id, unified, new("sample-map", "Synthetic map", 10, 20, At.AddSeconds(1)));
        session = session with { ItemCount = 0, Loot = [], TimelineEvents = [session.TimelineEvents[0] with
            { Kind = LootTrackerTimelineEventKind.Availability, Loot = [], Metric = LootTrackerTimelineMetric.KinaUnbound, Available = true }] };
        Assert.Throws<InvalidDataException>(() => fixture.Append(session, unified));
        Assert.False(File.Exists(fixture.Journal));
    }

    private static LootTrackerSession Session(string id, bool unified, LootTrackerCombatPosition? position) => new()
    {
        Id = id, Kind = unified ? LootTrackerSessionKind.Mixed : LootTrackerSessionKind.Grind,
        UnifiedVersion = unified ? 1 : 0, TimelineVersion = 1, Status = LootTrackerSessionStatus.Completed,
        StartedAt = At, LastLootAt = At.AddSeconds(1), EndedAt = At.AddSeconds(1), Elapsed = TimeSpan.FromSeconds(1),
        Character = new("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Synthetic character"), ItemCount = 1, SoulCount = 0,
        HarvestCount = 0, FailedHarvestCount = 0, KinaUnbound = 0,
        Loot = [new("Item:7", "Synthetic item", LootTrackerLootKind.Item, 1)],
        TimelineEvents = [new(Guid.NewGuid().ToString("N"), LootTrackerTimelineEventKind.Drop, At.AddSeconds(1), TimeSpan.FromSeconds(1))
            { Origin = LootTrackerActivityOrigin.Pve, Position = position, Loot = [new("Item:7", "Synthetic item", LootTrackerLootKind.Item, 1)] }]
    };
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "soulcrest-drop-position-" + Guid.NewGuid().ToString("N"));
        internal string Id { get; } = Guid.NewGuid().ToString("N");
        internal string Journal => Path.Combine(Root, Id, "events-0001.jsonl");
        internal void Append(LootTrackerSession session, bool unified)
        { if (unified) new UnifiedLootSessionStore(Root).Append(session); else new FarmLootSessionStore(Root).Append(session); }
        internal LootTrackerSession Read(bool unified) => unified ? Assert.Single(new UnifiedLootSessionStore(Root).Read().Sessions)
            : Assert.Single(new FarmLootSessionStore(Root).Read().Sessions);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
