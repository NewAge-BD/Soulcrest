using Soulcrest.Core.Combat;
using Xunit;

namespace Soulcrest.Core.Tests;

/// <summary>Synthetic confirmed contracts only, without captures, actual identities or a wall clock.</summary>
public sealed class CombatRunControllerTests
{
    private static readonly DateTimeOffset Utc = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly CombatSourceContext Context = new("synthetic-stream", 1);
    private static readonly CombatEntityReference Boss = new("synthetic-boss", 1, Context.StreamId);
    private static readonly CombatParticipant One = new("synthetic-one", "Synthetic One", CombatPower: 100);
    private static readonly CombatParticipant Two = new("synthetic-two", "Synthetic Two", CombatPower: 200);
    private static readonly CombatParticipant Three = new("synthetic-three", "Synthetic Three", CombatPower: 300);
    private static readonly CombatEncounterDefinition Definition = new("synthetic-boss-kind", null,
        CombatAttemptKind.Boss, CombatCapabilities.Damage | CombatCapabilities.Deaths | CombatCapabilities.Healing,
        new("synthetic-rules", DecoderVersion: "synthetic-decoder"));

    [Fact]
    public void OnlyPositiveConfirmedGroupBossDamageStartsAndAllSetupMayShareOneStamp()
    {
        var c = new CombatRunController();
        var first = Stamp(1, 0);
        Assert.True(c.ConfirmDungeon(first, "synthetic-dungeon", "confirmed-instance"));
        Assert.True(c.SetRoster(first, [One, Two]));
        Assert.True(c.BindEncounter(first, Definition, [Boss]));
        Assert.False(c.Observe(first, new CombatHpEvent(Boss, 0, 1000)));
        Assert.False(c.Observe(first, Hit(0)));
        Assert.False(c.Observe(first, Hit(100) with { SourceParticipantId = "outside-group" }));
        Assert.False(c.Observe(first, Hit(100) with { Target = Boss with { EntityId = "ordinary-add" } }));
        Assert.Null(c.Snapshot.ActiveAttemptId);
        Assert.True(c.Observe(first, Hit(100)));
        var attempt = Assert.Single(c.Snapshot.CurrentRun!.Attempts);
        Assert.Equal(new[] { One, Two }, attempt.Participants);
        Assert.Equal(0, Assert.Single(attempt.Events).OffsetTicks);
        Assert.Null(attempt.EndReason);
    }

    [Fact]
    public void SharedTimeIncludesDeathAndMechanicsAndAdvanceDoesNotRejectQueuedObservation()
    {
        var c = Ready();
        c.Observe(Stamp(2, 1), Hit(100));
        c.Observe(Stamp(3, 2), new CombatDeathEvent(One.ParticipantId));
        c.Observe(Stamp(4, 3), new CombatHpEvent(Boss, 0, 1000));
        c.Advance(Ticks(50));
        Assert.NotNull(c.Snapshot.ActiveAttemptId);
        Assert.True(c.Observe(Stamp(5, 11), Hit(100) with { SourceParticipantId = Two.ParticipantId }));
        var id = c.Snapshot.ActiveAttemptId!.Value;
        Assert.True(c.ConfirmEnd(Stamp(6, 21), id, CombatEndReason.Victory));
        var attempt = Assert.Single(c.Snapshot.CurrentRun!.Attempts);
        Assert.Equal(Ticks(20), attempt.DurationTicks);
        Assert.Equal(10m, CombatProjection.Project(attempt).Dps);
        Assert.Equal(CombatEndReason.Victory, attempt.EndReason);
    }

