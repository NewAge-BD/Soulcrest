using System.Reflection;
using System.Text;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Instances;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class NetworkAbyssCombatSourceTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private const string Stream = "synthetic-combat";

    [Theory]
    [InlineData(true, false, false, NetworkAbyssCombatKind.Kill)]
    [InlineData(false, true, false, NetworkAbyssCombatKind.Death)]
    [InlineData(false, false, true, NetworkAbyssCombatKind.Assist)]
    public void CapabilitiesAreIndependentAndUnknownKindsNeverQualify(bool kill, bool death, bool assist,
        NetworkAbyssCombatKind expected)
    {
        var capabilities = new NetworkAbyssCombatCapabilities(kill, death, assist);
        foreach (var kind in Enum.GetValues<NetworkAbyssCombatKind>())
            Assert.Equal(kind == expected, capabilities.Supports(kind));
        Assert.False(capabilities.Supports((NetworkAbyssCombatKind)(-1)));
        Assert.False(capabilities.Supports((NetworkAbyssCombatKind)99));
    }

    [Fact]
    public void CompleteOwnVictimAndPcCasterEmitExactlyOneRamDeathWithoutRankAttribution()
    {
        using var f = new Fixture(); f.Send(Own());
        var own = f.Network.OwnCharacter; var before = f.Network.AbyssSnapshot;
        f.Send(Death(), 1);
        var observation = Assert.Single(f.Events);
        Assert.Equal(NetworkAbyssCombatKind.Death, observation.Kind); Assert.Equal(At.AddSeconds(1), observation.At);
        Assert.Same(own, observation.Character); Assert.True(observation.Sequence > 0);
        Assert.Equal("SyntheticOpponent", observation.OpponentName); Assert.Null(observation.RankPointDelta);
        Assert.Null(observation.OpponentClassId); Assert.Null(observation.OpponentAbyssRankGrade);
        Assert.Equal(before, f.Network.AbyssSnapshot); AssertQualifiedCapabilities(f);
        f.Send(Death(), 2); f.Send(Death(caster: 401), 3);
        f.Send(Own(), 4); f.Send(Death(), 5);
        Assert.Single(f.Events); // a repeated full Own is not proof of a new life
    }

    [Theory]
    [InlineData(400U, 300U)] // own killer still has no independently qualified victim-player proof
    [InlineData(400U, 500U)] // nearby death never proves own assist
    public void OtherVictimsAndRankChangesCannotBecomeOwnKillAssistOrDeath(uint victim, uint caster)
    {
        using var f = new Fixture(); f.Send(Own());
        f.Send(Death(victim, caster), 1); f.Send(Rank(1_200), 2); f.Send(Rank(1_034), 3);
        Assert.Equal(1_034, f.Network.AbyssSnapshot.RankPoints); Assert.Empty(f.Events);
        AssertQualifiedCapabilities(f);
        f.Send(Death(), 4); Assert.Single(f.Events);
    }

    [Fact]
    public void ForeignStreamAndUnsupportedFormsDoNotConsumeOwnEntity()
    {
        using var f = new Fixture(); f.Send(Own());
        f.Send(Death(), 1, "synthetic-foreign");
        f.Send(Death(reason: 2), 2);
        f.Send([.. Death(), 0], 3);
        Assert.Empty(f.Events); Assert.NotNull(f.Network.OwnCharacter);
        f.Send(Death(), 4); Assert.Single(f.Events);
    }

    [Fact]
    public void NoIdentityStoppedTrackingUnknownWorldAndOutsideAbyssCannotPublish()
    {
        using var f = new Fixture();
        f.Send(Death()); Assert.Empty(f.Events);
        f.Send(Own(map: 21), 1); f.Send(Death(), 2); Assert.Empty(f.Events);
        f.Send(Own(map: 99), 3); f.Send(Death(), 4); Assert.Empty(f.Events);
        f.Send(Own(), 5); f.Send(Death(), 6); Assert.Single(f.Events);
        f.Network.Stop(); f.Send(Own(entity: 301), 7); f.Send(Death(victim: 301), 8);
        Assert.Single(f.Events);
    }

    [Fact]
    public void DefaultFutureAndPreOwnTimesDoNotConsumeOwnEntity()
    {
        using var f = new Fixture(); f.Send(Own(), 2);
        f.Network.ProcessProgressMessage(Stream, Death(), default);
        f.Network.ProcessProgressMessage(Stream, Death(), DateTimeOffset.UtcNow.AddMinutes(1));
        f.Send(Death(), 1); Assert.Empty(f.Events);
        f.Send(Death(), 3); Assert.Single(f.Events);
    }

    [Fact]
    public void OwnAttributionThroughAnotherEntityIsNotAnOpponent()
    {
        using var f = new Fixture(); f.Send(Own());
        f.Send(Death(server: 501, name: "SyntheticCombat"), 1); Assert.Empty(f.Events);
        f.Send(Death(server: 502, name: "SyntheticCombat"), 2); Assert.Single(f.Events);
    }

    [Fact]
    public void GapAndRepeatedOwnKeepSeenKeysButActualCaptureChangeAllowsANewObservation()
    {
        using var f = new Fixture(); f.Send(Own()); f.Send(Death(), 1);
        f.Network.ProcessProgressGap(Stream, capturedAtUtc: At.AddSeconds(2));
        f.Send(Own(), 3); f.Send(Death(), 4); Assert.Single(f.Events);
        f.Send(Own(entity: 301), 5); f.Send(Death(victim: 301), 6); Assert.Equal(2, f.Events.Count);
        f.ChangeCapture(7); f.Send(Own(), 8); f.Send(Death(), 9);
        Assert.Equal(3, f.Events.Count); Assert.True(f.Events[2].Sequence > f.Events[1].Sequence);
    }

    [Fact]
    public void NestedRepeatIsConsumedBeforeAnySubscriberAndDoesNotDuplicateTheEvent()
    {
        using var f = new Fixture(); f.Send(Own()); var firstCalls = 0; var secondCalls = 0;
        f.Network.CombatEvent += _ => { firstCalls++; f.Send(Death(), 2); };
        f.Network.CombatEvent += _ => secondCalls++;
        f.Send(Death(), 1);
        Assert.Equal(1, firstCalls); Assert.Equal(1, secondCalls); Assert.Single(f.Events);
    }

    [Theory]
    [InlineData("stop")] [InlineData("gap")] [InlineData("own")] [InlineData("capture")] [InlineData("world")]
    public void ReentrantBoundaryStopsRemainingSubscribersOfTheOldOwn(string boundary)
    {
        using var f = new Fixture(); f.Send(Own()); var lastCalls = 0;
        f.Network.CombatEvent += _ =>
        {
            switch (boundary)
            {
                case "stop": f.Network.Stop(); break;
                case "gap": f.Network.ProcessProgressGap(Stream, capturedAtUtc: At.AddSeconds(2)); break;
                case "own": f.Send(Own(entity: 301), 2); break;
                case "capture": f.ChangeCapture(2); break;
                case "world": f.World.Invalidate(WorldContextStatus.Transitioning,
                    f.Network.OwnCharacter!.Generation, "synthetic-transition", At.AddSeconds(2)); break;
            }
        };
        f.Network.CombatEvent += _ => lastCalls++;
        f.Send(Death(), 1);
        Assert.Single(f.Events); Assert.Equal(0, lastCalls);
    }

    [Fact]
    public void ThrowingSubscriberCannotLeakOpponentNameToLogOrStopTheNextSubscriber()
    {
        using var f = new Fixture(); f.Send(Own()); var calls = 0;
        var secret = "SyntheticOpponent" + Guid.NewGuid().ToString("N");
        f.Network.CombatEvent += _ => throw new InvalidOperationException(secret);
        f.Network.CombatEvent += _ => calls++;
        f.Send(Death(name: secret), 1);
        Assert.Single(f.Events); Assert.Equal(1, calls);
        var log = Path.Combine(AppPaths.LogsDirectory, "errors.log");
        Assert.DoesNotContain(secret, File.ReadAllText(log));
    }

    [Fact]
    public void SeenKeyBudgetIsBoundedWithoutEvictionAndOnlyNewCaptureClearsOverflow()
    {
        using var f = new Fixture();
        for (var index = 0; index < AbyssOwnDeathLife.MaximumSeenEntities; index++)
        {
            var entity = 1_000 + index;
            f.Send(Own(entity: entity), index * 2); f.Send(Death(victim: (uint)entity), index * 2 + 1);
        }
        Assert.Equal(AbyssOwnDeathLife.MaximumSeenEntities, f.Events.Count); AssertQualifiedCapabilities(f);
        f.Send(Own(entity: 9_000), 600); f.Send(Death(victim: 9_000), 601);
        Assert.False(f.Network.CombatCapabilities.DeathQualified);
        f.Send(Own(entity: 1_000), 602); f.Send(Death(victim: 1_000), 603);
        Assert.Equal(AbyssOwnDeathLife.MaximumSeenEntities, f.Events.Count);
        f.ChangeCapture(604); f.Send(Own(entity: 1_000), 605); f.Send(Death(victim: 1_000), 606);
        Assert.Equal(AbyssOwnDeathLife.MaximumSeenEntities + 1, f.Events.Count); AssertQualifiedCapabilities(f);
    }

    [Fact]
    public void FullPlayerAppearanceOnTheOwnTransportFeedsThePureJoinWithoutPublishingIt()
    {
        using var f = new Fixture(); var stream = f.InstallTransport(); f.Send(Own(), stream: stream);
        var appearance = Appearance(); Assert.NotNull(AbyssPlayerAppearanceMessage.TryParse(appearance));
        f.Send(appearance, 1, stream);
        Assert.Equal(1, f.Registry.Count);
        Assert.True(f.Registry.TryConsumeOwnKill(f.RegistryContext(stream), new(400, 300, 501, "SyntheticCombat"),
            At.AddSeconds(2), At.AddHours(1), out var opponent));
        Assert.Equal("SyntheticVictim", opponent); Assert.Empty(f.Events); AssertQualifiedCapabilities(f);
    }

    [Fact]
    public void DispatcherEmitsOnlyOneKillForCompletePcVictimAndExplicitOwnCaster()
    {
        using var f = new Fixture(); var stream = f.InstallTransport(); f.Send(Own(), stream: stream);
        f.Send(Appearance(), 1, stream);
        f.Send(Death(victim: 400, caster: 300, server: 501, name: "SyntheticCombat"), 2, stream);
        var kill = Assert.Single(f.Events); Assert.Equal(NetworkAbyssCombatKind.Kill, kill.Kind);
        Assert.Equal("SyntheticVictim", kill.OpponentName); Assert.Null(kill.RankPointDelta);
        Assert.Same(f.Network.OwnCharacter, kill.Character); Assert.Equal(At.AddSeconds(2), kill.At);
        AssertQualifiedCapabilities(f);
        Assert.False(f.Registry.TryConsumeOwnKill(f.RegistryContext(stream), new(400, 300, 501, "SyntheticCombat"),
            At.AddSeconds(3), At.AddHours(1), out _)); // the consumed candidate cannot be replayed/rearmed
        f.Send(Appearance(), 4, stream);
        f.Send(Death(victim: 400, caster: 900), 5, stream);
        f.Send(Rank(1_200), 6, stream); Assert.Single(f.Events);
        f.Send(Death(), 7, stream); Assert.Equal(2, f.Events.Count); // independently qualified Death remains usable
        Assert.Equal(NetworkAbyssCombatKind.Death, f.Events[1].Kind);
    }

    [Fact]
    public void DirectMessageDispatchWithoutAnOwnTransportCannotPopulatePlayerEvidence()
    {
        using var f = new Fixture(); f.Send(Own()); f.Send(Appearance(), 1);
        Assert.Equal(0, f.Registry.Count);
        var stream = f.InstallTransport();
        f.Send(Appearance(), 2, stream); Assert.Equal(0, f.Registry.Count);
        f.Send(Own(), 3, stream); f.Send(Appearance(), 4, stream); Assert.Equal(1, f.Registry.Count);
    }

    [Fact]
    public void ATransportAddedAfterOwnCannotAuthorizeAnAppearanceUntilAnotherCompleteOwn()
    {
        using var f = new Fixture(); var stream = Fixture.TransportStream;
        f.Send(Own(), stream: stream); f.InstallTransport();
        f.Send(Appearance(), 1, stream); Assert.Equal(0, f.Registry.Count);
        f.Send(Own(), 2, stream); f.Send(Appearance(), 3, stream); Assert.Equal(1, f.Registry.Count);
    }

    [Fact]
    public void ReplacedTransportAndNewOwnCannotRearmPreviouslyObservedActors()
    {
        using var f = new Fixture(); var stream = f.InstallTransport(); f.Send(Own(), stream: stream);
        f.Send(Appearance(), 1, stream); f.InstallTransport();
        f.Send(Appearance(entity: 401), 2, stream); Assert.Equal(1, f.Registry.Count);
        f.Send(Own(), 3, stream); f.Send(Appearance(), 4, stream);
        Assert.False(f.Registry.TryConsumeOwnKill(f.RegistryContext(stream), new(400, 300, 501, "SyntheticCombat"),
            At.AddSeconds(5), At.AddHours(1), out _));
        f.Send(Appearance(entity: 401), 6, stream);
        Assert.True(f.Registry.TryConsumeOwnKill(f.RegistryContext(stream), new(401, 300, 501, "SyntheticCombat"),
            At.AddSeconds(7), At.AddHours(1), out _));
        Assert.Empty(f.Events);
    }

    [Fact]
    public void UnsupportedAppearanceTombstonesKnownPlayersButForeignStreamCannotChangeThem()
    {
        using var f = new Fixture(); var stream = f.InstallTransport(); f.Send(Own(), stream: stream);
        f.Send(Appearance(), 1, stream); f.Send([0x45, 0x36], 2, "synthetic-foreign");
        Assert.True(f.Registry.TryConsumeOwnKill(f.RegistryContext(stream), new(400, 300, 501, "SyntheticCombat"),
            At.AddSeconds(3), At.AddHours(1), out _));
        f.Send(Appearance(entity: 401), 4, stream); f.Send([.. Appearance(entity: 402), 0], 5, stream);
        f.Send(Appearance(entity: 401), 6, stream);
        Assert.False(f.Registry.TryConsumeOwnKill(f.RegistryContext(stream), new(401, 300, 501, "SyntheticCombat"),
            At.AddSeconds(7), At.AddHours(1), out _));
        Assert.Empty(f.Events); AssertQualifiedCapabilities(f);
    }

    [Theory]
    [InlineData((ushort)0x3641)] [InlineData((ushort)0x3642)] [InlineData((ushort)0x3647)]
    public void QualifiedLifecyclePrefixesOnlyRevokeTheTargetAndNeverCreateOrRearmPlayerEvidence(ushort opcode)
    {
        using var f = new Fixture(); var stream = f.InstallTransport(); f.Send(Own(), stream: stream);
        f.Send(Appearance(), 1, stream); f.Send(Appearance(entity: 401), 2, stream);
        f.Send(Revocation(opcode, 400), 3, stream); f.Send(Appearance(), 4, stream);
        Assert.False(f.Registry.TryConsumeOwnKill(f.RegistryContext(stream), new(400, 300, 501, "SyntheticCombat"),
            At.AddSeconds(5), At.AddHours(1), out _));
        Assert.True(f.Registry.TryConsumeOwnKill(f.RegistryContext(stream), new(401, 300, 501, "SyntheticCombat"),
            At.AddSeconds(5), At.AddHours(1), out _));
        f.Send(Revocation(opcode, 402), 6, stream); f.Send(Appearance(entity: 402), 7, stream);
        Assert.False(f.Registry.TryConsumeOwnKill(f.RegistryContext(stream), new(402, 300, 501, "SyntheticCombat"),
            At.AddSeconds(8), At.AddHours(1), out _));
        Assert.Equal(3, f.Registry.Count); Assert.Empty(f.Events); AssertQualifiedCapabilities(f);
    }

    [Theory]
    [InlineData((ushort)0x3641)] [InlineData((ushort)0x3642)] [InlineData((ushort)0x3647)]
    public void MalformedOrUnsupportedLifecycleFormsRevokeAllKnownPlayers(ushort opcode)
    {
        using var f = new Fixture(); var stream = f.InstallTransport(); f.Send(Own(), stream: stream);
        f.Send(Appearance(), 1, stream); f.Send(Appearance(entity: 401), 2, stream);
        // The CharDisappear key alone does not qualify a missing/unknown child-byte form.
        f.Send(Packet(opcode, writer => writer.Write((byte)0x80)), 3, stream);
        f.Send(Appearance(), 4, stream);
        foreach (var entity in new[] { 400, 401 })
            Assert.False(f.Registry.TryConsumeOwnKill(f.RegistryContext(stream), new(entity, 300, 501, "SyntheticCombat"),
                At.AddSeconds(5), At.AddHours(1), out _));
        Assert.Empty(f.Events); f.Send(Death(), 6, stream); Assert.Single(f.Events);
    }

    [Fact]
    public void ForeignLifecycleHasNoAuthorityAndMissingTransportCanOnlyInvalidateEvidence()
    {
        using var f = new Fixture(); var stream = f.InstallTransport(); f.Send(Own(), stream: stream);
        f.Send(Appearance(), 1, stream); f.Send(Revocation(0x3641, 400), 2, "synthetic-foreign");
        Assert.True(f.Registry.TryConsumeOwnKill(f.RegistryContext(stream), new(400, 300, 501, "SyntheticCombat"),
            At.AddSeconds(3), At.AddHours(1), out _));
        f.Send(Appearance(entity: 401), 4, stream); f.InstallTransport(); f.Send(Revocation(0x3642, 999), 5, stream);
        f.Send(Own(), 6, stream); f.Send(Appearance(entity: 401), 7, stream);
        Assert.False(f.Registry.TryConsumeOwnKill(f.RegistryContext(stream), new(401, 300, 501, "SyntheticCombat"),
            At.AddSeconds(8), At.AddHours(1), out _));
        Assert.Empty(f.Events);
    }

    [Fact]
    public void RepeatedDeathAppearanceAndOwnCannotRearmAnAlreadyPublishedKill()
    {
        using var f = new Fixture(); var stream = f.InstallTransport(); f.Send(Own(), stream: stream);
        f.Send(Appearance(), 1, stream); f.Send(OwnKill(), 2, stream);
        f.Send(OwnKill(), 3, stream); f.Send(Appearance(), 4, stream); f.Send(OwnKill(), 5, stream);
        f.Send(Own(), 6, stream); f.Send(Appearance(), 7, stream); f.Send(OwnKill(), 8, stream);
        Assert.Equal(NetworkAbyssCombatKind.Kill, Assert.Single(f.Events).Kind);
    }

    [Fact]
    public void KillFreezesTheLatestStrictlyFreshCompletePlayerClassAndNativeGrade()
    {
        using var f = new Fixture(); var stream = f.InstallTransport(); f.Send(Own(), stream: stream);
        f.Send(Appearance(template: 30, grade: 19), 1, stream);
        f.Send(Appearance(template: 9, grade: 13), 3, stream);
        f.Send(Appearance(template: 25, grade: 1), 2, stream); // stale fact cannot overwrite newer metadata
        f.Send(OwnKill(), 4, stream);
        var kill = Assert.Single(f.Events);
        Assert.Equal("SyntheticVictim", kill.OpponentName);
        Assert.Equal("Templar", kill.OpponentClassId); Assert.Equal(13, kill.OpponentAbyssRankGrade);
        Assert.Null(kill.RankPointDelta); Assert.Equal(nameof(NetworkAbyssCombatEvent), kill.ToString());
    }

    [Fact]
    public void NewUnknownWireMetadataClearsOldValuesAndDoesNotSuppressAQualifiedKill()
    {
        using var f = new Fixture(); var stream = f.InstallTransport(); f.Send(Own(), stream: stream);
        f.Send(Appearance(template: 30, grade: 19), 1, stream);
        f.Send(Appearance(template: 40, grade: 999), 2, stream);
        f.Send(OwnKill(), 3, stream);
        var kill = Assert.Single(f.Events);
        Assert.Equal(NetworkAbyssCombatKind.Kill, kill.Kind);
        Assert.Null(kill.OpponentClassId); Assert.Null(kill.OpponentAbyssRankGrade);
    }

    [Fact]
    public void LaterMetadataCannotBeBorrowedByAnOlderKillOrRearmItsConsumedVictim()
    {
        using var f = new Fixture(); var stream = f.InstallTransport(); f.Send(Own(), stream: stream);
        f.Send(Appearance(template: 30, grade: 19), 1, stream);
        f.Send(Appearance(template: 9, grade: 13), 5, stream);
        f.Send(OwnKill(), 4, stream);
        var kill = Assert.Single(f.Events);
        Assert.Null(kill.OpponentClassId); Assert.Null(kill.OpponentAbyssRankGrade);
        f.Send(Appearance(template: 25, grade: 1), 6, stream); f.Send(OwnKill(), 7, stream);
        Assert.Single(f.Events);
    }

    [Fact]
    public void ReentrantAppearancesCannotChangeTheAlreadyFrozenPublicationForLaterSubscribers()
    {
        using var f = new Fixture(); var stream = f.InstallTransport(); f.Send(Own(), stream: stream);
        f.Send(Appearance(template: 30, grade: 19), 1, stream);
        NetworkAbyssCombatEvent? late = null;
        f.Network.CombatEvent += _ => f.Send(Appearance(template: 9, grade: 13), 3, stream);
        f.Network.CombatEvent += observation => late = observation;
        f.Send(OwnKill(), 2, stream);
        var kill = Assert.Single(f.Events);
        Assert.Same(kill, late); Assert.Equal("Cleric", late!.OpponentClassId);
        Assert.Equal(19, late.OpponentAbyssRankGrade);
    }

    [Fact]
    public void ForeignOrMalformedMetadataCannotDecorateAnotherQualifiedVictim()
    {
        using var f = new Fixture(); var stream = f.InstallTransport(); f.Send(Own(), stream: stream);
        f.Send(Appearance(), 1, stream);
        f.Send(Appearance(template: 30, grade: 19), 2, "synthetic-foreign");
        f.Send(OwnKill(), 3, stream);
        var kill = Assert.Single(f.Events);
        Assert.Null(kill.OpponentClassId); Assert.Null(kill.OpponentAbyssRankGrade);
        f.Send(Appearance(entity: 401), 4, stream);
        f.Send([.. Appearance(entity: 401, template: 30, grade: 19), 0], 5, stream);
        f.Send(OwnKill(401), 6, stream); Assert.Single(f.Events);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CompleteNeutralDeathRevokesOnlyItsVictimAndCannotBecomeACombatResult(bool removeCaster)
    {
        using var f = new Fixture(); var stream = f.InstallTransport(); f.Send(Own(), stream: stream);
        f.Send(Appearance(template: 25, grade: 13), 1, stream);
        f.Send(NeutralDeath(removeCaster ? 400u : 999u), 2, stream);
        Assert.Empty(f.Events);
        if (removeCaster) f.Send(Appearance(template: 25, grade: 13), 3, stream);
        f.Send(Death(name: "SyntheticVictim"), 4, stream);
        var death = Assert.Single(f.Events);
        Assert.Equal(NetworkAbyssCombatKind.Death, death.Kind);
        Assert.Equal(removeCaster ? null : "Sorcerer", death.OpponentClassId);
        Assert.Equal(removeCaster ? (int?)null : 13, death.OpponentAbyssRankGrade);
        Assert.Null(death.RankPointDelta); Assert.Null(death.AbyssPointGain);
    }

    [Theory]
    [InlineData("skill")] [InlineData("reason")] [InlineData("tail")]
    public void UnqualifiedNeutralDeathVariantsStillUseTheGlobalFallback(string variant)
    {
        using var f = new Fixture(); var stream = f.InstallTransport(); f.Send(Own(), stream: stream);
        f.Send(Appearance(template: 25, grade: 13), 1, stream);
        var packet = NeutralDeath(999, skill: variant == "skill" ? 1 : 0, reason: variant == "reason" ? (byte)1 : (byte)0);
        if (variant == "tail") packet = [.. packet, 0];
        f.Send(packet, 2, stream); f.Send(Death(name: "SyntheticVictim"), 3, stream);
        var death = Assert.Single(f.Events);
        Assert.Null(death.OpponentClassId); Assert.Null(death.OpponentAbyssRankGrade);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void UnrelatedCharDisappearKeepsTheConfirmedKillerButRemovingTheCasterCannotBeRearmed(bool removeCaster)
    {
        using var f = new Fixture(); var stream = f.InstallTransport(); f.Send(Own(), stream: stream);
        f.Send(Appearance(template: 25, grade: 13), 1, stream);
        for (uint entity = 900; entity < 912; entity++) f.Send(Revocation(0x3647, entity), 2, stream);
        if (removeCaster)
        {
            f.Send(Revocation(0x3647, 400), 3, stream);
            f.Send(Appearance(template: 25, grade: 13), 4, stream); // appearance cannot revive a consumed slot
        }
        f.Send(Death(name: "SyntheticVictim"), 5, stream);
        var death = Assert.Single(f.Events);
        Assert.Equal(NetworkAbyssCombatKind.Death, death.Kind);
        Assert.Equal(removeCaster ? null : "Sorcerer", death.OpponentClassId);
        Assert.Equal(removeCaster ? (int?)null : 13, death.OpponentAbyssRankGrade);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void TruncatedOrUnsupportedCharDisappearStillRemovesEveryKillerMetadataFact(bool unsupportedPair)
    {
        using var f = new Fixture(); var stream = f.InstallTransport(); f.Send(Own(), stream: stream);
        f.Send(Appearance(template: 25, grade: 13), 1, stream);
        var invalid = Packet(0x3647, writer =>
        {
            Var(writer, 999); writer.Write((byte)0xFF);
            if (unsupportedPair) writer.Write((byte)0xFF);
        });
        f.Send(invalid, 2, stream); f.Send(Death(name: "SyntheticVictim"), 3, stream);
        var death = Assert.Single(f.Events);
        Assert.Null(death.OpponentClassId); Assert.Null(death.OpponentAbyssRankGrade);
    }

    [Fact]
    public void OwnDeathFreezesTheExactPreviouslyConfirmedPcCasterClassAndNativeRank()
    {
        using var f = new Fixture(); var stream = f.InstallTransport(); f.Send(Own(), stream: stream);
        f.Send(Appearance(template: 13, grade: 19), 1, stream);
        NetworkAbyssCombatEvent? late = null;
        f.Network.CombatEvent += _ => f.Send(Appearance(template: 30, grade: 13), 3, stream);
        f.Network.CombatEvent += observation => late = observation;
        f.Send(Death(name: "SyntheticVictim"), 2, stream);
        var death = Assert.Single(f.Events);
        Assert.Equal(NetworkAbyssCombatKind.Death, death.Kind); Assert.Same(death, late);
        Assert.Equal("SyntheticVictim", death.OpponentName);
        Assert.Equal("Ranger", death.OpponentClassId); Assert.Equal(19, death.OpponentAbyssRankGrade);
        Assert.Null(death.RankPointDelta);
    }

    [Theory]
    [InlineData("entity")] [InlineData("name")] [InlineData("server")] [InlineData("before-appearance")]
    [InlineData("later-metadata")] [InlineData("revoked")]
    public void QualifiedOwnDeathStillPublishesWithoutUnmatchedFutureOrRevokedCasterMetadata(string invalid)
    {
        using var f = new Fixture(); var stream = f.InstallTransport(); f.Send(Own(), stream: stream);
        f.Send(Appearance(template: 13, grade: 19), invalid == "before-appearance" ? 5 : 1, stream);
        if (invalid == "later-metadata") f.Send(Appearance(template: 30, grade: 13), 5, stream);
        if (invalid == "revoked") f.Send(Revocation(0x3641, 400), 2, stream);
        var packet = invalid switch
        {
            "entity" => Death(caster: 401, name: "SyntheticVictim"),
            "name" => Death(name: "SyntheticOther"),
            "server" => Death(server: 503, name: "SyntheticVictim"),
            _ => Death(name: "SyntheticVictim")
        };
        f.Send(packet, 4, stream);
        var death = Assert.Single(f.Events);
        Assert.Equal(NetworkAbyssCombatKind.Death, death.Kind);
        Assert.Null(death.OpponentClassId); Assert.Null(death.OpponentAbyssRankGrade);
    }

    [Fact]
    public void OwnDeathMetadataLookupDoesNotConsumeTheCasterOrRearmItsTombstone()
    {
        using var f = new Fixture(); var stream = f.InstallTransport(); f.Send(Own(), stream: stream);
        f.Send(Appearance(template: 13, grade: 19), 1, stream);
        f.Send(Death(name: "SyntheticVictim"), 2, stream);
        Assert.Equal("Ranger", Assert.Single(f.Events).OpponentClassId);
        // The metadata read itself has not consumed the caster's registry slot.
        Assert.True(f.Registry.TryConsumeOwnKill(f.RegistryContext(stream),
            new(400, 300, 501, "SyntheticCombat"), At.AddSeconds(3), At.AddHours(1), out _));
        f.Send(Appearance(template: 30, grade: 13), 4, stream);
        Assert.False(f.Registry.TryConsumeOwnKill(f.RegistryContext(stream),
            new(400, 300, 501, "SyntheticCombat"), At.AddSeconds(5), At.AddHours(1), out _));
        Assert.Single(f.Events);
    }

    [Theory]
    [InlineData("unknown-victim")] [InlineData("npc-victim")] [InlineData("foreign-caster")]
    [InlineData("name-mismatch")] [InlineData("server-mismatch")] [InlineData("transport")]
    [InlineData("foreign-stream")] [InlineData("before-appearance")] [InlineData("future")]
    public void UnqualifiedVictimCasterTimeOrTransportCannotPublishAKillOrAssist(string invalid)
    {
        using var f = new Fixture(); var stream = f.InstallTransport(); f.Send(Own(), stream: stream);
        if (invalid != "unknown-victim") f.Send(Appearance(), 1, stream);
        if (invalid == "npc-victim") f.Send(Revocation(0x3641, 400), 2, stream);
        if (invalid == "transport") f.InstallTransport();
        var death = invalid switch
        {
            "foreign-caster" => Death(victim: 400, caster: 900, server: 501, name: "SyntheticCombat"),
            "name-mismatch" => Death(victim: 400, caster: 300, server: 501, name: "SyntheticOther"),
            "server-mismatch" => Death(victim: 400, caster: 300, server: 502, name: "SyntheticCombat"),
            _ => OwnKill()
        };
        if (invalid == "future") f.Network.ProcessProgressMessage(stream, death, DateTimeOffset.UtcNow.AddMinutes(1));
        else f.Send(death, invalid == "before-appearance" ? 0 : 3, invalid == "foreign-stream" ? "synthetic-foreign" : stream);
        Assert.Empty(f.Events); AssertQualifiedCapabilities(f);
    }

    [Fact]
    public void NestedIdenticalKillIsConsumedBeforeSubscribersAndCannotDuplicate()
    {
        using var f = new Fixture(); var stream = f.InstallTransport(); f.Send(Own(), stream: stream);
        f.Send(Appearance(), 1, stream); var firstCalls = 0; var lastCalls = 0;
        f.Network.CombatEvent += _ => { firstCalls++; f.Send(OwnKill(), 3, stream); };
        f.Network.CombatEvent += _ => lastCalls++;
        f.Send(OwnKill(), 2, stream);
        Assert.Single(f.Events); Assert.Equal(1, firstCalls); Assert.Equal(1, lastCalls);
    }

    [Fact]
    public void DifferentNestedKillsReachEverySubscriberInStrictSourceSequence()
    {
        using var f = new Fixture(); var stream = f.InstallTransport(); f.Send(Own(), stream: stream);
        f.Send(Appearance(), 1, stream); f.Send(Appearance(entity: 401), 1, stream);
        var nested = false; var late = new List<long>();
        f.Network.CombatEvent += _ =>
        {
            if (nested) return; nested = true; f.Send(OwnKill(401), 3, stream);
        };
        f.Network.CombatEvent += observation => late.Add(observation.Sequence);
        f.Send(OwnKill(), 2, stream);
        Assert.Equal(2, f.Events.Count); Assert.Equal(f.Events.Select(row => row.Sequence), late);
        Assert.True(late[1] > late[0]); Assert.All(f.Events, row => Assert.Null(row.RankPointDelta));
    }

    [Theory]
    [InlineData("stop")] [InlineData("gap")] [InlineData("own")]
    [InlineData("capture")] [InlineData("world")] [InlineData("transport")]
    public void ReentrantBoundaryRejectsRemainingOldSubscribersAndAlreadyQueuedKills(string boundary)
    {
        using var f = new Fixture(); var stream = f.InstallTransport(); f.Send(Own(), stream: stream);
        f.Send(Appearance(), 1, stream); f.Send(Appearance(entity: 401), 1, stream); var lateCalls = 0;
        f.Network.CombatEvent += _ =>
        {
            f.Send(OwnKill(401), 3, stream); // queued behind the current publication
            switch (boundary)
            {
                case "stop": f.Network.Stop(); break;
                case "gap": f.Network.ProcessProgressGap(stream, capturedAtUtc: At.AddSeconds(4)); break;
                case "own": f.Send(Own(entity: 301), 4, stream); break;
                case "capture": f.ChangeCapture(4); break;
                case "world": f.World.Invalidate(WorldContextStatus.Transitioning,
                    f.Network.OwnCharacter!.Generation, "synthetic-transition", At.AddSeconds(4)); break;
                case "transport": f.InstallTransport(); break;
            }
        };
        f.Network.CombatEvent += _ => lateCalls++;
        f.Send(OwnKill(), 2, stream);
        Assert.Single(f.Events); Assert.Equal(0, lateCalls);
    }

    [Fact]
    public void ThrowingKillSubscriberCannotBreakFifoOrExportItsExceptionBody()
    {
        using var f = new Fixture(); var stream = f.InstallTransport(); f.Send(Own(), stream: stream);
        f.Send(Appearance(), 1, stream); f.Send(Appearance(entity: 401), 1, stream);
        var secret = "SyntheticPrivateException" + Guid.NewGuid().ToString("N"); var nested = false;
        var late = new List<long>();
        f.Network.CombatEvent += _ =>
        {
            if (!nested) { nested = true; f.Send(OwnKill(401), 3, stream); }
            throw new InvalidOperationException(secret);
        };
        f.Network.CombatEvent += observation => late.Add(observation.Sequence);
        f.Send(OwnKill(), 2, stream);
        Assert.Equal(2, f.Events.Count); Assert.Equal(f.Events.Select(row => row.Sequence), late);
        Assert.DoesNotContain(secret, File.ReadAllText(Path.Combine(AppPaths.LogsDirectory, "errors.log")));
    }

    [Fact]
    public void CombatFifoOverflowRevokesOwnAuthorityRatherThanPublishingAnUnorderedPartialBacklog()
    {
        using var f = new Fixture(); var stream = f.InstallTransport(); f.Send(Own(), stream: stream);
        f.Send(Appearance(), 1, stream); var entered = false; var lateCalls = 0;
        f.Network.CombatEvent += _ =>
        {
            if (entered) return; entered = true;
            for (uint i = 0; i < 70; i++)
            {
                f.Send(Appearance(1_000 + i), 3 + (int)i * 2, stream);
                f.Send(OwnKill(1_000 + i), 4 + (int)i * 2, stream);
            }
        };
        f.Network.CombatEvent += _ => lateCalls++;
        f.Send(OwnKill(), 2, stream);
        Assert.Single(f.Events); Assert.Equal(0, lateCalls); Assert.Null(f.Network.OwnCharacter);
    }

    private static void AssertQualifiedCapabilities(Fixture f)
    {
        Assert.True(f.Network.CombatCapabilities.KillQualified);
        Assert.True(f.Network.CombatCapabilities.DeathQualified);
        Assert.False(f.Network.CombatCapabilities.AssistQualified);
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly CaptureConnection Main = new(7, "192.0.2.10", 50001, "198.51.100.20", 443);
        internal static string TransportStream => Main.StreamKey;
        private readonly string _root = Path.Combine(Path.GetTempPath(), "soulcrest-combat-" + Guid.NewGuid().ToString("N"));
        internal NetworkLootService Network { get; }
        internal WorldContextService World { get; }
        internal List<NetworkAbyssCombatEvent> Events { get; } = [];
        internal AbyssPlayerRegistry Registry => (AbyssPlayerRegistry)Field("_abyssPlayers").GetValue(Network)!;
        private Dictionary<CaptureConnection, (TcpReassembler Tcp, GameMessageStream Messages)> Streams =>
            (Dictionary<CaptureConnection, (TcpReassembler Tcp, GameMessageStream Messages)>)Field("_streams").GetValue(Network)!;
        internal Fixture()
        {
            Directory.CreateDirectory(_root);
            World = new(InstanceCatalog.FromJson("""
                {"schemaVersion":1,"maps":[
                  {"id":20,"kind":"instance","category":"Abyss","sourceRefs":["synthetic"]},
                  {"id":21,"kind":"openWorld","category":"OpenWorld","sourceRefs":["synthetic"]}],"dungeons":[]}
                """));
            Network = new(new ProgressService(Path.Combine(_root, "progress.json"), new PetCatalog([])), worldContext: World);
            typeof(NetworkLootService).GetProperty(nameof(NetworkLootService.Running))!.SetValue(Network, true);
            Network.CombatEvent += Events.Add;
        }
        internal void Send(byte[] message, int seconds = 0, string stream = Stream) =>
            Network.ProcessProgressMessage(stream, message, At.AddSeconds(seconds));
        internal string InstallTransport()
        {
            Streams[Main] = (new(), new());
            return Main.StreamKey;
        }
        internal AbyssPlayerRegistry.Context RegistryContext(string stream) => new(Network.OwnCharacter!, stream,
            Streams[Main].Messages, (long)Field("_generation").GetValue(Network)!,
            (long)Field("_abyssCombatRevision").GetValue(Network)!, World.Snapshot);
        private static FieldInfo Field(string name) => typeof(NetworkLootService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        internal void ChangeCapture(int seconds)
        {
            var field = typeof(NetworkLootService).GetField("_generation", BindingFlags.Instance | BindingFlags.NonPublic)!;
            field.SetValue(Network, (long)field.GetValue(Network)! + 1);
            Network.ResetProgressContext("capture-closed", At.AddSeconds(seconds));
        }
        public void Dispose() { Network.Dispose(); Directory.Delete(_root, recursive: true); }
    }

    private static byte[] Death(uint victim = 300, uint caster = 400, ushort server = 502,
        string name = "SyntheticOpponent", byte reason = 1) => Packet(0x8D04, writer =>
    {
        Var(writer, victim); writer.Write(1); Var(writer, caster); writer.Write(server);
        Text(writer, name); Text(writer, "Synthetic group");
        writer.Write((ushort)0); Var(writer, 0); writer.Write(0); writer.Write(reason); writer.Write((byte)1);
    });

    private static byte[] OwnKill(uint victim = 400) => Death(victim, 300, 501, "SyntheticCombat");

    private static byte[] Appearance(uint entity = 400, int template = 0, int? grade = null) => Packet(0x3645, writer =>
    {
        Var(writer, entity); writer.Write(1 | (grade.HasValue ? 0x1000000 : 0)); writer.Write((byte)1); Text(writer, "SyntheticVictim");
        writer.Write(template); // unknown class template is still an explicit complete PC descriptor
        writer.Write((ushort)0); writer.Write(new byte[18]); writer.Write((byte)0);
        Var(writer, 0); Var(writer, 0); writer.Write(new byte[52]); writer.Write((ushort)502);
        Var(writer, 0); Var(writer, 0); writer.Write(0L); Var(writer, 0); writer.Write(new byte[12]);
        Var(writer, 0); if (grade is { } value) writer.Write(value);
        writer.Write(0L); writer.Write((byte)0); writer.Write((byte)1);
    });

    private static byte[] NeutralDeath(uint victim, int skill = 0, byte reason = 0) => Packet(0x8D04, writer =>
    {
        Var(writer, victim); writer.Write(skill); Var(writer, 0); writer.Write((ushort)0);
        Text(writer, ""); Text(writer, ""); writer.Write((ushort)0); Var(writer, 0);
        writer.Write(0); writer.Write(reason); writer.Write((byte)0);
    });

    private static byte[] Revocation(ushort opcode, uint entity) => Packet(opcode, writer =>
    {
        Var(writer, entity);
        if (opcode == 0x3647) { writer.Write((byte)1); writer.Write((byte)0); }
        else writer.Write((byte)0xFF);
    }); // no child-byte meaning participates in PC qualification

    private static byte[] Own(int entity = 300, int map = 20) => Packet(0x3633, writer =>
    {
        Var(writer, (uint)entity); writer.Write(65536); writer.Write((byte)1);
        Text(writer, "SyntheticCombat"); writer.Write((ushort)501); writer.Write(29);
        writer.Write(new byte[36]); Var(writer, 0); Var(writer, 0); writer.Write(new byte[16]);
        writer.Write((byte)0); writer.Write((ushort)0); writer.Write(new byte[18]); writer.Write((byte)1);
        Var(writer, 0); Var(writer, 0); writer.Write(new byte[52]); Var(writer, 0); Var(writer, 0); Var(writer, 0);
        writer.Write(0L); writer.Write(map); writer.Write(101L); Var(writer, 0); writer.Write((byte)1);
        writer.Write(1_000L); writer.Write(new byte[32]); Var(writer, 0); Var(writer, 0);
    });

    private static byte[] Rank(long total) => Packet(0xE273, writer => { writer.Write((byte)0); writer.Write(total); });
    private static byte[] Packet(ushort opcode, Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(opcode); write(writer); return stream.ToArray();
    }
    private static void Text(BinaryWriter writer, string text)
    { var bytes = Encoding.UTF8.GetBytes(text); Var(writer, (uint)bytes.Length); writer.Write(bytes); }
    private static void Var(BinaryWriter writer, uint value)
    {
        while (value >= 128) { writer.Write((byte)(value | 128)); value >>= 7; }
        writer.Write((byte)value);
    }
}
