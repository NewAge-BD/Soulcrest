using System.Text;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Synthetic own traffic only; neither a live capture nor real user profiles are opened.</summary>
public sealed class NetworkOwnCharacterContextTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch;
    private static readonly OwnCharacterIdentity Alpha = new(101, 501, 300, "SyntheticAlpha");
    private static readonly OwnCharacterIdentity Beta = new(202, 502, 301, "SyntheticBeta");
    private const string Game = "synthetic-own";

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "soulcrest-own-class-" + Guid.NewGuid().ToString("N"));
        internal ProgressService Progress { get; }
        internal ExplorationService Exploration { get; }
        internal NetworkLootService Network { get; }
        internal Fixture()
        {
            Directory.CreateDirectory(_root);
            Progress = new(Path.Combine(_root, "progress.json"), new PetCatalog([
                new PetDefinition { Id = "fossa", En = "Fossa", Genus = "Fera", NumericId = "1004" }]));
            Exploration = new(Path.Combine(_root, "exploration.json"), [
                new("pillar", "altgard", "kibelisk", 1, "Synthetic Pillar", null, TeleportId: 1052020)]);
            Network = new(Progress, Exploration);
            typeof(NetworkLootService).GetProperty(nameof(NetworkLootService.Running))!.SetValue(Network, true);
        }
        internal void Send(byte[] message, string stream = Game) => Network.ProcessProgressMessage(stream, message, At);
        public void Dispose() { Network.Dispose(); Directory.Delete(_root, recursive: true); }
    }

    [Fact]
    public void ParsedClassAndIdentityAreOneImmutableAtomicSnapshotAndSwitchTogether()
    {
        using var f = new Fixture();
        var observed = new List<OwnCharacterContext?>();
        f.Network.OwnCharacterChanged += () => observed.Add(f.Network.OwnCharacter);
        Assert.Null(f.Network.OwnCharacter);
        f.Send(Own(Alpha, 31));
        var first = Assert.IsType<OwnCharacterContext>(f.Network.OwnCharacter);
        Assert.Equal(Alpha with { ClassId = "Cleric" }, first.Identity);
        Assert.Same(first, f.Network.OwnCharacter);
        f.Send(Own(Beta, 28));
        var second = Assert.IsType<OwnCharacterContext>(f.Network.OwnCharacter);
        Assert.Equal(Beta with { ClassId = "Sorcerer" }, second.Identity);
        Assert.True(second.Generation > first.Generation);
        Assert.Equal("Cleric", first.Identity.ClassId);
        Assert.Collection(observed, item => Assert.Same(first, item), item => Assert.Null(item), item => Assert.Same(second, item));
    }

    [Theory]
    [InlineData("candidate")] [InlineData("prepare")] [InlineData("unsupported-own")]
    [InlineData("gap-own")] [InlineData("gap-all")] [InlineData("reset")] [InlineData("stop")]
    public void EveryOwnContextBoundaryClearsTheSnapshotAndPublishesItsInvalidation(string boundary)
    {
        using var f = new Fixture();
        f.Send(Own(Alpha, 31));
        var invalidations = 0;
        f.Network.OwnCharacterChanged += () => { Assert.Null(f.Network.OwnCharacter); invalidations++; };
        switch (boundary)
        {
            case "candidate": f.Send(Candidate(Beta)); break;
            case "prepare": f.Send(Packet(0x3621, w => w.Write(new byte[46]))); break;
            case "unsupported-own": f.Send([0x33, 0x36]); break;
            case "gap-own": f.Network.ProcessProgressGap(Game); break;
            case "gap-all": f.Network.ProcessProgressGap("*"); break;
            case "reset": f.Network.ResetProgressContext(); break;
            case "stop": f.Network.Stop(); break;
        }
        Assert.Null(f.Network.OwnCharacter);
        Assert.Equal(1, invalidations);
    }

    [Fact]
    public void ForeignPreparationAndGapCannotClearTheBoundOwnClass()
    {
        using var f = new Fixture(); f.Send(Own(Alpha, 31));
        var first = f.Network.OwnCharacter;
        var changes = 0; f.Network.OwnCharacterChanged += () => changes++;
        f.Send(Packet(0x3621, w => w.Write(new byte[46])), "synthetic-foreign");
        f.Network.ProcessProgressGap("synthetic-foreign");
        Assert.Same(first, f.Network.OwnCharacter);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void UnknownPcClassKeepsOwnBindingLootAndCharacterExplorationWorking()
    {
        using var f = new Fixture(); f.Send(Own(Alpha, int.MaxValue));
        var current = Assert.IsType<OwnCharacterContext>(f.Network.OwnCharacter);
        Assert.Equal(Alpha, current.Identity);
        Assert.Null(current.Identity.ClassId);
        var gains = new List<NetworkSoul>(); f.Network.SoulGained += gains.Add;
        f.Send(Packet(0x9233, w => w.Write(1052020)));
        f.Send(Packet(0x900D, w => { w.Write((byte)1); w.Write(1004); w.Write(2); }));
        Assert.True(f.Network.ProgressSources.IdentityIsLive);
        Assert.Contains("pillar", f.Exploration.Completed);
        Assert.Equal(2, Assert.Single(gains).Souls);
        Assert.Equal(2, f.Progress.Souls("fossa"));
        Assert.Same(current, f.Network.OwnCharacter);
    }

    [Fact]
    public void ReentrantResetFromClassNotificationPreventsOldOwnCallbacksAndPublication()
    {
        using var f = new Fixture();
        var entered = 0; f.Network.OwnCharacterEntered += _ => entered++;
        f.Network.OwnCharacterChanged += () =>
        {
            if (f.Network.OwnCharacter is not null) f.Network.ResetProgressContext();
        };
        f.Send(Own(Alpha, 31));
        Assert.Null(f.Network.OwnCharacter);
        Assert.Null(f.Network.Character);
        Assert.Equal(0, entered);
    }

    [Fact]
    public void GapRevokesClassBeforeOtherCallbacksAndCannotOverwriteAReentrantNewOwn()
    {
        using var f = new Fixture(); f.Send(Own(Alpha, 31));
        var rebound = false;
        f.Network.MerchantSessionReset += () =>
        {
            if (rebound) return;
            rebound = true;
            Assert.Null(f.Network.OwnCharacter);
            f.Send(Own(Beta, 28));
        };
        f.Network.ProcessProgressGap(Game);
        Assert.True(rebound);
        Assert.Equal(Beta with { ClassId = "Sorcerer" }, f.Network.OwnCharacter?.Identity);
    }

    private static byte[] Candidate(OwnCharacterIdentity identity) => Packet(0x3620, w =>
    {
        w.Write((ushort)0); V(w, (uint)identity.EntityKey); w.Write(new byte[4]); w.Write(identity.CharacterId);
    });

    private static byte[] Own(OwnCharacterIdentity identity, int pcId) => Packet(0x3633, w =>
    {
        V(w, (uint)identity.EntityKey); w.Write(0);
        w.Write((byte)1); var name = Encoding.UTF8.GetBytes(identity.Name); V(w, (uint)name.Length); w.Write(name);
        w.Write((ushort)identity.ServerId); w.Write(pcId); w.Write(new byte[36]);
        V(w, 0); V(w, 0); w.Write(new byte[16]); w.Write((byte)0);
        w.Write((ushort)0); w.Write(new byte[18]); w.Write((byte)1); V(w, 0); V(w, 0); w.Write(new byte[52]);
        V(w, 0); V(w, 0); V(w, 0);
        w.Write(new byte[12]); w.Write(identity.CharacterId); V(w, 0); w.Write((byte)1);
        w.Write(new byte[32]); V(w, 0); V(w, 0);
    });

    private static byte[] Packet(ushort opcode, Action<BinaryWriter> write)
    {
        using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes);
        writer.Write(opcode); write(writer); return bytes.ToArray();
    }

    private static void V(BinaryWriter writer, uint value)
    {
        while (value >= 128) { writer.Write((byte)(value | 128)); value >>= 7; }
        writer.Write((byte)value);
    }
}
