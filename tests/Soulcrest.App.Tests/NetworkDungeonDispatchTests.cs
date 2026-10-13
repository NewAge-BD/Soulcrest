using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class NetworkDungeonDispatchTests
{
    private static readonly OwnCharacterIdentity Own = new(101, 501, 100, "Synthetic dungeon profile");
    private static readonly DateTimeOffset At = new(2026, 10, 13, 1, 0, 0, TimeSpan.Zero);

    private sealed class Fixture : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-dungeon-dispatch-" + Guid.NewGuid().ToString("N"));
        internal ProgressService Progress { get; }
        internal ExplorationService Exploration { get; }
        internal NetworkLootService Network { get; }
        internal Fixture()
        {
            Directory.CreateDirectory(_folder);
            Progress = new(Path.Combine(_folder, "progress.json"), new PetCatalog([
                new PetDefinition { Id = "fossa", En = "Fossa", Genus = "Fera", NumericId = "1004" }]));
            Exploration = new(Path.Combine(_folder, "exploration.json"), [
                new("cave", "altgard", "dungeon", 0, "Synthetic cave", null, QuestId: 2810027, DungeonId: 320027),
                new("other", "altgard", "dungeon", 1, "Synthetic second cave", null, DungeonId: 320028)]);
            Network = new(Progress, Exploration);
        }
        public void Dispose() { Network.Dispose(); Directory.Delete(_folder, recursive: true); }
    }

    private static byte[] Packet(ushort opcode, Action<BinaryWriter> write)
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        writer.Write(opcode); write(writer);
        return bytes.ToArray();
    }
    private static byte[] Completion(int dungeon = 320027, bool succeeded = true) => Packet(0x6101, writer =>
    { writer.Write((byte)0); writer.Write(dungeon); writer.Write(succeeded); writer.Write((byte)0); });
    private static byte[] Gain() => Packet(0x900D, writer => { writer.Write((byte)1); writer.Write(1004); writer.Write(2); });

    [Fact]
    public void ResultWaitsForItsOwnStreamIdentityAndDoesNotRequireQuestOrPetBaselines()
    {
        using var f = new Fixture();
        f.Network.ProcessProgressMessage("game", Completion(), At);
        Assert.Empty(f.Exploration.Completed);
        Assert.Null(f.Network.ProgressSources.DungeonActivityAt);
        f.Network.ObserveIdentity("other", Own, At);
        Assert.Empty(f.Exploration.Completed);
        f.Network.ProcessProgressMessage("game", Completion(), At);
        Assert.Empty(f.Exploration.Completed);
        f.Network.ResetProgressContext();
        f.Network.ProcessProgressMessage("game", Completion(), At);
        f.Network.ObserveIdentity("game", Own, At);
        Assert.Empty(f.Exploration.Completed); // Results received before Own are never replayed.
        f.Network.ProcessProgressMessage("game", Completion(), At);
        Assert.Contains("cave", f.Exploration.Completed);
        Assert.DoesNotContain("other", f.Exploration.Completed);
        Assert.Equal(At, f.Network.ProgressSources.DungeonActivityAt);
        Assert.Null(f.Network.QuestBaselineAt);
        Assert.Null(f.Network.PetBaselineAt);
        Assert.Null(f.Network.TeleportBaselineAt);
        Assert.Empty(f.Progress.Recent());
        Assert.Empty(f.Network.Recent);
        Assert.Equal(Own.CharacterId, f.Exploration.Snapshot().NetworkCharacterId);
        f.Network.ProcessProgressMessage("game", Gain(), At.AddSeconds(1));
        Assert.Equal(2, f.Progress.Souls("fossa"));
        Assert.Single(f.Progress.Recent());
    }

    [Theory]
    [InlineData(0x3621, true)]
    [InlineData(0x3633, true)]
    [InlineData(0x3621, false)]
    [InlineData(0x3633, false)]
    public void UnboundResultsAndSoulsNeverReplayAcrossAnUnsupportedOwnBoundary(ushort opcode, bool bindAffectedStream)
    {
        using var f = new Fixture();
        var gain = Gain();
        var affectedCompletion = Completion();
        var unrelatedCompletion = Completion(320028);
        f.Network.ProcessProgressMessage("affected", gain, At);
        f.Network.ProcessProgressMessage("affected", affectedCompletion, At);
        f.Network.ProcessProgressMessage("unrelated", gain, At);
        f.Network.ProcessProgressMessage("unrelated", unrelatedCompletion, At);
        Assert.Equal(0, f.Network.ProgressDiagnostics.PendingMessages);
        Assert.Equal(0, f.Network.ProgressDiagnostics.PendingBytes);

        f.Network.ProcessProgressMessage("affected", Packet(opcode, writer => writer.Write((byte)0)), At.AddSeconds(1));
        Assert.Equal(0, f.Network.ProgressDiagnostics.PendingMessages);
        Assert.Equal(0, f.Network.ProgressDiagnostics.PendingBytes);
        Assert.Null(f.Network.Character);
        var nextOwn = Own with { CharacterId = 102, Name = "Synthetic Beta" };
        var nextStream = bindAffectedStream ? "affected" : "unrelated";
        f.Network.ObserveIdentity(nextStream, nextOwn, At.AddSeconds(2));
        Assert.Equal(nextOwn.CharacterId, f.Exploration.Snapshot().NetworkCharacterId);
        Assert.DoesNotContain("cave", f.Exploration.Completed);
        Assert.Equal(0, f.Progress.Souls("fossa"));
        Assert.DoesNotContain("other", f.Exploration.Completed);
        Assert.Empty(f.Network.Recent);
        Assert.Equal(0, f.Network.ProgressDiagnostics.PendingMessages);
        Assert.Equal(0, f.Network.ProgressDiagnostics.PendingBytes);

        f.Network.ProcessProgressMessage(nextStream, Completion(), At.AddSeconds(3));
        f.Network.ProcessProgressMessage(nextStream, Gain(), At.AddSeconds(3));
        Assert.Contains("cave", f.Exploration.Completed);
        Assert.Equal(2, f.Progress.Souls("fossa"));
    }

    [Theory]
    [InlineData(0x3621)]
    [InlineData(0x3633)]
    public void UnsupportedOwnBoundaryOnAnotherStreamDoesNotInvalidateTheBoundCharacter(ushort opcode)
    {
        using var f = new Fixture();
        f.Network.ObserveIdentity("game", Own, At);
        f.Network.ProcessProgressMessage("game", Gain(), At);
        f.Network.ProcessProgressMessage("other", Packet(opcode, writer => writer.Write((byte)0)), At.AddSeconds(1));
        Assert.Equal(Own.Name, f.Network.Character);
        Assert.Equal(Own.CharacterId, f.Exploration.Snapshot().NetworkCharacterId);
        f.Network.ProcessProgressMessage("game", Completion(), At.AddSeconds(2));
        f.Network.ProcessProgressMessage("game", Gain(), At.AddSeconds(2));
        Assert.Contains("cave", f.Exploration.Completed);
        Assert.Equal(4, f.Progress.Souls("fossa"));
        Assert.Equal(2, f.Network.Recent.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void DungeonRunStateAloneNeverInventsExplorationCompletionOrDiscovery(byte state)
    {
        using var f = new Fixture();
        f.Network.ObserveIdentity("game", Own, At);
        var message = Packet(0x6100, writer => { writer.Write(320027); writer.Write(state); writer.Write(0L); writer.Write(0L); });
        Assert.NotNull(DungeonProgressMessage.TryParseState(message));
        f.Network.ProcessProgressMessage("game", message, At);
        Assert.Empty(f.Exploration.Completed);
        Assert.Null(f.Exploration.Snapshot().Progress.GetValueOrDefault("cave")?.State);
        Assert.Null(f.Network.ProgressSources.DungeonActivityAt);
    }

    [Fact]
    public void FailedUnknownAndMalformedResultsDoNotMarkAnythingOrStopLaterValidResultsAndSouls()
    {
        using var f = new Fixture();
        f.Network.ObserveIdentity("game", Own, At);
        f.Network.ProcessProgressMessage("game", Completion(succeeded: false), At);
        f.Network.ProcessProgressMessage("game", Completion(999999), At);
        f.Network.ProcessProgressMessage("game", [.. Completion(), 0], At);
        Assert.Empty(f.Exploration.Completed);
        Assert.Null(f.Network.ProgressSources.DungeonActivityAt);
        Assert.True(f.Network.ProgressSources.DungeonFormatRejected);
        Assert.Equal(Own.Name, f.Network.Character);
        f.Network.ProcessProgressMessage("game", Gain(), At.AddSeconds(1));
        Assert.Equal(2, f.Progress.Souls("fossa"));
        f.Network.ProcessProgressMessage("game", Completion(), At.AddSeconds(2));
        Assert.Contains("cave", f.Exploration.Completed);
        // A valid event cannot repair the unrelated result rejected earlier in this session.
        Assert.True(f.Network.ProgressSources.DungeonFormatRejected);
        Assert.Contains("Netzwerkformat für Instanzabschlüsse", f.Network.ProgressStatus);
    }

    [Fact]
    public void UnknownQuestFormatCannotBlockAnExplicitDungeonResultAndExitKeepsItsSavedCompletion()
    {
        using var f = new Fixture();
        f.Network.ObserveIdentity("game", Own, At);
        f.Network.ProcessProgressMessage("game", [0x09, 0x8D, 0, 0], At);
        Assert.True(f.Network.ProgressSources.QuestFormatRejected);
        f.Network.ProcessProgressMessage("game", Completion(), At.AddSeconds(1));
        Assert.Contains("cave", f.Exploration.Completed);
        var profile = f.Exploration.ActiveId;
        f.Network.ProcessProgressGap("game");
        Assert.Null(f.Network.Character);
        Assert.Null(f.Network.ProgressSources.DungeonActivityAt);
        Assert.Contains("cave", f.Exploration.Completed);
        f.Network.ProcessProgressMessage("game", Completion(320028), At.AddSeconds(2));
        Assert.DoesNotContain("other", f.Exploration.Completed);
        f.Network.ObserveIdentity("new-game", Own, At.AddSeconds(3));
        Assert.Equal(profile, f.Exploration.ActiveId);
        Assert.Contains("cave", f.Exploration.Completed);
        Assert.DoesNotContain("other", f.Exploration.Completed);
    }
}
