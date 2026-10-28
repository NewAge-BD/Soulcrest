using System.Drawing;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class AbyssLootPresentationTests
{
    [Theory]
    [InlineData(50L, 80L, -30L, -90d)]
    [InlineData(100L, 0L, 100L, 300d)]
    [InlineData(0L, 0L, 0L, 0d)]
    [InlineData(null, 20L, null, null)]
    [InlineData(20L, null, null, null)]
    public void RankSessionAndHourlyMetricsAreBothSignedNetAndPreserveUnknownAmounts(long? gained, long? lost,
        long? expectedNet, double? expectedRate)
    {
        var (progress, session, world) = RankContext();
        session = session with { RankPointsGained = gained, RankPointsLost = lost };
        var snapshot = AbyssRankOverlaySource.ProjectRank(progress, [], false, session: session, world: world, trackingAvailable: true);
        Assert.True(snapshot.SessionActive);
        Assert.Equal(session.Id, snapshot.SessionId);
        Assert.Equal(expectedNet, snapshot.SessionNetRankPoints);
        Assert.Equal(expectedRate, snapshot.SessionRankPointsPerHour);
        Assert.Empty(snapshot.KillFeed);
        var zeroDuration = AbyssRankOverlaySource.ProjectRank(progress, [], false,
            session: session with { Elapsed = TimeSpan.Zero }, world: world, trackingAvailable: true);
        Assert.Equal(expectedNet, zeroDuration.SessionNetRankPoints);
        Assert.Null(zeroDuration.SessionRankPointsPerHour);
        Assert.Null(AbyssRankOverlaySource.RankPointsPerHour(expectedNet, TimeSpan.FromTicks(-1)));
    }

    [Theory]
    [InlineData("foreign-profile")]
    [InlineData("missing-profile")]
    [InlineData("completed")]
    [InlineData("ended")]
    [InlineData("other-kind")]
    [InlineData("missing-session")]
    [InlineData("missing-binding")]
    [InlineData("wrong-generation")]
    [InlineData("transition")]
    [InlineData("outside")]
    [InlineData("future-world")]
    [InlineData("unavailable")]
    public void RankMetricsNeverReuseAnotherProfileHistoricalSessionOrUnconfirmedResidence(string boundary)
    {
        var (progress, session, world) = RankContext();
        var available = true;
        switch (boundary)
        {
            case "foreign-profile": session = session with { Character = new("22222222222222222222222222222222", "Other synthetic") }; break;
            case "missing-profile": progress = progress with { ProfileId = null }; break;
            case "completed": session = session with { Status = LootTrackerSessionStatus.Completed }; break;
            case "ended": session = session with { EndedAt = DateTimeOffset.UnixEpoch }; break;
            case "other-kind": session = session with { Kind = LootTrackerSessionKind.Grind }; break;
            case "missing-session": session = session with { Id = "" }; break;
            case "missing-binding": progress = progress with { BindingGeneration = null }; break;
            case "wrong-generation": world = world with { BindingGeneration = 9 }; break;
            case "transition": world = world with { Status = WorldContextStatus.Transitioning }; break;
            case "outside": world = world with { Category = "Party" }; break;
            case "future-world": world = world with { ObservedAt = DateTimeOffset.MaxValue }; break;
            case "unavailable": available = false; break;
        }
        var snapshot = AbyssRankOverlaySource.ProjectRank(progress, [], false, session: session, world: world, trackingAvailable: available);
        Assert.False(snapshot.SessionActive);
        Assert.Null(snapshot.SessionId);
        Assert.Null(snapshot.SessionNetRankPoints);
        Assert.Null(snapshot.SessionRankPointsPerHour);
    }

    [Theory]
    [InlineData(5L, 0L, 80L, -80L, -240d)]
    [InlineData(0L, 50L, 80L, -30L, -90d)]
    [InlineData(5L, 50L, 80L, -30L, -90d)]
    public void OpenWorldRankTrackerRequiresEitherBookedPointGainAndKeepsSignedNetMetricsWithoutTheFeed(
        long ap, long gained, long lost, long expectedNet, double expectedRate)
    {
        var (progress, session, world) = OpenWorldRankContext(ap, gained, lost);
        var entry = new AbyssKillFeedEntry(session.StartedAt, AbyssKillFeedKind.Kill, "Synthetic opponent") { Generation = 8 };
        var feed = new AbyssKillFeedDisplaySnapshot(8, [entry]);
        var snapshot = AbyssRankOverlaySource.ProjectRank(progress, feed, true,
            session: session, world: world, trackingAvailable: true);
        Assert.True(snapshot.WorldContextVisible); Assert.True(snapshot.SessionActive);
        Assert.Equal(session.Id, snapshot.SessionId);
        Assert.Equal(expectedNet, snapshot.SessionNetRankPoints);
        Assert.Equal(expectedRate, snapshot.SessionRankPointsPerHour);
        Assert.True(snapshot.RankTrackingActive);
        Assert.Empty(snapshot.KillFeed); // A visible Open World rank HUD never reveals historical Abyss combat.
        Assert.Empty(AbyssRankOverlaySource.ProjectRank(progress, feed.Entries, true,
            session: session, world: world, trackingAvailable: true).KillFeed);
    }

    [Theory]
    [InlineData("zero")]
    [InlineData("loss-only")]
    [InlineData("no-session")]
    [InlineData("completed")]
    [InlineData("ended")]
    [InlineData("foreign-profile")]
    [InlineData("missing-profile")]
    [InlineData("wrong-generation")]
    [InlineData("transition")]
    [InlineData("stale")]
    [InlineData("unknown")]
    [InlineData("dungeon")]
    [InlineData("instance-layer")]
    [InlineData("future-world")]
    [InlineData("unavailable")]
    [InlineData("cache-only")]
    [InlineData("foreign-origin")]
    [InlineData("future-fact")]
    [InlineData("old-fact")]
    [InlineData("other-kind")]
    public void OpenWorldBaselineLossHistoricalOrUnqualifiedContextCannotRevealRankTracker(string boundary)
    {
        var (progress, original, world) = OpenWorldRankContext(5, 0, 0);
        LootTrackerSession? session = original;
        var available = true;
        switch (boundary)
        {
            case "zero": session = session with { AbyssPoints = 0 }; break;
            case "loss-only": session = session with { AbyssPoints = null, RankPointsGained = 0, RankPointsLost = 5,
                TimelineEvents = [session.TimelineEvents[0] with { Kind = LootTrackerTimelineEventKind.RankPoints, Amount = -5 }] }; break;
            case "no-session": session = null; break;
            case "completed": session = session with { Status = LootTrackerSessionStatus.Completed }; break;
            case "ended": session = session with { EndedAt = session.LastLootAt }; break;
            case "foreign-profile": session = session with { Character = new("22222222222222222222222222222222", "Synthetic other") }; break;
            case "missing-profile": progress = progress with { ProfileId = null }; break;
            case "wrong-generation": world = world with { BindingGeneration = 9 }; break;
            case "transition": world = world with { Status = WorldContextStatus.Transitioning }; break;
            case "stale": world = world with { Status = WorldContextStatus.Stale }; break;
            case "unknown": world = world with { Status = WorldContextStatus.Unknown }; break;
            case "dungeon": world = world with { Kind = WorldContextKind.Instance, Category = "Party" }; break;
            case "instance-layer": world = world with { InstanceLayer = true }; break;
            case "future-world": world = world with { ObservedAt = DateTimeOffset.MaxValue }; break;
            case "unavailable": available = false; break;
            case "cache-only": session = session with { TimelineEvents = [] }; break;
            case "foreign-origin": session = session with { TimelineEvents = [session.TimelineEvents[0] with { Origin = LootTrackerActivityOrigin.Lifeskill }] }; break;
            case "future-fact": session = session with { TimelineEvents = [session.TimelineEvents[0] with { At = DateTimeOffset.MaxValue }] }; break;
            case "old-fact": session = session with { TimelineEvents = [session.TimelineEvents[0] with { At = session.StartedAt.AddTicks(-1) }] }; break;
            case "other-kind": session = session with { Kind = LootTrackerSessionKind.Abyss }; break;
        }
        var snapshot = AbyssRankOverlaySource.ProjectRank(progress, [], true,
            session: session, world: world, trackingAvailable: available);
        Assert.False(snapshot.WorldContextVisible); Assert.False(snapshot.SessionActive);
        Assert.False(snapshot.RankTrackingActive); Assert.Null(snapshot.SessionId);
        Assert.Null(snapshot.SessionNetRankPoints); Assert.Null(snapshot.SessionRankPointsPerHour);
        Assert.Empty(snapshot.KillFeed);
    }

    private static (AbyssProgressSnapshot Progress, LootTrackerSession Session, WorldContextSnapshot World)
        OpenWorldRankContext(long ap, long gained, long lost)
    {
        var (progress, session, world) = RankContext();
        var at = DateTimeOffset.UtcNow.AddMinutes(-1);
        var events = System.Collections.Immutable.ImmutableArray.CreateBuilder<LootTrackerTimelineEvent>();
        if (ap > 0) events.Add(LootTrackerTimelineFacts.Create(LootTrackerTimelineEventKind.AbyssPoints, at, TimeSpan.FromMinutes(19))
            with { Origin = LootTrackerActivityOrigin.Pve, Amount = ap });
        if (gained > 0) events.Add(LootTrackerTimelineFacts.Create(LootTrackerTimelineEventKind.RankPoints, at, TimeSpan.FromMinutes(19))
            with { Origin = LootTrackerActivityOrigin.Pve, Amount = gained });
        session = session with { Kind = LootTrackerSessionKind.Mixed, UnifiedVersion = 1, TimelineVersion = 1,
            StartedAt = at.AddMinutes(-19), LastLootAt = at, RankPointsGained = gained, RankPointsLost = lost,
            AbyssPoints = ap, TimelineEvents = events.ToImmutable() };
        world = world with { Kind = WorldContextKind.OpenWorld, Category = "field", ObservedAt = session.StartedAt };
        return (progress with { RankTrackingActive = true }, session, world);
    }

    private static (AbyssProgressSnapshot Progress, LootTrackerSession Session, WorldContextSnapshot World) RankContext()
    {
        const string profile = "11111111111111111111111111111111";
        var progress = new AbyssProgressSnapshot(profile, null, null, 12_500, null, DateTimeOffset.UnixEpoch, true)
        { BindingGeneration = 8, RankPointsConfirmed = true };
        var session = new LootTrackerSession
        {
            Id = "synthetic-current-abyss", Kind = LootTrackerSessionKind.Abyss, Status = LootTrackerSessionStatus.Live,
            Character = new(profile, "Synthetic"), Elapsed = TimeSpan.FromMinutes(20),
            RankPointsGained = 50, RankPointsLost = 80
        };
        var world = new WorldContextSnapshot(WorldContextStatus.Confirmed, WorldContextKind.Instance, 20,
            null, "Abyss", null, "Abyss", "Abyss", 1, DateTimeOffset.UnixEpoch, "synthetic") { BindingGeneration = 8 };
        return (progress, session, world);
    }

    [Fact]
    public void RankAndKillFeedCannotMixCharacterGenerationsOrResurfaceWhenDisabled()
    {
        var progress = new AbyssProgressSnapshot("11111111111111111111111111111111", null, null, 12_500, null, null, true)
        { BindingGeneration = 8 };
        AbyssKillFeedEntry[] entries = [new(DateTimeOffset.UnixEpoch, AbyssKillFeedKind.Kill, "Synthetic opponent") { Generation = 8 },
            new(DateTimeOffset.UnixEpoch, AbyssKillFeedKind.Death, "Other synthetic opponent") { Generation = 9 }];
        var projected = AbyssRankOverlaySource.ProjectRank(progress, entries, true, world: RankContext().World, trackingAvailable: true);
        Assert.Equal(8, Assert.Single(projected.KillFeed).Generation);
        Assert.Empty(AbyssRankOverlaySource.ProjectRank(progress, entries, false, world: RankContext().World, trackingAvailable: true).KillFeed);
        Assert.Empty(AbyssRankOverlaySource.ProjectRank(progress with { BindingGeneration = null }, entries, true, world: RankContext().World, trackingAvailable: true).KillFeed);
        Assert.Empty(AbyssRankOverlaySource.ProjectRank(progress with { ProfileId = null }, entries, true, world: RankContext().World, trackingAvailable: true).KillFeed);
    }

    [Fact]
    public void RankProjectionHonorsIndividuallySelectedKindsWithinTheOwnGeneration()
    {
        var progress = new AbyssProgressSnapshot("11111111111111111111111111111111", null, null, null, null, null, false)
        { BindingGeneration = 8 };
        AbyssKillFeedEntry[] entries =
        [new(DateTimeOffset.UnixEpoch, AbyssKillFeedKind.Kill) { Generation = 8 },
            new(DateTimeOffset.UnixEpoch, AbyssKillFeedKind.Death) { Generation = 8 },
            new(DateTimeOffset.UnixEpoch, AbyssKillFeedKind.Assist) { Generation = 8 }];
        var assistsOnly = AbyssRankOverlaySource.ProjectRank(progress, entries, true, false, false, true, world: RankContext().World, trackingAvailable: true);
        Assert.Equal(AbyssKillFeedKind.Assist, Assert.Single(assistsOnly.KillFeed).Kind);
        Assert.False(assistsOnly.ShowKills); Assert.False(assistsOnly.ShowDeaths); Assert.True(assistsOnly.ShowAssists);
        Assert.Equal([AbyssKillFeedKind.Kill, AbyssKillFeedKind.Assist],
            AbyssRankOverlaySource.ProjectRank(progress, entries, true, world: RankContext().World, trackingAvailable: true).KillFeed.Select(row => row.Kind));
        Assert.Empty(AbyssRankOverlaySource.ProjectRank(progress, entries, true, false, false, false, world: RankContext().World, trackingAvailable: true).KillFeed);
    }

    [Fact]
    public void DisplayOwnerAllowsFrozenHistoricalEntriesAfterAConfirmedSameCharacterRebind()
    {
        var (progress, session, world) = RankContext();
        var historical = new AbyssKillFeedEntry(DateTimeOffset.UnixEpoch, AbyssKillFeedKind.Kill, "Synthetic opponent")
        { Generation = 3, Sequence = 5, OpponentClassId = "Sorcerer", OpponentAbyssRankGrade = 13 };
        var current = new AbyssKillFeedEntry(DateTimeOffset.UnixEpoch.AddSeconds(1), AbyssKillFeedKind.Death)
        { Generation = 8, Sequence = 1 };
        var feed = new AbyssKillFeedDisplaySnapshot(8, [current, historical]);
        var projected = AbyssRankOverlaySource.ProjectRank(progress, feed, true, showDeaths: true,
            session: session, world: world, trackingAvailable: true);

        Assert.True(projected.WorldContextVisible); Assert.True(projected.SessionActive);
        Assert.Equal(2, projected.KillFeed.Count);
        Assert.Same(current, projected.KillFeed[0]); Assert.Same(historical, projected.KillFeed[1]);
        Assert.Equal(3, projected.KillFeed[1].Generation); Assert.Equal(5, projected.KillFeed[1].Sequence);
        Assert.Equal("Sorcerer", projected.KillFeed[1].OpponentClassId); Assert.Equal(13, projected.KillFeed[1].OpponentAbyssRankGrade);
        Assert.Null(projected.KillFeed[1].AbyssPointGain); Assert.Null(projected.KillFeed[1].RankPointDelta);
        Assert.Equal(-30, projected.SessionNetRankPoints);
        // A raw list has no independently published current display owner and keeps its strict contract.
        Assert.Same(current, Assert.Single(AbyssRankOverlaySource.ProjectRank(progress, feed.Entries,
            true, showDeaths: true, world: world, trackingAvailable: true).KillFeed));
    }

    [Theory]
    [InlineData("wrong-display-owner")]
    [InlineData("missing-display-owner")]
    [InlineData("missing-progress-owner")]
    [InlineData("zero-progress-owner")]
    [InlineData("stale-progress-owner")]
    [InlineData("wrong-world-owner")]
    [InlineData("missing-profile")]
    [InlineData("loading")]
    [InlineData("unknown-world")]
    [InlineData("stale-world")]
    [InlineData("outside")]
    [InlineData("missing-world")]
    [InlineData("future-world")]
    [InlineData("unavailable")]
    [InlineData("disabled")]
    public void HistoricalFeedRequiresItsCurrentDisplayOwnerAndConfirmedAbyssWorld(string boundary)
    {
        var (progress, _, confirmedWorld) = RankContext();
        WorldContextSnapshot? world = confirmedWorld;
        var entry = new AbyssKillFeedEntry(DateTimeOffset.UnixEpoch, AbyssKillFeedKind.Kill)
        { Generation = 8, Sequence = 1 }; // Matching row metadata cannot override a missing/wrong display owner.
        var feed = new AbyssKillFeedDisplaySnapshot(8, [entry]);
        var enabled = true; var available = true;
        switch (boundary)
        {
            case "wrong-display-owner": feed = feed with { BindingGeneration = 9 }; break;
            case "missing-display-owner": feed = feed with { BindingGeneration = null }; break;
            case "missing-progress-owner": progress = progress with { BindingGeneration = null }; break;
            case "zero-progress-owner": progress = progress with { BindingGeneration = 0 }; break;
            case "stale-progress-owner": progress = progress with { BindingGeneration = 7 }; break;
            case "wrong-world-owner": world = world with { BindingGeneration = 9 }; break;
            case "missing-profile": progress = progress with { ProfileId = null }; break;
            case "loading": world = world with { Status = WorldContextStatus.Transitioning }; break;
            case "unknown-world": world = world with { Status = WorldContextStatus.Unknown }; break;
            case "stale-world": world = world with { Status = WorldContextStatus.Stale }; break;
            case "outside": world = world with { Kind = WorldContextKind.OpenWorld, Category = null }; break;
            case "missing-world": world = null; break;
            case "future-world": world = world with { ObservedAt = DateTimeOffset.MaxValue }; break;
            case "unavailable": available = false; break;
            case "disabled": enabled = false; break;
        }
        Assert.Empty(AbyssRankOverlaySource.ProjectRank(progress, feed, enabled,
            world: world, trackingAvailable: available).KillFeed);
        Assert.Same(entry, Assert.Single(feed.Entries));
    }

    [Fact]
    public void HistoricalDisplayStillHonorsEachKindSelectionAndAnEmptyDefaultSnapshot()
    {
        var (progress, _, world) = RankContext();
        var kill = new AbyssKillFeedEntry(DateTimeOffset.UnixEpoch, AbyssKillFeedKind.Kill) { Generation = 3 };
        var death = new AbyssKillFeedEntry(DateTimeOffset.UnixEpoch, AbyssKillFeedKind.Death) { Generation = 4 };
        var assist = new AbyssKillFeedEntry(DateTimeOffset.UnixEpoch, AbyssKillFeedKind.Assist) { Generation = 5 };
        var feed = new AbyssKillFeedDisplaySnapshot(8, [kill, death, assist]);
        var onlyDeaths = AbyssRankOverlaySource.ProjectRank(progress, feed, true, false, true, false,
            world: world, trackingAvailable: true);
        Assert.Same(death, Assert.Single(onlyDeaths.KillFeed));
        Assert.False(onlyDeaths.ShowKills); Assert.True(onlyDeaths.ShowDeaths); Assert.False(onlyDeaths.ShowAssists);
        Assert.Empty(AbyssRankOverlaySource.ProjectRank(progress, feed, true, false, false, false,
            world: world, trackingAvailable: true).KillFeed);
        Assert.Empty(AbyssRankOverlaySource.ProjectRank(progress, feed with { Entries = default }, true,
            world: world, trackingAvailable: true).KillFeed);
        Assert.Equal(3, feed.Entries.Length);
    }

    private static LootOverlaySnapshot ProjectLoot(LootTrackerSession session, AbyssProgressSnapshot progress, AppSettings settings) =>
        UnifiedLootOverlaySource.Project(new(session, true, new(null, null, null, 0), null, false, DateTimeOffset.UtcNow), progress, settings);

    [Fact]
    public void SessionAndCapUseIndependentAmountsAndRateSelectionUsesResidenceDuration()
    {
        var session = new LootTrackerSession
        { Kind = LootTrackerSessionKind.Abyss, Character = new("11111111111111111111111111111111", "Synthetic"), Elapsed = TimeSpan.FromMinutes(20), AbyssPoints = 6_000, KinaUnbound = null };
        var progress = new AbyssProgressSnapshot("11111111111111111111111111111111", 10, 1_800_000, 100, 2_000_000, null, true)
        { FarmedAbyssPointsConfirmed = true };
        var settings = new AppSettings();
        var hour = ProjectLoot(session, progress, settings);
        Assert.Equal(6_000, hour.SessionAbyssPoints); Assert.Equal(18_000, hour.SessionAbyssPointsRate);
        Assert.Equal(1_800_000, hour.FarmedAbyssPoints); Assert.Equal(2_000_000, hour.AbyssPointCap);
        Assert.Null(hour.DailyMonsterKina); Assert.False(hour.SessionKinaKnown); Assert.False(hour.DailyLimitReached);
        settings.AbyssPointRatePeriod = AbyssPointRatePeriod.Minute;
        Assert.Equal(300, ProjectLoot(session, progress, settings).SessionAbyssPointsRate);
        Assert.Null(ProjectLoot(session with { Elapsed = TimeSpan.Zero }, progress, settings).SessionAbyssPointsRate);
        Assert.Null(ProjectLoot(session with { AbyssPoints = null }, progress, settings).SessionAbyssPointsRate);
        Assert.Equal(0, ProjectLoot(session with { AbyssPoints = 0 }, progress, settings).SessionAbyssPointsRate);
        var other = ProjectLoot(session, progress with { ProfileId = "22222222222222222222222222222222" }, settings);
        Assert.Null(other.FarmedAbyssPoints); Assert.False(other.AbyssFarmConfirmed);
        Assert.Equal(6_000, other.SessionAbyssPoints);
    }

    [Fact]
    public void AbyssLootOverlayOrdersAfterGroupingButItsSourceSessionRetainsExactDrops()
    {
        var session = new LootTrackerSession
        {
            Kind = LootTrackerSessionKind.Abyss,
            Loot = [new("Item:110150003", "Rare first", LootTrackerLootKind.Item, 2),
                new("Item:610530005", "Rare material", LootTrackerLootKind.Item, 4),
                new("Item:110140002", "Legend first", LootTrackerLootKind.Item, 1),
                new("Item:110150004", "Rare second", LootTrackerLootKind.Item, 3),
                new("Item:610530008", "Legend material", LootTrackerLootKind.Item, 2),
                new("Item:110140003", "Legend second", LootTrackerLootKind.Item, 2)],
        };
        var before = session.Loot.ToArray();
        var snapshot = ProjectLoot(session, AbyssProgressSnapshot.Empty, new AppSettings());
        Assert.Equal(new[] { LootOverlayDropPresentation.LegendGearKey, "Item:610530008",
            LootOverlayDropPresentation.RareGearKey, "Item:610530005" }, snapshot.Drops.Select(row => row.Key));
        Assert.Equal(new[] { 3L, 2L, 5L, 4L }, snapshot.Drops.Select(row => row.Count));
        Assert.Equal(before, session.Loot.ToArray());
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void EveryThemeMeasuresAbyssRowsWithoutDailyKinaCelebration(string language)
    {
        var previous = UiText.Language;
        try
        {
            UiText.Language = language;
            var snapshot = new LootOverlaySnapshot("synthetic", true, 300, 2_000_000,
                [new("synthetic-item", "Synthetic item", 12)])
            { SessionKind = LootTrackerSessionKind.Abyss, SessionAbyssPoints = 6_000, SessionAbyssPointsRate = 18_000,
                FarmedAbyssPoints = 1_800_000, AbyssPointCap = 2_000_000, AbyssFarmConfirmed = true };
            Assert.False(snapshot.DailyLimitReached);
            Assert.Equal(RectangleF.Empty, LootOverlayRenderer.SessionSegmentBounds(snapshot));
            foreach (var theme in OverlayThemeDefinition.All)
            {
                using var bitmap = LootOverlayRenderer.RenderBitmap(snapshot, theme.Id);
                using var scaled = LootOverlayRenderer.RenderBitmap(snapshot, theme.Id, 2);
                Assert.Equal(LootOverlayRenderer.DropsTopFor(snapshot) + LootOverlayRenderer.RowHeight + LootOverlayRenderer.FooterHeight, bitmap.Height);
                Assert.Equal(bitmap.Height * 2, scaled.Height);
                Assert.Equal(bitmap.Width * 2, scaled.Width);
            }
        }
        finally { UiText.Language = previous; }
    }
}