    [Fact]
    public void CaptureClockRegressionIsPreservedWhileStoredRunMetadataUsesMonotoneAnchor()
    {
        var c = Ready();
        c.Observe(Stamp(2, 10) with { CapturedAtUtc = Utc.AddMinutes(-5) }, Hit(100));
        var id = c.Snapshot.ActiveAttemptId!.Value;
        c.ConfirmEnd(Stamp(3, 20) with { CapturedAtUtc = Utc.AddMinutes(-10) }, id, CombatEndReason.Victory);
        c.CloseDungeon(Stamp(4, 30) with { CapturedAtUtc = Utc.AddMinutes(-20) });
        var run = c.Snapshot.CurrentRun!;
        var attempt = Assert.Single(run.Attempts);
        Assert.Equal(Utc.AddSeconds(10), attempt.StartedAtUtc);
        Assert.Equal(Utc.AddMinutes(-5), Assert.Single(attempt.Events).CapturedAtUtc);
        Assert.Equal(Utc.AddSeconds(30), run.EndedAtUtc);
        Assert.True(run.EndedAtUtc >= attempt.StartedAtUtc.AddTicks(attempt.DurationTicks!.Value));
    }

    [Fact]
    public void SameWireStampCanProduceSeveralJournalEventsButStaleOrConflictingInputIsRejected()
    {
        var c = Ready();
        var wire = Stamp(2, 1);
        Assert.True(c.Observe(wire, Hit(100)));
        Assert.True(c.Observe(wire, Hit(100)));
        Assert.True(c.Observe(wire, new CombatHealEvent(One.ParticipantId, Two.ParticipantId, "synthetic-heal", 50)));
        Assert.False(c.Observe(Stamp(1, 0), Hit(999)));
        Assert.False(c.Observe(wire with { CapturedAtUtc = wire.CapturedAtUtc.AddTicks(1) }, Hit(999)));
        var events = c.Snapshot.CurrentRun!.Attempts.Single().Events;
        Assert.Equal(3, events.Length);
        Assert.Equal(3, events.Select(item => item.Sequence).Distinct().Count());
        Assert.True(events.Zip(events.Skip(1)).All(pair => pair.First.Sequence < pair.Second.Sequence));
    }

    [Fact]
    public void RosterSwitchPreservesInitialParticipantsAndReturnedArraysCannotRewriteHistory()
    {
        var c = new CombatRunController();
        var roster = new[] { One, Two };
        c.ConfirmDungeon(Stamp(1, 0), "synthetic-dungeon", "instance");
        c.SetRoster(Stamp(1, 0), roster);
        c.BindEncounter(Stamp(1, 0), Definition, [Boss]);
        roster[0] = Three;
        c.Observe(Stamp(2, 1), Hit(100));
        c.SetRoster(Stamp(3, 2), [One, Three]);
        c.Observe(Stamp(4, 3), Hit(100) with { SourceParticipantId = Three.ParticipantId });
        var original = c.Snapshot.CurrentRun!.Attempts.Single();
        Assert.Equal(new[] { One, Two }, original.Participants);
        original.Participants[0] = Three;
        Assert.IsType<CombatRosterEvent>(original.Events[1].Event).Members[0] = Two;
        original.Events[0] = original.Events[0] with { Event = Hit(9999) };
        var untouched = c.Snapshot.CurrentRun!.Attempts.Single();
        Assert.Equal(One, untouched.Participants[0]);
        Assert.Equal(One, Assert.IsType<CombatRosterEvent>(untouched.Events[1].Event).Members[0]);
        Assert.Equal(100, Assert.IsType<CombatDamageEvent>(untouched.Events[0].Event).Amount);
    }

    [Theory]
    [InlineData(CombatEndReason.Wipe)]
    [InlineData(CombatEndReason.Reset)]
    public void ExplicitRetryCreatesNewAttemptAndCannotRewriteCompletedMeasurement(CombatEndReason reason)
    {
        var c = Ready();
        c.Observe(Stamp(2, 1), Hit(100));
        var firstId = c.Snapshot.ActiveAttemptId!.Value;
        var proof = Stamp(3, 5);
        Assert.True(c.ConfirmEnd(proof, firstId, reason));
        Assert.False(c.BindEncounter(proof, Definition, [Boss]));
        Assert.False(c.ConfirmEnd(Stamp(4, 6), firstId, CombatEndReason.Victory));
        Assert.True(c.BindEncounter(Stamp(5, 7), Definition, [Boss]));
        Assert.True(c.Observe(Stamp(5, 7), Hit(100)));
        var second = c.Snapshot.ActiveAttemptId!.Value;
        Assert.NotEqual(firstId, second);
        Assert.False(c.BindEncounter(Stamp(6, 8), Definition with { Capabilities = CombatCapabilities.None }, [Boss]));
        var run = c.Snapshot.CurrentRun!;
        Assert.Equal(2, run.Attempts.Length);
        Assert.Equal(reason, run.Attempts[0].EndReason);
        Assert.Equal(Ticks(4), run.Attempts[0].DurationTicks);
        Assert.Equal(Definition.Capabilities, run.Attempts[1].Capabilities);
        Assert.False(c.Snapshot.IsResultVisible);
    }

