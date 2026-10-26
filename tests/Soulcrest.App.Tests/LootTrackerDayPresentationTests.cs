using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class LootTrackerDayPresentationTests
{
    [Fact]
    public void AllSessionTypesUseUtcResetAndStoredDayOwnsLegacyFallback()
    {
        var before = new DateTimeOffset(2026, 10, 25, 6, 59, 59, TimeSpan.Zero);
        var after = before.AddSeconds(1);
        var groups = LootTrackerDayPresentation.Group([
            new() { Id = "before", Kind = LootTrackerSessionKind.Grind, StartedAt = before },
            new() { Id = "after", Kind = LootTrackerSessionKind.Dungeon, StartedAt = after },
            new() { Id = "same-in-local-time", Kind = LootTrackerSessionKind.Lifeskill, StartedAt = after.ToOffset(TimeSpan.FromHours(8)) },
            new() { Id = "stored", StartedAt = after, FarmDay = new(2026, 10, 23) },
        ]);
        Assert.Equal([new DateOnly(2026, 10, 25), new(2026, 10, 24), new(2026, 10, 23)], groups.Select(group => group.Day));
        Assert.Equal(2, groups[0].Sessions.Length);
        Assert.Equal("before", groups[1].Sessions.Single().Id);
        Assert.Equal("stored", groups[2].Sessions.Single().Id);
    }

    [Fact]
    public void KnownSubtotalsAreMarkedPartialAndUnknownIsNotZero()
    {
        var at = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
        var group = Assert.Single(LootTrackerDayPresentation.Group([
            new() { Id = "one", StartedAt = at, KinaUnbound = 10, KinaBound = null, ItemCount = 0, SoulCount = null },
            new() { Id = "two", StartedAt = at, KinaUnbound = 20, KinaBound = null, ItemCount = 2, SoulCount = 3 },
            new() { Id = "unknown", StartedAt = at, KinaUnbound = null, KinaBound = null, ItemCount = 0, SoulCount = -1 },
        ]));
        Assert.Equal(new LootTrackerDayAmount(30, false), group.KinaUnbound);
        Assert.Equal(new LootTrackerDayAmount(null, false), group.KinaBound);
        Assert.Equal(new LootTrackerDayAmount(2, true), group.Items);
        Assert.Equal(new LootTrackerDayAmount(3, false), group.Souls);
    }

    [Fact]
    public void OverflowNeverWrapsIntoAFalseDayTotal()
    {
        var at = DateTimeOffset.UtcNow;
        var group = Assert.Single(LootTrackerDayPresentation.Group([
            new() { Id = "one", StartedAt = at, KinaUnbound = long.MaxValue },
            new() { Id = "two", StartedAt = at, KinaUnbound = 1 },
        ]));
        Assert.Null(group.KinaUnbound.Value);
        Assert.False(group.KinaUnbound.Complete);
    }
}
