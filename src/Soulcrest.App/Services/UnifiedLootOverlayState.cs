using Soulcrest.App.Network;

namespace Soulcrest.App.Services;

/// <summary>One coherent own binding for overlay projection; independently read properties must not be joined.</summary>
public sealed record UnifiedLootOverlayState(LootTrackerSession? Session, bool TrackingAvailable,
    NetworkDailyMonsterKinaSnapshot Daily, SavedDailyMonsterKina? CachedDaily, bool DailyConfirmed, DateTimeOffset At)
{
    public static UnifiedLootOverlayState Empty { get; } = new(null, false, new(null, null, null, 0), null, false, default);
}
