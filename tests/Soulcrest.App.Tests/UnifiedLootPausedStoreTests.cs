using System.Text.Json;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed partial class UnifiedLootSessionStoreTests
{
    [Fact]
    public void APausedLiveSessionRecoversAtItsLastProductiveDurationWithoutResumingOrLosingFacts()
    {
        using var fixture = new Fixture(); var live = Session(fixture.Id);
        fixture.Store.Append(live); fixture.Store.Append(live with { IsPaused = true });
        using var json = JsonDocument.Parse(File.ReadAllLines(fixture.Journal)[^1]);
        Assert.True(json.RootElement.GetProperty("Snapshot").GetProperty("IsPaused").GetBoolean());
        var restored = Assert.Single(new UnifiedLootSessionStore(fixture.Root).Read().Sessions);
        Assert.Equal(LootTrackerSessionStatus.Interrupted, restored.Status); Assert.False(restored.IsPaused);
        Assert.Equal(live.Elapsed, restored.Elapsed); Assert.Equal(live.TimelineEvents.ToArray(), restored.TimelineEvents.ToArray());
    }

    [Fact]
    public void OldJournalsWithoutThePauseFieldRemainReadable()
    {
        using var fixture = new Fixture(); fixture.Store.Append(Session(fixture.Id));
        Assert.DoesNotContain("IsPaused", File.ReadAllText(fixture.Journal));
        var read = new UnifiedLootSessionStore(fixture.Root).Read();
        Assert.Empty(read.Issues); Assert.False(Assert.Single(read.Sessions).IsPaused);
    }

    [Theory]
    [InlineData(true, LootTrackerSessionStatus.Live)]
    [InlineData(false, LootTrackerSessionStatus.Completed)]
    [InlineData(false, LootTrackerSessionStatus.Interrupted)]
    public void OnlyALiveOutsideSessionMayPersistAPause(bool abyss, LootTrackerSessionStatus status)
    {
        using var fixture = new Fixture(); var session = Session(fixture.Id, abyss) with
        { IsPaused = true, Status = status, EndedAt = status == LootTrackerSessionStatus.Live ? null : Start };
        Assert.Throws<InvalidDataException>(() => fixture.Store.Append(session));
    }
}
