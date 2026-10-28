using Soulcrest.App.Network;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class AbyssKillFeedServiceTests
{
    [Fact]
    public void ExplicitResultAmountsAndKillerMetadataAreFrozenWithoutBorrowingFromAnotherEvent()
    {
        using var f = new Fixture(showDeaths: true);
        f.Source.Emit(NetworkAbyssCombatKind.Kill, 1, "Synthetic opponent", 200, apGain: 120);
        f.Source.Emit(NetworkAbyssCombatKind.Death, 2, "Synthetic killer", -130,
            classId: "Cleric", rankGrade: 13);
        var before = f.Service.Snapshot;
        Assert.Equal(120, before[1].AbyssPointGain); Assert.Equal(200, before[1].RankPointDelta);
        Assert.Null(before[0].AbyssPointGain); Assert.Equal(-130, before[0].RankPointDelta);
        Assert.Equal("Cleric", before[0].OpponentClassId); Assert.Equal(13, before[0].OpponentAbyssRankGrade);
        f.Source.Emit(NetworkAbyssCombatKind.Kill, 3);
        var unknown = f.Service.Snapshot[0];
        Assert.Null(unknown.AbyssPointGain); Assert.Null(unknown.RankPointDelta);
        Assert.Same(before[0], f.Service.Snapshot[1]); Assert.Same(before[1], f.Service.Snapshot[2]);
        f.Source.Emit(NetworkAbyssCombatKind.Kill, 3, rankDelta: 999, apGain: 999);
        Assert.Same(unknown, f.Service.Snapshot[0]); // a duplicate cannot fill unknown amounts later
    }

    [Theory]
    [InlineData(NetworkAbyssCombatKind.Kill, -1, -1, null, null)]
    [InlineData(NetworkAbyssCombatKind.Assist, -1, -1, null, null)]
    [InlineData(NetworkAbyssCombatKind.Death, 1, 1, null, null)]
    [InlineData(NetworkAbyssCombatKind.Kill, 0L, 0L, 0L, 0L)]
    [InlineData(NetworkAbyssCombatKind.Death, 0L, 0L, 0L, null)]
    public void WrongSignedAmountsAreUnknownAndExplicitZeroIsNotMissing(NetworkAbyssCombatKind kind,
        long rankDelta, long apGain, long? expectedRank, long? expectedAp)
    {
        using var f = new Fixture(showDeaths: true);
        f.Source.Emit(kind, 1, rankDelta: rankDelta, apGain: apGain);
        var entry = Assert.Single(f.Service.Snapshot);
        Assert.Equal(expectedRank, entry.RankPointDelta); Assert.Equal(expectedAp, entry.AbyssPointGain);
    }

    [Fact]
    public void QualifiedOwnKillAndDeathKeepOptionalLiveDetailsWithoutGuessingAMissingOpponent()
    {
        using var f = new Fixture(showDeaths: true);
        f.Source.Emit(NetworkAbyssCombatKind.Kill, 1, "Synthetic opponent", 120);
        f.Source.Emit(NetworkAbyssCombatKind.Death, 2, rankDelta: -45);
        Assert.Equal(2, f.Service.Snapshot.Length);
        var death = f.Service.Snapshot[0]; var kill = f.Service.Snapshot[1];
        Assert.Equal(AbyssKillFeedKind.Death, death.Kind); Assert.Null(death.OpponentName); Assert.Equal(-45, death.RankPointDelta);
        Assert.Equal(AbyssKillFeedKind.Kill, kill.Kind); Assert.Equal("Synthetic opponent", kill.OpponentName);
        Assert.Equal(120, kill.RankPointDelta); Assert.Equal(f.Source.OwnCharacter!.Generation, kill.Generation);
    }

    [Theory]
    [InlineData(NetworkAbyssCombatKind.Kill)]
    [InlineData(NetworkAbyssCombatKind.Death)]
    [InlineData(NetworkAbyssCombatKind.Assist)]
    public void EachSourceCapabilityMustBeQualifiedAndOldCallbacksCannotResurface(NetworkAbyssCombatKind kind)
    {
        using var f = new Fixture(showDeaths: true);
        f.Source.CombatCapabilities = NetworkAbyssCombatCapabilities.None;
        f.Source.Notify(); f.Source.Emit(kind, 1); Assert.Empty(f.Service.Snapshot);
        f.Source.CombatCapabilities = new(kind == NetworkAbyssCombatKind.Kill,
            kind == NetworkAbyssCombatKind.Death, kind == NetworkAbyssCombatKind.Assist);
        f.Source.Notify(); f.Source.Emit(kind, 1); Assert.Empty(f.Service.Snapshot);
        f.Source.Emit(kind, 2); Assert.Single(f.Service.Snapshot);
        f.Source.CombatCapabilities = NetworkAbyssCombatCapabilities.None;
        f.Source.Notify(); Assert.Empty(f.Service.Snapshot);
    }

    [Fact]
    public void FeedKeepsExactlyTenAndOnlyDropsTheOldestWhenTheEleventhArrives()
    {
        using var f = new Fixture();
        for (var sequence = 1; sequence <= 10; sequence++) f.Source.Emit(NetworkAbyssCombatKind.Kill, sequence);
        Assert.Equal(Enumerable.Range(1, 10).Reverse().Select(value => (long)value), f.Service.Snapshot.Select(row => row.Sequence));
        f.Source.Emit(NetworkAbyssCombatKind.Kill, 11);
        Assert.Equal(Enumerable.Range(2, 10).Reverse().Select(value => (long)value), f.Service.Snapshot.Select(row => row.Sequence));
        Assert.All(f.Service.Snapshot, row => { Assert.Null(row.OpponentName); Assert.Null(row.RankPointDelta); });
    }

    [Fact]
    public void SelectedKindsShareOneTenEntryBudgetAndUnselectedKindsDoNotEvictAnything()
    {
        using var f = new Fixture();
        var expected = new List<long>();
        for (var sequence = 1; sequence <= 15; sequence++)
        {
            var kind = (NetworkAbyssCombatKind)((sequence - 1) % 3);
            f.Source.Emit(kind, sequence);
            if (kind != NetworkAbyssCombatKind.Death) expected.Insert(0, sequence);
        }
        Assert.Equal(10, expected.Count); Assert.Equal(expected, f.Service.Snapshot.Select(row => row.Sequence));
        f.Source.Emit(NetworkAbyssCombatKind.Death, 16); Assert.Equal(expected, f.Service.Snapshot.Select(row => row.Sequence));
        f.Source.Emit(NetworkAbyssCombatKind.Kill, 17); expected.Insert(0, 17); expected.RemoveAt(10);
        Assert.Equal(expected, f.Service.Snapshot.Select(row => row.Sequence));
    }

    [Theory]
    [InlineData("Gladiator")] [InlineData("Templar")] [InlineData("Ranger")] [InlineData("Assassin")]
    [InlineData("Elementalist")] [InlineData("Sorcerer")] [InlineData("Cleric")] [InlineData("Chanter")]
    public void KnownClassMetadataAndActualGameRankAreRetainedUnchanged(string classId)
    {
        using var f = new Fixture();
        f.Source.Emit(NetworkAbyssCombatKind.Kill, 1, "Synthetic opponent", classId: classId, rankGrade: 19);
        var entry = Assert.Single(f.Service.Snapshot);
        Assert.Equal(classId, entry.OpponentClassId); Assert.Equal(19, entry.OpponentAbyssRankGrade);
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("cleric")] [InlineData(" Cleric")]
    [InlineData("Brawler")] [InlineData("UnknownClass")] [InlineData("Cleric\n")]
    public void UnknownClassCannotBeInferredOrDiscardAnIndependentlyKnownRank(string? classId)
    {
        using var f = new Fixture();
        f.Source.Emit(NetworkAbyssCombatKind.Kill, 1, "Cleric Grade19", classId: classId, rankGrade: 19);
        var entry = Assert.Single(f.Service.Snapshot);
        Assert.Null(entry.OpponentClassId); Assert.Equal(19, entry.OpponentAbyssRankGrade);
    }

    [Theory]
    [InlineData(1)] [InlineData(10)] [InlineData(19)]
    public void ExplicitRankGradeIsPreservedWithoutPointTierConversion(int grade)
    {
        using var f = new Fixture();
        f.Source.Emit(NetworkAbyssCombatKind.Kill, 1, classId: "Cleric", rankGrade: grade);
        Assert.Equal(grade, Assert.Single(f.Service.Snapshot).OpponentAbyssRankGrade);
    }

    [Theory]
    [InlineData(null)] [InlineData(int.MinValue)] [InlineData(-1)] [InlineData(0)] [InlineData(20)] [InlineData(int.MaxValue)]
    public void MissingOrUnrecognizedRankCannotBeInferredFromNamesOrRankPointAmounts(int? grade)
    {
        using var f = new Fixture();
        f.Source.Emit(NetworkAbyssCombatKind.Kill, 1, "Rank19 opponent", 20_000_000, classId: "Cleric", rankGrade: grade);
        var entry = Assert.Single(f.Service.Snapshot);
        Assert.Null(entry.OpponentAbyssRankGrade); Assert.Equal("Cleric", entry.OpponentClassId);
    }

    [Fact]
    public void EveryEntryFreezesItsOwnMetadataAndOldSnapshotsNeverFollowNewSourceFacts()
    {
        using var f = new Fixture();
        var first = new NetworkAbyssCombatEvent(f.Clock.GetUtcNow(), 1, f.Source.OwnCharacter!, NetworkAbyssCombatKind.Kill,
            "Synthetic opponent") { OpponentClassId = "Cleric", OpponentAbyssRankGrade = 19 };
        f.Source.Send(first); var before = f.Service.Snapshot;
        f.Source.Send(first with { Sequence = 2, OpponentClassId = "Templar", OpponentAbyssRankGrade = 17 });
        Assert.Equal("Cleric", Assert.Single(before).OpponentClassId); Assert.Equal(19, before[0].OpponentAbyssRankGrade);
        Assert.Same(before[0], f.Service.Snapshot[1]);
        Assert.Equal("Templar", f.Service.Snapshot[0].OpponentClassId); Assert.Equal(17, f.Service.Snapshot[0].OpponentAbyssRankGrade);
    }

    [Fact]
    public void AValidOlderEventInsideTheSameOwnEpochHasNoArbitraryAgeRejection()
    {
        using var f = new Fixture(); var at = f.Clock.GetUtcNow().AddSeconds(1);
        f.Clock.Advance(TimeSpan.FromDays(2));
        f.Source.Emit(NetworkAbyssCombatKind.Kill, 1, at: at);
        Assert.Equal(at, Assert.Single(f.Service.Snapshot).At);
    }

    [Fact]
    public void OldOwnEpochCannotDisplayOrConsumeTheNewEpochSequence()
    {
        using var f = new Fixture(); var oldOwn = f.Source.OwnCharacter!;
        f.Clock.Advance(TimeSpan.FromSeconds(1)); f.Source.Switch(2);
        f.Source.Send(new(f.Clock.GetUtcNow(), 100, oldOwn, NetworkAbyssCombatKind.Kill));
        Assert.Empty(f.Service.Snapshot);
        f.Source.Emit(NetworkAbyssCombatKind.Kill, 1); Assert.Equal(1, Assert.Single(f.Service.Snapshot).Sequence);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void MissingOrDefaultOwnObservationCannotAuthorizeAFeedEntry(bool defaultTime)
    {
        using var f = new Fixture();
        f.Source.SetOwn(f.Source.OwnCharacter! with { ObservedAt = defaultTime ? default(DateTimeOffset) : null });
        f.Source.Emit(NetworkAbyssCombatKind.Kill, 1); Assert.Empty(f.Service.Snapshot);
    }

    [Fact]
    public void DuplicateOutOfOrderCannotReplaceRetainedEntriesButANewGenerationCanRestartItsSequence()
    {
        using var f = new Fixture(showDeaths: true);
        f.Source.Emit(NetworkAbyssCombatKind.Kill, 3); f.Source.Emit(NetworkAbyssCombatKind.Kill, 3);
        f.Source.Emit(NetworkAbyssCombatKind.Kill, 2); Assert.Single(f.Service.Snapshot);
        f.Clock.Advance(TimeSpan.FromHours(1)); f.Source.Notify(); Assert.Single(f.Service.Snapshot);
        f.Source.Emit(NetworkAbyssCombatKind.Kill, 3); Assert.Single(f.Service.Snapshot);
        f.Source.Switch(2); f.Source.Emit(NetworkAbyssCombatKind.Death, 1);
        var entry = Assert.Single(f.Service.Snapshot); Assert.Equal(2, entry.Generation); Assert.Equal(1, entry.Sequence);
    }

    [Fact]
    public void FeedDisableClearsNamesAndKeepsWatermarksForEventsObservedWhileDisabled()
    {
        using var f = new Fixture(showDeaths: true); f.Source.Emit(NetworkAbyssCombatKind.Kill, 1, "Synthetic opponent");
        f.Settings.Current.AbyssKillFeedEnabled = false; f.Source.Notify(); Assert.Empty(f.Service.Snapshot);
        f.Source.Emit(NetworkAbyssCombatKind.Death, 2, "Synthetic disabled opponent"); Assert.Empty(f.Service.Snapshot);
        f.Settings.Current.AbyssKillFeedEnabled = true; f.Source.Notify();
        f.Source.Emit(NetworkAbyssCombatKind.Death, 2); Assert.Empty(f.Service.Snapshot);
        f.Source.Emit(NetworkAbyssCombatKind.Kill, 3); Assert.Single(f.Service.Snapshot);
    }

    [Fact]
    public void GlobalNetworkOptOutClearsEvenWhenTheSourceStillClaimsToRun()
    {
        using var f = new Fixture(); f.Source.Emit(NetworkAbyssCombatKind.Kill, 1);
        f.Settings.Current.LootTrackingEnabled = false; f.Source.Notify(); Assert.Empty(f.Service.Snapshot);
        f.Source.Emit(NetworkAbyssCombatKind.Kill, 2); Assert.Empty(f.Service.Snapshot);
    }

    [Fact]
    public void TransportStopAndCharacterSwitchClearButTemporaryOwnLossKeepsAcceptedHistory()
    {
        using var f = new Fixture(); var original = f.Source.OwnCharacter;
        f.Source.Emit(NetworkAbyssCombatKind.Kill, 1, "Synthetic opponent");
        f.Source.Running = false; f.Source.Notify(); Assert.Empty(f.Service.Snapshot);
        f.Source.Running = true; f.Source.Notify(); f.Source.Emit(NetworkAbyssCombatKind.Kill, 1); Assert.Empty(f.Service.Snapshot);
        f.Source.Emit(NetworkAbyssCombatKind.Kill, 2); Assert.Single(f.Service.Snapshot);
        var retained = Assert.Single(f.Service.Snapshot);
        f.Source.SetOwn(null); Assert.Same(retained, Assert.Single(f.Service.Snapshot));
        Assert.Null(f.Service.DisplaySnapshot.BindingGeneration);
        f.Source.Send(new(f.Clock.GetUtcNow(), 100, original!, NetworkAbyssCombatKind.Kill));
        f.Source.SetOwn(original); f.Source.Emit(NetworkAbyssCombatKind.Kill, 2);
        Assert.Same(retained, Assert.Single(f.Service.Snapshot));
        f.Source.Emit(NetworkAbyssCombatKind.Kill, 3); Assert.Equal(2, f.Service.Snapshot.Length);
        f.Source.Switch(2); Assert.Empty(f.Service.Snapshot);
        f.Source.Emit(NetworkAbyssCombatKind.Kill, 1); Assert.Single(f.Service.Snapshot);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameCharacterReentryKeepsTenFrozenRowsAndAcceptsOnlyTheNewOwnEpoch(bool temporaryOwnLoss)
    {
        using var f = new Fixture(showDeaths: true);
        for (var sequence = 1; sequence <= 10; sequence++)
            f.Source.Emit((NetworkAbyssCombatKind)((sequence - 1) % 3), sequence,
                "Synthetic opponent " + sequence, classId: "Cleric", rankGrade: 13);
        var before = f.Service.DisplaySnapshot;
        var oldOwn = f.Source.OwnCharacter!;
        if (temporaryOwnLoss)
        {
            f.Source.SetOwn(null); f.Source.Notify();
            Assert.Null(f.Service.DisplaySnapshot.BindingGeneration);
            Assert.Equal(before.Entries.ToArray(), f.Service.Snapshot.ToArray());
            f.Source.Send(new(f.Clock.GetUtcNow(), 100, oldOwn, NetworkAbyssCombatKind.Kill));
        }
        f.Clock.Advance(TimeSpan.FromSeconds(2));
        f.Source.Reenter(oldOwn.Identity with { EntityKey = 77, Name = "Synthetic renamed character" });
        var rebound = f.Service.DisplaySnapshot;
        Assert.Equal(2, rebound.BindingGeneration); Assert.Equal(before.Entries.ToArray(), rebound.Entries.ToArray());
        Assert.All(rebound.Entries, row => Assert.Equal(1, row.Generation));
        f.Source.Send(new(f.Clock.GetUtcNow(), 100, oldOwn, NetworkAbyssCombatKind.Kill));
        f.Source.Emit(NetworkAbyssCombatKind.Kill, 1, "Synthetic new opponent", classId: "Templar", rankGrade: 10);
        f.Source.Emit(NetworkAbyssCombatKind.Kill, 1); // new-epoch echo still cannot duplicate the receipt
        var after = f.Service.Snapshot;
        Assert.Equal(10, after.Length); Assert.Equal(2, after[0].Generation); Assert.Equal(1, after[0].Sequence);
        Assert.Equal("Templar", after[0].OpponentClassId); Assert.Equal(10, after[0].OpponentAbyssRankGrade);
        for (var index = 0; index < 9; index++) Assert.Same(before.Entries[index], after[index + 1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReentryOfAnotherCharacterOrServerCannotInheritHistoricalNames(bool changeServer)
    {
        using var f = new Fixture();
        f.Source.Emit(NetworkAbyssCombatKind.Kill, 1, "Synthetic previous opponent");
        var oldOwn = f.Source.OwnCharacter!;
        f.Source.SetOwn(null); Assert.Single(f.Service.Snapshot);
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        f.Source.Reenter(oldOwn.Identity with { CharacterId = changeServer ? 1 : 2, ServerId = changeServer ? 2 : 1 });
        Assert.Empty(f.Service.Snapshot);
        f.Source.Send(new(f.Clock.GetUtcNow(), 100, oldOwn, NetworkAbyssCombatKind.Kill));
        f.Source.Emit(NetworkAbyssCombatKind.Kill, 1, "Synthetic current opponent");
        Assert.Equal("Synthetic current opponent", Assert.Single(f.Service.Snapshot).OpponentName);
    }

    [Fact]
    public void QuietPeriodsAndWallClockChangesNeverExpireTheRetainedFeed()
    {
        using var f = new Fixture(); f.Source.Emit(NetworkAbyssCombatKind.Kill, 1);
        f.Clock.WallOffset = TimeSpan.FromDays(2);
        f.Clock.Advance(TimeSpan.FromDays(30)); f.Source.Notify(); Assert.Single(f.Service.Snapshot);
        f.Clock.WallOffset = TimeSpan.FromDays(-2);
        f.Clock.Advance(TimeSpan.FromDays(30)); f.Source.Notify(); Assert.Single(f.Service.Snapshot);
    }

    [Fact]
    public void PreOwnOrFutureTimestampedEventsCannotBecomeValidAfterAnEcho()
    {
        using var f = new Fixture(showDeaths: true);
        f.Source.Emit(NetworkAbyssCombatKind.Kill, 1, at: f.Clock.GetUtcNow().AddSeconds(-11));
        f.Source.Emit(NetworkAbyssCombatKind.Kill, 1); Assert.Empty(f.Service.Snapshot);
        f.Source.Emit(NetworkAbyssCombatKind.Death, 2, at: f.Clock.GetUtcNow().AddSeconds(1));
        f.Source.Emit(NetworkAbyssCombatKind.Death, 2); Assert.Empty(f.Service.Snapshot);
        f.Source.Emit(NetworkAbyssCombatKind.Kill, 3); Assert.Single(f.Service.Snapshot);
    }

    [Fact]
    public void ForeignOrInvalidEventsCannotDisplayOrPoisonTheOwnSequence()
    {
        using var f = new Fixture();
        var foreign = new OwnCharacterContext(new(2, 1, 2, "Synthetic other character"), 1);
        f.Source.Send(new(f.Clock.GetUtcNow(), 100, foreign, NetworkAbyssCombatKind.Kill, "Synthetic other opponent"));
        f.Source.Emit((NetworkAbyssCombatKind)99, 50); f.Source.Emit(NetworkAbyssCombatKind.Kill, 0);
        Assert.Empty(f.Service.Snapshot);
        f.Source.Emit(NetworkAbyssCombatKind.Kill, 1); Assert.Single(f.Service.Snapshot);
    }

    [Fact]
    public void DefaultsShowOwnKillsAndExplicitAssistsWhileKeepingDeathsHidden()
    {
        using var f = new Fixture();
        f.Source.Emit(NetworkAbyssCombatKind.Kill, 1, "Synthetic kill opponent");
        f.Source.Emit(NetworkAbyssCombatKind.Death, 2, "Synthetic death opponent");
        f.Source.Emit(NetworkAbyssCombatKind.Assist, 3, "Synthetic assist opponent", 20);
        Assert.Equal([AbyssKillFeedKind.Assist, AbyssKillFeedKind.Kill], f.Service.Snapshot.Select(row => row.Kind).ToArray());
        Assert.Equal("Synthetic assist opponent", f.Service.Snapshot[0].OpponentName);
        Assert.Equal(20, f.Service.Snapshot[0].RankPointDelta);
    }

    [Theory]
    [InlineData(NetworkAbyssCombatKind.Kill)]
    [InlineData(NetworkAbyssCombatKind.Death)]
    [InlineData(NetworkAbyssCombatKind.Assist)]
    public void EveryKindCanBeSelectedIndependently(NetworkAbyssCombatKind kind)
    {
        using var f = new Fixture(showKills: kind == NetworkAbyssCombatKind.Kill,
            showDeaths: kind == NetworkAbyssCombatKind.Death, showAssists: kind == NetworkAbyssCombatKind.Assist);
        f.Source.Emit(NetworkAbyssCombatKind.Kill, 1); f.Source.Emit(NetworkAbyssCombatKind.Death, 2);
        f.Source.Emit(NetworkAbyssCombatKind.Assist, 3);
        Assert.Equal((int)kind, (int)Assert.Single(f.Service.Snapshot).Kind);
    }

    [Theory]
    [InlineData(NetworkAbyssCombatKind.Kill)]
    [InlineData(NetworkAbyssCombatKind.Death)]
    [InlineData(NetworkAbyssCombatKind.Assist)]
    public void DeselectingOneKindPreservesOthersAndReselectingCannotRestoreFilteredEchoes(NetworkAbyssCombatKind kind)
    {
        using var f = new Fixture(showDeaths: true);
        f.Source.Emit(NetworkAbyssCombatKind.Kill, 1); f.Source.Emit(NetworkAbyssCombatKind.Death, 2);
        f.Source.Emit(NetworkAbyssCombatKind.Assist, 3);
        var preserved = f.Service.Snapshot.Where(row => (int)row.Kind != (int)kind).ToArray();
        f.Select(kind, false);
        Assert.Equal(preserved, f.Service.Snapshot.ToArray());
        f.Source.Emit(kind, 4); Assert.Equal(preserved, f.Service.Snapshot.ToArray());
        f.Select(kind, true);
        f.Source.Emit(kind, 4); Assert.Equal(preserved, f.Service.Snapshot.ToArray());
        f.Source.Emit(kind, 5);
        Assert.Equal((int)kind, (int)f.Service.Snapshot[0].Kind);
        Assert.Equal(preserved, f.Service.Snapshot.Skip(1).ToArray());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Synthetic\nOpponent")]
    public void MissingOrControlBearingNamesRemainUnassigned(string? name)
    {
        using var f = new Fixture(); f.Source.Emit(NetworkAbyssCombatKind.Kill, 1, name);
        Assert.Null(Assert.Single(f.Service.Snapshot).OpponentName);
    }

    [Fact]
    public void SubscriberFailuresDoNotInterruptTheFeedAndParallelCallbacksRemainBounded()
    {
        using var f = new Fixture(); var notifications = 0;
        f.Service.Changed += () => throw new InvalidOperationException("Synthetic subscriber failure");
        f.Service.Changed += () => { _ = f.Service.Snapshot; Interlocked.Increment(ref notifications); };
        Parallel.For(1, 101, sequence => f.Source.Emit(NetworkAbyssCombatKind.Kill, sequence));
        Assert.InRange(f.Service.Snapshot.Length, 1, AbyssKillFeedService.MaximumEntries);
        Assert.Equal(100, f.Service.Snapshot[0].Sequence);
        Assert.Equal(f.Service.Snapshot.Length, f.Service.Snapshot.Select(row => row.Sequence).Distinct().Count());
        Assert.True(notifications > 0);
    }

    [Fact]
    public async Task AnUnchangedNewerSourceRefreshCannotSuppressAnAlreadyDeliveredCurrentEvent()
    {
        using var f = new Fixture();
        var own = f.Source.OwnCharacter!;
        var observation = new NetworkAbyssCombatEvent(f.Clock.GetUtcNow(), 1, own, NetworkAbyssCombatKind.Kill);
        using var captured = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var eventThread = 0;
        f.Source.ReadOwnHook = () =>
        {
            if (Environment.CurrentManagedThreadId != Volatile.Read(ref eventThread)) return;
            captured.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Synthetic source-read wait expired");
        };
        var sending = Task.Run(() => { Volatile.Write(ref eventThread, Environment.CurrentManagedThreadId); f.Source.Send(observation); });
        try
        {
            Assert.True(captured.Wait(TimeSpan.FromSeconds(5)));
            f.Clock.Advance(TimeSpan.FromSeconds(1)); f.Source.Notify(); // Same context, newer refresh gets the gate first.
        }
        finally { release.Set(); }
        await sending;
        var entry = Assert.Single(f.Service.Snapshot);
        Assert.Equal(own.Generation, entry.Generation); Assert.Equal(1, entry.Sequence);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARealContextChangeStillRejectsAnEventCapturedBeforeTheBoundary(bool switchCharacter)
    {
        using var f = new Fixture();
        var own = f.Source.OwnCharacter!;
        var observation = new NetworkAbyssCombatEvent(f.Clock.GetUtcNow(), 2, own, NetworkAbyssCombatKind.Kill);
        using var captured = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var eventThread = 0;
        f.Source.ReadOwnHook = () =>
        {
            if (Environment.CurrentManagedThreadId != Volatile.Read(ref eventThread)) return;
            captured.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Synthetic source-read wait expired");
        };
        var sending = Task.Run(() => { Volatile.Write(ref eventThread, Environment.CurrentManagedThreadId); f.Source.Send(observation); });
        try
        {
            Assert.True(captured.Wait(TimeSpan.FromSeconds(5)));
            f.Clock.Advance(TimeSpan.FromSeconds(1));
            if (switchCharacter) f.Source.Switch(2);
            else
            {
                f.Settings.Current.AbyssKillFeedEnabled = false; f.Source.Notify();
                f.Clock.Advance(TimeSpan.FromSeconds(1));
                f.Settings.Current.AbyssKillFeedEnabled = true; f.Source.Notify();
            }
        }
        finally { release.Set(); }
        await sending;
        Assert.Empty(f.Service.Snapshot);
        if (!switchCharacter)
        {
            f.Source.Emit(NetworkAbyssCombatKind.Kill, 2);
            Assert.Empty(f.Service.Snapshot);
        }
        else
        {
            f.Source.Emit(NetworkAbyssCombatKind.Kill, 1);
            Assert.Equal(1, Assert.Single(f.Service.Snapshot).Sequence);
        }
        f.Source.Emit(NetworkAbyssCombatKind.Kill, 3);
        Assert.Equal(3, f.Service.Snapshot[0].Sequence); Assert.Equal(switchCharacter ? 2 : 1, f.Service.Snapshot.Length);
    }

    [Fact]
    public async Task LoadingBoundaryKeepsHistoryButRejectsAPendingCallbackWhenTheOriginalOwnObjectReturns()
    {
        using var f = new Fixture();
        f.Source.Emit(NetworkAbyssCombatKind.Kill, 1, "Synthetic retained opponent");
        var retained = Assert.Single(f.Service.Snapshot); var own = f.Source.OwnCharacter!;
        using var captured = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var eventThread = 0;
        f.Source.ReadOwnHook = () =>
        {
            if (Environment.CurrentManagedThreadId != Volatile.Read(ref eventThread)) return;
            captured.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Synthetic source-read wait expired");
        };
        var sending = Task.Run(() =>
        {
            Volatile.Write(ref eventThread, Environment.CurrentManagedThreadId);
            f.Source.Send(new(f.Clock.GetUtcNow(), 2, own, NetworkAbyssCombatKind.Kill));
        });
        try
        {
            Assert.True(captured.Wait(TimeSpan.FromSeconds(5)));
            f.Source.SetOwn(null); f.Source.SetOwn(own);
        }
        finally { release.Set(); }
        await sending;
        f.Source.Emit(NetworkAbyssCombatKind.Kill, 2);
        Assert.Same(retained, Assert.Single(f.Service.Snapshot));
        f.Source.Emit(NetworkAbyssCombatKind.Kill, 3);
        Assert.Equal(2, f.Service.Snapshot.Length); Assert.Same(retained, f.Service.Snapshot[1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AKindFilterBoundaryRejectsOnlyItsOwnPendingEvents(bool toggleSameKind)
    {
        using var f = new Fixture();
        var own = f.Source.OwnCharacter!;
        var observation = new NetworkAbyssCombatEvent(f.Clock.GetUtcNow(), 1, own, NetworkAbyssCombatKind.Kill);
        using var captured = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var eventThread = 0;
        f.Source.ReadOwnHook = () =>
        {
            if (Environment.CurrentManagedThreadId != Volatile.Read(ref eventThread)) return;
            captured.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Synthetic source-read wait expired");
        };
        var sending = Task.Run(() => { Volatile.Write(ref eventThread, Environment.CurrentManagedThreadId); f.Source.Send(observation); });
        try
        {
            Assert.True(captured.Wait(TimeSpan.FromSeconds(5)));
            // Keep the monotonic stamp identical: callback order still establishes an exact boundary.
            var changedKind = toggleSameKind ? NetworkAbyssCombatKind.Kill : NetworkAbyssCombatKind.Assist;
            f.Select(changedKind, false); f.Select(changedKind, true);
        }
        finally { release.Set(); }
        await sending;
        if (toggleSameKind) Assert.Empty(f.Service.Snapshot);
        else Assert.Single(f.Service.Snapshot);
        f.Source.Emit(NetworkAbyssCombatKind.Kill, 1);
        Assert.Equal(toggleSameKind ? 0 : 1, f.Service.Snapshot.Length);
        f.Source.Emit(NetworkAbyssCombatKind.Kill, 2);
        Assert.Equal(2, f.Service.Snapshot[0].Sequence);
    }

    [Fact]
    public void DisposingClearsAndDetachesTheLiveFeed()
    {
        using var f = new Fixture(); f.Source.Emit(NetworkAbyssCombatKind.Kill, 1, "Synthetic opponent");
        f.Service.Dispose(); Assert.Empty(f.Service.Snapshot);
        f.Source.Emit(NetworkAbyssCombatKind.Kill, 2); f.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(f.Service.Snapshot);
    }

    private sealed class Fixture : IDisposable
    {
        public SettingsService Settings { get; } = new();
        public FakeSource Source { get; } = new();
        public ManualTime Clock { get; } = new();
        public AbyssKillFeedService Service { get; }
        public Fixture(bool showKills = true, bool showDeaths = false, bool showAssists = true)
        {
            Settings.Current.AbyssKillFeedEnabled = true; Settings.Current.LootTrackingEnabled = true;
            Settings.Current.AbyssKillFeedShowKills = showKills; Settings.Current.AbyssKillFeedShowDeaths = showDeaths;
            Settings.Current.AbyssKillFeedShowAssists = showAssists;
            Source.UtcNow = Clock.GetUtcNow; Service = new(Settings, Source, Clock);
        }
        public void Select(NetworkAbyssCombatKind kind, bool selected)
        {
            switch (kind)
            {
                case NetworkAbyssCombatKind.Kill: Settings.Current.AbyssKillFeedShowKills = selected; break;
                case NetworkAbyssCombatKind.Death: Settings.Current.AbyssKillFeedShowDeaths = selected; break;
                case NetworkAbyssCombatKind.Assist: Settings.Current.AbyssKillFeedShowAssists = selected; break;
            }
            Source.Notify();
        }
        public void Dispose() => Service.Dispose();
    }
    private sealed class FakeSource : INetworkAbyssCombatSource
    {
        private long _generation = 1;
        private OwnCharacterContext? _own = new(new(1, 1, 1, "Synthetic character"), 1)
        { ObservedAt = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero) };
        public bool Running { get; set; } = true;
        public NetworkAbyssCombatCapabilities CombatCapabilities { get; set; } = new(true, true, true);
        public Action? ReadOwnHook { get; set; }
        public OwnCharacterContext? OwnCharacter
        {
            get { var own = _own; ReadOwnHook?.Invoke(); return own; }
            private set => _own = value;
        }
        public event Action<NetworkAbyssCombatEvent>? CombatEvent;
        public event Action? OwnCharacterChanged;
        public event Action? Changed;
        public Func<DateTimeOffset> UtcNow { get; set; } = () => DateTimeOffset.UtcNow;
        public void Notify() => Changed?.Invoke();
        public void Send(NetworkAbyssCombatEvent observation) => CombatEvent?.Invoke(observation);
        public void Emit(NetworkAbyssCombatKind kind, long sequence, string? name = null, long? rankDelta = null, DateTimeOffset? at = null,
            string? classId = null, int? rankGrade = null, long? apGain = null) =>
            Send(new(at ?? UtcNow(), sequence, OwnCharacter!, kind, name, rankDelta)
            { OpponentClassId = classId, OpponentAbyssRankGrade = rankGrade, AbyssPointGain = apGain });
        public void Switch(int id) => Reenter(new(id, 1, id, "Synthetic character"));
        public void Reenter(OwnCharacterIdentity identity) => SetOwn(new(identity, ++_generation) { ObservedAt = UtcNow() });
        public void SetOwn(OwnCharacterContext? own) { OwnCharacter = own; OwnCharacterChanged?.Invoke(); Changed?.Invoke(); }
    }
    private sealed class ManualTime : TimeProvider
    {
        private long _stamp;
        public TimeSpan WallOffset { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _stamp);
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero).AddTicks(GetTimestamp()).Add(WallOffset);
        public void Advance(TimeSpan by) => Interlocked.Add(ref _stamp, by.Ticks);
    }
}
