using Soulcrest.App.Overlay;

namespace Soulcrest.App.Services;

/// <summary>Projects rank progress for Abyss residence or a current Open World point-gain session, plus the Abyss-only feed.</summary>
public sealed class AbyssRankOverlaySource : IAbyssRankOverlaySource, IDisposable
{
    private readonly UnifiedLootTrackerService _unified;
    private readonly AbyssProgressService _abyss;
    private readonly SettingsService _settings;
    private readonly WorldContextService _world;
    private readonly AbyssKillFeedService _killFeed;

    public AbyssRankOverlaySource(UnifiedLootTrackerService unified, AbyssProgressService abyss,
        SettingsService settings, WorldContextService world, AbyssKillFeedService killFeed)
    {
        _unified = unified; _abyss = abyss; _settings = settings; _world = world; _killFeed = killFeed;
        unified.Changed += Refresh; abyss.Changed += Refresh; settings.Changed += Refresh;
        world.Changed += Refresh; killFeed.Changed += Refresh;
    }

    public event Action? Changed;
    public AbyssRankOverlaySnapshot Snapshot
    {
        get
        {
            var progress = _abyss.ProgressSnapshot;
            var settings = _settings.Current;
            var world = _world.Snapshot;
            var sessions = _unified.Snapshot.Sessions;
            var session = sessions.IsDefaultOrEmpty ? null
                : sessions.FirstOrDefault(row => row.Kind is LootTrackerSessionKind.Abyss or LootTrackerSessionKind.Mixed
                    && row.Status == LootTrackerSessionStatus.Live && row.EndedAt is null);
            var feed = _killFeed.DisplaySnapshot;
            return ProjectRank(progress, feed, settings.AbyssKillFeedEnabled,
                settings.AbyssKillFeedShowKills, settings.AbyssKillFeedShowDeaths, settings.AbyssKillFeedShowAssists,
                session, world, _abyss.SourceAvailable && settings.LootTrackingEnabled
                    && (world.Kind == WorldContextKind.OpenWorld && world.InstanceLayer != true
                        ? settings.MonsterLootTrackingEnabled : settings.AbyssTrackingEnabled));
        }
    }
    // Raw-entry callers retain the original strict per-entry generation contract.
    internal static AbyssRankOverlaySnapshot ProjectRank(AbyssProgressSnapshot progress,
        IReadOnlyList<AbyssKillFeedEntry> entries, bool enabled, bool showKills = true,
        bool showDeaths = false, bool showAssists = true, LootTrackerSession? session = null,
        WorldContextSnapshot? world = null, bool trackingAvailable = false) =>
        ProjectRankCore(progress, entries, enabled, showKills, showDeaths, showAssists, session,
            world, trackingAvailable, null, requireEntryGeneration: true);

    // Entries remain frozen in their original event epoch. The feed's atomically published
    // current display owner authorizes historical presentation, never a new combat event.
    internal static AbyssRankOverlaySnapshot ProjectRank(AbyssProgressSnapshot progress,
        AbyssKillFeedDisplaySnapshot feed, bool enabled, bool showKills = true,
        bool showDeaths = false, bool showAssists = true, LootTrackerSession? session = null,
        WorldContextSnapshot? world = null, bool trackingAvailable = false) =>
        ProjectRankCore(progress, feed.Entries.IsDefault ? [] : feed.Entries, enabled,
            showKills, showDeaths, showAssists, session, world, trackingAvailable,
            feed.BindingGeneration, requireEntryGeneration: false);

