using Soulcrest.App.Network;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Only the RAM gate is tested here; it does not enable the live wire capability.</summary>
public sealed class AbyssOwnDeathLifeTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly OwnCharacterIdentity Identity = new(101, 501, 300, "SyntheticDeath");
    private static readonly AbyssPlayerCasterDeathObservation Death = new(300, 400, 502, "SyntheticCaster");

    [Fact]
    public void OneVictimEntityCountsOnceDespiteRepeatedBodiesTimesOrCasters()
    {
        var gate = new AbyssOwnDeathLife(); var own = Own();
        Assert.True(Consume(gate, own, Death));
        Assert.False(Consume(gate, own, Death));
        Assert.False(Consume(gate, own, Death with { CasterEntityKey = 401, CasterName = "OtherSyntheticCaster" }, At.AddMinutes(10)));
    }

    [Fact]
    public void FreshOwnAndSoftOrGapResetCannotRearmTheSameEntityWithoutAnAliveProof()
    {
        var gate = new AbyssOwnDeathLife();
        Assert.True(Consume(gate, Own(), Death));
        Assert.True(gate.ObserveCapture(1));
        Assert.False(Consume(gate, Own(2, At.AddSeconds(5)), Death, At.AddSeconds(6)));
        Assert.False(Consume(gate, Own(3, At.AddSeconds(7)), Death, At.AddSeconds(8)));
        Assert.False(gate.Overflowed);
    }

    [Fact]
    public void ANewOwnVictimEntityHasItsOwnBoundedSlot()
    {
        var gate = new AbyssOwnDeathLife(); Assert.True(Consume(gate, Own(), Death));
        var next = Own(2) with { Identity = Identity with { EntityKey = 301 } };
        Assert.True(Consume(gate, next, Death with { VictimEntityKey = 301 }));
        Assert.False(Consume(gate, Own(3), Death));
    }

    [Fact]
    public void StableCharacterAndServerArePartOfTheSeenKey()
    {
        var gate = new AbyssOwnDeathLife(); Assert.True(Consume(gate, Own(), Death));
        Assert.True(Consume(gate, Own(2) with { Identity = Identity with { CharacterId = 102 } }, Death));
        Assert.True(Consume(gate, Own(3) with { Identity = Identity with { ServerId = 503 } }, Death));
        Assert.False(Consume(gate, Own(4), Death));
    }

    [Fact]
    public void OnlyANewerCaptureGenerationClearsSeenKeysAndOldGenerationsNeverRollItBack()
    {
        var gate = new AbyssOwnDeathLife(); Assert.True(Consume(gate, Own(), Death));
        Assert.True(Consume(gate, Own(2), Death, capture: 2));
        Assert.False(Consume(gate, Own(3), Death, capture: 1));
        Assert.False(Consume(gate, Own(4), Death, capture: 2));
    }

    [Fact]
    public void FullCapacityLatchesWithoutEvictingOldEntitiesUntilANewCapture()
    {
        var gate = new AbyssOwnDeathLife();
        for (var i = 0; i < AbyssOwnDeathLife.MaximumSeenEntities; i++)
        {
            var key = 1_000 + i;
            Assert.True(Consume(gate, Own(i + 1) with { Identity = Identity with { EntityKey = key } },
                Death with { VictimEntityKey = key }));
        }
        Assert.False(gate.Overflowed);
        Assert.False(Consume(gate, Own(1_000) with { Identity = Identity with { EntityKey = 9_000 } },
            Death with { VictimEntityKey = 9_000 }));
        Assert.True(gate.Overflowed); Assert.True(gate.ObserveCapture(1));
        Assert.True(gate.Overflowed);
        Assert.False(Consume(gate, Own(1_001) with { Identity = Identity with { EntityKey = 1_000 } },
            Death with { VictimEntityKey = 1_000 }));
        Assert.True(Consume(gate, Own(1_002), Death, capture: 2));
        Assert.False(gate.Overflowed);
    }

    [Fact]
    public void ForeignVictimCannotConsumeOwnSlotOrEstablishAnAssist()
    {
        var gate = new AbyssOwnDeathLife(); var own = Own();
        Assert.False(Consume(gate, own, Death with { VictimEntityKey = 999 }));
        Assert.True(Consume(gate, own, Death));
    }

    [Fact]
    public void ExplicitSameOwnPcAttributionIsNotPvpEvenWithADifferentCasterEntity()
    {
        var gate = new AbyssOwnDeathLife(); var own = Own();
        Assert.False(Consume(gate, own, Death with { CasterName = Identity.Name, CasterServerId = (ushort)Identity.ServerId }));
        Assert.True(Consume(gate, own, Death with { CasterName = Identity.Name, CasterServerId = 503 }));
    }

    [Theory]
    [InlineData("missing-own-time")] [InlineData("default-own-time")] [InlineData("no-generation")]
    [InlineData("default-event-time")] [InlineData("before-own")] [InlineData("future")]
    [InlineData("no-character")] [InlineData("no-server")] [InlineData("negative-capture")]
    public void InvalidIdentityTimeOrCaptureCannotConsumeAValidSlot(string invalid)
    {
        var gate = new AbyssOwnDeathLife(); var own = Own(); var at = At.AddSeconds(1); var capture = 1L;
        switch (invalid)
        {
            case "missing-own-time": own = own with { ObservedAt = null }; break;
            case "default-own-time": own = own with { ObservedAt = default(DateTimeOffset) }; break;
            case "no-generation": own = own with { Generation = 0 }; break;
            case "default-event-time": at = default; break;
            case "before-own": at = At.AddSeconds(-1); break;
            case "future": at = At.AddHours(2); break;
            case "no-character": own = own with { Identity = Identity with { CharacterId = 0 } }; break;
            case "no-server": own = own with { Identity = Identity with { ServerId = 0 } }; break;
            case "negative-capture": capture = -1; break;
        }
        Assert.False(Consume(gate, own, Death, at, capture));
        Assert.True(Consume(gate, Own(2), Death));
    }

    private static bool Consume(AbyssOwnDeathLife gate, OwnCharacterContext own,
        AbyssPlayerCasterDeathObservation death, DateTimeOffset? at = null, long capture = 1) =>
        gate.TryConsume(own, death, at ?? At.AddSeconds(1), At.AddHours(1), capture);
    private static OwnCharacterContext Own(long generation = 1, DateTimeOffset? entered = null) =>
        new(Identity, generation) { ObservedAt = entered ?? At };
}
