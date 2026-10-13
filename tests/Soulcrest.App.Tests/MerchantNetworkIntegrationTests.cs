using System.Globalization;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Pure dispatch integration; native tuple ownership is covered by the existing capture tests.</summary>
public sealed class MerchantNetworkIntegrationTests
{
    [Fact]
    public void ATransportGapInvalidatesMerchantOffersEvenBeforeTheFirstCharacterBinding()
    {
        using var f = new Fixture();
        f.Accept();
        Assert.False(f.Network.ProgressDiagnostics.IdentityBound);
        f.Network.ProcessProgressGap(OwnStream);
        Assert.Null(f.Merchant.Snapshot);
        f.Accept();
        Assert.NotNull(f.Merchant.Snapshot);
    }

    [Fact]
    public void ASeparateMerchantStreamGapKeepsOwnAndAllProgressBaselinesWhileAdvancingOnlyTheShopGeneration()
    {
        using var f = new Fixture(withExploration: true);
        var exploration = f.Exploration!;
        typeof(NetworkLootService).GetProperty(nameof(NetworkLootService.Running))!.SetValue(f.Network, true);
        f.Bind();
        f.Network.ProcessProgressMessage(OwnStream, Packet(0x8D09, writer =>
        {
            writer.Write((byte)1); writer.Write(2810034); writer.Write((byte)4); writer.Write((byte)1);
            writer.Write(new byte[] { 1, 0, 0, 0, 0, 0 }); writer.Write(1L); writer.Write(new byte[2]);
        }), At.AddMilliseconds(11));
        f.Network.ProcessProgressMessage(OwnStream,
            Packet(0x9232, writer => { writer.Write((byte)1); writer.Write(1052020); }), At.AddMilliseconds(12));
        Assert.True(f.Network.ProgressSources.InitialProgressConfirmed);
        Assert.Contains("cave", exploration.Completed);
        Assert.Contains("pillar", exploration.Completed);
        var character = f.Network.Character;
        var profile = exploration.ActiveId;
        var sources = f.Network.ProgressSources;
        var diagnostics = f.Network.ProgressDiagnostics;
        var pet = f.Progress.EntrySnapshot(PetId);
        const string merchantStream = "synthetic-merchant-stream";
        f.Network.ProcessProgressMessage(merchantStream, Status(), At.AddMilliseconds(20));
        f.Network.ProcessProgressMessage(merchantStream, List(), At.AddMilliseconds(100));
        var previousGeneration = f.Merchant.Snapshot!.SourceGeneration;
        Assert.Equal(merchantStream, f.Merchant.Snapshot.Stream);
        f.Network.ProcessProgressGap(merchantStream);
        Assert.Null(f.Merchant.Snapshot);
        Assert.Empty(f.Merchant.Rows);
        Assert.True(f.Network.ProgressDiagnostics.IdentityBound);
        Assert.Equal(character, f.Network.Character);
        Assert.Equal(profile, exploration.ActiveId);
        Assert.Equal(diagnostics.ContextResets, f.Network.ProgressDiagnostics.ContextResets);
        Assert.Equal(sources.PetBaselineAt, f.Network.PetBaselineAt);
        Assert.Equal(sources.QuestBaselineAt, f.Network.QuestBaselineAt);
        Assert.Equal(sources.TeleportBaselineAt, f.Network.TeleportBaselineAt);
        Assert.True(f.Network.ProgressSources.InitialProgressConfirmed);
        Assert.Contains("cave", exploration.Completed);
        Assert.Contains("pillar", exploration.Completed);
        Assert.Equal(pet.Freshness, f.Progress.EntrySnapshot(PetId).Freshness);
        Assert.Equal(3, f.Progress.AvailableSouls(PetId));
        f.Network.ProcessProgressMessage(merchantStream, Status(), At.AddSeconds(1));
        f.Network.ProcessProgressMessage(merchantStream, List(), At.AddSeconds(1.1));
        Assert.True(f.Merchant.Snapshot!.SourceGeneration > previousGeneration);
        Assert.Equal(merchantStream, f.Merchant.Snapshot.Stream);
        Assert.Equal("3/25", Assert.Single(f.Merchant.Rows).Counter);
        Assert.True(f.Network.ProgressSources.InitialProgressConfirmed);
        Assert.Empty(f.Progress.Recent());
        Assert.False(File.Exists(f.EventsPath));
    }

