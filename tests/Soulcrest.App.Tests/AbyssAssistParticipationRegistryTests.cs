using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class AbyssAssistParticipationRegistryTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = At.AddHours(1);
    private static readonly AbyssPlayerAppearanceObservation Player = new(400, 502, "SyntheticVictim")
    { ClassId = "Ranger", AbyssRankGrade = 13 };
    private static readonly AbyssPlayerCasterDeathObservation OtherKill = new(400, 900, 503, "SyntheticOtherCaster");
    private static readonly AbyssPlayerCasterDeathObservation OwnKill = new(400, 300, 501, "SyntheticOwn");

    [Fact]
    public void OwnPositiveDamageThenAnotherExplicitPlayerKillProducesOneLabelledAssistWithFrozenMetadata()
    {
        var registry = new AbyssPlayerRegistry(); var context = Context();
        Assert.True(Appear(registry, context)); Assert.True(Damage(registry, context));
        Assert.True(registry.TryConsumeOwnTakedownWithMetadata(context, OtherKill, At.AddSeconds(3), Now,
            out var estimated, out var name, out var classId, out var grade));
        Assert.True(estimated); Assert.Equal(Player.Name, name); Assert.Equal("Ranger", classId); Assert.Equal(13, grade);
        Assert.False(Takedown(registry, context, OtherKill, 4, out var repeated)); Assert.False(repeated);
        Assert.False(Appear(registry, context, seconds: 5));
        Assert.False(Damage(registry, context, seconds: 6));
        Assert.False(Takedown(registry, context, OtherKill, 7, out _));
        Assert.Equal(1, registry.Count);
    }

    [Theory]
    [InlineData(299999999L, true)]
    [InlineData(300000000L, true)]
    [InlineData(300000001L, false)]
    public void ParticipationWindowIncludesExactlyThirtySecondsAndRejectsOneTickMore(long delayTicks, bool accepted)
    {
        var registry = new AbyssPlayerRegistry(); var context = Context();
        Assert.True(Appear(registry, context)); Assert.True(Damage(registry, context));
        Assert.Equal(accepted, registry.TryConsumeOwnTakedownWithMetadata(context, OtherKill,
            At.AddSeconds(2).AddTicks(delayTicks), Now, out var estimated, out _, out _, out _));
        Assert.Equal(accepted, estimated);
        Assert.False(Takedown(registry, context, OtherKill, 40, out _));
    }

    [Fact]
    public void NewerPositiveDamageRefreshesParticipationButAnOlderHitCannotReplaceItsTime()
    {
        var registry = new AbyssPlayerRegistry(); var context = Context();
        Assert.True(Appear(registry, context)); Assert.True(Damage(registry, context));
        Assert.True(Damage(registry, context, seconds: 20));
        Assert.False(Damage(registry, context, seconds: 10));
        Assert.True(Takedown(registry, context, OtherKill, 50, out var estimated)); Assert.True(estimated);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("nonpositive")]
    [InlineData("foreign-caster")]
    [InlineData("own-victim")]
    [InlineData("unproven-npc-victim")]
    public void NoPositiveOwnDamageToAnIndependentlyConfirmedPlayerMeansNoAssist(string invalid)
    {
        var registry = new AbyssPlayerRegistry(); var context = Context();
        if (invalid != "unproven-npc-victim") Assert.True(Appear(registry, context));
        if (invalid != "absent")
            Assert.False(registry.ObserveOwnDamage(context, invalid == "foreign-caster" ? 301 : 300,
                invalid == "own-victim" ? 300 : 400, invalid != "nonpositive", At.AddSeconds(2), Now));
        Assert.False(Takedown(registry, context, OtherKill, 3, out var estimated)); Assert.False(estimated);
    }

    [Theory]
    [InlineData("own-entity-wrong-name")]
    [InlineData("own-entity-wrong-server")]
    [InlineData("own-identity-other-entity")]
    [InlineData("invalid-entity")]
    [InlineData("invalid-server")]
    [InlineData("invalid-name")]
    [InlineData("self-death")]
    public void MissingOrContradictoryExplicitCasterIdentityNeverBecomesAnEstimatedAssist(string invalid)
    {
        var registry = new AbyssPlayerRegistry(); var context = Context();
        Assert.True(Appear(registry, context)); Assert.True(Damage(registry, context));
        var death = invalid switch
        {
            "own-entity-wrong-name" => OwnKill with { CasterName = "SyntheticOtherCaster" },
            "own-entity-wrong-server" => OwnKill with { CasterServerId = 503 },
            "own-identity-other-entity" => OwnKill with { CasterEntityKey = 301 },
            "invalid-entity" => OtherKill with { CasterEntityKey = 0 },
            "invalid-server" => OtherKill with { CasterServerId = 0 },
            "invalid-name" => OtherKill with { CasterName = "Synthetic\nCaster" },
            _ => OtherKill with { CasterEntityKey = 400 }
        };
        Assert.False(Takedown(registry, context, death, 3, out var estimated)); Assert.False(estimated);
    }

    [Theory]
    [InlineData("default")]
    [InlineData("future")]
    [InlineData("before-own")]
    [InlineData("before-appearance")]
    public void InvalidDamageTimesDoNotEstablishParticipation(string invalid)
    {
        var registry = new AbyssPlayerRegistry(); var context = Context(); Assert.True(Appear(registry, context));
        var at = invalid switch
        {
            "default" => default,
            "future" => Now.AddTicks(1),
            "before-own" => At.AddTicks(-1),
            _ => At
        };
        Assert.False(registry.ObserveOwnDamage(context, 300, 400, true, at, Now));
        Assert.False(Takedown(registry, context, OtherKill, 3, out _));
    }

    [Theory]
    [InlineData("default")]
    [InlineData("future")]
    [InlineData("before-damage")]
    [InlineData("before-appearance")]
    public void InvalidOrOutOfOrderDeathTimesCannotBorrowLaterParticipation(string invalid)
    {
        var registry = new AbyssPlayerRegistry(); var context = Context();
        Assert.True(Appear(registry, context)); Assert.True(Damage(registry, context));
        var at = invalid switch
        {
            "default" => default,
            "future" => Now.AddTicks(1),
            "before-damage" => At.AddSeconds(1),
            _ => At
        };
        Assert.False(registry.TryConsumeOwnTakedownWithMetadata(context, OtherKill, at, Now,
            out var estimated, out var name, out var classId, out var grade));
        Assert.False(estimated); Assert.Null(name); Assert.Null(classId); Assert.Null(grade);
    }

    [Theory]
    [InlineData("own")]
    [InlineData("stream")]
    [InlineData("transport")]
    [InlineData("revision")]
    [InlineData("world")]
    [InlineData("invalidate")]
    public void AuthorityChangesDiscardParticipationAndCannotRearmTheOldActor(string boundary)
    {
        var registry = new AbyssPlayerRegistry(); var context = Context();
        Assert.True(Appear(registry, context)); Assert.True(Damage(registry, context));
        var next = boundary switch
        {
            "own" => context with { Own = context.Own with { } },
            "stream" => context with { Stream = "synthetic-other" },
            "transport" => context with { Transport = new() },
            "revision" => context with { Revision = 2 },
            "world" => context with { World = context.World with { Generation = 2 } },
            _ => context
        };
        if (boundary == "invalidate") registry.Invalidate();
        Assert.False(Takedown(registry, next, OtherKill, 3, out _));
        Assert.False(Appear(registry, next, seconds: 4)); Assert.False(Damage(registry, next, seconds: 5));
    }

    [Theory]
    [InlineData(WorldContextStatus.Stale)]
    [InlineData(WorldContextStatus.Transitioning)]
    [InlineData(WorldContextStatus.Unknown)]
    public void UnconfirmedWorldCannotAcceptDamageOrAnEstimatedTakedown(WorldContextStatus status)
    {
        var registry = new AbyssPlayerRegistry(); var context = Context();
        Assert.True(Appear(registry, context)); Assert.True(Damage(registry, context));
        var invalid = context with { World = context.World with { Status = status } };
        Assert.False(Damage(registry, invalid, seconds: 3));
        Assert.False(Takedown(registry, invalid, OtherKill, 4, out _));
    }

    [Fact]
    public void ANewCaptureClearsOldParticipationAndOnlyFreshEvidenceCanQualifyAgain()
    {
        var registry = new AbyssPlayerRegistry(); var context = Context();
        Assert.True(Appear(registry, context)); Assert.True(Damage(registry, context));
        var next = context with { CaptureGeneration = 2 };
        Assert.True(Appear(registry, next, seconds: 3));
        Assert.False(Takedown(registry, next, OtherKill, 4, out _));
        Assert.False(Appear(registry, context, seconds: 5));
        var fresh = context with { CaptureGeneration = 3 };
        Assert.True(Appear(registry, fresh, seconds: 6)); Assert.True(Damage(registry, fresh, seconds: 7));
        Assert.True(Takedown(registry, fresh, OtherKill, 8, out var estimated)); Assert.True(estimated);
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("identity-conflict")]
    public void ActorRevocationOrIdentityConflictRemovesParticipationPermanently(string boundary)
    {
        var registry = new AbyssPlayerRegistry(); var context = Context();
        Assert.True(Appear(registry, context)); Assert.True(Damage(registry, context));
        if (boundary == "revoked") Assert.True(registry.RevokeActor(context, 400, At.AddSeconds(3), Now));
        else Assert.False(Appear(registry, context, Player with { ServerId = 503 }, 3));
        Assert.False(Takedown(registry, context, OtherKill, 4, out _));
        Assert.False(Appear(registry, context, seconds: 5)); Assert.False(Damage(registry, context, seconds: 6));
    }

    [Fact]
    public void ExactOwnFinalBlowSupersedesParticipationAndCannotAlsoEmitAnEstimatedAssist()
    {
        var registry = new AbyssPlayerRegistry(); var context = Context();
        Assert.True(Appear(registry, context)); Assert.True(Damage(registry, context));
        Assert.True(Takedown(registry, context, OwnKill, 3, out var estimated)); Assert.False(estimated);
        Assert.False(Takedown(registry, context, OtherKill, 3, out _));
        Assert.False(Takedown(registry, context, OwnKill, 4, out _));
        Assert.False(Appear(registry, context, seconds: 5));
    }

    [Fact]
    public void ExistingQualifiedKillJoinNeverStartsReturningEstimatedAssists()
    {
        var registry = new AbyssPlayerRegistry(); var context = Context();
        Assert.True(Appear(registry, context)); Assert.True(Damage(registry, context));
        Assert.False(registry.TryConsumeOwnKillWithMetadata(context, OtherKill, At.AddSeconds(3), Now,
            out var name, out var classId, out var grade));
        Assert.Null(name); Assert.Null(classId); Assert.Null(grade);
        Assert.False(Takedown(registry, context, OtherKill, 4, out _));
    }

    [Fact]
    public void ClearOnOwnDeathRemovesEveryParticipationWithoutDiscardingLiveOpponentMetadata()
    {
        var registry = new AbyssPlayerRegistry(); var context = Context();
        Assert.True(Appear(registry, context));
        Assert.True(Appear(registry, context, Player with { EntityKey = 401 }));
        Assert.True(Damage(registry, context));
        Assert.True(registry.ObserveOwnDamage(context, 300, 401, true, At.AddSeconds(2), Now));
        registry.ClearOwnParticipation();
        Assert.False(Takedown(registry, context, OtherKill, 3, out _));
        Assert.True(registry.ObserveOwnDamage(context, 300, 401, true, At.AddSeconds(4), Now));
        Assert.True(registry.TryConsumeOwnTakedownWithMetadata(context, OtherKill with { VictimEntityKey = 401 },
            At.AddSeconds(5), Now, out var estimated, out _, out var classId, out var grade));
        Assert.True(estimated); Assert.Equal("Ranger", classId); Assert.Equal(13, grade);
    }

    [Fact]
    public void DamageUsesOnlyExistingBoundedPlayerEntriesAndCannotAllocateUnknownVictims()
    {
        var registry = new AbyssPlayerRegistry(); var context = Context();
        for (var index = 0; index < AbyssPlayerRegistry.MaximumEntries; index++)
            Assert.True(Appear(registry, context, Player with { EntityKey = 1_000 + index }));
        for (var index = 0; index < 10_000; index++)
            Assert.False(registry.ObserveOwnDamage(context, 300, 90_000 + index, true, At.AddSeconds(2), Now));
        Assert.Equal(AbyssPlayerRegistry.MaximumEntries, registry.Count); Assert.False(registry.Overflowed);
        Assert.True(registry.ObserveOwnDamage(context, 300, 1_000, true, At.AddSeconds(2), Now));
        Assert.True(Takedown(registry, context, OtherKill with { VictimEntityKey = 1_000 }, 3, out var estimated));
        Assert.True(estimated); Assert.Equal(AbyssPlayerRegistry.MaximumEntries, registry.Count);
    }

    [Fact]
    public void SaturatedRegistryCannotCreateNewParticipationUntilANewerCapture()
    {
        var registry = new AbyssPlayerRegistry(); var context = Context();
        for (var index = 0; index < AbyssPlayerRegistry.MaximumEntries; index++)
            Assert.True(Appear(registry, context, Player with { EntityKey = 1_000 + index }));
        Assert.True(registry.ObserveOwnDamage(context, 300, 1_000, true, At.AddSeconds(2), Now));
        Assert.False(Appear(registry, context, Player with { EntityKey = 90_000 }, 3)); Assert.True(registry.Overflowed);
        Assert.False(registry.ObserveOwnDamage(context, 300, 1_000, true, At.AddSeconds(4), Now));
        Assert.False(Takedown(registry, context, OtherKill with { VictimEntityKey = 1_000 }, 5, out _));
        var next = context with { CaptureGeneration = 2 };
        Assert.True(Appear(registry, next, seconds: 6)); Assert.True(Damage(registry, next, seconds: 7));
        Assert.True(Takedown(registry, next, OtherKill, 8, out var estimated)); Assert.True(estimated);
    }

    private static bool Appear(AbyssPlayerRegistry registry, AbyssPlayerRegistry.Context context,
        AbyssPlayerAppearanceObservation? player = null, int seconds = 1) =>
        registry.ObserveAppearance(context, player ?? Player, At.AddSeconds(seconds), Now);
    private static bool Damage(AbyssPlayerRegistry registry, AbyssPlayerRegistry.Context context, int seconds = 2) =>
        registry.ObserveOwnDamage(context, 300, 400, true, At.AddSeconds(seconds), Now);
    private static bool Takedown(AbyssPlayerRegistry registry, AbyssPlayerRegistry.Context context,
        AbyssPlayerCasterDeathObservation death, int seconds, out bool estimated) =>
        registry.TryConsumeOwnTakedownWithMetadata(context, death, At.AddSeconds(seconds), Now, out estimated, out _, out _, out _);
    private static AbyssPlayerRegistry.Context Context()
    {
        var own = new OwnCharacterContext(new(101, 501, 300, "SyntheticOwn"), 1) { ObservedAt = At };
        var world = new WorldContextSnapshot(WorldContextStatus.Confirmed, WorldContextKind.Instance,
            20, null, "Abyss", null, "Synthetic", "Synthetic", 1, At, "synthetic") { BindingGeneration = 1 };
        return new(own, "synthetic-combat", new(), 1, 1, world);
    }
}
