using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed partial class NetworkAbyssKinaSourceTests
{
    private static Change Bound(long count, byte modification = 1) =>
        new(count, Key: 93, Item: CurrencyInventoryMessage.BoundKinaItemId, Modification: modification);

    [Theory]
    [InlineData((byte)0)] [InlineData((byte)1)]
    public void UnknownBoundWalletUsesItsFirstRecordOnlyAsBaselineAndCountsTheFollowing583(byte firstModification)
    {
        using var f = new Fixture(); f.Own(); var own = f.Network.OwnCharacter;
        f.Send(Changes(Bound(342, firstModification)), At.AddSeconds(1));
        var baseline = f.Network.AbyssSnapshot;
        Assert.Equal(342, f.Network.KinaSnapshot.BoundBalance); Assert.Null(f.Network.KinaSnapshot.UnboundBalance);
        Assert.Equal(At.AddSeconds(1), f.Network.KinaSnapshot.BaselineAt);
        Assert.False(baseline.BoundKinaGainsQualified); Assert.Equal(At.AddSeconds(1), baseline.BoundKinaGainsObservedAt);
        Assert.Empty(f.Gains);
        f.Rank(1_002, 2); var rank = f.Network.AbyssSnapshot;
        f.Send(Changes(Bound(925)), At.AddSeconds(3));
        var current = f.Network.AbyssSnapshot; var gain = Assert.Single(f.Gains);
        Assert.Equal(583, gain.BoundKina); Assert.Equal(0, gain.UnboundKina); Assert.Same(own, gain.Character);
        Assert.True(gain.InInstance); Assert.Equal(At.AddSeconds(3), gain.At);
        Assert.Equal(925, f.Network.KinaSnapshot.BoundBalance); Assert.True(current.BoundKinaGainsQualified);
        Assert.Equal(At.AddSeconds(3), current.BoundKinaGainsObservedAt); Assert.Equal(At.AddSeconds(3), current.At);
        Assert.True(current.Sequence > rank.Sequence); Assert.Equal(rank.RankPointsObservedAt, current.RankPointsObservedAt);
        Assert.Equal(1_002, current.RankPoints); Assert.False(current.KinaGainsQualified);
        f.Send(Changes(Bound(925)), At.AddSeconds(4));
        Assert.Single(f.Gains); Assert.True(f.Network.AbyssSnapshot.BoundKinaGainsQualified); AssertNoOtherIncome(f);
    }

    [Fact]
    public void ExplicitLoginBoundZeroCountsBothPostOwnRewardDifferencesWithoutInventingUnknownZero()
    {
        using var f = new Fixture(); LoginBound(f, 0);
        Assert.Equal(0, f.Network.KinaSnapshot.BoundBalance); Assert.Null(f.Network.KinaSnapshot.UnboundBalance);
        Assert.False(f.Network.AbyssSnapshot.BoundKinaGainsQualified); Assert.Empty(f.Gains);
        f.Send(Changes(Bound(342)), At.AddSeconds(1));
        Assert.Equal(342, Assert.Single(f.Gains).BoundKina); Assert.True(f.Network.AbyssSnapshot.BoundKinaGainsQualified);
        f.Send(Changes(Bound(925)), At.AddSeconds(2));
        Assert.Equal(new long[] { 342, 583 }, f.Gains.Select(gain => gain.BoundKina));
        Assert.Equal(925, f.Gains.Sum(gain => gain.BoundKina)); Assert.All(f.Gains, gain => Assert.Equal(0, gain.UnboundKina));
        Assert.Equal(At.AddSeconds(2), f.Network.AbyssSnapshot.BoundKinaGainsObservedAt); AssertNoOtherIncome(f);
    }

    [Fact]
    public void UnanchoredPreOwnBoundListCannotCatchUpTheFirst406AndSpendingPreservesIncomeAndTheReducedComparison()
    {
        using var f = new Fixture();
        // User comparison 2026-11-13: the observed list/preparation has no preceding entry transaction.
        f.Send(Packet(0x5611, w => { w.U16(528); w.V(1); w.Item(Bound(2_726)); w.Z(6); }), At.AddSeconds(-2));
        f.Send(Packet(0x3621, w => w.Z(46)), At.AddSeconds(-1)); f.Own();
        Assert.Null(f.Network.KinaSnapshot.BoundBalance); Assert.Empty(f.Gains);
        f.Send(Changes(Bound(3_132)), At.AddSeconds(1));
        Assert.Equal(3_132, f.Network.KinaSnapshot.BoundBalance);
        Assert.False(f.Network.AbyssSnapshot.BoundKinaGainsQualified); Assert.Empty(f.Gains);
        f.Send(Changes(Bound(3_553)), At.AddSeconds(2));
        var confirmed = Assert.Single(f.Gains);
        Assert.Equal(421, confirmed.BoundKina); Assert.Equal(0, confirmed.UnboundKina);
        Assert.True(f.Network.AbyssSnapshot.BoundKinaGainsQualified);
        f.Send(Changes(Bound(2_553) with { Increase = 0, Decrease = 9 }), At.AddSeconds(3));
        Assert.Equal(2_553, f.Network.KinaSnapshot.BoundBalance);
        Assert.True(f.Network.AbyssSnapshot.BoundKinaGainsQualified); Assert.Equal(At.AddSeconds(3), f.Network.AbyssSnapshot.BoundKinaGainsObservedAt);
        Assert.Same(confirmed, Assert.Single(f.Gains)); Assert.Equal(421, f.Gains.Sum(gain => gain.BoundKina));
        f.Send(Changes(Bound(2_554)), At.AddSeconds(4));
        Assert.Equal(new long[] { 421, 1 }, f.Gains.Select(gain => gain.BoundKina));
        Assert.Equal(422, f.Gains.Sum(gain => gain.BoundKina)); AssertNoOtherIncome(f);
    }

    [Fact]
    public void DuplicateQualifiedBoundDecreasesProduceNoIncomeAndKeepTheReducedComparison()
    {
        using var f = new Fixture(); f.Own();
        f.Send(Changes(Bound(3_132)), At.AddSeconds(1)); f.Send(Changes(Bound(3_553)), At.AddSeconds(2));
        f.Send(Changes(Bound(2_553) with { Increase = 0, Decrease = 9 }), At.AddSeconds(3));
        f.Send(Changes(Bound(2_553) with { Increase = 0, Decrease = 9 }), At.AddSeconds(4));
        Assert.Equal(421, Assert.Single(f.Gains).BoundKina);
        Assert.Equal(2_553, f.Network.KinaSnapshot.BoundBalance); Assert.True(f.Network.AbyssSnapshot.BoundKinaGainsQualified);
        Assert.Equal(At.AddSeconds(4), f.Network.AbyssSnapshot.BoundKinaGainsObservedAt);
        f.Send(Changes(Bound(2_554)), At.AddSeconds(5));
        Assert.Equal(new long[] { 421, 1 }, f.Gains.Select(gain => gain.BoundKina)); AssertNoOtherIncome(f);
    }

    [Theory]
    [InlineData("unknown")] [InlineData("baseline-only")] [InlineData("login-only")] [InlineData("larger")]
    public void BoundDecreaseRequiresPriorQualificationAndCannotIncreaseTheComparisonBalance(string variant)
    {
        using var f = new Fixture();
        if (variant == "login-only") LoginBound(f, 3_553); else f.Own();
        if (variant is "baseline-only" or "larger") f.Send(Changes(Bound(3_132)), At.AddSeconds(1));
        if (variant == "larger") f.Send(Changes(Bound(3_553)), At.AddSeconds(2));
        var previousIncome = f.Gains.Sum(gain => gain.BoundKina);
        var decreased = Bound(variant == "larger" ? 3_554 : 2_553) with { Increase = 0, Decrease = 9 };
        f.Send(Changes(decreased), At.AddSeconds(3));
        Assert.False(f.Network.AbyssSnapshot.BoundKinaGainsQualified); Assert.Null(f.Network.AbyssSnapshot.BoundKinaGainsObservedAt);
        f.Send(Changes(Bound(4_000)), At.AddSeconds(4)); f.Send(Changes(Bound(4_001)), At.AddSeconds(5));
        Assert.False(f.Network.AbyssSnapshot.BoundKinaGainsQualified);
        Assert.Equal(previousIncome, f.Gains.Sum(gain => gain.BoundKina)); AssertNoOtherIncome(f);
    }

    [Theory]
    [InlineData(610660025)]
    [InlineData(CurrencyInventoryMessage.UnboundKinaItemId)]
    [InlineData(930100003)]
    public void ReplacementOfTheLoginBoundWalletKeyBeforeItsFirstModificationRevokesBoundIncome(int replacementItemId)
    {
        using var f = new Fixture(); LoginBound(f, 0);
        var replacement = Changes(new Change(7, Key: 93, Item: replacementItemId));
        Assert.NotNull(CurrencyInventoryMessage.TryParseChanges(replacement));
        f.Send(replacement, At.AddSeconds(1));
        f.Send(Changes(Bound(342)), At.AddSeconds(2)); f.Send(Changes(Bound(925)), At.AddSeconds(3));
        Assert.DoesNotContain(f.Gains, gain => gain.BoundKina > 0);
        Assert.False(f.Network.AbyssSnapshot.BoundKinaGainsQualified);
        Assert.Null(f.Network.AbyssSnapshot.BoundKinaGainsObservedAt);
    }

    [Fact]
    public void BoundAndUnboundRecordsInOneInventoryKeepSeparateWalletsQualificationsAndSingleGains()
    {
        using var f = new Fixture(); f.Own();
        f.Send(Changes(new Change(488_546), Bound(342, modification: 0)), At.AddSeconds(1));
        Assert.Empty(f.Gains);
        f.Send(Changes(Bound(925), new Change(488_645)), At.AddSeconds(2));
        Assert.Equal(2, f.Gains.Count);
        var bound = Assert.Single(f.Gains, gain => gain.BoundKina > 0);
        var unbound = Assert.Single(f.Gains, gain => gain.UnboundKina > 0);
        Assert.Equal(583, bound.BoundKina); Assert.Equal(0, bound.UnboundKina);
        Assert.Equal(99, unbound.UnboundKina); Assert.Equal(0, unbound.BoundKina);
        Assert.NotEqual(bound.Sequence, unbound.Sequence);
        Assert.Equal(925, f.Network.KinaSnapshot.BoundBalance); Assert.Equal(488_645, f.Network.KinaSnapshot.UnboundBalance);
        Assert.True(f.Network.AbyssSnapshot.BoundKinaGainsQualified); Assert.True(f.Network.AbyssSnapshot.KinaGainsQualified);
        f.Send(Changes(Bound(925), new Change(488_645)), At.AddSeconds(3));
        Assert.Equal(2, f.Gains.Count); AssertNoOtherIncome(f);
    }

    [Theory]
    [InlineData("add")] [InlineData("remove")] [InlineData("reason5")]
    [InlineData("reason12")] [InlineData("reason63")] [InlineData("spending")]
    [InlineData("update")] [InlineData("container")] [InlineData("changed-key")]
    [InlineData("lower")] [InlineData("replaced-id")]
    public void UnsupportedBoundVariantRevokesOnlyItsChannelUntilFreshOwn(string variant)
    {
        using var f = new Fixture(); f.Own(); f.Pair();
        f.Send(Changes(Bound(342)), At.AddSeconds(3)); f.Send(Changes(Bound(925)), At.AddSeconds(4));
        f.Rank(1_002, 5); f.Farm(717_422, 6);
        var before = f.Network.AbyssSnapshot; var own = f.Network.OwnCharacter;
        var invalid = variant switch
        {
            "add" => Bound(926, modification: 0), "remove" => Bound(0, modification: 2),
            "reason5" => Bound(926) with { Increase = 5 }, "reason12" => Bound(926) with { Increase = 12 },
            "reason63" => Bound(926) with { Increase = 63 }, "spending" => Bound(924) with { Increase = 0, Decrease = 66 },
            "update" => Bound(926) with { Update = 1 }, "container" => Bound(926) with { Container = 2 },
            "changed-key" => Bound(926) with { Key = 94 }, "lower" => Bound(924),
            "replaced-id" => Bound(1) with { Item = 610660025 }, _ => throw new InvalidOperationException()
        };
        var message = Changes(invalid); Assert.NotNull(CurrencyInventoryMessage.TryParseChanges(message));
        f.Send(message, At.AddSeconds(7)); var after = f.Network.AbyssSnapshot;
        Assert.False(after.BoundKinaGainsQualified); Assert.Null(after.BoundKinaGainsObservedAt);
        Assert.True(after.KinaGainsQualified); Assert.Equal(before.KinaGainsObservedAt, after.KinaGainsObservedAt);
        Assert.Equal(before.RankPoints, after.RankPoints); Assert.Equal(before.RankPointsObservedAt, after.RankPointsObservedAt);
        Assert.True(after.RankPointChangesQualified); Assert.Equal(before.FarmedAbyssPoints, after.FarmedAbyssPoints);
        Assert.Same(own, f.Network.OwnCharacter);
        f.Send(Changes(Bound(1_000)), At.AddSeconds(8));
        Assert.False(f.Network.AbyssSnapshot.BoundKinaGainsQualified);
        Assert.Equal(583, Assert.Single(f.Gains, gain => gain.BoundKina > 0).BoundKina);
        f.Send(Changes(new Change(488_646)), At.AddSeconds(8).AddMilliseconds(1));
        Assert.Equal(new long[] { 99, 1 }, f.Gains.Where(gain => gain.UnboundKina > 0).Select(gain => gain.UnboundKina));
        Assert.False(f.Network.AbyssSnapshot.BoundKinaGainsQualified);
        f.Own(seconds: 9); f.Send(Changes(Bound(2_000)), At.AddSeconds(10));
        Assert.False(f.Network.AbyssSnapshot.BoundKinaGainsQualified);
        f.Send(Changes(Bound(2_001)), At.AddSeconds(11));
        Assert.True(f.Network.AbyssSnapshot.BoundKinaGainsQualified);
        Assert.Equal(new long[] { 583, 1 }, f.Gains.Where(gain => gain.BoundKina > 0).Select(gain => gain.BoundKina));
        AssertNoOtherIncome(f);
    }

    [Theory]
    [InlineData("before-own")] [InlineData("backward")] [InlineData("default")] [InlineData("future")]
    public void InvalidBoundFactTimeRevokesOnlyBoundAndLeavesUnboundRankFactsIntact(string timing)
    {
        using var f = new Fixture(); f.Own(); f.Pair();
        f.Send(Changes(Bound(342)), At.AddSeconds(3)); f.Send(Changes(Bound(925)), At.AddSeconds(4));
        var before = f.Network.AbyssSnapshot;
        var invalidAt = timing switch
        {
            "before-own" => At.AddTicks(-1), "backward" => At.AddSeconds(3),
            "default" => default, "future" => DateTimeOffset.MaxValue, _ => throw new InvalidOperationException()
        };
        f.Send(Changes(Bound(926)), invalidAt); var after = f.Network.AbyssSnapshot;
        Assert.False(after.BoundKinaGainsQualified); Assert.Null(after.BoundKinaGainsObservedAt);
        Assert.True(after.KinaGainsQualified); Assert.Equal(before.KinaGainsObservedAt, after.KinaGainsObservedAt);
        Assert.Equal(before.RankPoints, after.RankPoints); Assert.Equal(before.RankPointsObservedAt, after.RankPointsObservedAt);
        f.Send(Changes(Bound(927)), At.AddSeconds(5));
        Assert.Equal(583, Assert.Single(f.Gains, gain => gain.BoundKina > 0).BoundKina); AssertNoOtherIncome(f);
    }

    [Theory]
    [InlineData("negative")] [InlineData("zero-key")] [InlineData("equipment")]
    [InlineData("truncated")] [InlineData("duplicate")] [InlineData("collision")]
    public void MalformedInventoryCannotPreserveOrReinitializeBoundIncome(string variant)
    {
        using var f = new Fixture(); f.Own();
        f.Send(Changes(Bound(342)), At.AddSeconds(1)); f.Send(Changes(Bound(925)), At.AddSeconds(2));
        var invalid = variant switch
        {
            "negative" => Changes(Bound(-1)), "zero-key" => Changes(Bound(926) with { Key = 0 }),
            "equipment" => Changes(Bound(926) with { Equipment = true }), "truncated" => Changes(Bound(926))[..^1],
            "duplicate" => Changes(Bound(926), Bound(926)),
            "collision" => Changes(Bound(926), new Change(1, Key: 93, Item: 610660025)),
            _ => throw new InvalidOperationException()
        };
        Assert.Null(CurrencyInventoryMessage.TryParseChanges(invalid));
        f.Send(invalid, At.AddSeconds(3));
        Assert.True(f.Network.KinaSnapshot.FormatRejected); Assert.False(f.Network.AbyssSnapshot.BoundKinaGainsQualified);
        Assert.Null(f.Network.AbyssSnapshot.BoundKinaGainsObservedAt);
        f.Send(Changes(Bound(2_000)), At.AddSeconds(4)); f.Send(Changes(Bound(2_001)), At.AddSeconds(5));
        Assert.Equal(583, Assert.Single(f.Gains).BoundKina); AssertNoOtherIncome(f);
    }

    [Fact]
    public void OwnGapAndFreshOwnDiscardBoundComparisonWithoutCatchingUp()
    {
        using var f = new Fixture(); f.Own();
        f.Send(Changes(Bound(342)), At.AddSeconds(1)); f.Send(Changes(Bound(925)), At.AddSeconds(2));
        f.Network.ProcessProgressGap(Foreign); Assert.True(f.Network.AbyssSnapshot.BoundKinaGainsQualified);
        f.Network.ProcessProgressGap(OwnStream, capturedAtUtc: At.AddSeconds(3));
        Assert.False(f.Network.AbyssSnapshot.BoundKinaGainsQualified); Assert.Null(f.Network.KinaSnapshot.BoundBalance);
        f.Send(Changes(Bound(5_000)), At.AddSeconds(4)); Assert.Single(f.Gains);
        f.Own(seconds: 5); f.Send(Changes(Bound(6_000)), At.AddSeconds(6));
        Assert.Single(f.Gains); Assert.False(f.Network.AbyssSnapshot.BoundKinaGainsQualified);
        f.Send(Changes(Bound(6_001)), At.AddSeconds(7));
        Assert.Equal(new long[] { 583, 1 }, f.Gains.Select(gain => gain.BoundKina)); AssertNoOtherIncome(f);
    }

    [Fact]
    public void BoundPreOwnAndForeignRecordsCannotPrimeOrInvalidateTheOwnBoundComparison()
    {
        using var f = new Fixture(); f.Send(Changes(Bound(100)), At.AddSeconds(-1)); f.Own();
        Assert.Null(f.Network.KinaSnapshot.BoundBalance);
        f.Send(Changes(Bound(342)), At.AddSeconds(1)); f.Send(Changes(Bound(925)), At.AddSeconds(2));
        var before = f.Network.AbyssSnapshot;
        f.Send(Changes(Bound(5_000)), At.AddSeconds(3), Foreign); f.Send([0x1B, 0x56], At.AddSeconds(4), Foreign);
        Assert.Equal(before, f.Network.AbyssSnapshot); Assert.Equal(925, f.Network.KinaSnapshot.BoundBalance);
        Assert.Equal(583, Assert.Single(f.Gains).BoundKina); AssertNoOtherIncome(f);
    }

    [Theory]
    [InlineData("open-world")] [InlineData("unknown-map")] [InlineData("transitioning")]
    [InlineData("other-generation")] [InlineData("world-after-record")]
    public void BoundComparisonRequiresCurrentConfirmedOwnAbyssContext(string context)
    {
        using var f = new Fixture(); f.Own(map: context == "open-world" ? 21 : context == "unknown-map" ? 999 : 20);
        var generation = f.Network.OwnCharacter!.Generation;
        if (context == "transitioning") f.Network.WorldContext.Invalidate(WorldContextStatus.Transitioning, generation, "synthetic-transition", At);
        if (context == "other-generation") f.Network.WorldContext.ObserveOwn(20, generation + 1, At);
        if (context == "world-after-record") f.Network.WorldContext.ObserveOwn(20, generation, At.AddSeconds(5));
        f.Send(Changes(Bound(342, modification: 0)), At.AddSeconds(1)); f.Send(Changes(Bound(925)), At.AddSeconds(2));
        Assert.Null(f.Network.KinaSnapshot.BoundBalance); Assert.False(f.Network.AbyssSnapshot.BoundKinaGainsQualified);
        Assert.Null(f.Network.AbyssSnapshot.BoundKinaGainsObservedAt); Assert.Empty(f.Gains); AssertNoOtherIncome(f);
    }

    [Theory]
    [InlineData("own")] [InlineData("gap")] [InlineData("capture")]
    public void BoundCallbackBoundariesStopRemainingSubscribersAndDiscardOlderQueuedInventory(string boundary)
    {
        using var f = new Fixture(); f.Own(); f.Send(Changes(Bound(342)), At.AddSeconds(1));
        var lastSubscriber = new List<long>();
        f.Network.KinaGained += _ =>
        {
            f.Send(Changes(Bound(926)), At.AddSeconds(3));
            if (boundary == "own") f.Own(5_000, 4);
            else if (boundary == "gap") f.Network.ProcessProgressGap(OwnStream, capturedAtUtc: At.AddSeconds(4));
            else f.ChangeCaptureGeneration();
        };
        f.Network.KinaGained += gain => lastSubscriber.Add(gain.BoundKina);
        f.Send(Changes(Bound(925)), At.AddSeconds(2));
        Assert.Empty(lastSubscriber); Assert.Equal(583, Assert.Single(f.Gains).BoundKina);
    }

    private static void LoginBound(Fixture f, long bound)
    {
        f.Send(Packet(0x3620, w => { w.U16(0); w.V(Identity.EntityKey); w.Z(4); w.I64(Identity.CharacterId); }), At.AddSeconds(-3));
        f.Send(Packet(0x5611, w => { w.U16(528); w.V(1); w.Item(Bound(bound)); w.Z(6); }), At.AddSeconds(-2));
        f.Send(Packet(0x3621, w => w.Z(46)), At.AddSeconds(-1)); f.Own();
    }
}
