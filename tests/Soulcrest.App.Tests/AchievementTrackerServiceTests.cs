using System.Collections.Immutable;
using System.Text.Json;
using Soulcrest.App.Components;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class AchievementTrackerServiceTests
{
    private const int Riding = 420101100, Soaring = 420101000;
    private static readonly int Build = AchievementDefinitionCatalog.Default.MetadataBuild;
    private static readonly int Unqualified = CombatAchievementProgressContractCatalog.Default.Contracts
        .Where(type => type.CounterRule == CombatAchievementCounterRule.Unqualified
            && type.ClaimRule == CombatAchievementClaimRule.ContiguousRewardOrdinal)
        .SelectMany(type => type.Families).Where(family => family.Id != 401100100 && family.Stages.Count > 1
            && AchievementProgressThresholdCatalog.Default.FindFamily(family.Id) is null
            && new AchievementTrackerStageResolver().IsEligible(family.Id, Build)).Select(family => family.Id).First();
    private static readonly AchievementProfileBinding Owner = new(new string('a', 32), new string('b', 32));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameOwnSavedBaselineKeepsDirectAndTitleFavoriteNumbersWithoutContinuingAnAnimation(bool titleFavorite)
    {
        const int title = 23030006;
        using var fixture = new Fixture();
        if (titleFavorite) fixture.Favorites.ToggleTitle(Owner, title);
        else fixture.Favorites.Toggle(Owner, Riding);
        fixture.Source.Emit(Raw((Riding, 4, 0)), NetworkAchievementObservationKind.Baseline);
        fixture.Source.Emit(Raw((Riding, 5, 0)), milestones: [fixture.Source.Milestone(Riding, 1, AchievementMilestoneKind.Completed)]);
        fixture.Tracker.Tick(); Assert.True(Assert.Single(fixture.Tracker.Snapshot.Rows).IsAnimating);
        fixture.Source.Invalidate(reuseSequence: false); fixture.Tracker.Tick();
        Assert.Empty(fixture.Tracker.Snapshot.Rows);
        fixture.Source.ChangeOwner(Owner);
        fixture.Source.Saved(Raw((Riding, 7, 0))); fixture.Tracker.Tick();
        var row = Assert.Single(fixture.Tracker.Snapshot.Rows);
        Assert.Equal(Owner, fixture.Tracker.Snapshot.Binding); Assert.False(fixture.Tracker.Snapshot.IsLive);
        Assert.True(fixture.Source.CurrentOwnSnapshot.IsHistorical); Assert.True(fixture.Source.CurrentOwnSnapshot.HasProgressEvidence);
        Assert.True(row.IsFavorite); Assert.True(row.IsHistorical); Assert.False(row.IsTitlePlaceholder);
        Assert.Equal(Riding, row.FamilyId); Assert.Equal(7, row.Current); Assert.Equal(20, row.Target); Assert.Equal(7, row.RawValue);
        Assert.False(row.IsAnimating); Assert.Null(row.CelebratedStageId);
        if (titleFavorite) Assert.Equal(title, row.TrackedTitleId); else Assert.Null(row.TrackedTitleId);
        fixture.Clock.Advance(600); fixture.Tracker.Tick();
        row = Assert.Single(fixture.Tracker.Snapshot.Rows);
        Assert.True(row.IsHistorical); Assert.Equal(7, row.Current); Assert.False(row.IsAnimating);
    }

    [Fact]
    public void SavedOwnFactsDoNotSeedAutomaticPendingOrRecentRowsAndCannotRetainLiveCelebrations()
    {
        using var fixture = new Fixture();
        fixture.Source.Emit(Raw((Riding, 4, 0)), NetworkAchievementObservationKind.Baseline);
        fixture.Source.Emit(Raw((Riding, 5, 0)), milestones: [fixture.Source.Milestone(Riding, 1, AchievementMilestoneKind.Completed)]);
        fixture.Tracker.Tick(); Assert.True(Assert.Single(fixture.Tracker.Snapshot.Rows).IsAnimating);
        fixture.Source.Saved(Raw((Riding, 7, 0), (Unqualified, 8, 0))); fixture.Tracker.Tick();
        Assert.True(fixture.Source.CurrentOwnSnapshot.IsHistorical); Assert.Empty(fixture.Tracker.Snapshot.Rows);
        fixture.Clock.Advance(600); fixture.Tracker.Tick(); Assert.Empty(fixture.Tracker.Snapshot.Rows);
        fixture.Favorites.Toggle(Owner, Riding); fixture.Tracker.Tick();
        var row = Assert.Single(fixture.Tracker.Snapshot.Rows);
        Assert.Equal(7, row.Current); Assert.True(row.IsHistorical); Assert.False(row.IsAnimating);
        fixture.Favorites.Toggle(Owner, Riding); fixture.Tracker.Tick(); Assert.Empty(fixture.Tracker.Snapshot.Rows);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ANewOwnerWithoutOwnFactsCannotBorrowThePreviousOwnersSavedFavoriteValues(bool titleFavorite)
    {
        const int title = 23030006;
        var another = new AchievementProfileBinding(new string('c', 32), new string('d', 32));
        using var fixture = new Fixture();
        void Favorite(AchievementProfileBinding binding)
        { if (titleFavorite) fixture.Favorites.ToggleTitle(binding, title); else fixture.Favorites.Toggle(binding, Riding); }
        Favorite(Owner); Favorite(another);
        fixture.Source.Saved(Raw((Riding, 7, 0))); fixture.Tracker.Tick();
        Assert.Equal(7, Assert.Single(fixture.Tracker.Snapshot.Rows).Current);
        fixture.Source.ChangeOwner(another); fixture.Source.Unready(another); fixture.Tracker.Tick();
        var row = Assert.Single(fixture.Tracker.Snapshot.Rows);
        Assert.Equal(another, fixture.Tracker.Snapshot.Binding); Assert.False(fixture.Tracker.Snapshot.IsLive);
        Assert.False(fixture.Source.CurrentOwnSnapshot.HasProgressEvidence);
        Assert.Null(row.Current); Assert.Null(row.Target); Assert.Null(row.RawValue); Assert.False(row.IsHistorical);
        Assert.False(row.IsAnimating); Assert.Equal(titleFavorite, row.IsTitlePlaceholder);
        // Each assignment retains its pins, without carrying the former owner's progress.
        Assert.True(fixture.Favorites.Get(Owner).ContainsTitle(title) || fixture.Favorites.Get(Owner).Contains(Riding));
        Assert.True(fixture.Favorites.Get(another).ContainsTitle(title) || fixture.Favorites.Get(another).Contains(Riding));
        fixture.Tracker.Tick();
        Assert.Null(Assert.Single(fixture.Tracker.Snapshot.Rows).Current);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LivePartialRecoveryNeverCrossfillsAMissingFavoriteFromSavedFactsAndLaterLiveFactsReplaceIt(bool titleFavorite)
    {
        const int title = 23030006;
        using var fixture = new Fixture();
        if (titleFavorite) fixture.Favorites.ToggleTitle(Owner, title);
        else fixture.Favorites.Toggle(Owner, Riding);
        fixture.Source.Saved(Raw((Riding, 7, 0))); fixture.Tracker.Tick();
        Assert.Equal(7, Assert.Single(fixture.Tracker.Snapshot.Rows).Current);
        fixture.Source.Emit(Raw(), NetworkAchievementObservationKind.RecoveryBaseline); fixture.Tracker.Tick();
        var missing = Assert.Single(fixture.Tracker.Snapshot.Rows);
        Assert.True(fixture.Tracker.Snapshot.IsLive); Assert.False(missing.IsHistorical);
        Assert.Null(missing.Current); Assert.Null(missing.Target); Assert.Null(missing.RawValue); Assert.False(missing.IsAnimating);
        Assert.Equal(titleFavorite, missing.IsTitlePlaceholder);
        fixture.Source.Emit(Raw((Riding, 9, 0)), NetworkAchievementObservationKind.RecoveryBaseline); fixture.Tracker.Tick();
        var live = Assert.Single(fixture.Tracker.Snapshot.Rows);
        Assert.True(fixture.Tracker.Snapshot.IsLive); Assert.False(live.IsHistorical); Assert.False(live.IsTitlePlaceholder);
        Assert.Equal(9, live.Current); Assert.Equal(20, live.Target); Assert.Equal(9, live.RawValue); Assert.False(live.IsAnimating);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LivePartialUsesExactOwnHistoryForMissingFavoritesButEveryObservedLiveCounterTakesPrecedence(bool titleFavorite)
    {
        const int title = 23030006;
        using var fixture = new Fixture();
        if (titleFavorite) fixture.Favorites.ToggleTitle(Owner, title);
        else fixture.Favorites.Toggle(Owner, Riding);
        var history = Historical(Raw((Riding, 7, 0), (Soaring, 7, 0)));
        fixture.Source.Emit(Raw((Unqualified, 2, 0)), NetworkAchievementObservationKind.RecoveryBaseline, history: history);
        fixture.Tracker.Tick();
        var saved = Assert.Single(fixture.Tracker.Snapshot.Rows);
        Assert.True(fixture.Tracker.Snapshot.IsLive); Assert.True(saved.IsHistorical); Assert.False(saved.IsAnimating);
        Assert.False(fixture.Source.CurrentOwnSnapshot.Raw!.Achievements.ContainsKey(Riding));
        Assert.Single(fixture.Source.CurrentOwnSnapshot.Raw.Achievements);
        Assert.Equal(7, saved.Current); Assert.Equal(20, saved.Target); Assert.Equal(7, saved.RawValue);
        Assert.False(saved.HasUnclaimedRewards); Assert.False(saved.IsTitlePlaceholder);
        fixture.Clock.Advance(600); fixture.Tracker.Tick();
        Assert.Equal(7, Assert.Single(fixture.Tracker.Snapshot.Rows).Current);

        // A present but unusable live counter is not a gap that older history may fill.
        fixture.Source.Emit(Raw((Riding, -1, 0)), NetworkAchievementObservationKind.RecoveryBaseline, history: history);
        fixture.Tracker.Tick();
        var invalid = Assert.Single(fixture.Tracker.Snapshot.Rows);
        Assert.False(invalid.IsHistorical); Assert.Null(invalid.Current); Assert.Null(invalid.Target);
        Assert.Equal(-1, invalid.RawValue); Assert.False(invalid.IsAnimating);
        fixture.Source.Emit(Raw((Riding, 9, 0)), NetworkAchievementObservationKind.RecoveryBaseline, history: history);
        fixture.Tracker.Tick();
        var live = Assert.Single(fixture.Tracker.Snapshot.Rows);
        Assert.False(live.IsHistorical); Assert.Equal(9, live.Current); Assert.Equal(20, live.Target);
        Assert.False(live.IsAnimating); Assert.Equal(7, history.Raw!.Achievements[Riding].Count);
    }

    [Theory]
    [InlineData("profile", false)] [InlineData("profile", true)]
    [InlineData("revision", false)] [InlineData("revision", true)]
    [InlineData("metadata", false)] [InlineData("metadata", true)]
    public void LivePartialCannotUseHistoryFromAnotherAssignmentOrMetadataBuild(string mismatch, bool titleFavorite)
    {
        const int title = 23030006;
        using var fixture = new Fixture();
        if (titleFavorite) fixture.Favorites.ToggleTitle(Owner, title);
        else fixture.Favorites.Toggle(Owner, Riding);
        var binding = mismatch switch
        {
            "profile" => new AchievementProfileBinding(new string('c', 32), Owner.Revision),
            "revision" => new AchievementProfileBinding(Owner.ProfileId, new string('d', 32)),
            _ => Owner
        };
        var history = Historical(Raw((Riding, 7, 0)), binding, mismatch == "metadata" ? Build + 1 : Build);
        fixture.Source.Emit(Raw((Unqualified, 2, 0)), NetworkAchievementObservationKind.RecoveryBaseline, history: history);
        fixture.Tracker.Tick();
        var row = Assert.Single(fixture.Tracker.Snapshot.Rows);
        Assert.Equal(Owner, fixture.Tracker.Snapshot.Binding); Assert.True(fixture.Tracker.Snapshot.IsLive);
        Assert.False(row.IsHistorical); Assert.Null(row.Current); Assert.Null(row.Target); Assert.Null(row.RawValue);
        Assert.False(row.IsAnimating); Assert.Equal(titleFavorite, row.IsTitlePlaceholder);
        Assert.False(fixture.Source.CurrentOwnSnapshot.Raw!.Achievements.ContainsKey(Riding));
    }

    [Fact]
    public void TitleFavoriteTracksTheEntireObservedChainAfterItsTitleRewardAndUntilTheFinalClaim()
    {
        const int title = 23010003, family = 400100300;
        using var fixture = new Fixture();
        fixture.Favorites.ToggleTitle(Owner, title);
        // The title is rewarded at stage III, but the favorite tracks the whole native family.
        fixture.Source.Emit(Raw((family, 47, 3)), NetworkAchievementObservationKind.Baseline);
        fixture.Tracker.Tick();
        var row = Assert.Single(fixture.Tracker.Snapshot.Rows);
        Assert.Equal(family, row.FamilyId); Assert.Equal(Stage(family, 4), row.StageId);
        Assert.Equal(title, row.TrackedTitleId); Assert.True(row.IsFavorite); Assert.False(row.IsTitlePlaceholder);
        Assert.Equal(CombatTitleDisplayCatalog.Default.Name(title), row.TitleNameEn);
        fixture.Clock.Advance(600); fixture.Tracker.Tick(); Assert.Single(fixture.Tracker.Snapshot.Rows);
        var stages = Contract(family).Stages;
        var finalTarget = AchievementProgressThresholdCatalog.Default.FindFamily(family)!.Stages[^1].Target;
        fixture.Source.Emit(Raw((family, checked((int)finalTarget), checked((byte)stages.Count))),
            NetworkAchievementObservationKind.Reward);
        fixture.Tracker.Tick(); Assert.Empty(fixture.Tracker.Snapshot.Rows);
        Assert.True(fixture.Favorites.Get(Owner).ContainsTitle(title)); Assert.Empty(fixture.Favorites.Get(Owner).FamilyIds);
    }

    [Fact]
    public void DirectAndTitleFavoritesShareOneFamilyCardWithoutMutatingEitherPreference()
    {
        const int title = 23030006;
        using var fixture = new Fixture();
        fixture.Favorites.Toggle(Owner, Riding); fixture.Favorites.ToggleTitle(Owner, title);
        fixture.Source.Emit(Raw((Riding, 4, 0)), NetworkAchievementObservationKind.Baseline);
        fixture.Tracker.Tick();
        var row = Assert.Single(fixture.Tracker.Snapshot.Rows);
        Assert.Equal(Riding, row.FamilyId); Assert.Equal(title, row.TrackedTitleId);
        fixture.Favorites.ToggleTitle(Owner, title); fixture.Tracker.Tick();
        row = Assert.Single(fixture.Tracker.Snapshot.Rows);
        Assert.Null(row.TrackedTitleId); Assert.True(row.IsFavorite);
        fixture.Favorites.ToggleTitle(Owner, title); fixture.Favorites.Toggle(Owner, Riding); fixture.Tracker.Tick();
        Assert.Equal(title, Assert.Single(fixture.Tracker.Snapshot.Rows).TrackedTitleId);
        Assert.Empty(fixture.Favorites.Get(Owner).FamilyIds);
    }

    [Fact]
    public void UnobservedTitleGoalsStayNeutralAcrossTimeoutsAndOwnSourceBoundaries()
    {
        const int title = 23030006;
        using var fixture = new Fixture();
        fixture.Favorites.ToggleTitle(Owner, title);
        fixture.Source.Unready(Owner); fixture.Tracker.Tick();
        var waiting = Assert.Single(fixture.Tracker.Snapshot.Rows);
        Assert.True(waiting.IsTitlePlaceholder); Assert.Equal(title, waiting.TrackedTitleId);
        Assert.Null(waiting.RawValue); Assert.False(waiting.HasQualifiedGoal); Assert.False(waiting.HasUnclaimedRewards);
        fixture.Clock.Advance(600); fixture.Tracker.Tick(); Assert.Single(fixture.Tracker.Snapshot.Rows);
        fixture.Source.Emit(Raw((Riding, 4, 0)), NetworkAchievementObservationKind.Baseline); fixture.Tracker.Tick();
        Assert.False(Assert.Single(fixture.Tracker.Snapshot.Rows).IsTitlePlaceholder);
        fixture.Source.Invalidate(reuseSequence: true); fixture.Tracker.Tick(); Assert.Empty(fixture.Tracker.Snapshot.Rows);
        var another = new AchievementProfileBinding(new string('c', 32), new string('d', 32));
        fixture.Source.ChangeOwner(another); fixture.Source.Unready(another); fixture.Tracker.Tick();
        Assert.Empty(fixture.Tracker.Snapshot.Rows);
        fixture.Source.ChangeOwner(Owner); fixture.Source.Unready(Owner); fixture.Tracker.Tick();
        Assert.True(Assert.Single(fixture.Tracker.Snapshot.Rows).IsTitlePlaceholder);
    }

    [Fact]
    public void GenericProgressExpiresAfterTwentySecondsAndOnlyANewValueRenewsIt()
    {
        using var fixture = new Fixture();
        fixture.Source.Emit(Raw((Unqualified, 2, 0)), NetworkAchievementObservationKind.Baseline);
        fixture.Tracker.Tick(); Assert.Empty(fixture.Tracker.Snapshot.Rows);
        fixture.Source.Emit(Raw((Unqualified, 3, 0)));
        fixture.Tracker.Tick();
        var row = Assert.Single(fixture.Tracker.Snapshot.Rows);
        Assert.Equal(3, row.RawValue); Assert.False(row.HasQualifiedGoal);
        fixture.Clock.Advance(19);
        fixture.Source.Emit(Raw((Unqualified, 4, 0))); fixture.Tracker.Tick();
        fixture.Clock.Advance(19);
        fixture.Source.Emit(Raw((Unqualified, 4, 0))); fixture.Tracker.Tick();
        Assert.Single(fixture.Tracker.Snapshot.Rows);
        fixture.Clock.Advance(1); fixture.Tracker.Tick();
        Assert.Empty(fixture.Tracker.Snapshot.Rows);
    }

    [Fact]
    public void DefaultVisibilityEndsAtExactlyTwentySecondsFromReceivingTheChange()
    {
        using var fixture = new Fixture();
        fixture.Source.Emit(Raw((Unqualified, 2, 0)), NetworkAchievementObservationKind.Baseline);
        fixture.Source.Emit(Raw((Unqualified, 3, 0))); fixture.Tracker.Tick();
        fixture.Clock.Advance(19.999); fixture.Tracker.Tick(); Assert.Single(fixture.Tracker.Snapshot.Rows);
        fixture.Clock.Advance(.001); fixture.Tracker.Tick(); Assert.Empty(fixture.Tracker.Snapshot.Rows);
        Assert.Equal(20, AchievementTrackerService.ProgressDuration.TotalSeconds);
    }

    [Fact]
    public void LiveDurationCanExtendAnActiveCardButNeverResurrectAnExpiredCard()
    {
        using var fixture = new Fixture();
        fixture.Source.Emit(Raw((Unqualified, 2, 0)), NetworkAchievementObservationKind.Baseline);
        fixture.Source.Emit(Raw((Unqualified, 3, 0))); fixture.Tracker.Tick();
        fixture.Clock.Advance(10); fixture.DisplaySeconds = 40; fixture.Tracker.Tick();
        fixture.Clock.Advance(29.999); fixture.Tracker.Tick(); Assert.Single(fixture.Tracker.Snapshot.Rows);
        fixture.Clock.Advance(.001); fixture.Tracker.Tick(); Assert.Empty(fixture.Tracker.Snapshot.Rows);
        fixture.DisplaySeconds = 300; fixture.Tracker.Tick(); Assert.Empty(fixture.Tracker.Snapshot.Rows);
        fixture.Source.Emit(Raw((Unqualified, 3, 0))); fixture.Tracker.Tick(); Assert.Empty(fixture.Tracker.Snapshot.Rows);
        fixture.Source.Emit(Raw((Unqualified, 4, 0))); fixture.Tracker.Tick(); Assert.Single(fixture.Tracker.Snapshot.Rows);
    }

    [Fact]
    public void ShorteningTheLiveDurationExpiresTheExistingCardWithoutRestartingItsDeadline()
    {
        using var fixture = new Fixture(); fixture.DisplaySeconds = 40;
        fixture.Source.Emit(Raw((Unqualified, 2, 0)), NetworkAchievementObservationKind.Baseline);
        fixture.Source.Emit(Raw((Unqualified, 3, 0))); fixture.Tracker.Tick();
        fixture.Clock.Advance(25); fixture.Tracker.Tick(); Assert.Single(fixture.Tracker.Snapshot.Rows);
        fixture.DisplaySeconds = 20; fixture.Tracker.Tick(); Assert.Empty(fixture.Tracker.Snapshot.Rows);
        fixture.DisplaySeconds = 40; fixture.Tracker.Tick(); Assert.Empty(fixture.Tracker.Snapshot.Rows);
    }

    [Fact]
    public void OpenBaselineRewardsExpireOnceAndUnrelatedUpdatesCannotSeedThemAgain()
    {
        using var fixture = new Fixture();
        fixture.Source.Emit(Raw((Riding, 5, 0), (Unqualified, 2, 0)), NetworkAchievementObservationKind.Baseline);
        fixture.Tracker.Tick(); Assert.True(Assert.Single(fixture.Tracker.Snapshot.Rows).HasUnclaimedRewards);
        fixture.Clock.Advance(20); fixture.Tracker.Tick(); Assert.Empty(fixture.Tracker.Snapshot.Rows);
        fixture.DisplaySeconds = 40;
        fixture.Source.Emit(Raw((Riding, 5, 0), (Unqualified, 3, 0))); fixture.Tracker.Tick();
        Assert.Equal(Unqualified, Assert.Single(fixture.Tracker.Snapshot.Rows).FamilyId);
        fixture.Source.Emit(Raw((Riding, 6, 0), (Unqualified, 3, 0))); fixture.Tracker.Tick();
        var renewed = Assert.Single(fixture.Tracker.Snapshot.Rows, row => row.FamilyId == Riding);
        Assert.True(renewed.HasUnclaimedRewards); Assert.False(renewed.IsAnimating);
    }

    [Fact]
    public void NonfavoriteCompletionAnimationAndOpenRewardsEndWithTheConfiguredWindow()
    {
        using var fixture = new Fixture();
        fixture.Source.Emit(Raw((Riding, 4, 0)), NetworkAchievementObservationKind.Baseline);
        var completed = fixture.Source.Milestone(Riding, 1, AchievementMilestoneKind.Completed);
        fixture.Source.Emit(Raw((Riding, 5, 0)), milestones: [completed]); fixture.Tracker.Tick();
        fixture.Clock.Advance(10); fixture.Tracker.Tick();
        Assert.Equal(1d / 6, Assert.Single(fixture.Tracker.Snapshot.Rows).AnimationProgress, 5);
        fixture.Clock.Advance(9);
        fixture.Source.Emit(Raw((Riding, 5, 0)), milestones: [completed]); fixture.Tracker.Tick();
        Assert.True(Assert.Single(fixture.Tracker.Snapshot.Rows).IsAnimating);
        fixture.Clock.Advance(1); fixture.Tracker.Tick(); Assert.Empty(fixture.Tracker.Snapshot.Rows);
        fixture.DisplaySeconds = 40; fixture.Tracker.Tick(); Assert.Empty(fixture.Tracker.Snapshot.Rows);
        fixture.Source.Emit(Raw((Riding, 6, 0))); fixture.Tracker.Tick();
        Assert.False(Assert.Single(fixture.Tracker.Snapshot.Rows).IsAnimating);
        fixture.Source.Emit(Raw((Riding, 20, 0)), milestones: [fixture.Source.Milestone(Riding, 2, AchievementMilestoneKind.Completed)]);
        fixture.Tracker.Tick(); Assert.Equal(0, Assert.Single(fixture.Tracker.Snapshot.Rows).AnimationProgress);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovingAnOldDirectOrTitleFavoriteDoesNotStartANewAutomaticWindow(bool titleFavorite)
    {
        using var fixture = new Fixture();
        void Toggle()
        { if (titleFavorite) fixture.Favorites.ToggleTitle(Owner, 23030006); else fixture.Favorites.Toggle(Owner, Riding); }
        Toggle(); fixture.Source.Emit(Raw((Riding, 5, 0)), NetworkAchievementObservationKind.Baseline);
        fixture.Tracker.Tick(); fixture.Clock.Advance(300); fixture.Tracker.Tick();
        Assert.True(Assert.Single(fixture.Tracker.Snapshot.Rows).IsFavorite);
        Toggle(); fixture.Tracker.Tick(); Assert.Empty(fixture.Tracker.Snapshot.Rows);
        fixture.DisplaySeconds = 300; fixture.Tracker.Tick(); Assert.Empty(fixture.Tracker.Snapshot.Rows);
        Toggle(); fixture.Tracker.Tick(); Assert.True(Assert.Single(fixture.Tracker.Snapshot.Rows).IsFavorite);
    }

    [Fact]
    public void FavoriteCompletionAnimatesForSixtySecondsThenKeepsEarlierOpenRewardsWithoutFreezingTheStage()
    {
        using var fixture = new Fixture();
        fixture.Favorites.Toggle(Owner, Riding);
        fixture.Source.Emit(Raw((Riding, 4, 0)), NetworkAchievementObservationKind.Baseline);
        fixture.Source.Emit(Raw((Riding, 5, 0)), milestones: [fixture.Source.Milestone(Riding, 1, AchievementMilestoneKind.Completed)]);
        fixture.Tracker.Tick();
        var row = Assert.Single(fixture.Tracker.Snapshot.Rows);
        Assert.Equal(AchievementTrackerRowState.Celebrating, row.State); Assert.Equal(0, row.AnimationProgress);
        Assert.Equal(Stage(Riding, 2), row.StageId); Assert.Equal(Stage(Riding, 1), row.CelebratedStageId);
        Assert.Equal(5, row.Current); Assert.Equal(20, row.Target);
        Assert.False(row.IsCompleted); Assert.True(row.HasUnclaimedRewards);
        fixture.Clock.Advance(30);
        // A duplicate milestone with a different observation cannot restart the same stage's deadline.
        fixture.Source.Emit(Raw((Riding, 5, 0)), milestones: [fixture.Source.Milestone(Riding, 1, AchievementMilestoneKind.Completed)]);
        fixture.Tracker.Tick();
        Assert.Equal(.5, Assert.Single(fixture.Tracker.Snapshot.Rows).AnimationProgress, 5);
        fixture.Clock.Advance(30); fixture.Tracker.Tick();
        row = Assert.Single(fixture.Tracker.Snapshot.Rows);
        Assert.Equal(AchievementTrackerRowState.InProgress, row.State); Assert.False(row.IsAnimating);
        Assert.Equal(Stage(Riding, 2), row.StageId); Assert.True(row.HasUnclaimedRewards);
        fixture.Clock.Advance(600); fixture.Tracker.Tick();
        Assert.True(Assert.Single(fixture.Tracker.Snapshot.Rows).HasUnclaimedRewards);
    }

    [Fact]
    public void ClaimImmediatelyRemovesANonfavoriteAndDoesNotSeedTheAlreadyCompletedNextTier()
    {
        using var fixture = new Fixture();
        fixture.Source.Emit(Raw((Riding, 4, 0)), NetworkAchievementObservationKind.Baseline);
        fixture.Source.Emit(Raw((Riding, 50, 0)), milestones: [fixture.Source.Milestone(Riding, 1, AchievementMilestoneKind.Completed),
            fixture.Source.Milestone(Riding, 2, AchievementMilestoneKind.Completed)]);
        fixture.Tracker.Tick(); Assert.True(Assert.Single(fixture.Tracker.Snapshot.Rows).IsAnimating);
        fixture.Source.Emit(Raw((Riding, 50, 1)), NetworkAchievementObservationKind.Reward,
            [fixture.Source.Milestone(Riding, 1, AchievementMilestoneKind.RewardClaimed)]);
        fixture.Tracker.Tick(); Assert.Empty(fixture.Tracker.Snapshot.Rows);
        fixture.Clock.Advance(80); fixture.Tracker.Tick(); Assert.Empty(fixture.Tracker.Snapshot.Rows);
        fixture.Source.Emit(Raw((Riding, 51, 1))); fixture.Tracker.Tick();
        var next = Assert.Single(fixture.Tracker.Snapshot.Rows);
        Assert.Equal(Stage(Riding, 4), next.StageId);
        Assert.Equal(AchievementTrackerRowState.InProgress, next.State); Assert.False(next.IsAnimating);
        Assert.True(next.HasUnclaimedRewards); Assert.Equal(51, next.Current); Assert.Equal(100, next.Target);
    }

    [Fact]
    public void FavoriteFollowsItsCurrentProgressGoalWithoutReplayingAnOlderUnclaimedTier()
    {
        using var fixture = new Fixture();
        fixture.Favorites.Toggle(Owner, Riding);
        fixture.Source.Emit(Raw((Riding, 4, 0)), NetworkAchievementObservationKind.Baseline);
        fixture.Source.Emit(Raw((Riding, 50, 0)), milestones: [fixture.Source.Milestone(Riding, 1, AchievementMilestoneKind.Completed),
            fixture.Source.Milestone(Riding, 2, AchievementMilestoneKind.Completed)]);
        fixture.Tracker.Tick();
        fixture.Source.Emit(Raw((Riding, 50, 1)), NetworkAchievementObservationKind.Reward,
            [fixture.Source.Milestone(Riding, 1, AchievementMilestoneKind.RewardClaimed)]);
        fixture.Tracker.Tick();
        var row = Assert.Single(fixture.Tracker.Snapshot.Rows);
        Assert.True(row.IsFavorite); Assert.Equal(Stage(Riding, 4), row.StageId);
        Assert.Equal(AchievementTrackerRowState.InProgress, row.State); Assert.False(row.IsAnimating);
        Assert.True(row.HasUnclaimedRewards); Assert.Equal(50, row.Current); Assert.Equal(100, row.Target);
        Assert.Equal(CombatAchievementDisplayCatalog.Default.Find(Riding)!.Stages.Single(stage => stage.Id == row.StageId).NameEn, row.NameEn);
    }

    [Fact]
    public void LastTierClaimHidesTheFavoriteWithoutDeletingItsPin()
    {
        using var fixture = new Fixture();
        var stages = Contract(Riding).Stages;
        fixture.Favorites.Toggle(Owner, Riding);
        fixture.Source.Emit(Raw((Riding, checked((int)stages[^1].Target!.Value), checked((byte)(stages.Count - 1)))),
            NetworkAchievementObservationKind.Baseline);
        fixture.Tracker.Tick(); Assert.Single(fixture.Tracker.Snapshot.Rows);
        fixture.Source.Emit(Raw((Riding, checked((int)stages[^1].Target!.Value), checked((byte)stages.Count))),
            NetworkAchievementObservationKind.Reward, [fixture.Source.Milestone(Riding, stages.Count, AchievementMilestoneKind.RewardClaimed)]);
        fixture.Tracker.Tick(); Assert.Empty(fixture.Tracker.Snapshot.Rows);
        Assert.True(fixture.Favorites.Get(Owner).Contains(Riding));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompletionAndClaimBeforeOnePresentationTickNeverFlashAClaimedTier(bool favorite)
    {
        using var fixture = new Fixture();
        if (favorite) fixture.Favorites.Toggle(Owner, Riding);
        fixture.Source.Emit(Raw((Riding, 4, 0)), NetworkAchievementObservationKind.Baseline);
        fixture.Source.Emit(Raw((Riding, 5, 0)), milestones: [fixture.Source.Milestone(Riding, 1, AchievementMilestoneKind.Completed)]);
        fixture.Source.Emit(Raw((Riding, 5, 1)), NetworkAchievementObservationKind.Reward,
            [fixture.Source.Milestone(Riding, 1, AchievementMilestoneKind.RewardClaimed)]);
        fixture.Tracker.Tick();
        if (!favorite) Assert.Empty(fixture.Tracker.Snapshot.Rows);
        else
        {
            var row = Assert.Single(fixture.Tracker.Snapshot.Rows);
            Assert.Equal(Stage(Riding, 2), row.StageId); Assert.False(row.IsAnimating);
            Assert.Equal(AchievementTrackerRowState.InProgress, row.State);
        }
    }

    [Fact]
    public void LoginSeedsConfirmedPendingCardsWithoutAnimations()
    {
        using var fixture = new Fixture();
        fixture.Source.Emit(Raw((Riding, 5, 0)), NetworkAchievementObservationKind.Baseline);
        fixture.Tracker.Tick();
        var row = Assert.Single(fixture.Tracker.Snapshot.Rows);
        Assert.False(row.IsFavorite); Assert.False(row.IsAnimating);
        Assert.Equal(AchievementTrackerRowState.InProgress, row.State);
        Assert.True(row.HasUnclaimedRewards); Assert.Equal(Stage(Riding, 2), row.StageId);
    }

    [Fact]
    public void FavoritesRemainInPinOrderThenPendingNewestFirstThenRecentProgress()
    {
        using var fixture = new Fixture();
        const int levelFamily = 401100100;
        fixture.Favorites.Toggle(Owner, levelFamily);
        fixture.Favorites.Toggle(Owner, Unqualified);
        fixture.Source.Emit(Raw((levelFamily, 1, 0), (Unqualified, 2, 0), (Riding, 4, 0), (Soaring, 4, 0)), NetworkAchievementObservationKind.Baseline);
        fixture.Source.Emit(Raw((levelFamily, 1, 0), (Unqualified, 3, 0), (Riding, 5, 0), (Soaring, 4, 0)),
            milestones: [fixture.Source.Milestone(Riding, 1, AchievementMilestoneKind.Completed)]);
        fixture.Clock.Advance(1);
        fixture.Source.Emit(Raw((levelFamily, 1, 0), (Unqualified, 4, 0), (Riding, 5, 0), (Soaring, 5, 0)),
            milestones: [fixture.Source.Milestone(Soaring, 1, AchievementMilestoneKind.Completed)]);
        fixture.Tracker.Tick();
        Assert.Equal(new[] { levelFamily, Unqualified, Soaring, Riding }, fixture.Tracker.Snapshot.Rows.Select(row => row.FamilyId));
        Assert.All(fixture.Tracker.Snapshot.Rows.Take(2), row => Assert.True(row.IsFavorite));
        Assert.Equal(1, fixture.Tracker.Snapshot.Rows.Count(row => row.FamilyId == Unqualified));
    }

    [Fact]
    public void BoundButUnreadyFavoriteIsNeutralAndAnOwnerChangeCannotBorrowIt()
    {
        using var fixture = new Fixture();
        fixture.Favorites.Toggle(Owner, Riding);
        fixture.Source.Unready(Owner); fixture.Tracker.Tick();
        var row = Assert.Single(fixture.Tracker.Snapshot.Rows);
        Assert.True(row.IsFavorite); Assert.Equal(AchievementTrackerRowState.Unknown, row.State);
        Assert.Null(row.Current); Assert.Null(row.Target); Assert.Null(row.RawValue);
        fixture.Source.ChangeOwner(new(new string('c', 32), new string('d', 32)));
        fixture.Source.Emit(Raw((Riding, 4, 0)), NetworkAchievementObservationKind.Baseline);
        fixture.Tracker.Tick(); Assert.Empty(fixture.Tracker.Snapshot.Rows);
        fixture.Source.Invalidate(reuseSequence: true); fixture.Tracker.Tick();
        Assert.Null(fixture.Tracker.Snapshot.Binding);
    }

    [Fact]
    public void AClaimInPermanentHistoryCannotBecomePendingAgainAfterARawPrefixRegression()
    {
        using var fixture = new Fixture();
        fixture.Favorites.Toggle(Owner, Riding);
        fixture.Source.AddHistory(fixture.Source.Milestone(Riding, 1, AchievementMilestoneKind.RewardClaimed) with { NotificationEligible = false });
        fixture.Source.Emit(Raw((Riding, 5, 0)), NetworkAchievementObservationKind.Baseline);
        fixture.Tracker.Tick();
        var row = Assert.Single(fixture.Tracker.Snapshot.Rows);
        Assert.Equal(Stage(Riding, 2), row.StageId); Assert.False(row.IsAnimating);
        Assert.Equal(AchievementTrackerRowState.InProgress, row.State);
    }

    [Fact]
    public void BaselineRecoveryGapAndEqualSequenceInvalidationClearTransientPresentation()
    {
        using var fixture = new Fixture();
        fixture.Source.Emit(Raw((Unqualified, 2, 0)), NetworkAchievementObservationKind.Baseline);
        fixture.Source.Emit(Raw((Unqualified, 3, 0))); fixture.Tracker.Tick();
        Assert.Single(fixture.Tracker.Snapshot.Rows);
        fixture.Source.Emit(Raw((Unqualified, 7, 0)), NetworkAchievementObservationKind.RecoveryBaseline, historyGap: true);
        fixture.Tracker.Tick(); Assert.Empty(fixture.Tracker.Snapshot.Rows);
        fixture.Source.Emit(Raw((Unqualified, 8, 0))); fixture.Tracker.Tick(); Assert.Single(fixture.Tracker.Snapshot.Rows);
        fixture.Source.Invalidate(reuseSequence: true); fixture.Tracker.Tick(); Assert.Empty(fixture.Tracker.Snapshot.Rows);
    }

    [Fact]
    public void DelayedPaintingDoesNotRestartAnAlreadyExpiredProgressDeadline()
    {
        using var fixture = new Fixture();
        fixture.Source.Emit(Raw((Unqualified, 2, 0)), NetworkAchievementObservationKind.Baseline);
        fixture.Source.Emit(Raw((Unqualified, 3, 0)));
        fixture.Clock.Advance(21); fixture.Tracker.Tick();
        Assert.Empty(fixture.Tracker.Snapshot.Rows);
    }

    [Fact]
    public void UnknownReferenceUnnamedAndMismatchedMetadataActivitiesCannotCreateCards()
    {
        var (definitions, display, contracts) = SyntheticCatalogs();
        using var fixture = new Fixture(new(display, contracts, definitions), metadataBuild: 42);
        fixture.Source.Emit(Raw((100, 1, 0), (200, 1, 0), (300, 1, 0), (400, 1, 0), (999999, 1, 0)), NetworkAchievementObservationKind.Baseline);
        fixture.Source.Emit(Raw((100, 2, 0), (200, 2, 0), (300, 2, 0), (400, 2, 0), (999999, 2, 0)));
        fixture.Tracker.Tick();
        Assert.Equal(new[] { 100 }, fixture.Tracker.Snapshot.Rows.Select(row => row.FamilyId).Order());
        fixture.Source.MetadataBuild = 43;
        fixture.Source.Emit(Raw((100, 3, 0), (400, 3, 0))); fixture.Tracker.Tick();
        Assert.Empty(fixture.Tracker.Snapshot.Rows);
    }

    [Fact]
    public void LaggingClaimsDoNotReplaceTheMostAdvancedNumericGoalWithAnOlderOpenReward()
    {
        using var fixture = new Fixture();
        fixture.Source.Emit(Raw((Riding, 51, 0)), NetworkAchievementObservationKind.Baseline);
        fixture.Tracker.Tick();
        var row = Assert.Single(fixture.Tracker.Snapshot.Rows);
        Assert.Equal(Stage(Riding, 4), row.StageId);
        Assert.Equal(51, row.Current); Assert.Equal(100, row.Target);
        Assert.True(row.HasUnclaimedRewards); Assert.False(row.IsCompleted); Assert.False(row.IsAnimating);
        var stage = CombatAchievementDisplayCatalog.Default.Find(Riding)!.Stages.Single(item => item.Id == row.StageId);
        Assert.Equal(stage.NameEn, row.NameEn); Assert.Equal(stage.ConditionEn, row.ConditionEn);
        Assert.Equal(stage.Rewards.Select(item => item.Quantity), row.Rewards.Select(item => item.Quantity));
    }

    [Fact]
    public void NativeThresholdChainUsesTheFortySevenOfFiftyStageDespiteOnlyTwoClaimedRewards()
    {
        const int family = 400100300;
        using var fixture = new Fixture();
        fixture.Source.Emit(Raw((family, 47, 2)), NetworkAchievementObservationKind.Baseline);
        fixture.Tracker.Tick();
        var row = Assert.Single(fixture.Tracker.Snapshot.Rows);
        Assert.Equal(Stage(family, 4), row.StageId);
        Assert.Equal(47, row.Current); Assert.Equal(50, row.Target);
        Assert.True(row.HasUnclaimedRewards); Assert.False(row.IsCompleted);
        Assert.Equal(CombatAchievementDisplayCatalog.Default.Find(family)!.Stages.Single(stage => stage.Id == row.StageId).NameEn, row.NameEn);
    }

    [Fact]
    public void ACompletionPulseContinuesOverFreshCountsAndTheCurrentStageMetadata()
    {
        using var fixture = new Fixture();
        fixture.Source.Emit(Raw((Riding, 4, 0)), NetworkAchievementObservationKind.Baseline);
        fixture.Source.Emit(Raw((Riding, 5, 0)), milestones: [fixture.Source.Milestone(Riding, 1, AchievementMilestoneKind.Completed)]);
        fixture.Tracker.Tick();
        fixture.Clock.Advance(10);
        fixture.Source.Emit(Raw((Riding, 7, 0))); fixture.Tracker.Tick();
        var row = Assert.Single(fixture.Tracker.Snapshot.Rows);
        Assert.Equal(Stage(Riding, 2), row.StageId); Assert.Equal(Stage(Riding, 1), row.CelebratedStageId);
        Assert.Equal(7, row.Current); Assert.Equal(20, row.Target);
        Assert.Equal(1d / 6, row.AnimationProgress, 5); Assert.False(row.IsCompleted);
        fixture.Clock.Advance(10);
        fixture.Source.Emit(Raw((Riding, 20, 0)), milestones: [fixture.Source.Milestone(Riding, 2, AchievementMilestoneKind.Completed)]);
        fixture.Tracker.Tick();
        row = Assert.Single(fixture.Tracker.Snapshot.Rows);
        Assert.Equal(Stage(Riding, 3), row.StageId); Assert.Equal(Stage(Riding, 2), row.CelebratedStageId);
        Assert.Equal(20, row.Current); Assert.Equal(50, row.Target); Assert.Equal(0, row.AnimationProgress);
        Assert.Equal(CombatAchievementDisplayCatalog.Default.Find(Riding)!.Stages.Single(stage => stage.Id == row.StageId).NameEn, row.NameEn);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheHighestCompletedNativeStageKeepsItsPulseWhenLowerMilestonesArriveLater(bool sameBatch)
    {
        using var fixture = new Fixture();
        fixture.Source.Emit(Raw((Riding, 4, 0)), NetworkAchievementObservationKind.Baseline);
        var higher = fixture.Source.Milestone(Riding, 2, AchievementMilestoneKind.Completed);
        var lower = fixture.Source.Milestone(Riding, 1, AchievementMilestoneKind.Completed);
        fixture.Source.Emit(Raw((Riding, 21, 0)), milestones: sameBatch ? [higher, lower] : [higher]);
        fixture.Tracker.Tick();
        if (!sameBatch)
        {
            fixture.Clock.Advance(10);
            fixture.Source.Emit(Raw((Riding, 22, 0)), milestones: [lower]);
            fixture.Tracker.Tick();
        }
        var row = Assert.Single(fixture.Tracker.Snapshot.Rows);
        Assert.Equal(Stage(Riding, 3), row.StageId);
        Assert.Equal(Stage(Riding, 2), row.CelebratedStageId);
        Assert.Equal(sameBatch ? 0 : 1d / 6, row.AnimationProgress, 5);
        Assert.Equal(sameBatch ? 21 : 22, row.Current); Assert.Equal(50, row.Target);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FirstObservedHistoricalClaimsDoNotSuppressANewCurrentProgressCard(bool withHistoricalMilestone)
    {
        using var fixture = new Fixture();
        fixture.Source.Emit(Raw(), NetworkAchievementObservationKind.Baseline);
        fixture.Tracker.Tick();
        var historical = fixture.Source.Milestone(Riding, 1, AchievementMilestoneKind.RewardClaimed)
            with { NotificationEligible = false, Origin = "Baseline" };
        fixture.Source.Emit(Raw((Riding, 7, 1)), milestones: withHistoricalMilestone ? [historical] : []);
        fixture.Tracker.Tick();
        var row = Assert.Single(fixture.Tracker.Snapshot.Rows);
        Assert.Equal(Stage(Riding, 2), row.StageId);
        Assert.Equal(7, row.Current); Assert.Equal(20, row.Target);
        Assert.False(row.IsAnimating); Assert.False(row.HasUnclaimedRewards);
    }

    [Fact]
    public void ClaimOnlyRowsKeepTheirExplicitUnclaimedStageWhileLeavingTheGoalUnknown()
    {
        using var fixture = new Fixture();
        fixture.Favorites.Toggle(Owner, Unqualified);
        fixture.Source.Emit(Raw((Unqualified, 17, 1)), NetworkAchievementObservationKind.Baseline);
        fixture.Tracker.Tick();
        var row = Assert.Single(fixture.Tracker.Snapshot.Rows);
        Assert.Equal(Stage(Unqualified, 2), row.StageId); Assert.Equal(17, row.RawValue);
        Assert.Null(row.Current); Assert.Null(row.Target); Assert.False(row.HasQualifiedGoal);
        Assert.Equal(CombatAchievementDisplayCatalog.Default.Find(Unqualified)!.Stages.Single(stage => stage.Id == row.StageId).NameEn, row.NameEn);
    }

    [Theory]
    [InlineData("Normal")]
    [InlineData("Season")]
    [InlineData("Event")]
    [InlineData("BattlePass")]
    [InlineData("Attendance")]
    [InlineData("Onboarding")]
    public void CandidateSystemsWithMatchingTextAppearOnlyWhenExplicitlyFavorited(string candidateArea)
    {
        var (definitions, display, contracts) = SyntheticCatalogs(candidateArea);
        using var fixture = new Fixture(new(display, contracts, definitions), metadataBuild: 42, definitions: definitions);
        fixture.Source.Emit(Raw((100, 1, 0), (400, 1, 0)), NetworkAchievementObservationKind.Baseline);
        fixture.Source.Emit(Raw((100, 2, 0), (400, 2, 0))); fixture.Tracker.Tick();
        Assert.Equal(new[] { 100 }, fixture.Tracker.Snapshot.Rows.Select(row => row.FamilyId));
        fixture.Favorites.Toggle(Owner, 400); fixture.Tracker.Tick();
        Assert.Equal(new[] { 400, 100 }, fixture.Tracker.Snapshot.Rows.Select(row => row.FamilyId));
        var candidate = fixture.Tracker.Snapshot.Rows[0];
        Assert.True(candidate.IsFavorite); Assert.Equal(2, candidate.RawValue); Assert.False(candidate.HasQualifiedGoal);
        Assert.Equal(2, fixture.Tracker.Snapshot.Rows[1].RawValue);
    }

    private static CombatAchievementFamilyContract Contract(int family) => CombatAchievementProgressContractCatalog.Default.FindFamily(family)!
        .Families.Single(item => item.Id == family);
    private static int Stage(int family, int ordinal) => Contract(family).Stages.Single(stage => stage.Ordinal == ordinal).Id;
    private static CharacterAchievementState Raw(params (int Id, int Count, byte Claimed)[] rows) => new(
        rows.ToImmutableDictionary(row => row.Id, row => new CharacterAchievement(row.Count, 0, row.Claimed)),
        ImmutableDictionary<int, ImmutableArray<bool>>.Empty);
    private static AchievementOwnSnapshot Historical(CharacterAchievementState raw, AchievementProfileBinding? binding = null,
        int? metadataBuild = null)
    {
        binding ??= Owner;
        var state = new AchievementViewState
        {
            Binding = binding, ProfileId = binding.ProfileId, Source = AchievementViewSource.Saved,
            ObservedAt = DateTimeOffset.UnixEpoch, MetadataBuild = metadataBuild ?? Build, Generation = 1, Sequence = 1,
            Progress = new AchievementRuleEvaluator().Evaluate(raw, metadataBuild ?? Build, 1, 1)
        };
        return new(binding, state, raw, 0);
    }

    private static (AchievementDefinitionCatalog Definitions, CombatAchievementDisplayCatalog Display,
        CombatAchievementProgressContractCatalog Contracts) SyntheticCatalogs(string candidateArea = "Event")
    {
        var display = CombatAchievementDisplayCatalog.FromJson(JsonSerializer.Serialize(new
        {
            schema = 1, metadataBuild = 42, referenceOnly = true, doesNotEstablishCompletionOrClaim = true,
            categories = Array.Empty<object>(), groups = Array.Empty<object>(), achievements = new[] { 100, 200, 300, 400 }.Select(id => new
            {
                id, nameDe = "Synthetischer Erfolg " + id, nameEn = "Synthetic achievement " + id,
                conditionDe = "Synthetische Bedingung", conditionEn = "Synthetic condition", rewards = Array.Empty<object>(),
                hasNativeName = id != 300, stages = new[] { new { id = id + 1, nameDe = "Stufe I", nameEn = "Stage I",
                    conditionDe = "Bedingung", conditionEn = "Condition", nativeTarget = 5, rewards = Array.Empty<object>() } },
            }).ToArray(),
        }));
        var definitions = AchievementDefinitionCatalog.FromJson(JsonSerializer.Serialize(new
        {
            schema = 1, metadataBuild = 42, definitions = new[] { 100, 200, 300, 400 }.Select(id => new
            {
                id, area = id == 200 ? "Reference" : id == 400 ? candidateArea : "Normal",
                role = id == 200 ? "Reference" : id == 400 ? "Candidate" : "PlayerFacing",
                semanticGroup = "synthetic", resetPolicy = "None", signature = new string('a', 64), stageIds = new[] { id + 1 }, reason = "synthetic",
            }).ToArray(),
        }));
        var contracts = CombatAchievementProgressContractCatalog.FromJson(JsonSerializer.Serialize(new
        {
            schema = 2, metadataBuild = 42, contracts = new[] { new { id = "synthetic", counterRule = "Cumulative", unit = "Count",
                completionRule = "AtLeastTarget", claimRule = "ContiguousRewardOrdinal", families = new[] { new { id = 100,
                    stages = new[] { new { id = 101, ordinal = 1, target = 5 } } } } } },
        }), display);
        return (definitions, display, contracts);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "soulcrest-achievement-tracker-" + Guid.NewGuid().ToString("N"));
        internal Clock Clock { get; } = new();
        internal Source Source { get; }
        internal AchievementFavoriteService Favorites { get; }
        internal AchievementTrackerService Tracker { get; }
        internal int DisplaySeconds { get; set; } = 20;
        internal Fixture(AchievementTrackerStageResolver? stages = null, int? metadataBuild = null,
            AchievementDefinitionCatalog? definitions = null)
        {
            Directory.CreateDirectory(_root);
            Source = new(Clock) { MetadataBuild = metadataBuild ?? Build };
            Favorites = definitions is null ? new(_root)
                : new(_root, null, definitions, (path, value) => JsonFile.Save(path, value));
            Tracker = new(Source, Favorites, stages ?? new(), Clock, readDisplaySeconds: () => DisplaySeconds);
        }
        public void Dispose() { Tracker.Dispose(); Favorites.Dispose(); Directory.Delete(_root, true); }
    }

    private sealed class Clock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(_timestamp);
        internal void Advance(double seconds) => _timestamp += (long)Math.Round(seconds * TimeSpan.TicksPerSecond);
    }

    private sealed class Source(Clock clock) : IAchievementTrackerSource
    {
        private readonly Guid _instance = Guid.NewGuid();
        private readonly Dictionary<string, AchievementMilestone> _history = new(StringComparer.Ordinal);
        private AchievementProfileBinding _binding = Owner;
        private long _sequence, _generation = 1;
        internal int MetadataBuild { get; set; } = Build;
        public AchievementOwnSnapshot CurrentOwnSnapshot { get; private set; } = AchievementOwnSnapshot.Empty;
        public event Action<AchievementChangeBatch>? AchievementObserved;

        internal void AddHistory(AchievementMilestone milestone) => _history[milestone.Id] = milestone;
        internal AchievementMilestone Milestone(int family, int ordinal, AchievementMilestoneKind kind) => new(
            $"synthetic-{family}-{ordinal}-{kind}", kind, family, Stage(family, ordinal), clock.GetUtcNow(), null, "Live", MetadataBuild,
            AchievementRuleEvaluator.Version, "permanent", Raw((family, 5, 0))) { NotificationEligible = true };

        internal void Emit(CharacterAchievementState raw, NetworkAchievementObservationKind kind = NetworkAchievementObservationKind.Update,
            ImmutableArray<AchievementMilestone> milestones = default, bool historyGap = false, AchievementOwnSnapshot? history = null)
        {
            var activities = ImmutableArray.CreateBuilder<AchievementActivity>();
            if (kind == NetworkAchievementObservationKind.Update && CurrentOwnSnapshot.Raw is { } previous)
                foreach (var (family, row) in raw.Achievements)
                {
                    var old = previous.Achievements.GetValueOrDefault(family);
                    if (old is null || old.Count != row.Count) activities.Add(new(family, old?.Count, row.Count, clock.GetUtcNow()));
                }
            if (milestones.IsDefault) milestones = [];
            foreach (var milestone in milestones) AddHistory(milestone);
            _sequence++;
            var earned = _history.Values.Where(item => item.Kind == AchievementMilestoneKind.Completed && item.StageId is not null)
                .Select(item => item.StageId!.Value).ToHashSet();
            var claimed = _history.Values.Where(item => item.Kind == AchievementMilestoneKind.RewardClaimed && item.StageId is not null)
                .Select(item => item.StageId!.Value).ToHashSet();
            var state = new AchievementViewState
            {
                Binding = _binding, ProfileId = _binding.ProfileId, Source = AchievementViewSource.Live,
                MetadataBuild = MetadataBuild, Generation = _generation, Sequence = _sequence,
                Milestones = _history.Values.ToImmutableArray(), Progress = new AchievementRuleEvaluator().Evaluate(raw, MetadataBuild, _generation, _sequence, earned, claimed),
            };
            CurrentOwnSnapshot = new(_binding, state, raw, clock.GetTimestamp()) { SourceInstanceId = _instance, History = history };
            AchievementObserved?.Invoke(new(CurrentOwnSnapshot, kind, activities.ToImmutable(), milestones, historyGap,
                _instance, _sequence, _generation, clock.GetTimestamp()));
        }

        internal void Unready(AchievementProfileBinding binding)
        {
            _binding = binding; _sequence++;
            CurrentOwnSnapshot = new(binding, new() { Binding = binding, ProfileId = binding.ProfileId, MetadataBuild = MetadataBuild,
                Generation = _generation, Sequence = _sequence }, null, clock.GetTimestamp()) { SourceInstanceId = _instance };
            AchievementObserved?.Invoke(new(CurrentOwnSnapshot, NetworkAchievementObservationKind.Baseline, [], [], false,
                _instance, _sequence, _generation, clock.GetTimestamp()));
        }
        internal void Saved(CharacterAchievementState raw)
        {
            _sequence++;
            var state = new AchievementViewState
            {
                Binding = _binding, ProfileId = _binding.ProfileId, Source = AchievementViewSource.Saved,
                MetadataBuild = MetadataBuild, Generation = _generation, Sequence = _sequence, ObservedAt = clock.GetUtcNow(),
                Milestones = _history.Values.Select(item => item with { NotificationEligible = false }).ToImmutableArray(),
                Progress = new AchievementRuleEvaluator().Evaluate(raw, MetadataBuild, _generation, _sequence)
            };
            CurrentOwnSnapshot = new(_binding, state, raw, clock.GetTimestamp()) { SourceInstanceId = _instance };
            AchievementObserved?.Invoke(new(CurrentOwnSnapshot, NetworkAchievementObservationKind.Baseline, [], [], false,
                _instance, _sequence, _generation, clock.GetTimestamp()));
        }
        internal void ChangeOwner(AchievementProfileBinding binding) { _binding = binding; _generation++; _history.Clear(); }
        internal void Invalidate(bool reuseSequence)
        {
            if (!reuseSequence) _sequence++;
            CurrentOwnSnapshot = AchievementOwnSnapshot.Empty;
            AchievementObserved?.Invoke(new(CurrentOwnSnapshot, NetworkAchievementObservationKind.Invalidated, [], [], false,
                _instance, _sequence, _generation, clock.GetTimestamp()));
        }
    }
}
