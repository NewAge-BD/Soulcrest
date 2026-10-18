using System.Collections.Concurrent;
using System.Text;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Combat;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class CombatNetworkServiceTests
{
    [Fact]
    public async Task RealDecodersRequireOwnAndRosterBeforePublishingGroupObservations()
    {
        using var f = new Fixture();
        f.Send(Party());
        await Until(() => f.Service.Snapshot.Unbound == 1);
        Assert.Empty(f.Observations);
        f.Send(Own()); f.Send(Party()); f.Send(Status()); f.Send(Hit(302, 900));
        await Until(() => f.Observations.Count == 3);
        Assert.Equal(1, f.Service.Snapshot.ConfirmedRosters);
        var roster = Assert.IsType<CombatWireParty>(f.Observations.First().Observation);
        Assert.Equal(2, roster.Value.Members.Count); // Own is already present; never add a third member.
        var hit = Assert.IsType<CombatWireHit>(f.Observations.Last().Observation);
        Assert.Equal(100, hit.Value.Amount); // A neutral amount, not an attributed damage event.
        Assert.Null(hit.Value.RawHitFlags);
        Assert.Equal(0, f.Source.Starts); // Observing the existing source starts no recorder.
    }

    [Fact]
    public async Task GapsAndWorldEntryRevokeRosterAuthorityAndInvalidateOldBossBindings()
    {
        using var f = new Fixture();
        f.Send(Own()); f.Send(Party());
        await Until(() => f.Observations.Count == 1);
        var first = f.Observations.Last().Context;
        Assert.True(f.Service.SetConfirmedBossTargets(first, [900]));
        Assert.NotNull(f.Service.GetConfirmedWorld(first));
        f.Feed.Gap("test-stream", "transport");
        f.Send(Party()); f.Send(Status());
        await Until(() => f.Service.Snapshot.Unbound == 2);
        Assert.Equal(0, f.Service.Snapshot.ConfirmedRosters);
        Assert.False(f.Service.SetConfirmedBossTargets(first, [900]));
        Assert.Null(f.Service.GetConfirmedWorld(first));
        f.Send(Own()); f.Send(Party());
        await Until(() => f.Observations.Count == 2);
        Assert.NotEqual(first, f.Observations.Last().Context);
        f.Send([0x21, 0x36]);
        await Until(() => f.Service.Snapshot.ConfirmedOwnStreams == 0);
        f.Send(Party());
        await Until(() => f.Service.Snapshot.Unbound == 3);
    }

    [Fact]
    public async Task BossHpNeedsExplicitEncounterBindingAndZeroHpIsStillOnlyAReading()
    {
        using var f = new Fixture();
        f.Send(Own()); f.Send(Party());
        await Until(() => f.Observations.Count == 1);
        f.Send(Hp(900, 0));
        await Until(() => f.Service.Snapshot.Unbound == 1);
        Assert.True(f.Service.SetConfirmedBossTargets(f.Observations.Last().Context, [900]));
        f.Send(Hp(900, 0));
        await Until(() => f.Observations.Count == 2);
        var hp = Assert.IsType<CombatWireHp>(f.Observations.Last().Observation);
        Assert.Equal(0, hp.Value.Current);
        Assert.Equal(100, hp.Value.Maximum);
        Assert.DoesNotContain(f.Boundaries, b => b.Code.Contains("Victory", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RejectedFormatsRequireFreshRosterAndANewLogicalContext()
    {
        using var f = new Fixture();
        f.Send(Own()); f.Send(Party());
        await Until(() => f.Observations.Count == 1);
        var previous = f.Observations.Last().Context;
        f.Send([0x00, 0x8D, 0x01, 0xFF]); // Unsupported point-update flags.
        f.Send(Status());
        await Until(() => f.Service.Snapshot.Rejected == 1 && f.Service.Snapshot.Unbound == 1);
        Assert.Equal(0, f.Service.Snapshot.ConfirmedRosters);
        f.Send(Party());
        await Until(() => f.Observations.Count == 2);
        Assert.NotEqual(previous, f.Observations.Last().Context);
    }

    [Fact]
    public async Task NewPartyKeyCannotReuseTheOldEncounterBinding()
    {
        using var f = new Fixture();
        f.Send(Own()); f.Send(Party());
        await Until(() => f.Observations.Count == 1);
        var previous = f.Observations.Last().Context;
        Assert.True(f.Service.SetConfirmedBossTargets(previous, [900]));
        f.Send(Party(66));
        await Until(() => f.Observations.Count == 2);
        var current = f.Observations.Last().Context;
        Assert.NotEqual(previous, current);
        Assert.False(f.Service.SetConfirmedBossTargets(previous, [900]));
        Assert.Contains(f.Boundaries, b => b.Code == "party-changed" && b.Context == previous);
        f.Send(Hp(900, 1));
        await Until(() => f.Service.Snapshot.Unbound == 1);
        Assert.True(f.Service.SetConfirmedBossTargets(current, [901]));
    }

    [Fact]
    public async Task SameAmountsAtDifferentSequencesAndRegressingCaptureUtcAreRetained()
    {
        using var f = new Fixture();
        f.Send(Own()); f.Send(Party());
        await Until(() => f.Observations.Count == 1);
        f.Send(Hit(302, 900), Fixture.At.AddSeconds(1));
        f.Send(Hit(302, 900), Fixture.At.AddSeconds(-1));
        await Until(() => f.Observations.Count == 3);
        var hits = f.Observations.Skip(1).ToArray();
        Assert.True(hits[0].Sequence < hits[1].Sequence);
        Assert.True(hits[0].ReceivedElapsedTicks < hits[1].ReceivedElapsedTicks);
        Assert.True(hits[0].CapturedAtUtc > hits[1].CapturedAtUtc);
    }

    [Theory]
    [InlineData(0x920D)]
    [InlineData(0x9222)]
    [InlineData(0x921F)]
    [InlineData(0x9219)]
    [InlineData(0x9602)]
    [InlineData(0x960A)]
    [InlineData(0x961A)]
    [InlineData(0x961B)]
    [InlineData(0x9622)]
    [InlineData(0x962E)]
    public async Task UndecodedPartyUpdatesRevokeRosterAndRequireAFreshSnapshot(int opcode)
    {
        using var f = new Fixture();
        f.Send(Own()); f.Send(Party());
        await Until(() => f.Observations.Count == 1);
        var previous = f.Observations.Last().Context;
        Assert.True(f.Service.SetConfirmedBossTargets(previous, [900]));
        f.Send(Packet((ushort)opcode, w => w.Write((byte)0)));
        f.Send(Status()); f.Send(Hp(900, 1));
        await Until(() => f.Service.Snapshot.Unbound == 2);
        Assert.Equal(0, f.Service.Snapshot.ConfirmedRosters);
        Assert.False(f.Service.SetConfirmedBossTargets(previous, [900]));
        Assert.Contains(f.Boundaries, b => b.Code == "unsupported-party-update");
        f.Send(Party());
        await Until(() => f.Observations.Count == 2);
        Assert.NotEqual(previous, f.Observations.Last().Context);
    }

    [Fact]
    public async Task UnrelatedEntitiesCannotBecomePartyUpdates()
    {
        using var f = new Fixture();
        f.Send(Own()); f.Send(Party());
        await Until(() => f.Observations.Count == 1);
        f.Send(Hit(999, 900)); f.Send(Hp(999, 1)); f.Send(Effect(999));
        await Until(() => f.Service.Snapshot.Unbound == 3);
        Assert.Single(f.Observations);
        f.Send(Effect(301));
        await Until(() => f.Observations.Count == 2);
        Assert.IsType<CombatWireEffect>(f.Observations.Last().Observation);
    }

    [Fact]
    public async Task ThrowingSubscribersDoNotStopTheWorkerOrCaptureAndDisposeFinishesIt()
    {
        using var f = new Fixture();
        f.Service.ObservationReceived += _ => throw new InvalidOperationException("synthetic subscriber");
        f.Service.SetCaptureRequired(true);
        f.Send(Own()); f.Send(Party()); f.Send(Status());
        await Until(() => f.Service.Snapshot.SubscriberFailures == 2);
        Assert.True(f.Source.Running);
        f.Service.Dispose();
        await f.Service.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(f.Source.Running);
        Assert.False(f.Service.Snapshot.Observing);
    }

    [Fact]
    public async Task NpcDiscoveryDeathAndDungeonResultsRemainSeparateScopedEvidence()
    {
        using var f = new Fixture();
        f.Send(Reward());
        await Until(() => f.Service.Snapshot.Unbound == 1);
        f.Send(Own()); f.Send(Party());
        await Until(() => f.Observations.Count == 1);
        var context = f.Observations.Last().Context;
        Assert.True(f.Service.SetConfirmedBossTargets(context, [900]));
        Assert.NotNull(CombatEncounterMessage.TryParseNpcAppearance(Npc()));
        f.Send(Npc());
        await Until(() => f.Observations.Count == 2);
        Assert.IsType<CombatWireNpcAppearance>(f.Observations.Last().Observation);
        f.Send(Hp(900, 70));
        await Until(() => f.Service.Snapshot.Unbound == 2); // Reappearance revoked the old boss binding.
        Assert.True(f.Service.SetConfirmedBossTargets(context, [900]));
        f.Send(Death(900)); f.Send(Reward());
        f.Send(Packet(0x6100, w => { w.Write(5001); w.Write((byte)2); w.Write(1L); w.Write(0L); }));
        f.Send(Packet(0x6101, w => { w.Write((byte)0); w.Write(5001); w.Write((byte)1); V(w, 0); }));
        await Until(() => f.Observations.Count == 6);
        Assert.Contains(f.Observations, item => item.Observation is CombatWireDeath);
        Assert.Contains(f.Observations, item => item.Observation is CombatWireMiddleBossReward);
        Assert.True(Assert.IsType<CombatWireDungeonResult>(f.Observations.Last().Observation).Value.Succeeded);
        f.Send(Packet(0x3642, w => { V(w, 900); w.Write((byte)0); w.Write((byte)1); }));
        await Until(() => f.Observations.Count == 7);
        f.Send(Hp(900, 0)); f.Send(Death(999));
        await Until(() => f.Service.Snapshot.Unbound == 4);
        Assert.DoesNotContain(f.Boundaries, item => item.Code.Contains("Victory", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NpcEntityCollisionCannotInheritAPlayerAttribution()
    {
        using var f = new Fixture();
        f.Send(Own()); f.Send(Party());
        await Until(() => f.Observations.Count == 1);
        var previous = f.Observations.Last().Context;
        f.Send(Npc(301)); f.Send(Hit(301, 900));
        await Until(() => f.Service.Snapshot.Unbound == 2);
        Assert.Equal(0, f.Service.Snapshot.ConfirmedRosters);
        Assert.Single(f.Observations);
        Assert.Contains(f.Boundaries, item => item.Code == "npc-roster-entity-conflict");
        Assert.Null(f.Service.GetConfirmedWorld(previous));
    }

    [Fact]
    public async Task BoundariesPreserveSourceTimeWithoutInventingPacketUtc()
    {
        using var f = new Fixture();
        f.Send(Own()); f.Send(Party());
        await Until(() => f.Observations.Count == 1);
        var last = f.Observations.Last();
        f.Feed.Gap("test-stream", "transport");
        await Until(() => f.Boundaries.Any(b => b.Code == "Gap"));
        var gap = f.Boundaries.Last(b => b.Code == "Gap");
        Assert.True(gap.ReceivedElapsedTicks >= last.ReceivedElapsedTicks);
        Assert.Null(gap.CapturedAtUtc);
    }

    [Fact]
    public async Task OrderedWireControllerAndHistoryWriterSaveAnIsolatedLocalAttempt()
    {
        var directory = Path.Combine(Path.GetTempPath(), "soulcrest-run-integration-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var f = new Fixture();
            var store = new CombatHistoryStore(directory);
            await using var writer = new CombatHistoryWriter(store);
            var controller = new CombatRunController();
            var definition = new CombatEncounterDefinition("synthetic-boss", "normal", CombatAttemptKind.Boss,
                CombatCapabilities.Deaths, new("synthetic-rules"));
            var catalog = new CombatBossCatalog([new(5001, 420, 5001, definition,
                CombatBossVictoryProof.MiddleBossReward, "synthetic-catalog", "synthetic-scope")]);
            var amounts = new CombatAmountClassifier("synthetic-amounts", "synthetic-scope",
                [new(false, 2001, CombatAmountKind.Damage, 4, 2)]);
            using var runs = new CombatRunService(f.Service, writer, controller, catalog, amounts);
            Assert.False(Directory.Exists(directory));
            f.Send(Own(420)); f.Send(Party());
            f.Send(Packet(0x6100, w => { w.Write(5001); w.Write((byte)2); w.Write(1L); w.Write(3600001L); }));
            f.Send(Npc()); f.Send(Hit(302, 900));
            await Until(() => runs.Snapshot.Controller.ActiveAttemptId is not null);
            f.Send(Hit(302, 900, 2002)); // A neutral local timeline entry; never guessed damage.
            f.Send(Death(900)); f.Send(Reward());
            await Until(() => runs.Snapshot.Adapter.EndProofs == 1);
            var id = runs.Snapshot.Controller.CurrentRun!.Id;
            await runs.FlushAsync();
            var attempt = Assert.Single(store.LoadRun(id).Run.Attempts);
            Assert.Equal(CombatEndReason.Victory, attempt.EndReason);
            Assert.Equal(100, CombatProjection.Project(attempt).Damage);
            Assert.Single(attempt.Events, e => e.Event is CombatUnclassifiedAmountEvent);
            Assert.Null(CombatProjection.Project(attempt).Dps);
            Assert.Null(writer.Fault);
            Assert.Equal(0, f.Source.Starts);
            runs.Dispose();
            await runs.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(store.LoadRun(id).Run.EndedAtUtc);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task PartySnapshotCannotInheritAnExistingBossEntityBinding()
    {
        using var f = new Fixture();
        f.Send(Own()); f.Send(Party());
        await Until(() => f.Observations.Count == 1);
        var context = f.Observations.Last().Context;
        Assert.True(f.Service.SetConfirmedBossTargets(context, [900]));
        f.Send(Party(secondEntity: 900));
        await Until(() => f.Boundaries.Any(b => b.Code == "roster-boss-entity-conflict"));
        Assert.Equal(0, f.Service.Snapshot.ConfirmedRosters);
        Assert.False(f.Service.SetConfirmedBossTargets(context, [900]));
        Assert.Single(f.Observations);
    }

    [Fact]
    public async Task RejectedWriterHandoffRetainsEveryDrainedRunForExplicitRecovery()
    {
        using var f = new Fixture();
        var directory = Path.Combine(Path.GetTempPath(), "soulcrest-run-rejected-" + Guid.NewGuid().ToString("N"));
        await using var writer = new CombatHistoryWriter(new CombatHistoryStore(directory));
        writer.Complete();
        await writer.Completion;
        var controller = new CombatRunController();
        var context = new CombatSourceContext("synthetic-stream", 1);
        var target = new CombatEntityReference("synthetic-boss", 1, context.StreamId);
        var definition = new CombatEncounterDefinition("synthetic-boss", null, CombatAttemptKind.Boss,
            CombatCapabilities.Damage, new("synthetic-rules"));
        for (var i = 1; i <= 2; i++)
        {
            var stamp = new CombatControllerStamp(i, i, Fixture.At, context);
            controller.ConfirmDungeon(stamp, "synthetic-dungeon", null);
            controller.SetRoster(stamp, [new("player")]);
            controller.BindEncounter(stamp, definition, [target]);
            controller.Observe(stamp, new CombatDamageEvent("player", target, "skill", 100));
            controller.CloseDungeon(stamp);
        }
        using var runs = new CombatRunService(f.Service, writer, controller, CombatBossCatalog.Empty);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runs.FlushAsync());
        Assert.Equal(2, runs.Snapshot.UnacceptedCheckpoints);
        Assert.Equal("history-checkpoint-rejected", runs.Snapshot.FaultCode);
        var recovered = runs.GetUnwrittenCheckpoints();
        Assert.Equal(2, recovered.Count);
        recovered[0].Attempts[0].Events[0] = recovered[0].Attempts[0].Events[0] with
            { Event = new CombatDamageEvent("player", target, "skill", 999) };
        Assert.All(runs.GetUnwrittenCheckpoints(), run => Assert.Equal(100,
            Assert.IsType<CombatDamageEvent>(run.Attempts[0].Events[0].Event).Amount));
        Assert.False(Directory.Exists(directory));
    }

    private sealed class Fixture : IDisposable
    {
        public static readonly DateTimeOffset At = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private long _ticks;
        public NetworkCombatFeed Feed { get; } = new();
        public FakeSource Source { get; } = new();
        public NetworkCaptureCoordinator Capture { get; }
        public CombatNetworkService Service { get; }
        public ConcurrentQueue<CombatNetworkObservation> Observations { get; } = new();
        public ConcurrentQueue<CombatNetworkBoundary> Boundaries { get; } = new();
        public Fixture()
        {
            Feed.ElapsedTicks = () => Interlocked.Increment(ref _ticks);
            Capture = new(Source);
            Service = new(Feed, Capture);
            Service.ObservationReceived += Observations.Enqueue;
            Service.BoundaryReceived += Boundaries.Enqueue;
        }
        public void Send(byte[] message, DateTimeOffset? at = null) => Feed.Message("test-stream", message, at ?? At);
        public void Dispose() { Service.Dispose(); Capture.Dispose(); Feed.Dispose(); }
    }

    private sealed class FakeSource : INetworkLootSource
    {
        public int Starts { get; private set; }
        public bool Running { get; private set; }
        public string Status => "test";
        public bool StatusIsError => false;
        public string? Connection => null;
        public long Packets => 0;
        public long Messages => 0;
        public IReadOnlyList<int> UnknownPets => [];
        public event Action? Changed;
        public event Action<NetworkSoul>? SoulGained { add { } remove { } }
        public void Start() { Starts++; Running = true; Changed?.Invoke(); }
        public void Stop() { Running = false; Changed?.Invoke(); }
    }

    private static async Task Until(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(3);
        while (!condition())
        {
            if (DateTime.UtcNow >= until) throw new TimeoutException("Combat worker did not reach the expected state.");
            await Task.Delay(5);
        }
    }

    // Invented protocol values. These builders use the same full wire schema as the Core decoder tests.
    private static byte[] Own(int mapDataId = 0) => Packet(0x3633, w =>
    {
        V(w, 301); w.Write(0); w.Write((byte)1); S(w, "SyntheticOne");
        w.Write((ushort)1); w.Write(0); w.Write(new byte[36]);
        V(w, 0); V(w, 0); w.Write(new byte[16]); w.Write((byte)0);
        w.Write((ushort)0); w.Write(new byte[18]); w.Write((byte)1); V(w, 0); V(w, 0); w.Write(new byte[52]);
        V(w, 0); V(w, 0); V(w, 0); w.Write(123456L); w.Write(mapDataId); w.Write(101L); V(w, 0); w.Write((byte)1);
        w.Write(new byte[32]); V(w, 0); V(w, 0);
    });
    private static byte[] Party(long key = 55, int secondEntity = 302) => Packet(0x9200, w =>
    {
        w.Write((byte)0); w.Write(key); w.Write(101L); w.Write(5); w.Write(0); V(w, 2);
        Member(w, 0, 301, 101); Member(w, 1, secondEntity, 102); w.Write((byte)0);
    });
    private static void Member(BinaryWriter w, byte slot, int entity, long id)
    {
        w.Write((byte)0); w.Write(slot); w.Write(entity); w.Write((ushort)1); w.Write((ushort)2);
        S(w, "UnusedAccount"); w.Write(id); S(w, id == 101 ? "SyntheticOne" : "SyntheticTwo"); w.Write(10);
        V(w, 70); V(w, 90); w.Write(new byte[48]);
        // Bool packing spans scalar fields and members: two true/false/true triples share this byte.
        if (slot == 0) w.Write((byte)45);
        w.Write(new byte[12]); w.Write(12345L); w.Write((byte)0);
    }
    private static byte[] Npc(uint entity = 900) => Packet(0x3641, w =>
    {
        V(w, entity); w.Write((ushort)0x1C); w.Write((byte)0); w.Write(5001);
        w.Write((ushort)0x0200); w.Write(new byte[18]); w.Write((byte)0); V(w, 70); V(w, 100);
        w.Write(new byte[52]); w.Write((byte)0); V(w, 0); w.Write((byte)0); w.Write((byte)0);
        w.Write(0); V(w, 0); w.Write(1); V(w, 0);
    });
    private static byte[] Death(uint entity) => Packet(0x8D04, w =>
    {
        V(w, entity); w.Write(2001); V(w, 302); w.Write((ushort)0); S(w, "SkippedKiller");
        S(w, "SkippedGuild"); w.Write((ushort)0); V(w, 0); w.Write(0); w.Write((byte)1); w.Write((byte)2);
    });
    private static byte[] Reward() => Packet(0x572E, w => w.Write(5001));
    private static byte[] Status() => Packet(0x921C, w =>
    { w.Write(102L); w.Write(10); w.Write(new byte[36]); S(w, "UnusedGuild"); w.Write(1); w.Write((byte)1); w.Write(12346L); w.Write((byte)0); });
    private static byte[] Hit(uint source, uint target, uint effect = 2001) => Packet(0x3804, w =>
    { V(w, target); w.Write((ushort)4); V(w, source); w.Write(100001U); w.Write((byte)0); w.Write((byte)0); w.Write(effect); w.Write(0U); V(w, 0); V(w, 100); w.Write((ushort)0); });
    private static byte[] Hp(uint target, long current) => Packet(0x8D00, w =>
    { V(w, target); w.Write((byte)2); V(w, 2); w.Write((byte)0); w.Write(current); w.Write((byte)7); w.Write(100L); });
    private static byte[] Effect(uint target) => Packet(0x382A, w =>
    { V(w, target); V(w, 1); w.Write((byte)0x11); V(w, 1); w.Write(2001); w.Write(1000L); w.Write(2000L); V(w, 302); w.Write((byte)1); w.Write((byte)0); w.Write(new byte[12]); });
    private static byte[] Packet(ushort opcode, Action<BinaryWriter> write)
    { using var data = new MemoryStream(); using var w = new BinaryWriter(data); w.Write(opcode); write(w); return data.ToArray(); }
    private static void S(BinaryWriter writer, string value)
    { var bytes = Encoding.UTF8.GetBytes(value); V(writer, (uint)bytes.Length); writer.Write(bytes); }
    private static void V(BinaryWriter writer, uint value)
    { while (value >= 128) { writer.Write((byte)(value | 128)); value >>= 7; } writer.Write((byte)value); }
}
