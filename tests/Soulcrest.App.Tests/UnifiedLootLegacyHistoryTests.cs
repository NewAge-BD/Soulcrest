using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class UnifiedLootLegacyHistoryTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 10, 26, 8, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "soulcrest-legacy-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ADamagedLegacySessionMarksOnlyItselfAndReportsAnIncompleteHistory()
    {
        var store = new FarmLootSessionStore(Path.Combine(_root, "monster-sessions"));
        var healthy = Session(Guid.NewGuid().ToString("N"));
        store.Append(healthy);
        var damaged = Session(Guid.NewGuid().ToString("N"));
        store.Append(damaged);
        store.Append(damaged with { KinaUnbound = 20 });
        var journal = Path.Combine(_root, "monster-sessions", damaged.Id, "events-0001.jsonl");
        var lines = File.ReadAllLines(journal);
        File.WriteAllLines(journal, [lines[0], "{ damaged"]);

        var read = new UnifiedLootLegacyHistory(_root).ReadSessions();

        Assert.True(read.Incomplete);
        Assert.False(Assert.Single(read.Sessions, row => row.Id == healthy.Id).CoverageLimited);
        Assert.True(Assert.Single(read.Sessions, row => row.Id == damaged.Id).CoverageLimited);
    }

    [Fact]
    public void HealthyLegacyStoresAreComplete()
    {
        new FarmLootSessionStore(Path.Combine(_root, "monster-sessions")).Append(Session(Guid.NewGuid().ToString("N")));
        var read = new UnifiedLootLegacyHistory(_root).ReadSessions();
        Assert.False(read.Incomplete);
        Assert.False(Assert.Single(read.Sessions).CoverageLimited);
    }

    private static LootTrackerSession Session(string id) => new()
    {
        Id = id, Kind = LootTrackerSessionKind.Grind, Status = LootTrackerSessionStatus.Live,
        Location = "Synthetic location", StartedAt = At, LastLootAt = At, Elapsed = TimeSpan.Zero,
        Character = new("11111111111111111111111111111111", "Synthetic character", 1000, 200),
        KinaUnbound = 10, KinaBound = null, MonsterCount = null, ItemCount = null, SoulCount = null,
    };

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
