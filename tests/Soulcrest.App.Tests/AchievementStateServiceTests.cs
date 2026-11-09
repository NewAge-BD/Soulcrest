using System.Collections.Immutable;
using System.Text;
using Soulcrest.App.Components;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Combat;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class AchievementStateServiceTests
{
    private const int Riding = 420101100;
    private static readonly DateTimeOffset At = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public async Task AnOlderInvalidationCannotClearANewerOwnProfileCatchup()
    {
        using var fixture = new ProfileFixture();
        fixture.BindOwner();
        fixture.Source.Emit(Raw(4), NetworkAchievementObservationKind.Baseline);
        await fixture.Service.FlushAsync();
        var first = fixture.Source.AchievementSnapshot;
        var delayed = first with
        {
            Sequence = 2, Kind = NetworkAchievementObservationKind.Invalidated,
            Domain = first.Domain with { IsFresh = false, Status = NetworkCombatSourceStatus.Stale },
        };
        fixture.Source.Owner = new(new(202, 7, 43, "Synthetic second owner"), 2);
        var latest = first with
        {
            Sequence = 3, Generation = 2, Character = fixture.Source.Owner, ObservedAt = At.AddSeconds(3),
            Domain = new(Raw(8), NetworkCombatSourceStatus.Complete, true, At.AddSeconds(3), 3, "own-achievement-list"),
        };
        fixture.Source.SetCurrent(latest);
        fixture.BindOwner(); // Local binding catches up to the already-published newest source.
        await fixture.Service.FlushAsync();
        var second = fixture.Service.CurrentOwnSnapshot;
        Assert.True(second.IsLive);
        Assert.Equal(3, second.State.Sequence);
        fixture.Source.Deliver(delayed);
        fixture.Source.Deliver(latest);
        await fixture.Service.FlushAsync();
        Assert.Same(second, fixture.Service.CurrentOwnSnapshot);
        Assert.Equal(8, fixture.Service.CurrentOwnSnapshot.Raw!.Achievements[Riding].Count);
    }

    [Fact]
    public async Task OrderedTrackerBatchesPreserveCompletionAndClaimBeforeViewCoalescing()
    {
        using var fixture = new Fixture();
        var batches = new List<AchievementChangeBatch>();
        fixture.Service.AchievementObserved += batches.Add;
        fixture.Source.Emit(Raw(4), NetworkAchievementObservationKind.Baseline);
        fixture.Source.Emit(Raw(5), NetworkAchievementObservationKind.Update);
        fixture.Source.Emit(Raw(5, 1), NetworkAchievementObservationKind.Reward);
        fixture.Source.Invalidate();
        await fixture.Service.FlushAsync();
        Assert.Equal(new[] { NetworkAchievementObservationKind.Baseline, NetworkAchievementObservationKind.Update,
            NetworkAchievementObservationKind.Reward, NetworkAchievementObservationKind.Invalidated }, batches.Select(item => item.Kind));
        var update = batches[1];
        Assert.Equal(fixture.Binding, update.Snapshot.Binding);
        Assert.Equal(5, Assert.Single(update.Activities).CurrentValue);
        Assert.Contains(update.Milestones, item => item.Kind == AchievementMilestoneKind.Completed && item.NotificationEligible);
        Assert.Equal(0, update.Snapshot.Raw!.Achievements[Riding].RewardedStep);
        Assert.Contains(batches[2].Milestones, item => item.Kind == AchievementMilestoneKind.RewardClaimed && item.NotificationEligible);
        Assert.Equal(1, batches[2].Snapshot.Raw!.Achievements[Riding].RewardedStep);
        Assert.True(update.ReceivedTimestamp > 0);
        Assert.True(update.ReceivedTimestamp <= batches[2].ReceivedTimestamp);
        Assert.Null(batches[3].Snapshot.Binding);
        Assert.Null(fixture.Service.CurrentOwnSnapshot.Binding);
    }

    [Fact]
    public async Task SelectingAnOfflineProfileDoesNotChangeTheActualOwnOverlaySource()
    {
        using var fixture = new ProfileFixture();
        fixture.BindOwner();
        fixture.Source.Emit(Raw(4), NetworkAchievementObservationKind.Baseline);
        await fixture.Service.FlushAsync();
        var own = fixture.Service.CurrentOwnSnapshot;
        Assert.NotNull(own.Binding);
        fixture.Profiles.Add("Synthetic offline selection");
        await fixture.Service.FlushAsync();
        Assert.NotEqual(own.Binding.ProfileId, fixture.Profiles.ActiveId);
        Assert.Same(own, fixture.Service.CurrentOwnSnapshot);
        Assert.True(fixture.Service.CurrentOwnSnapshot.IsLive);
        Assert.Equal(4, fixture.Service.CurrentOwnSnapshot.Raw!.Achievements[Riding].Count);
        fixture.Source.Emit(Raw(5), NetworkAchievementObservationKind.Update);
        await fixture.Service.FlushAsync();
        Assert.Equal(own.Binding, fixture.Service.CurrentOwnSnapshot.Binding);
        Assert.Equal(5, fixture.Service.CurrentOwnSnapshot.Raw!.Achievements[Riding].Count);
    }

    [Fact]
    public async Task ATrackerConsumerCannotBlockOtherConsumersByThrowing()
    {
        using var fixture = new Fixture();
        var count = 0;
        fixture.Service.AchievementObserved += _ => throw new InvalidOperationException("Synthetic subscriber failure");
        fixture.Service.AchievementObserved += _ => count++;
        fixture.Source.Emit(Raw(4), NetworkAchievementObservationKind.Baseline);
        fixture.Source.Emit(Raw(5), NetworkAchievementObservationKind.Update);
        await fixture.Service.FlushAsync();
        Assert.Equal(2, count);
        Assert.Equal(5, fixture.Store.Load(fixture.Binding).Snapshot!.Raw.Achievements[Riding].Count);
    }

    [Fact]
    public async Task UnqualifiedAchievementsReportChangesWithoutNeedingTheirCauseOrTarget()
    {
        using var fixture = new Fixture();
        var first = CombatAchievementProgressContractCatalog.Default.Contracts
            .Where(type => type.CounterRule == CombatAchievementCounterRule.Unqualified).SelectMany(type => type.Families)
            .First(family => AchievementProgressThresholdCatalog.Default.FindFamily(family.Id) is null).Id;
        const int second = 999999;
        var baseline = Rows((first, 2), (second, 7));
        fixture.Source.Emit(baseline, NetworkAchievementObservationKind.Baseline);
        await fixture.Service.FlushAsync();
        Assert.Empty(fixture.Service.View.Activities);
        fixture.Source.Emit(Rows((first, 3), (second, 8)), NetworkAchievementObservationKind.Update);
        await fixture.Service.FlushAsync();
        Assert.Equal(2, fixture.Service.View.Activities.Count);
        Assert.Equal(2, fixture.Service.View.Activities[first].PreviousValue);
        Assert.Equal(3, fixture.Service.View.Activities[first].CurrentValue);
        Assert.Equal(first, fixture.Service.View.Activities[first].FamilyId);
        Assert.Equal(fixture.Source.AchievementSnapshot.ObservedAt, fixture.Service.View.Activities[first].ObservedAt);
        var prefixOnly = Assert.Single(fixture.Service.View.Progress.Families).Value;
        Assert.Equal(first, prefixOnly.Id);
        Assert.All(prefixOnly.Stages.Values, stage => { Assert.Null(stage.Current); Assert.Null(stage.Target); Assert.Null(stage.Completed); });
        Assert.Empty(fixture.Service.View.Milestones);
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.jsonl", SearchOption.AllDirectories));
        Assert.Equal(8, fixture.Store.Load(fixture.Binding).Snapshot!.Raw.Achievements[second].Count);
    }

    [Fact]
    public async Task DuplicateClockAndClaimOnlyChangesDoNotCreateNewCounterActivity()
    {
        using var fixture = new Fixture();
        fixture.Source.Emit(Rows((999999, 2)), NetworkAchievementObservationKind.Baseline);
        var otherFields = new CharacterAchievementState(ImmutableDictionary<int, CharacterAchievement>.Empty.Add(999999, new(2, 12345, 1)),
            ImmutableDictionary<int, ImmutableArray<bool>>.Empty);
        fixture.Source.Emit(otherFields, NetworkAchievementObservationKind.Update);
        fixture.Source.Replay();
        await fixture.Service.FlushAsync();
        Assert.Empty(fixture.Service.View.Activities);
        fixture.Source.Emit(Rows((999999, 3)), NetworkAchievementObservationKind.Update);
        await fixture.Service.FlushAsync();
        var activity = Assert.Single(fixture.Service.View.Activities).Value;
        fixture.Source.Emit(Rows((999999, 3)), NetworkAchievementObservationKind.Update);
        fixture.Source.Emit(Rows((999999, 3)), NetworkAchievementObservationKind.Reward);
        await fixture.Service.FlushAsync();
        Assert.Equal(activity, Assert.Single(fixture.Service.View.Activities).Value);
    }

    [Fact]
    public async Task FirstRowUpdateKeepsTheUnknownPreviousValueAndSignedChangesStayRaw()
    {
        using var fixture = new Fixture();
        fixture.Source.Emit(Rows((999999, int.MinValue)), NetworkAchievementObservationKind.Baseline);
        fixture.Source.Emit(Rows((999999, int.MaxValue), (999998, -1)), NetworkAchievementObservationKind.Update);
        await fixture.Service.FlushAsync();
        Assert.Null(fixture.Service.View.Activities[999998].PreviousValue);
        Assert.Equal(-1, fixture.Service.View.Activities[999998].CurrentValue);
        Assert.Equal(int.MinValue, fixture.Service.View.Activities[999999].PreviousValue);
        Assert.Equal(int.MaxValue, fixture.Service.View.Activities[999999].CurrentValue);
        Assert.Empty(fixture.Service.View.Milestones);
        Assert.Empty(fixture.Service.View.Progress.Families);
    }

    [Fact]
    public async Task LoginRecoveryAndInvalidationNeverReannounceOldProgressChanges()
    {
        using var fixture = new Fixture();
        fixture.Source.Emit(Rows((999999, 2)), NetworkAchievementObservationKind.Baseline);
        fixture.Source.Emit(Rows((999999, 3)), NetworkAchievementObservationKind.Update);
        await fixture.Service.FlushAsync();
        Assert.Single(fixture.Service.View.Activities);
        fixture.Source.Invalidate();
        await fixture.Service.FlushAsync();
        Assert.True(fixture.Service.View.IsSaved);
        Assert.Empty(fixture.Service.View.Activities);
        fixture.Source.Emit(Rows((999999, 4)), NetworkAchievementObservationKind.Baseline, generation: 2);
        await fixture.Service.FlushAsync();
        Assert.Empty(fixture.Service.View.Activities);
        fixture.Source.Emit(Rows((999999, 5)), NetworkAchievementObservationKind.Update);
        await fixture.Service.FlushAsync();
        Assert.Single(fixture.Service.View.Activities);
        fixture.Source.Emit(Rows((999999, 9)), NetworkAchievementObservationKind.RecoveryBaseline, historyGap: true);
        await fixture.Service.FlushAsync();
        Assert.Empty(fixture.Service.View.Activities);
        fixture.Service.Dispose();
        using var restarted = new AchievementStateService(new FakeSource(), fixture.Store, _ => fixture.Binding, () => fixture.Binding);
        await restarted.FlushAsync();
        Assert.True(restarted.View.IsSaved);
        Assert.Empty(restarted.View.Activities);
    }

    private static CharacterAchievementState Rows(params (int Id, int Count)[] rows) => new(
        rows.ToImmutableDictionary(row => row.Id, row => new CharacterAchievement(row.Count, 0, 0)),
        ImmutableDictionary<int, ImmutableArray<bool>>.Empty);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AConfirmedOwnBaselineRestoresSavedTitleProgressWithoutGrantingLiveAuthority(bool retainRaw)
    {
        const int family = 433101200, stage = 433101201;
        var writes = 0;
        using var fixture = new Fixture(save: (path, value) => { Interlocked.Increment(ref writes); JsonFile.Save(path, value); });
        var batches = new List<AchievementChangeBatch>();
        fixture.Service.AchievementObserved += batches.Add;
        fixture.Source.Emit(Rows((family, 11)), NetworkAchievementObservationKind.Baseline);
        await fixture.Service.FlushAsync();
        var saved = fixture.Store.Load(fixture.Binding).Snapshot!;
        fixture.Source.Invalidate();
        await fixture.Service.FlushAsync();
        Assert.Null(fixture.Service.CurrentOwnSnapshot.Binding);
        var writesBefore = Volatile.Read(ref writes);
        fixture.Source.Unready(2, retainRaw);
        await fixture.Service.FlushAsync();

        var own = fixture.Service.CurrentOwnSnapshot;
        Assert.Equal(fixture.Binding, own.Binding); Assert.True(own.IsHistorical); Assert.True(own.HasProgressEvidence);
        Assert.False(own.IsLive); Assert.True(own.State.IsSaved); Assert.Null(own.History);
        Assert.Equal(2, own.State.Generation); Assert.Equal(fixture.Source.AchievementSnapshot.Sequence, own.State.Sequence);
        Assert.Equal(fixture.Source.AchievementSnapshot.SourceInstanceId, own.SourceInstanceId);
        Assert.Equal(saved.ObservedAt, own.State.ObservedAt); Assert.Equal(11, own.Raw!.Achievements[family].Count);
        var progress = own.State.Progress.Find(family)!.Find(stage)!;
        Assert.Equal(11, progress.Current); Assert.Equal(20, progress.Target);
        Assert.Empty(own.State.Activities); Assert.All(own.State.Milestones, item => Assert.False(item.NotificationEligible));
        var recovered = batches[^1];
        Assert.Equal(NetworkAchievementObservationKind.Baseline, recovered.Kind);
        Assert.Empty(recovered.Activities); Assert.Empty(recovered.Milestones);
        Assert.False(fixture.Source.AchievementSnapshot.Domain.IsFresh); Assert.False(fixture.Service.Snapshot.IsLive);
        Assert.Equal(writesBefore, Volatile.Read(ref writes));
        Assert.Equal(saved.SourceSequence, fixture.Store.Load(fixture.Binding).Snapshot!.SourceSequence);
    }

    [Fact]
    public async Task LivePartialRowsKeepTheirOwnFactsAndExposeMissingTitleProgressOnlyThroughSeparateHistory()
    {
        const int family = 433101200;
        using var fixture = new Fixture();
        fixture.Source.Emit(Rows((family, 11), (Riding, 4)), NetworkAchievementObservationKind.Baseline);
        await fixture.Service.FlushAsync();
        fixture.Source.Invalidate(); fixture.Source.Unready(2);
        fixture.Source.Emit(Raw(3), NetworkAchievementObservationKind.RecoveryBaseline,
            status: NetworkCombatSourceStatus.Partial);
        await fixture.Service.FlushAsync();

        var own = fixture.Service.CurrentOwnSnapshot;
        Assert.True(own.IsLive); Assert.False(own.IsHistorical);
        Assert.Single(own.Raw!.Achievements); Assert.False(own.Raw.Achievements.ContainsKey(family));
        Assert.Null(own.State.Progress.Find(family)); Assert.False(own.State.ObservedValues.ContainsKey(family));
        var history = Assert.IsType<AchievementOwnSnapshot>(own.History);
        Assert.True(history.IsHistorical); Assert.False(history.IsLive); Assert.True(history.HasProgressEvidence);
        Assert.Equal(own.Binding, history.Binding); Assert.Equal(own.State.MetadataBuild, history.State.MetadataBuild);
        Assert.Equal(11, history.Raw!.Achievements[family].Count); Assert.Equal(3, history.Raw.Achievements[Riding].Count);
        Assert.Null(history.History); Assert.Empty(history.State.Activities);
        Assert.All(history.State.Milestones, item => Assert.False(item.NotificationEligible));
        Assert.Same(history, own.ForProgressFamily(family)); Assert.Same(own, own.ForProgressFamily(Riding));

        fixture.Source.Emit(Rows((family, 12)), NetworkAchievementObservationKind.Update,
            status: NetworkCombatSourceStatus.Partial);
        await fixture.Service.FlushAsync();
        var updated = fixture.Service.CurrentOwnSnapshot;
        Assert.Same(updated, updated.ForProgressFamily(family));
        Assert.Equal(12, updated.ForProgressFamily(family).Raw!.Achievements[family].Count);
        Assert.Equal(3, updated.ForProgressFamily(Riding).Raw!.Achievements[Riding].Count);

        fixture.Source.Emit(Raw(3), NetworkAchievementObservationKind.Baseline);
        await fixture.Service.FlushAsync();
        var complete = fixture.Service.CurrentOwnSnapshot;
        Assert.True(complete.IsLive); Assert.Null(complete.History);
        Assert.Same(complete, complete.ForProgressFamily(family));
    }

    [Theory]
    [InlineData("unknown-source")]
    [InlineData("missing-live-raw")]
    [InlineData("different-binding")]
    [InlineData("different-build")]
    [InlineData("not-historical")]
    [InlineData("missing-family")]
    [InlineData("live-family")]
    public void HistoryNeverReplacesAnExistingLiveFamilyOrBorrowsUnqualifiedEvidence(string variant)
    {
        const int family = 433101200;
        var binding = new AchievementProfileBinding(new string('a', 32), new string('b', 32));
        var history = new AchievementOwnSnapshot(binding, new() { Binding = binding, Source = AchievementViewSource.Saved,
            MetadataBuild = 26425393 }, Rows((family, 11)), 1);
        var own = new AchievementOwnSnapshot(binding, new() { Binding = binding, Source = AchievementViewSource.Live,
            MetadataBuild = 26425393 }, Rows(), 2);
        switch (variant)
        {
            case "unknown-source": own = own with { State = own.State with { Source = AchievementViewSource.Unknown } }; break;
            case "missing-live-raw": own = own with { Raw = null }; break;
            case "different-binding": history = history with { Binding = new(new string('c', 32), new string('d', 32)) }; break;
            case "different-build": history = history with { State = history.State with { MetadataBuild = 26425392 } }; break;
            case "not-historical": history = history with { State = history.State with { Source = AchievementViewSource.Live } }; break;
            case "missing-family": history = history with { Raw = Rows() }; break;
            case "live-family": own = own with { Raw = Rows((family, -1)) }; break;
        }
        own = own with { History = history };
        Assert.Same(own, own.ForProgressFamily(family));
    }

    [Theory]
    [InlineData("missing-character")]
    [InlineData("zero-generation")]
    [InlineData("mismatched-generation")]
    public async Task AnUnconfirmedOwnBaselineCannotExposeThePreviousOwnersHistory(string variant)
    {
        using var fixture = new Fixture();
        fixture.Source.Emit(Rows((433101200, 11)), NetworkAchievementObservationKind.Baseline);
        await fixture.Service.FlushAsync();
        fixture.Source.Invalidate();
        fixture.Source.Unready(variant == "zero-generation" ? 0 : 2,
            confirmOwner: variant != "missing-character", observationGeneration: variant == "mismatched-generation" ? 3 : null);
        await fixture.Service.FlushAsync();
        var own = fixture.Service.CurrentOwnSnapshot;
        Assert.Null(own.Binding); Assert.Null(own.Raw); Assert.Null(own.History);
        Assert.False(own.IsLive); Assert.False(own.IsHistorical); Assert.False(own.HasProgressEvidence);
    }

    [Fact]
    public async Task SavedHudFactsFollowOnlyTheConfirmedOwnerDespiteOfflineSelectionUnresolvedOwnersAndDeletion()
    {
        using var fixture = new ProfileFixture(); fixture.BindOwner();
        fixture.Source.Emit(Rows((433101200, 11)), NetworkAchievementObservationKind.Baseline);
        await fixture.Service.FlushAsync();
        var first = fixture.Service.CurrentOwnSnapshot.Binding!;
        fixture.Profiles.Add("Synthetic offline selection");
        fixture.Source.Unready(2);
        await fixture.Service.FlushAsync();
        Assert.NotEqual(first.ProfileId, fixture.Profiles.ActiveId);
        Assert.Equal(first, fixture.Service.CurrentOwnSnapshot.Binding);
        Assert.True(fixture.Service.CurrentOwnSnapshot.IsHistorical);
        Assert.Equal(11, fixture.Service.CurrentOwnSnapshot.Raw!.Achievements[433101200].Count);

        fixture.Source.Invalidate();
        fixture.Source.Owner = new(new(202, 7, 43, "Synthetic second owner"), 3);
        fixture.Source.Unready(3);
        await fixture.Service.FlushAsync();
        Assert.Null(fixture.Service.CurrentOwnSnapshot.Binding);
        fixture.BindOwner();
        await fixture.Service.FlushAsync();
        var second = fixture.Service.CurrentOwnSnapshot;
        Assert.NotNull(second.Binding); Assert.NotEqual(first, second.Binding);
        Assert.False(second.HasProgressEvidence); Assert.False(second.IsHistorical); Assert.Null(second.Raw);

        fixture.Source.Owner = new(new(101, 7, 42, "Synthetic owner"), 4);
        fixture.Source.Unready(4);
        await fixture.Service.FlushAsync();
        Assert.True(fixture.Service.CurrentOwnSnapshot.IsHistorical);
        Assert.Equal(first, fixture.Service.CurrentOwnSnapshot.Binding);
        Assert.True(fixture.Profiles.Remove(first.ProfileId));
        fixture.Source.Unready(5);
        await fixture.Service.FlushAsync();
        Assert.Null(fixture.Service.CurrentOwnSnapshot.Binding);
        Assert.Null(fixture.Store.Load(first).Snapshot);
    }

    [Fact]
    public async Task AStoredMetadataMismatchCannotSupplySavedHudProgress()
    {
        using var fixture = new Fixture(); fixture.Service.Dispose();
        fixture.Store.Save(new()
        {
            Binding = fixture.Binding, Raw = Rows((433101200, 11)), ObservedAt = At,
            MetadataBuild = CombatAchievementDisplayCatalog.Default.MetadataBuild - 1,
            RuleVersion = AchievementRuleEvaluator.Version, Generation = 1, SourceSequence = 1,
        }, []);
        var source = new FakeSource();
        using var restarted = new AchievementStateService(source, fixture.Store, _ => fixture.Binding, () => fixture.Binding);
        source.Unready(2);
        await restarted.FlushAsync();
        var own = restarted.CurrentOwnSnapshot;
        Assert.Equal(fixture.Binding, own.Binding); Assert.Null(own.Raw); Assert.Null(own.History);
        Assert.False(own.HasProgressEvidence); Assert.False(own.IsHistorical); Assert.False(own.IsLive);
    }

    [Fact]
    public async Task AQueuedOwnBaselineUsesTheLatestPendingCounterInsteadOfTheOlderSavedCheckpoint()
    {
        using var blocked = new BlockedSave();
        using var fixture = new Fixture(blocked.Save);
        fixture.Source.Emit(Rows((433101200, 10)), NetworkAchievementObservationKind.Baseline);
        try
        {
            Assert.True(blocked.Entered.Wait(TimeSpan.FromSeconds(10)));
            fixture.Source.Emit(Rows((433101200, 11)), NetworkAchievementObservationKind.Update);
            fixture.Source.Invalidate(); fixture.Source.Unready(2);
        }
        finally { blocked.Release.Set(); }
        await fixture.Service.FlushAsync();
        var own = fixture.Service.CurrentOwnSnapshot;
        Assert.True(own.IsHistorical); Assert.False(own.IsLive);
        Assert.Equal(11, own.Raw!.Achievements[433101200].Count);
        Assert.Equal(11, fixture.Store.Load(fixture.Binding).Snapshot!.Raw.Achievements[433101200].Count);
        Assert.Empty(own.State.Activities);
    }

    [Fact]
    public async Task SavedHudMilestonesCannotReannounceTheEarlierLiveCompletion()
    {
        using var fixture = new Fixture();
        fixture.Source.Emit(Raw(4), NetworkAchievementObservationKind.Baseline);
        fixture.Source.Emit(Raw(5), NetworkAchievementObservationKind.Update);
        await fixture.Service.FlushAsync();
        Assert.True(Assert.Single(fixture.Service.CurrentOwnSnapshot.State.Milestones).NotificationEligible);
        var batches = new List<AchievementChangeBatch>(); fixture.Service.AchievementObserved += batches.Add;
        fixture.Source.Invalidate(); fixture.Source.Unready(2);
        await fixture.Service.FlushAsync();
        var own = fixture.Service.CurrentOwnSnapshot;
        Assert.True(own.IsHistorical); Assert.False(Assert.Single(own.State.Milestones).NotificationEligible);
        Assert.Empty(own.State.Activities);
        Assert.All(batches, batch => { Assert.Empty(batch.Activities); Assert.Empty(batch.Milestones); });
    }

    [Fact]
    public async Task PartialRecoveryKeepsLastKnownRowsOnlyInSavedHistory()
    {
        using var fixture = new Fixture();
        var baseline = Rows((Riding, 4), (999999, -7)) with
        { RewardGroups = ImmutableDictionary<int, ImmutableArray<bool>>.Empty.Add(10, [true, false]) };
        fixture.Source.Emit(baseline, NetworkAchievementObservationKind.Baseline);
        await fixture.Service.FlushAsync();
        fixture.Source.Invalidate();
        fixture.Source.Emit(Raw(5), NetworkAchievementObservationKind.RecoveryBaseline, generation: 2,
            status: NetworkCombatSourceStatus.Partial);
        await fixture.Service.FlushAsync();

        var live = fixture.Service.CurrentOwnSnapshot;
        Assert.True(live.IsLive);
        Assert.Single(live.Raw!.Achievements);
        Assert.Empty(live.Raw.RewardGroups);
        Assert.False(live.State.ObservedValues.ContainsKey(999999));
        Assert.Empty(live.State.Activities);
        Assert.DoesNotContain(live.State.Milestones, item => item.NotificationEligible);
        var saved = fixture.Store.Load(fixture.Binding).Snapshot!;
        Assert.Equal("accumulated-observations", saved.Coverage);
        Assert.Equal(5, saved.Raw.Achievements[Riding].Count);
        Assert.Equal(-7, saved.Raw.Achievements[999999].Count);
        Assert.Equal(new[] { true, false }, saved.Raw.RewardGroups[10]);

        fixture.Source.Invalidate();
        await fixture.Service.FlushAsync();
        Assert.True(fixture.Service.View.IsSaved);
        Assert.Equal(-7, fixture.Service.View.ObservedValues[999999]);
        Assert.False(fixture.Service.CurrentOwnSnapshot.IsLive);
    }

    [Fact]
    public async Task ConsecutivePartialCheckpointsPreserveRowsBeforeAndAfterRestart()
    {
        using var fixture = new Fixture();
        fixture.Source.Emit(Rows((999999, 2)), NetworkAchievementObservationKind.RecoveryBaseline,
            status: NetworkCombatSourceStatus.Partial);
        fixture.Source.Emit(Rows((999998, 3)), NetworkAchievementObservationKind.RecoveryBaseline, generation: 2,
            status: NetworkCombatSourceStatus.Partial);
        fixture.Source.Emit(Rows((999997, 4)), NetworkAchievementObservationKind.RecoveryBaseline, generation: 3,
            status: NetworkCombatSourceStatus.Partial);
        await fixture.Service.FlushAsync();
        Assert.Single(fixture.Service.View.ObservedValues);
        Assert.Equal(3, fixture.Store.Load(fixture.Binding).Snapshot!.Raw.Achievements.Count);
        fixture.Service.Dispose();
        using var restarted = new AchievementStateService(new FakeSource(), fixture.Store, _ => fixture.Binding, () => fixture.Binding);
        await restarted.FlushAsync();
        Assert.True(restarted.View.IsSaved);
        Assert.Equal(3, restarted.View.ObservedValues.Count);
        Assert.Empty(restarted.View.Activities);
        Assert.False(restarted.CurrentOwnSnapshot.IsLive);
    }

    [Fact]
    public async Task RealChangesAfterPartialRecoveryStillProduceActivities()
    {
        using var fixture = new Fixture();
        fixture.Source.Emit(Raw(3), NetworkAchievementObservationKind.RecoveryBaseline,
            status: NetworkCombatSourceStatus.Partial);
        fixture.Source.Emit(Raw(4), NetworkAchievementObservationKind.Update,
            status: NetworkCombatSourceStatus.Partial);
        await fixture.Service.FlushAsync();
        var activity = Assert.Single(fixture.Service.View.Activities).Value;
        Assert.Equal(3, activity.PreviousValue);
        Assert.Equal(4, activity.CurrentValue);
        Assert.Empty(fixture.Service.View.Milestones);
    }

    [Fact]
    public async Task CurrentProgressCrossingConfirmsOneLiveCompletionAndSurvivesADecrease()
    {
        using var fixture = new Fixture();
        const int familyId = 401100800, stageId = 401100804;
        CharacterAchievementState Current(int count, byte claimed = 3) => new(
            ImmutableDictionary<int, CharacterAchievement>.Empty.Add(familyId, new(count, 0, claimed)),
            ImmutableDictionary<int, ImmutableArray<bool>>.Empty);
        fixture.Source.Emit(Current(79), NetworkAchievementObservationKind.Baseline);
        await fixture.Service.FlushAsync();
        var before = fixture.Service.View.Progress.Find(familyId)!.Find(stageId)!;
        Assert.Null(before.Completed);
        Assert.False(before.RequirementsMetNow);
        fixture.Source.Emit(Current(80), NetworkAchievementObservationKind.Update);
        await fixture.Service.FlushAsync();
        var completion = Assert.Single(fixture.Service.View.Milestones,
            item => item.StageId == stageId && item.Kind == AchievementMilestoneKind.Completed);
        Assert.True(completion.NotificationEligible);
        Assert.Equal("Live", completion.Origin);
        Assert.Equal(401100805, fixture.Service.View.Progress.Find(familyId)!.CurrentStageId);

        fixture.Source.Emit(Current(77), NetworkAchievementObservationKind.Update);
        fixture.Source.Emit(Current(80), NetworkAchievementObservationKind.Update);
        fixture.Source.Emit(Current(77, 4), NetworkAchievementObservationKind.Reward);
        await fixture.Service.FlushAsync();
        var family = fixture.Service.View.Progress.Find(familyId)!;
        Assert.NotEqual(CombatKnowledgeStatus.Conflict, family.Status);
        Assert.True(family.Find(stageId)!.Completed);
        Assert.True(family.Find(stageId)!.Claimed);
        Assert.False(family.Find(stageId)!.RequirementsMetNow);
        Assert.Equal(completion, Assert.Single(fixture.Service.View.Milestones,
            item => item.StageId == stageId && item.Kind == AchievementMilestoneKind.Completed));
    }

    [Fact]
    public async Task CurrentProgressRecoveryImportsAnEarnedGoalWithoutACatchUpCelebration()
    {
        using var fixture = new Fixture();
        const int familyId = 401101100, stageId = 401101105;
        CharacterAchievementState Current(int count) => new(
            ImmutableDictionary<int, CharacterAchievement>.Empty.Add(familyId, new(count, 0, 4)),
            ImmutableDictionary<int, ImmutableArray<bool>>.Empty);
        fixture.Source.Emit(Current(59), NetworkAchievementObservationKind.Baseline);
        await fixture.Service.FlushAsync();
        fixture.Source.Invalidate();
        fixture.Source.Emit(Current(60), NetworkAchievementObservationKind.RecoveryBaseline,
            generation: 2, status: NetworkCombatSourceStatus.Partial);
        await fixture.Service.FlushAsync();
        var completion = Assert.Single(fixture.Service.View.Milestones,
            item => item.StageId == stageId && item.Kind == AchievementMilestoneKind.Completed);
        Assert.False(completion.NotificationEligible);
        Assert.Equal("Recovery", completion.Origin);
        Assert.Empty(fixture.Service.View.Activities);
    }

    [Fact]
    public async Task EveryReceivedCurrentCounterIsProjectedWithoutWaitingForGameplayChangesOrCounterRules()
    {
        using var fixture = new Fixture();
        fixture.Source.Emit(Rows((444100500, 0), (Riding, -1), (999999, int.MinValue)), NetworkAchievementObservationKind.Baseline);
        await fixture.Service.FlushAsync();
        var state = fixture.Service.View;
        Assert.True(state.IsLive);
        Assert.Equal(3, state.ObservedValues.Count);
        Assert.Equal(0, state.ObservedValues[444100500]);
        Assert.Equal(-1, state.ObservedValues[Riding]);
        Assert.Equal(int.MinValue, state.ObservedValues[999999]);
        Assert.False(state.ObservedValues.ContainsKey(420101000));
        Assert.Empty(state.Activities);
        Assert.Empty(state.Milestones);
        Assert.All(state.Progress.Find(444100500)!.Stages.Values, stage => Assert.Equal(0, stage.Current));
        Assert.Null(state.Progress.Find(Riding)!.Stages.Values.First().Current);
        Assert.False(state.Progress.Families.ContainsKey(999999));
        Assert.Equal(state.ObservedValues, fixture.Service.CurrentOwnSnapshot.State.ObservedValues);
    }

    [Fact]
    public async Task ObservedCountersAreImmutableAndReloadAsAnExplicitlySavedStand()
    {
        using var fixture = new Fixture();
        fixture.Source.Emit(Rows((444100500, 3), (999999, -7)), NetworkAchievementObservationKind.Baseline);
        await fixture.Service.FlushAsync();
        var first = fixture.Service.View.ObservedValues;
        fixture.Source.Emit(Rows((444100500, 4), (999999, -6)), NetworkAchievementObservationKind.Update);
        await fixture.Service.FlushAsync();
        Assert.Equal(3, first[444100500]); Assert.Equal(-7, first[999999]);
        Assert.Equal(4, fixture.Service.View.ObservedValues[444100500]);
        fixture.Source.Invalidate();
        await fixture.Service.FlushAsync();
        Assert.True(fixture.Service.View.IsSaved); Assert.False(fixture.Service.View.IsLive);
        Assert.Equal(-6, fixture.Service.View.ObservedValues[999999]);
        fixture.Service.Dispose();
        using var restarted = new AchievementStateService(new FakeSource(), fixture.Store, _ => fixture.Binding, () => fixture.Binding);
        await restarted.FlushAsync();
        Assert.True(restarted.View.IsSaved); Assert.False(restarted.View.IsLive);
        Assert.Equal(4, restarted.View.ObservedValues[444100500]); Assert.Equal(-6, restarted.View.ObservedValues[999999]);
        Assert.Empty(restarted.View.Activities);
        Assert.False(restarted.CurrentOwnSnapshot.IsLive);
    }

    [Fact]
    public async Task PermanentHistoricalClaimsKeepTheNextNativeStageWhenTheRawPrefixRegresses()
    {
        using var fixture = new Fixture();
        const int familyId = 444100500;
        var stages = Soulcrest.App.Components.CombatAchievementProgressContractCatalog.Default.FindFamily(familyId)!
            .Families.Single(family => family.Id == familyId).Stages;
        CharacterAchievementState RawPrefix(int count, byte prefix) => new(
            ImmutableDictionary<int, CharacterAchievement>.Empty.Add(familyId, new(count, 0, prefix)),
            ImmutableDictionary<int, ImmutableArray<bool>>.Empty);
        fixture.Source.Emit(RawPrefix(20, 2), NetworkAchievementObservationKind.Baseline);
        await fixture.Service.FlushAsync();
        Assert.Equal(stages[2].Id, fixture.Service.View.Progress.Find(familyId)!.CurrentStageId);
        var milestoneCount = fixture.Service.View.Milestones.Length;
        fixture.Source.Emit(RawPrefix(0, 0), NetworkAchievementObservationKind.Update);
        await fixture.Service.FlushAsync();
        var family = fixture.Service.View.Progress.Find(familyId)!;
        Assert.Equal(stages[2].Id, family.CurrentStageId);
        Assert.True(family.Find(stages[0].Id)!.Claimed); Assert.True(family.Find(stages[1].Id)!.Claimed);
        Assert.True(family.Find(stages[0].Id)!.Completed); Assert.True(family.Find(stages[1].Id)!.Completed);
        Assert.False(family.Find(stages[2].Id)!.Claimed); Assert.Null(family.Find(stages[2].Id)!.Completed);
        Assert.Equal(0, fixture.Service.View.ObservedValues[familyId]);
        Assert.Equal(0, fixture.Source.AchievementSnapshot.Domain.Value!.Achievements[familyId].RewardedStep);
        Assert.Equal(0, fixture.Store.Load(fixture.Binding).Snapshot!.Raw.Achievements[familyId].RewardedStep);
        Assert.Equal(milestoneCount, fixture.Service.View.Milestones.Length);
        fixture.Service.Dispose();
        using var restarted = new AchievementStateService(new FakeSource(), fixture.Store, _ => fixture.Binding, () => fixture.Binding);
        await restarted.FlushAsync();
        Assert.True(restarted.View.IsSaved);
        Assert.Equal(stages[2].Id, restarted.View.Progress.Find(familyId)!.CurrentStageId);
        Assert.True(restarted.View.Progress.Find(familyId)!.Find(stages[1].Id)!.Claimed);
    }

    [Fact]
    public async Task AConfirmedClaimIsPreservedIndependentlyOfAConflictingCounter()
    {
        using var fixture = new Fixture();
        fixture.Source.Emit(Raw(3, 1), NetworkAchievementObservationKind.Baseline);
        await fixture.Service.FlushAsync();
        Assert.Equal(2, fixture.Service.View.Milestones.Length);
        Assert.Contains(fixture.Service.View.Milestones, item => item.Kind == AchievementMilestoneKind.RewardClaimed);
        Assert.Equal(Soulcrest.Core.Combat.CombatKnowledgeStatus.Conflict, fixture.Service.View.Progress.Find(Riding)!.Status);
        Assert.True(fixture.Service.View.Progress.Find(Riding)!.Find(Riding + 1)!.Claimed);
    }

    [Fact]
    public async Task AFinalGapInvalidationPersistsWithoutAnotherFreshBaseline()
    {
        using var fixture = new Fixture();
        fixture.Source.Emit(Raw(4), NetworkAchievementObservationKind.Baseline);
        await fixture.Service.FlushAsync();
        fixture.Source.Invalidate(historyGap: true);
        await fixture.Service.FlushAsync();
        Assert.True(fixture.Store.Load(fixture.Binding).Snapshot!.HistoryGap);
        Assert.True(fixture.Service.View.IsSaved);
        Assert.True(fixture.Service.View.HistoryGap);
    }

    [Fact]
    public async Task LoadingPreviouslyLiveMilestonesNeverMakesThemNotificationEligibleAgain()
    {
        using var fixture = new Fixture();
        fixture.Source.Emit(Raw(4), NetworkAchievementObservationKind.Baseline);
        fixture.Source.Emit(Raw(5), NetworkAchievementObservationKind.Update);
        await fixture.Service.FlushAsync();
        Assert.True(Assert.Single(fixture.Service.View.Milestones).NotificationEligible);
        fixture.Service.Dispose();
        using var restarted = new AchievementStateService(new FakeSource(), fixture.Store, _ => fixture.Binding, () => fixture.Binding);
        await restarted.FlushAsync();
        Assert.False(Assert.Single(restarted.View.Milestones).NotificationEligible);
    }

    [Fact]
    public async Task EarnedCompletionSurvivesADecreasingCounterAndRestart()
    {
        using var fixture = new Fixture();
        fixture.Source.Emit(Raw(5), NetworkAchievementObservationKind.Baseline);
        await fixture.Service.FlushAsync();
        fixture.Source.Emit(Raw(4), NetworkAchievementObservationKind.Update);
        await fixture.Service.FlushAsync();
        Assert.True(fixture.Service.View.Progress.Find(Riding)!.Find(Riding + 1)!.Completed);
        Assert.Equal(Soulcrest.Core.Combat.CombatKnowledgeStatus.Conflict, fixture.Service.View.Progress.Find(Riding)!.Status);
        Assert.Single(fixture.Service.View.Milestones);
        fixture.Service.Dispose();
        using var restarted = new AchievementStateService(new FakeSource(), fixture.Store, _ => fixture.Binding, () => fixture.Binding);
        await restarted.FlushAsync();
        Assert.True(restarted.View.IsSaved);
        Assert.True(restarted.View.Progress.Find(Riding)!.Find(Riding + 1)!.Completed);
    }

    [Fact]
    public async Task BaselineImportConfirmsHistoricalFactsWithoutInventingALiveCompletionTime()
    {
        using var fixture = new Fixture();
        fixture.Source.Emit(Raw(5, 1), NetworkAchievementObservationKind.Baseline);
        await fixture.Service.FlushAsync();
        Assert.True(fixture.Service.View.IsLive);
        Assert.Equal(fixture.Binding.ProfileId, fixture.Service.View.ProfileId);
        Assert.Equal(2, fixture.Service.View.Milestones.Length);
        Assert.All(fixture.Service.View.Milestones, milestone =>
        {
            Assert.Equal("Baseline", milestone.Origin);
            Assert.False(milestone.NotificationEligible);
            Assert.Null(milestone.OccurredAt);
        });
        Assert.Equal(2, fixture.Store.Load(fixture.Binding).Milestones.Length);
    }

    [Fact]
    public async Task ImmediateCompletionAndClaimPreserveBothTransitionsBeforeDisplayCoalescing()
    {
        using var fixture = new Fixture();
        fixture.Source.Emit(Raw(4), NetworkAchievementObservationKind.Baseline);
        fixture.Source.Emit(Raw(5), NetworkAchievementObservationKind.Update);
        fixture.Source.Emit(Raw(5, 1), NetworkAchievementObservationKind.Reward);
        await fixture.Service.FlushAsync();
        var events = fixture.Store.Load(fixture.Binding).Milestones;
        var completed = Assert.Single(events.Where(item => item.Kind == AchievementMilestoneKind.Completed));
        var claimed = Assert.Single(events.Where(item => item.Kind == AchievementMilestoneKind.RewardClaimed));
        Assert.True(completed.FirstObservedAt < claimed.FirstObservedAt);
        Assert.Equal(0, completed.RawPatch.Achievements[Riding].RewardedStep);
        Assert.Equal(1, claimed.RawPatch.Achievements[Riding].RewardedStep);
        Assert.All(events, item => Assert.True(item.NotificationEligible));
        Assert.True(fixture.Service.View.Progress.Find(Riding)!.Find(Riding + 1)!.Claimed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUnknownPreviousConditionDoesNotProveALiveCompletion(bool negativePrevious)
    {
        using var fixture = new Fixture();
        var previous = negativePrevious ? Raw(-1) : new CharacterAchievementState(
            ImmutableDictionary<int, CharacterAchievement>.Empty, ImmutableDictionary<int, ImmutableArray<bool>>.Empty);
        fixture.Source.Emit(previous, NetworkAchievementObservationKind.Baseline);
        fixture.Source.Emit(Raw(5), NetworkAchievementObservationKind.Update);
        await fixture.Service.FlushAsync();
        var milestone = Assert.Single(fixture.Service.View.Milestones);
        Assert.False(milestone.NotificationEligible);
        Assert.NotEqual("Live", milestone.Origin);
        Assert.Null(milestone.OccurredAt);
    }

    [Fact]
    public async Task RelogAndDuplicateAcknowledgementDoNotDuplicateHistory()
    {
        using var fixture = new Fixture();
        fixture.Source.Emit(Raw(5, 1), NetworkAchievementObservationKind.Baseline);
        await fixture.Service.FlushAsync();
        var first = fixture.Service.View.Milestones.Select(item => item.Id).Order().ToArray();
        fixture.Source.Emit(Raw(5, 1), NetworkAchievementObservationKind.Reward);
        fixture.Source.Replay();
        fixture.Source.Emit(Raw(5, 1), NetworkAchievementObservationKind.Baseline, generation: 2);
        await fixture.Service.FlushAsync();
        Assert.Equal(first, fixture.Service.View.Milestones.Select(item => item.Id).Order());
        Assert.Equal(2, fixture.Store.Load(fixture.Binding).Milestones.Length);
    }

    [Fact]
    public async Task RestartLoadsAnExplicitlySavedViewWithoutCreatingALiveNetworkBaseline()
    {
        using var fixture = new Fixture();
        fixture.Source.Emit(Raw(5, 1), NetworkAchievementObservationKind.Baseline);
        await fixture.Service.FlushAsync();
        var observedAt = fixture.Service.View.ObservedAt;
        fixture.Service.Dispose();
        using var restarted = new AchievementStateService(new FakeSource(), fixture.Store,
            _ => fixture.Binding, () => fixture.Binding);
        await restarted.FlushAsync();
        Assert.True(restarted.View.IsSaved);
        Assert.False(restarted.Snapshot.IsLive);
        Assert.Null(restarted.CurrentOwnSnapshot.Binding);
        Assert.False(restarted.CurrentOwnSnapshot.IsHistorical);
        Assert.Null(restarted.CurrentOwnSnapshot.History);
        Assert.Equal(observedAt, restarted.View.ObservedAt);
        Assert.Equal(2, restarted.View.Milestones.Length);
        Assert.All(restarted.View.Milestones, milestone => Assert.False(milestone.NotificationEligible));
    }

    [Fact]
    public async Task MissingRowsStayUnknownAndNegativeUnknownRowsRemainInTheLastRawState()
    {
        using var fixture = new Fixture();
        var raw = new CharacterAchievementState(ImmutableDictionary<int, CharacterAchievement>.Empty
                .Add(-17, new(-9, long.MinValue, 255)).Add(Riding, new(-1, -2, 0)),
            ImmutableDictionary<int, ImmutableArray<bool>>.Empty.Add(91, [true, false, true]));
        fixture.Source.Emit(raw, NetworkAchievementObservationKind.Baseline);
        await fixture.Service.FlushAsync();
        var knownPrefix = Assert.Single(fixture.Service.View.Progress.Families).Value;
        Assert.Equal(Riding, knownPrefix.Id);
        Assert.All(knownPrefix.Stages.Values, stage => { Assert.Null(stage.Current); Assert.Null(stage.Target); Assert.Null(stage.Completed); });
        Assert.Empty(fixture.Service.View.Milestones);
        var saved = fixture.Store.Load(fixture.Binding).Snapshot!;
        Assert.Equal(-9, saved.Raw.Achievements[-17].Count);
        Assert.Equal(long.MinValue, saved.Raw.Achievements[-17].CompleteTime);
        Assert.Equal(new[] { true, false, true }, saved.Raw.RewardGroups[91].ToArray());
        Assert.False(saved.Raw.Achievements.ContainsKey(420101000));
    }

    [Fact]
    public async Task CounterOnlyBurstSavesTheLastValueWithoutMilestoneJournalEntries()
    {
        using var fixture = new Fixture();
        for (var value = 1; value <= 100; value++)
        {
            var raw = new CharacterAchievementState(ImmutableDictionary<int, CharacterAchievement>.Empty.Add(999999, new(value, 0, 0)),
                ImmutableDictionary<int, ImmutableArray<bool>>.Empty);
            fixture.Source.Emit(raw, value == 1 ? NetworkAchievementObservationKind.Baseline : NetworkAchievementObservationKind.Update);
        }
        await fixture.Service.FlushAsync();
        var read = fixture.Store.Load(fixture.Binding);
        Assert.Equal(100, read.Snapshot!.Raw.Achievements[999999].Count);
        Assert.Empty(read.Milestones);
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.jsonl", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task InvalidationAfterAQueuedClaimStillPersistsTheLastValidCheckpoint()
    {
        using var blocked = new BlockedSave();
        using var fixture = new Fixture(blocked.Save);
        fixture.Source.Emit(Raw(4), NetworkAchievementObservationKind.Baseline);
        try
        {
            Assert.True(blocked.Entered.Wait(TimeSpan.FromSeconds(10)));
            fixture.Source.Emit(Raw(5), NetworkAchievementObservationKind.Update);
            fixture.Source.Emit(Raw(5, 1), NetworkAchievementObservationKind.Reward);
            fixture.Source.Invalidate();
        }
        finally { blocked.Release.Set(); }
        await fixture.Service.FlushAsync();
        Assert.False(fixture.Service.View.IsLive);
        Assert.True(fixture.Service.View.IsSaved);
        var read = fixture.Store.Load(fixture.Binding);
        Assert.Equal(1, read.Snapshot!.Raw.Achievements[Riding].RewardedStep);
        Assert.Equal(2, read.Milestones.Length);
    }

    [Fact]
    public async Task AFailedMilestoneAppendIsRetriedOnTheNextCounterCheckpoint()
    {
        var attempts = 0;
        using var fixture = new Fixture(append: (path, line) =>
        {
            if (Interlocked.Increment(ref attempts) == 1) throw new IOException("Synthetic first append failure.");
            AppendDurably(path, line);
        });
        fixture.Source.Emit(Raw(5), NetworkAchievementObservationKind.Baseline);
        await fixture.Service.FlushAsync();
        Assert.Contains("history-write-failed", fixture.Service.View.Issues);
        fixture.Source.Emit(Raw(6), NetworkAchievementObservationKind.Update);
        await fixture.Service.FlushAsync();
        var read = fixture.Store.Load(fixture.Binding);
        Assert.Single(read.Milestones);
        Assert.Equal(6, read.Snapshot!.Raw.Achievements[Riding].Count);
        Assert.DoesNotContain("history-write-failed", fixture.Service.View.Issues);
    }

    [Fact]
    public async Task RecoveryBaselineNeverTurnsAnUnobservedTransitionIntoALiveNotification()
    {
        using var fixture = new Fixture();
        fixture.Source.Emit(Raw(4), NetworkAchievementObservationKind.Baseline);
        fixture.Source.Invalidate(historyGap: true);
        fixture.Source.Emit(Raw(5), NetworkAchievementObservationKind.RecoveryBaseline, historyGap: true);
        await fixture.Service.FlushAsync();
        var milestone = Assert.Single(fixture.Service.View.Milestones);
        Assert.Equal("Recovery", milestone.Origin);
        Assert.False(milestone.NotificationEligible);
        Assert.True(fixture.Service.View.HistoryGap);
    }

    [Fact]
    public async Task AStoredHistoryGapRemainsVisibleAfterAFreshLiveBaseline()
    {
        using var fixture = new Fixture();
        fixture.Source.Emit(Raw(5), NetworkAchievementObservationKind.Baseline);
        await fixture.Service.FlushAsync();
        fixture.Service.Dispose();
        var journal = Assert.Single(Directory.GetFiles(fixture.Root, "*.jsonl", SearchOption.AllDirectories));
        File.AppendAllText(journal, "broken-record\n");
        var source = new FakeSource();
        using var restarted = new AchievementStateService(source, fixture.Store, _ => fixture.Binding, () => fixture.Binding);
        await restarted.FlushAsync();
        source.Emit(Raw(6), NetworkAchievementObservationKind.Baseline);
        await restarted.FlushAsync();
        Assert.True(restarted.View.IsLive);
        Assert.True(restarted.View.HistoryGap);
    }

    [Fact]
    public async Task LateProfileBindingRevisitsOnlyTheCurrentOwnBaseline()
    {
        using var fixture = new ProfileFixture();
        fixture.Source.Emit(Raw(5), NetworkAchievementObservationKind.Baseline);
        await fixture.Service.FlushAsync();
        Assert.Equal(AchievementViewSource.Unknown, fixture.Service.View.Source);
        fixture.BindOwner();
        await fixture.Service.FlushAsync();
        Assert.True(fixture.Service.View.IsLive);
        Assert.Single(fixture.Service.View.Milestones);
        Assert.False(fixture.Service.View.Milestones[0].NotificationEligible);
    }

    [Fact]
    public async Task CharacterSwitchKeepsHistoriesAndSelectedViewsIsolated()
    {
        using var fixture = new ProfileFixture();
        fixture.BindOwner();
        fixture.Source.Emit(Raw(5), NetworkAchievementObservationKind.Baseline);
        await fixture.Service.FlushAsync();
        var first = fixture.Profiles.GetAchievementBinding(fixture.Source.Owner)!;
        fixture.Source.Owner = new(new(202, 7, 52, "Synthetic second"), 2);
        fixture.BindOwner();
        fixture.Source.Emit(Raw(3), NetworkAchievementObservationKind.Baseline, generation: 2);
        await fixture.Service.FlushAsync();
        var second = fixture.Profiles.GetAchievementBinding(fixture.Source.Owner)!;
        Assert.NotEqual(first.ProfileId, second.ProfileId);
        Assert.Equal(second.ProfileId, fixture.Service.View.ProfileId);
        Assert.Empty(fixture.Service.View.Milestones);
        Assert.Single(fixture.Store.Load(first).Milestones);
        Assert.Equal(3, fixture.Store.Load(second).Snapshot!.Raw.Achievements[Riding].Count);
        fixture.Profiles.Select(first.ProfileId);
        await fixture.Service.FlushAsync();
        Assert.True(fixture.Service.View.IsSaved);
        Assert.Equal(first.ProfileId, fixture.Service.View.ProfileId);
    }

    [Fact]
    public async Task DeletionQueuedDuringAWriteSurvivesObservationQueueOverflow()
    {
        using var blocked = new BlockedSave();
        using var fixture = new ProfileFixture(blocked.Save);
        fixture.BindOwner();
        var binding = fixture.Profiles.GetAchievementBinding(fixture.Source.Owner)!;
        fixture.Source.Emit(Raw(4), NetworkAchievementObservationKind.Baseline);
        try
        {
            Assert.True(blocked.Entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(fixture.Profiles.Remove(binding.ProfileId));
            for (var count = 0; count < 600; count++) fixture.Source.Emit(Raw(4), NetworkAchievementObservationKind.Update);
        }
        finally { blocked.Release.Set(); }
        await fixture.Service.FlushAsync();
        var read = fixture.Store.Load(binding);
        Assert.Null(read.Snapshot);
        Assert.Contains("profile-deleted", read.Issues);
        Assert.False(Directory.Exists(Path.Combine(fixture.StoreRoot, binding.ProfileId)));
        Assert.Equal(AchievementViewSource.Unknown, fixture.Service.View.Source);
    }

    [Fact]
    public async Task OverflowMarksEveryDiscardedCharacterHistoryWithoutGrantingLostLiveTransitions()
    {
        var root = Path.Combine(Path.GetTempPath(), "soulcrest-achievement-overflow-" + Guid.NewGuid().ToString("N"));
        using var blocked = new BlockedSave();
        try
        {
            var first = new AchievementProfileBinding(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
            var second = new AchievementProfileBinding(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
            var seed = new AchievementStateStore(root);
            seed.Save(new()
            {
                Binding = first, Raw = Raw(4), ObservedAt = At,
                MetadataBuild = Soulcrest.App.Components.CombatAchievementDisplayCatalog.Default.MetadataBuild,
                RuleVersion = AchievementRuleEvaluator.Version, Generation = 1, SourceSequence = 0,
            }, []);
            var store = new AchievementStateStore(root, AppendDurably, blocked.Save);
            var source = new FakeSource();
            AchievementProfileBinding selected = first;
            using var service = new AchievementStateService(source, store,
                owner => owner.Identity.CharacterId switch { 101 => first, 202 => second, _ => null }, () => selected);
            source.Emit(Raw(4), NetworkAchievementObservationKind.Baseline);
            try
            {
                Assert.True(blocked.Entered.Wait(TimeSpan.FromSeconds(10)));
                for (var count = 0; count < 20; count++) source.Emit(Raw(5), NetworkAchievementObservationKind.Update);
                source.Owner = new(new(202, 7, 52, "Synthetic second"), 2);
                selected = second;
                for (var count = 0; count < 600; count++)
                    source.Emit(Raw(5), count == 0 ? NetworkAchievementObservationKind.Baseline : NetworkAchievementObservationKind.Update);
            }
            finally { blocked.Release.Set(); }
            await service.FlushAsync();
            var savedFirst = store.Load(first);
            var savedSecond = store.Load(second);
            Assert.True(savedFirst.Snapshot!.HistoryGap);
            Assert.True(savedSecond.Snapshot!.HistoryGap);
            Assert.Equal(4, savedFirst.Snapshot.Raw.Achievements[Riding].Count);
            Assert.Empty(savedFirst.Milestones);
            Assert.Equal(5, savedSecond.Snapshot.Raw.Achievements[Riding].Count);
            var recovered = Assert.Single(savedSecond.Milestones);
            Assert.Equal("Recovery", recovered.Origin);
            Assert.False(recovered.NotificationEligible);
        }
        finally { Cleanup(root); }
    }

    private static CharacterAchievementState Raw(int count, byte rewarded = 0) => new(
        ImmutableDictionary<int, CharacterAchievement>.Empty.Add(Riding, new(count, 123456789, rewarded)),
        ImmutableDictionary<int, ImmutableArray<bool>>.Empty);

    private static void AppendDurably(string path, string value)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        stream.Write(Encoding.UTF8.GetBytes(value)); stream.Flush(flushToDisk: true);
    }

    private sealed class FakeSource : INetworkAchievementSource
    {
        private long _sequence;
        private readonly Guid _instance = Guid.NewGuid();
        public OwnCharacterContext Owner { get; set; } = new(new(101, 7, 42, "Synthetic owner"), 1);
        public NetworkAchievementObservation AchievementSnapshot { get; private set; } = new(0, 0, At, null,
            NetworkCombatDomain<CharacterAchievementState>.Unknown, NetworkAchievementObservationKind.Invalidated);
        public event Action<NetworkAchievementObservation>? AchievementObserved;
        public AchievementIngressDiagnostics AchievementDiagnostics { get; } = new();
        public void Emit(CharacterAchievementState raw, NetworkAchievementObservationKind kind, long? generation = null, bool historyGap = false,
            NetworkCombatSourceStatus status = NetworkCombatSourceStatus.Complete)
        {
            var sequence = Interlocked.Increment(ref _sequence);
            Owner = Owner with { Generation = generation ?? Owner.Generation };
            var at = At.AddSeconds(sequence);
            var source = kind switch
            {
                NetworkAchievementObservationKind.Reward => "own-achievement-reward",
                NetworkAchievementObservationKind.Update => "own-achievement-update",
                _ => "own-achievement-list",
            };
            AchievementSnapshot = new(sequence, Owner.Generation, at, Owner,
                new(raw, status, true, at, sequence, source), kind)
            { SourceInstanceId = _instance, HistoryGap = historyGap };
            AchievementObserved?.Invoke(AchievementSnapshot);
        }
        public void SetCurrent(NetworkAchievementObservation observation) => AchievementSnapshot = observation;
        public void Deliver(NetworkAchievementObservation observation) => AchievementObserved?.Invoke(observation);
        public void Replay() => AchievementObserved?.Invoke(AchievementSnapshot);
        public void Unready(long generation, bool retainRaw = true, bool confirmOwner = true, long? observationGeneration = null)
        {
            var sequence = Interlocked.Increment(ref _sequence);
            Owner = Owner with { Generation = generation };
            var at = At.AddSeconds(sequence);
            var domain = retainRaw ? AchievementSnapshot.Domain with { IsFresh = false, Status = NetworkCombatSourceStatus.Stale }
                : NetworkCombatDomain<CharacterAchievementState>.Unknown;
            AchievementSnapshot = new(sequence, observationGeneration ?? generation, at, confirmOwner ? Owner : null,
                domain, NetworkAchievementObservationKind.Baseline) { SourceInstanceId = _instance };
            AchievementObserved?.Invoke(AchievementSnapshot);
        }
        public void Invalidate(bool historyGap = false)
        {
            var sequence = Interlocked.Increment(ref _sequence);
            AchievementSnapshot = AchievementSnapshot with
            {
                Sequence = sequence, ObservedAt = At.AddSeconds(sequence), Character = Owner,
                Kind = NetworkAchievementObservationKind.Invalidated, SourceInstanceId = _instance, HistoryGap = historyGap,
                Domain = AchievementSnapshot.Domain with { IsFresh = false, Status = NetworkCombatSourceStatus.Stale },
            };
            AchievementObserved?.Invoke(AchievementSnapshot);
        }
    }

    private sealed class BlockedSave : IDisposable
    {
        private int _attempt;
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public void Save(string path, object value)
        {
            if (Interlocked.Increment(ref _attempt) == 1)
            {
                Entered.Set();
                if (!Release.Wait(TimeSpan.FromSeconds(15))) throw new IOException("Synthetic write barrier timed out.");
            }
            JsonFile.Save(path, value);
        }
        public void Dispose() { Release.Set(); Entered.Dispose(); Release.Dispose(); }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "soulcrest-achievement-service-" + Guid.NewGuid().ToString("N"));
        public AchievementProfileBinding Binding { get; } = new(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
        public FakeSource Source { get; } = new();
        public AchievementStateStore Store { get; }
        public AchievementStateService Service { get; }
        public Fixture(Action<string, object>? save = null, Action<string, string>? append = null)
        {
            Store = new(Root, append ?? AppendDurably, save ?? ((path, value) => JsonFile.Save(path, value)));
            Service = new(Source, Store, _ => Binding, () => Binding);
        }
        public void Dispose() { Service.Dispose(); Cleanup(Root); }
    }

    private sealed class ProfileFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "soulcrest-achievement-profiles-" + Guid.NewGuid().ToString("N"));
        public string StoreRoot => Path.Combine(Root, "history");
        public FakeSource Source { get; } = new();
        public ExplorationService Profiles { get; }
        public AchievementStateStore Store { get; }
        public AchievementStateService Service { get; }
        public ProfileFixture(Action<string, object>? save = null)
        {
            Profiles = new(Path.Combine(Root, "exploration.json"), []);
            Store = new(StoreRoot, AppendDurably, save ?? ((path, value) => JsonFile.Save(path, value)));
            Service = new(Source, Profiles, Store);
        }
        public void BindOwner() => Profiles.BindNetworkContext(new(Source.Owner.Identity, Source.Owner.Generation, 1, At));
        public void Dispose() { Service.Dispose(); Cleanup(Root); }
    }

    private static void Cleanup(string root)
    {
        var actual = Path.GetFullPath(root);
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), actual, StringComparison.OrdinalIgnoreCase);
        if (Directory.Exists(actual)) Directory.Delete(actual, recursive: true);
    }
}
