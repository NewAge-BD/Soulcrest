using System.Text;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Synthetic packets only. No live device, process lookup, socket probing or real user stores.</summary>
public sealed class NetworkWorldContextTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 21, 1, 0, 0, TimeSpan.Zero);
    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "soulcrest-world-context-" + Guid.NewGuid().ToString("N"));
        internal WorldContextService World { get; } = new(WorldContextTestCatalog.Create());
        internal NetworkLootService Network { get; }
        internal Fixture()
        {
            Directory.CreateDirectory(_root);
            var progress = new ProgressService(Path.Combine(_root, "progress.json"), new PetCatalog([]));
            Network = new(progress, worldContext: World);
            typeof(NetworkLootService).GetProperty(nameof(NetworkLootService.Running))!.SetValue(Network, true);
        }
        internal void Send(byte[] packet, int second = 0, string stream = "synthetic-own") =>
            Network.ProcessProgressMessage(stream, packet, At.AddSeconds(second));
        public void Dispose() { Network.Dispose(); Directory.Delete(_root, recursive: true); }
    }

    [Fact]
    public void FullOwnWithoutPrepareConfirmsMapAndLeavesExistingIdentityBindingCountersIntact()
    {
        using var f = new Fixture(); f.Send(Own(100));
        Assert.Same(f.World, f.Network.WorldContext);
        Assert.Equal(WorldContextStatus.Confirmed, f.World.Snapshot.Status);
        Assert.Equal(100, f.World.Snapshot.MapDataId);
        Assert.Equal(1, f.Network.ProgressDiagnostics.IdentityBindings);
        Assert.True(f.Network.ProgressSources.IdentityIsLive);
    }

    [Theory]
    [InlineData(0x3620)] [InlineData(0x3621)] [InlineData(0x3633)]
    public void CurrentUnknownEntryShapeRevokesWorldBeforeItsNotification(ushort opcode)
    {
        using var f = new Fixture(); f.Send(Own(100));
        f.Network.OwnCharacterChanged += () => Assert.NotEqual(WorldContextStatus.Confirmed, f.World.Snapshot.Status);
        f.Send(Packet(opcode, _ => { }));
        Assert.Null(f.World.Snapshot.MapDataId);
        Assert.Null(f.Network.OwnCharacter);
        Assert.NotEqual(WorldContextStatus.Confirmed, f.World.Snapshot.Status);
    }

    [Fact]
    public void CandidateAndPrepareCannotConfirmAndMatchingStateBeforeOwnIsNotReplayed()
    {
        using var f = new Fixture(); f.Send(Candidate());
        Assert.Equal(WorldContextStatus.Transitioning, f.World.Snapshot.Status);
        f.Send(Prepare(), 1); f.Send(State(1000), 2);
        Assert.Equal(WorldContextStatus.Transitioning, f.World.Snapshot.Status);
        f.Send(Own(200), 3);
        Assert.Null(f.World.Snapshot.DungeonId);
        Assert.Null(f.World.Snapshot.Mode);
    }

    [Fact]
    public void ForeignGapsUnknownEntriesAndDungeonStatesCannotChangeTheCurrentOwnWorld()
    {
        using var f = new Fixture(); f.Send(Own(200)); var current = f.World.Snapshot;
        f.Send(State(1000), 1, "synthetic-other");
        f.Send(Prepare(), 2, "synthetic-other");
        f.Send(Candidate(), 2, "synthetic-other");
        f.Send(Packet(0x3633, _ => { }), 3, "synthetic-other");
        f.Network.ProcessProgressGap("synthetic-other", "stream-gap", At.AddSeconds(4));
        Assert.Same(current, f.World.Snapshot);
    }

    [Fact]
    public void DungeonStateRefinesOnlyAfterOwnAndNeverCountsAsExplorationOrSourceActivity()
    {
        using var f = new Fixture(); f.Send(Own(200));
        var rejected = f.Network.ProgressDiagnostics.RejectedProgressMessages;
        f.Send(State(1000), 1);
        Assert.Equal(1000, f.World.Snapshot.DungeonId);
        Assert.Equal("normal", f.World.Snapshot.Mode);
        Assert.Null(f.Network.ProgressSources.DungeonActivityAt);
        f.Send(State(1000, DungeonRunState.End), 2);
        f.Send(Packet(0x6101, w => { w.Write((byte)0); w.Write(1000); w.Write((byte)1); V(w, 0); }), 3);
        Assert.Equal(WorldContextKind.Instance, f.World.Snapshot.Kind);
        Assert.Equal(WorldContextStatus.Confirmed, f.World.Snapshot.Status);
        Assert.Equal(rejected, f.Network.ProgressDiagnostics.RejectedProgressMessages);
    }

    [Fact]
    public void SameMapOwnStartsNewGenerationAndOldOrMismatchingStateCannotRestorePriorMode()
    {
        using var f = new Fixture(); f.Send(Own(200)); f.Send(State(1000), 1); var first = f.World.Snapshot;
        f.Send(Own(200), 3);
        Assert.True(f.World.Snapshot.Generation > first.Generation);
        Assert.Null(f.World.Snapshot.Mode);
        f.Send(State(1000), 2); // Regressing observation cannot refine the newer Own.
        f.Send(State(1002), 4); // Known dungeon on a different map.
        Assert.Null(f.World.Snapshot.DungeonId);
        f.Send(State(1001), 5);
        Assert.Equal(1001, f.World.Snapshot.DungeonId);
    }

    [Fact]
    public void RejectedOwnStateClearsOnlyRefinementAndKeepsMapIdentityAndSourceCounters()
    {
        using var f = new Fixture(); f.Send(Own(200)); f.Send(State(1000), 1);
        var identity = f.Network.OwnCharacter;
        var diagnostics = f.Network.ProgressDiagnostics;
        f.Send(Packet(0x6100, w => w.Write(1000)), 2);
        Assert.Equal(WorldContextStatus.Confirmed, f.World.Snapshot.Status);
        Assert.Equal(WorldContextKind.Instance, f.World.Snapshot.Kind);
        Assert.Equal(200, f.World.Snapshot.MapDataId);
        Assert.Null(f.World.Snapshot.DungeonId);
        Assert.Null(f.World.Snapshot.Mode);
        Assert.Equal("shared", f.World.Snapshot.Category);
        Assert.Same(identity, f.Network.OwnCharacter);
        Assert.Equal(diagnostics.RejectedProgressMessages, f.Network.ProgressDiagnostics.RejectedProgressMessages);
        Assert.Equal(diagnostics.ContextResets, f.Network.ProgressDiagnostics.ContextResets);
        Assert.True(f.Network.ProgressSources.IdentityIsLive);
    }

    [Fact]
    public void UnknownMatchingStateCannotLeaveAnOldLayerOrModeConfirmed()
    {
        using var f = new Fixture(); f.Send(Own(100)); f.Send(State(1002), 1);
        Assert.Equal(WorldContextKind.Instance, f.World.Snapshot.Kind);
        f.Send(State(9999), 2);
        Assert.Equal(WorldContextKind.OpenWorld, f.World.Snapshot.Kind);
        Assert.Equal(100, f.World.Snapshot.MapDataId);
        Assert.Null(f.World.Snapshot.DungeonId);
        Assert.Equal("field", f.World.Snapshot.Category);
    }

    [Fact]
    public void ReentrantStopFromWorldPublicationPreventsLaterOwnCallbacks()
    {
        using var f = new Fixture(); var entries = 0;
        f.Network.OwnCharacterEntered += _ => entries++;
        f.World.Changed += () => { if (f.World.Snapshot.Status == WorldContextStatus.Confirmed) f.Network.Stop(); };
        f.Send(Own(100));
        Assert.Equal(WorldContextStatus.Stale, f.World.Snapshot.Status);
        Assert.Null(f.World.Snapshot.MapDataId);
        Assert.Null(f.Network.OwnCharacter);
        Assert.Equal(0, entries);
    }

    [Fact]
    public void GapRevokesWorldBeforeReentrantCallbacksAndCannotOverwriteANewerOwn()
    {
        using var f = new Fixture(); f.Send(Own(100)); var rebound = false;
        f.Network.MerchantSessionReset += () =>
        {
            if (rebound) return;
            rebound = true; Assert.Null(f.World.Snapshot.MapDataId);
            f.Send(Own(200), 2);
        };
        f.Network.ProcessProgressGap("synthetic-own", "stream-gap", At.AddSeconds(1));
        Assert.True(rebound);
        Assert.Equal(200, f.World.Snapshot.MapDataId);
        Assert.Equal(WorldContextStatus.Confirmed, f.World.Snapshot.Status);
    }

    [Fact]
    public void StopOrManualResetAlwaysRemovesConfirmedLocation()
    {
        using var f = new Fixture(); f.Send(Own(100)); f.Network.Stop();
        Assert.Equal(WorldContextStatus.Stale, f.World.Snapshot.Status);
        f.Send(Own(100), 1); f.Network.ResetProgressContext();
        Assert.Null(f.World.Snapshot.MapDataId);
    }

    [Fact]
    public void ReentrantNewOwnDuringCandidateCannotLeaveAnOlderCandidateThatLaterExpiresTheNewWorld()
    {
        using var f = new Fixture(); f.Send(Own(100)); var rebound = false;
        f.World.Changed += () =>
        {
            if (rebound || f.World.Snapshot.Source != "world-entry-candidate") return;
            rebound = true; f.Send(Own(200), 1);
        };
        f.Send(Candidate());
        Assert.True(rebound);
        f.Send(State(1000), 121);
        Assert.Equal(WorldContextStatus.Confirmed, f.World.Snapshot.Status);
        Assert.Equal(200, f.World.Snapshot.MapDataId);
        Assert.Equal(1000, f.World.Snapshot.DungeonId);
        Assert.NotNull(f.Network.OwnCharacter);
    }

    [Fact]
    public void ReentrantNewOwnDuringPrepareDoesNotReceiveTheOlderPreparesResetCallbacks()
    {
        using var f = new Fixture(); f.Send(Own(100)); var rebound = false; var bossResets = 0;
        f.Network.BossSessionReset += () => bossResets++;
        f.World.Changed += () =>
        {
            if (rebound || f.World.Snapshot.Source != "world-entry-preparation") return;
            rebound = true; f.Send(Own(200), 1);
        };
        f.Send(Prepare());
        Assert.True(rebound);
        Assert.Equal(1, bossResets); // The newer Own, not the superseded preparation.
        Assert.Equal(200, f.World.Snapshot.MapDataId);
    }

    [Fact]
    public void ReentrantOwnAfterRejectedEntryKeepsItsHealthyStatusInsteadOfAnOlderFormatWarning()
    {
        using var f = new Fixture(); f.Send(Own(100)); var rebound = false;
        f.World.Changed += () =>
        {
            if (rebound || f.World.Snapshot.Source != "unsupported-own-entry") return;
            rebound = true; f.Send(Own(200), 1);
        };
        f.Send(Packet(0x3633, _ => { }));
        Assert.True(rebound);
        Assert.Equal(200, f.World.Snapshot.MapDataId);
        Assert.DoesNotContain("nicht unterstützt", f.Network.ProgressStatus);
    }

    private static byte[] Candidate() => Packet(0x3620, w =>
    { w.Write((ushort)0); V(w, 300); w.Write(new byte[4]); w.Write(101L); });
    private static byte[] Prepare() => Packet(0x3621, w => w.Write(new byte[46]));
    private static byte[] State(int dungeon, DungeonRunState state = DungeonRunState.Process) => Packet(0x6100, w =>
    { w.Write(dungeon); w.Write((byte)state); w.Write(1L); w.Write(9999L); });
    private static byte[] Own(int map) => Packet(0x3633, w =>
    {
        V(w, 300); w.Write(0); w.Write((byte)1);
        var name = Encoding.UTF8.GetBytes("SyntheticAlpha"); V(w, (uint)name.Length); w.Write(name);
        w.Write((ushort)501); w.Write(31); w.Write(new byte[36]);
        V(w, 0); V(w, 0); w.Write(new byte[16]); w.Write((byte)0);
        w.Write((ushort)0); w.Write(new byte[18]); w.Write((byte)1); V(w, 0); V(w, 0); w.Write(new byte[52]);
        V(w, 0); V(w, 0); V(w, 0); w.Write(0L); w.Write(map); w.Write(101L); V(w, 0); w.Write((byte)1);
        w.Write(new byte[32]); V(w, 0); V(w, 0);
    });
    private static byte[] Packet(ushort opcode, Action<BinaryWriter> write)
    { using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes); writer.Write(opcode); write(writer); return bytes.ToArray(); }
    private static void V(BinaryWriter writer, uint value)
    { while (value >= 128) { writer.Write((byte)(value | 128)); value >>= 7; } writer.Write((byte)value); }
}
