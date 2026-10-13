using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Soulcrest.Core.Progress;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class NetworkKibeliskDispatchTests
{
    private static readonly OwnCharacterIdentity Own = new(101, 501, 100, "Synthetic Kibelisk profile");
    private static readonly DateTimeOffset At = new(2026, 10, 13, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BindingConfirmsOnlyItsMappedKibeliskWithoutInventingAFullBaselineOrSouls()
    {
        using var f = new Fixture();
        f.Network.ObserveIdentity("game", Own, At);
        f.Network.ProcessProgressMessage("game", Binding(), At.AddSeconds(1));

        Assert.Equal(Own.CharacterId, f.Exploration.Snapshot().NetworkCharacterId);
        var place = f.Exploration.Snapshot().Progress["pillar"];
        Assert.Equal(ExplorationProgressState.DiscoveredCompleted, place.State);
        Assert.Equal(ProgressFreshness.Fresh, place.Freshness);
        Assert.Equal("network-kibelisk-binding", place.Source);
        Assert.Equal("network-teleport-binding", place.RawSource);
        Assert.True(place.TeleportPresent);
        Assert.Contains("pillar", f.Exploration.Completed);
        Assert.DoesNotContain("other", f.Exploration.Completed);
        Assert.Null(f.Exploration.Snapshot().Progress["other"].State);
        var sources = f.Network.ProgressSources;
        Assert.Equal(At.AddSeconds(1), sources.KibeliskActivityAt);
        Assert.False(sources.KibeliskFormatRejected);
        Assert.False(sources.HasAnyBaseline);
        Assert.Null(sources.DungeonActivityAt);
        Assert.Empty(f.Progress.Recent());
        Assert.Empty(f.Network.Recent);
        f.Network.ProcessProgressMessage("game", Gain(), At.AddSeconds(2));
        Assert.Equal(2, f.Progress.Souls("fossa"));
        Assert.Single(f.Progress.Recent());
        Assert.Single(f.Network.Recent);
    }

    [Fact]
    public void PreIdentityBindingWaitsForItsOwnStreamAndForeignStreamsCannotWrite()
    {
        using var f = new Fixture();
        f.Network.ProcessProgressMessage("game", Binding(), At);
        Assert.Empty(f.Exploration.Completed);
        Assert.Null(f.Network.ProgressSources.KibeliskActivityAt);
        Assert.Equal(0, f.Network.ProgressDiagnostics.PendingMessages);
        f.Network.ObserveIdentity("other", Own, At.AddSeconds(1));
        Assert.Empty(f.Exploration.Completed);
        Assert.Equal(0, f.Network.ProgressDiagnostics.PendingMessages);
        f.Network.ProcessProgressMessage("game", Binding(), At.AddSeconds(2));
        f.Network.ProcessProgressMessage("game", [.. Binding(), 0], At.AddSeconds(2));
        Assert.Empty(f.Exploration.Completed);
        Assert.False(f.Network.ProgressSources.KibeliskFormatRejected);
        f.Network.ResetProgressContext();
        f.Network.ProcessProgressMessage("game", Binding(), At.AddSeconds(3));
        f.Network.ObserveIdentity("game", Own, At.AddSeconds(4));
        Assert.Empty(f.Exploration.Completed); // A singleton is never an entry baseline.
        f.Network.ProcessProgressMessage("game", Binding(), At.AddSeconds(5));
        Assert.Contains("pillar", f.Exploration.Completed);
        Assert.Equal(At.AddSeconds(5), f.Network.ProgressSources.KibeliskActivityAt);
        Assert.Equal(0, f.Network.ProgressDiagnostics.PendingMessages);
        Assert.Null(f.Network.TeleportBaselineAt);
    }

    [Theory]
    [InlineData(0x3621, true)]
    [InlineData(0x3633, true)]
    [InlineData(0x3621, false)]
    [InlineData(0x3633, false)]
    public void GlobalSoulsCountOnceWhileUnboundKibelisksNeverReplayAcrossAnUnsupportedOwnBoundary(ushort opcode, bool bindAffectedStream)
    {
        using var f = new Fixture();
        var affectedBinding = Binding();
        var unrelatedBinding = Binding(1052021);
        var gain = Gain();
        f.Network.ProcessProgressMessage("affected", affectedBinding, At);
        f.Network.ProcessProgressMessage("affected", gain, At);
        f.Network.ProcessProgressMessage("unrelated", unrelatedBinding, At);
        f.Network.ProcessProgressMessage("unrelated", gain, At);
        Assert.Equal(4, f.Progress.Souls("fossa"));
        Assert.Equal(2, f.Network.ProgressDiagnostics.AcceptedSoulMessages);
        Assert.Equal(4, f.Network.ProgressDiagnostics.AcceptedSoulPoints);
        Assert.Equal(2, f.Progress.Recent().Count);
        Assert.Equal(2, f.Network.Recent.Count);
        Assert.Empty(f.Exploration.Completed);
        Assert.Null(f.Network.ProgressSources.KibeliskActivityAt);
        Assert.Null(f.Network.PetBaselineAt);
        Assert.Equal(0, f.Network.ProgressDiagnostics.PendingMessages);
        f.Network.ProcessProgressMessage("affected", Packet(opcode, writer => writer.Write((byte)0)), At.AddSeconds(1));
        Assert.Equal(0, f.Network.ProgressDiagnostics.PendingMessages);
        Assert.Equal(0, f.Network.ProgressDiagnostics.PendingBytes);

        var nextOwn = Own with { CharacterId = 102, Name = "Synthetic Beta" };
        var stream = bindAffectedStream ? "affected" : "unrelated";
        f.Network.ObserveIdentity(stream, nextOwn, At.AddSeconds(2));
        Assert.Equal(nextOwn.CharacterId, f.Exploration.Snapshot().NetworkCharacterId);
        Assert.DoesNotContain("pillar", f.Exploration.Completed);
        Assert.DoesNotContain("other", f.Exploration.Completed);
        // The two earlier pickups already counted globally. Own binding must neither lose
        // them nor replay them; the two unbound Kibelisk singletons still have no authority.
        Assert.Equal(4, f.Progress.Souls("fossa"));
        Assert.Equal(2, f.Network.ProgressDiagnostics.AcceptedSoulMessages);
        Assert.Equal(4, f.Network.ProgressDiagnostics.AcceptedSoulPoints);
        Assert.Equal(2, f.Progress.Recent().Count);
        Assert.Equal(2, f.Network.Recent.Count);
        Assert.Null(f.Network.ProgressSources.KibeliskActivityAt);
        Assert.Null(f.Network.TeleportBaselineAt);
        Assert.Equal(0, f.Network.ProgressDiagnostics.PendingMessages);
        Assert.Equal(0, f.Network.ProgressDiagnostics.PendingBytes);
        f.Network.ProcessProgressMessage(stream, Binding(), At.AddSeconds(3));
        f.Network.ProcessProgressMessage(stream, Gain(), At.AddSeconds(3));
        Assert.Contains("pillar", f.Exploration.Completed);
        Assert.DoesNotContain("other", f.Exploration.Completed);
        Assert.Equal(At.AddSeconds(3), f.Network.ProgressSources.KibeliskActivityAt);
        Assert.Equal(6, f.Progress.Souls("fossa"));
        Assert.Equal(3, f.Network.ProgressDiagnostics.AcceptedSoulMessages);
        Assert.Equal(6, f.Network.ProgressDiagnostics.AcceptedSoulPoints);
        Assert.Equal(3, f.Progress.Recent().Count);
        Assert.Equal(3, f.Network.Recent.Count);
    }

    [Fact]
    public void ValidWorldEntryPreparationDropsThePreviousPendingBinding()
    {
        using var f = new Fixture();
        f.Network.ProcessProgressMessage("game", Binding(), At);
        var preparation = Packet(0x3621, writer => writer.Write(new byte[46]));
        Assert.True(OwnIdentityMessage.IsWorldEntryPreparation(preparation));
        f.Network.ProcessProgressMessage("game", preparation, At.AddSeconds(1));
        Assert.Equal(0, f.Network.ProgressDiagnostics.PendingMessages);
        f.Network.ObserveIdentity("game", Own, At.AddSeconds(2));
        Assert.Empty(f.Exploration.Completed);
        Assert.Null(f.Network.ProgressSources.KibeliskActivityAt);
        f.Network.ProcessProgressMessage("game", Binding(), At.AddSeconds(3));
        Assert.Contains("pillar", f.Exploration.Completed);
    }

    [Theory]
    [InlineData(0x3621)]
    [InlineData(0x3633)]
    public void UnsupportedOwnOnAnotherStreamCannotInvalidateTheBoundBindingOrLaterGains(ushort opcode)
    {
        using var f = new Fixture();
        f.Network.ObserveIdentity("game", Own, At);
        f.Network.ProcessProgressMessage("game", Binding(), At);
        f.Network.ProcessProgressMessage("other", Packet(opcode, writer => writer.Write((byte)0)), At.AddSeconds(1));
        Assert.Equal(Own.Name, f.Network.Character);
        Assert.Equal(At, f.Network.ProgressSources.KibeliskActivityAt);
        f.Network.ProcessProgressMessage("game", Binding(1052021), At.AddSeconds(2));
        f.Network.ProcessProgressMessage("game", Gain(), At.AddSeconds(2));
        Assert.Contains("pillar", f.Exploration.Completed);
        Assert.Contains("other", f.Exploration.Completed);
        Assert.Equal(2, f.Progress.Souls("fossa"));
    }

    [Theory]
    [InlineData("trailing")]
    [InlineData("truncated")]
    [InlineData("zero")]
    [InlineData("negative")]
    public void UnsupportedBindingFormatRetainsAllBaselinesAndConfirmedBindingsAndSoulsContinue(string invalid)
    {
        using var f = new Fixture();
        f.Network.ObserveIdentity("game", Own, At);
        f.Network.ProcessProgressMessage("game", PetSnapshot(), At);
        f.Network.ProcessProgressMessage("game", Quests(), At);
        f.Network.ProcessProgressMessage("game", Teleports(1052020), At);
        f.Network.ProcessProgressMessage("game", Binding(), At);
        byte[] malformed = invalid switch
        {
            "trailing" => [.. Binding(), 0],
            "truncated" => [0x33, 0x92, 1],
            "zero" => Binding(0),
            _ => Binding(-1),
        };
        f.Network.ProcessProgressMessage("game", malformed, At.AddSeconds(1));

        var sources = f.Network.ProgressSources;
        Assert.True(sources.KibeliskFormatRejected);
        Assert.False(sources.TeleportFormatRejected);
        Assert.False(sources.PetFormatRejected);
        Assert.False(sources.QuestFormatRejected);
        Assert.True(sources.HasAllBaselines);
        Assert.Equal(At, sources.PetBaselineAt);
        Assert.Equal(At, sources.QuestBaselineAt);
        Assert.Equal(At, sources.TeleportBaselineAt);
        Assert.Equal(At, sources.KibeliskActivityAt);
        Assert.Equal(Own.Name, f.Network.Character);
        Assert.Contains("pillar", f.Exploration.Completed);
        Assert.Equal(ProgressFreshness.Fresh, f.Exploration.Snapshot().Progress["pillar"].Freshness);
        f.Network.ProcessProgressMessage("game", Gain(), At.AddSeconds(2));
        Assert.Equal(10, f.Progress.Souls("fossa"));
        Assert.Single(f.Network.Recent);
        Assert.True(f.Network.ProgressSources.KibeliskFormatRejected);
        f.Network.ProcessProgressMessage("game", Teleports(1052020), At.AddSeconds(3));
        f.Network.ProcessProgressMessage("game", Binding(1052021), At.AddSeconds(4));
        Assert.True(f.Network.ProgressSources.KibeliskFormatRejected);
        Assert.Equal(At.AddSeconds(3), f.Network.TeleportBaselineAt);
        Assert.Equal(At.AddSeconds(4), f.Network.ProgressSources.KibeliskActivityAt);
        Assert.Contains("other", f.Exploration.Completed);
        f.Network.ObserveIdentity("game", Own, At.AddSeconds(5));
        Assert.False(f.Network.ProgressSources.KibeliskFormatRejected);
        Assert.Null(f.Network.ProgressSources.KibeliskActivityAt);
        Assert.Null(f.Network.TeleportBaselineAt);
        Assert.Contains("pillar", f.Exploration.Completed);
        Assert.Contains("other", f.Exploration.Completed);
    }

    [Fact]
    public void UnmappedPositiveIdDoesNotConfirmActivityOrInventAFormatError()
    {
        using var f = new Fixture();
        f.Network.ObserveIdentity("game", Own, At);
        f.Network.ProcessProgressMessage("game", Binding(999999), At);
        Assert.Empty(f.Exploration.Completed);
        Assert.Null(f.Network.ProgressSources.KibeliskActivityAt);
        Assert.False(f.Network.ProgressSources.KibeliskFormatRejected);
        Assert.Null(f.Network.TeleportBaselineAt);
        f.Network.ProcessProgressMessage("game", Binding(), At.AddSeconds(1));
        Assert.Contains("pillar", f.Exploration.Completed);
    }

    [Fact]
    public void GapDropsLiveBindingEvidenceButKeepsTheSavedBindingAndCannotReplayTheOldSession()
    {
        using var f = new Fixture();
        f.Network.ObserveIdentity("game", Own, At);
        f.Network.ProcessProgressMessage("game", Binding(), At);
        f.Network.ProcessProgressMessage("game", [.. Binding(), 0], At.AddSeconds(1));
        f.Network.ProcessProgressGap("game");
        Assert.Null(f.Network.Character);
        Assert.Null(f.Network.ProgressSources.KibeliskActivityAt);
        Assert.False(f.Network.ProgressSources.KibeliskFormatRejected);
        Assert.Contains("pillar", f.Exploration.Completed);
        Assert.Equal(ProgressFreshness.Stale, f.Exploration.Snapshot().Progress["pillar"].Freshness);
        f.Network.ProcessProgressMessage("game", Binding(1052021), At.AddSeconds(2));
        Assert.DoesNotContain("other", f.Exploration.Completed);
        f.Network.ObserveIdentity("new-game", Own, At.AddSeconds(3));
        Assert.Contains("pillar", f.Exploration.Completed);
        Assert.DoesNotContain("other", f.Exploration.Completed);
    }

    private static byte[] Packet(ushort opcode, Action<BinaryWriter> write)
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        writer.Write(opcode); write(writer);
        return bytes.ToArray();
    }
    private static byte[] Binding(int teleportId = 1052020) => Packet(0x9233, writer => writer.Write(teleportId));
    private static byte[] Gain() => Packet(0x900D, writer => { writer.Write((byte)1); writer.Write(1004); writer.Write(2); });
    private static byte[] PetSnapshot() => Packet(0x9000, writer =>
    {
        writer.Write(0); writer.Write((byte)1); writer.Write(999); writer.Write(1004); writer.Write(1);
        writer.Write((byte)1); writer.Write(1004); writer.Write(3); writer.Write(new byte[4]);
    });
    private static byte[] Quests() => Packet(0x8D09, writer => writer.Write((byte)0));
    private static byte[] Teleports(params int[] ids) => Packet(0x9232, writer =>
    { writer.Write((byte)ids.Length); foreach (var id in ids) writer.Write(id); });

    private sealed class Fixture : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-kibelisk-dispatch-" + Guid.NewGuid().ToString("N"));
        internal ProgressService Progress { get; }
        internal ExplorationService Exploration { get; }
        internal NetworkLootService Network { get; }
        internal Fixture()
        {
            Directory.CreateDirectory(_folder);
            Progress = new(Path.Combine(_folder, "progress.json"), new PetCatalog([
                new PetDefinition { Id = "fossa", En = "Fossa", Genus = "Fera", NumericId = "1004" }]));
            Exploration = new(Path.Combine(_folder, "exploration.json"), [
                new("pillar", "altgard", "kibelisk", 0, "Synthetic pillar", null, TeleportId: 1052020),
                new("other", "altgard", "kibelisk", 1, "Synthetic second pillar", null, TeleportId: 1052021)]);
            Network = new(Progress, Exploration);
        }
        public void Dispose() { Network.Dispose(); Directory.Delete(_folder, recursive: true); }
    }
}
