using System.Collections.Immutable;
using System.Text;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class OwnSkillStateTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch.AddDays(20000);
    private const int Skill = 17060000;
    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "soulcrest-own-skills-" + Guid.NewGuid().ToString("N"));
        internal NetworkLootService Network { get; }
        internal OwnSkillStateService State { get; }
        internal Fixture()
        {
            Directory.CreateDirectory(_root);
            var exploration = new ExplorationService(Path.Combine(_root, "exploration.json"), []);
            Network = new(new ProgressService(Path.Combine(_root, "progress.json"), new PetCatalog([])), exploration);
            typeof(NetworkLootService).GetProperty(nameof(NetworkLootService.Running))!.SetValue(Network, true);
            State = new(Network, exploration);
        }
        internal void Send(byte[] message, int second, string stream = "synthetic-own") =>
            Network.ProcessProgressMessage(stream, message, At.AddSeconds(second));
        internal void Login(int total = 14)
        {
            Send(Candidate(), 0); Send(Prepare(), 1); Send(Skills(total), 2); Send(Own(), 3);
        }
        public void Dispose() { State.Dispose(); Network.Dispose(); Directory.Delete(_root, true); }
    }

    [Fact]
    public void PreparedLoginListIsPublishedOnlyAfterMatchingFullOwnAndLocalProfileBinding()
    {
        using var f = new Fixture();
        f.Send(Candidate(), 0); f.Send(Prepare(), 1); f.Send(Skills(), 2);
        Assert.False(f.State.Snapshot.IsKnown);
        f.Send(Own(), 3);
        var snapshot = f.State.Snapshot;
        Assert.True(snapshot.IsKnown); Assert.True(snapshot.IsFresh);
        Assert.NotNull(snapshot.ProfileId); Assert.Equal("Cleric", snapshot.ClassId);
        Assert.Equal(At.AddSeconds(2), snapshot.ObservedAt);
        Assert.Equal(At.AddSeconds(3), snapshot.CombatScoreObservedAt);
        Assert.Equal(12345L, snapshot.CurrentCombatScore);
        Assert.Equal(14, snapshot.Skills[Skill].TotalLevel);
        Assert.Equal(10, snapshot.Skills[Skill].OriginalLevel);
        Assert.Equal(4, snapshot.Skills[Skill].AdditionalLevels?.Daevanion);
        Assert.DoesNotContain("SyntheticAlpha", snapshot.ToString());
    }

    [Theory]
    [InlineData("wrong-character")] [InlineData("wrong-entity")] [InlineData("wrong-stream")]
    [InlineData("unprepared")] [InlineData("expired")] [InlineData("duplicate")]
    public void UnmatchedIncompleteOrExpiredLoginDoesNotSupplySkillAuthority(string variant)
    {
        using var f = new Fixture();
        f.Send(Candidate(), 0);
        if (variant != "unprepared") f.Send(Prepare(), 1);
        f.Send(Skills(), 2);
        if (variant == "duplicate") f.Send(Skills(), 2);
        f.Send(Own(variant == "wrong-character" ? 102 : 101, variant == "wrong-entity" ? 301u : 300u),
            variant == "expired" ? 124 : 3, variant == "wrong-stream" ? "synthetic-other" : "synthetic-own");
        Assert.False(f.State.Snapshot.IsKnown);
        Assert.Empty(f.State.Snapshot.Skills);
    }

    [Fact]
    public void FullOwnAndQualifiedUpdatesAreImmutableAndForeignOrOlderUpdatesCannotReplaceThem()
    {
        using var f = new Fixture(); f.Login();
        var first = f.State.Snapshot;
        f.Send(Change(15), 4);
        Assert.Equal(15, f.State.Snapshot.Skills[Skill].TotalLevel);
        Assert.Equal(14, first.Skills[Skill].TotalLevel);
        f.Send(Change(16), 5, "synthetic-other");
        f.Send(Change(13), 2);
        Assert.Equal(15, f.State.Snapshot.Skills[Skill].TotalLevel);
        Assert.Null(f.State.Snapshot.Skills[Skill].AdditionalLevels);
    }

    [Fact]
    public void MalformedUpdateRevokesFreshnessWithoutInventingZeroAndCompleteListHealsOnlyItsSource()
    {
        using var f = new Fixture(); f.Login();
        f.Send([0x13, 0x38], 4);
        Assert.False(f.State.Snapshot.IsFresh);
        Assert.Equal(14, f.State.Snapshot.Skills[Skill].TotalLevel);
        f.Send(Skills(15), 5);
        Assert.True(f.State.Snapshot.IsFresh);
        Assert.Equal(15, f.State.Snapshot.Skills[Skill].TotalLevel);
        Assert.True(f.Network.ProgressSources.IdentityIsLive);
    }

    [Theory]
    [InlineData("prepare")] [InlineData("gap")] [InlineData("stop")] [InlineData("character")]
    public void BoundariesImmediatelyRemovePreviousCharactersSkills(string boundary)
    {
        using var f = new Fixture(); f.Login();
        switch (boundary)
        {
            case "prepare": f.Send(Prepare(), 4); f.Send(Own(), 5); break;
            case "gap": f.Network.ProcessProgressGap("synthetic-own", capturedAtUtc: At.AddSeconds(4)); break;
            case "stop": f.Network.Stop(); break;
            default: f.Send(Own(102), 4); break;
        }
        Assert.False(f.State.Snapshot.IsKnown);
        Assert.Empty(f.State.Snapshot.Skills);
    }

    [Fact]
    public void IdentityOnlyHookAndPartialChangesNeverInventACompleteList()
    {
        using var f = new Fixture();
        f.Network.ObserveIdentity("synthetic-own", new(101, 501, 300, "SyntheticAlpha") { ClassId = "Cleric" }, At);
        f.Send(Change(15), 1);
        Assert.False(f.State.Snapshot.IsKnown);
        Assert.Empty(f.State.Snapshot.Skills);
        f.Send(Skills(), 2);
        Assert.False(f.State.Snapshot.IsKnown);
        f.Send(Own(), 3); f.Send(Skills(), 4);
        Assert.True(f.State.Snapshot.IsKnown);
    }

    [Fact]
    public void AdapterRejectsAResolverRaceAndDisposedSnapshotAndNeverUsesNamesAsKeys()
    {
        var source = new Source();
        using var state = new OwnSkillStateService(source, own =>
        {
            source.SkillSnapshot = source.SkillSnapshot with { Character = own with { Generation = own.Generation + 1 } };
            return new("local-profile", "Unused");
        });
        source.SkillSnapshot = new(new(new(101, 501, 300, "SyntheticAlpha"), 7),
            ImmutableDictionary<int, OwnSkillLevel>.Empty, true, true, At, 1, "synthetic");
        Assert.Equal(OwnSkillStateSnapshot.Empty, state.Snapshot);
        state.Dispose();
        Assert.Equal(OwnSkillStateSnapshot.Empty, state.Snapshot);
    }

    private sealed class Source : INetworkSkillSource
    {
        public bool Running => true;
        public NetworkSkillSnapshot SkillSnapshot { get; set; } = NetworkSkillSnapshot.Empty;
        public event Action? Changed { add { } remove { } }
        public event Action? OwnCharacterChanged { add { } remove { } }
    }

    private static byte[] Skills(int total = 14) => Packet(0x5100, w =>
    {
        w.Write((byte)1); w.Write((byte)1); w.Write(Skill); w.Write((byte)total); w.Write((byte)10);
        w.Write((byte)(total - 10)); w.Write(new byte[4]); w.Write(0);
    });
    private static byte[] Change(int total) => Packet(0x3813, w =>
    {
        w.Write((byte)1); w.Write(999); w.Write((byte)0); w.Write(Skill); w.Write((byte)total); w.Write((byte)10); w.Write((byte)255);
    });
    private static byte[] Candidate() => Packet(0x3620, w => { w.Write((ushort)0); V(w, 300); w.Write(0); w.Write(101L); });
    private static byte[] Prepare() => Packet(0x3621, w => w.Write(new byte[46]));
    private static byte[] Own(long character = 101, uint entity = 300) => Packet(0x3633, w =>
    {
        V(w, entity); w.Write(0); w.Write((byte)1);
        var name = Encoding.UTF8.GetBytes("SyntheticAlpha"); V(w, (uint)name.Length); w.Write(name);
        w.Write((ushort)501); w.Write(31); w.Write(new byte[36]);
        V(w, 0); V(w, 0); w.Write(new byte[16]); w.Write((byte)0);
        w.Write((ushort)0); w.Write(new byte[18]); w.Write((byte)1); V(w, 0); V(w, 0); w.Write(new byte[52]);
        V(w, 0); V(w, 0); V(w, 0); w.Write(new byte[12]); w.Write(character); V(w, 0); w.Write((byte)1);
        w.Write(new byte[16]); w.Write(12345L); w.Write(67890L); V(w, 0); V(w, 0);
    });
    private static byte[] Packet(ushort opcode, Action<BinaryWriter> write)
    {
        using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes);
        writer.Write(opcode); write(writer); return bytes.ToArray();
    }
    private static void V(BinaryWriter w, uint value)
    {
        while (value >= 128) { w.Write((byte)(value | 128)); value >>= 7; }
        w.Write((byte)value);
    }
}