    private static AbyssRankOverlaySnapshot ProjectRankCore(AbyssProgressSnapshot progress,
        IReadOnlyList<AbyssKillFeedEntry> entries, bool enabled, bool showKills,
        bool showDeaths, bool showAssists, LootTrackerSession? session,
        WorldContextSnapshot? world, bool trackingAvailable, long? displayBindingGeneration,
        bool requireEntryGeneration)
    {
        var now = DateTimeOffset.UtcNow;
        var abyssWorld = world is { Kind: WorldContextKind.Instance, Category: "Abyss" };
        var openWorld = world is { Kind: WorldContextKind.OpenWorld, InstanceLayer: not true };
        var contextCurrent = trackingAvailable && progress.ProfileId is { Length: > 0 }
            && progress.BindingGeneration is > 0 and var binding
            && world is { Status: WorldContextStatus.Confirmed, ObservedAt: { } observed }
            && world.BindingGeneration == binding && observed != default && observed <= now;
        var boundSession = session is { Status: LootTrackerSessionStatus.Live, EndedAt: null, Id.Length: > 0 }
            && LootTrackerPresentation.CharacterProfileId(session) == progress.ProfileId;
        var openWorldSession = openWorld && boundSession && session!.Kind == LootTrackerSessionKind.Mixed
            && HasOwnPointGain(session!, now);
        var worldVisible = contextCurrent && (abyssWorld || openWorldSession);
        var currentSession = worldVisible && boundSession
            && (abyssWorld && session!.Kind == LootTrackerSessionKind.Abyss || openWorldSession);
        var net = currentSession ? LootTrackerPresentation.NetRankPoints(session!) : null;
        return new(progress.RankPoints, progress.ProfileId is not null, progress.RankPointsConfirmed)
        {
            WorldContextVisible = worldVisible,
            ProfileId = progress.ProfileId,
            BindingGeneration = progress.BindingGeneration,
            RankObservationSequence = progress.RankObservationSequence,
            RankPointsObservedAt = progress.RankPointsObservedAt,
            RankTrackingActive = worldVisible && progress.RankTrackingActive,
            RankContinuityRevision = progress.RankContinuityRevision,
            SessionId = currentSession ? session!.Id : null,
            SessionActive = currentSession,
            SessionNetRankPoints = net,
            SessionRankPointsPerHour = RankPointsPerHour(net, currentSession ? session!.Elapsed : TimeSpan.Zero),
            KillFeedEnabled = enabled,
            ShowKills = showKills, ShowDeaths = showDeaths, ShowAssists = showAssists,
            KillFeed = worldVisible && abyssWorld && enabled && progress.ProfileId is not null && progress.BindingGeneration is { } generation
                && (requireEntryGeneration || displayBindingGeneration == generation)
                ? entries.Where(row => (!requireEntryGeneration || row.Generation == generation) && (row.Kind switch
                {
                    AbyssKillFeedKind.Kill => showKills,
                    AbyssKillFeedKind.Death => showDeaths,
                    AbyssKillFeedKind.Assist => showAssists,
                    _ => false
                })).ToArray() : []
        };
    }

    // Only a booked own gain in the current Open World session can reveal this HUD.
    // Absolute caches, availability baselines and losses cannot act as a receipt.
    private static bool HasOwnPointGain(LootTrackerSession session, DateTimeOffset now) =>
        session.UnifiedVersion > 0 && session.TimelineVersion > 0 && !session.TimelineEvents.IsDefaultOrEmpty
        && session.LastLootAt is { } last && session.TimelineEvents.Any(fact =>
            fact.Origin == LootTrackerActivityOrigin.Pve && fact.Amount is > 0
            && fact.At >= session.StartedAt && fact.At <= last && fact.At <= now
            && fact.Elapsed >= TimeSpan.Zero && fact.Elapsed <= session.Elapsed
            && (fact.Kind == LootTrackerTimelineEventKind.AbyssPoints && session.AbyssPoints is > 0
                || fact.Kind == LootTrackerTimelineEventKind.RankPoints && session.RankPointsGained is > 0));

    internal static double? RankPointsPerHour(long? net, TimeSpan elapsed)
    {
        if (net is null || elapsed <= TimeSpan.Zero) return null;
        var rate = net.Value / elapsed.TotalHours;
        return double.IsFinite(rate) ? rate : null;
    }
    private void Refresh() => SafeEvent.Raise(Changed, "Abyss-Overlay");
    public void Dispose()
    {
        _unified.Changed -= Refresh; _abyss.Changed -= Refresh; _settings.Changed -= Refresh;
        _world.Changed -= Refresh; _killFeed.Changed -= Refresh;
    }
}
