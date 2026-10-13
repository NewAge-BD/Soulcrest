using System.Globalization;
using System.Reflection;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Soulcrest.Core.Progress;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Global pet continuity is independent of character transitions; real transport and source failures still invalidate confirmation.</summary>
public sealed class NetworkPetContinuityTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly OwnCharacterIdentity Own = new(101, 501, 300, "SyntheticGlobalPet");
    private const int Slime = 1062;
    private const string Game = "synthetic-world";

    private sealed class Fixture : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-global-pet-dispatch-" + Guid.NewGuid().ToString("N"));
        internal ProgressService Progress { get; }
        internal ExplorationService Exploration { get; }
        internal NetworkLootService Network { get; }
        internal List<NetworkSoul> Gains { get; } = [];
        internal string PetId { get; }
        internal Fixture()
        {
            Directory.CreateDirectory(_folder);
            var source = PetIdTable.Find(Slime)!;
            PetId = source.CatalogId!;
            Progress = new(Path.Combine(_folder, "progress.json"), new PetCatalog([
                new PetDefinition { Id = PetId, En = source.En, Genus = source.Genus, NumericId = Slime.ToString(CultureInfo.InvariantCulture) }]));
            Exploration = new(Path.Combine(_folder, "exploration.json"), [
                new("cave", "altgard", "dungeon", 0, "Synthetic Cave", null, QuestId: 2810034),
                new("pillar", "altgard", "kibelisk", 1, "Synthetic Pillar", null, TeleportId: 1052020)]);
            Network = new(Progress, Exploration);
            typeof(NetworkLootService).GetProperty(nameof(NetworkLootService.Running))!.SetValue(Network, true);
            Network.SoulGained += Gains.Add;
        }
        internal void Send(byte[] message, int second, string stream = Game) => Network.ProcessProgressMessage(stream, message, At.AddSeconds(second));
        internal void Enter(int second = 3, OwnCharacterIdentity? own = null, string stream = Game) => Network.ObserveIdentity(stream, own ?? Own, At.AddSeconds(second));
        internal void Bind()
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
        public void Dispose() { Network.Dispose(); Directory.Delete(_folder, recursive: true); }
    }

    [Fact]
    public void GlobalSnapshotGainSpendAndUpgradeWorkBeforeAnyOwnWithoutInventingExtraPickups()
    {
        using var f = new Fixture();
        f.Send(Pets(), 1);
        Assert.Null(f.Network.Character);
        Assert.Equal(At.AddSeconds(1), f.Network.PetBaselineAt);
        Assert.Equal(21, f.Progress.AvailableSouls(f.PetId));
        f.Send(Points(0x900D, 4), 2, "another-native-owned-stream");
        f.Send(Upgrade(), 3);
        f.Send(Points(0x900E, 25), 4);
        f.Send(Points(0x900D, 2), 5);
        Assert.Null(f.Network.Character);
        Assert.Equal(2, f.Progress.ObservedLevel(f.PetId));
        Assert.Equal(2, f.Progress.AvailableSouls(f.PetId));
        Assert.Equal("2/75", f.Progress.SoulCount(f.PetId));
        Assert.Equal(32, f.Progress.Souls(f.PetId));
        Assert.Equal(2, f.Gains.Count);
        Assert.Equal(6, f.Gains.Sum(g => g.Souls));
        Assert.Equal(2, f.Progress.Recent().Count);
        Assert.Equal(0, f.Network.ProgressDiagnostics.PendingMessages);
    }

    [Theory]
    [InlineData("candidate")]
    [InlineData("unsupported-candidate")]
    [InlineData("prepare")]
    [InlineData("unsupported-prepare")]
    [InlineData("unsupported-own")]
    [InlineData("quest-format")]
    [InlineData("teleport-format")]
    public void CharacterBoundariesAndExplorationFailuresDoNotStaleTheGlobalPetBaseline(string boundary)
    {
        using var f = new Fixture(); f.Bind();
        var original = f.Progress.EntrySnapshot(f.PetId);
        var message = boundary switch
        {
            "candidate" => Candidate(),
            "unsupported-candidate" => new byte[] { 0x20, 0x36, 0 },
            "prepare" => Prepare(),
            "unsupported-prepare" => new byte[] { 0x21, 0x36, 0 },
            "unsupported-own" => new byte[] { 0x33, 0x36, 0 },
            "quest-format" => new byte[] { 0x09, 0x8D, 0xFF },
            _ => new byte[] { 0x32, 0x92, 0xFF },
        };
        f.Send(message, 2);
        Assert.Equal(At.AddSeconds(1), f.Network.PetBaselineAt);
        Assert.Equal(At.AddSeconds(1), f.Progress.NetworkBaselineAt);
        Assert.Equal(21, f.Progress.AvailableSouls(f.PetId));
        Assert.Equal(ProgressFreshness.Fresh, f.Progress.EntrySnapshot(f.PetId).Freshness);
        Assert.Equal(original.UpdatedAt, f.Progress.EntrySnapshot(f.PetId).UpdatedAt);
        Assert.Empty(f.Gains);
        f.Send(Points(0x900D, 1), 3);
        Assert.Equal(22, f.Progress.AvailableSouls(f.PetId));
        Assert.Single(f.Gains);
    }

    [Theory]
    [InlineData(101L, 501)]
    [InlineData(202L, 501)]
    [InlineData(101L, 502)]
    [InlineData(202L, 502)]
    public void CharacterAndServerChangesKeepTheSameGlobalPetsWhileExplorationKeepsItsOwnProfile(long character, int server)
    {
        using var f = new Fixture(); f.Bind();
        var explorationGuid = f.Exploration.ActiveId;
        f.Progress.ToggleFocus(f.PetId);
        f.Send(Prepare(), 2);
        Assert.Equal(At.AddSeconds(1), f.Network.PetBaselineAt);
        f.Enter(own: Own with { CharacterId = character, ServerId = server, EntityKey = 301 });
        Assert.Equal(21, f.Progress.AvailableSouls(f.PetId));
        Assert.True(f.Progress.IsFocus(f.PetId));
        Assert.Equal(ProgressFreshness.Fresh, f.Progress.EntrySnapshot(f.PetId).Freshness);
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
    [InlineData(0x9000)]
    [InlineData(0x900D)]
    [InlineData(0x900E)]
    [InlineData(0x9014)]
    public void ValidGlobalPetUpdatesDuringACharacterTransitionApplyImmediatelyAndAreNeverReplayed(ushort opcode)
    {
        using var f = new Fixture(); f.Bind(); f.Send(Prepare(), 2);
        Assert.Null(f.Network.Character);
        f.Send(opcode switch
        {
            0x9000 => Pets(23),
            0x9014 => Upgrade(),
            _ => Points(opcode, 1),
        }, 3);
        var expectedBalance = opcode switch { 0x9000 => 23, 0x900D => 22, 0x900E => 20, _ => 21 };
        Assert.Equal(expectedBalance, f.Progress.AvailableSouls(f.PetId));
        Assert.Equal(opcode == 0x9014 ? 2 : 1, f.Progress.ObservedLevel(f.PetId));
        Assert.Equal(opcode == 0x9000 ? At.AddSeconds(3) : At.AddSeconds(1), f.Network.PetBaselineAt);
        f.Enter(4);
        Assert.Equal(expectedBalance, f.Progress.AvailableSouls(f.PetId));
        Assert.Equal(opcode == 0x900D ? 1 : 0, f.Gains.Count);
        Assert.Equal(opcode == 0x900D ? 1 : 0, f.Progress.Recent().Count);
        Assert.Equal(0, f.Network.ProgressDiagnostics.PendingMessages);
    }

    [Fact]
    public void AcquisitionBroadcastStillRequiresTheExactOwnEntityAndStream()
    {
        using var f = new Fixture();
        f.Send(Acquired(Own.EntityKey), 1);
        Assert.Null(f.Progress.ObservedLevel(f.PetId));
        f.Send(Pets(), 2);
        f.Send(Acquired(Own.EntityKey), 3);
        Assert.Equal(1, f.Progress.ObservedLevel(f.PetId));
        f.Enter(4);
        f.Send(Acquired(Own.EntityKey + 1), 5);
        f.Send(Acquired(Own.EntityKey), 6, "another-native-owned-stream");
        Assert.Equal(1, f.Progress.ObservedLevel(f.PetId));
        f.Send(Acquired(Own.EntityKey), 7);
        Assert.Equal(2, f.Progress.ObservedLevel(f.PetId));
        Assert.Equal(21, f.Progress.AvailableSouls(f.PetId));
        Assert.Empty(f.Gains);
        Assert.Empty(f.Progress.Recent());
    }

    [Theory]
    [InlineData("pet-stream")]
    [InlineData("additional-pet-stream")]
    [InlineData("global-gap")]
    [InlineData("close")]
    [InlineData("stop")]
    [InlineData("manual")]
    public void RealTransportOrSessionLossStalesConfirmationWithoutDeletingAnyGlobalPetCounts(string loss)
    {
        using var f = new Fixture(); f.Bind();
        f.Send(Points(0x900D, 1), 2, "additional-native-owned-stream");
        switch (loss)
        {
            case "pet-stream": f.Network.ProcessProgressGap(Game); break;
            case "additional-pet-stream": f.Network.ProcessProgressGap("additional-native-owned-stream"); break;
            case "global-gap": f.Network.ProcessProgressGap("*"); break;
            case "close": typeof(NetworkLootService).GetMethod("CloseDevice", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(f.Network, null); break;
            case "stop": f.Network.Stop(); break;
            default: f.Network.ResetProgressContext(); break;
        }
        Assert.Null(f.Network.PetBaselineAt);
        Assert.NotEqual(ProgressFreshness.Fresh, f.Progress.EntrySnapshot(f.PetId).Freshness);
        Assert.Equal(27, f.Progress.Souls(f.PetId));
        Assert.Equal(22, f.Progress.SoulsInLevel(f.PetId));
        Assert.Single(f.Gains);
        Assert.Single(f.Progress.Recent());
        if (loss is "pet-stream" or "additional-pet-stream" or "global-gap" or "manual")
        {
            f.Send(Pets(22), 4);
            Assert.Equal(At.AddSeconds(4), f.Network.PetBaselineAt);
            Assert.Equal(22, f.Progress.AvailableSouls(f.PetId));
            Assert.Equal(ProgressFreshness.Fresh, f.Progress.EntrySnapshot(f.PetId).Freshness);
            Assert.Single(f.Gains);
        }
    }

    [Fact]
    public void ATrackedPetStreamGapInvalidatesTheBaselineEvenBeforeTheFirstOwn()
    {
        using var f = new Fixture();
        f.Send(Pets(), 1);
        Assert.Null(f.Network.Character);
        f.Network.ProcessProgressGap(Game);
        Assert.Null(f.Network.PetBaselineAt);
        Assert.Equal(26, f.Progress.Souls(f.PetId));
        Assert.Equal(21, f.Progress.SoulsInLevel(f.PetId));
        Assert.NotEqual(ProgressFreshness.Fresh, f.Progress.EntrySnapshot(f.PetId).Freshness);
        f.Send(Points(0x900D, 1), 2);
        Assert.Equal(27, f.Progress.Souls(f.PetId));
        Assert.Null(f.Network.PetBaselineAt);
        Assert.Single(f.Gains);
    }

    [Fact]
    public void AnUntrackedForeignGapCannotInvalidateTheExistingGlobalPetBaseline()
    {
        using var f = new Fixture(); f.Bind();
        f.Network.ProcessProgressGap("untracked-foreign-stream");
        Assert.Equal(At.AddSeconds(1), f.Network.PetBaselineAt);
        Assert.Equal(21, f.Progress.AvailableSouls(f.PetId));
        Assert.Equal(ProgressFreshness.Fresh, f.Progress.EntrySnapshot(f.PetId).Freshness);
        Assert.True(f.Network.ProgressDiagnostics.IdentityBound);
        Assert.Empty(f.Gains);
    }

    [Theory]
    [InlineData(0x9000)]
    [InlineData(0x900D)]
    [InlineData(0x900E)]
    [InlineData(0x9014)]
    public void MalformedPetSourcesRemainStrictWithoutOwnAndOnlyAFullSnapshotRestoresConfirmation(ushort opcode)
    {
        using var f = new Fixture(); f.Send(Pets(), 1);
        f.Send(Packet(opcode, _ => { }), 2);
        Assert.Null(f.Network.Character);
        Assert.Null(f.Network.PetBaselineAt);
        Assert.True(f.Network.ProgressSources.PetFormatRejected);
        Assert.Equal(26, f.Progress.Souls(f.PetId));
        Assert.Equal(21, f.Progress.SoulsInLevel(f.PetId));
        f.Send(Points(0x900D, 1), 3);
        Assert.Equal(27, f.Progress.Souls(f.PetId));
        Assert.Equal(22, f.Progress.SoulsInLevel(f.PetId));
        Assert.Null(f.Network.PetBaselineAt);
        Assert.True(f.Network.ProgressSources.PetFormatRejected);
        Assert.Single(f.Gains);
        f.Send(Pets(23), 4);
        Assert.Equal(23, f.Progress.AvailableSouls(f.PetId));
        Assert.Equal(At.AddSeconds(4), f.Network.PetBaselineAt);
        Assert.False(f.Network.ProgressSources.PetFormatRejected);
        Assert.Single(f.Gains);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ManualValuesRemainProtectedBeforeOrDuringCharacterTransition(bool during)
    {
        using var f = new Fixture(); f.Bind();
        if (!during) f.Progress.SetSouls(f.PetId, 8);
        f.Send(Prepare(), 2);
        if (during) f.Progress.SetSouls(f.PetId, 8);
        var manual = f.Progress.EntrySnapshot(f.PetId);
        f.Enter(3, Own with { CharacterId = 202, ServerId = 502 });
        var after = f.Progress.EntrySnapshot(f.PetId);
        Assert.Equal(manual.Souls, after.Souls);
        Assert.Equal(manual.Source, after.Source);
        Assert.Equal(manual.UpdatedAt, after.UpdatedAt);
        Assert.Equal(manual.Freshness, after.Freshness);
        Assert.True(after.ManualOverride);
        Assert.Null(f.Progress.AvailableSouls(f.PetId));
        Assert.Equal(At.AddSeconds(1), f.Network.PetBaselineAt);
    }

    [Fact]
    public void ALongCharacterTransitionDoesNotExpireAnUninterruptedGlobalPetBaseline()
    {
        using var f = new Fixture(); f.Bind();
        f.Send(Prepare(), 2); f.Enter(123);
        Assert.Equal(At.AddSeconds(1), f.Network.PetBaselineAt);
        Assert.Equal(21, f.Progress.AvailableSouls(f.PetId));
        Assert.Equal(ProgressFreshness.Fresh, f.Progress.EntrySnapshot(f.PetId).Freshness);
        Assert.Empty(f.Gains);
    }

    [Fact]
    public void TheFirstSnapshotStreamIsTrackedBeforeItsApplyCallbackCanReportAGap()
    {
        using var f = new Fixture();
        var fired = false;
        f.Progress.Changed += () =>
        {
            if (fired || f.Progress.NetworkBaselineAt is null) return;
            fired = true;
            f.Network.ProcessProgressGap(Game);
        };
        f.Send(Pets(), 1);
        Assert.True(fired);
        Assert.Null(f.Network.PetBaselineAt);
        Assert.Equal(ProgressFreshness.Stale, f.Progress.EntrySnapshot(f.PetId).Freshness);
        Assert.Equal(26, f.Progress.Souls(f.PetId));
        Assert.Equal(21, f.Progress.SoulsInLevel(f.PetId));
        Assert.Empty(f.Gains);
        Assert.Empty(f.Progress.Recent());
    }

    [Fact]
    public void AGainInTheFirstBindCallbackIsProcessedAfterTheOuterGainWithoutLosingEitherPickup()
    {
        using var f = new Fixture();
        var fired = false;
        f.Progress.Changed += () =>
        {
            if (fired) return;
            fired = true;
            f.Send(Points(0x900D, 3), 2);
        };
        f.Send(Points(0x900D, 2), 1);
        Assert.True(fired);
        Assert.Equal(5, f.Progress.Souls(f.PetId));
        Assert.Collection(f.Gains, gain => Assert.Equal(2, gain.Souls), gain => Assert.Equal(3, gain.Souls));
        Assert.Equal(2, f.Progress.Recent().Count);
        Assert.Equal(2, f.Network.ProgressDiagnostics.AcceptedSoulMessages);
        Assert.Equal(5, f.Network.ProgressDiagnostics.AcceptedSoulPoints);
        Assert.Null(f.Network.PetBaselineAt);
    }

    [Fact]
    public void AScopedGapKeepsOtherPetStreamsTrackedAfterANewSnapshotRestoresFreshness()
    {
        using var f = new Fixture();
        const string other = "another-native-owned-stream";
        f.Send(Pets(), 1);
        f.Send(Points(0x900D, 1), 2, other);
        f.Network.ProcessProgressGap(Game);
        Assert.Null(f.Network.PetBaselineAt);
        f.Send(Pets(22), 3);
        Assert.Equal(At.AddSeconds(3), f.Network.PetBaselineAt);
        Assert.Equal(ProgressFreshness.Fresh, f.Progress.EntrySnapshot(f.PetId).Freshness);
        f.Network.ProcessProgressGap(other);
        Assert.Null(f.Network.PetBaselineAt);
        Assert.Equal(ProgressFreshness.Stale, f.Progress.EntrySnapshot(f.PetId).Freshness);
        Assert.Equal(27, f.Progress.Souls(f.PetId));
        Assert.Equal(22, f.Progress.SoulsInLevel(f.PetId));
        Assert.Single(f.Gains);
        Assert.Single(f.Progress.Recent());
    }

    [Fact]
    public void AGainInTheFirstSnapshotBindCallbackAppliesAfterTheSnapshotAndKeepsItsFreshBaseline()
    {
        using var f = new Fixture();
        var fired = false;
        f.Progress.Changed += () =>
        {
            if (fired) return;
            fired = true;
            f.Send(Points(0x900D, 3), 2);
        };
        f.Send(Pets(5), 1);
        Assert.True(fired);
        Assert.Equal(1, f.Progress.ObservedLevel(f.PetId));
        Assert.Equal(8, f.Progress.AvailableSouls(f.PetId));
        Assert.Equal(13, f.Progress.Souls(f.PetId));
        Assert.Equal(ProgressFreshness.Fresh, f.Progress.EntrySnapshot(f.PetId).Freshness);
        Assert.Equal(At.AddSeconds(1), f.Network.PetBaselineAt);
        Assert.Equal(At.AddSeconds(1), f.Progress.NetworkBaselineAt);
        Assert.Equal(At.AddSeconds(2), f.Network.ProgressSources.PetActivityAt);
        Assert.Equal(3, Assert.Single(f.Gains).Souls);
        Assert.Single(f.Progress.Recent());
    }

    [Fact]
    public void AnUnboundAcquisitionCannotBeRetroactivelyAuthorizedByOwnDuringPetDispatch()
    {
        using var f = new Fixture();
        var fired = false;
        f.Progress.Changed += () =>
        {
            if (fired) return;
            fired = true;
            f.Send(Acquired(Own.EntityKey), 2);
            f.Enter(3);
        };
        f.Send(Pets(), 1);
        Assert.True(fired);
        Assert.True(f.Network.ProgressDiagnostics.IdentityBound);
        Assert.Equal(1, f.Progress.ObservedLevel(f.PetId));
        Assert.Equal(21, f.Progress.AvailableSouls(f.PetId));
        Assert.Equal(At.AddSeconds(1), f.Network.PetBaselineAt);
        Assert.DoesNotContain(f.Network.ProgressDiagnostics.Events, entry => entry.Kind == NetworkProgressEventKind.Acquired);
        Assert.Empty(f.Gains);
        Assert.Empty(f.Progress.Recent());
    }

    [Theory]
    [InlineData("stream-gap")]
    [InlineData("ownership-removed")]
    public void AGapOfANewQueuedPetStreamDiscardsItsGainAndCannotRepublishTheOuterSnapshot(string reason)
    {
        using var f = new Fixture();
        const string other = "another-native-owned-stream";
        var fired = false;
        f.Progress.Changed += () =>
        {
            if (fired || f.Progress.NetworkBaselineAt is null) return;
            fired = true;
            f.Send(Points(0x900D, 3), 2, other);
            f.Network.ProcessProgressGap(other, reason);
        };
        f.Send(Pets(), 1);
        Assert.True(fired);
        Assert.Null(f.Network.PetBaselineAt);
        Assert.Equal(ProgressFreshness.Stale, f.Progress.EntrySnapshot(f.PetId).Freshness);
        Assert.Equal(26, f.Progress.Souls(f.PetId));
        Assert.Equal(21, f.Progress.SoulsInLevel(f.PetId));
        Assert.Equal(0, f.Network.ProgressDiagnostics.AcceptedSoulMessages);
        Assert.Empty(f.Gains);
        Assert.Empty(f.Progress.Recent());
    }

    [Theory]
    [InlineData("gap")]
    [InlineData("manual")]
    [InlineData("stop")]
    [InlineData("close")]
    public void OwnAuthorityIsRevokedBeforeAResetProgressCallbackCanSupplyAnAcquisition(string reset)
    {
        using var f = new Fixture(); f.Bind();
        var fired = false;
        f.Progress.Changed += () =>
        {
            if (fired) return;
            fired = true;
            f.Send(Acquired(Own.EntityKey), 2);
        };
        switch (reset)
        {
            case "gap": f.Network.ProcessProgressGap(Game); break;
            case "manual": f.Network.ResetProgressContext(); break;
            case "stop": f.Network.Stop(); break;
            case "close": typeof(NetworkLootService).GetMethod("CloseDevice", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(f.Network, null); break;
        }
        Assert.True(fired);
        Assert.False(f.Network.ProgressDiagnostics.IdentityBound);
        Assert.Null(f.Network.Character);
        Assert.Null(f.Network.PetBaselineAt);
        Assert.Equal(1, f.Progress.ObservedLevel(f.PetId));
        Assert.Equal(26, f.Progress.Souls(f.PetId));
        Assert.Equal(ProgressFreshness.Stale, f.Progress.EntrySnapshot(f.PetId).Freshness);
        Assert.DoesNotContain(f.Network.ProgressDiagnostics.Events, entry => entry.Kind == NetworkProgressEventKind.Acquired);
        Assert.Empty(f.Gains);
        Assert.Empty(f.Progress.Recent());
    }

    [Fact]
    public void OwnAuthorityIsRevokedBeforeTheBossGapCallbackCanSupplyAnAcquisition()
    {
        using var f = new Fixture(); f.Bind();
        var notifications = 0;
        f.Network.BossStreamGap += stream =>
        {
            Assert.Equal(Game, stream);
            notifications++;
            f.Send(Acquired(Own.EntityKey), 2);
        };
        f.Network.ProcessProgressGap(Game);
        Assert.Equal(1, notifications);
        Assert.False(f.Network.ProgressDiagnostics.IdentityBound);
        Assert.Null(f.Network.Character);
        Assert.Null(f.Network.PetBaselineAt);
        Assert.Equal(1, f.Progress.ObservedLevel(f.PetId));
        Assert.Equal(26, f.Progress.Souls(f.PetId));
        Assert.Equal(ProgressFreshness.Stale, f.Progress.EntrySnapshot(f.PetId).Freshness);
        Assert.DoesNotContain(f.Network.ProgressDiagnostics.Events, entry => entry.Kind == NetworkProgressEventKind.Acquired);
        Assert.Empty(f.Gains);
        Assert.Empty(f.Progress.Recent());
    }

    [Fact]
    public void ANewerSnapshotFromAProgressCallbackCannotBeOverwrittenByOuterBaselinePublication()
    {
        using var f = new Fixture();
        var fired = false;
        f.Progress.Changed += () =>
        {
            if (fired || f.Progress.SoulsInLevel(f.PetId) != 21) return;
            fired = true;
            f.Send(Pets(23), 2);
        };
        f.Send(Pets(), 1);
        Assert.True(fired);
        Assert.Equal(23, f.Progress.AvailableSouls(f.PetId));
        Assert.Equal(At.AddSeconds(2), f.Network.PetBaselineAt);
        Assert.Equal(At.AddSeconds(2), f.Network.ProgressSources.PetActivityAt);
        Assert.Empty(f.Gains);
        Assert.Empty(f.Progress.Recent());
    }

    [Fact]
    public void AnOwnNotificationCallbackCanCountOneGlobalGainWithoutAnOldBaselineReplay()
    {
        using var f = new Fixture(); f.Send(Pets(), 1);
        var notifications = 0;
        f.Network.OwnCharacterEntered += _ =>
        {
            notifications++;
            f.Send(Points(0x900D, 1), 4);
        };
        f.Enter(3);
        Assert.Equal(1, notifications);
        Assert.Equal(22, f.Progress.AvailableSouls(f.PetId));
        Assert.Equal(27, f.Progress.Souls(f.PetId));
        Assert.Equal(At.AddSeconds(1), f.Network.PetBaselineAt);
        Assert.Equal(ProgressFreshness.Fresh, f.Progress.EntrySnapshot(f.PetId).Freshness);
        Assert.Single(f.Gains);
        Assert.Single(f.Progress.Recent());
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
    private static byte[] Upgrade() => Packet(0x9014, w =>
    { w.Write((ushort)0); w.Write(Slime); w.Write(2); w.Write(new byte[13]); });
    private static byte[] Acquired(int entity) => Packet(0x9009, w =>
    { V(w, (uint)entity); w.Write(Slime); w.Write(2); w.Write(new byte[13]); });
    private static byte[] Points(ushort opcode, int amount) => Packet(opcode, w => { w.Write((byte)1); w.Write(Slime); w.Write(amount); });
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