    [Fact]
    public void KnownInstanceResumesAfterInterruptionOnlyWithFreshContextRosterAndBinding()
    {
        var c = Ready();
        c.Observe(Stamp(2, 1), Hit(100));
        var runId = c.Snapshot.CurrentRun!.Id;
        c.Interrupt(Stamp(3, 5), CombatGapReason.Transport);
        var ended = c.Snapshot.CurrentRun!.Attempts.Single();
        Assert.Equal(CombatEndReason.Interrupted, ended.EndReason);
        Assert.IsType<CombatGapEvent>(ended.Events[^1].Event);
        Assert.True(ended.HasIncompleteJournal);
        var nextContext = Context with { Generation = 2 };
        var fresh = Stamp(4, 6) with { Context = nextContext };
        var nextBoss = Boss with { Generation = 2 };
        Assert.False(c.Observe(fresh, Hit(999)));
        Assert.True(c.ConfirmDungeon(fresh, "synthetic-dungeon", "instance"));
        Assert.Equal(runId, c.Snapshot.CurrentRun!.Id);
        Assert.False(c.Observe(fresh, Hit(999) with { Target = nextBoss }));
        c.SetRoster(fresh, [One, Two]);
        c.BindEncounter(fresh, Definition, [nextBoss]);
        Assert.False(c.Observe(fresh, Hit(999)));
        Assert.True(c.Observe(fresh, Hit(100) with { Target = nextBoss }));
        Assert.Equal(2, c.Snapshot.CurrentRun!.Attempts.Length);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("instance-a", "instance-b")]
    public void SameDungeonKeyCannotJoinUnknownOrDifferentInstancesAcrossGap(string? before, string? after)
    {
        var c = Ready(before);
        c.Observe(Stamp(2, 1), Hit(100));
        var old = c.Snapshot.CurrentRun!.Id;
        c.Interrupt(Stamp(3, 5), CombatGapReason.Transport);
        c.ConfirmDungeon(Stamp(4, 6), "synthetic-dungeon", after);
        Assert.NotEqual(old, c.Snapshot.CurrentRun!.Id);
        var checkpoints = c.DrainCheckpoints();
        Assert.Equal(2, checkpoints.Count);
        Assert.NotNull(checkpoints.Single(run => run.Id == old).EndedAtUtc);
    }

    [Fact]
    public void DelayedExactEndUsesDeathTimeButRefusesToTruncateAlreadyAcceptedEvents()
    {
        var c = Ready();
        c.Observe(Stamp(2, 1), Hit(100));
        c.Observe(Stamp(3, 8), new CombatHpEvent(Boss, 0, 1000));
        var id = c.Snapshot.ActiveAttemptId!.Value;
        Assert.False(c.ConfirmEnd(Stamp(4, 10), id, CombatEndReason.Victory, Ticks(7)));
        Assert.Equal(2, c.Snapshot.CurrentRun!.Attempts.Single().Events.Length);
        Assert.True(c.ConfirmEnd(Stamp(4, 10), id, CombatEndReason.Victory, Ticks(8)));
        Assert.Equal(Ticks(7), c.Snapshot.CurrentRun!.Attempts.Single().DurationTicks);
    }

