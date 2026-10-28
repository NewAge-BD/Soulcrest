using Soulcrest.App.Network;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class UnifiedLootOverlaySourceTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private const string Profile = "00000000000000000000000000000123";

    [Fact]
    public void PureGatheringShowsHarvestsAndRateWithoutInventingCurrencyBlocks()
    {
        var session = Session() with
        {
            HarvestCount = 12, FailedHarvestCount = 2, ItemCount = 48,
            TimelineEvents = [Fact(LootTrackerTimelineEventKind.HarvestSuccess, LootTrackerActivityOrigin.Lifeskill)]
        };
        var result = Project(session);
        Assert.False(result.HasPveActivity); Assert.True(result.HasLifeskillActivity);
        Assert.Equal(12, result.HarvestCount); Assert.Equal(24d, result.HarvestsPerHour);
        Assert.Equal(2, result.FailedHarvestCount); Assert.Equal(48, result.ItemCount);
        Assert.False(result.SessionKinaKnown); Assert.Null(result.SessionAbyssPoints);
    }

    [Theory]
    [InlineData(LootTrackerViewMode.Mixed)] [InlineData(LootTrackerViewMode.Pve)]
    [InlineData(LootTrackerViewMode.Lifeskill)] [InlineData(LootTrackerViewMode.Abyss)]
    public void HistoricalViewPreferenceCannotChangeTheMixedLiveOverlay(LootTrackerViewMode mode)
    {
        var session = Session() with
        {
            KinaUnbound = 600, HarvestCount = 3, FailedHarvestCount = 0, ItemCount = 8,
            TimelineEvents = [Fact(LootTrackerTimelineEventKind.KinaUnbound, LootTrackerActivityOrigin.Pve),
                Fact(LootTrackerTimelineEventKind.HarvestSuccess, LootTrackerActivityOrigin.Lifeskill)]
        };
        var result = Project(session, new() { LootTrackerViewMode = mode });
        Assert.True(result.HasPveActivity); Assert.True(result.HasLifeskillActivity);
        Assert.Equal(600, result.SessionKina); Assert.Equal(1_200d, result.SessionKinaPerHour);
        Assert.Equal(6d, result.HarvestsPerHour);
    }

    [Fact]
    public void OpenWorldPointIncomeUsesTheSharedDurationAndDoesNotRequireKinaOrTheAbyssView()
    {
        var session = Session() with
        {
            AbyssPoints = 60,
            TimelineEvents = [Fact(LootTrackerTimelineEventKind.AbyssPoints, LootTrackerActivityOrigin.Pve) with { Amount = 60 }]
        };
        var result = Project(session, new() { LootTrackerViewMode = LootTrackerViewMode.Lifeskill });
        Assert.Equal(LootTrackerSessionKind.Mixed, result.SessionKind);
        Assert.True(result.HasAbyssPointActivity); Assert.False(result.HasPveActivity);
        Assert.False(result.HasLifeskillActivity); Assert.Equal(60, result.SessionAbyssPoints);
        Assert.Equal(120d, result.SessionAbyssPointsRate);
        Assert.Equal(2d, Project(session, new() { AbyssPointRatePeriod = AbyssPointRatePeriod.Minute }).SessionAbyssPointsRate);
        Assert.Null(Project(session with { Elapsed = TimeSpan.Zero }).SessionAbyssPointsRate);
        Assert.False(Project(session with { Status = LootTrackerSessionStatus.Completed, EndedAt = At }).HasAbyssPointActivity);
    }

    [Fact]
    public void AbsolutePointCachesAndRankOnlyReceiptsCannotInventAnApActivityBlock()
    {
        var progress = new AbyssProgressSnapshot(Profile, 12345, 123456, 900000, 2000000, At, true)
            { FarmedAbyssPointsConfirmed = true };
        var session = Session() with
        {
            HarvestCount = 1,
            TimelineEvents = [Fact(LootTrackerTimelineEventKind.HarvestSuccess, LootTrackerActivityOrigin.Lifeskill)]
        };
        Assert.False(Project(session, progress: progress).HasAbyssPointActivity);
        var ranks = session with { RankPointsGained = 12, RankPointsLost = 0,
            TimelineEvents = [.. session.TimelineEvents, Fact(LootTrackerTimelineEventKind.RankPoints, LootTrackerActivityOrigin.Pve) with { Amount = 12 }] };
        Assert.False(Project(ranks, progress: progress).HasAbyssPointActivity);
        Assert.False(Project(ranks with { AbyssPoints = 0 }, progress: progress).HasAbyssPointActivity);
        var unavailable = UnifiedLootOverlaySource.Project(State(session) with { TrackingAvailable = false }, progress, new());
        Assert.False(unavailable.HasAbyssPointActivity); Assert.False(unavailable.SessionActive);
    }

    [Fact]
    public void AbyssKeepsItsPointChannelAndAlsoShowsItsOwnHarvests()
    {
        var session = Session() with
        {
            Kind = LootTrackerSessionKind.Abyss, AbyssPoints = 360, HarvestCount = 6,
            TimelineEvents = [Fact(LootTrackerTimelineEventKind.HarvestSuccess, LootTrackerActivityOrigin.Lifeskill)]
        };
        var progress = new AbyssProgressSnapshot(Profile, 10, 1_500, 900_000, 2_000_000, At, true)
            { FarmedAbyssPointsConfirmed = true };
        var result = Project(session, new() { AbyssPointRatePeriod = AbyssPointRatePeriod.Minute }, progress);
        Assert.Equal(LootTrackerSessionKind.Abyss, result.SessionKind);
        Assert.Equal(12d, result.SessionAbyssPointsRate); Assert.Equal(12d, result.HarvestsPerHour);
        Assert.Equal(1_500, result.FarmedAbyssPoints); Assert.True(result.AbyssFarmConfirmed);
    }

    [Fact]
    public void MissingBindingOrInactiveSessionCannotKeepOldDropsOnTheLiveOverlay()
    {
        var session = Session() with { Loot = [new("Item:410010001", "Synthetic resource", LootTrackerLootKind.Item, 7)] };
        var result = UnifiedLootOverlaySource.Project(State(session) with { TrackingAvailable = false },
            AbyssProgressSnapshot.Empty, new());
        Assert.False(result.SessionActive); Assert.Null(result.SessionId); Assert.Empty(result.Drops);
        Assert.Null(result.HarvestsPerHour);
        Assert.False(Project(session with { Status = LootTrackerSessionStatus.Completed, EndedAt = At }).SessionActive);
    }

    [Fact]
    public void ZeroDurationAndUnknownHarvestsNeverBecomeAKnownRate()
    {
        var session = Session() with
        {
            Elapsed = TimeSpan.Zero, HarvestCount = 1,
            TimelineEvents = [Fact(LootTrackerTimelineEventKind.HarvestSuccess, LootTrackerActivityOrigin.Lifeskill)]
        };
        Assert.Null(Project(session).HarvestsPerHour);
        Assert.Null(Project(session with { Elapsed = TimeSpan.FromHours(1), HarvestCount = null }).HarvestsPerHour);
    }

    [Fact]
    public void DifferentProfileCannotBorrowAbyssCapProgressFromAnotherCharacter()
    {
        var progress = new AbyssProgressSnapshot("another-local-profile", 10, 1_500, 900_000, 2_000_000, At, true)
            { FarmedAbyssPointsConfirmed = true };
        var result = Project(Session() with { Kind = LootTrackerSessionKind.Abyss }, progress: progress);
        Assert.Null(result.FarmedAbyssPoints); Assert.False(result.AbyssFarmConfirmed);
        Assert.Equal(2_000_000, result.AbyssPointCap);
    }

    private static LootOverlaySnapshot Project(LootTrackerSession session, AppSettings? settings = null,
        AbyssProgressSnapshot? progress = null) => UnifiedLootOverlaySource.Project(State(session),
            progress ?? AbyssProgressSnapshot.Empty, settings ?? new());
    private static UnifiedLootOverlayState State(LootTrackerSession session) => new(session, true,
        new NetworkDailyMonsterKinaSnapshot(null, null, null, 0), null, false, At);
    private static LootTrackerSession Session() => new()
    {
        Id = Guid.NewGuid().ToString("N"), Kind = LootTrackerSessionKind.Mixed, UnifiedVersion = 1,
        Status = LootTrackerSessionStatus.Live, StartedAt = At.AddMinutes(-30), Elapsed = TimeSpan.FromMinutes(30),
        Character = new(Profile, "Synthetic character"), TimelineVersion = 1
    };
    private static LootTrackerTimelineEvent Fact(LootTrackerTimelineEventKind kind, LootTrackerActivityOrigin origin) =>
        new(Guid.NewGuid().ToString("N"), kind, At, TimeSpan.FromMinutes(30)) { Origin = origin };
}
