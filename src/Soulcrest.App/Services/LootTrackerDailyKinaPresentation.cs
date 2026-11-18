using Soulcrest.App.Overlay;

namespace Soulcrest.App.Services;

/// <summary>Only confirmed current-farm-day PvE Bound receipts can explain a part of the daily bar.</summary>
internal static class LootTrackerDailyKinaPresentation
{
    internal static long? SessionContribution(LootTrackerSession? session, long? daily, bool confirmed,
        bool trackingAvailable, DateTimeOffset now)
    {
        if (!confirmed || !trackingAvailable || now == default || daily is not (> 0 and < LootOverlaySnapshot.DailyLimit)
            || session is not { Status: LootTrackerSessionStatus.Live, EndedAt: null }
            || session.Kind is LootTrackerSessionKind.Abyss or LootTrackerSessionKind.Lifeskill) return null;
        var total = 0L;
        foreach (var fact in session.TimelineEvents)
        {
            if (fact.Origin != LootTrackerActivityOrigin.Pve || fact.Kind != LootTrackerTimelineEventKind.KinaBound
                || fact.Amount is not > 0 || fact.At == default || fact.At > now
                || LootFarmDay.Get(fact.At) != LootFarmDay.Get(now)) continue;
            // Clamp before adding: a malformed or older partial counter cannot overflow or exceed the confirmed day.
            total += Math.Min(fact.Amount.Value, daily.Value - total);
            if (total == daily.Value) break;
        }
        return total > 0 ? total : null;
    }
}
