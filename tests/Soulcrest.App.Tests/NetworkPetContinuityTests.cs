using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Soulcrest.Core.Progress;
using Xunit;

namespace Soulcrest.App.Tests;

[Collection("Settings file")]
public sealed class NetworkPetContinuityTests
{
    private const int Slime = 1062;
    private static readonly OwnCharacterIdentity Own = new(101, 501, 300, "SyntheticContinuity");
    private static readonly DateTimeOffset At = new(2026, 10, 13, 11, 0, 0, TimeSpan.Zero);
    private const string Game = "synthetic-world";

    private sealed class Fixture : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-pet-continuity-" + Guid.NewGuid().ToString("N"));
        private readonly string? _previousData = Environment.GetEnvironmentVariable("SOULCREST_DATA");
        public ProgressService Progress { get; }
        public ExplorationService Exploration { get; }
        public NetworkLootService Network { get; }
        public List<NetworkSoul> Gains { get; } = [];
        public string PetId { get; }
        public Fixture()
        {
            Directory.CreateDirectory(_folder);
            Environment.SetEnvironmentVariable("SOULCREST_DATA", _folder);
            var source = PetIdTable.Find(Slime)!;
            PetId = source.CatalogId ?? "synthetic-slime";
            Progress = new(Path.Combine(_folder, "progress.json"), new PetCatalog([
                new PetDefinition { Id = PetId, En = source.En, Genus = source.Genus, NumericId = Slime.ToString(CultureInfo.InvariantCulture) }]));
            Exploration = new(Path.Combine(_folder, "exploration.json"), [
                new("cave", "altgard", "dungeon", 0, "Synthetic Cave", null, QuestId: 2810034),
                new("pillar", "altgard", "kibelisk", 1, "Synthetic Pillar", null, TeleportId: 1052020)]);
            Network = new(Progress, Exploration);
            typeof(NetworkLootService).GetProperty(nameof(NetworkLootService.Running))!.SetValue(Network, true);
            Network.SoulGained += Gains.Add;
        }
        public void Send(byte[] message, int second, string stream = Game) => Network.ProcessProgressMessage(stream, message, At.AddSeconds(second));
        public void Enter(int second = 3, OwnCharacterIdentity? own = null, string stream = Game) => Network.ObserveIdentity(stream, own ?? Own, At.AddSeconds(second));
        public void Bind()
        {
            Enter(0); Send(Pets(), 1);
            Send(Packet(0x8D09, w =>
            {
                w.Write((byte)1); w.Write(2810034); w.Write((byte)4); w.Write((byte)1);
                w.Write(new byte[] { 1, 0, 0, 0, 0, 0 }); w.Write(1L); w.Write(new byte[2]);
            }), 1);
            Send(Packet(0x9232, w => { w.Write((byte)1); w.Write(1052020); }), 1);
            Assert.True(Network.ProgressSources.InitialProgressConfirmed);
        }
        public bool HasBaseline(int number = Slime) =>
            ((ProgressProfile)typeof(ProgressService).GetField("_profile", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(Progress)!).NetworkPets[number].HasBaseline;
        public ProgressProfile Stored()
        {
            var root = JsonSerializer.Deserialize<ProgressProfile>(File.ReadAllText(Path.Combine(_folder, "progress.json")))!;
            return root.ActiveServerId is { } server && server != root.ServerId ? root.Servers[server] : root;
        }
        public void Dispose()
        {
            Network.Dispose();
            Environment.SetEnvironmentVariable("SOULCREST_DATA", _previousData);
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void ContinuousSameServerMapEntryKeepsTheOriginalConfirmedBalanceAndCountsTheNextDropOnce()
    {
        using var f = new Fixture(); f.Bind();
        var original = f.Progress.EntrySnapshot(f.PetId);
        f.Send(Prepare(), 2);
        Assert.Null(f.Network.PetBaselineAt);
        Assert.Equal(ProgressFreshness.Stale, f.Progress.EntrySnapshot(f.PetId).Freshness);
        f.Enter();
        Assert.Equal(At.AddSeconds(1), f.Network.PetBaselineAt);
        Assert.Equal(At.AddSeconds(1), f.Progress.NetworkBaselineAt);
        Assert.Equal(21, f.Progress.AvailableSouls(f.PetId));
        Assert.Equal(1, f.Progress.ObservedLevel(f.PetId));
        Assert.True(f.Stored().NetworkPets[Slime].HasBaseline);
        var carried = f.Progress.EntrySnapshot(f.PetId);
        Assert.Equal(original.Source, carried.Source);
        Assert.Equal(original.UpdatedAt, carried.UpdatedAt);
        Assert.Equal(ProgressFreshness.Fresh, carried.Freshness);
        Assert.Null(f.Network.QuestBaselineAt);
        Assert.Null(f.Network.TeleportBaselineAt);
        Assert.False(f.Network.ProgressSources.InitialProgressConfirmed);
        Assert.Equal(ProgressFreshness.Stale, f.Exploration.Snapshot().Progress["cave"].Freshness);
        Assert.Equal(ProgressFreshness.Stale, f.Exploration.Snapshot().Progress["pillar"].Freshness);
        Assert.Empty(f.Progress.Recent());
        Assert.Empty(f.Gains);
        Assert.Single(f.Network.ProgressDiagnostics.Events, e => e.Kind == NetworkProgressEventKind.Snapshot);
        f.Send(Points(0x900D, 1), 4);
        Assert.Equal(22, f.Progress.AvailableSouls(f.PetId));
        Assert.Equal(27, f.Progress.Souls(f.PetId));
        Assert.Equal(ProgressFreshness.Fresh, f.Progress.EntrySnapshot(f.PetId).Freshness);
        Assert.Equal(1, Assert.Single(f.Gains).Souls);
        Assert.Single(f.Progress.Recent());
    }

    [Theory]
    [InlineData(101L, 501, true)]
    [InlineData(202L, 501, true)]
    [InlineData(101L, 502, false)]
    [InlineData(202L, 502, false)]
    public void PetContinuityUsesTheServerWhileExplorationStillUsesTheOwnCharacter(long character, int server, bool carried)
    {
        using var f = new Fixture(); f.Bind();
        var explorationGuid = f.Exploration.ActiveId;
        f.Send(Prepare(), 2);
        f.Enter(own: Own with { CharacterId = character, ServerId = server, EntityKey = 301 });
        Assert.Equal(carried, f.Network.PetBaselineAt is not null);
        Assert.Equal(carried ? 21 : 0, f.Progress.SoulsInLevel(f.PetId));
        Assert.Equal(server, f.Progress.ActiveServerId);
        Assert.Equal(server, f.Exploration.Snapshot().ServerId);
        Assert.Equal(character, f.Exploration.Snapshot().NetworkCharacterId);
        if (character != Own.CharacterId || server != Own.ServerId)
        {
            Assert.NotEqual(explorationGuid, f.Exploration.ActiveId);
            Assert.Empty(f.Exploration.Completed);
        }
        Assert.Null(f.Network.QuestBaselineAt);
        Assert.Null(f.Network.TeleportBaselineAt);
        Assert.Empty(f.Gains);
    }

    [Theory]
    [InlineData("gap")]
    [InlineData("global-gap")]
    [InlineData("closed")]
    [InlineData("stopped")]
    [InlineData("new-candidate")]
    [InlineData("unsupported-candidate")]
    [InlineData("unsupported-own")]
    [InlineData("unsupported-prepare")]
    [InlineData("second-prepare")]
    [InlineData("wrong-stream")]
    [InlineData("expired")]
    [InlineData("negative-time")]
    public void UncertainBoundariesDiscardTheContinuityTokenWithoutInventingANewPetBaseline(string variant)
    {
        using var f = new Fixture(); f.Bind(); f.Send(Prepare(), 2);
        switch (variant)
        {
            case "gap": f.Network.ProcessProgressGap(Game); break;
            case "global-gap": f.Network.ProcessProgressGap("*"); break;
            case "closed": typeof(NetworkLootService).GetMethod("CloseDevice", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(f.Network, null); break;
            case "stopped": f.Network.Stop(); break;
            case "new-candidate": f.Send(Candidate(), 2); break;
            case "unsupported-candidate": f.Send([0x20, 0x36, 0], 2); break;
            case "unsupported-own": f.Send([0x33, 0x36, 0], 2); break;
            case "unsupported-prepare": f.Send([0x21, 0x36, 0], 2); break;
            case "second-prepare": f.Send(Prepare(), 2); break;
        }
        f.Enter(variant == "expired" ? 123 : variant == "negative-time" ? 1 : 3, stream: variant == "wrong-stream" ? "other-world" : Game);
        Assert.Null(f.Network.PetBaselineAt);
        Assert.False(f.Stored().NetworkPets[Slime].HasBaseline);
        Assert.NotEqual(ProgressFreshness.Fresh, f.Progress.EntrySnapshot(f.PetId).Freshness);
        Assert.Empty(f.Gains);
    }

    [Theory]
    [InlineData(0x9000)]
    [InlineData(0x9009)]
    [InlineData(0x900D)]
    [InlineData(0x900E)]
    [InlineData(0x9014)]
    public void AnyUnreadPetUpdateInvalidatesContinuityEvenWhenItsBodyCannotBeDecoded(ushort opcode)
    {
        using var f = new Fixture(); f.Bind(); f.Send(Prepare(), 2);
        f.Send(Packet(opcode, _ => { }), 2);
        f.Enter();
        Assert.Null(f.Network.PetBaselineAt);
        Assert.False(f.Stored().NetworkPets[Slime].HasBaseline);
        Assert.Equal(21, f.Progress.SoulsInLevel(f.PetId));
        Assert.Empty(f.Gains);
    }

    [Theory]
    [InlineData(0x9000)]
    [InlineData(0x9009)]
    [InlineData(0x900D)]
    [InlineData(0x900E)]
    [InlineData(0x9014)]
    public void ValidPetPacketsDuringTheUnboundTransitionAreNeverSilentlyCarriedOrReplayed(ushort opcode)
    {
        using var f = new Fixture(); f.Bind(); f.Send(Prepare(), 2);
        var packet = opcode switch
        {
            0x9000 => Pets(),
            0x9009 => Packet(opcode, w => { V(w, 300); w.Write(Slime); w.Write(2); w.Write(new byte[13]); }),
            0x9014 => Packet(opcode, w => { w.Write((ushort)0); w.Write(Slime); w.Write(2); w.Write(new byte[13]); }),
            _ => Points(opcode, 1),
        };
        f.Send(packet, 2); f.Enter();
        Assert.Null(f.Network.PetBaselineAt);
        Assert.Equal(1, f.Progress.Level(f.PetId));
        Assert.Equal(21, f.Progress.SoulsInLevel(f.PetId));
        Assert.Equal(26, f.Progress.Souls(f.PetId));
        Assert.False(f.Stored().NetworkPets[Slime].HasBaseline);
        Assert.Empty(f.Gains);
        Assert.Empty(f.Progress.Recent());
    }

    [Fact]
    public void UnrelatedExplorationPacketsAndForeignGapsOrPetMessagesDoNotBreakServerPetContinuity()
    {
        using var f = new Fixture(); f.Bind(); f.Send(Prepare(), 2);
        f.Send([0x09, 0x8D, 0xFF], 2);
        f.Send([0x32, 0x92, 0xFF], 2);
        f.Send(Points(0x900D, 1), 2, "foreign-world");
        f.Send(Prepare(), 2, "foreign-world");
        f.Network.ProcessProgressGap("foreign-world");
        f.Enter();
        Assert.Equal(At.AddSeconds(1), f.Network.PetBaselineAt);
        Assert.Equal(21, f.Progress.AvailableSouls(f.PetId));
        Assert.True(f.Stored().NetworkPets[Slime].HasBaseline);
        Assert.Empty(f.Gains);
    }

    [Fact]
    public void ARejectedPetSourceBeforePrepareCannotBePromotedBackToConfirmedByTheNextOwn()
    {
        using var f = new Fixture(); f.Bind();
        f.Send([0x09, 0x90, 0xFF], 2);
        Assert.Null(f.Network.PetBaselineAt);
        f.Send(Prepare(), 3); f.Enter(4);
        Assert.Null(f.Network.PetBaselineAt);
        Assert.Null(f.Progress.AvailableSouls(f.PetId));
        Assert.False(f.Stored().NetworkPets[Slime].HasBaseline);
        Assert.NotEqual(ProgressFreshness.Fresh, f.Progress.EntrySnapshot(f.PetId).Freshness);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ManualValuesRemainProtectedWhetherCorrectedBeforeOrDuringTheTransition(bool during)
    {
        using var f = new Fixture(); f.Bind();
        if (!during) f.Progress.SetSouls(f.PetId, 8);
        f.Send(Prepare(), 2);
        if (during) f.Progress.SetSouls(f.PetId, 8);
        var manual = f.Progress.EntrySnapshot(f.PetId);
        f.Enter();
        var after = f.Progress.EntrySnapshot(f.PetId);
        Assert.Equal(manual.Souls, after.Souls);
        Assert.Equal(manual.Source, after.Source);
        Assert.Equal(manual.UpdatedAt, after.UpdatedAt);
        Assert.Equal(manual.Freshness, after.Freshness);
        Assert.True(after.ManualOverride);
        Assert.Null(f.Progress.AvailableSouls(f.PetId));
        Assert.True(f.Stored().NetworkPets[Slime].HasBaseline);
        Assert.Equal(21, f.Stored().NetworkPets[Slime].AvailableSouls);
        Assert.Equal(At.AddSeconds(1), f.Network.PetBaselineAt);
    }

    [Fact]
    public void PreviouslyUnconfirmedPartialPetEntriesAreNotFreshenedAlongsideTheConfirmedServerCollection()
    {
        using var f = new Fixture(); f.Bind();
        f.Send(Points(0x900D, 2, pet: 1004), 2);
        var other = f.Progress.Catalog.Pets.Single(p => f.Progress.SoulId(p.Id) == 1004).Id;
        Assert.False(f.Stored().NetworkPets[1004].HasBaseline);
        f.Send(Prepare(), 3); f.Enter(4);
        Assert.True(f.Stored().NetworkPets[Slime].HasBaseline);
        Assert.False(f.Stored().NetworkPets[1004].HasBaseline);
        Assert.Equal(ProgressFreshness.Unknown, f.Progress.EntrySnapshot(other).Freshness);
        Assert.Null(f.Progress.AvailableSouls(other));
        Assert.Equal(2, f.Progress.SoulsInLevel(other));
        Assert.Single(f.Gains);
    }

    [Fact]
    public void ANewVerifiedLoginSnapshotSupersedesTheOldContinuityState()
    {
        using var f = new Fixture(); f.Bind(); f.Send(Prepare(), 2);
        f.Send(Candidate(), 3); f.Send(Pets(23), 4); f.Send(Prepare(), 5); f.Enter(6);
        Assert.Equal(At.AddSeconds(4), f.Network.PetBaselineAt);
        Assert.Equal(23, f.Progress.AvailableSouls(f.PetId));
        Assert.Equal(At.AddSeconds(4), f.Progress.EntrySnapshot(f.PetId).UpdatedAt);
        Assert.Empty(f.Gains);
        Assert.Equal(2, f.Network.ProgressDiagnostics.Events.Count(e => e.Kind == NetworkProgressEventKind.Snapshot));
    }

    [Fact]
    public void ServiceTokenIsSingleUseAndCannotOverwriteAcceptedNewData()
    {
        using var f = new Fixture(); f.Bind();
        var original = new NetworkProgressContext(Own, 1, 3, At.AddSeconds(2));
        var token = Assert.IsType<ProgressService.NetworkPetContinuityToken>(f.Progress.CaptureNetworkContinuity(original));
        f.Progress.MarkNetworkStale();
        var bound = new NetworkProgressContext(Own with { CharacterId = 202 }, 2, 0, At.AddSeconds(3));
        Assert.True(f.Progress.BindNetworkContext(bound));
        Assert.True(f.Progress.TryRestoreNetworkContinuity(token, bound));
        Assert.False(f.Progress.TryRestoreNetworkContinuity(token, bound));

        var next = Assert.IsType<ProgressService.NetworkPetContinuityToken>(f.Progress.CaptureNetworkContinuity(bound));
        f.Progress.MarkNetworkStale();
        var third = bound with { Generation = 3 };
        Assert.True(f.Progress.BindNetworkContext(third));
        Assert.True(f.Progress.ApplyNetworkGain([new(Slime, 1)], third with { Sequence = 1 }));
        Assert.False(f.Progress.TryRestoreNetworkContinuity(next, third with { Sequence = 2 }));
        Assert.False(f.Stored().NetworkPets[Slime].HasBaseline);
        Assert.Equal(22, f.Progress.SoulsInLevel(f.PetId));
    }

    [Fact]
    public void SourceRejectionAfterTokenCaptureCannotBeUndoneByRestoringTheOldToken()
    {
        using var f = new Fixture(); f.Bind();
        var token = Assert.IsType<ProgressService.NetworkPetContinuityToken>(f.Progress.CaptureNetworkContinuity(new(Own, 1, 3, At)));
        f.Progress.MarkNetworkPetSourceStale();
        f.Progress.MarkNetworkStale();
        var bound = new NetworkProgressContext(Own, 2, 0, At.AddSeconds(3));
        Assert.True(f.Progress.BindNetworkContext(bound));
        Assert.False(f.Progress.TryRestoreNetworkContinuity(token, bound));
        Assert.False(f.Stored().NetworkPets[Slime].HasBaseline);
        Assert.Null(f.Progress.AvailableSouls(f.PetId));
    }

    [Fact]
    public void TheExistingTwoMinuteInclusiveTransitionLimitCanStillCarryTheConfirmedServerState()
    {
        using var f = new Fixture(); f.Bind(); f.Send(Prepare(), 2); f.Enter(122);
        Assert.Equal(At.AddSeconds(1), f.Network.PetBaselineAt);
        Assert.Equal(21, f.Progress.AvailableSouls(f.PetId));
        Assert.True(f.Stored().NetworkPets[Slime].HasBaseline);
    }

    [Theory]
    [InlineData("prepare", 0x900D)]
    [InlineData("prepare", 0x900E)]
    [InlineData("prepare", 0x9014)]
    [InlineData("own", 0x900D)]
    [InlineData("own", 0x900E)]
    [InlineData("own", 0x9014)]
    public void UnboundPetMessagesInSynchronousResetCallbacksPreventOldConfirmationFromReturning(string phase, ushort opcode)
    {
        using var f = new Fixture(); f.Bind();
        if (phase == "own") f.Send(Prepare(), 2);
        var fired = false;
        f.Progress.Changed += () =>
        {
            if (fired || f.Network.Character is not null) return;
            fired = true;
            f.Send(opcode == 0x9014 ? Packet(opcode, w =>
            { w.Write((ushort)0); w.Write(Slime); w.Write(2); w.Write(new byte[13]); }) : Points(opcode, 1), 2);
        };
        if (phase == "prepare") f.Send(Prepare(), 2);
        f.Enter();
        Assert.True(fired);
        Assert.Equal(Own.Name, f.Network.Character);
        Assert.Null(f.Network.PetBaselineAt);
        Assert.False(f.Stored().NetworkPets[Slime].HasBaseline);
        Assert.NotEqual(ProgressFreshness.Fresh, f.Progress.EntrySnapshot(f.PetId).Freshness);
        Assert.Equal(21, f.Progress.SoulsInLevel(f.PetId));
        Assert.Empty(f.Gains);
        Assert.Empty(f.Progress.Recent());
        if (opcode == 0x900D)
        {
            Assert.Equal(1, f.Network.ProgressDiagnostics.UnboundSoulMessages);
            Assert.Equal(1, f.Network.ProgressDiagnostics.UnboundSoulPoints);
        }
    }

    [Theory]
    [InlineData("gap")]
    [InlineData("stop")]
    [InlineData("malformed-upgrade")]
    public void RestoreCallbacksCannotOverwriteAResetOrRejectedSourceWithTheOldBaseline(string action)
    {
        using var f = new Fixture(); f.Bind(); f.Send(Prepare(), 2);
        var fired = false;
        f.Progress.Changed += () =>
        {
            if (fired || f.Progress.EntrySnapshot(f.PetId).Freshness != ProgressFreshness.Fresh) return;
            fired = true;
            switch (action)
            {
                case "gap": f.Network.ProcessProgressGap(Game); break;
                case "stop": f.Network.Stop(); break;
                default: f.Send([0x14, 0x90, 0], 4); break;
            }
        };
        f.Enter();
        Assert.True(fired);
        Assert.Null(f.Network.PetBaselineAt);
        Assert.False(f.HasBaseline());
        Assert.NotEqual(ProgressFreshness.Fresh, f.Progress.EntrySnapshot(f.PetId).Freshness);
        Assert.Equal(action == "malformed-upgrade", f.Network.Character is not null);
        Assert.Equal(action == "malformed-upgrade", f.Network.ProgressSources.PetFormatRejected);
        Assert.Empty(f.Gains);
    }

    [Fact]
    public void AResetDuringTheBindCallbackCannotBindExplorationOrAnnounceTheInvalidatedOwn()
    {
        using var f = new Fixture(); f.Bind(); f.Send(Prepare(), 2);
        var fired = false;
        var announced = 0;
        f.Network.OwnCharacterEntered += _ => announced++;
        f.Progress.Changed += () =>
        {
            if (fired || f.Network.Character is null) return;
            fired = true;
            f.Network.ProcessProgressGap(Game);
        };
        f.Enter();
        Assert.True(fired);
        Assert.Equal(0, announced);
        Assert.Null(f.Network.Character);
        Assert.Null(f.Network.PetBaselineAt);
        Assert.False(f.Network.ProgressSources.HasAnyBaseline);
        Assert.False(f.Stored().NetworkPets[Slime].HasBaseline);
    }

    [Fact]
    public void AForeignUnboundPetMessageInAnOwnResetCallbackDoesNotInvalidateServerContinuity()
    {
        using var f = new Fixture(); f.Bind(); f.Send(Prepare(), 2);
        var fired = false;
        f.Progress.Changed += () =>
        {
            if (fired || f.Network.Character is not null) return;
            fired = true;
            f.Send(Points(0x900D, 1), 2, "foreign-world");
        };
        f.Enter();
        Assert.True(fired);
        Assert.Equal(At.AddSeconds(1), f.Network.PetBaselineAt);
        Assert.True(f.Stored().NetworkPets[Slime].HasBaseline);
        Assert.Equal(ProgressFreshness.Fresh, f.Progress.EntrySnapshot(f.PetId).Freshness);
        Assert.Equal(21, f.Progress.AvailableSouls(f.PetId));
        Assert.Empty(f.Gains);
    }

    [Theory]
    [InlineData("prepare", "gap")]
    [InlineData("prepare", "unsupported-own")]
    [InlineData("own", "gap")]
    [InlineData("own", "unsupported-own")]
    public void SameStreamBoundariesInAResetCallbackInvalidateTheInFlightToken(string phase, string boundary)
    {
        using var f = new Fixture(); f.Bind();
        if (phase == "own") f.Send(Prepare(), 2);
        var fired = false;
        f.Progress.Changed += () =>
        {
            if (fired || f.Network.Character is not null) return;
            fired = true;
            if (boundary == "gap") f.Network.ProcessProgressGap(Game);
            else f.Send([0x33, 0x36, 0], 2);
        };
        if (phase == "prepare") f.Send(Prepare(), 2);
        f.Enter();
        Assert.True(fired);
        Assert.Null(f.Network.PetBaselineAt);
        Assert.False(f.HasBaseline());
        Assert.NotEqual(ProgressFreshness.Fresh, f.Progress.EntrySnapshot(f.PetId).Freshness);
        Assert.Equal(phase == "prepare", f.Network.Character is not null);
        Assert.Empty(f.Gains);
    }

    [Fact]
    public void ForeignEntryCandidatesPreparationsAndGapsInAResetCallbackDoNotDiscardTheCarry()
    {
        using var f = new Fixture(); f.Bind(); f.Send(Prepare(), 2);
        var fired = false;
        f.Progress.Changed += () =>
        {
            if (fired || f.Network.Character is not null) return;
            fired = true;
            f.Send(Candidate(), 2, "foreign-world");
            f.Send(Prepare(), 2, "foreign-world");
            f.Network.ProcessProgressGap("foreign-world");
        };
        f.Enter();
        Assert.True(fired);
        Assert.Equal(Own.Name, f.Network.Character);
        Assert.Equal(At.AddSeconds(1), f.Network.PetBaselineAt);
        Assert.True(f.HasBaseline());
        Assert.Equal(21, f.Progress.AvailableSouls(f.PetId));
        Assert.Equal(0, f.Network.ProgressDiagnostics.PendingMessages);
        Assert.Empty(f.Gains);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ValidBoundUpdatesInTheRestoreCallbackAreCountedOnceAndKeepTheirNewerObservation(bool fullSnapshot)
    {
        using var f = new Fixture(); f.Bind(); f.Send(Prepare(), 2);
        var fired = false;
        f.Progress.Changed += () =>
        {
            if (fired || f.Progress.EntrySnapshot(f.PetId).Freshness != ProgressFreshness.Fresh) return;
            fired = true;
            f.Send(fullSnapshot ? Pets(23) : Points(0x900D, 1), 4);
        };
        f.Enter();
        Assert.True(fired);
        Assert.Equal(fullSnapshot ? At.AddSeconds(4) : At.AddSeconds(1), f.Network.PetBaselineAt);
        Assert.Equal(At.AddSeconds(4), f.Network.ProgressSources.PetActivityAt);
        Assert.True(f.Stored().NetworkPets[Slime].HasBaseline);
        Assert.Equal(fullSnapshot ? 23 : 22, f.Progress.AvailableSouls(f.PetId));
        Assert.Equal(fullSnapshot ? 28 : 27, f.Progress.Souls(f.PetId));
        Assert.Equal(ProgressFreshness.Fresh, f.Progress.EntrySnapshot(f.PetId).Freshness);
        Assert.Equal(fullSnapshot ? 0 : 1, f.Gains.Count);
        Assert.Equal(fullSnapshot ? 0 : 1, f.Progress.Recent().Count);
    }

    private static byte[] Prepare() => Packet(0x3621, w => w.Write(new byte[46]));
    private static byte[] Candidate() => Packet(0x3620, w =>
    {
        w.Write((ushort)0); V(w, (uint)Own.EntityKey); w.Write(new byte[4]); w.Write(Own.CharacterId);
        w.Write(new byte[2279 - (int)(w.BaseStream.Position - 2)]);
    });
    private static byte[] Pets(int balance = 21) => Packet(0x9000, w =>
    {
        w.Write(0); w.Write((byte)1); w.Write(1); w.Write(Slime); w.Write(1);
        w.Write((byte)1); w.Write(Slime); w.Write(balance); w.Write(new byte[4]);
    });
    private static byte[] Points(ushort opcode, int amount, int pet = Slime) => Packet(opcode, w => { w.Write((byte)1); w.Write(pet); w.Write(amount); });
    private static byte[] Packet(ushort opcode, Action<BinaryWriter> action)
    {
        using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes);
        writer.Write(opcode); action(writer); return bytes.ToArray();
    }
    private static void V(BinaryWriter writer, uint value)
    {
        while (value >= 128) { writer.Write((byte)(value | 128)); value >>= 7; }
        writer.Write((byte)value);
    }
}