    [Fact]
    public void ResultStaysSixtySecondsAndNewPullReplacesItImmediately()
    {
        var c = Ready();
        c.Observe(Stamp(2, 1), Hit(100));
        c.ConfirmEnd(Stamp(3, 10), c.Snapshot.ActiveAttemptId!.Value, CombatEndReason.Victory);
        c.Advance(Ticks(69));
        Assert.True(c.Snapshot.IsResultVisible);
        Assert.Equal(Ticks(1), c.Snapshot.ResultRemainingTicks);
        c.Advance(Ticks(70));
        Assert.False(c.Snapshot.IsResultVisible);
        c.BindEncounter(Stamp(4, 71), Definition, [Boss]);
        c.Observe(Stamp(4, 71), Hit(100));
        c.ConfirmEnd(Stamp(5, 72), c.Snapshot.ActiveAttemptId!.Value, CombatEndReason.Victory);
        c.SetResultDisplayDuration(TimeSpan.FromSeconds(5));
        Assert.Equal(Ticks(5), c.Snapshot.ResultRemainingTicks);
        c.BindEncounter(Stamp(6, 73), Definition, [Boss]);
        c.Observe(Stamp(6, 73), Hit(100));
        Assert.False(c.Snapshot.IsResultVisible);
        Assert.NotNull(c.Snapshot.DisplayAttempt);
        Assert.Equal(c.Snapshot.ActiveAttemptId, c.Snapshot.DisplayAttempt!.Id);
    }

    [Fact]
    public void OverflowReservesOneGapAndEndsPartialWithoutUnboundedEvents()
    {
        var c = Ready(options: new() { MaxEventsPerAttempt = 4, MaxEventsPerRun = 4 });
        c.Observe(Stamp(2, 1), Hit(100));
        c.Observe(Stamp(3, 2), new CombatCastEvent(One.ParticipantId, "synthetic-skill"));
        c.Observe(Stamp(4, 3), Hit(100));
        Assert.False(c.Observe(Stamp(5, 4), Hit(100)));
        var attempt = c.Snapshot.CurrentRun!.Attempts.Single();
        Assert.Equal(4, attempt.Events.Length);
        Assert.Equal(CombatGapReason.QueueOverflow, Assert.IsType<CombatGapEvent>(attempt.Events[^1].Event).Reason);
        Assert.Equal(CombatEndReason.Interrupted, attempt.EndReason);
        Assert.Null(c.Snapshot.ActiveAttemptId);
        Assert.False(CombatProjection.Project(attempt).IsComplete);
    }

    [Fact]
    public void PendingRunLimitRequiresDrainAndNeverDropsAnOlderCheckpoint()
    {
        var c = new CombatRunController(new() { MaxPendingRuns = 2 });
        c.ConfirmDungeon(Stamp(1, 0), "synthetic-dungeon", "one");
        c.ConfirmDungeon(Stamp(2, 1), "synthetic-dungeon", "two");
        var current = c.Snapshot.CurrentRun!.Id;
        Assert.Throws<InvalidOperationException>(() => c.ConfirmDungeon(Stamp(3, 2), "synthetic-dungeon", "three"));
        Assert.Equal(current, c.Snapshot.CurrentRun!.Id);
        Assert.Equal(2, c.DrainCheckpoints().Count);
        Assert.True(c.ConfirmDungeon(Stamp(3, 2), "synthetic-dungeon", "three"));
        Assert.Equal(2, c.DrainCheckpoints().Count);
        Assert.Empty(c.DrainCheckpoints());
    }

    [Fact]
    public void AttemptLimitIsExplicitAndDoesNotModifyTheCompletedAttempt()
    {
        var c = Ready(options: new() { MaxAttemptsPerRun = 1 });
        c.Observe(Stamp(2, 1), Hit(100));
        c.ConfirmEnd(Stamp(3, 2), c.Snapshot.ActiveAttemptId!.Value, CombatEndReason.Reset);
        c.BindEncounter(Stamp(4, 3), Definition, [Boss]);
        Assert.False(c.Observe(Stamp(4, 3), Hit(100)));
        Assert.Equal("attempt-limit", c.Snapshot.LimitReason);
        Assert.Single(c.Snapshot.CurrentRun!.Attempts);
        Assert.Equal(CombatEndReason.Reset, c.Snapshot.CurrentRun.Attempts[0].EndReason);
    }

