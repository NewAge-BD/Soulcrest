using Soulcrest.Core.Alerts;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class EventWindowTests
{
    private static readonly AlertSchedule Hourly = new() { TimeZoneId = "UTC" };
    private static DateTimeOffset At(int hour, int minute, int second = 0) => new(2026, 10, 12, hour, minute, second, TimeSpan.Zero);

    [Fact]
    public void InactiveEventShowsNextStart()
    {
        var window = Assert.IsType<EventWindow>(EventWindows.At(Hourly, At(12, 30), TimeSpan.FromMinutes(10)));
        Assert.Equal(EventWindowPhase.Upcoming, window.Phase);
        Assert.False(window.IsActive);
        Assert.Equal(At(13, 0), window.StartsAt);
        Assert.Equal(At(13, 10), window.EndsAt);
        Assert.Equal(window.StartsAt, window.NextAt);
        Assert.Null(window.PortalEndsAt);
    }

    [Theory]
    [InlineData(0, EventWindowPhase.PortalOpen)]
    [InlineData(9, EventWindowPhase.PortalOpen)]
    [InlineData(10, EventWindowPhase.Active)]
    [InlineData(59, EventWindowPhase.Active)]
    public void PortalAndEventPhasesHaveIndependentDurations(int minute, EventWindowPhase phase)
    {
        var schedule = new AlertSchedule { Kind = AlertScheduleKind.Daily, TimeZoneId = "UTC", Times = [new(12, 0), new(15, 0)] };
        var window = Assert.IsType<EventWindow>(EventWindows.At(schedule, At(12, minute), TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(10)));
        Assert.Equal(phase, window.Phase);
        Assert.True(window.IsActive);
        Assert.Equal(At(12, 0), window.StartsAt);
        Assert.Equal(At(12, 10), window.PortalEndsAt);
        Assert.Equal(At(13, 0), window.EndsAt);
        Assert.Equal(At(15, 0), window.NextAt);
    }

    [Fact]
    public void EventEndIsExclusive()
    {
        var window = Assert.IsType<EventWindow>(EventWindows.At(Hourly, At(12, 10), TimeSpan.FromMinutes(10)));
        Assert.Equal(EventWindowPhase.Upcoming, window.Phase);
        Assert.Equal(At(13, 0), window.StartsAt);
    }

    [Fact]
    public void OverlappingEventsUseMostRecentOccurrence()
    {
        var window = Assert.IsType<EventWindow>(EventWindows.At(Hourly, At(12, 5), TimeSpan.FromMinutes(90)));
        Assert.Equal(At(12, 0), window.StartsAt);
        Assert.Equal(At(13, 30), window.EndsAt);
    }

    [Fact]
    public void InstantResetHasNoLivePhase()
    {
        var window = Assert.IsType<EventWindow>(EventWindows.At(Hourly, At(12, 0), TimeSpan.Zero));
        Assert.Equal(EventWindowPhase.Upcoming, window.Phase);
        Assert.Equal(At(13, 0), window.StartsAt);
        Assert.Equal(window.StartsAt, window.EndsAt);
    }

    [Fact]
    public void CompletedOnceRuleHasNoWindow()
    {
        var schedule = new AlertSchedule { Kind = AlertScheduleKind.Once, TimeZoneId = "UTC", At = At(12, 0) };
        var active = Assert.IsType<EventWindow>(EventWindows.At(schedule, At(12, 5), TimeSpan.FromMinutes(10)));
        Assert.True(active.IsActive);
        Assert.Null(active.NextAt);
        Assert.Null(EventWindows.At(schedule, At(12, 10), TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void FixedUtcResetIsIndependentOfBerlinDaylightSaving()
    {
        var schedule = new AlertSchedule { Kind = AlertScheduleKind.Daily, TimeZoneId = "UTC", Times = [new(7, 0)] };
        var summer = Assert.IsType<EventWindow>(EventWindows.At(schedule, new(2026, 10, 24, 6, 0, 0, TimeSpan.Zero), TimeSpan.Zero));
        var winter = Assert.IsType<EventWindow>(EventWindows.At(schedule, new(2026, 10, 25, 6, 0, 0, TimeSpan.Zero), TimeSpan.Zero));
        Assert.Equal(7, summer.StartsAt.Hour);
        Assert.Equal(7, winter.StartsAt.Hour);
    }

    [Fact]
    public void RepeatedHourlyHourHasTwoRealLiveWindows()
    {
        var schedule = new AlertSchedule { TimeZoneId = "Europe/Berlin" };
        var first = Assert.IsType<EventWindow>(EventWindows.At(schedule, new(2026, 10, 25, 0, 5, 0, TimeSpan.Zero), TimeSpan.FromMinutes(10)));
        var second = Assert.IsType<EventWindow>(EventWindows.At(schedule, new(2026, 10, 25, 1, 5, 0, TimeSpan.Zero), TimeSpan.FromMinutes(10)));
        Assert.Equal(EventWindowPhase.Active, first.Phase);
        Assert.Equal(EventWindowPhase.Active, second.Phase);
        Assert.Equal(TimeSpan.FromHours(1), second.StartsAt - first.StartsAt);
    }

    [Fact]
    public void TimelineIncludesFirstBoundaryAndExcludesLastBoundary()
    {
        Assert.Equal(new[] { At(12, 0), At(13, 0), At(14, 0) }, EventWindows.Starts(Hourly, At(12, 0), At(15, 0)));
        Assert.Equal(2, EventWindows.Starts(Hourly, At(12, 0), At(15, 0), 2).Count);
        Assert.Empty(EventWindows.Starts(Hourly, At(15, 0), At(12, 0)));
        Assert.Empty(EventWindows.Starts(Hourly, At(12, 0), At(15, 0), 0));
    }

    [Fact]
    public void InvalidRulesAndDurationsDoNotInventWindows()
    {
        Assert.Null(EventWindows.At(Hourly with { TimeZoneId = "invalid" }, At(12, 0), TimeSpan.FromMinutes(10)));
        Assert.Null(EventWindows.At(Hourly, At(12, 0), TimeSpan.FromMinutes(-1)));
        Assert.Null(EventWindows.At(Hourly, At(12, 0), TimeSpan.FromDays(8)));
        Assert.Null(EventWindows.At(Hourly, At(12, 0), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(11)));
        Assert.Null(EventWindows.At(Hourly, At(12, 0), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(-1)));
    }

    [Fact]
    public void DateBoundsDoNotOverflow()
    {
        Assert.NotNull(EventWindows.At(Hourly, DateTimeOffset.MinValue, TimeSpan.FromMinutes(10)));
        _ = EventWindows.At(Hourly, DateTimeOffset.MaxValue, TimeSpan.FromMinutes(10));
        Assert.Empty(EventWindows.Starts(Hourly, DateTimeOffset.MaxValue, DateTimeOffset.MaxValue));
    }
}
