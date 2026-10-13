using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class NetworkStrongholdDispatchTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 13, 2, 0, 0, TimeSpan.Zero);
    private static readonly OwnCharacterIdentity Own = new(101, 501, 100, "Synthetic outpost profile");

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "soulcrest-stronghold-dispatch-" + Guid.NewGuid().ToString("N"));
        internal ProgressService Progress { get; }
        internal ExplorationService Exploration { get; }
        internal NetworkLootService Network { get; }
        internal Fixture()
        {
            Directory.CreateDirectory(_root);
            Progress = new(Path.Combine(_root, "progress.json"), new PetCatalog([
                new PetDefinition { Id = "fossa", En = "Fossa", Genus = "Fera", NumericId = "1004" }]));
            Exploration = new(Path.Combine(_root, "exploration.json"), [
                new("fort", "altgard", "stronghold", 0, "Synthetic Fort", null, QuestId: 2820012, StrongholdInstanceId: 142017),
                new("side", "altgard", "stronghold", 1, "Synthetic Side Gate", null, StrongholdInstanceId: 142017),
                new("other", "altgard", "stronghold", 2, "Synthetic Other Fort", null, StrongholdInstanceId: 142018),
                new("legacy", "altgard", "stronghold", 3, "Synthetic Legacy Field", null, DungeonId: 142017),
                new("wrong-kind", "altgard", "dungeon", 4, "Synthetic Wrong Kind", null, StrongholdInstanceId: 142017)]);
            Network = new(Progress, Exploration);
        }
        public void Dispose() { Network.Dispose(); Directory.Delete(_root, recursive: true); }
    }

    private static byte[] Packet(ushort opcode, Action<BinaryWriter> body)
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        writer.Write(opcode); body(writer); return bytes.ToArray();
    }
    private static byte[] Completion(int instance = 142017, bool succeeded = true) => Packet(0x6101, writer =>
    {
        writer.Write((byte)0); writer.Write(instance); writer.Write(succeeded);
        writer.Write((byte)1); writer.Write(1004); writer.Write(8L);
    });

    [Fact]
    public void OwnBoundResultCompletesExactStrongholdPlacementsWithoutCountingItsRewardsAsSouls()
    {
        using var f = new Fixture();
        f.Network.ProcessProgressMessage("game", Completion(), At);
        Assert.Empty(f.Exploration.Completed);
        Assert.Null(f.Network.ProgressSources.DungeonActivityAt);
        f.Network.ObserveIdentity("game", Own, At.AddSeconds(1));
        Assert.Empty(f.Exploration.Completed); // An unbound result cannot initialize a profile.
        f.Network.ProcessProgressMessage("game", Completion(), At.AddSeconds(1));
        Assert.Equal(new[] { "fort", "side" }, f.Exploration.Completed.Order());
        Assert.Equal("network-stronghold-completion", f.Exploration.Snapshot().Progress["fort"].Source);
        Assert.Null(f.Network.QuestBaselineAt);
        Assert.Null(f.Network.PetBaselineAt);
        Assert.Empty(f.Progress.Recent());
        Assert.Empty(f.Network.Recent);
        Assert.Equal(0, f.Progress.Souls("fossa"));
        Assert.Equal(At.AddSeconds(1), f.Network.ProgressSources.DungeonActivityAt);

        f.Network.ProcessProgressMessage("game", Packet(0x900D, writer =>
        { writer.Write((byte)1); writer.Write(1004); writer.Write(2); }), At.AddSeconds(2));
        Assert.Equal(2, f.Progress.Souls("fossa"));
        Assert.Single(f.Progress.Recent());
        Assert.Single(f.Network.Recent);
    }

    [Fact]
    public void FailedUnknownAndUnsupportedStrongholdResultsCannotCompleteOrBreakLaterSoulTracking()
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
        f.Network.ProcessProgressMessage("game", Completion(), At.AddSeconds(1));
        Assert.Equal(new[] { "fort", "side" }, f.Exploration.Completed.Order());
        f.Network.ProcessProgressMessage("game", Packet(0x900D, writer =>
        { writer.Write((byte)1); writer.Write(1004); writer.Write(2); }), At.AddSeconds(2));
        Assert.Equal(2, f.Progress.Souls("fossa"));
        Assert.True(f.Network.ProgressSources.DungeonFormatRejected);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void RunStateAloneDoesNotInventStrongholdCompletionOrDiscovery(byte state)
    {
        using var f = new Fixture();
        f.Network.ObserveIdentity("game", Own, At);
        f.Network.ProcessProgressMessage("game", Packet(0x6100, writer =>
        { writer.Write(142017); writer.Write(state); writer.Write(0L); writer.Write(0L); }), At);
        Assert.Empty(f.Exploration.Completed);
        Assert.Null(f.Exploration.Snapshot().Progress.GetValueOrDefault("fort")?.State);
        Assert.Null(f.Network.ProgressSources.DungeonActivityAt);
    }
}
