using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Synthetic inventory/entry messages only; no process, capture or actual profile is opened.</summary>
public sealed class NetworkKinaSourceTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch;
    private static readonly OwnCharacterIdentity Identity = new(7, 9, 300, "SyntheticKina");
    private const string Game = "synthetic-kina-own", Foreign = "synthetic-kina-foreign";
    private const int UnboundId = 930100031, BoundId = 930100001;

    [Fact]
    public void ExplicitOwnAbyssWalletIsABalanceWithoutFarmOrRankAuthority()
    {
        using var f = new Fixture();
        var apEvents = 0; var rankEvents = 0;
        f.Network.AbyssPointsGained += _ => apEvents++;
        f.Network.RankPointsChanged += _ => rankEvents++;
        f.Begin(); f.Send(Snapshot(Unbound(100), new ItemState(91, 930100003, 125_000)), milliseconds: 20);
        Assert.Null(f.Network.AbyssSnapshot.AbyssPointBalance);
        f.Prepare(); f.Bind(milliseconds: 100);
        var snapshot = f.Network.AbyssSnapshot;
        Assert.Same(f.Source.OwnCharacter, snapshot.Character);
        Assert.Equal(125_000, snapshot.AbyssPointBalance);
        Assert.Null(snapshot.FarmedAbyssPoints); Assert.Null(snapshot.RankPoints);
        Assert.False(snapshot.AbyssPointGainsQualified); Assert.False(snapshot.RankPointChangesQualified);
        Assert.False(snapshot.KinaGainsQualified);
        Assert.Equal(0, apEvents); Assert.Equal(0, rankEvents);
        f.Network.ResetProgressContext("synthetic-gap");
        Assert.Null(f.Network.AbyssSnapshot.Character); Assert.Null(f.Network.AbyssSnapshot.AbyssPointBalance);
    }

    [Fact]
    public void SupportedAbyssModificationRefreshesOnlyTheAbsoluteWalletWithoutCreatingIncome()
    {
        using var f = new Fixture();
        f.Login(Snapshot(Unbound(100), new ItemState(91, 930100003, 125_000)));
        f.Send(Changes(Change(new ItemState(91, 930100003, 126_000))), Foreign, 150);
        Assert.Equal(125_000, f.Network.AbyssSnapshot.AbyssPointBalance);
        f.Send(Changes(Change(new ItemState(91, 930100003, 126_000))), milliseconds: 200);
        Assert.Equal(126_000, f.Network.AbyssSnapshot.AbyssPointBalance);
        Assert.False(f.Network.AbyssSnapshot.AbyssPointGainsQualified);
        Assert.Null(f.Network.AbyssSnapshot.FarmedAbyssPoints);
        Assert.Empty(f.Gains);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AbyssInventoryInvalidationDoesNotRevokeOrRefreshTheIndependentOwnRank(bool malformed)
    {
        using var f = new Fixture();
        f.Begin(); f.Send(Snapshot(Unbound(100), new ItemState(91, 930100003, 125_000)), milliseconds: 20);
        f.Prepare();
        f.Network.ObserveIdentity(Game, Identity, At.AddMilliseconds(100), abyssRankPoints: 900_000);
        var before = f.Network.AbyssSnapshot;
        Assert.Equal(125_000, before.AbyssPointBalance);
        Assert.Equal(900_000, before.RankPoints);
        Assert.Equal(At.AddMilliseconds(20), before.AbyssPointBalanceObservedAt);
        Assert.Equal(At.AddMilliseconds(100), before.RankPointsObservedAt);

        f.Send(malformed ? [0x1B, 0x56] : Changes(Change(new ItemState(91, 930100003, 126_000), increase: 12)), milliseconds: 200);
        var after = f.Network.AbyssSnapshot;
        Assert.Null(after.AbyssPointBalance);
        Assert.Equal(900_000, after.RankPoints);
        Assert.Equal(before.RankPointsObservedAt, after.RankPointsObservedAt);
        Assert.Same(before.Character, after.Character);
        Assert.False(after.RankPointChangesQualified);
        Assert.Empty(f.Gains);
    }

    [Fact]
    public void LateInventoryListRevokesOnlyWalletAuthorityAndKeepsTheOwnRankEvidence()
    {
        using var f = new Fixture();
        f.Begin(); f.Send(Snapshot(Unbound(100), new ItemState(91, 930100003, 125_000)), milliseconds: 20);
        f.Prepare();
        f.Network.ObserveIdentity(Game, Identity, At.AddMilliseconds(100), abyssRankPoints: 900_000);
        var before = f.Network.AbyssSnapshot;
        f.Send(Snapshot(Unbound(110), new ItemState(91, 930100003, 126_000)), milliseconds: 200);
        var after = f.Network.AbyssSnapshot;
        Assert.False(f.Source.KinaSnapshot.IsReady);
        Assert.Null(after.AbyssPointBalance);
        Assert.Equal(before.RankPoints, after.RankPoints);
        Assert.Equal(before.RankPointsObservedAt, after.RankPointsObservedAt);
        Assert.Same(before.Character, after.Character);
        Assert.False(after.RankPointChangesQualified);
        Assert.Empty(f.Gains);
    }

    [Fact]
    public void AbyssWalletChangedBeforeOwnCannotCommitTheEarlierLoginBalance()
    {
        using var f = new Fixture(); f.Begin();
        f.Send(Snapshot(Unbound(100), new ItemState(91, 930100003, 125_000)), milliseconds: 20);
        f.Send(Changes(Change(new ItemState(91, 930100003, 126_000))), milliseconds: 30);
        f.Prepare(); f.Bind(milliseconds: 100);
        Assert.Null(f.Network.AbyssSnapshot.AbyssPointBalance);
    }

    [Fact]
    public void DuplicateAbyssCurrencyOrInstanceCannotConfirmAnAmbiguousWallet()
    {
        using var f = new Fixture();
        f.Login(Snapshot(Unbound(100), new ItemState(91, 930100003, 1), new ItemState(92, 930100003, 2)));
        Assert.Null(f.Network.AbyssSnapshot.AbyssPointBalance);
    }

    [Fact]
    public void InitialSourceHasNoInventedBalanceOrIncome()
    {
        using var f = new Fixture();
        Assert.Null(f.Source.KinaSnapshot.UnboundBalance);
        Assert.Null(f.Source.KinaSnapshot.BoundBalance);
        Assert.Null(f.Source.KinaSnapshot.BaselineAt);
        Assert.False(f.Source.KinaSnapshot.IsReady);
        Assert.False(f.Source.KinaSnapshot.FormatRejected);
        Assert.NotEmpty(f.Source.KinaSnapshot.Status);
        Assert.Empty(f.Gains);
    }

    [Fact]
    public void ListBeforePreparationIsAppliedOnlyAtMatchingOwn()
    {
        using var f = new Fixture();
        f.Begin();
        f.Send(Snapshot(Unbound(100), Bound(50)), milliseconds: 20);
        Assert.Null(f.Source.KinaSnapshot.UnboundBalance);
        Assert.Null(f.Source.KinaSnapshot.BoundBalance);
        Assert.False(f.Source.KinaSnapshot.IsReady);
        f.Prepare();
        Assert.False(f.Source.KinaSnapshot.IsReady);
        f.Bind(milliseconds: 100);
        Assert.Equal(100L, f.Source.KinaSnapshot.UnboundBalance);
        Assert.Equal(50L, f.Source.KinaSnapshot.BoundBalance);
        Assert.NotNull(f.Source.KinaSnapshot.BaselineAt);
        Assert.True(f.Source.KinaSnapshot.IsReady);
        Assert.False(f.Source.KinaSnapshot.FormatRejected);
        Assert.Empty(f.Gains);
    }

    [Fact]
    public void ListAfterPreparationIsOutsideTheQualifiedLoginVariant()
    {
        using var f = new Fixture(); f.Begin(); f.Prepare();
        f.Send(Snapshot(Unbound(100), Bound(50)), milliseconds: 60);
        f.Bind(milliseconds: 100);
        f.Send(Changes(Change(Unbound(150))), milliseconds: 200);
        Assert.False(f.Source.KinaSnapshot.IsReady);
        Assert.Empty(f.Gains);
    }

    [Fact]
    public void FirstCombatGainUsesTheLoginBalanceAndKeepsItsOwnContextAndTime()
    {
        using var f = new Fixture(); f.Login();
        var character = f.Source.OwnCharacter;
        f.Send(Changes(Change(Unbound(166))), milliseconds: 200);
        var gain = Assert.Single(f.Gains);
        Assert.Equal(66L, gain.UnboundKina);
        Assert.Equal(At.AddMilliseconds(200), gain.At);
        Assert.Same(character, gain.Character);
        Assert.True(gain.Sequence > 0);
        Assert.Equal(166L, f.Source.KinaSnapshot.UnboundBalance);
        Assert.Equal(50L, f.Source.KinaSnapshot.BoundBalance);
        f.Send(Changes(Change(Unbound(304))), milliseconds: 300);
        Assert.Equal(new long[] { 66, 138 }, f.Gains.Select(g => g.UnboundKina));
        Assert.True(f.Gains[1].Sequence > f.Gains[0].Sequence);
    }

    [Fact]
    public void MissingBoundRecordRemainsUnknownAndDoesNotBlockKnownUnboundIncome()
    {
        using var f = new Fixture(); f.Login(Snapshot(Unbound(100)));
        Assert.Null(f.Source.KinaSnapshot.BoundBalance);
        Assert.True(f.Source.KinaSnapshot.IsReady);
        f.Send(Changes(Change(Unbound(115))), milliseconds: 200);
        Assert.Equal(15L, Assert.Single(f.Gains).UnboundKina);
        Assert.Null(f.Source.KinaSnapshot.BoundBalance);
    }

    [Fact]
    public void MissingUnboundRecordCannotBeInitializedFromAnArbitraryModification()
    {
        using var f = new Fixture(); f.Login(Snapshot(Bound(50)));
        f.Send(Changes(Change(Unbound(150))), milliseconds: 200);
        Assert.Null(f.Source.KinaSnapshot.UnboundBalance);
        Assert.Equal(50L, f.Source.KinaSnapshot.BoundBalance);
        Assert.False(f.Source.KinaSnapshot.IsReady);
        Assert.Empty(f.Gains);
    }

    [Fact]
    public void StorageAndOrdinaryItemsAreFullyReadWithoutBecomingWalletBalances()
    {
        using var f = new Fixture();
        f.Login(Snapshot(Bound(50),
            new ItemState(41, 610660025, 3), new ItemState(42, 610660028, 4),
            Unbound(1_000_000, instance: 43) with { Container = 2 },
            new ItemState(44, 610660001, 7), Unbound(100)));
        Assert.Equal(100L, f.Source.KinaSnapshot.UnboundBalance);
        Assert.Equal(50L, f.Source.KinaSnapshot.BoundBalance);
        f.Send(Changes(Change(new ItemState(41, 610660025, 9), increase: 41)), milliseconds: 200);
        Assert.Empty(f.Gains);
        Assert.False(f.Source.KinaSnapshot.FormatRejected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SnapshotWithoutEntryNeverCreatesABaseline(bool ownAlreadyBound)
    {
        using var f = new Fixture();
        if (ownAlreadyBound) f.Bind();
        f.Send(Snapshot(Unbound(100), Bound(50)), milliseconds: 20);
        if (!ownAlreadyBound) f.Bind(milliseconds: 100);
        f.Send(Changes(Change(Unbound(150))), milliseconds: 200);
        Assert.False(f.Source.KinaSnapshot.IsReady);
        Assert.Empty(f.Gains);
    }

    [Fact]
    public void EntryWithoutPreparationCannotAuthorizeTheList()
    {
        using var f = new Fixture(); f.Begin();
        f.Send(Snapshot(Unbound(100)), milliseconds: 20);
        f.Bind(milliseconds: 100);
        f.Send(Changes(Change(Unbound(150))), milliseconds: 200);
        Assert.False(f.Source.KinaSnapshot.IsReady);
        Assert.Empty(f.Gains);
    }

    [Theory]
    [InlineData(8, 300)]
    [InlineData(7, 301)]
    public void EntryMustMatchBothCharacterAndEntity(long character, int entity)
    {
        using var f = new Fixture(); f.Begin();
        f.Send(Snapshot(Unbound(100)), milliseconds: 20); f.Prepare();
        f.Bind(new(character, 9, entity, "SyntheticMismatch"), milliseconds: 100);
        f.Send(Changes(Change(Unbound(150))), milliseconds: 200);
        Assert.False(f.Source.KinaSnapshot.IsReady);
        Assert.Empty(f.Gains);
    }

    [Fact]
    public void ForeignSnapshotsAndChangesCannotMutateTheOwnWallet()
    {
        using var f = new Fixture(); f.Login();
        f.Send(Snapshot(Unbound(9000)), Foreign, 150);
        f.Send(Changes(Change(Unbound(9999))), Foreign, 200);
        Assert.Equal(100L, f.Source.KinaSnapshot.UnboundBalance);
        Assert.Equal(50L, f.Source.KinaSnapshot.BoundBalance);
        Assert.Empty(f.Gains);
        f.Send(Changes(Change(Unbound(110))), milliseconds: 250);
        Assert.Equal(10L, Assert.Single(f.Gains).UnboundKina);
    }

    [Fact]
    public void ForeignPendingListCannotBeCommittedByTheOwnStream()
    {
        using var f = new Fixture(); f.Begin();
        f.Send(Snapshot(Unbound(100)), Foreign, 20); f.Prepare(); f.Bind(milliseconds: 100);
        Assert.False(f.Source.KinaSnapshot.IsReady);
        f.Send(Changes(Change(Unbound(200))), milliseconds: 200);
        Assert.Empty(f.Gains);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DuplicatePendingListsAndPreOwnMoneyChangesRejectTheWholeCandidate(bool moneyChange)
    {
        using var f = new Fixture(); f.Begin();
        f.Send(Snapshot(Unbound(100), Bound(50)), milliseconds: 20);
        f.Send(moneyChange ? Changes(Change(Unbound(150))) : Snapshot(Unbound(100), Bound(50)), milliseconds: 30);
        f.Prepare(); f.Bind(milliseconds: 100);
        f.Send(Changes(Change(Unbound(200))), milliseconds: 200);
        Assert.False(f.Source.KinaSnapshot.IsReady);
        Assert.True(f.Source.KinaSnapshot.FormatRejected);
        Assert.Empty(f.Gains);
    }

    [Fact]
    public void NonWalletPendingChangesDoNotInvalidateAnOtherwiseQualifiedList()
    {
        using var f = new Fixture(); f.Begin();
        f.Send(Snapshot(Unbound(100)), milliseconds: 20);
        f.Send(Changes(Change(new ItemState(40, 610660025, 3), increase: 41)), milliseconds: 30);
        f.Prepare(); f.Bind(milliseconds: 100);
        f.Send(Changes(Change(Unbound(110))), milliseconds: 200);
        Assert.Equal(10L, Assert.Single(f.Gains).UnboundKina);
    }

    [Theory]
    [InlineData((ushort)0)]
    [InlineData((ushort)529)]
    public void UnobservedListHeadersRemainRejected(ushort header)
    {
        using var f = new Fixture(); f.Begin();
        f.Send(SnapshotWithHeader(header, Unbound(100)), milliseconds: 20);
        f.Prepare(); f.Bind(milliseconds: 100);
        Assert.False(f.Source.KinaSnapshot.IsReady);
        Assert.True(f.Source.KinaSnapshot.FormatRejected);
        Assert.Empty(f.Gains);
    }

    [Theory]
    [InlineData(0, 11, 0, 0)]
    [InlineData(1, 24, 0, 0)]
    [InlineData(1, 11, 1, 0)]
    [InlineData(1, 11, 0, 1)]
    public void ValidUnqualifiedIncreaseOnlyMovesTheComparisonBase(byte modification, byte increase, byte decrease, byte update)
    {
        using var f = new Fixture(); f.Login();
        f.Send(Changes(Change(Unbound(160), modification, increase, decrease, update)), milliseconds: 200);
        Assert.Empty(f.Gains);
        Assert.Equal(160L, f.Source.KinaSnapshot.UnboundBalance);
        f.Send(Changes(Change(Unbound(170))), milliseconds: 300);
        Assert.Equal(10L, Assert.Single(f.Gains).UnboundKina);
    }

    [Fact]
    public void NegativeChangesAndRepeatedAbsoluteCountsAreNotIncome()
    {
        using var f = new Fixture(); f.Login();
        f.Send(Changes(Change(Unbound(50), decrease: 1)), milliseconds: 200);
        f.Send(Changes(Change(Unbound(50))), milliseconds: 250);
        Assert.Empty(f.Gains);
        f.Send(Changes(Change(Unbound(80))), milliseconds: 300);
        Assert.Equal(30L, Assert.Single(f.Gains).UnboundKina);
    }

    [Fact]
    public void BoundChangesAreVisibleButDoNotEmitUnqualifiedBoundIncome()
    {
        using var f = new Fixture(); f.Login();
        f.Send(Changes(Change(Bound(80))), milliseconds: 200);
        Assert.Equal(80L, f.Source.KinaSnapshot.BoundBalance);
        Assert.Equal(100L, f.Source.KinaSnapshot.UnboundBalance);
        Assert.Empty(f.Gains);
    }

    [Fact]
    public void UnknownUnboundInstanceInvalidatesOnlyThatCurrency()
    {
        using var f = new Fixture(); f.Login();
        f.Send(Changes(Change(Unbound(9000, instance: 99))), milliseconds: 200);
        Assert.Null(f.Source.KinaSnapshot.UnboundBalance);
        Assert.Equal(50L, f.Source.KinaSnapshot.BoundBalance);
        Assert.False(f.Source.KinaSnapshot.IsReady);
        f.Send(Changes(Change(Unbound(9010, instance: 99))), milliseconds: 300);
        Assert.Null(f.Source.KinaSnapshot.UnboundBalance);
        Assert.Empty(f.Gains);
    }

    [Fact]
    public void MovingTheKnownWalletInstanceOutOfInventoryInvalidatesOnlyThatBalance()
    {
        using var f = new Fixture(); f.Login();
        f.Send(Changes(Change(Unbound(100) with { Container = 2 })), milliseconds: 200);
        Assert.Null(f.Source.KinaSnapshot.UnboundBalance);
        Assert.Equal(50L, f.Source.KinaSnapshot.BoundBalance);
        f.Send(Changes(Change(Unbound(110))), milliseconds: 300);
        Assert.Empty(f.Gains);
        Assert.False(f.Source.KinaSnapshot.IsReady);
    }

    [Fact]
    public void AnotherStorageInstanceOfTheSameCurrencyCannotInvalidateTheOwnWallet()
    {
        using var f = new Fixture(); f.Login();
        f.Send(Changes(Change(Unbound(1_000_000, instance: 99) with { Container = 2 })), milliseconds: 200);
        Assert.Equal(100L, f.Source.KinaSnapshot.UnboundBalance);
        Assert.Equal(50L, f.Source.KinaSnapshot.BoundBalance);
        f.Send(Changes(Change(Unbound(110))), milliseconds: 300);
        Assert.Equal(10L, Assert.Single(f.Gains).UnboundKina);
    }

    [Fact]
    public void RemovingTheWalletInstanceCannotTurnANewInstanceIntoIncome()
    {
        using var f = new Fixture(); f.Login();
        f.Send(Changes(Change(Unbound(0), modification: 2)), milliseconds: 200);
        Assert.Null(f.Source.KinaSnapshot.UnboundBalance);
        f.Send(Changes(Change(Unbound(9000, instance: 99), modification: 0)), milliseconds: 300);
        Assert.Empty(f.Gains);
        Assert.Equal(50L, f.Source.KinaSnapshot.BoundBalance);
    }

    [Theory]
    [InlineData("own-gap")]
    [InlineData("global-gap")]
    [InlineData("reset")]
    [InlineData("own-refresh")]
    [InlineData("character-change")]
    public void EveryOwnBoundaryClearsWalletAuthority(string boundary)
    {
        using var f = new Fixture(); f.Login();
        switch (boundary)
        {
            case "own-gap": f.Network.ProcessProgressGap(Game); break;
            case "global-gap": f.Network.ProcessProgressGap("*"); break;
            case "reset": f.Network.ResetProgressContext(); break;
            case "own-refresh": f.Bind(milliseconds: 150); break;
            case "character-change": f.Bind(new(8, 9, 301, "SyntheticOther"), milliseconds: 150); break;
        }
        Assert.Null(f.Source.KinaSnapshot.UnboundBalance);
        Assert.Null(f.Source.KinaSnapshot.BoundBalance);
        Assert.Null(f.Source.KinaSnapshot.BaselineAt);
        Assert.False(f.Source.KinaSnapshot.IsReady);
        f.Send(Changes(Change(Unbound(500))), milliseconds: 200);
        Assert.Empty(f.Gains);
    }

    [Fact]
    public void ForeignGapDoesNotInvalidateTheOwnWallet()
    {
        using var f = new Fixture(); f.Login();
        f.Network.ProcessProgressGap(Foreign);
        Assert.Equal(100L, f.Source.KinaSnapshot.UnboundBalance);
        f.Send(Changes(Change(Unbound(110))), milliseconds: 200);
        Assert.Equal(10L, Assert.Single(f.Gains).UnboundKina);
    }

    [Fact]
    public void NewLoginUsesItsNewInstanceAndNeverThePreviousCharacterBalance()
    {
        using var f = new Fixture(); f.Login();
        f.Send(Changes(Change(Unbound(110))), milliseconds: 200);
        f.Begin(milliseconds: 300);
        f.Send(Snapshot(Unbound(9000, instance: 99), Bound(10, instance: 98)), milliseconds: 320);
        f.Prepare(milliseconds: 340); f.Bind(milliseconds: 400);
        Assert.Equal(9000L, f.Source.KinaSnapshot.UnboundBalance);
        f.Send(Changes(Change(Unbound(9015, instance: 99))), milliseconds: 500);
        Assert.Equal(new long[] { 10, 15 }, f.Gains.Select(g => g.UnboundKina));
        Assert.NotSame(f.Gains[0].Character, f.Gains[1].Character);
    }

    [Fact]
    public void MalformedInventoryInvalidatesOnlyKinaAndDoesNotBlockGlobalSouls()
    {
        using var f = new Fixture(); f.Login();
        var character = f.Source.OwnCharacter;
        var souls = new List<NetworkSoul>(); f.Network.SoulGained += souls.Add;
        f.Send([.. Changes(Change(Unbound(120))), 0], milliseconds: 200);
        Assert.Same(character, f.Source.OwnCharacter);
        Assert.True(f.Source.KinaSnapshot.FormatRejected);
        Assert.False(f.Source.KinaSnapshot.IsReady);
        f.Send(Packet(0x900D, w => { w.V(1); w.I32(1004); w.I32(2); }), milliseconds: 250);
        Assert.Equal(2, Assert.Single(souls).Souls);
        Assert.Empty(f.Gains);
    }

    [Fact]
    public void PublishedSnapshotsAreImmutableAfterAnotherBalanceObservation()
    {
        using var f = new Fixture(); f.Login();
        var before = f.Source.KinaSnapshot;
        f.Send(Changes(Change(Unbound(120))), milliseconds: 200);
        Assert.Equal(100L, before.UnboundBalance);
        Assert.Equal(120L, f.Source.KinaSnapshot.UnboundBalance);
        Assert.NotSame(before, f.Source.KinaSnapshot);
    }

    [Fact]
    public void ExactInt64BalancesDoNotLoseSmallGains()
    {
        using var f = new Fixture();
        f.Login(Snapshot(Unbound(long.MaxValue - 10)));
        f.Send(Changes(Change(Unbound(long.MaxValue))), milliseconds: 200);
        Assert.Equal(10L, Assert.Single(f.Gains).UnboundKina);
        Assert.Equal(long.MaxValue, f.Source.KinaSnapshot.UnboundBalance);
    }

    [Fact]
    public void DisabledSourceCannotPublishIncomeOrExposeAReadyLiveWallet()
    {
        using var f = new Fixture(running: false); f.Login();
        f.Send(Changes(Change(Unbound(150))), milliseconds: 200);
        Assert.False(f.Source.KinaSnapshot.IsReady);
        Assert.Empty(f.Gains);
    }

    [Theory]
    [InlineData("preparation")]
    [InlineData("own")]
    public void ReentrantCurrencyDuringAnAuthorityResetCannotResurrectAnOlderPendingList(string boundary)
    {
        using var f = new Fixture(); f.Begin();
        f.Send(Snapshot(Unbound(100), Bound(50)), milliseconds: 20);
        if (boundary == "own") f.Prepare();
        var injected = false;
        f.Network.WorldContext.Changed += () =>
        {
            if (injected) return;
            injected = true;
            f.Send(Changes(Change(Unbound(150))), milliseconds: 80);
        };
        if (boundary == "preparation") f.Prepare();
        f.Bind(milliseconds: 100);
        Assert.True(injected);
        Assert.False(f.Source.KinaSnapshot.IsReady);
        Assert.True(f.Source.KinaSnapshot.FormatRejected);
        f.Send(Changes(Change(Unbound(200))), milliseconds: 200);
        Assert.Empty(f.Gains);
    }

    [Theory]
    [InlineData(120000, true)]
    [InlineData(120001, false)]
    public void EntryLifetimeHasAnExactBoundaryAndCannotLeaveAnExpiredWalletCandidate(int ownMilliseconds, bool accepted)
    {
        using var f = new Fixture(); f.Begin();
        f.Send(Snapshot(Unbound(100)), milliseconds: 20); f.Prepare();
        f.Bind(milliseconds: ownMilliseconds);
        Assert.Equal(accepted, f.Source.KinaSnapshot.IsReady);
        f.Send(Changes(Change(Unbound(110))), milliseconds: ownMilliseconds + 1000);
        Assert.Equal(accepted ? 1 : 0, f.Gains.Count);
    }

    [Fact]
    public void ReentrantGainsKeepEverySubscriberInSequenceOrder()
    {
        using var f = new Fixture(); f.Login();
        var calls = new List<string>();
        f.Network.KinaGained += gain =>
        {
            calls.Add("A:" + gain.UnboundKina);
            if (gain.UnboundKina == 10) f.Send(Changes(Change(Unbound(130))), milliseconds: 250);
        };
        f.Network.KinaGained += gain => calls.Add("B:" + gain.UnboundKina);
        f.Send(Changes(Change(Unbound(110))), milliseconds: 200);
        Assert.Equal(new[] { "A:10", "B:10", "A:20", "B:20" }, calls);
        Assert.Equal(new long[] { 10, 20 }, f.Gains.Select(g => g.UnboundKina));
        Assert.True(f.Gains[1].Sequence > f.Gains[0].Sequence);
    }

    [Fact]
    public void ReentrantOwnChangeDiscardsQueuedOldGains()
    {
        using var f = new Fixture(); f.Login();
        var changed = false;
        f.Network.KinaGained += gain =>
        {
            if (changed) return;
            changed = true;
            f.Send(Changes(Change(Unbound(130))), milliseconds: 250);
            f.Bind(new(8, 9, 301, "SyntheticOther"), milliseconds: 300);
        };
        f.Send(Changes(Change(Unbound(110))), milliseconds: 200);
        Assert.Equal(10L, Assert.Single(f.Gains).UnboundKina);
        Assert.False(f.Source.KinaSnapshot.IsReady);
        Assert.Equal(8L, Assert.IsType<OwnCharacterContext>(f.Source.OwnCharacter).Identity.CharacterId);
    }

    [Fact]
    public void ThrowingSubscriberCannotPreventAnotherSubscriberReceivingTheGain()
    {
        using var f = new Fixture(); f.Login();
        var received = new List<long>();
        f.Network.KinaGained += _ => throw new InvalidOperationException("Synthetic subscriber error");
        f.Network.KinaGained += gain => received.Add(gain.UnboundKina);
        f.Send(Changes(Change(Unbound(110))), milliseconds: 200);
        Assert.Equal(new long[] { 10 }, received);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "soulcrest-kina-source-" + Guid.NewGuid().ToString("N"));
        internal ProgressService Progress { get; }
        internal NetworkLootService Network { get; }
        internal INetworkKinaSource Source => Network;
        internal List<NetworkKinaGain> Gains { get; } = [];
        internal Fixture(bool running = true)
        {
            Directory.CreateDirectory(_directory);
            Progress = new(Path.Combine(_directory, "progress.json"), new PetCatalog([
                new PetDefinition { Id = PetIdTable.Find(1004)!.CatalogId ?? PetCatalog.Slug(PetIdTable.Find(1004)!.En), En = PetIdTable.Find(1004)!.En, Genus = PetIdTable.Find(1004)!.Genus, NumericId = "1004" }]));
            Network = new(Progress);
            typeof(NetworkLootService).GetProperty(nameof(NetworkLootService.Running))!.SetValue(Network, running);
            Network.KinaGained += Gains.Add;
        }
        internal void Begin(OwnCharacterIdentity? identity = null, long milliseconds = 0) =>
            Send(Entry(identity ?? Identity), milliseconds: milliseconds);
        internal void Prepare(long milliseconds = 40) => Send(Preparation(), milliseconds: milliseconds);
        internal void Bind(OwnCharacterIdentity? identity = null, long milliseconds = 0) =>
            Network.ObserveIdentity(Game, identity ?? Identity, At.AddMilliseconds(milliseconds));
        internal void Send(byte[] message, string stream = Game, long milliseconds = 0) =>
            Network.ProcessProgressMessage(stream, message, At.AddMilliseconds(milliseconds));
        internal void Login(byte[]? snapshot = null)
        {
            Begin(); Send(snapshot ?? Snapshot(Unbound(100), Bound(50)), milliseconds: 20);
            Prepare(); Bind(milliseconds: 100);
        }
        public void Dispose() { Network.Dispose(); Directory.Delete(_directory, recursive: true); }
    }

    private sealed record ItemState(long Instance, int Item, long Count, byte Container = 1);
    private sealed record ItemChange(ItemState Item, byte Modification = 1, byte Increase = 11, byte Decrease = 0, byte Update = 0);
    private static ItemState Unbound(long count, long instance = 11) => new(instance, UnboundId, count);
    private static ItemState Bound(long count, long instance = 12) => new(instance, BoundId, count);
    private static ItemChange Change(ItemState item, byte modification = 1, byte increase = 11, byte decrease = 0, byte update = 0) =>
        new(item, modification, increase, decrease, update);
    private static byte[] Snapshot(params ItemState[] items) => SnapshotWithHeader(528, items);
    private static byte[] SnapshotWithHeader(ushort header, params ItemState[] items) => Packet(0x5611, w =>
    {
        w.U16(header); w.V(items.Length);
        foreach (var item in items) w.Item(item);
        w.Bytes(new byte[6]);
    });
    private static byte[] Changes(params ItemChange[] changes) => Packet(0x561B, w =>
    {
        w.V(changes.Length);
        foreach (var change in changes)
        {
            w.U8(change.Modification); w.Item(change.Item);
            w.U8(change.Increase); w.U8(change.Decrease); w.U8(change.Update);
        }
    });
    private static byte[] Entry(OwnCharacterIdentity identity) => Packet(0x3620, w =>
    {
        w.U16(0); w.V(identity.EntityKey); w.Bytes(new byte[4]); w.I64(identity.CharacterId);
    });
    private static byte[] Preparation() => Packet(0x3621, w => w.Bytes(new byte[46]));
    private static byte[] Packet(ushort opcode, Action<WireWriter> write)
    {
        using var writer = new WireWriter(); writer.U16(opcode); write(writer); return writer.ToArray();
    }

    // The inventory bool reservoir spans both scalars and item/list boundaries.
    private sealed class WireWriter : IDisposable
    {
        private readonly MemoryStream _stream = new();
        private readonly BinaryWriter _writer;
        private int _boolOffset = -1, _boolBit;
        internal WireWriter() => _writer = new(_stream);
        internal void U8(byte value) => _writer.Write(value);
        internal void U16(ushort value) => _writer.Write(value);
        internal void I32(int value) => _writer.Write(value);
        internal void I64(long value) => _writer.Write(value);
        internal void Bytes(byte[] value) => _writer.Write(value);
        internal void V(int value)
        {
            var remaining = unchecked((uint)value);
            while (remaining >= 128) { U8((byte)(remaining | 128)); remaining >>= 7; }
            U8((byte)remaining);
        }
        private void Bool(bool value)
        {
            if (_boolOffset < 0) { _boolOffset = (int)_stream.Position; U8(0); _boolBit = 0; }
            if (value) _stream.GetBuffer()[_boolOffset] |= (byte)(1 << _boolBit);
            if (++_boolBit == 8) _boolOffset = -1;
        }
        internal void Item(ItemState item)
        {
            U8(0); I64(item.Instance); I32(item.Item); I64(item.Count); U8(item.Container); I32(0);
            Bool(false); V(0); I64(0); Bool(false);
        }
        internal byte[] ToArray() => _stream.ToArray();
        public void Dispose() { _writer.Dispose(); _stream.Dispose(); }
    }
}
