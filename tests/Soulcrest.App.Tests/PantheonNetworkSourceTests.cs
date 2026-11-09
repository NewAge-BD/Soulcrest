using System.Text;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class PantheonNetworkSourceTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch.AddDays(20000);

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "soulcrest-pantheon-network-" + Guid.NewGuid().ToString("N"));
        internal NetworkLootService Network { get; }
        internal Fixture()
        {
            Directory.CreateDirectory(_root);
            Network = new(new ProgressService(Path.Combine(_root, "progress.json"), new PetCatalog([])),
                new ExplorationService(Path.Combine(_root, "exploration.json"), []));
            typeof(NetworkLootService).GetProperty(nameof(NetworkLootService.Running))!.SetValue(Network, true);
        }
        internal void Send(byte[] message, int second, string stream = "synthetic-own") =>
            Network.ProcessProgressMessage(stream, message, At.AddSeconds(second));
        internal void Login()
        {
            Send(Candidate(), 0); Send(List((101, 0), (202, 17)), 1); Send(Prepare(), 2); Send(Own(), 3);
        }
        public void Dispose() { Network.Dispose(); Directory.Delete(_root, true); }
    }

    [Fact]
    public void CandidateListIsPrivateUntilPreparedMatchingFullOwnAndKeepsItsOriginalFactTime()
    {
        using var f = new Fixture(); f.Send(Candidate(), 0); f.Send(List((101, 0), (202, 17)), 1);
        Assert.Null(f.Network.PantheonSnapshot.Character); Assert.Null(f.Network.PantheonSnapshot.Decorations.Value);
        f.Send(Prepare(), 2);
        Assert.False(f.Network.PantheonSnapshot.Decorations.IsFresh);
        f.Send(Own(), 3);
        var state = f.Network.PantheonSnapshot;
        Assert.Same(f.Network.CombatSnapshot.Character, state.Character);
        Assert.Equal(state.Character!.Generation, state.Generation);
        Assert.True(state.Decorations.IsFresh); Assert.Equal(NetworkCombatSourceStatus.Complete, state.Decorations.Status);
        Assert.Equal("own-pantheon-decoration-list", state.Decorations.Source);
        Assert.Equal(At.AddSeconds(1), state.Decorations.ObservedAt);
        Assert.InRange(state.Decorations.Sequence, 1, state.Sequence);
        Assert.Equal(2, state.Decorations.Value!.Decorations.Count);
        Assert.Equal((byte)0, state.Decorations.Value.Decorations[101]);
        Assert.Equal((byte)17, state.Decorations.Value.Decorations[202]);
    }

    [Theory]
    [InlineData("character")] [InlineData("entity")] [InlineData("stream")]
    [InlineData("unprepared")] [InlineData("expired")] [InlineData("before-candidate")]
    [InlineData("future-list")] [InlineData("future-prepare")]
    public void UnmatchedOrTemporallyInvalidLoginCannotPromoteTheProvisionalCollection(string variant)
    {
        using var f = new Fixture(); f.Send(Candidate(), 0);
        f.Send(List((101, 0)), variant == "before-candidate" ? -1 : variant == "future-list" ? 5 : 1);
        if (variant != "unprepared") f.Send(Prepare(), variant == "future-prepare" ? 5 : 2);
        f.Send(Own(character: variant == "character" ? 102 : 101, entity: variant == "entity" ? 301u : 300u),
            variant == "expired" ? 124 : 3, variant == "stream" ? "synthetic-other" : "synthetic-own");
        Assert.False(f.Network.PantheonSnapshot.Decorations.IsFresh);
        Assert.Null(f.Network.PantheonSnapshot.Decorations.Value);
    }

    [Fact]
    public void UnanchoredListAndIdentityOnlyHookNeverGrantOwnAuthority()
    {
        using var f = new Fixture(); f.Send(List((101, 0)), 0);
        f.Network.ObserveIdentity("synthetic-own", new(101, 501, 300, "SyntheticPantheon"), At.AddSeconds(1));
        f.Send(List((101, 0)), 2);
        Assert.Null(f.Network.PantheonSnapshot.Character);
        Assert.False(f.Network.PantheonSnapshot.Decorations.IsFresh);
        f.Send(Own(), 3);
        Assert.Null(f.Network.PantheonSnapshot.Decorations.Value);
        f.Send(List((101, 0)), 4);
        Assert.True(f.Network.PantheonSnapshot.Decorations.IsFresh);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DuplicateOrInitiallyMalformedPendingListCannotRecoverWithinTheSameTransaction(bool malformedFirst)
    {
        using var f = new Fixture(); f.Send(Candidate(), 0);
        f.Send(malformedFirst ? [0xaf, 0x8a] : List((101, 0)), 1);
        f.Send(List((101, 0)), 1); f.Send(Prepare(), 2); f.Send(Own(), 3);
        Assert.False(f.Network.PantheonSnapshot.Decorations.IsFresh);
        Assert.Equal(NetworkCombatSourceStatus.Conflict, f.Network.PantheonSnapshot.Decorations.Status);
        f.Send(List((202, 17)), 4);
        Assert.True(f.Network.PantheonSnapshot.Decorations.IsFresh);
        Assert.Single(f.Network.PantheonSnapshot.Decorations.Value!.Decorations);
    }

    [Fact]
    public void LiveFullListsReplaceInsteadOfMergingAndConfirmedEmptyIsExplicit()
    {
        using var f = new Fixture(); f.Login(); var first = f.Network.PantheonSnapshot;
        f.Send(List((303, 0)), 4);
        Assert.Equal(new[] { 303 }, f.Network.PantheonSnapshot.Decorations.Value!.Decorations.Keys);
        Assert.Equal(2, first.Decorations.Value!.Decorations.Count);
        f.Send(List(), 5);
        Assert.True(f.Network.PantheonSnapshot.Decorations.IsFresh);
        Assert.Empty(f.Network.PantheonSnapshot.Decorations.Value!.Decorations);
    }

    [Fact]
    public void MalformedOwnListInvalidatesOnlyPantheonAndACompleteListCanRecover()
    {
        using var f = new Fixture(); f.Login(); var combat = f.Network.CombatSnapshot; var first = f.Network.PantheonSnapshot;
        f.Send([.. List((303, 1)), 0], 4);
        Assert.Same(combat, f.Network.CombatSnapshot);
        Assert.False(f.Network.PantheonSnapshot.Decorations.IsFresh);
        Assert.Equal(NetworkCombatSourceStatus.Conflict, f.Network.PantheonSnapshot.Decorations.Status);
        Assert.Same(first.Decorations.Value, f.Network.PantheonSnapshot.Decorations.Value);
        f.Send(List((303, 1)), 5);
        Assert.True(f.Network.PantheonSnapshot.Decorations.IsFresh);
    }

    [Fact]
    public void ARejectedLaterMessageAlsoFencesOutBackwardRecoveryTimestamps()
    {
        using var f = new Fixture(); f.Login(); f.Send([0xaf, 0x8a], 6);
        var rejected = f.Network.PantheonSnapshot;
        f.Send(List((303, 1)), 5);
        Assert.Same(rejected, f.Network.PantheonSnapshot);
        Assert.False(f.Network.PantheonSnapshot.Decorations.IsFresh);
        f.Send(List((303, 1)), 7);
        Assert.True(f.Network.PantheonSnapshot.Decorations.IsFresh);
    }

    [Fact]
    public void ForeignStreamsAndOlderListsIncludingMalformedOnesLeaveTheCurrentSourceUntouched()
    {
        using var f = new Fixture(); f.Login(); f.Send(List((303, 1)), 5); var current = f.Network.PantheonSnapshot;
        f.Send(List((404, 2)), 6, "synthetic-other"); f.Send([0xaf, 0x8a], 6, "synthetic-other");
        f.Send(List((404, 2)), 4); f.Send([0xaf, 0x8a], 4); f.Send(List((404, 2)), 2);
        Assert.Same(current, f.Network.PantheonSnapshot);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RelevantGapsInvalidateAuthorityAndCannotReplayTheOldPendingList(bool global)
    {
        using var f = new Fixture(); f.Send(Candidate(), 0); f.Send(List((101, 0)), 1);
        f.Network.ProcessProgressGap(global ? "*" : "synthetic-own", capturedAtUtc: At.AddSeconds(2));
        f.Send(Prepare(), 3); f.Send(Own(), 4);
        Assert.False(f.Network.PantheonSnapshot.Decorations.IsFresh);
        Assert.Null(f.Network.PantheonSnapshot.Decorations.Value);
        f.Send(List((202, 17)), 5);
        f.Network.ProcessProgressGap(global ? "*" : "synthetic-own", capturedAtUtc: At.AddSeconds(6));
        Assert.Null(f.Network.PantheonSnapshot.Character);
        Assert.False(f.Network.PantheonSnapshot.Decorations.IsFresh);
    }

    [Fact]
    public void SameCharacterReentryKeepsOnlyStaleHistoryAndACharacterSwitchClearsThatHistory()
    {
        using var f = new Fixture(); f.Login(); var first = f.Network.PantheonSnapshot;
        f.Send(Own(), 4);
        Assert.Same(first.Decorations.Value, f.Network.PantheonSnapshot.Decorations.Value);
        Assert.Equal(NetworkCombatSourceStatus.Stale, f.Network.PantheonSnapshot.Decorations.Status);
        Assert.False(f.Network.PantheonSnapshot.Decorations.IsFresh);
        Assert.Same(f.Network.CombatSnapshot.Character, f.Network.PantheonSnapshot.Character);
        f.Send(Own(character: 102, entity: 301), 5);
        Assert.Null(f.Network.PantheonSnapshot.Decorations.Value);
        Assert.Equal(NetworkCombatSourceStatus.Unknown, f.Network.PantheonSnapshot.Decorations.Status);
    }

    [Fact]
    public void NewCharacterCandidateRevokesTheOldOwnBeforeItsPrePrepareDecorationListCanPublish()
    {
        using var f = new Fixture(); f.Login(); var oldOwn = f.Network.CombatSnapshot.Character;
        var beforeOwn = new List<NetworkPantheonSnapshot>();
        f.Network.Changed += () => beforeOwn.Add(f.Network.PantheonSnapshot);
        f.Send(Candidate(character: 202, entity: 301), 4);
        Assert.Null(f.Network.CombatSnapshot.Character);
        Assert.Null(f.Network.PantheonSnapshot.Character);
        Assert.False(f.Network.PantheonSnapshot.Decorations.IsFresh);
        f.Send(List((303, 1), (404, 0)), 5);
        Assert.Null(f.Network.PantheonSnapshot.Character);
        Assert.False(f.Network.PantheonSnapshot.Decorations.IsFresh);
        Assert.DoesNotContain(303, f.Network.PantheonSnapshot.Decorations.Value!.Decorations.Keys);
        f.Send(Prepare(), 6);
        Assert.All(beforeOwn, snapshot => Assert.False(snapshot.Decorations.IsFresh));
        Assert.DoesNotContain(beforeOwn, snapshot => ReferenceEquals(snapshot.Character, oldOwn));
        f.Send(Own(character: 202, entity: 301), 7);
        var current = f.Network.PantheonSnapshot;
        Assert.Equal(202, current.Character!.Identity.CharacterId);
        Assert.Same(f.Network.CombatSnapshot.Character, current.Character);
        Assert.True(current.Decorations.IsFresh);
        Assert.Equal(new[] { 303, 404 }, current.Decorations.Value!.Decorations.Keys.Order());
        Assert.DoesNotContain(101, current.Decorations.Value.Decorations.Keys);
    }

    [Fact]
    public void UnrelatedScoresForeignGapsAndUnsupportedOpcodesDoNotRepaintTheStandaloneSource()
    {
        using var f = new Fixture(); f.Login(); var current = f.Network.PantheonSnapshot;
        f.Send(Packet(0x3656, writer => { writer.Write(45678L); writer.Write(67890L); }), 4);
        f.Network.ProcessProgressGap("synthetic-other", capturedAtUtc: At.AddSeconds(5));
        f.Send(Packet(0x8AB0, writer => writer.Write(101)), 6);
        Assert.Same(current, f.Network.PantheonSnapshot);
        Assert.Equal(45678, f.Network.CombatSnapshot.CurrentCombatScore);
    }

    [Fact]
    public void ReentrantNewerFullListCannotBeOverwrittenByTheEarlierPublication()
    {
        using var f = new Fixture(); f.Login(); var replace = true;
        f.Network.Changed += () => { if (!replace) return; replace = false; f.Send(List((404, 2)), 6); };
        f.Send(List((303, 1)), 5);
        Assert.Equal(new[] { 404 }, f.Network.PantheonSnapshot.Decorations.Value!.Decorations.Keys);
        Assert.Equal(At.AddSeconds(6), f.Network.PantheonSnapshot.Decorations.ObservedAt);
    }

    [Fact]
    public void ReentrantOwnCallbackCannotRestoreThePreviousCharactersCollection()
    {
        using var f = new Fixture(); var replace = true;
        f.Network.OwnCharacterChanged += () =>
        {
            if (!replace || f.Network.CombatSnapshot.Character is null) return;
            replace = false; f.Send(Own(character: 102, entity: 301), 4); f.Send(List((404, 2)), 5);
        };
        f.Login();
        Assert.Equal(102, f.Network.PantheonSnapshot.Character!.Identity.CharacterId);
        Assert.Same(f.Network.CombatSnapshot.Character, f.Network.PantheonSnapshot.Character);
        Assert.Equal(new[] { 404 }, f.Network.PantheonSnapshot.Decorations.Value!.Decorations.Keys);
    }

    [Fact]
    public void StoppedSourceCannotAcceptAnotherList()
    {
        using var f = new Fixture(); f.Login(); var current = f.Network.PantheonSnapshot;
        typeof(NetworkLootService).GetProperty(nameof(NetworkLootService.Running))!.SetValue(f.Network, false);
        f.Send(List((404, 2)), 4);
        Assert.Same(current, f.Network.PantheonSnapshot);
    }

    private static byte[] List(params (int Id, byte Slot)[] rows) => Packet(0x8AAF, writer =>
    { V(writer, (uint)rows.Length); foreach (var row in rows) { writer.Write(row.Id); writer.Write(row.Slot); } });
    private static byte[] Candidate(long character = 101, uint entity = 300) => Packet(0x3620, writer =>
    { writer.Write((ushort)0); V(writer, entity); writer.Write(0); writer.Write(character); });
    private static byte[] Prepare() => Packet(0x3621, writer => writer.Write(new byte[46]));
    private static byte[] Own(long character = 101, uint entity = 300) => Packet(0x3633, writer =>
    {
        V(writer, entity); writer.Write(0); writer.Write((byte)1);
        var name = Encoding.UTF8.GetBytes("SyntheticPantheon"); V(writer, (uint)name.Length); writer.Write(name);
        writer.Write((ushort)501); writer.Write(31); writer.Write(45); writer.Write(new byte[20]); writer.Write(1); writer.Write(0L);
        V(writer, 0); V(writer, 0); writer.Write(new byte[16]); writer.Write((byte)0);
        writer.Write((ushort)0); writer.Write(new byte[18]); writer.Write((byte)1); V(writer, 0); V(writer, 0);
        writer.Write(0); writer.Write(0); writer.Write(new byte[44]);
        V(writer, 0); V(writer, 0); V(writer, 0);
        writer.Write(new byte[12]); writer.Write(character); V(writer, 0); writer.Write((byte)1);
        writer.Write(new byte[16]); writer.Write(12345L); writer.Write(67890L); V(writer, 0); V(writer, 0);
    });
    private static byte[] Packet(ushort opcode, Action<BinaryWriter> write)
    { using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes); writer.Write(opcode); write(writer); return bytes.ToArray(); }
    private static void V(BinaryWriter writer, uint value)
    { while (value >= 128) { writer.Write((byte)(value | 128)); value >>= 7; } writer.Write((byte)value); }
}
