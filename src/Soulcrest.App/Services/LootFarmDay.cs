namespace Soulcrest.App.Services;

/// <summary>User request 2026-10-26: farm history resets at 07:00 UTC, independently of local DST.</summary>
public static class LootFarmDay
{
    public static DateOnly Get(DateTimeOffset at) => DateOnly.FromDateTime(at.UtcDateTime.AddHours(-7));
    public static DateTimeOffset Start(DateOnly day) => new(day.ToDateTime(new TimeOnly(7, 0)), TimeSpan.Zero);
    public static DateTimeOffset End(DateOnly day) => Start(day).AddDays(1);
}

/// <summary>A newly committed own drop. Recovery and presentation changes never publish this signal.</summary>
public sealed record AcceptedFarmLootDrop(string SessionId, string ProfileId, DateTimeOffset At,
    string LootKey, string Name, long Quantity)
{
}
