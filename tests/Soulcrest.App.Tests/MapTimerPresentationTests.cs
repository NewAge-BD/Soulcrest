using Soulcrest.App.Services;
using Soulcrest.Core.Alerts;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class MapTimerPresentationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 18, 5, 0, TimeSpan.Zero);
    private static IReadOnlyList<EventTimerSnapshot> Events => EventCatalog.All.Select(definition => new EventTimerSnapshot(
        definition, EventWindows.At(definition.Schedule, Now, TimeSpan.FromMinutes(definition.DurationMinutes), TimeSpan.FromMinutes(definition.PortalMinutes))!,
        null, new())).ToArray();
    private static readonly BossChoice Choice = new(1, "fixture", new(2, 3, "Synthetic boss", "Testboss", 0, 0));

    [Fact]
    public void ShowsAllEventsWhenTheirRemindersAndWidgetsAreOff()
    {
        var rows = MapTimerPresentation.Create(Events, [], [], [], Now, "en");
        Assert.Equal(6, rows.Count);
        var festival = Assert.Single(rows, row => row.Key == "event:shugo");
        Assert.Equal("00:55:00", festival.Counter);
        Assert.Equal(UiText.T("Startet in"), festival.State);
        Assert.Contains(rows, row => row.Key == "event:daily-reset");
        Assert.Contains(rows, row => row.Key == "event:weekly-reset");
        Assert.Equal(UiText.T("Portal schließt in"), Assert.Single(rows, row => row.Key == "event:rift").State);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 3600)]
    [InlineData(true, 0)]
    [InlineData(true, 3600)]
    public void OnlySelectedBossesAppearAndCachedCountdownsStayProvisional(bool cached, int age)
    {
        var other = new BossChoice(1, "fixture", new(9, 3, "Other synthetic boss", null, 0, 0));
        var captured = new[] { new BossRushEntry(Choice, false, TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(age), cached),
            new BossRushEntry(other, false, TimeSpan.FromMinutes(1), TimeSpan.Zero) };
        var rows = MapTimerPresentation.Create(Events, captured, [Choice, other], [Choice.Key], Now, "de");
        var boss = Assert.Single(rows, row => row.Key.StartsWith("boss:", StringComparison.Ordinal));
        Assert.Equal("Testboss", boss.Title);
        Assert.Equal("00:02:00", boss.Counter);
        Assert.Equal(cached, boss.Provisional);
        Assert.Equal(7, rows.Count);
    }

    [Fact]
    public void SelectedUncapturedBossShowsWaitingInsteadOfInventingASpawn()
    {
        var rows = MapTimerPresentation.Create([], [], [Choice], [Choice.Key], Now, "en");
        var boss = Assert.Single(rows);
        Assert.Equal("—", boss.Counter);
        Assert.Equal(UiText.T("Warte auf Boss-Timer"), boss.State);
        Assert.Null(boss.At);
        Assert.True(boss.Provisional);
    }

    [Theory]
    [InlineData(10, 25, 0)]
    [InlineData(10, 25, 1)]
    [InlineData(12, 31, 23)]
    public void TimelineGridRemainsOnFullHoursAcrossDstAndDateBoundaries(int month, int day, int hour)
    {
        var now = new DateTimeOffset(2026, month, day, hour, 47, 29, TimeSpan.Zero);
        var snapshot = EventTimelinePresentation.Create([], [], [], now, 24, "en");
        Assert.Equal(now, snapshot.Now);
        Assert.Equal(0, snapshot.From.Minute);
        Assert.Equal(0, snapshot.From.Second);
        Assert.Equal(TimeSpan.FromHours(24), snapshot.Until - snapshot.From);
        Assert.Equal(0, snapshot.Until.Minute);
        Assert.InRange(snapshot.Fraction(now), 0.03, 0.04);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        for (var index = 0; index <= 24; index++)
            Assert.Equal(0, TimeZoneInfo.ConvertTime(snapshot.From.AddHours(index), zone).Minute);
    }
}
