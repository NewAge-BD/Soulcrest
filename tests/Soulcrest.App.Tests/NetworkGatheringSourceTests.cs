using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Instances;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Synthetic messages only; no capture, game process or real character profile is opened.</summary>
public sealed class NetworkGatheringSourceTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch;
    private const string Game = "synthetic-own";
    private const string Foreign = "synthetic-foreign";
    private const int OwnEntity = 300;
    private const int Target = 400;
    private const int Item = 610660025;

    [Fact]
    public void VerifiedSourceStartsWithoutAWarningAndWaitsForOwnAuthority()
    {
        using var f = new Fixture();
        Assert.True(f.Source.GatheringVerified);
        Assert.Null(f.Source.GatheringWarning);
        Assert.Equal(0, f.Source.GatheringWarningVersion);
        Assert.Null(f.Source.OwnCharacter);
        f.Send(Completion(0, 1, 1));
        f.Send(Witness());
        Assert.Empty(f.Gatherings);
        Assert.Null(f.Source.OwnCharacter);
    }

    [Theory]
    [InlineData(0, 1, 0, 1)]
    [InlineData(0, 1, 1, 2)]
    [InlineData(1, 1, 0, 1)]
    public void OwnWitnessQualifiesOnlyTheCheckedPrimaryQuantity(int result, int basic, int bonus, long expected)
    {
        using var f = new Fixture(); f.Bind();
        var character = f.Source.OwnCharacter;
        f.Pair(basic, bonus, result: (byte)result);
        var gather = Assert.Single(f.Gatherings);
        Assert.Same(character, gather.Character);
        Assert.Equal(Target, gather.TargetKey);
        Assert.Equal(new NetworkGatheringItem(Item, expected), Assert.Single(gather.Items));
        Assert.Null(gather.Object); // A missing object must not block a verified reward or invent a node.
        Assert.Equal(At.AddMilliseconds(100), gather.At);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothMessageOrdersPublishOnceAndKeepTheCompletionTimestamp(bool witnessFirst)
    {
        using var f = new Fixture(); f.Bind();
        f.Pair(1, 1, milliseconds: 100, witnessFirst: witnessFirst);
        var gather = Assert.Single(f.Gatherings);
        Assert.Equal(At.AddMilliseconds(witnessFirst ? 150 : 100), gather.At);
        Assert.Equal(2, Assert.Single(gather.Items).Quantity);
    }

    [Theory]
    [InlineData(false, 2000, 1)]
    [InlineData(true, 2000, 1)]
    [InlineData(false, 2001, 0)]
    [InlineData(true, 2001, 0)]
    public void PairingWindowIncludesExactlyTwoSecondsButCannotReuseExpiredEvidence(bool witnessFirst, int delay, int expected)
    {
        using var f = new Fixture(); f.Bind();
        f.Send(witnessFirst ? Witness() : Completion(0, 1, 0), milliseconds: 100);
        f.Send(witnessFirst ? Completion(0, 1, 0) : Witness(), milliseconds: 100 + delay);
        Assert.Equal(expected, f.Gatherings.Count);
    }

    [Fact]
    public void AnUnmatchedOwnWitnessDoesNotWarnImmediatelyButItsExpiryIsVisible()
    {
        using var f = new Fixture(); f.Bind();
        f.Send(Witness(), milliseconds: 100);
        Assert.Null(f.Source.GatheringWarning);
        Assert.Empty(f.Gatherings);
        f.Pair(1, 0, target: Target + 1, milliseconds: 2201);
        Assert.NotNull(f.Source.GatheringWarning);
        Assert.True(f.Source.GatheringWarningVersion > 0);
        Assert.Single(f.Gatherings);
    }

    [Fact]
    public void OrdinaryOwnMessageExpiresPendingEvidenceWhileForeignClockCannotForceExpiry()
    {
        using var f = new Fixture(); f.Bind();
        f.Send(Witness(), milliseconds: 100);

        f.Send([0x36, 0x00], Foreign, 2201);
        Assert.Null(f.Source.GatheringWarning);
        Assert.Equal(0, f.Source.GatheringWarningVersion);

        f.Send([0x36, 0x00], milliseconds: 2250);
        Assert.NotNull(f.Source.GatheringWarning);
        Assert.True(f.Source.GatheringWarningVersion > 0);
        Assert.Empty(f.Gatherings);
    }

    [Fact]
    public void ExpiryRebindingCannotQueueTheOldGatheringPacketForTheNewOwn()
    {
        using var f = new Fixture(); f.Bind();
        f.Send(Witness(), milliseconds: 100);
        var rebound = BindNextOwnWhenExpiryWarns(f);

        f.Send(Completion(0, 1, 0), milliseconds: 2201);

        Assert.True(rebound());
        Assert.Equal(OwnEntity + 1, Assert.IsType<OwnCharacterContext>(f.Source.OwnCharacter).Identity.EntityKey);
        f.Send(Witness(actor: OwnEntity + 1), milliseconds: 2250);
        Assert.Empty(f.Gatherings);
    }

    [Fact]
    public void ExpiryRebindingCannotAuthorizeAnOldAcquisitionThatMatchesOnlyTheNewOwn()
    {
        using var f = new Fixture(); f.Bind();
        f.Send(Witness(), milliseconds: 100);
        var rebound = BindNextOwnWhenExpiryWarns(f);
        var acquisition = Packet(0x9009, writer =>
        {
            V(writer, OwnEntity + 1); writer.Write(1004); writer.Write(1); writer.Write(new byte[13]);
        });

        f.Send(acquisition, milliseconds: 2201);

        Assert.True(rebound());
        Assert.Equal(OwnEntity + 1, Assert.IsType<OwnCharacterContext>(f.Source.OwnCharacter).Identity.EntityKey);
        Assert.Null(f.Progress.ObservedLevel("synthetic-pet"));
        f.Send(acquisition, milliseconds: 2300); // The same valid shape is accepted only as a fresh packet.
        Assert.Equal(1, f.Progress.ObservedLevel("synthetic-pet"));
    }

    [Fact]
    public void ExpiryRebindingStillAllowsAnEntityFreeGlobalSoulGain()
    {
        using var f = new Fixture(); f.Bind();
        f.Send(Witness(), milliseconds: 100);
        var rebound = BindNextOwnWhenExpiryWarns(f);
        var souls = new List<NetworkSoul>(); f.Network.SoulGained += souls.Add;

        f.Send(Packet(0x900D, writer => { writer.Write((byte)1); writer.Write(1004); writer.Write(2); }),
            milliseconds: 2201);

        Assert.True(rebound());
        Assert.Equal(OwnEntity + 1, Assert.IsType<OwnCharacterContext>(f.Source.OwnCharacter).Identity.EntityKey);
        Assert.Equal(2, Assert.Single(souls).Souls);
        Assert.Equal(2, f.Progress.Souls("synthetic-pet"));
        Assert.Empty(f.Gatherings);
    }

    private static Func<bool> BindNextOwnWhenExpiryWarns(Fixture f)
    {
        var rebound = false;
        f.Network.Changed += () =>
        {
            if (rebound || f.Source.GatheringWarningVersion == 0) return;
            rebound = true;
            f.Bind(OwnEntity + 1, 124, 2201);
        };
        return () => rebound;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(255)]
    public void MatchedZeroRewardCreatesOneFailedAttemptWithoutCreatingAGain(int result)
    {
        using var f = new Fixture(); f.Bind();
        f.Pair(0, 0, result: (byte)result);
        var failed = Assert.Single(f.Gatherings);
        Assert.Equal(NetworkGatheringOutcome.Failed, failed.Outcome);
        Assert.Empty(failed.Items);
        f.Send(Completion(0, 1, 0), milliseconds: 200);
        Assert.Single(f.Gatherings);
        f.Send(Witness(), milliseconds: 250);
        Assert.Equal(2, f.Gatherings.Count);
        var success = f.Gatherings[1];
        Assert.Equal(NetworkGatheringOutcome.Harvested, success.Outcome);
        Assert.Equal(1, Assert.Single(success.Items).Quantity);
        Assert.True(success.Sequence > failed.Sequence);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(Item)]
    public void FailedAttemptNeedsNoRewardItemIdentityAndNeverCreatesAnInventoryEntry(int itemId)
    {
        using var f = new Fixture(); f.Bind();
        f.Send(Completion(2, 0, 0, item: itemId), milliseconds: 100);
        f.Send(Witness(opaqueA: 2), milliseconds: 150);
        var failed = Assert.Single(f.Gatherings);
        Assert.Equal(NetworkGatheringOutcome.Failed, failed.Outcome);
        Assert.Empty(failed.Items);
        Assert.Null(f.Source.GatheringWarning);
    }

    [Fact]
    public void UnboundForeignAndMismatchedNoRewardPacketsCannotCreateFailedAttempts()
    {
        using var f = new Fixture();
        f.Pair(0, 0, result: 2);
        Assert.Empty(f.Gatherings);
        f.Bind(milliseconds: 200);
        f.Pair(0, 0, result: 2, actor: OwnEntity + 1, milliseconds: 300);
        f.Send(Completion(2, 0, 0, target: Target + 1), Foreign, 400);
        f.Send(Witness(opaqueA: 2, target: Target + 1), Foreign, 450);
        f.Send(Completion(2, 0, 0, target: Target + 2), milliseconds: 500);
        f.Send(Witness(opaqueA: 0, target: Target + 2), milliseconds: 550);
        Assert.Empty(f.Gatherings);
        f.Pair(0, 0, result: 2, target: Target + 3, milliseconds: 600);
        Assert.Equal(NetworkGatheringOutcome.Failed, Assert.Single(f.Gatherings).Outcome);
    }

    [Theory]
    [InlineData(510116011, 1)]
    [InlineData(510116011, 0)]
    [InlineData(0, 1)]
    public void UnknownAdditionalFieldsNeverTurnIntoAFalseFailure(int item, int count)
    {
        using var f = new Fixture(); f.Bind();
        f.Pair(0, 0, result: 2, additionalItem: item, additionalCount: count);
        Assert.Empty(f.Gatherings);
        Assert.NotNull(f.Source.GatheringWarning);
        f.Pair(0, 0, result: 2, target: Target + 1, milliseconds: 300);
        Assert.Equal(NetworkGatheringOutcome.Failed, Assert.Single(f.Gatherings).Outcome);
    }

    [Fact]
    public void RepeatedCompleteFailedAttemptsAreNotDeduplicatedByTheirIdenticalBodies()
    {
        using var f = new Fixture(); f.Bind();
        f.Pair(0, 0, result: 2);
        f.Pair(0, 0, result: 2, milliseconds: 300);
        Assert.Equal(2, f.Gatherings.Count);
        Assert.All(f.Gatherings, attempt =>
        {
            Assert.Equal(NetworkGatheringOutcome.Failed, attempt.Outcome);
            Assert.Empty(attempt.Items);
        });
        Assert.True(f.Gatherings[1].Sequence > f.Gatherings[0].Sequence);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OwnInstanceContextIsPreservedOnBothLootAndNoLootForSeparateDungeonRecording(bool failed)
    {
        var world = new WorldContextService(InstanceCatalog.FromJson("""
            {"schemaVersion":1,"maps":[
              {"id":101,"names":{"en":"Example field"},"kind":"openWorld","sourceRefs":["synthetic"]},
              {"id":201,"names":{"en":"Example dungeon"},"kind":"instance","sourceRefs":["synthetic"]}
            ],"dungeons":[]}
            """));
        using var f = new Fixture(world);
        f.Bind(mapDataId: 201);
        f.Pair(failed ? 0 : 1, 0, result: failed ? (byte)2 : (byte)0);
        var dungeonAttempt = Assert.Single(f.Gatherings);
        Assert.True(dungeonAttempt.InInstance); // Still available to a dungeon consumer; Lifeskill filters it.
        Assert.Equal(failed ? NetworkGatheringOutcome.Failed : NetworkGatheringOutcome.Harvested, dungeonAttempt.Outcome);
        f.Bind(milliseconds: 300, mapDataId: 101);
        f.Pair(1, 0, milliseconds: 400);
        Assert.False(f.Gatherings[1].InInstance);
    }

    [Fact]
    public void InstanceFlagUsesCompletionContextEvenWhenTheWitnessArrivesAfterRefinementChanges()
    {
        var world = new WorldContextService(InstanceCatalog.FromJson("""
            {"schemaVersion":1,"maps":[
              {"id":201,"names":{"en":"Example dungeon"},"kind":"instance","sourceRefs":["synthetic"]}
            ],"dungeons":[]}
            """));
        using var f = new Fixture(world); f.Bind(mapDataId: 201);
        f.Send(Completion(0, 1, 0), milliseconds: 100);
        world.Invalidate(WorldContextStatus.Stale, f.Source.OwnCharacter!.Generation, "synthetic-gap", At.AddMilliseconds(120));
        f.Send(Witness(), milliseconds: 150);
        Assert.True(Assert.Single(f.Gatherings).InInstance);
    }

    [Theory]
    [InlineData(int.MaxValue, 1)]
    [InlineData(-1, 2)]
    [InlineData(2, -1)]
    public void InvalidOrOverflowingPrimaryAmountsCannotEmitAndDoNotPoisonTheNextPair(int basic, int bonus)
    {
        using var f = new Fixture(); f.Bind();
        var character = f.Source.OwnCharacter;
        f.Pair(basic, bonus);
        Assert.Empty(f.Gatherings);
        Assert.Same(character, f.Source.OwnCharacter);
        f.Pair(1, 0, target: Target + 1, milliseconds: 300);
        Assert.Single(f.Gatherings);
    }

    [Fact]
    public void ForeignActorWitnessCannotAuthorizeACompletionOrReplaceTheBoundCharacter()
    {
        using var f = new Fixture(); f.Bind();
        var character = f.Source.OwnCharacter;
        f.Send(Completion(0, 1, 1), milliseconds: 100);
        f.Send(Witness(actor: OwnEntity + 1), milliseconds: 150);
        Assert.Empty(f.Gatherings);
        Assert.Same(character, f.Source.OwnCharacter);
        f.Pair(1, 0, target: Target + 1, milliseconds: 300);
        Assert.Single(f.Gatherings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompletionAndWitnessMustBelongToTheSameBoundTransport(bool foreignCompletion)
    {
        using var f = new Fixture(); f.Bind();
        var character = f.Source.OwnCharacter;
        f.Send(Completion(0, 1, 0), foreignCompletion ? Foreign : Game, 100);
        f.Send(Witness(), foreignCompletion ? Game : Foreign, 150);
        Assert.Empty(f.Gatherings);
        Assert.Same(character, f.Source.OwnCharacter);
    }

    [Fact]
    public void ACompleteForeignPairCannotCreateOwnAuthorityOrAReward()
    {
        using var f = new Fixture(); f.Bind();
        var character = f.Source.OwnCharacter;
        f.Send(Completion(0, 1, 0), Foreign, 100);
        f.Send(Witness(), Foreign, 150);
        Assert.Empty(f.Gatherings);
        Assert.Same(character, f.Source.OwnCharacter);
    }

    [Fact]
    public void PreBindingMessagesAreNotReplayedWhenOwnIsLaterConfirmed()
    {
        using var f = new Fixture();
        f.Send(Completion(0, 1, 0), milliseconds: 100);
        f.Send(Witness(), milliseconds: 150);
        Assert.Null(f.Source.OwnCharacter);
        f.Bind(milliseconds: 200);
        f.Send(Witness(), milliseconds: 250);
        Assert.Empty(f.Gatherings);
        f.Pair(1, 0, target: Target + 1, milliseconds: 300);
        Assert.Single(f.Gatherings);
    }

    [Theory]
    [InlineData(1, 58)]
    [InlineData(0, 57)]
    public void BothOpaqueWitnessBytesMustMatchTheCompletionContract(int opaqueA, int opaqueB)
    {
        using var f = new Fixture(); f.Bind();
        f.Send(Completion(0, 1, 0), milliseconds: 100);
        f.Send(Witness(opaqueA: (byte)opaqueA, opaqueB: (byte)opaqueB), milliseconds: 150);
        Assert.Empty(f.Gatherings);
    }

    [Fact]
    public void MatchingOpaqueValuesAreComparedWithoutInventingAnEnum()
    {
        using var f = new Fixture(); f.Bind();
        f.Send(Completion(0, 1, 0, unknown: 57), milliseconds: 100);
        f.Send(Witness(opaqueB: 57), milliseconds: 150);
        Assert.Equal(1, Assert.Single(Assert.Single(f.Gatherings).Items).Quantity);
    }

    [Fact]
    public void DifferentTargetKeysCannotPairEvenWhenActorAndOpaqueBytesMatch()
    {
        using var f = new Fixture(); f.Bind();
        f.Send(Completion(0, 1, 0), milliseconds: 100);
        f.Send(Witness(target: Target + 1), milliseconds: 150);
        Assert.Empty(f.Gatherings);
    }

    [Fact]
    public void CompleteIdenticalRewardPairsAreBothCountedWithoutBodyHashDeduplication()
    {
        using var f = new Fixture(); f.Bind();
        f.Pair(1, 1, milliseconds: 100);
        f.Pair(1, 1, milliseconds: 300);
        Assert.Equal(2, f.Gatherings.Count);
        Assert.Equal(4, f.Gatherings.Sum(item => Assert.Single(item.Items).Quantity));
        Assert.True(f.Gatherings[0].Sequence > 0);
        Assert.True(f.Gatherings[1].Sequence > f.Gatherings[0].Sequence);
    }

    [Fact]
    public void ReentrantGatheringPublishesEachSequenceToEverySubscriberInFifoOrder()
    {
        using var f = new Fixture(); f.Bind();
        var nested = false;
        var laterSubscriber = new List<long>();
        f.Network.GatheringCompleted += _ =>
        {
            if (nested) return;
            nested = true;
            f.Pair(1, 0, target: Target + 1, milliseconds: 300);
        };
        f.Network.GatheringCompleted += gathering => laterSubscriber.Add(gathering.Sequence);

        f.Pair(1, 0, milliseconds: 100);

        Assert.True(nested);
        Assert.Equal(2, f.Gatherings.Count);
        Assert.Equal(f.Gatherings.Select(gathering => gathering.Sequence), laterSubscriber);
        Assert.True(laterSubscriber[0] < laterSubscriber[1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EachAcceptedPairConsumesBothMembersExactlyOnce(bool extraWitness)
    {
        using var f = new Fixture(); f.Bind();
        f.Pair(1, 0, milliseconds: 100);
        f.Send(extraWitness ? Witness() : Completion(0, 1, 0), milliseconds: 300);
        Assert.Single(f.Gatherings);
        f.Send(extraWitness ? Completion(0, 1, 0) : Witness(), milliseconds: 350);
        Assert.Equal(2, f.Gatherings.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DuplicatePendingEvidenceIsAmbiguousAndCannotBeSilentlyDeduplicated(bool duplicateWitness)
    {
        using var f = new Fixture(); f.Bind();
        var duplicate = duplicateWitness ? Witness() : Completion(0, 1, 0);
        f.Send(duplicate, milliseconds: 100);
        f.Send(duplicate, milliseconds: 110);
        f.Send(duplicateWitness ? Completion(0, 1, 0) : Witness(), milliseconds: 120);
        Assert.Empty(f.Gatherings);
        Assert.NotNull(f.Source.GatheringWarning);
        Assert.True(f.Source.GatheringWarningVersion > 0);
        f.Pair(1, 0, target: Target + 1, milliseconds: 300);
        Assert.Single(f.Gatherings);
    }

    [Theory]
    [InlineData("reset", false)] [InlineData("reset", true)]
    [InlineData("gap-own", false)] [InlineData("gap-own", true)]
    [InlineData("gap-all", false)] [InlineData("gap-all", true)]
    [InlineData("own-change", false)] [InlineData("own-change", true)]
    [InlineData("own-refresh", false)] [InlineData("own-refresh", true)]
    public void EveryOwnBoundaryDiscardsBothKindsOfPendingEvidence(string boundary, bool witnessFirst)
    {
        using var f = new Fixture(); f.Bind();
        f.Send(witnessFirst ? Witness() : Completion(0, 1, 0), milliseconds: 100);
        var nextActor = OwnEntity;
        switch (boundary)
        {
            case "reset": f.Network.ResetProgressContext(); break;
            case "gap-own": f.Network.ProcessProgressGap(Game); break;
            case "gap-all": f.Network.ProcessProgressGap("*"); break;
            case "own-change": nextActor = OwnEntity + 1; f.Bind(nextActor, 124, 200); break;
            case "own-refresh": f.Bind(milliseconds: 200); break;
        }
        if (f.Source.OwnCharacter is null) f.Bind(milliseconds: 300);
        f.Send(witnessFirst ? Completion(0, 1, 0) : Witness(actor: nextActor), milliseconds: 400);
        Assert.Empty(f.Gatherings);
        f.Pair(1, 0, target: Target + 1, actor: nextActor, milliseconds: 500);
        Assert.Single(f.Gatherings);
    }

    [Fact]
    public void AForeignStreamGapDoesNotDiscardTheBoundOwnPendingPair()
    {
        using var f = new Fixture(); f.Bind();
        var character = f.Source.OwnCharacter;
        f.Send(Completion(0, 1, 0), milliseconds: 100);
        f.Network.ProcessProgressGap(Foreign);
        f.Send(Witness(), milliseconds: 150);
        Assert.Same(character, f.Source.OwnCharacter);
        Assert.Single(f.Gatherings);
    }

    [Fact]
    public void OnlyAnExactlyKeyedCurrentWorldObjectIsAttached()
    {
        using var f = new Fixture(); f.Bind();
        f.Send(Object(Target + 1));
        f.Pair(1, 0);
        Assert.Null(Assert.Single(f.Gatherings).Object);
        f.Send(Object(Target), milliseconds: 300);
        f.Pair(1, 0, milliseconds: 400);
        Assert.Equal(new NetworkGatheringObject(1550020, 77, 9001), f.Gatherings[1].Object);
        f.Bind(milliseconds: 600);
        f.Pair(1, 0, milliseconds: 700);
        Assert.Null(f.Gatherings[2].Object);
    }

    [Theory]
    [InlineData(610660028, 1)]
    [InlineData(610660028, 0)]
    [InlineData(0, 1)]
    public void UnsupportedAdditionalFieldsWarnWithoutSilentlyCountingThePrimary(int additionalItem, int additionalCount)
    {
        using var f = new Fixture(); f.Bind();
        f.Pair(1, 1, additionalItem: additionalItem, additionalCount: additionalCount);
        Assert.Empty(f.Gatherings);
        Assert.NotNull(f.Source.GatheringWarning);
        Assert.True(f.Source.GatheringWarningVersion > 0);
        f.Pair(1, 1, target: Target + 1, milliseconds: 300);
        Assert.Equal(2, Assert.Single(Assert.Single(f.Gatherings).Items).Quantity);
    }

    [Fact]
    public void WarningVersionRemainsMonotonicAcrossContextResetAndRepeatedWarnings()
    {
        using var f = new Fixture(); f.Bind();
        f.Pair(1, 0, additionalItem: 610660028, additionalCount: 1);
        var beforeReset = f.Source.GatheringWarningVersion;
        Assert.True(beforeReset > 0);
        f.Network.ResetProgressContext();
        Assert.True(f.Source.GatheringWarningVersion >= beforeReset);
        f.Bind(milliseconds: 300);
        f.Pair(1, 0, target: Target + 1, milliseconds: 400, additionalItem: 610660028, additionalCount: 1);
        Assert.True(f.Source.GatheringWarningVersion > beforeReset);
        Assert.Empty(f.Gatherings);
    }

    [Fact]
    public void MalformedGatheringDoesNotInvalidateOwnIdentityOrTheIndependentPetSource()
    {
        using var f = new Fixture(); f.Bind();
        var character = f.Source.OwnCharacter;
        f.Send([0x33, 0x8D, 0]);
        f.Send([.. Completion(0, 1, 0), 0]);
        f.Send([0x34, 0x8D, 0]);
        f.Send([.. Witness(), 0]);
        f.Send([0x34, 0x36, 0]);
        f.Send([0x35, 0x36, 0]);
        var souls = new List<NetworkSoul>(); f.Network.SoulGained += souls.Add;
        f.Send(Packet(0x900D, writer => { writer.Write((byte)1); writer.Write(1004); writer.Write(2); }));
        Assert.Same(character, f.Source.OwnCharacter);
        Assert.Equal(2, Assert.Single(souls).Souls);
        Assert.Empty(f.Gatherings);
        Assert.True(f.Source.GatheringVerified);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "soulcrest-gather-source-" + Guid.NewGuid().ToString("N"));
        internal ProgressService Progress { get; }
        internal NetworkLootService Network { get; }
        internal INetworkGatheringSource Source => Network;
        internal List<NetworkGathering> Gatherings { get; } = [];
        internal Fixture(WorldContextService? world = null)
        {
            Directory.CreateDirectory(_directory);
            Progress = new ProgressService(Path.Combine(_directory, "progress.json"), new PetCatalog([
                new PetDefinition { Id = "synthetic-pet", En = "Synthetic Pet", Genus = "Fera", NumericId = "1004" }]));
            Network = new(Progress, worldContext: world);
            typeof(NetworkLootService).GetProperty(nameof(NetworkLootService.Running))!.SetValue(Network, true);
            Network.GatheringCompleted += Gatherings.Add;
        }
        internal void Bind(int entity = OwnEntity, long character = 123, long milliseconds = 0, int? mapDataId = null) =>
            Network.ObserveIdentity(Game, new(character, 456, entity, "SyntheticOwn"), At.AddMilliseconds(milliseconds), mapDataId);
        internal void Send(byte[] message, string stream = Game, long milliseconds = 0) =>
            Network.ProcessProgressMessage(stream, message, At.AddMilliseconds(milliseconds));
        internal void Pair(int basic, int bonus, int target = Target, long milliseconds = 100,
            bool witnessFirst = false, byte result = 0, int actor = OwnEntity,
            int additionalItem = 0, int additionalCount = 0)
        {
            var completion = Completion(result, basic, bonus, target, additionalItem, additionalCount);
            var witness = Witness(actor, result, target: target);
            Send(witnessFirst ? witness : completion, milliseconds: milliseconds);
            Send(witnessFirst ? completion : witness, milliseconds: milliseconds + 50);
        }
        public void Dispose() { Network.Dispose(); Directory.Delete(_directory, recursive: true); }
    }

    private static byte[] Completion(byte result, int basic, int bonus, int target = Target,
        int additionalItem = 0, int additionalCount = 0, byte unknown = 58, int item = Item) => Packet(0x8D33, writer =>
    {
        writer.Write(result); writer.Write(unknown); V(writer, target);
        writer.Write(item); writer.Write(basic); writer.Write(bonus); writer.Write(additionalItem); writer.Write(additionalCount);
    });
    private static byte[] Witness(int actor = OwnEntity, byte opaqueA = 0, byte opaqueB = 58, int target = Target) => Packet(0x8D34, writer =>
    {
        V(writer, actor); writer.Write(opaqueA); writer.Write(opaqueB); V(writer, target);
    });
    private static byte[] Object(int target) => Packet(0x3634, writer =>
    {
        V(writer, target); writer.Write((byte)4); writer.Write((byte)0); writer.Write(1550020);
        writer.Write(new byte[12]); writer.Write(77); writer.Write(new byte[4 + 1]); writer.Write((byte)1);
        writer.Write(new byte[1 + 8 + 4]); writer.Write(0); writer.Write(9001);
        writer.Write((byte)0); writer.Write((byte)0); V(writer, 0);
    });
    private static void V(BinaryWriter writer, int value)
    {
        var unsigned = unchecked((uint)value);
        while (unsigned >= 128) { writer.Write((byte)(unsigned | 128)); unsigned >>= 7; }
        writer.Write((byte)unsigned);
    }
    private static byte[] Packet(ushort opcode, Action<BinaryWriter> write)
    {
        using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes);
        writer.Write(opcode); write(writer); return bytes.ToArray();
    }
}
