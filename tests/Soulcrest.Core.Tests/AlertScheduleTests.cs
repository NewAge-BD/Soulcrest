using Soulcrest.Core.Alerts;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class AlertScheduleTests
{
    [Fact]
    public void HourlyIsStrictlyLaterAndUsesUtcOutput()
    {
        var rule = new AlertSchedule { Minute = 15 };
        AssertNext(rule, "2026-10-12T14:14:59+02:00", "2026-10-12T12:15:00Z");
        AssertNext(rule, "2026-10-12T14:15:00+02:00", "2026-10-12T13:15:00Z");
    }

    [Fact]
    public void HourlyUsesLocalHourInHalfHourZone()
    {
        var rule = new AlertSchedule { TimeZoneId = "Asia/Kolkata", Minute = 0 };
        AssertNext(rule, "2026-10-12T12:31:00Z", "2026-10-12T13:30:00Z");
    }

    [Fact]
    public void HourlySkipsNonexistentSpringHour()
    {
        var rule = new AlertSchedule { Minute = 30 };
        AssertNext(rule, "2026-03-29T00:30:00Z", "2026-03-29T01:30:00Z");
    }

    [Fact]
    public void HourlyIncludesBothInstantsOfRepeatedHour()
    {
        var rule = new AlertSchedule { Minute = 30 };
        AssertNext(rule, "2026-10-25T00:29:00Z", "2026-10-25T00:30:00Z");
        AssertNext(rule, "2026-10-25T00:30:00Z", "2026-10-25T01:30:00Z");
        AssertNext(rule, "2026-10-25T01:30:00Z", "2026-10-25T02:30:00Z");
    }

    [Fact]
    public void DailySortsDeduplicatesAndCrossesMidnight()
    {
        var rule = Daily(new TimeOnly(21, 0), new TimeOnly(8, 0), new TimeOnly(8, 0));
        AssertNext(rule, "2026-10-12T04:00:00Z", "2026-10-12T06:00:00Z");
        AssertNext(rule, "2026-10-12T06:00:00Z", "2026-10-12T19:00:00Z");
        AssertNext(rule, "2026-10-12T19:00:00Z", "2026-10-13T06:00:00Z");
    }

    [Fact]
    public void DailySkipsNonexistentTimeWithoutMovingItForward()
    {
        var rule = Daily(new TimeOnly(2, 30));
        AssertNext(rule, "2026-03-28T01:30:00Z", "2026-03-30T00:30:00Z");
    }

    [Fact]
    public void DailyRepeatedHourOccursOnlyAtItsEarlierInstant()
    {
        var rule = Daily(new TimeOnly(2, 30));
        AssertNext(rule, "2026-10-25T00:29:00Z", "2026-10-25T00:30:00Z");
        AssertNext(rule, "2026-10-25T00:30:00Z", "2026-10-26T01:30:00Z");
        AssertNext(rule, "2026-10-25T01:00:00Z", "2026-10-26T01:30:00Z");
    }

    [Theory]
    [InlineData("Europe/Berlin")]
    [InlineData("W. Europe Standard Time")]
    public void IanaAndWindowsZonesDescribeTheSameCalendar(string zone)
    {
        var rule = Daily(new TimeOnly(21, 0)) with { TimeZoneId = zone };
        Assert.Null(AlertSchedules.Error(rule));
        AssertNext(rule, "2026-10-12T18:00:00Z", "2026-10-12T19:00:00Z");
    }

    [Fact]
    public void CalendarUsesItsExplicitZoneInsteadOfInputOffset()
    {
        var rule = Daily(new TimeOnly(0, 15)) with { TimeZoneId = "Asia/Seoul" };
        AssertNext(rule, "2026-10-12T17:00:00+02:00", "2026-10-12T15:15:00Z");
    }

    [Fact]
    public void WeeklyUsesLocalWeekday()
    {
        var rule = Daily(new TimeOnly(0, 15)) with
        {
            Kind = AlertScheduleKind.Weekly,
            TimeZoneId = "Asia/Seoul",
            Weekdays = [DayOfWeek.Monday]
        };
        AssertNext(rule, "2026-10-11T15:00:00Z", "2026-10-11T15:15:00Z");
        AssertNext(rule, "2026-10-11T15:15:00Z", "2026-10-18T15:15:00Z");
    }

    [Fact]
    public void WeeklyAcceptsMultipleDaysAndUnorderedTimes()
    {
        var rule = Daily(new TimeOnly(21, 0), new TimeOnly(8, 0)) with
        {
            Kind = AlertScheduleKind.Weekly,
            Weekdays = [DayOfWeek.Friday, DayOfWeek.Monday, DayOfWeek.Friday]
        };
        AssertNext(rule, "2026-10-12T19:00:00Z", "2026-10-16T06:00:00Z");
    }

    [Fact]
    public void WeeklySkipsNonexistentTimeAndUsesOnlyEarlierRepeatedTime()
    {
        var rule = Daily(new TimeOnly(2, 30)) with
        {
            Kind = AlertScheduleKind.Weekly,
            Weekdays = [DayOfWeek.Sunday]
        };
        AssertNext(rule, "2026-03-22T01:30:00Z", "2026-04-05T00:30:00Z");
        AssertNext(rule, "2026-10-25T00:30:00Z", "2026-11-01T01:30:00Z");
    }

    [Fact]
    public void EveryNDaysStartsAtAnchorAndKeepsItsCalendarParity()
    {
        var rule = EveryDays(new DateOnly(2026, 10, 12), 2, new TimeOnly(21, 0));
        AssertNext(rule, "2026-10-10T19:00:00Z", "2026-10-12T19:00:00Z");
        AssertNext(rule, "2026-10-12T18:59:00Z", "2026-10-12T19:00:00Z");
        AssertNext(rule, "2026-10-12T19:00:00Z", "2026-10-14T19:00:00Z");
        AssertNext(rule, "2026-10-13T00:00:00Z", "2026-10-14T19:00:00Z");
    }

    [Fact]
    public void EveryNDaysUsesCalendarDatesRatherThanFortyEightHoursAcrossDst()
    {
        var rule = EveryDays(new DateOnly(2026, 10, 24), 2, new TimeOnly(21, 0));
        AssertNext(rule, "2026-10-24T19:00:00Z", "2026-10-26T20:00:00Z");
        rule = rule with { AnchorDate = new DateOnly(2026, 3, 28) };
        AssertNext(rule, "2026-03-28T20:00:00Z", "2026-03-30T19:00:00Z");
    }

    [Fact]
    public void EveryNDaysSkipsInvalidTimeWithoutChangingItsAnchor()
    {
        var rule = EveryDays(new DateOnly(2026, 3, 29), 2, new TimeOnly(2, 30));
        AssertNext(rule, "2026-03-28T00:00:00Z", "2026-03-31T00:30:00Z");
    }

    [Fact]
    public void EveryNDaysRepeatedTimeOccursOnlyOnce()
    {
        var rule = EveryDays(new DateOnly(2026, 10, 25), 2, new TimeOnly(2, 30));
        AssertNext(rule, "2026-10-25T00:29:00Z", "2026-10-25T00:30:00Z");
        AssertNext(rule, "2026-10-25T00:30:00Z", "2026-10-27T01:30:00Z");
    }

    [Fact]
    public void AnnualCalendarRuleCanSkipInvalidOccurrence()
    {
        var rule = EveryDays(new DateOnly(2026, 3, 29), 365, new TimeOnly(2, 30));
        AssertNext(rule, "2026-03-28T00:00:00Z", "2027-03-29T00:30:00Z");
    }

    [Fact]
    public void OnceKeepsAbsoluteInstantAndExpiresExactlyAtIt()
    {
        var rule = new AlertSchedule { Kind = AlertScheduleKind.Once, At = Instant("2026-10-12T21:00:00+09:00") };
        AssertNext(rule, "2026-10-12T11:00:00Z", "2026-10-12T12:00:00Z");
        Assert.Null(AlertSchedules.Next(rule, Instant("2026-10-12T12:00:00Z")));
        Assert.Null(AlertSchedules.Next(rule, Instant("2026-10-13T12:00:00Z")));
    }

    [Fact]
    public void IntervalUsesElapsedMinutesAndStrictFutureBoundary()
    {
        var rule = new AlertSchedule
        {
            Kind = AlertScheduleKind.Interval,
            At = Instant("2026-10-24T21:00:00+02:00"),
            IntervalMinutes = 48 * 60
        };
        AssertNext(rule, "2026-10-24T18:00:00Z", "2026-10-24T19:00:00Z");
        AssertNext(rule, "2026-10-24T19:00:00Z", "2026-10-26T19:00:00Z");
        AssertNext(rule, "2026-11-01T19:00:00Z", "2026-11-03T19:00:00Z");
    }

    [Fact]
    public void IntervalRetainsAnchorSecondsAndSkipsElapsedOccurrences()
    {
        var rule = new AlertSchedule
        {
            Kind = AlertScheduleKind.Interval,
            At = Instant("2026-01-01T00:00:12.345Z"),
            IntervalMinutes = 17
        };
        AssertNext(rule, "2026-01-01T00:51:12.345Z", "2026-01-01T01:08:12.345Z");
    }

    [Fact]
    public void MaxDateAndHugeIntervalsDoNotOverflow()
    {
        var rules = new[]
        {
            new AlertSchedule(),
            Daily(new TimeOnly(21, 0)),
            Daily(new TimeOnly(21, 0)) with { Kind = AlertScheduleKind.Weekly, Weekdays = [DayOfWeek.Monday] },
            EveryDays(DateOnly.MaxValue, int.MaxValue, new TimeOnly(21, 0)),
            new AlertSchedule { Kind = AlertScheduleKind.Interval, At = DateTimeOffset.MinValue, IntervalMinutes = int.MaxValue },
            new AlertSchedule { Kind = AlertScheduleKind.Once, At = DateTimeOffset.MaxValue }
        };
        foreach (var rule in rules)
        {
            Assert.Null(AlertSchedules.Error(rule));
            Assert.Null(AlertSchedules.Next(rule, DateTimeOffset.MaxValue));
        }
    }

    [Fact]
    public void MinDateWithNegativeOrPositiveZoneDoesNotOverflow()
    {
        var rule = Daily(new TimeOnly(0, 0)) with { TimeZoneId = "UTC" };
        AssertNext(rule, "0001-01-01T00:00:00Z", "0001-01-02T00:00:00Z");
        foreach (var zone in new[] { "Europe/Berlin", "America/New_York" })
        {
            var next = AlertSchedules.Next(rule with { TimeZoneId = zone }, DateTimeOffset.MinValue);
            Assert.NotNull(next);
            Assert.True(next > DateTimeOffset.MinValue);
            Assert.Equal(TimeSpan.Zero, next.Value.Offset);
        }
    }

    [Fact]
    public void FinalRepresentableCalendarAndAbsoluteInstantsRemainAvailable()
    {
        var rule = Daily(TimeOnly.MaxValue) with { TimeZoneId = "UTC" };
        Assert.Equal(DateTimeOffset.MaxValue, AlertSchedules.Next(rule, DateTimeOffset.MaxValue.AddTicks(-1)));
        var interval = new AlertSchedule { Kind = AlertScheduleKind.Interval, At = DateTimeOffset.MaxValue };
        Assert.Equal(DateTimeOffset.MaxValue, AlertSchedules.Next(interval, DateTimeOffset.MaxValue.AddTicks(-1)));
    }

    [Fact]
    public void DistantAnchorAndLargeCycleAreCalculatedWithoutWalkingAllDays()
    {
        var rule = EveryDays(new DateOnly(9000, 1, 1), int.MaxValue, new TimeOnly(12, 0)) with { TimeZoneId = "UTC" };
        AssertNext(rule, "2026-01-01T00:00:00Z", "9000-01-01T12:00:00Z");
        Assert.Null(AlertSchedules.Next(rule, Instant("9000-01-01T12:00:00Z")));
    }

    [Theory]
    [InlineData("unknown-zone")]
    [InlineData("")]
    [InlineData(" ")]
    public void InvalidZoneNeverFallsBackToComputerZone(string zone)
    {
        var rule = new AlertSchedule { TimeZoneId = zone };
        Assert.Equal("time-zone", AlertSchedules.Error(rule));
        Assert.Null(AlertSchedules.Next(rule, Instant("2026-10-12T00:00:00Z")));
    }

    private static (AlertSchedule Rule, string Error)[] InvalidRules =>
    [
        (null!, "schedule"),
        (new AlertSchedule { Kind = (AlertScheduleKind)500 }, "kind"),
        (new AlertSchedule { TimeZoneId = null! }, "time-zone"),
        (new AlertSchedule { Minute = -1 }, "minute"),
        (new AlertSchedule { Minute = 60 }, "minute"),
        (new AlertSchedule { Kind = AlertScheduleKind.Daily }, "times"),
        (new AlertSchedule { Kind = AlertScheduleKind.Daily, Times = null! }, "times"),
        (Daily(new TimeOnly(12, 0)) with { Kind = AlertScheduleKind.Weekly }, "weekdays"),
        (Daily(new TimeOnly(12, 0)) with { Kind = AlertScheduleKind.Weekly, Weekdays = null! }, "weekdays"),
        (Daily(new TimeOnly(12, 0)) with { Kind = AlertScheduleKind.Weekly, Weekdays = [(DayOfWeek)20] }, "weekdays"),
        (Daily(new TimeOnly(12, 0)) with { Kind = AlertScheduleKind.EveryNDays }, "anchor-date"),
        (EveryDays(new DateOnly(2026, 10, 12), 0, new TimeOnly(21, 0)), "every-days"),
        (EveryDays(new DateOnly(2026, 10, 12), -2, new TimeOnly(21, 0)), "every-days"),
        (new AlertSchedule { Kind = AlertScheduleKind.Interval }, "at"),
        (new AlertSchedule { Kind = AlertScheduleKind.Interval, At = DateTimeOffset.MinValue, IntervalMinutes = 0 }, "interval-minutes"),
        (new AlertSchedule { Kind = AlertScheduleKind.Interval, At = DateTimeOffset.MinValue, IntervalMinutes = -1 }, "interval-minutes"),
        (new AlertSchedule { Kind = AlertScheduleKind.Once }, "at")
    ];

    [Fact]
    public void InvalidRulesReturnCodesAndNoNextOccurrence()
    {
        foreach (var (rule, error) in InvalidRules)
        {
            Assert.Equal(error, AlertSchedules.Error(rule));
            Assert.Null(AlertSchedules.Next(rule, Instant("2026-10-12T00:00:00Z")));
        }
    }

    private static AlertSchedule Daily(params TimeOnly[] times) => new() { Kind = AlertScheduleKind.Daily, Times = times };
    private static AlertSchedule EveryDays(DateOnly anchor, int days, params TimeOnly[] times) =>
        Daily(times) with { Kind = AlertScheduleKind.EveryNDays, AnchorDate = anchor, EveryDays = days };
    private static DateTimeOffset Instant(string value) => DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    private static void AssertNext(AlertSchedule rule, string after, string expected)
    {
        var next = AlertSchedules.Next(rule, Instant(after));
        Assert.NotNull(next);
        Assert.Equal(Instant(expected), next.Value);
        Assert.Equal(TimeSpan.Zero, next.Value.Offset);
    }
}
