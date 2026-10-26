using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class LootFarmDayTests
{
    [Theory]
    [InlineData("2026-10-25T06:59:59Z", "2026-10-24")]
    [InlineData("2026-10-25T07:00:00Z", "2026-10-25")]
    [InlineData("2026-03-29T06:59:59Z", "2026-03-28")]
    [InlineData("2026-03-29T07:00:00Z", "2026-03-29")]
    [InlineData("2026-10-26T08:59:59+02:00", "2026-10-25")]
    [InlineData("2026-10-26T09:00:00+02:00", "2026-10-26")]
    public void SevenUtcBoundaryIgnoresLocalOffsetsAndDst(string timestamp, string expected) =>
        Assert.Equal(DateOnly.Parse(expected), LootFarmDay.Get(DateTimeOffset.Parse(timestamp)));

    [Fact]
    public void FarmDayBoundsAreAlwaysTwentyFourHoursInUtc()
    {
        var day = new DateOnly(2026, 10, 25);
        Assert.Equal(DateTimeOffset.Parse("2026-10-25T07:00:00Z"), LootFarmDay.Start(day));
        Assert.Equal(DateTimeOffset.Parse("2026-10-26T07:00:00Z"), LootFarmDay.End(day));
        Assert.Equal(TimeSpan.FromDays(1), LootFarmDay.End(day) - LootFarmDay.Start(day));
    }
}
