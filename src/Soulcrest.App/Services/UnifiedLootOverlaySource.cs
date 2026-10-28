using Soulcrest.App.Overlay;

namespace Soulcrest.App.Services;

/// <summary>Always projects the mixed current session. A historical detail-view filter cannot change the live HUD.</summary>
public sealed class UnifiedLootOverlaySource : ILootOverlaySource, IDisposable
{
    private readonly UnifiedLootTrackerService _source;
    private readonly AbyssProgressService _abyss;
    private readonly SettingsService _settings;

    public UnifiedLootOverlaySource(UnifiedLootTrackerService source, AbyssProgressService abyss, SettingsService settings)
    {
        _source = source; _abyss = abyss; _settings = settings;
        source.Changed += Refresh; abyss.Changed += Refresh; settings.Changed += Refresh;
    }

    public LootOverlaySnapshot Snapshot => Project(_source.OverlayState, _abyss.ProgressSnapshot, _settings.Current);
    public event Action? Changed;

    internal static LootOverlaySnapshot Project(UnifiedLootOverlayState state, AbyssProgressSnapshot progress, AppSettings settings)
    {
        var session = state.TrackingAvailable && state.Session is { Status: LootTrackerSessionStatus.Live, EndedAt: null }
            ? state.Session : null;
        var elapsed = session?.Elapsed ?? TimeSpan.Zero;
        var profile = session?.Character?.ProfileId ?? state.CachedDaily?.ProfileId;
        if (session?.Character?.ProfileId != progress.ProfileId)
            progress = AbyssProgressSnapshot.Empty with { Cap = progress.Cap };
        var drops = session?.Loot.Select(item => new LootOverlayDrop(item.Id, item.Name, item.Quantity,
            item.Kind == LootTrackerLootKind.Soul ? LootOverlayDropKind.SoulCrystal : LootOverlayDropKind.Item)
        {
            IconPath = MonsterLootIconCatalog.Default.DropAsset(item.Id),
            Rarity = MonsterLootItemCatalog.Default.DropRarity(item.Id),
        }).ToArray() ?? [];
        var pve = session is null ? state.TrackingAvailable && state.CachedDaily is not null
            : session.TimelineEvents.Any(fact => fact.Origin == LootTrackerActivityOrigin.Pve
                && fact.Kind is LootTrackerTimelineEventKind.Drop or LootTrackerTimelineEventKind.KinaBound or LootTrackerTimelineEventKind.KinaUnbound);
        var life = session?.TimelineEvents.Any(fact => fact.Kind is LootTrackerTimelineEventKind.HarvestSuccess
            or LootTrackerTimelineEventKind.HarvestFailure) == true;
        return new(session?.Id, session is not null, session?.KinaUnbound ?? 0,
            state.CachedDaily?.Total, LootOverlayDropPresentation.GroupCommonGear(drops), state.TrackingAvailable,
            profile is null || state.At == default ? null : $"{profile}:{LootFarmDay.Get(state.At):yyyy-MM-dd}")
        {
            SessionKind = session?.Kind ?? LootTrackerSessionKind.Mixed,
            HasPveActivity = pve, HasLifeskillActivity = life,
            HasAbyssPointActivity = session?.TimelineEvents.Any(fact =>
                fact.Kind == LootTrackerTimelineEventKind.AbyssPoints && fact.Amount is > 0) == true,
            HarvestCount = life ? session?.HarvestCount : null,
            FailedHarvestCount = life ? session?.FailedHarvestCount : null,
            HarvestsPerHour = life ? LootTrackerPresentation.PerHour(session?.HarvestCount, elapsed) : null,
            ItemCount = session?.ItemCount,
            SessionKinaKnown = session?.KinaUnbound is not null,
            SessionKinaPerHour = LootTrackerPresentation.PerHour(session?.KinaUnbound, elapsed),
            SessionAbyssPoints = session?.AbyssPoints,
            SessionAbyssPointsRate = settings.AbyssPointRatePeriod == AbyssPointRatePeriod.Minute
                ? PerMinute(session?.AbyssPoints, elapsed) : LootTrackerPresentation.PerHour(session?.AbyssPoints, elapsed),
            AbyssPointRatePeriod = settings.AbyssPointRatePeriod,
            FarmedAbyssPoints = progress.FarmedAbyssPoints, AbyssPointCap = progress.Cap,
            AbyssFarmConfirmed = progress.FarmedAbyssPointsConfirmed,
            DailyConfirmed = state.DailyConfirmed,
            LimitedKinaSession = session?.Mode == LootTrackerSessionMode.LimitedKina,
            KinaIconPath = MonsterLootIconCatalog.Default.ItemAsset(930100031),
            AbyssPointIconPath = MonsterLootIconCatalog.Default.ItemAsset(930100003),
            AnimationsEnabled = settings.LootDropAnimationsEnabled,
            SessionElapsed = elapsed,
        };
    }

    private static double? PerMinute(long? amount, TimeSpan elapsed) => amount is null || elapsed <= TimeSpan.Zero
        ? null : amount.Value / elapsed.TotalMinutes;
    private void Refresh() => SafeEvent.Raise(Changed, "Gemeinsames Loot-Overlay");
    public void Dispose()
    {
        _source.Changed -= Refresh; _abyss.Changed -= Refresh; _settings.Changed -= Refresh;
    }
}