    private static readonly DateTimeOffset At = new(2026, 10, 13, 18, 0, 0, TimeSpan.Zero);
    private static readonly PetIdEntry Pet = PetIdTable.Find(1002)!;
    private static string PetId => Pet.CatalogId!;
    private const string OwnStream = "synthetic-own-stream";

    [Fact]
    public void UnboundDispatchCarriesNoServerAuthorityAndCopiesItsWireMessage()
    {
        using var f = new Fixture();
        var emitted = new List<MerchantNetworkMessage>();
        f.Network.MerchantMessageReceived += emitted.Add;
        var message = List();
        f.Network.ProcessProgressMessage(OwnStream, Status(), At);
        f.Network.ProcessProgressMessage(OwnStream, message, At.AddMilliseconds(100));
        Assert.Equal(2, emitted.Count);
        Assert.All(emitted, envelope => { Assert.Null(envelope.ServerId); Assert.Equal(OwnStream, envelope.Stream); });
        Assert.NotSame(message, emitted[1].Message);
        Array.Fill(message, (byte)0);
        Assert.Equal((byte)0x91, emitted[1].Message[0]);
        Assert.Null(f.Merchant.Snapshot!.ServerId);
        Assert.Equal("?", Assert.Single(f.Merchant.Rows).Counter);
        Assert.Null(f.Progress.ActiveServerId);
        Assert.False(File.Exists(f.ProgressPath));
        Assert.False(File.Exists(f.EventsPath));
    }

    [Fact]
    public void MerchantCountersDoNotNeedTheOfferStreamToCarryACharacterOrServer()
    {
        using var f = new Fixture();
        f.Bind();
        var emitted = new List<MerchantNetworkMessage>();
        f.Network.MerchantMessageReceived += emitted.Add;
        f.Accept();
        Assert.Equal(2, emitted.Count);
        Assert.All(emitted, envelope => Assert.Equal(501, envelope.ServerId));
        Assert.Equal("3/25", Assert.Single(f.Merchant.Rows).Counter);
        var snapshot = f.Merchant.Snapshot;
        f.Network.ProcessProgressMessage("synthetic-foreign-stream", Status(), At.AddSeconds(1));
        f.Network.ProcessProgressMessage("synthetic-foreign-stream", List(), At.AddSeconds(1.1));
        Assert.Equal(4, emitted.Count);
        Assert.All(emitted.Skip(2), envelope => Assert.Null(envelope.ServerId));
        Assert.NotSame(snapshot, f.Merchant.Snapshot);
        Assert.Equal("3/25", Assert.Single(f.Merchant.Rows).Counter);
        Assert.Equal(3, f.Progress.AvailableSouls(PetId));
        Assert.Empty(f.Progress.Recent());
        Assert.False(File.Exists(f.EventsPath));
    }

