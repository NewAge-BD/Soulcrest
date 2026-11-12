using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed partial class UnifiedLootTrackerServiceTests
{
    [Theory]
    [InlineData("unknown", true)]
    [InlineData("transition", true)]
    [InlineData("stale", true)]
    [InlineData("binding", true)]
    [InlineData("future", true)]
    [InlineData("ready-without-session", false)]
    [InlineData("instance", false)]
    [InlineData("unclassified-map", false)]
    [InlineData("paused", false)]
    [InlineData("stopped", false)]
    [InlineData("unbound", false)]
    [InlineData("no-own", false)]
    [InlineData("tracking-off", false)]
    [InlineData("channels-off", false)]
    [InlineData("unverified-gathering", false)]
    [InlineData("writer-failed", false)]
    public async Task MissingWorldContextHintRequiresAnOtherwiseEnabledBoundRunningLootSource(string reason, bool expected)
    {
        await using var f = new Fixture(); await f.Ready();
        switch (reason)
        {
            case "unknown": f.WorldStatus = WorldContextStatus.Unknown; break;
            case "transition": f.WorldStatus = WorldContextStatus.Transitioning; break;
            case "stale": f.WorldStatus = WorldContextStatus.Stale; break;
            case "binding": f.BindingOffset = 1; break;
            case "future": f.WorldOffset = TimeSpan.FromSeconds(1); break;
            case "instance": f.WorldKind = WorldContextKind.Instance; f.Category = "Dungeon"; break;
            case "unclassified-map": f.WorldKind = WorldContextKind.Unknown; break;
            case "paused":
                f.Source.Drop(7, 1, 1); await f.Ready();
                f.Clock.Advance(TimeSpan.FromSeconds(21)); await f.Ready();
                Assert.True(Assert.Single(f.Service.Snapshot.Sessions).IsPaused);
                break;
            case "stopped": f.WorldStatus = WorldContextStatus.Unknown; f.Source.Running = false; break;
            case "unbound": f.WorldStatus = WorldContextStatus.Unknown; f.Bound = false; break;
            case "no-own": f.WorldStatus = WorldContextStatus.Unknown; f.Source.LoseOwn(); break;
            case "tracking-off": f.WorldStatus = WorldContextStatus.Unknown; f.Settings.Current.LootTrackingEnabled = false; break;
            case "channels-off":
                f.WorldStatus = WorldContextStatus.Unknown;
                f.Settings.Current.MonsterLootTrackingEnabled = f.Settings.Current.LifeskillTrackingEnabled = f.Settings.Current.AbyssTrackingEnabled = false;
                break;
            case "unverified-gathering":
                f.WorldStatus = WorldContextStatus.Unknown;
                f.Settings.Current.MonsterLootTrackingEnabled = f.Settings.Current.AbyssTrackingEnabled = false;
                f.Source.GatheringVerified = false;
                break;
            case "writer-failed":
                f.FailAppend = true; f.Source.Drop(7, 1, 1); await f.Ready();
                f.WorldStatus = WorldContextStatus.Unknown;
                break;
        }
        f.Source.Notify(); await f.Ready();
        Assert.Equal(expected, f.Service.OverlayState.WaitingForWorldContext);
        if (expected)
        {
            Assert.False(f.Service.OverlayState.TrackingAvailable);
            Assert.Null(f.Service.OverlayState.Session);
            Assert.Empty(f.Accepted);
            Assert.Empty(f.Journal);
            Assert.True(f.Source.Running);
        }
    }

    [Fact]
    public async Task ConfirmedWorldClearsTheHintWithoutReplayingDropsFromBeforeItsConfirmation()
    {
        await using var f = new Fixture(); f.WorldStatus = WorldContextStatus.Unknown;
        f.Source.Notify(); await f.Ready();
        Assert.True(f.Service.OverlayState.WaitingForWorldContext);
        f.Source.Drop(7, 2, 1); await f.Ready();
        Assert.Empty(f.Service.Snapshot.Sessions);
        f.WorldStatus = WorldContextStatus.Confirmed; f.WorldGeneration++; f.Source.Notify(); await f.Ready();
        Assert.False(f.Service.OverlayState.WaitingForWorldContext);
        Assert.True(f.Service.OverlayState.TrackingAvailable);
        Assert.Empty(f.Service.Snapshot.Sessions);
        f.Source.Drop(7, 3, 2); await f.Ready();
        Assert.Equal(3, Assert.Single(f.Service.Snapshot.Sessions).ItemCount);
        Assert.Equal(3, Assert.Single(f.Accepted).Quantity);
    }
}
