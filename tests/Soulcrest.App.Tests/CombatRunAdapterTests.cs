using Soulcrest.App.Services;
using Soulcrest.Core.Combat;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class CombatRunAdapterTests
{
    [Fact]
    public void ExplicitJoinAndAmountFactStartAttemptAndSeparateProofEndsAtDeath()
    {
        var f = new Fixture(); f.Ready(); f.Hit(3); f.Death(13); f.Reward(15);
        var attempt = Assert.Single(f.Controller.Snapshot.CurrentRun!.Attempts);
        Assert.Equal(CombatEndReason.Victory, attempt.EndReason);
        Assert.Equal(TimeSpan.FromSeconds(10).Ticks, attempt.DurationTicks);
        Assert.Equal(100, CombatProjection.Project(attempt).Damage);
        Assert.Null(CombatProjection.Project(attempt).Dps); // An empirical subset never promises full DPS.
        Assert.Equal(1, f.Adapter.Snapshot.EndProofs);
        Assert.Null(f.Controller.Snapshot.CurrentRun.InstanceKey); // RawMapKey is deliberately not used.
    }

    [Fact]
    public void EmptyProductionCatalogAndMissingOwnDoNotCreateRuns()
    {
        var f = new Fixture(emptyCatalog: true); f.Ready(); f.Hit(3); f.Death(10); f.Reward(11);
        Assert.Null(f.Controller.Snapshot.CurrentRun);
        Assert.Equal(1, f.Adapter.Snapshot.UnconfirmedDungeons);
        var other = new Fixture();
        other.Send(new CombatWireParty(Fixture.Party), 0, noWorld: true);
        other.Send(new CombatWireDungeonState(new(42, DungeonRunState.Process, 100, 99999)), 1);
        other.Hit(2);
        Assert.Null(other.Controller.Snapshot.CurrentRun);
    }

    [Fact]
    public void ZeroHpPartyDeathsSilenceAndUnboundRewardDoNotProveWipeOrVictory()
    {
        var f = new Fixture(); f.Ready(); f.Hit(3);
        f.Send(new CombatWireHp(new(900, 0, 10000)), 4);
        f.Send(new CombatWireDeath(new(101, 900, 10, 0, 1, 2)), 5);
        f.Send(new CombatWireDeath(new(102, 900, 10, 0, 1, 2)), 6);
        f.Reward(7);
        f.Controller.Advance(TimeSpan.FromHours(1).Ticks);
        Assert.NotNull(f.Controller.Snapshot.ActiveAttemptId);
        Assert.Null(f.Controller.Snapshot.CurrentRun!.Attempts[0].EndReason);
        Assert.Equal(0, f.Adapter.Snapshot.EndProofs);
    }

    [Fact]
    public void FailedDungeonResultInterruptsRatherThanInventingWipe()
    {
        var f = new Fixture(); f.Ready(); f.Hit(3);
        f.Send(new CombatWireDungeonResult(new(42, false, null, new Dictionary<int, long>(), null)), 10);
        Assert.Equal(CombatEndReason.Interrupted, Assert.Single(f.Controller.Snapshot.CurrentRun!.Attempts).EndReason);
    }

    [Fact]
    public void GapCannotBeRepairedByOldRewardOrSameMapKey()
    {
        var f = new Fixture(); f.Ready(); f.Hit(3); f.Death(8);
        var first = f.Controller.Snapshot.CurrentRun!.Id;
        f.Adapter.Boundary(new(100, Fixture.Source, "Gap", TimeSpan.FromSeconds(9).Ticks, Fixture.Start.AddSeconds(9)));
        f.Reward(10);
        Assert.Equal(0, f.Adapter.Snapshot.EndProofs);
        var old = f.Controller.DrainCheckpoints().Single(r => r.Id == first);
        Assert.Equal(CombatEndReason.Interrupted, old.Attempts[0].EndReason);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DuplicateCandidateOrReusedEntityInvalidatesAttempt(bool sameEntity)
    {
        var f = new Fixture(); f.Ready(); f.Hit(3);
        f.Send(Fixture.Npc(sameEntity ? 900 : 901), 4);
        Assert.Equal(CombatEndReason.Interrupted, f.Controller.Snapshot.CurrentRun!.Attempts[0].EndReason);
        f.Reward(6);
        Assert.Equal(0, f.Adapter.Snapshot.EndProofs);
    }

    [Fact]
    public void UnknownAmountsStayInLocalTimelineWithoutErasingRoster()
    {
        var f = new Fixture(); f.Ready(); f.Hit(3);
        f.Hit(4, effect: 99); f.Hit(5);
        var attempt = f.Controller.Snapshot.CurrentRun!.Attempts[0];
        var projection = CombatProjection.Project(attempt, TimeSpan.FromSeconds(5).Ticks);
        Assert.Equal(200, projection.Damage);
        Assert.Single(projection.Timeline, e => e.Event is CombatUnclassifiedAmountEvent);
        Assert.Contains(projection.QualityIssues, q => q.Code == "amount-semantics-unconfirmed");
    }

    [Fact]
    public void TerminalDungeonProofRequiresMatchingDungeonAndExplicitDeath()
    {
        var f = new Fixture(terminal: true); f.Ready(); f.Hit(3); f.Death(8);
        f.Send(new CombatWireDungeonResult(new(43, true, null, new Dictionary<int, long>(), null)), 9);
        Assert.NotNull(f.Controller.Snapshot.ActiveAttemptId);
        f.Send(new CombatWireDungeonResult(new(42, true, null, new Dictionary<int, long>(), null)), 10);
        Assert.Equal(CombatEndReason.Victory, f.Controller.Snapshot.CurrentRun!.Attempts[0].EndReason);
        Assert.NotNull(f.Controller.Snapshot.CurrentRun.EndedAtUtc);
    }

    [Fact]
    public void DeathDepartureReappearanceCannotReuseTheOldVictoryCandidate()
    {
        var f = new Fixture(); f.Ready(); f.Hit(3); f.Death(8);
        f.Send(new CombatWireNpcDeparture(new(900, 1, null)), 9);
        f.Send(Fixture.Npc(900), 10); f.Reward(11);
        Assert.Equal(0, f.Adapter.Snapshot.EndProofs);
        Assert.Equal(CombatEndReason.Interrupted, f.Controller.Snapshot.CurrentRun!.Attempts[0].EndReason);
    }

    [Fact]
    public void RegressingBoundarySequenceStillRevokesActiveMeasurement()
    {
        var f = new Fixture(); f.Ready(); f.Hit(3);
        f.Adapter.Boundary(new(1, null, "invalid-observation-order", 0, Fixture.Start));
        Assert.Null(f.Controller.ActiveAttemptId);
        Assert.Equal(CombatEndReason.Interrupted, f.Controller.Snapshot.CurrentRun!.Attempts[0].EndReason);
    }

    [Fact]
    public void RosterCannotTurnABossEntityIntoGroupDamage()
    {
        var f = new Fixture(); f.Ready(); f.Hit(3);
        f.Send(new CombatWireParty(Fixture.Party with { Members = [Fixture.Party.Members[0], Fixture.Party.Members[1] with { EntityKey = 900 }] }), 4);
        f.Hit(5); f.Death(8); f.Reward(9);
        Assert.Equal(0, f.Adapter.Snapshot.EndProofs);
        Assert.Equal(100, CombatProjection.Project(f.Controller.Snapshot.CurrentRun!.Attempts[0]).Damage);
        Assert.Equal(CombatEndReason.Interrupted, f.Controller.Snapshot.CurrentRun.Attempts[0].EndReason);
    }

    [Fact]
    public void ReplacementEntityAfterDepartureCannotReuseAHeldVictoryProof()
    {
        var f = new Fixture(); f.Ready(); f.Hit(3); f.Death(8);
        f.Send(new CombatWireNpcDeparture(new(900, 1, null)), 9);
        f.Send(Fixture.Npc(901), 10); f.Reward(11);
        Assert.Equal(0, f.Adapter.Snapshot.EndProofs);
        Assert.Equal(CombatEndReason.Interrupted, f.Controller.Snapshot.CurrentRun!.Attempts[0].EndReason);
    }

    [Fact]
    public void MissingFreshWorldRevokesTheCachedMapAndActiveMeasurement()
    {
        var f = new Fixture(); f.Ready(); f.Hit(3);
        f.Send(new CombatWireHit(new(900, 101, 10, 11, 100, false, null, null, 0)), 4, noWorld: true);
        f.Hit(5); f.Death(8); f.Reward(9);
        Assert.Equal(0, f.Adapter.Snapshot.EndProofs);
        Assert.Equal(CombatEndReason.Interrupted, f.Controller.Snapshot.CurrentRun!.Attempts[0].EndReason);
        Assert.Equal(100, CombatProjection.Project(f.Controller.Snapshot.CurrentRun.Attempts[0]).Damage);
    }

    [Fact]
    public void CatalogAndAmountVersionsRemainIndependentInTheHistoricalProfile()
    {
        var f = new Fixture(); f.Ready(); f.Hit(3);
        var profile = f.Controller.Snapshot.CurrentRun!.Attempts[0].MeasurementProfile;
        Assert.Equal("synthetic-catalog-version", profile.CatalogVersion);
        Assert.Equal("synthetic-rules|amount:synthetic-amount-proof", profile.RulesetVersion);
    }

    [Fact]
    public void SingleNpcRulesCannotSilentlyCreateACombinedMultiNpcEncounter()
    {
        var definition = new CombatEncounterDefinition("synthetic-boss", null, CombatAttemptKind.Boss,
            CombatCapabilities.None, new("synthetic-rules"));
        Assert.Throws<ArgumentException>(() => new CombatBossCatalog([
            new(42, 420, 700, definition, CombatBossVictoryProof.None, "synthetic-proof"),
            new(42, 420, 701, definition, CombatBossVictoryProof.None, "synthetic-proof")]));
    }

    [Fact]
    public void HitAfterDeathAndDepartureCannotBeHiddenBeforeTheTerminalGuard()
    {
        var f = new Fixture(); f.Ready(); f.Hit(3); f.Death(8);
        f.Send(new CombatWireNpcDeparture(new(900, 1, null)), 9);
        f.Hit(10); f.Reward(11);
        Assert.Equal(0, f.Adapter.Snapshot.EndProofs);
        Assert.Equal(CombatEndReason.Interrupted, f.Controller.Snapshot.CurrentRun!.Attempts[0].EndReason);
    }

    [Fact]
    public void RejectedParentShapeStillInvalidatesTheOldDeathCandidate()
    {
        var f = new Fixture(); f.Ready(); f.Hit(3); f.Death(8);
        f.Send(new CombatWireNpcDeparture(new(900, 1, null)), 9);
        f.Send(new CombatWireNpcAppearance(Fixture.Npc(901).Value with { ParentEntityKey = 777 }), 10);
        f.Reward(11);
        Assert.Equal(0, f.Adapter.Snapshot.EndProofs);
        Assert.Equal(CombatEndReason.Interrupted, f.Controller.Snapshot.CurrentRun!.Attempts[0].EndReason);
    }

    [Fact]
    public void PrepareAfterProcessRevokesOldRunWithoutInventingWipe()
    {
        var f = new Fixture(); f.Ready(); f.Hit(3);
        f.Send(new CombatWireDungeonState(new(42, DungeonRunState.Prepare, 0, 30000)), 4);
        f.Hit(5); f.Death(8); f.Reward(9);
        Assert.Equal(CombatEndReason.Interrupted, f.Controller.Snapshot.CurrentRun!.Attempts[0].EndReason);
        Assert.Equal(0, f.Adapter.Snapshot.EndProofs);
    }

    [Fact]
    public void RepeatedProcessKeepsTheSameSharedAttempt()
    {
        var f = new Fixture(); f.Ready(); f.Hit(3);
        var id = f.Controller.ActiveAttemptId;
        f.Send(new CombatWireDungeonState(new(42, DungeonRunState.Process, 0, 3600000)), 4);
        f.Hit(5);
        Assert.Equal(id, f.Controller.ActiveAttemptId);
        Assert.Equal(200, CombatProjection.Project(f.Controller.Snapshot.CurrentRun!.Attempts[0], TimeSpan.FromSeconds(3).Ticks).Damage);
    }

    private sealed class Fixture
    {
        public static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public static readonly CombatSourceContext Source = new("synthetic-stream", 1);
        public static readonly CombatPartySnapshot Party = new(1, 1, 5,
            [new(0, 101, 7, 7, 1, "Player One", 1, 1000, 100, 100, true, true),
             new(1, 102, 7, 7, 2, "Player Two", 2, 2000, 100, 100, true, true)], 0);
        private static readonly OwnWorldObservation World = new(new(1, 7, 101, "Player One"), 123456, 420);
        public CombatRunController Controller { get; } = new();
        public CombatRunAdapter Adapter { get; }
        private long _sequence;
        public Fixture(bool emptyCatalog = false, bool terminal = false)
        {
            var definition = new CombatEncounterDefinition("synthetic-boss", "normal", CombatAttemptKind.Boss,
                CombatCapabilities.Damage | CombatCapabilities.Healing | CombatCapabilities.Deaths, new("synthetic-rules", CatalogVersion: "synthetic-catalog-version"));
            var catalog = emptyCatalog ? CombatBossCatalog.Empty : new CombatBossCatalog([
                new(42, 420, 700, definition, terminal ? CombatBossVictoryProof.DungeonSuccess : CombatBossVictoryProof.MiddleBossReward,
                    "synthetic-catalog-proof", "synthetic-scope")]);
            var classifier = new CombatAmountClassifier("synthetic-amount-proof", "synthetic-scope",
                [new(false, 11, CombatAmountKind.Damage, 4, 2), new(false, 12, CombatAmountKind.Healing, 4, 2)]);
            Adapter = new(Controller, catalog, (_, _) => true, classifier);
        }
        public void Ready()
        {
            Send(new CombatWireParty(Party), 0);
            Send(new CombatWireDungeonState(new(42, DungeonRunState.Process, 100, 3600100)), 1);
            Send(Npc(900), 2);
        }
        public void Hit(int at, uint effect = 11) => Send(new CombatWireHit(new(900, 101, 10, effect, 100, false, null, null, 0)), at);
        public void Death(int at) => Send(new CombatWireDeath(new(900, 101, 10, 0, 1, 2)), at);
        public void Reward(int at) => Send(new CombatWireMiddleBossReward(new(700)), at);
        public void Send(CombatWireObservation observation, int at, bool noWorld = false) => Adapter.Observe(
            new(++_sequence, TimeSpan.FromSeconds(at).Ticks, Start.AddSeconds(at), Source, 1, observation), noWorld ? null : World);
        public static CombatWireNpcAppearance Npc(int entity) => new(new(entity, 700, 10000, 10000, 0, null, null, null, null, null, 0, 0));
    }
}
