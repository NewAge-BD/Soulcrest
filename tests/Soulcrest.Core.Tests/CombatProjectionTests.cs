using System.Text.Json;
using Soulcrest.Core.Combat;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class CombatProjectionTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly CombatEntityReference Boss = new("boss-entity", 1, "stream-a");
    private static readonly CombatParticipant One = new("player-one", "Player One", "class-a", 12000, true);
    private static readonly CombatParticipant Two = new("player-two", "Player Two", "class-b", 13000);
    private static readonly CombatParticipant Three = new("player-three", "Player Three", "class-c", 14000);
    private const CombatCapabilities All = CombatCapabilities.Damage | CombatCapabilities.SkillCasts
        | CombatCapabilities.Healing | CombatCapabilities.Deaths | CombatCapabilities.Buffs;

    [Fact]
    public void SharedTimeIncludesMechanicsDeathAndLateJoin()
    {
        var summary = CombatProjection.Project(Attempt(20,
            At(1, 0, Hit(One, 1000)), At(2, 1, new CombatDeathEvent(One.ParticipantId)),
            At(3, 10, new CombatRosterEvent([One, Two])), At(4, 19, Hit(Two, 1000))) with { Participants = [One] });
        Assert.Equal(100m, summary.Dps);
        Assert.All(summary.Players, player => Assert.Equal(50m, player.Dps));
        Assert.Equal(1, summary.Players.Single(p => p.Participant.ParticipantId == One.ParticipantId).Deaths);
        Assert.True(summary.IsComplete);
    }

    [Fact]
    public void BossGroupAndConfirmedOwnerAreRequired()
    {
        var summary = CombatProjection.Project(Attempt(10,
            At(1, 0, Hit(One, 100)),
            At(2, 1, new CombatDamageEvent("outsider", Boss, "skill-a", 9999)),
            At(3, 2, Hit(One, 9999) with { Target = new("ordinary-add", 1, "stream-a") }),
            At(4, 3, new CombatDamageEvent(null, Boss, "summon-skill", 50, ConfirmedOwnerParticipantId: One.ParticipantId)),
            At(5, 4, new CombatDamageEvent(null, Boss, "unknown-summon", 9999))));
        Assert.Equal(150, summary.Damage);
        Assert.Equal(2, summary.Timeline.Count);
        Assert.DoesNotContain(summary.Players, p => p.Participant.ParticipantId == "outsider");
        Assert.Equal(2, summary.Players[0].Skills.Count);
    }

    [Fact]
    public void EqualEntityIdsFromOtherGenerationsOrStreamsCannotDamageBoss()
    {
        var summary = CombatProjection.Project(Attempt(10,
            At(1, 0, Hit(One, 100)),
            At(2, 1, Hit(One, 1000)) with { Source = new("stream-b", 1) },
            At(3, 2, Hit(One, 1000)) with { Source = new("stream-a", 2) },
            At(4, 3, Hit(One, 1000) with { Target = Boss with { StreamId = "stream-b" } })));
        Assert.Equal(100, summary.Damage);
        Assert.False(summary.IsComplete);
        Assert.Contains(summary.QualityIssues, i => i.Code == "unbound-source-context");
        Assert.Contains(summary.QualityIssues, i => i.Code == "stale-entity-generation");
    }

    [Fact]
    public void RosterChangesPreserveFormerMembersButOnlyCurrentMembersContribute()
    {
        var summary = CombatProjection.Project(Attempt(10,
            At(1, 0, Hit(Two, 100)), At(2, 1, Hit(Three, 1000)),
            At(3, 4, new CombatRosterEvent([One, Three])), At(4, 5, Hit(Two, 1000)),
            At(5, 6, Hit(Three, 200))));
        Assert.Equal(300, summary.Damage);
        Assert.Equal(3, summary.Players.Count);
        Assert.Equal(100, summary.Players.Single(p => p.Participant.ParticipantId == Two.ParticipantId).Damage);
        Assert.Equal(Three, summary.Players.Single(p => p.Participant.ParticipantId == Three.ParticipantId).Participant);
        Assert.True(summary.IsComplete);
    }

    [Fact]
    public void SequenceDeduplicatesRetransmissionButNeverDeduplicatesEqualPayloads()
    {
        var first = At(1, 0, Hit(One, 100));
        var summary = CombatProjection.Project(Attempt(10, first, first,
            At(2, 0, Hit(One, 100))));
        Assert.Equal(200, summary.Damage);
        Assert.Equal(2, summary.Timeline.Count);
        Assert.Contains(summary.QualityIssues, i => i.Code == "duplicate-sequence");
    }

    [Fact]
    public void CastsHitsTicksAndKnownFlagDenominatorsStayDistinct()
    {
        var summary = CombatProjection.Project(Attempt(10,
            At(1, 0, new CombatCastEvent(One.ParticipantId, "skill-a")),
            At(2, 1, Hit(One, 150) with { Flags = new(Critical: true) }),
            At(3, 2, Hit(One, 100) with { Flags = new(Critical: false) }),
            At(4, 3, Hit(One, 50) with { IsPeriodic = true })));
        var skill = summary.Players[0].Skills.Single();
        Assert.Equal(1, skill.Casts);
        Assert.Equal(3, skill.Hits);
        Assert.Equal(1, skill.PeriodicTicks);
        Assert.Equal(150, skill.MaximumHit);
        Assert.Equal(100m, skill.AverageHit);
        Assert.Equal(new CombatRate(1, 2), skill.Rates["critical"]);
        Assert.False(skill.Rates.ContainsKey("front"));
    }

    [Fact]
    public void UnsupportedMetricsRemainUnknownInsteadOfBecomingZero()
    {
        var summary = CombatProjection.Project(Attempt(10, At(1, 0, Hit(One, 100))) with
        { Capabilities = CombatCapabilities.Damage });
        Assert.Null(summary.Players[0].Deaths);
        Assert.Null(summary.Players[0].Healing);
        Assert.Null(summary.Players[0].Hps);
        Assert.Null(summary.Players[0].Skills[0].Casts);
        Assert.Null(summary.Players[0].Skills[0].Healing);
        Assert.Null(summary.Players[0].Skills[0].Rates.GetValueOrDefault("critical"));
    }

    [Fact]
    public void MissingCapabilitiesKeepDerivedRatesUnknownDespitePartialObservedEvents()
    {
        var summary = CombatProjection.Project(Attempt(10,
            At(1, 0, Effect(CombatBuffAction.ObservedActive, One)),
            At(2, 1, Hit(One, 100))) with { Capabilities = CombatCapabilities.None });
        Assert.Equal(100, summary.Damage);
        Assert.Null(summary.Dps);
        Assert.Equal(100, summary.Players[0].Damage);
        Assert.Null(summary.Players[0].Dps);
        Assert.Null(summary.Players[0].DamageShare);
        Assert.Null(Assert.Single(summary.Buffs).Uptime);
        Assert.False(summary.IsComplete);
        Assert.Contains(summary.QualityIssues, issue => issue.Code == "damage-coverage-unknown");
    }

    [Fact]
    public void BuffUptimeUsesUnionWhilePreservingSourceStackAndLevelIntervals()
    {
        var summary = CombatProjection.Project(Attempt(10,
            At(1, 0, Effect(CombatBuffAction.ObservedActive, One)),
            At(2, 2, Effect(CombatBuffAction.Apply, Two)),
            At(3, 3, Effect(CombatBuffAction.Refresh, One) with { StackCount = 2, Level = 5 }),
            At(4, 5, Effect(CombatBuffAction.Remove, One)),
            At(5, 8, Effect(CombatBuffAction.Remove, Two))));
        var buff = Assert.Single(summary.Buffs);
        Assert.Equal(1, buff.Applications);
        Assert.Equal(Ticks(8), buff.ActiveTicks);
        Assert.Equal(.8m, buff.Uptime);
        Assert.Equal(3, buff.Intervals.Count);
        Assert.Contains(buff.Intervals, i => i.SourceParticipantId == One.ParticipantId && i.StackCount == 2 && i.Level == 5);
        Assert.True(summary.IsComplete);
    }

    [Fact]
    public void GapClosesKnownIntervalsAndDoesNotBridgeUnobservedTime()
    {
        var summary = CombatProjection.Project(Attempt(10,
            At(1, 0, Effect(CombatBuffAction.ObservedActive, One)),
            At(2, 4, new CombatGapEvent(CombatGapReason.Transport)),
            At(3, 8, Effect(CombatBuffAction.Remove, One))));
        var buff = Assert.Single(summary.Buffs);
        Assert.Equal(Ticks(4), buff.ActiveTicks);
        Assert.Null(buff.Uptime);
        Assert.False(buff.InitialStateKnown);
        Assert.False(summary.IsComplete);
        Assert.Contains(summary.QualityIssues, i => i.Code == "gap-Transport");
    }

    [Fact]
    public void UnknownInitialStateCannotClaimExactBuffUptime()
    {
        var summary = CombatProjection.Project(Attempt(10,
            At(1, 0, Effect(CombatBuffAction.ObservedActive, One) with { InitialStateKnown = false })));
        var buff = Assert.Single(summary.Buffs);
        Assert.Null(buff.Uptime);
        Assert.Equal(0, buff.Applications);
        Assert.False(summary.IsComplete);
    }

    [Fact]
    public void GapRequiresNewRosterBeforeAttributionResumes()
    {
        var summary = CombatProjection.Project(Attempt(10,
            At(1, 0, Hit(One, 100)), At(2, 1, new CombatGapEvent(CombatGapReason.Transport)),
            At(3, 2, Hit(One, 1000)), At(4, 3, new CombatRosterEvent([One, Two])), At(5, 4, Hit(One, 200))));
        Assert.Equal(300, summary.Damage);
        Assert.False(summary.IsComplete);
    }

    [Fact]
    public void HistoricalBuffSourceRemovalDoesNotRemoveOtherSources()
    {
        var summary = CombatProjection.Project(Attempt(10,
            At(1, 0, Effect(CombatBuffAction.Apply, Two)),
            At(2, 1, new CombatRosterEvent([One, Three])),
            At(3, 2, Effect(CombatBuffAction.Apply, Three)),
            At(4, 3, Effect(CombatBuffAction.Remove, Two)),
            At(5, 8, Effect(CombatBuffAction.Remove, Three))));
        var buff = Assert.Single(summary.Buffs);
        Assert.Equal(Ticks(8), buff.ActiveTicks);
        Assert.Equal(Ticks(3), buff.Intervals.Single(i => i.SourceParticipantId == Two.ParticipantId).EndTicks);
        Assert.Equal(Ticks(8), buff.Intervals.Single(i => i.SourceParticipantId == Three.ParticipantId).EndTicks);
    }

    [Fact]
    public void UnattributedRemoveIsNotWildcardButExplicitRemoveAllClosesEverySource()
    {
        var summary = CombatProjection.Project(Attempt(10,
            At(1, 0, Effect(CombatBuffAction.Apply, One)), At(2, 1, Effect(CombatBuffAction.Apply, Two)),
            At(3, 2, Effect(CombatBuffAction.Remove, One) with { SourceParticipantId = null }),
            At(4, 5, Effect(CombatBuffAction.Remove, One) with { SourceParticipantId = null, RemoveAllSources = true })));
        var buff = Assert.Single(summary.Buffs);
        Assert.All(buff.Intervals, i => Assert.Equal(Ticks(5), i.EndTicks));
        Assert.Equal(Ticks(5), buff.ActiveTicks);
        Assert.Null(buff.Uptime);
    }

    [Fact]
    public void MissingBossDamageAttributionAndInterruptedAttemptAreNotComplete()
    {
        var unassigned = CombatProjection.Project(Attempt(10, At(1, 0, new CombatDamageEvent(null, Boss, "skill-a", 10))));
        Assert.False(unassigned.IsComplete);
        Assert.Contains(unassigned.QualityIssues, i => i.Code == "unknown-damage-attribution");
        var interrupted = CombatProjection.Project(Attempt(10, At(1, 0, Hit(One, 100))) with { EndReason = CombatEndReason.Interrupted });
        Assert.False(interrupted.IsComplete);
        Assert.Contains(interrupted.QualityIssues, i => i.Code == "interrupted-attempt");
    }

    [Fact]
    public void ForeignStreamRosterAndDeathsCannotChangeGroupState()
    {
        var summary = CombatProjection.Project(Attempt(10,
            At(1, 0, new CombatRosterEvent([Three])) with { Source = new("stream-b", 1) },
            At(2, 1, new CombatDeathEvent(One.ParticipantId)) with { Source = new("stream-b", 1) },
            At(3, 2, Hit(One, 100))));
        Assert.Equal(100, summary.Damage);
        Assert.Equal(0, summary.Players[0].Deaths);
        Assert.DoesNotContain(summary.Players, p => p.Participant.ParticipantId == Three.ParticipantId);
    }

    [Fact]
    public void IncompleteJournalFlagSurvivesRoundtripAndPreventsCompleteProjection()
    {
        var attempt = Attempt(10, At(1, 0, Hit(One, 100))) with { HasIncompleteJournal = true };
        var restored = JsonSerializer.Deserialize<CombatAttemptDocument>(JsonSerializer.Serialize(attempt))!;
        var summary = CombatProjection.Project(restored);
        Assert.Equal(100, summary.Damage);
        Assert.True(restored.HasIncompleteJournal);
        Assert.False(summary.IsComplete);
        Assert.Contains(summary.QualityIssues, issue => issue.Code == "incomplete-journal");
    }

    [Fact]
    public void BossHpKeepsConfirmedMaximumUntilGapAndNeverInfersItFromDamage()
    {
        var summary = CombatProjection.Project(Attempt(10,
            At(1, 0, new CombatHpEvent(Boss, 100, 200)),
            At(2, 1, new CombatHpEvent(Boss, 50)),
            At(3, 2, new CombatGapEvent(CombatGapReason.Transport)),
            At(4, 3, new CombatHpEvent(Boss, 25))));
        Assert.Equal(.5m, summary.HpPoints[0].Fraction);
        Assert.Equal(200, summary.HpPoints[1].Maximum);
        Assert.Null(summary.HpPoints[2].Maximum);
        Assert.Null(summary.HpPoints[2].Fraction);
    }

    [Fact]
    public void CaptureWallClockCanRegressWithoutChangingEventTimeOrDps()
    {
        var first = At(1, 1, Hit(One, 100));
        var second = At(2, 9, Hit(One, 100)) with { CapturedAtUtc = Start.AddHours(-1) };
        var summary = CombatProjection.Project(Attempt(10, second, first));
        Assert.Equal(20m, summary.Dps);
        Assert.Equal(new long[] { 1, 2 }, summary.Timeline.Select(e => e.Sequence));
        Assert.True(summary.IsComplete);
    }

    [Fact]
    public void MissingDurationNeverFallsBackToLastHitAndLiveUsesExplicitHorizon()
    {
        var attempt = Attempt(10, At(1, 1, Hit(One, 100))) with { DurationTicks = null, EndReason = null };
        var unknown = CombatProjection.Project(attempt);
        Assert.Null(unknown.Dps);
        Assert.Null(unknown.DurationTicks);
        Assert.False(unknown.IsComplete);
        var live = CombatProjection.Project(attempt, Ticks(20));
        Assert.Equal(5m, live.Dps);
        Assert.False(live.IsComplete);
    }

    [Fact]
    public void InvalidRosterAndNegativeAmountsCannotPoisonTotals()
    {
        var summary = CombatProjection.Project(Attempt(10,
            At(1, 0, new CombatRosterEvent([One, One])),
            At(2, 1, Hit(One, -1)), At(3, 2, Hit(One, 100))));
        Assert.Equal(100, summary.Damage);
        Assert.Contains(summary.QualityIssues, i => i.Code == "invalid-roster");
        Assert.Contains(summary.QualityIssues, i => i.Code == "invalid-damage");
        Assert.False(summary.IsComplete);
    }

    [Fact]
    public void PolymorphicJsonRoundtripPreservesEventsAndHistoricalValues()
    {
        var attempt = Attempt(10,
            At(1, 0, new CombatRosterEvent([One, Two])), At(2, 1, Hit(One, 100)),
            At(3, 2, new CombatCastEvent(One.ParticipantId, "skill-a")),
            At(4, 3, new CombatHealEvent(Two.ParticipantId, One.ParticipantId, "healing-skill", 50)),
            At(5, 4, Effect(CombatBuffAction.Apply, Two)), At(6, 5, new CombatHpEvent(Boss, 1000)),
            At(7, 6, new CombatDeathEvent(One.ParticipantId)), At(8, 7, new CombatGapEvent(CombatGapReason.QueueOverflow)));
        var run = new CombatRunDocument { Id = Guid.NewGuid(), DungeonKey = "dungeon-a", StartedAtUtc = Start, Attempts = [attempt] };
        var json = JsonSerializer.Serialize(run);
        var restored = JsonSerializer.Deserialize<CombatRunDocument>(json)!;
        Assert.Equal(run.Id, restored.Id);
        Assert.Equal(One, restored.Attempts[0].Participants[0]);
        Assert.Equal(attempt.Events.Select(e => e.Event.GetType()), restored.Attempts[0].Events.Select(e => e.Event.GetType()));
        Assert.Equal(CombatProjection.Project(attempt).Damage, CombatProjection.Project(restored.Attempts[0]).Damage);
        Assert.Equal(8, restored.Attempts[0].Events.Length);
    }

    private static CombatAttemptDocument Attempt(int seconds, params CombatEventEnvelope[] events) => new()
    {
        Id = Guid.NewGuid(), BossKey = "boss-a", BossTargets = [Boss], StartedAtUtc = Start,
        DurationTicks = Ticks(seconds), EndReason = CombatEndReason.Victory, Capabilities = All,
        MeasurementProfile = new("boss-damage-v1", "synthetic-build", "synthetic-decoder", "synthetic-catalog"),
        Participants = [One, Two], Events = events
    };
    private static CombatEventEnvelope At(long sequence, int seconds, CombatEvent value) =>
        new(sequence, Ticks(seconds), Start.AddSeconds(seconds), new("stream-a", 1), value);
    private static CombatDamageEvent Hit(CombatParticipant participant, long amount) => new(participant.ParticipantId, Boss, "skill-a", amount);
    private static CombatBuffEvent Effect(CombatBuffAction action, CombatParticipant source) => new("buff-a", action,
        TargetParticipantId: One.ParticipantId, SourceParticipantId: source.ParticipantId, InitialStateKnown: true);
    private static long Ticks(int seconds) => TimeSpan.FromSeconds(seconds).Ticks;
}
