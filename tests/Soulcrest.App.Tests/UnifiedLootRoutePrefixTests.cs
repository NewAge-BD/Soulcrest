using System.Reflection;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed partial class UnifiedLootTrackerServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QualifiedBackdatedHarvestCannotReinsertAProvisionalPointInsideTheDurablePrefix(bool newSegment)
    {
        await using var f = new Fixture();
        f.WorldOffset = TimeSpan.FromSeconds(-2);
        var evidence = f.Clock.GetUtcNow();
        var mapGeneration = 1L;
        f.Position = () => new(new("sample-map", 10, 20, evidence) { ContextGeneration = mapGeneration },
            mapGeneration, f.WorldGeneration, f.Source.OwnCharacter!.Generation, evidence, 30, 1);
        await f.Ready();
        f.Source.Drop(7, 1, 1); await f.Ready();

        evidence = f.Clock.GetUtcNow().AddSeconds(9);
        f.Clock.Advance(TimeSpan.FromSeconds(10)); await f.Ready();
        var walkingAt = f.Clock.GetUtcNow();
        var factAt = walkingAt.AddMilliseconds(-500);
        evidence = factAt;
        if (newSegment) mapGeneration++;
        typeof(UnifiedLootTrackerService).GetMethod("Gathered", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(f.Service,
            [new NetworkGathering(factAt, 1, f.Source.OwnCharacter!, 123, [new(610620013, 2)])]);
        await f.Ready();
        Assert.Null(f.Service.RecordingFault);
        var stored = Assert.Single(new UnifiedLootSessionStore(f.SessionRoot).Read().Sessions);
        Assert.DoesNotContain(stored.RouteSegments.SelectMany(segment => segment.Points), point => point.At == walkingAt);
        Assert.Contains(stored.RouteSegments.SelectMany(segment => segment.Points), point => point.At == factAt);
        var committedRoute = stored.RouteSegments;
        var journal = f.Journal.ToArray();

        f.Clock.Advance(TimeSpan.FromSeconds(1), fireTimers: false);
        evidence = f.Clock.GetUtcNow();
        f.Source.Drop(7, 1, 2); await f.Ready();
        Assert.Null(f.Service.RecordingFault);
        Assert.True(f.Service.OverlayState.TrackingAvailable);
        Assert.Equal(stored.Id, f.Service.OverlayState.Session?.Id);
        var read = new UnifiedLootSessionStore(f.SessionRoot).Read();
        Assert.Empty(read.Issues);
        var recovered = Assert.Single(read.Sessions);
        Assert.Equal(4, recovered.ItemCount);
        Assert.Equal(committedRoute.Length, recovered.RouteSegments.Length);
        Assert.Equal(journal, f.Journal.Take(journal.Length));
        for (var segment = 0; segment < committedRoute.Length; segment++)
            Assert.Equal(committedRoute[segment].Points,
                recovered.RouteSegments[segment].Points.Take(committedRoute[segment].Points.Length));

        f.Clock.Advance(TimeSpan.FromSeconds(20)); await f.Ready();
        Assert.Null(f.Service.RecordingFault);
        Assert.True(Assert.Single(f.Service.Snapshot.Sessions).IsPaused);
        f.Clock.Advance(TimeSpan.FromSeconds(1), fireTimers: false);
        evidence = f.Clock.GetUtcNow();
        f.Source.Drop(7, 1, 3); await f.Ready();
        Assert.Null(f.Service.RecordingFault);
        Assert.Equal(5, f.Service.OverlayState.Session?.ItemCount);
        Assert.True(f.Service.OverlayState.TrackingAvailable);
    }
}
