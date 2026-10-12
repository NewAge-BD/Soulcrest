using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class EventTimelineTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 18, 5, 0, TimeSpan.Zero);

    [Fact]
    public void CalendarHasThreeCompactLanesWithAFullHourGridAndUpcomingFestivalStarts()
    {
        var snapshot = Create();
        Assert.Equal(3, snapshot.Lanes.Count);
        Assert.DoesNotContain(snapshot.Lanes, lane => lane.Bosses);
        Assert.Equal(Now, snapshot.Now);
        Assert.Equal(Now.AddMinutes(-5), snapshot.From);
        Assert.Equal(snapshot.From.AddHours(12), snapshot.Until);
        Assert.Equal(new[] { "shugo", "rift", "invasion" }, snapshot.Lanes.Select(lane => lane.Key));
        var shugo = Assert.Single(snapshot.Lanes, lane => lane.Key == "shugo");
        Assert.Equal(11, shugo.Occurrences.Count);
        Assert.Equal(Now.AddMinutes(55), shugo.Occurrences[0].StartsAt);
        Assert.All(shugo.Occurrences, item => Assert.Equal(item.StartsAt, item.EndsAt));
        Assert.All(shugo.Occurrences, occurrence =>
        {
            Assert.True(occurrence.EndsAt > snapshot.Now);
            Assert.True(occurrence.StartsAt < snapshot.Until);
        });
    }

    [Fact]
    public void ResetsCrossAllTracksAndSiegeKeepsItsIdentityInTheInvasionTrack()
    {
        var snapshot = Create(hours: 48);
        Assert.Equal(3, snapshot.Lanes.Count);
        Assert.DoesNotContain(snapshot.Lanes, lane => lane.Key is "daily-reset" or "weekly-reset" or "abyss");
        Assert.Equal(3, snapshot.Resets.Count);
        var weekly = Assert.Single(snapshot.Resets, item => item.Key.StartsWith("weekly-reset:", StringComparison.Ordinal));
        var daily = Assert.Single(snapshot.Resets, item => item.Key.StartsWith("daily-reset:", StringComparison.Ordinal) && item.StartsAt == weekly.StartsAt);
        Assert.NotEqual(daily.Title, weekly.Title);
        Assert.NotEqual(daily.Color, weekly.Color);
        var lane = Assert.Single(snapshot.Lanes, item => item.Key == "invasion");
        var siege = Assert.Single(lane.Occurrences, item => item.Key.StartsWith("abyss:", StringComparison.Ordinal));
        Assert.Equal(UiText.T("Abyss Siege"), siege.Title);
        Assert.Equal("#ef4444", siege.Color);
        Assert.Equal(TimeSpan.FromMinutes(30), siege.EndsAt - siege.StartsAt);
    }

    [Fact]
    public void BossSelectionWithoutCapturedTimesKeepsOneEmptyLaneAndNoInventedMarkers()
    {
        var snapshot = Create(selected: ["selected-but-not-captured"]);
        Assert.Empty(Assert.Single(snapshot.Lanes, lane => lane.Bosses).Occurrences);
        Assert.Equal(0, snapshot.CapturedBossCount);
    }

    [Fact]
    public void ActiveRiftIncludesItsPastStartAndFuturePortalClose()
    {
        var snapshot = Create();
        var rift = Assert.Single(snapshot.Lanes, lane => lane.Key == "rift");
        var active = rift.Occurrences[0];
        Assert.Equal(Now.AddMinutes(-5), active.StartsAt);
        Assert.Equal(Now.AddMinutes(5), active.PortalEndsAt);
        Assert.Equal(Now.AddMinutes(55), active.EndsAt);
        Assert.Equal(0, snapshot.Fraction(active.StartsAt));
        Assert.True(snapshot.Fraction(active.PortalEndsAt!.Value) > 0);
    }

    [Fact]
    public void UpcomingLaneCountdownUsesTheNearestOccurrence()
    {
        var invasion = Assert.Single(Create().Lanes, lane => lane.Key == "invasion");
        Assert.Equal(Now.AddMinutes(25), invasion.NextAt);
        Assert.Equal(invasion.NextAt, invasion.Occurrences[0].StartsAt);
    }

    [Theory]
    [InlineData(0, 12)]
    [InlineData(12, 12)]
    [InlineData(24, 24)]
    [InlineData(48, 48)]
    [InlineData(72, 12)]
    public void TimelineUsesOnlyTheThreeSupportedHorizons(int requested, int expected)
    {
        var snapshot = Create(hours: requested);
        Assert.Equal(expected, snapshot.Hours);
        Assert.Equal(snapshot.From.AddHours(expected), snapshot.Until);
        Assert.Equal(0, snapshot.Fraction(snapshot.From.AddSeconds(-1)));
        Assert.True(snapshot.Fraction(Now) > 0);
        Assert.Equal(1, snapshot.Fraction(snapshot.Until.AddSeconds(1)));
    }

    [Fact]
    public void OnlySelectedCapturedBossesAppearOnceWithoutForecastingTheirNextRespawn()
    {
        var first = Boss(1, TimeSpan.FromMinutes(10));
        first = first with { Place = first.Place with { Boss = first.Place.Boss with { Icon = "icons/fixture.png" } } };
        var second = Boss(2, TimeSpan.FromMinutes(20));
        var snapshot = Create([first, second], [first.Place.Key], 48);
        var lane = Assert.Single(snapshot.Lanes, item => item.Bosses);
        var occurrence = Assert.Single(lane.Occurrences);
        Assert.Equal(first.Place.Key, occurrence.Key);
        Assert.Equal("icons/fixture.png", occurrence.Icon);
        Assert.Equal(Now.AddMinutes(10), occurrence.StartsAt);
        Assert.Equal(occurrence.StartsAt, occurrence.EndsAt);
        Assert.False(occurrence.Provisional);
        Assert.Equal(1, snapshot.SelectedBossCount);
        Assert.Equal(1, snapshot.CapturedBossCount);
    }

    [Theory]
    [InlineData(true, 0, 600, false, true)]
    [InlineData(false, 15, 600, false, false)]
    [InlineData(false, 16, 600, false, true)]
    [InlineData(false, 0, 0, false, true)]
    [InlineData(false, 0, -1, false, true)]
    [InlineData(false, 0, 0, true, false)]
    [InlineData(true, 0, 0, true, true)]
    public void CachedOldAndExpiredBossTimesRemainUnconfirmed(bool cached, int age, int seconds, bool spawned, bool provisional)
    {
        var boss = Boss(1, TimeSpan.FromSeconds(seconds)) with { Cached = cached, Age = TimeSpan.FromSeconds(age), Spawned = spawned };
        var lane = Assert.Single(Create([boss], [boss.Place.Key]).Lanes, item => item.Bosses);
        var occurrence = Assert.Single(lane.Occurrences);
        Assert.Equal(provisional, occurrence.Provisional);
        Assert.Equal(spawned, occurrence.Spawned);
        Assert.True(occurrence.StartsAt >= Now);
    }

    [Fact]
    public void BossAtTheEndBoundaryWaitsForTheNextWindowAndUnknownSelectionCreatesNoTimer()
    {
        var boss = Boss(1, TimeSpan.FromHours(12));
        var snapshot = Create([boss], [boss.Place.Key, "unknown"]);
        Assert.Empty(Assert.Single(snapshot.Lanes, lane => lane.Bosses).Occurrences);
        Assert.Equal(2, snapshot.SelectedBossCount);
        Assert.Equal(1, snapshot.CapturedBossCount);
    }

    internal static EventTimelineSnapshot Create(IReadOnlyList<BossRushEntry>? bosses = null,
        IEnumerable<string>? selected = null, int hours = 12)
    {
        var path = Path.Combine(Path.GetTempPath(), "Soulcrest-timeline-test-" + Guid.NewGuid().ToString("N") + ".json");
        using var alerts = new AlertsService(path, new FixedTime());
        var snapshot = EventTimelinePresentation.Create(alerts.EventTimers, bosses ?? [], selected ?? [], Now, hours, "en");
        Assert.False(File.Exists(path));
        return snapshot;
    }

    internal static BossRushEntry Boss(int id, TimeSpan remaining) => new(
        new(1110, "fixture-map", new(id, id + 100, "Fixture boss " + id, "Beispielboss " + id, 10, 20)), false, remaining, TimeSpan.Zero);
    internal sealed class FixedTime : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
}