    [Fact]
    public void PrePullIncompleteQualityIsJournaledWithoutLosingTheConfirmedInitialRoster()
    {
        var c = Ready();
        c.MarkIncomplete(Stamp(2, 1), CombatGapReason.LateStart);
        c.Observe(Stamp(3, 2), Hit(100));
        var attempt = c.Snapshot.CurrentRun!.Attempts.Single();
        Assert.Equal(new[] { typeof(CombatGapEvent), typeof(CombatRosterEvent), typeof(CombatDamageEvent) },
            attempt.Events.Select(item => item.Event.GetType()));
        Assert.Equal(100, CombatProjection.Project(attempt, Ticks(1)).Damage);
        Assert.False(CombatProjection.Project(attempt, Ticks(1)).IsComplete);
    }

    [Fact]
    public void UnclassifiedAmountDuringFightMarksQualityWithoutInventingTotalsOrRemovingRoster()
    {
        var c = Ready();
        var unknown = new CombatUnclassifiedAmountEvent(One.ParticipantId, null, Boss, null, "synthetic-effect", null, false);
        Assert.False(c.Observe(Stamp(2, 1), unknown));
        c.Observe(Stamp(3, 2), Hit(100));
        Assert.True(c.Observe(Stamp(4, 3), unknown));
        Assert.True(c.Snapshot.HasFreshRoster);
        Assert.True(c.Observe(Stamp(5, 4), Hit(100)));
        var attempt = c.Snapshot.CurrentRun!.Attempts.Single();
        var summary = CombatProjection.Project(attempt, Ticks(3));
        Assert.Equal(200, summary.Damage);
        Assert.False(summary.IsComplete);
    }

    [Fact]
    public void DungeonSuccessCannotManufactureSpecificBossVictory()
    {
        var c = Ready();
        c.Observe(Stamp(2, 1), Hit(100));
        c.ConfirmedDungeonResult(Stamp(3, 2), true);
        var run = c.Snapshot.CurrentRun!;
        Assert.NotNull(run.EndedAtUtc);
        Assert.Equal(CombatEndReason.Interrupted, Assert.Single(run.Attempts).EndReason);
    }

    [Fact]
    public void ForeignContextCannotInterruptCloseEndOrAdvanceTheActiveAttempt()
    {
        var c = Ready();
        c.Observe(Stamp(2, 1), Hit(100));
        var attemptId = c.ActiveAttemptId!.Value;
        var foreign = Stamp(999, 999) with { Context = new("another-synthetic-stream", 7) };
        Assert.False(c.Interrupt(foreign, CombatGapReason.Transport));
        Assert.False(c.CloseDungeon(foreign));
        Assert.False(c.ConfirmEnd(foreign, attemptId, CombatEndReason.Victory));
        Assert.False(c.Observe(foreign, Hit(999)));
        Assert.Equal(attemptId, c.ActiveAttemptId);
        Assert.Equal(0, c.Snapshot.DisplayDurationTicks);
        Assert.True(c.ConfirmEnd(Stamp(3, 2), attemptId, CombatEndReason.Victory));
    }

    [Fact]
    public void ImpossibleMaximumHpCannotEnterTheJournal()
    {
        var c = Ready();
        c.Observe(Stamp(2, 1), Hit(100));
        Assert.False(c.Observe(Stamp(3, 2), new CombatHpEvent(Boss, 1001, 1000)));
        Assert.Single(c.Snapshot.CurrentRun!.Attempts.Single().Events);
    }

    private static CombatRunController Ready(string? instance = "instance", CombatRunControllerOptions? options = null)
    {
        var c = new CombatRunController(options);
        var stamp = Stamp(1, 0);
        c.ConfirmDungeon(stamp, "synthetic-dungeon", instance);
        c.SetRoster(stamp, [One, Two]);
        c.BindEncounter(stamp, Definition, [Boss]);
        return c;
    }
    private static CombatControllerStamp Stamp(long sequence, int seconds) => new(sequence, Ticks(seconds), Utc.AddSeconds(seconds), Context);
    private static long Ticks(int seconds) => TimeSpan.FromSeconds(seconds).Ticks;
    private static CombatDamageEvent Hit(long amount) => new(One.ParticipantId, Boss, "synthetic-skill", amount);
}