    [Theory]
    [InlineData("gap")]
    [InlineData("prepare")]
    [InlineData("close")]
    public void TrueProgressResetsAlsoInvalidateTheMerchantSnapshotAndPendingStatus(string reset)
    {
        using var f = new Fixture();
        f.Bind();
        f.Accept();
        var previousGeneration = f.Merchant.Snapshot!.SourceGeneration;
        var resets = 0;
        f.Network.MerchantSessionReset += () => resets++;
        switch (reset)
        {
            case "gap": f.Network.ProcessProgressGap(OwnStream); break;
            case "prepare": f.Network.ProcessProgressMessage(OwnStream, Packet(0x3621, writer => writer.Write(new byte[46])), At.AddSeconds(1)); break;
            case "close": f.Network.Stop(); break;
        }
        Assert.Equal(1, resets);
        Assert.Null(f.Merchant.Snapshot);
        Assert.Empty(f.Merchant.Rows);
        f.Network.ProcessProgressMessage(OwnStream, List(), At.AddSeconds(1.1));
        Assert.Null(f.Merchant.Snapshot); // Reset discarded the original successful status.
        if (reset != "close")
        {
            f.Network.ProcessProgressMessage(OwnStream, Status(), At.AddSeconds(1.2));
            f.Network.ProcessProgressMessage(OwnStream, List(), At.AddSeconds(1.3));
            Assert.True(f.Merchant.Snapshot!.SourceGeneration > previousGeneration);
            Assert.Null(f.Merchant.Snapshot.ServerId);
            Assert.Equal("3/25", Assert.Single(f.Merchant.Rows).Counter);
        }
        Assert.Empty(f.Progress.Recent());
        Assert.False(File.Exists(f.EventsPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MalformedMerchantTrafficClearsOnlyTheShopAndDoesNotTouchPetProgress(bool malformedStatus)
    {
        using var f = new Fixture();
        f.Bind();
        f.Accept();
        var before = File.ReadAllBytes(f.ProgressPath);
        var diagnostics = f.Network.ProgressDiagnostics;
        f.Network.ProcessProgressMessage(OwnStream,
            malformedStatus ? new byte[] { 0x93, 0x56, 0, 0 } : List()[..^1], At.AddMilliseconds(200));
        Assert.Null(f.Merchant.Snapshot);
        Assert.Equal(before, File.ReadAllBytes(f.ProgressPath));
        Assert.Equal(3, f.Progress.AvailableSouls(PetId));
        Assert.Equal(1, f.Progress.ObservedLevel(PetId));
        var after = f.Network.ProgressDiagnostics;
        Assert.Equal(diagnostics.IdentityBound, after.IdentityBound);
        Assert.Equal(diagnostics.ContextResets, after.ContextResets);
        Assert.Equal(diagnostics.RejectedProgressMessages, after.RejectedProgressMessages);
        Assert.Equal(diagnostics.AcceptedSoulMessages, after.AcceptedSoulMessages);
        Assert.Equal(diagnostics.AcceptedSoulPoints, after.AcceptedSoulPoints);
        Assert.Equal(diagnostics.Events, after.Events);
        Assert.Empty(f.Progress.Recent());
        Assert.False(File.Exists(f.EventsPath));
    }

    private static byte[] Packet(ushort opcode, Action<BinaryWriter> body)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(opcode); body(writer);
        return stream.ToArray();
    }

    private static byte[] Status() => [0x93, 0x56, 0, 0, 77];

    private static byte[] List() => Packet(0x5691, writer =>
    {
        writer.Write((byte)77); writer.Write((byte)1);
        writer.Write(10_001); writer.Write(20_001); writer.Write(1L);
        writer.Write((ushort)0xB38F); writer.Write(100_000L); writer.Write(70_000L);
        writer.Write((byte)0xFF); writer.Write(new byte[12]);
    });

    private static byte[] Pets() => Packet(0x9000, writer =>
    {
        writer.Write(0); writer.Write((byte)1); writer.Write(1); writer.Write(Pet.Id); writer.Write(1);
        writer.Write((byte)1); writer.Write(Pet.Id); writer.Write(3); writer.Write(new byte[4]);
    });

    private sealed class Fixture : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-merchant-network-" + Guid.NewGuid().ToString("N"));
        internal string ProgressPath => Path.Combine(_folder, "progress.json");
        internal string EventsPath => Path.Combine(_folder, "events.jsonl");
        internal ProgressService Progress { get; }
        internal ExplorationService? Exploration { get; }
        internal NetworkLootService Network { get; }
        internal MerchantService Merchant { get; }

        internal Fixture(bool withExploration = false)
        {
            Directory.CreateDirectory(_folder);
            Progress = new(ProgressPath, new PetCatalog([new PetDefinition
            {
                Id = PetId, En = Pet.En, Genus = Pet.Genus.ToLowerInvariant(),
                NumericId = Pet.Id.ToString(CultureInfo.InvariantCulture)
            }]));
            if (withExploration)
                Exploration = new(Path.Combine(_folder, "exploration.json"), [
                    new("cave", "altgard", "dungeon", 0, "Synthetic Cave", null, QuestId: 2810034),
                    new("pillar", "altgard", "kibelisk", 1, "Synthetic Pillar", null, TeleportId: 1052020)]);
            Network = new(Progress, Exploration);
            Merchant = new(Network, Progress, item => item == 20_001 ? Pet.Id : null);
        }

        internal void Bind()
        {
            Network.ObserveIdentity(OwnStream, new(101, 501, 1001, "Synthetic Merchant Integration"), At);
            Network.ProcessProgressMessage(OwnStream, Pets(), At.AddMilliseconds(10));
            Assert.True(Network.ProgressDiagnostics.IdentityBound);
            Assert.Equal(3, Progress.AvailableSouls(PetId));
        }

        internal void Accept()
        {
            Network.ProcessProgressMessage(OwnStream, Status(), At.AddMilliseconds(20));
            Network.ProcessProgressMessage(OwnStream, List(), At.AddMilliseconds(100));
            Assert.NotNull(Merchant.Snapshot);
        }

        public void Dispose()
        {
            Merchant.Dispose();
            Network.Dispose();
            Directory.Delete(_folder, recursive: true);
        }
    }
}
