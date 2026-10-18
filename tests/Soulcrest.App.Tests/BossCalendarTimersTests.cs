using System.Globalization;
using Soulcrest.App.Services;
using Soulcrest.Core.Alerts;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class BossCalendarTimersTests
{
    [Fact]
    public void ScreenshotInstantProducesAllSevenMissingLowerCalendarTimers()
    {
        var now = At("2026-10-18T13:56:01Z");
        var catalogue = LowerCatalogue();

        var rows = BossCalendarTimers.Create(catalogue, [], now).ToDictionary(row => row.Place.Boss.SpawnId);

        Assert.Equal(7, rows.Count);
        Assert.Equal(TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(59), rows[2002].Remaining);
        foreach (var id in new[] { 2003, 2004, 2005 })
            Assert.Equal(new TimeSpan(0, 5, 3, 59), rows[id].Remaining);
        foreach (var id in new[] { 2006, 2007, 2008 })
            Assert.Equal(new TimeSpan(1, 5, 33, 59), rows[id].Remaining);
        Assert.All(rows.Values, AssertCalendarOnly);
        Assert.All(catalogue, place => Assert.Same(place, rows[place.Boss.SpawnId].Place));
    }

    [Theory]
    [InlineData("2026-03-29T00:00:00Z", "2026-03-29T02:00:00Z")]
    [InlineData("2026-10-24T23:00:00Z", "2026-10-25T03:00:00Z")]
    [InlineData("2026-10-18T13:59:59Z", "2026-10-18T14:00:00Z")]
    [InlineData("2026-10-18T14:00:00Z", "2026-10-18T17:00:00Z")]
    [InlineData("2026-12-06T14:59:59Z", "2026-12-06T15:00:00Z")]
    public void DailySlotsInWeeklyBerlinRuleKeepLocalHoursAcrossDstAndSkipAnExactOccurrence(string instant, string target)
    {
        var now = At(instant);
        var row = Assert.Single(BossCalendarTimers.Create([Choice(2002, Kaira())], [], now));

        Assert.Equal(At(target) - now, row.Remaining);
        AssertCalendarOnly(row);
    }

    [Theory]
    [InlineData("2026-03-27T20:00:00Z", "2026-03-29T19:00:00Z")]
    [InlineData("2026-10-23T19:00:00Z", "2026-10-25T20:00:00Z")]
    [InlineData("2026-10-18T18:59:59Z", "2026-10-18T19:00:00Z")]
    [InlineData("2026-10-18T19:00:00Z", "2026-10-23T19:00:00Z")]
    [InlineData("2026-10-25T19:59:59Z", "2026-10-25T20:00:00Z")]
    public void WeeklyBerlinRulesUseLocalWeekdaysAndDstInsteadOfElapsedUtcDays(string instant, string target)
    {
        var now = At(instant);
        var row = Assert.Single(BossCalendarTimers.Create([Choice(2003, Nahma())], [], now));

        Assert.Equal(At(target) - now, row.Remaining);
        AssertCalendarOnly(row);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void EveryCapturedEntryWinsEvenWhenCachedSpawnedOrExpired(bool cached, bool spawned)
    {
        var catalogue = LowerCatalogue();
        var observed = new BossRushEntry(catalogue[0], spawned, TimeSpan.Zero, TimeSpan.FromDays(3), Cached: cached);
        BossRushEntry[] captured = [observed];

        var calendar = BossCalendarTimers.Create(catalogue, captured, At("2026-10-18T13:56:01Z"));

        Assert.Equal(6, calendar.Count);
        Assert.DoesNotContain(calendar, row => row.Place.Key == observed.Place.Key);
        Assert.Equal(7, calendar.Concat(captured).Select(row => row.Place.Key).Distinct().Count());
        Assert.Same(observed, Assert.Single(captured));
        Assert.Equal(cached, observed.Cached);
        Assert.Equal(spawned, observed.Spawned);
        Assert.Equal(TimeSpan.FromDays(3), observed.Age);
        Assert.All(calendar, AssertCalendarOnly);
    }

    [Fact]
    public void CapturedKeysUseMapAndSpawnIdWithoutSuppressingAnotherMapsSameSpawnId()
    {
        var first = Choice(2002, Kaira(), 20);
        var otherMap = Choice(2002, Kaira(), 22);
        var otherBoss = Choice(2003, Nahma(), 20);
        BossChoice[] catalogue = [first, otherMap, otherBoss];
        BossRushEntry[] captured = [new(first, false, TimeSpan.FromMinutes(20), TimeSpan.Zero, Cached: true)];

        var calendar = BossCalendarTimers.Create(catalogue, captured, At("2026-10-18T13:56:01Z"));

        Assert.Equal(2, calendar.Count);
        Assert.DoesNotContain(calendar, row => row.Place.Key == first.Key);
        Assert.Contains(calendar, row => row.Place.Key == otherMap.Key);
        Assert.Contains(calendar, row => row.Place.Key == otherBoss.Key);
        Assert.All(calendar, AssertCalendarOnly);
    }

    [Fact]
    public void FullyCapturedCatalogueNeedsNoCalendarAndUnrelatedCaptureDoesNotSuppressMissingTimers()
    {
        var now = At("2026-10-18T13:56:01Z");
        var catalogue = LowerCatalogue();
        var allCaptured = catalogue.Select(place => new BossRushEntry(place, false,
            TimeSpan.FromMinutes(10), TimeSpan.Zero)).ToArray();
        BossRushEntry[] unrelated = [new(Choice(999), false, TimeSpan.Zero, TimeSpan.Zero)];

        Assert.Empty(BossCalendarTimers.Create(catalogue, allCaptured, now));
        var missing = BossCalendarTimers.Create(catalogue, unrelated, now);
        Assert.Equal(7, missing.Count);
        Assert.All(missing, AssertCalendarOnly);
    }

    [Fact]
    public void MissingInvalidAndUnapprovedSchedulesNeverProduceCalendarEntries()
    {
        var now = At("2026-10-18T13:56:01Z");
        BossChoice[] catalogue =
        [
            Choice(1),
            Choice(2, new() { Kind = AlertScheduleKind.Daily }),
            Choice(3, new() { Kind = AlertScheduleKind.Weekly, Times = [new(21, 0)] }),
            Choice(4, Kaira() with { TimeZoneId = "Fixture/Unknown-Time-Zone" }),
            Choice(5, Kaira() with { Kind = (AlertScheduleKind)999 }),
            Choice(6, new() { Kind = AlertScheduleKind.Once, At = now.AddHours(1) }),
            Choice(7, Kaira() with { TimeZoneId = "UTC" }),
            Choice(8, Kaira() with { TimeZoneId = "W. Europe Standard Time" }),
            Choice(9, Kaira() with { Kind = AlertScheduleKind.Daily }),
            Choice(2002, Kaira())
        ];

        var row = Assert.Single(BossCalendarTimers.Create(catalogue, [], now));

        Assert.Equal(2002, row.Place.Boss.SpawnId);
        AssertCalendarOnly(row);
        Assert.Empty(BossCalendarTimers.Create([], [], now));
    }

    [Theory]
    [InlineData(-12)]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(14)]
    public void ExplicitNowInstantDeterminesCountdownsIndependentlyOfItsOffset(int offsetHours)
    {
        var now = At("2026-10-18T13:56:01Z");
        var catalogue = LowerCatalogue();
        var expected = BossCalendarTimers.Create(catalogue, [], now).ToDictionary(row => row.Place.Key);

        var actual = BossCalendarTimers.Create(catalogue, [], now.ToOffset(TimeSpan.FromHours(offsetHours)));

        Assert.Equal(expected.Count, actual.Count);
        Assert.All(actual, row =>
        {
            Assert.Equal(expected[row.Place.Key].Remaining, row.Remaining);
            AssertCalendarOnly(row);
        });
    }

    private static void AssertCalendarOnly(BossRushEntry row)
    {
        Assert.True(row.Calendar);
        Assert.False(row.Spawned);
        Assert.False(row.Cached);
        Assert.Equal(TimeSpan.Zero, row.Age);
        Assert.True(row.Remaining > TimeSpan.Zero);
    }

    private static BossChoice[] LowerCatalogue() =>
    [
        Choice(2002, Kaira()),
        Choice(2003, Nahma()), Choice(2004, Nahma()), Choice(2005, Nahma()),
        Choice(2006, Executors()), Choice(2007, Executors()), Choice(2008, Executors())
    ];

    private static BossChoice Choice(int spawnId, AlertSchedule? schedule = null, int mapId = 20) =>
        new(mapId, "fixture-map", new(spawnId, 1, "Fixture boss", null, null, null, CalendarSchedule: schedule));

    private static AlertSchedule Kaira() => new()
    {
        Kind = AlertScheduleKind.Weekly,
        TimeZoneId = "Europe/Berlin",
        Weekdays = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
            DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday],
        Times = [new(1, 0), new(4, 0), new(7, 0), new(10, 0), new(13, 0), new(16, 0), new(19, 0), new(22, 0)]
    };

    private static AlertSchedule Nahma() => new()
    {
        Kind = AlertScheduleKind.Weekly,
        TimeZoneId = "Europe/Berlin",
        Weekdays = [DayOfWeek.Friday, DayOfWeek.Sunday],
        Times = [new(21, 0)]
    };

    private static AlertSchedule Executors() => new()
    {
        Kind = AlertScheduleKind.Weekly,
        TimeZoneId = "Europe/Berlin",
        Weekdays = [DayOfWeek.Monday, DayOfWeek.Thursday, DayOfWeek.Saturday],
        Times = [new(21, 30)]
    };

    private static DateTimeOffset At(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
}
