using System.Buffers.Binary;
using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
using PacketDotNet;
using SharpPcap;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Only synthetic own traffic and isolated stores; no live device, sockets or user data.</summary>
public sealed class NetworkWorldEntryInitializationTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 13, 5, 0, 0, TimeSpan.Zero);
    private static readonly OwnCharacterIdentity Alpha = new(101, 501, 300, "SyntheticAlpha");
    private static readonly OwnCharacterIdentity Beta = new(202, 502, 301, "SyntheticBeta");
    private const string Game = "192.0.2.1:9000:50000";

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "soulcrest-entry-init-" + Guid.NewGuid().ToString("N"));
        internal string ProgressPath => Path.Combine(_root, "progress.json");
        internal string ExplorationPath => Path.Combine(_root, "exploration.json");
        internal ProgressService Progress { get; }
        internal ExplorationService Exploration { get; }
        internal NetworkLootService Network { get; }
        internal List<NetworkSoul> Gains { get; } = [];
        internal Fixture()
        {
            Directory.CreateDirectory(_root);
            Progress = new(ProgressPath, new PetCatalog([
                new PetDefinition { Id = "fossa", En = "Fossa", Genus = "Fera", NumericId = "1004" }]));
            Exploration = new(ExplorationPath, [
                new("fort", "altgard", "stronghold", 0, "Synthetic Fort", null, QuestId: 2820006, StrongholdInstanceId: 142005),
                new("pillar", "altgard", "kibelisk", 1, "Synthetic Pillar", null, TeleportId: 1052020)]);
            Network = new(Progress, Exploration);
            typeof(NetworkLootService).GetProperty(nameof(NetworkLootService.Running))!.SetValue(Network, true);
            Network.SoulGained += Gains.Add;
        }
        internal void Send(byte[] message, int second = 0, string stream = Game) =>
            Network.ProcessProgressMessage(stream, message, At.AddSeconds(second));
        internal void Start(OwnCharacterIdentity? identity = null, int second = 0) => Send(Candidate(identity ?? Alpha), second);
        internal void Full(int second = 1)
        {
            Send(Pets(), second); Send(Quests(), second); Send(Teleports(), second);
        }
        internal void Enter(OwnCharacterIdentity? identity = null, int second = 3, string stream = Game) =>
            Send(Own(identity ?? Alpha), second, stream);
        internal ExplorationProfiles StoredExploration() => JsonSerializer.Deserialize<ExplorationProfiles>(File.ReadAllText(ExplorationPath))!;
        public void Dispose() { Network.Dispose(); Directory.Delete(_root, recursive: true); }
    }

    [Fact]
    public void ColdStartRequiresCandidateOnePrepareAndMatchingStrictOwnBeforeAnyBaselineOrWrite()
    {
        using var f = new Fixture();
        f.Start(); f.Full();
        Assert.Null(f.Network.Character);
        Assert.False(f.Network.ProgressSources.HasAnyBaseline);
        Assert.Equal(3, f.Network.ProgressDiagnostics.PendingMessages);
        Assert.False(File.Exists(f.ProgressPath));
        Assert.False(File.Exists(f.ExplorationPath));
        Assert.Empty(f.Exploration.Completed);
        f.Send(Prepare(), 2);
        Assert.Equal(3, f.Network.ProgressDiagnostics.PendingMessages);
        Assert.False(File.Exists(f.ProgressPath));
        Assert.False(File.Exists(f.ExplorationPath));
        f.Enter();
        Assert.Equal(Alpha.Name, f.Network.Character);
        Assert.Equal(At.AddSeconds(1), f.Network.PetBaselineAt);
        Assert.Equal(At.AddSeconds(1), f.Network.QuestBaselineAt);
        Assert.Equal(At.AddSeconds(1), f.Network.TeleportBaselineAt);
        Assert.Equal(8, f.Progress.Souls("fossa"));
        Assert.Equal(new[] { "fort", "pillar" }, f.Exploration.Completed.Order());
        Assert.Empty(f.Gains);
        Assert.Empty(f.Progress.Recent());
        Assert.Empty(f.Network.Recent);
        Assert.Equal(0, f.Network.ProgressDiagnostics.PendingMessages);
        Assert.Equal(0, f.Network.ProgressDiagnostics.PendingBytes);
        Assert.True(f.Network.ProgressSources.HasAllBaselines);
        Assert.True(f.Network.ProgressSources.InitialProgressConfirmed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameOrDifferentCharacterInitializationNeverWritesTheNewBatchToTheOldProfile(bool sameCharacter)
    {
        using var f = new Fixture();
        f.Enter(Alpha, 0);
        var oldGuid = f.Exploration.ActiveId;
        f.Exploration.Rename("Synthetic Custom Label");
        var next = sameCharacter ? Alpha : Beta;
        f.Start(next, 1);
        Assert.Null(f.Network.Character);
        Assert.False(f.Network.ProgressSources.HasAnyBaseline);
        f.Full(2);
        Assert.Empty(f.Exploration.Completed);
        Assert.Equal(0, f.Progress.Souls("fossa"));
        var beforeOwn = f.StoredExploration();
        Assert.Single(beforeOwn.Characters);
        Assert.Empty(beforeOwn.Characters[0].Done);
        Assert.Empty(beforeOwn.Characters[0].RawQuests);
        f.Send(Prepare(), 3); f.Enter(next, 4);
        Assert.Equal(next.CharacterId, f.Exploration.Snapshot().NetworkCharacterId);
        Assert.Equal(next.ServerId, f.Exploration.Snapshot().ServerId);
        Assert.Equal(8, f.Progress.Souls("fossa"));
        var saved = f.StoredExploration();
        Assert.Equal("Synthetic Custom Label", saved.Characters.Single(c => c.Id == oldGuid).Name);
        if (sameCharacter)
        {
            Assert.Single(saved.Characters);
            Assert.Equal(oldGuid, f.Exploration.ActiveId);
        }
        else
        {
            Assert.NotEqual(oldGuid, f.Exploration.ActiveId);
            Assert.Empty(saved.Characters.Single(c => c.Id == oldGuid).Done);
            Assert.Empty(saved.Characters.Single(c => c.Id == oldGuid).RawQuests);
            f.Progress.BindNetworkContext(new(Alpha, 1000, 0, At.AddSeconds(5)));
            Assert.Equal(0, f.Progress.Souls("fossa"));
        }
        Assert.Empty(f.Gains);
    }

    [Theory]
    [InlineData("missing-prepare")]
    [InlineData("two-prepares")]
    [InlineData("wrong-character")]
    [InlineData("wrong-entity")]
    [InlineData("wrong-stream")]
    [InlineData("unsupported-prepare")]
    [InlineData("unsupported-own")]
    [InlineData("duplicate-source")]
    [InlineData("gap")]
    [InlineData("global-gap")]
    public void AnIncompleteOrConflictingTransactionCannotAuthorizeItsBufferedBaselines(string variant)
    {
        using var f = new Fixture();
        f.Start(); f.Full();
        if (variant != "missing-prepare") f.Send(Prepare(), 2);
        switch (variant)
        {
            case "two-prepares": f.Send(Prepare(), 2); break;
            case "unsupported-prepare": f.Send([0x21, 0x36, 0], 2); break;
            case "unsupported-own": f.Send([0x33, 0x36, 0], 2); break;
            case "duplicate-source": f.Send(Pets(), 2); break;
            case "gap": f.Network.ProcessProgressGap(Game); break;
            case "global-gap": f.Network.ProcessProgressGap("*"); break;
        }
        var own = variant switch
        {
            "wrong-character" => Alpha with { CharacterId = 102 },
            "wrong-entity" => Alpha with { EntityKey = 302 },
            _ => Alpha,
        };
        f.Enter(own, 3, variant == "wrong-stream" ? "other" : Game);
        AssertNoBaseline(f);
        Assert.Empty(f.Exploration.Completed);
        Assert.Equal(0, f.Progress.Souls("fossa"));
        Assert.Equal(0, f.Network.ProgressDiagnostics.PendingBytes);
        Assert.Empty(f.Gains);
    }

    [Theory]
    [InlineData("length")]
    [InlineData("result")]
    [InlineData("character-zero")]
    [InlineData("entity-zero")]
    public void UnsupportedEntryCandidateStillSuspendsThePreviousOwnAuthority(string variant)
    {
        using var f = new Fixture();
        f.Enter(Alpha, 0);
        var candidate = Candidate(Beta);
        switch (variant)
        {
            case "length": candidate = candidate[..^1]; break;
            case "result": candidate[2] = 1; break;
            case "character-zero": candidate.AsSpan(10, 8).Clear(); break;
            case "entity-zero": candidate[4] = 0; break;
        }
        f.Send(candidate, 1); f.Full(2);
        Assert.Null(f.Network.Character);
        Assert.Equal(0, f.Network.ProgressDiagnostics.PendingMessages);
        Assert.Empty(f.Exploration.Completed);
        Assert.Equal(0, f.Progress.Souls("fossa"));
        f.Send(Prepare(), 3); f.Enter(Beta, 4);
        AssertNoBaseline(f);
        Assert.All(f.StoredExploration().Characters, c => { Assert.Empty(c.Done); Assert.Empty(c.RawQuests); });
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(121)]
    public void CandidateRejectsNegativeOrExpiredCaptureTimeWithoutTimers(int seconds)
    {
        using var f = new Fixture();
        f.Start(); f.Full(); f.Send(Prepare(), 2);
        f.Enter(second: seconds);
        AssertNoBaseline(f);
        Assert.Empty(f.Exploration.Completed);
        Assert.Equal(0, f.Network.ProgressDiagnostics.PendingBytes);
    }

    [Fact]
    public void TwoMinuteInclusiveBoundaryStillAuthorizesTheVerifiedTransaction()
    {
        using var f = new Fixture();
        f.Start(); f.Full(); f.Send(Prepare(), 2); f.Enter(second: 120);
        Assert.True(f.Network.ProgressSources.HasAllBaselines);
        Assert.Equal(At.AddSeconds(1), f.Network.QuestBaselineAt);
    }

    [Fact]
    public void EveryUnanchoredPrefixAndEveryPreOwnDeltaIsDiscardedWithoutCountingLoot()
    {
        using var f = new Fixture();
        f.Full();
        foreach (var delta in Deltas()) f.Send(delta);
        Assert.Equal(0, f.Network.ProgressDiagnostics.PendingMessages);
        f.Enter();
        AssertNoBaseline(f);
        Assert.Empty(f.Exploration.Completed);
        Assert.Empty(f.Gains);
        f.Start(second: 4);
        foreach (var delta in Deltas()) f.Send(delta, 4);
        f.Send(Pets(), 5); f.Send(Prepare(), 6);
        foreach (var delta in Deltas()) f.Send(delta, 6);
        f.Enter(second: 7);
        Assert.Equal(8, f.Progress.Souls("fossa"));
        Assert.Null(f.Network.ProgressSources.DungeonActivityAt);
        Assert.Null(f.Network.ProgressSources.KibeliskActivityAt);
        Assert.Null(f.Network.QuestBaselineAt);
        Assert.Null(f.Network.TeleportBaselineAt);
        Assert.Empty(f.Exploration.Completed);
        Assert.Empty(f.Gains);
        f.Send(Points(0x900D), 8);
        Assert.Equal(10, f.Progress.Souls("fossa"));
        Assert.Equal(2, Assert.Single(f.Gains).Souls);
        Assert.Single(f.Progress.Recent());
        Assert.Single(f.Network.Recent);
    }

    [Fact]
    public void ANewAnchorDropsAllPreviousCandidateBytesAndRequiresItsOwnPrepare()
    {
        using var f = new Fixture();
        f.Start(); f.Full(); f.Send(Prepare(), 2);
        Assert.True(f.Network.ProgressDiagnostics.PendingBytes > 0);
        f.Start(Beta, 3);
        Assert.Equal(0, f.Network.ProgressDiagnostics.PendingBytes);
        f.Send(Pets(), 4); f.Send(Prepare(), 5); f.Enter(Beta, 6);
        Assert.Equal(At.AddSeconds(4), f.Network.PetBaselineAt);
        Assert.Null(f.Network.QuestBaselineAt);
        Assert.Null(f.Network.TeleportBaselineAt);
        Assert.Empty(f.Exploration.Completed);
    }

    [Fact]
    public void OversizedCandidateBaselineDiscardsTheEntireTransactionAndSignalsBoundedOverflow()
    {
        using var f = new Fixture();
        f.Start(); f.Send(Quests(), 1);
        var oversized = new byte[2 * 1024 * 1024 + 1];
        oversized[1] = 0x90;
        f.Send(oversized, 1);
        Assert.True(f.Network.ProgressDiagnostics.PendingOverflow);
        Assert.Equal(0, f.Network.ProgressDiagnostics.PendingMessages);
        Assert.Equal(0, f.Network.ProgressDiagnostics.PendingBytes);
        f.Send(Prepare(), 2); f.Enter();
        AssertNoBaseline(f);
        Assert.Empty(f.Exploration.Completed);
    }

    [Theory]
    [InlineData(0x9000)]
    [InlineData(0x8D09)]
    [InlineData(0x9232)]
    public void MalformedFullSourceCannotLeaveAPartialCandidateForTheNextOwn(ushort opcode)
    {
        using var f = new Fixture();
        f.Start();
        var valid = opcode switch { 0x9000 => Pets(), 0x8D09 => Quests(), _ => Teleports() };
        f.Send([.. valid, 0], 1);
        f.Send(Prepare(), 2); f.Enter();
        AssertNoBaseline(f);
        Assert.Equal(0, f.Network.ProgressDiagnostics.PendingBytes);
    }

    [Fact]
    public void ForeignEntryMessagesNeitherSuspendTheBoundOwnNorConsumeTheActualCandidatesBytes()
    {
        using var f = new Fixture();
        f.Enter(Alpha, 0); f.Send(Pets(), 1);
        f.Send(Candidate(Beta), 2, "other");
        f.Send(Prepare(), 2, "other");
        Assert.Equal(Alpha.Name, f.Network.Character);
        Assert.Equal(At.AddSeconds(1), f.Network.PetBaselineAt);
        f.Start(Alpha, 3); f.Full(4);
        var bytes = f.Network.ProgressDiagnostics.PendingBytes;
        f.Send(Prepare(), 5, "other");
        f.Send(Candidate(Beta), 5, "other");
        f.Network.ProcessProgressGap("other");
        Assert.Equal(bytes, f.Network.ProgressDiagnostics.PendingBytes);
        f.Send(Prepare(), 6); f.Enter(Alpha, 7);
        Assert.True(f.Network.ProgressSources.HasAllBaselines);
    }

    [Fact]
    public void BaselinesAfterPrepareAndAfterConfirmedOwnRemainSupported()
    {
        using var f = new Fixture();
        f.Start(); f.Send(Prepare(), 1); f.Full(2); f.Enter();
        Assert.True(f.Network.ProgressSources.HasAllBaselines);
        f.Send(Pets(9), 4);
        Assert.Equal(14, f.Progress.Souls("fossa"));
        Assert.Equal(At.AddSeconds(4), f.Network.PetBaselineAt);
        Assert.Empty(f.Gains);
    }

    [Fact]
    public void AReentrantSessionResetDuringOwnNotificationCannotReplayTheOldCandidate()
    {
        using var f = new Fixture();
        f.Start(); f.Full(); f.Send(Prepare(), 2);
        f.Network.OwnCharacterEntered += _ => f.Network.ResetProgressContext("synthetic-reentrant-reset", At.AddSeconds(3));
        f.Enter();
        AssertNoBaseline(f);
        Assert.Empty(f.Exploration.Completed);
        Assert.Equal(0, f.Progress.Souls("fossa"));
    }

    [Theory]
    [InlineData((byte)0x01)]
    [InlineData((byte)0x04)]
    public void EmptyTcpFinOrResetInvalidatesTheCandidateBeforeOwnCanAuthorizeIt(byte flags)
    {
        using var f = new Fixture();
        f.Start(); f.Full(); f.Send(Prepare(), 2);
        var streams = new Dictionary<(string, int, int), (TcpReassembler Tcp, GameMessageStream Messages)>();
        var feed = typeof(NetworkLootService).GetMethod("Feed", BindingFlags.NonPublic | BindingFlags.Static)!;
        var received = Assert.IsAssignableFrom<IEnumerable>(feed.Invoke(null,
            [streams, ClosePacket(flags), new HashSet<int> { 9000 }, new HashSet<int> { 50000 }]));
        foreach (var item in received)
        {
            var type = item.GetType();
            Assert.True((bool)type.GetProperty("Gap")!.GetValue(item)!);
            f.Network.ProcessProgressGap((string)type.GetProperty("Stream")!.GetValue(item)!);
        }
        Assert.Equal(0, f.Network.ProgressDiagnostics.PendingBytes);
        f.Enter();
        AssertNoBaseline(f);
        Assert.Empty(f.Exploration.Completed);
    }

    [Fact]
    public void RemovedOwnedTupleDiscardsItsCandidateWhileTheCaptureHandleCanBeRetained()
    {
        using var f = new Fixture();
        var world = new CaptureConnection(42, "192.0.2.2", 50000, "192.0.2.1", 9000);
        var web = new CaptureConnection(42, "192.0.2.2", 50001, "198.51.100.1", 443);
        var target = new CaptureTarget("adapter", "synthetic", new HashSet<int> { 9000, 443 },
            new HashSet<int> { 50000, 50001 }, "Synthetic target", new HashSet<CaptureConnection> { world, web });
        typeof(NetworkLootService).GetField("_target", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(f.Network, target);
        typeof(NetworkLootService).GetField("_installedCoverage", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(f.Network, target.RemoteEndpoints);
        f.Send(Candidate(Alpha), stream: world.StreamKey);
        f.Send(Pets(), 1, world.StreamKey);
        f.Send(Prepare(), 2, world.StreamKey);
        Assert.True(f.Network.ProgressDiagnostics.PendingBytes > 0);
        Assert.True(f.Network.TryRetainCapture(target with { Connections = new HashSet<CaptureConnection> { web } }));
        Assert.Equal(0, f.Network.ProgressDiagnostics.PendingBytes);
        f.Enter(stream: world.StreamKey);
        AssertNoBaseline(f);
    }

    private static void AssertNoBaseline(Fixture f)
    {
        Assert.Null(f.Network.PetBaselineAt);
        Assert.Null(f.Network.QuestBaselineAt);
        Assert.Null(f.Network.TeleportBaselineAt);
        Assert.False(f.Network.ProgressSources.InitialProgressConfirmed);
    }

    private static byte[] Candidate(OwnCharacterIdentity identity) => Packet(0x3620, w =>
    {
        w.Write((ushort)0); V(w, (uint)identity.EntityKey); w.Write(new byte[4]); w.Write(identity.CharacterId);
        w.Write(new byte[2279 - (int)(w.BaseStream.Position - 2)]); // Observed envelope, deliberately opaque tail.
    });
    private static byte[] Prepare() => Packet(0x3621, w => w.Write(new byte[46]));
    private static byte[] Pets(int balance = 3) => Packet(0x9000, w =>
    {
        w.Write(0); w.Write((byte)1); w.Write(999); w.Write(1004); w.Write(1);
        w.Write((byte)1); w.Write(1004); w.Write(balance); w.Write(new byte[4]);
    });
    private static byte[] Quests() => Packet(0x8D09, w =>
    {
        w.Write((byte)1); w.Write(2820006); w.Write((byte)4); w.Write((byte)1);
        w.Write(new byte[] { 1, 0, 0, 0, 0, 0 }); w.Write(80L); w.Write(new byte[2]);
    });
    private static byte[] Teleports() => Packet(0x9232, w => { w.Write((byte)1); w.Write(1052020); });
    private static byte[] Points(ushort opcode) => Packet(opcode, w => { w.Write((byte)1); w.Write(1004); w.Write(2); });
    private static IEnumerable<byte[]> Deltas()
    {
        yield return Points(0x900D);
        yield return Points(0x900E);
        yield return Packet(0x9009, w => { V(w, 300); w.Write(1004); w.Write(2); w.Write(new byte[13]); });
        yield return Packet(0x6101, w => { w.Write((byte)0); w.Write(142005); w.Write(true); w.Write((byte)0); });
        yield return Packet(0x9233, w => w.Write(1052020));
    }
    private static byte[] Own(OwnCharacterIdentity identity) => Packet(0x3633, w =>
    {
        V(w, (uint)identity.EntityKey); w.Write(0); // No optional Own flags.
        w.Write((byte)1); var name = Encoding.UTF8.GetBytes(identity.Name); V(w, (uint)name.Length); w.Write(name);
        w.Write((ushort)identity.ServerId); w.Write(0); w.Write(new byte[36]);
        V(w, 0); V(w, 0); w.Write(new byte[16]); w.Write((byte)0); // Detail lists and race.
        w.Write((ushort)0); w.Write(new byte[18]); w.Write((byte)1); V(w, 0); V(w, 0); w.Write(new byte[52]);
        V(w, 0); V(w, 0); V(w, 0); // Stats, abnormals, equips.
        w.Write(new byte[12]); w.Write(identity.CharacterId); V(w, 0); w.Write((byte)1);
        w.Write(new byte[32]); V(w, 0); V(w, 0); // Conqueror/combat data and two point lists.
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
    private static RawCapture ClosePacket(byte flags)
    {
        var bytes = new byte[54];
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(12), 0x0800); bytes[14] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(16), 40); bytes[22] = 64; bytes[23] = 6;
        new byte[] { 192, 0, 2, 1 }.CopyTo(bytes, 26); new byte[] { 192, 0, 2, 2 }.CopyTo(bytes, 30);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(34), 9000);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(36), 50000); bytes[46] = 0x50; bytes[47] = (byte)(0x10 | flags);
        return new(LinkLayers.Ethernet, new PosixTimeval(1791257500UL, 0UL), bytes, null);
    }
}
