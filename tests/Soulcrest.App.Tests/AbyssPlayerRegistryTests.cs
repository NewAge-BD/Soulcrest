using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class AbyssPlayerRegistryTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly OwnCharacterIdentity Identity = new(101, 501, 300, "SyntheticOwn");
    private static readonly AbyssPlayerAppearanceObservation Player = new(400, 502, "SyntheticVictim");
    private static readonly AbyssPlayerCasterDeathObservation Kill = new(400, 300, 501, "SyntheticOwn");

    [Fact]
    public void CompletePlayerAndExactOwnCasterProduceOneCandidateWithoutClassOrRankHeuristics()
    {
        var registry = new AbyssPlayerRegistry(); var context = Context();
        Assert.True(Appear(registry, context));
        Assert.True(Consume(registry, context, Kill, out var name)); Assert.Equal(Player.Name, name);
        Assert.False(Consume(registry, context, Kill, out _));
        Assert.False(Appear(registry, context, Player with { ClassId = "Cleric", AbyssRankGrade = 19 }, 3));
        Assert.False(Consume(registry, context, Kill, out _, 4)); Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void RepeatedAppearanceIsNotALifeAndCannotRearmAfterAnyObservedCasterDeath()
    {
        var registry = new AbyssPlayerRegistry(); var context = Context();
        Assert.True(Appear(registry, context)); Assert.True(Appear(registry, context, seconds: 2));
        Assert.False(Consume(registry, context, Kill with { CasterEntityKey = 900, CasterServerId = 503,
            CasterName = "SyntheticOtherCaster" }, out _, 3));
        Assert.False(Appear(registry, context, seconds: 4));
        Assert.False(Consume(registry, context, Kill, out _, 5));
    }

    [Fact]
    public void UnclassifiedVictimDeathCreatesOnlyATombstoneAndNeverAnNpcKill()
    {
        var registry = new AbyssPlayerRegistry(); var context = Context();
        Assert.False(Consume(registry, context, Kill, out var name)); Assert.Null(name);
        Assert.Equal(1, registry.Count); Assert.False(Appear(registry, context));
        Assert.False(Consume(registry, context, Kill, out _));
        Assert.True(Appear(registry, context, Player with { EntityKey = 401 }, 3));
        Assert.True(Consume(registry, context, Kill with { VictimEntityKey = 401 }, out _, 4));
    }

    [Theory]
    [InlineData("entity")] [InlineData("server")] [InlineData("name")]
    public void AllThreeExplicitOwnCasterFieldsAreRequiredAndRejectedCreditStillConsumesTheVictim(string field)
    {
        var registry = new AbyssPlayerRegistry(); var context = Context(); Assert.True(Appear(registry, context));
        var wrong = field switch
        {
            "entity" => Kill with { CasterEntityKey = 301 },
            "server" => Kill with { CasterServerId = 503 },
            _ => Kill with { CasterName = "SyntheticOther" }
        };
        Assert.False(Consume(registry, context, wrong, out _));
        Assert.False(Consume(registry, context, Kill, out _, 3));
    }

    [Theory]
    [InlineData("server")] [InlineData("name")]
    public void ActorIdentityConflictLatchesTheSlotAndCannotBeRepairedByTheOriginalAppearance(string field)
    {
        var registry = new AbyssPlayerRegistry(); var context = Context(); Assert.True(Appear(registry, context));
        var conflict = field == "server" ? Player with { ServerId = 503 } : Player with { Name = "SyntheticReuse" };
        Assert.False(Appear(registry, context, conflict, 2)); Assert.False(Appear(registry, context, seconds: 3));
        Assert.False(Consume(registry, context, Kill, out _, 4));
    }

    [Theory]
    [InlineData("own")] [InlineData("stream")] [InlineData("transport")]
    [InlineData("revision")] [InlineData("world")] [InlineData("explicit-invalidation")]
    public void EveryAuthorityBoundaryTombstonesExistingKeysWithoutRearmingThroughAppearance(string boundary)
    {
        var registry = new AbyssPlayerRegistry(); var context = Context(); Assert.True(Appear(registry, context));
        var next = boundary switch
        {
            "own" => context with { Own = context.Own with { } },
            "stream" => context with { Stream = "synthetic-other" },
            "transport" => context with { Transport = new() },
            "revision" => context with { Revision = 2 },
            "world" => context with { World = context.World with { Generation = 2 } },
            _ => context
        };
        if (boundary == "explicit-invalidation") registry.Invalidate();
        Assert.False(Appear(registry, next, seconds: 3)); Assert.False(Consume(registry, next, Kill, out _, 4));
        Assert.True(Appear(registry, next, Player with { EntityKey = 401 }, 5));
        Assert.True(Consume(registry, next, Kill with { VictimEntityKey = 401 }, out _, 6));
    }

    [Fact]
    public void OnlyANewerCaptureCanClearTombstonesAndOldGenerationsCannotRollItBack()
    {
        var registry = new AbyssPlayerRegistry(); var context = Context();
        Assert.True(Appear(registry, context)); Assert.True(Consume(registry, context, Kill, out _));
        var next = context with { CaptureGeneration = 2 };
        Assert.True(Appear(registry, next, seconds: 3));
        Assert.False(Appear(registry, context, seconds: 4));
        Assert.True(Consume(registry, next, Kill, out _, 5));
    }

    [Fact]
    public void LivePlayersAreBoundedAndInvalidatedPlayersFreeTheirBudgetWithoutBecomingEligibleAgain()
    {
        var registry = new AbyssPlayerRegistry(); var context = Context();
        for (var i = 0; i < AbyssPlayerRegistry.MaximumEntries; i++)
            Assert.True(Appear(registry, context, Player with { EntityKey = 1_000 + i }));
        Assert.Equal(4096, registry.Count); Assert.False(registry.Overflowed);
        // A context change turns every live PC into a one-bit tombstone and releases the live budget.
        registry.Invalidate();
        Assert.True(Appear(registry, context, Player with { EntityKey = 90_000 }, 3)); Assert.False(registry.Overflowed);
        Assert.False(Appear(registry, context, Player with { EntityKey = 1_000 }, 4));
        Assert.False(Consume(registry, context, Kill with { VictimEntityKey = 1_000 }, out _, 5));
        Assert.True(Consume(registry, context, Kill with { VictimEntityKey = 90_000 }, out _, 6));
        Assert.Equal(4097, registry.Count);
        var next = context with { CaptureGeneration = 2 };
        Assert.True(Appear(registry, next, seconds: 7)); Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void SimultaneouslyLivePlayersBeyondTheBudgetStillFailClosed()
    {
        var registry = new AbyssPlayerRegistry(); var context = Context();
        for (var i = 0; i < AbyssPlayerRegistry.MaximumEntries; i++)
            Assert.True(Appear(registry, context, Player with { EntityKey = 1_000 + i }));
        Assert.False(Appear(registry, context, Player with { EntityKey = 90_000 }, 3));
        Assert.True(registry.Overflowed); Assert.Equal(4096, registry.Count);
        Assert.False(Consume(registry, context, Kill with { VictimEntityKey = 1_000 }, out _, 4));
        var next = context with { CaptureGeneration = 2 };
        Assert.True(Appear(registry, next, seconds: 5)); Assert.False(registry.Overflowed); Assert.Equal(1, registry.Count);
    }

    [Theory]
    [InlineData("no-own-time")] [InlineData("no-world-time")] [InlineData("before-own")]
    [InlineData("before-world")] [InlineData("future")] [InlineData("default-time")]
    [InlineData("stale")] [InlineData("transition")] [InlineData("outside")]
    [InlineData("wrong-binding")] [InlineData("no-transport")] [InlineData("negative-capture")]
    public void InvalidTimeOrAuthorityCannotEstablishAPlayer(string invalid)
    {
        var registry = new AbyssPlayerRegistry(); var context = Context(); var at = At.AddSeconds(1);
        context = invalid switch
        {
            "no-own-time" => context with { Own = context.Own with { ObservedAt = null } },
            "no-world-time" => context with { World = context.World with { ObservedAt = default(DateTimeOffset) } },
            "before-own" => context with { Own = context.Own with { ObservedAt = At.AddSeconds(2) } },
            "before-world" => context with { World = context.World with { ObservedAt = At.AddSeconds(2) } },
            "stale" => context with { World = context.World with { Status = WorldContextStatus.Stale } },
            "transition" => context with { World = context.World with { Status = WorldContextStatus.Transitioning } },
            "outside" => context with { World = context.World with { Category = "OpenWorld" } },
            "wrong-binding" => context with { World = context.World with { BindingGeneration = 2 } },
            "no-transport" => context with { Transport = null! },
            "negative-capture" => context with { CaptureGeneration = -1 },
            _ => context
        };
        if (invalid == "future") at = At.AddHours(2);
        if (invalid == "default-time") at = default;
        Assert.False(registry.ObserveAppearance(context, Player, at, At.AddHours(1))); Assert.Equal(0, registry.Count);
    }

    [Theory]
    [InlineData(0, 502, "SyntheticVictim")] [InlineData(400, 0, "SyntheticVictim")]
    [InlineData(400, 502, "")] [InlineData(400, 502, "Synthetic\nVictim")]
    [InlineData(300, 502, "SyntheticVictim")] [InlineData(400, 501, "SyntheticOwn")]
    public void InvalidIdentityAndOwnRepresentationsCannotBecomeOpponents(int entity, ushort server, string name)
    {
        var registry = new AbyssPlayerRegistry();
        Assert.False(Appear(registry, Context(), new(entity, server, name))); Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void ADeathBeforeTheAcceptedAppearanceCannotBeCreditedOrRearmed()
    {
        var registry = new AbyssPlayerRegistry(); var context = Context(); Assert.True(Appear(registry, context, seconds: 5));
        Assert.False(Consume(registry, context, Kill, out _, 4));
        Assert.False(Appear(registry, context, seconds: 6)); Assert.False(Consume(registry, context, Kill, out _, 7));
    }

    [Fact]
    public void TargetedRevocationLeavesOtherPlayersEligibleButNeverRearmsTheRevokedActor()
    {
        var registry = new AbyssPlayerRegistry(); var context = Context();
        Assert.True(Appear(registry, context)); Assert.True(Appear(registry, context, Player with { EntityKey = 401 }));
        Assert.True(registry.RevokeActor(context, 400, At.AddSeconds(2), At.AddHours(1)));
        Assert.False(Appear(registry, context, seconds: 3)); Assert.False(Consume(registry, context, Kill, out _, 4));
        Assert.True(Consume(registry, context, Kill with { VictimEntityKey = 401 }, out _, 4));
        Assert.Equal(2, registry.Count);
    }

    [Fact]
    public void UnknownRevokedActorsNeverExhaustKillEvidenceAndCannotLaterAppearAsFreshPlayers()
    {
        var registry = new AbyssPlayerRegistry(); var context = Context();
        for (var i = 0; i < 100_000; i++)
            Assert.True(registry.RevokeActor(context, 1_000 + i, At.AddSeconds(1), At.AddHours(1)));
        Assert.Equal(100_000, registry.Count); Assert.False(registry.Overflowed);
        Assert.False(Appear(registry, context, Player with { EntityKey = 1_000 }, 2));
        Assert.False(Appear(registry, context, Player with { EntityKey = 100_999 }, 2));
        Assert.True(registry.RevokeActor(context, 1_000, At.AddSeconds(2), At.AddHours(1)));
        Assert.Equal(100_000, registry.Count);
        Assert.True(Appear(registry, context, Player with { EntityKey = 900_000 }, 3));
        Assert.True(Consume(registry, context, Kill with { VictimEntityKey = 900_000 }, out _, 4));
        Assert.False(registry.Overflowed);
    }

    [Fact]
    public void InvalidRevocationAuthorityRevokesExistingEvidenceInsteadOfKeepingAnOldPlayerCache()
    {
        var registry = new AbyssPlayerRegistry(); var context = Context(); Assert.True(Appear(registry, context));
        Assert.False(registry.RevokeActor(context, 999, default, At.AddHours(1)));
        Assert.False(Appear(registry, context, seconds: 3)); Assert.False(Consume(registry, context, Kill, out _, 4));
    }

    [Fact]
    public void OnlyStrictlyNewerSameIdentityAppearancesUpdateTheOptionalMetadata()
    {
        var registry = new AbyssPlayerRegistry(); var context = Context();
        Assert.True(Appear(registry, context, Player with { ClassId = "Cleric", AbyssRankGrade = 19 }));
        Assert.True(Appear(registry, context, Player with { ClassId = "Templar", AbyssRankGrade = 13 }, 3));
        Assert.True(Appear(registry, context, Player with { ClassId = "Ranger", AbyssRankGrade = 1 }, 2));
        Assert.True(Appear(registry, context, Player with { ClassId = "Assassin", AbyssRankGrade = 17 }, 3));
        Assert.True(registry.TryConsumeOwnKillWithMetadata(context, Kill, At.AddSeconds(4), At.AddHours(1),
            out var name, out var classId, out var grade));
        Assert.Equal(Player.Name, name); Assert.Equal("Templar", classId); Assert.Equal(13, grade);
    }

    [Theory]
    [InlineData(null, null)] [InlineData("Brawler", 20)] [InlineData("Unrecognized", -1)]
    public void ANewUnknownMetadataFactClearsOldValuesWithoutDiscardingThePlayer(string? nextClass, int? nextGrade)
    {
        var registry = new AbyssPlayerRegistry(); var context = Context();
        Assert.True(Appear(registry, context, Player with { ClassId = "Cleric", AbyssRankGrade = 19 }));
        Assert.True(Appear(registry, context, Player with { ClassId = nextClass, AbyssRankGrade = nextGrade }, 2));
        Assert.True(registry.TryConsumeOwnKillWithMetadata(context, Kill, At.AddSeconds(3), At.AddHours(1),
            out var name, out var classId, out var grade));
        Assert.Equal(Player.Name, name); Assert.Null(classId); Assert.Null(grade);
    }

    [Fact]
    public void MetadataObservedAfterADeathTimeCannotBeAttachedRetroactively()
    {
        var registry = new AbyssPlayerRegistry(); var context = Context();
        Assert.True(Appear(registry, context, Player with { ClassId = "Cleric", AbyssRankGrade = 19 }));
        Assert.True(Appear(registry, context, Player with { ClassId = "Templar", AbyssRankGrade = 13 }, 5));
        Assert.True(registry.TryConsumeOwnKillWithMetadata(context, Kill, At.AddSeconds(4), At.AddHours(1),
            out var name, out var classId, out var grade));
        Assert.Equal(Player.Name, name); Assert.Null(classId); Assert.Null(grade);
    }

    [Theory]
    [InlineData("death")] [InlineData("revoke")] [InlineData("identity-conflict")] [InlineData("context")]
    public void EvenRichNewMetadataCannotRearmATombstone(string boundary)
    {
        var registry = new AbyssPlayerRegistry(); var context = Context(); Assert.True(Appear(registry, context));
        switch (boundary)
        {
            case "death": Assert.True(Consume(registry, context, Kill, out _)); break;
            case "revoke": Assert.True(registry.RevokeActor(context, 400, At.AddSeconds(2), At.AddHours(1))); break;
            case "identity-conflict": Assert.False(Appear(registry, context, Player with { ServerId = 503 }, 2)); break;
            case "context": registry.Invalidate(); break;
        }
        Assert.False(Appear(registry, context, Player with { ClassId = "Cleric", AbyssRankGrade = 19 }, 3));
        Assert.False(registry.TryConsumeOwnKillWithMetadata(context, Kill, At.AddSeconds(4), At.AddHours(1),
            out var name, out var classId, out var grade));
        Assert.Null(name); Assert.Null(classId); Assert.Null(grade);
    }

    [Fact]
    public void ClassAndRankFactsDoNotReplaceAnExplicitPlayerIdentityOrOwnCaster()
    {
        var registry = new AbyssPlayerRegistry(); var context = Context();
        Assert.False(Appear(registry, context, Player with { EntityKey = 0, ClassId = "Cleric", AbyssRankGrade = 19 }));
        Assert.True(Appear(registry, context, Player with { ClassId = "Cleric", AbyssRankGrade = 19 }));
        Assert.False(registry.TryConsumeOwnKillWithMetadata(context, Kill with { CasterEntityKey = 900 },
            At.AddSeconds(2), At.AddHours(1), out var name, out var classId, out var grade));
        Assert.Null(name); Assert.Null(classId); Assert.Null(grade);
    }

    [Fact]
    public void OwnDeathCasterMetadataIsAReadOnlyExactIdentityJoinAndDoesNotConsumeItsLife()
    {
        var registry = new AbyssPlayerRegistry(); var context = Context();
        Assert.True(Appear(registry, context, Player with { ClassId = "Ranger", AbyssRankGrade = 19 }));
        var death = new AbyssPlayerCasterDeathObservation(300, 400, 502, "SyntheticVictim");
        for (var i = 0; i < 2; i++)
        {
            Assert.True(registry.TryGetOwnDeathCasterMetadata(context, death, At.AddSeconds(2), At.AddHours(1),
                out var classId, out var grade));
            Assert.Equal("Ranger", classId); Assert.Equal(19, grade); Assert.Equal(1, registry.Count);
        }
        Assert.True(Consume(registry, context, Kill, out _, 3)); // lookup has not consumed the caster
        Assert.False(registry.TryGetOwnDeathCasterMetadata(context, death, At.AddSeconds(4), At.AddHours(1), out _, out _));
        Assert.False(Appear(registry, context, Player with { ClassId = "Cleric", AbyssRankGrade = 1 }, 5));
    }

    [Fact]
    public void CasterLookupPreservesUnknownMetadataAndRejectsFactsObservedAfterTheDeath()
    {
        var registry = new AbyssPlayerRegistry(); var context = Context(); Assert.True(Appear(registry, context));
        var death = new AbyssPlayerCasterDeathObservation(300, 400, 502, "SyntheticVictim");
        Assert.True(registry.TryGetOwnDeathCasterMetadata(context, death, At.AddSeconds(2), At.AddHours(1),
            out var unknownClass, out var unknownGrade));
        Assert.Null(unknownClass); Assert.Null(unknownGrade);
        Assert.True(Appear(registry, context, Player with { ClassId = "Cleric", AbyssRankGrade = 13 }, 5));
        Assert.False(registry.TryGetOwnDeathCasterMetadata(context, death, At.AddSeconds(4), At.AddHours(1),
            out var futureClass, out var futureGrade));
        Assert.Null(futureClass); Assert.Null(futureGrade);
        Assert.True(registry.TryGetOwnDeathCasterMetadata(context, death, At.AddSeconds(5), At.AddHours(1),
            out var currentClass, out var currentGrade));
        Assert.Equal("Cleric", currentClass); Assert.Equal(13, currentGrade);
    }

    [Theory]
    [InlineData("entity")] [InlineData("name")] [InlineData("server")] [InlineData("other-victim")]
    [InlineData("own")] [InlineData("stream")] [InlineData("transport")] [InlineData("revision")]
    [InlineData("capture")] [InlineData("world")] [InlineData("future")] [InlineData("default-time")]
    [InlineData("before-appearance")]
    public void InvalidCasterIdentityContextOrTimeCannotBorrowMetadataOrMutateTheValidEntry(string invalid)
    {
        var registry = new AbyssPlayerRegistry(); var context = Context();
        Assert.True(Appear(registry, context, Player with { ClassId = "Ranger", AbyssRankGrade = 19 }));
        var death = new AbyssPlayerCasterDeathObservation(300, 400, 502, "SyntheticVictim");
        var candidate = invalid switch
        {
            "entity" => death with { CasterEntityKey = 401 },
            "name" => death with { CasterName = "SyntheticOther" },
            "server" => death with { CasterServerId = 503 },
            "other-victim" => death with { VictimEntityKey = 900 },
            _ => death
        };
        var otherContext = invalid switch
        {
            "own" => context with { Own = context.Own with { } },
            "stream" => context with { Stream = "synthetic-other" },
            "transport" => context with { Transport = new() },
            "revision" => context with { Revision = 2 },
            "capture" => context with { CaptureGeneration = 2 },
            "world" => context with { World = context.World with { Generation = 2 } },
            _ => context
        };
        var at = invalid switch
        {
            "future" => At.AddHours(2), "default-time" => default,
            "before-appearance" => At, _ => At.AddSeconds(2)
        };
        Assert.False(registry.TryGetOwnDeathCasterMetadata(otherContext, candidate, at, At.AddHours(1),
            out var classId, out var grade));
        Assert.Null(classId); Assert.Null(grade); Assert.Equal(1, registry.Count);
        Assert.True(registry.TryGetOwnDeathCasterMetadata(context, death, At.AddSeconds(3), At.AddHours(1), out _, out _));
        Assert.True(Consume(registry, context, Kill, out _, 4));
    }

    [Theory]
    [InlineData("revocation")] [InlineData("identity-conflict")] [InlineData("invalidation")]
    public void CasterMetadataLookupNeverRepairsOrRearmsRevokedPlayerEvidence(string boundary)
    {
        var registry = new AbyssPlayerRegistry(); var context = Context();
        Assert.True(Appear(registry, context, Player with { ClassId = "Cleric", AbyssRankGrade = 13 }));
        if (boundary == "revocation") registry.RevokeActor(context, 400, At.AddSeconds(2), At.AddHours(1));
        else if (boundary == "identity-conflict") Assert.False(Appear(registry, context, Player with { ServerId = 503 }, 2));
        else registry.Invalidate();
        Assert.False(registry.TryGetOwnDeathCasterMetadata(context, new(300, 400, 502, "SyntheticVictim"),
            At.AddSeconds(3), At.AddHours(1), out var classId, out var grade));
        Assert.Null(classId); Assert.Null(grade);
        Assert.False(Appear(registry, context, Player with { ClassId = "Ranger", AbyssRankGrade = 19 }, 4));
        Assert.False(Consume(registry, context, Kill, out _, 5));
    }

    private static bool Appear(AbyssPlayerRegistry registry, AbyssPlayerRegistry.Context context,
        AbyssPlayerAppearanceObservation? player = null, int seconds = 1) =>
        registry.ObserveAppearance(context, player ?? Player, At.AddSeconds(seconds), At.AddHours(1));
    private static bool Consume(AbyssPlayerRegistry registry, AbyssPlayerRegistry.Context context,
        AbyssPlayerCasterDeathObservation death, out string? name, int seconds = 2) =>
        registry.TryConsumeOwnKill(context, death, At.AddSeconds(seconds), At.AddHours(1), out name);
    private static AbyssPlayerRegistry.Context Context()
    {
        var own = new OwnCharacterContext(Identity, 1) { ObservedAt = At };
        var world = new WorldContextSnapshot(WorldContextStatus.Confirmed, WorldContextKind.Instance,
            20, null, "Abyss", null, "Synthetic", "Synthetic", 1, At, "synthetic") { BindingGeneration = 1 };
        return new(own, "synthetic-combat", new(), 1, 1, world);
    }
}
