using System.Collections.Immutable;

namespace Soulcrest.App.Services;

/// <summary>A subtotal of observed values. Unknown or invalid source values never become zero.</summary>
public sealed record LootTrackerDayAmount(long? Value, bool Complete);

public sealed record LootTrackerDayGroup(DateOnly Day, ImmutableArray<LootTrackerSession> Sessions,
    LootTrackerDayAmount KinaBound, LootTrackerDayAmount KinaUnbound,
    LootTrackerDayAmount Items, LootTrackerDayAmount Souls);

/// <summary>Groups every session type by the persisted farm day, with the same 07:00 UTC legacy boundary.</summary>
public static class LootTrackerDayPresentation
{
    public static DateOnly Day(LootTrackerSession session) => session.FarmDay ?? LootFarmDay.Get(session.StartedAt);

    public static ImmutableArray<LootTrackerDayGroup> Group(IEnumerable<LootTrackerSession> sessions) => sessions
        .GroupBy(Day).OrderByDescending(group => group.Key)
        .Select(group =>
        {
            var rows = group.OrderByDescending(session => session.StartedAt).ThenBy(session => session.Id, StringComparer.Ordinal).ToImmutableArray();
            return new LootTrackerDayGroup(group.Key, rows, Sum(rows.Select(row => row.KinaBound)),
                Sum(rows.Select(row => row.KinaUnbound)), Sum(rows.Select(row => row.ItemCount)), Sum(rows.Select(row => row.SoulCount)));
        }).ToImmutableArray();

    private static LootTrackerDayAmount Sum(IEnumerable<long?> values)
    {
        long total = 0;
        var any = false;
        var complete = true;
        foreach (var value in values)
        {
            if (value is not >= 0) { complete = false; continue; }
            if (total > long.MaxValue - value.Value) return new(null, false);
            total += value.Value;
            any = true;
        }
        return new(any ? total : null, any && complete);
    }
}
