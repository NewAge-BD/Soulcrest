using System.Buffers.Binary;
using System.Text;
using Soulcrest.App.Network;
using Soulcrest.App.OverlayEditorKit;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class NetworkDailyGatheringLimitTests
{
    private const string Game = "synthetic-gathering";
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch;
    private static readonly OwnCharacterIdentity Identity = new(7, 9, 35, "SyntheticGatherer");

    [Theory]
    [InlineData(0, 0)]
    [InlineData(76, 76)]
    [InlineData(1_000, 1_000)]
    [InlineData(-1, null)]
    [InlineData(1_001, null)]
    [InlineData(4_294_967_372L, null)]
    public void OnlyCompleteEnvelopesWithCountsInRangeDecode(long count, int? expected)
    {
        foreach (var opcode in new ushort[] { 0xFFA7, 0xFFA8 })
        {
            var message = Count(opcode, count);
            Assert.Equal(expected, DailyGatheringLimitMessage.TryParse(message));
            Assert.Null(DailyGatheringLimitMessage.TryParse(message.AsSpan(0, message.Length - 1)));
            Assert.Null(DailyGatheringLimitMessage.TryParse([.. message, 0]));
        }
        Assert.Null(DailyGatheringLimitMessage.TryParse(Count(0x8D3E, count)));
    }

    [Fact]
    public void LivePresentationFollowsAbsoluteUpdatesWithoutARecording()
    {
        using var fixture = new Fixture();
        using var source = new OverlayEditorPresentationSource(() => UnifiedLootOverlayState.Empty,
            () => null, (_, _) => throw new InvalidOperationException(), () => throw new InvalidOperationException(),
            network: fixture.Network);
        using var consumer = source.Acquire();
        var refreshed = 0;
        source.Changed += () => Interlocked.Increment(ref refreshed);

        fixture.Begin();
        fixture.Send(Count(0xFFA7, 76), milliseconds: 20);
        Assert.Null(source.Capture().GatheringProgress!.Fraction);
        fixture.Prepare();
        fixture.Bind();
        Assert.Equal("76 / 1,000", source.Capture(language: "en").GatheringProgress!.Value);
        var refreshesAtEntry = Volatile.Read(ref refreshed);

        fixture.Send(Count(0xFFA8, 77), milliseconds: 200);
        Assert.True(Volatile.Read(ref refreshed) > refreshesAtEntry);
        fixture.Send(Count(0xFFA8, 77), milliseconds: 201);
        Assert.Equal("77 / 1,000", source.Capture(language: "en").GatheringProgress!.Value);
        fixture.Send(Count(0xFFA8, 0), milliseconds: 202);
        Assert.Equal(0d, source.Capture().GatheringProgress!.Fraction);
        fixture.Network.ProcessProgressGap(Game);
        Assert.Null(source.Capture().GatheringProgress!.Fraction);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("pre-own-update")]
    [InlineData("no-preparation")]
    [InlineData("other-character")]
    [InlineData("gap")]
    public void InitialStandRequiresACompleteUnambiguousOwnEntry(string failure)
    {
        using var fixture = new Fixture();
        fixture.Begin();
        fixture.Send(Count(0xFFA7, 76), milliseconds: 20);
        if (failure == "duplicate") fixture.Send(Count(0xFFA7, 76), milliseconds: 21);
        if (failure != "no-preparation") fixture.Prepare();
        if (failure == "pre-own-update") fixture.Send(Count(0xFFA8, 77), milliseconds: 50);
        if (failure == "gap") fixture.Network.ProcessProgressGap(Game);

        fixture.Bind(failure == "other-character" ? Identity with { CharacterId = 8 } : Identity);
        Assert.Null(fixture.Network.DailyGatheringLimit);
        fixture.Send(Count(0xFFA8, 77), milliseconds: 200);
        Assert.Equal(77, fixture.Network.DailyGatheringLimit!.Count);
    }

    [Fact]
    public void InitialStandBeforePreparationAndFullOwnNeedsNoGatheringUpdate()
    {
        using var fixture = new Fixture();
        using var presentation = new OverlayEditorPresentationSource(() => UnifiedLootOverlayState.Empty,
            () => null, (_, _) => throw new InvalidOperationException(), () => throw new InvalidOperationException(),
            network: fixture.Network);
        fixture.Send(Count(0xFFA7, 77), milliseconds: 20);
        fixture.Prepare();
        Assert.Null(fixture.Network.DailyGatheringLimit);
        Assert.Null(presentation.Capture().GatheringProgress!.Fraction);

        fixture.FullOwn();
        Assert.Equal(77, fixture.Network.DailyGatheringLimit!.Count);
        Assert.Equal(At.AddMilliseconds(20), fixture.Network.DailyGatheringLimit!.At);
        Assert.Equal("77 / 1,000", presentation.Capture(language: "en").GatheringProgress!.Value);
        fixture.FullOwn(milliseconds: 150);
        Assert.Null(fixture.Network.DailyGatheringLimit);
    }

    [Theory]
    [InlineData("no-preparation")]
    [InlineData("different-preparation-stream")]
    [InlineData("different-own-stream")]
    [InlineData("duplicate")]
    [InlineData("second-preparation")]
    [InlineData("invalid-preparation")]
    [InlineData("invalid-own")]
    [InlineData("gap-before-prepare")]
    [InlineData("gap-after-prepare")]
    [InlineData("expired")]
    [InlineData("unvalidated-own")]
    public void InterruptedUnprefixedEntryCannotPublishAnInitialStand(string failure)
    {
        using var fixture = new Fixture();
        fixture.Send(Count(0xFFA7, 77), milliseconds: 20);
        if (failure == "duplicate") fixture.Send(Count(0xFFA7, 77), milliseconds: 21);
        if (failure == "gap-before-prepare") fixture.Network.ProcessProgressGap(Game);
        if (failure == "invalid-preparation") fixture.Send([0x21, 0x36], milliseconds: 25);
        if (failure != "no-preparation") fixture.Prepare(stream: failure == "different-preparation-stream" ? "other" : Game);
        if (failure == "second-preparation") fixture.Prepare(milliseconds: 50);
        if (failure == "invalid-own") fixture.Send([0x33, 0x36], milliseconds: 60);
        if (failure == "gap-after-prepare") fixture.Network.ProcessProgressGap(Game);
        if (failure == "unvalidated-own") fixture.Bind();
        else fixture.FullOwn(failure == "expired" ? 120_021 : 100, failure == "different-own-stream" ? "other" : Game);
        Assert.NotNull(fixture.Network.OwnCharacter);
        Assert.Null(fixture.Network.DailyGatheringLimit);
    }

    [Theory]
    [InlineData("same-character", 77)]
    [InlineData("different-character", null)]
    [InlineData("different-server", null)]
    [InlineData("different-stream", null)]
    [InlineData("gap", null)]
    [InlineData("expired", null)]
    public void SecondOwnBindingNeedsMatchingCharacterAndContinuousFreshEntry(string variant, int? expected)
    {
        using var fixture = new Fixture();
        fixture.Send(Count(0xFFA7, 77), milliseconds: 20);
        fixture.Prepare();
        fixture.FullOwn();
        Assert.Equal(77, fixture.Network.DailyGatheringLimit!.Count);

        fixture.Prepare(milliseconds: 130);
        Assert.Null(fixture.Network.DailyGatheringLimit);
        if (variant == "gap") fixture.Network.ProcessProgressGap(Game);
        var identity = variant switch
        {
            "different-character" => Identity with { CharacterId = 8 },
            "different-server" => Identity with { ServerId = 10 },
            _ => Identity with { EntityKey = Identity.EntityKey + 1 },
        };
        fixture.FullOwn(variant == "expired" ? 120_021 : 170, variant == "different-stream" ? "other" : Game, identity);

        Assert.NotNull(fixture.Network.OwnCharacter);
        Assert.Equal(expected, fixture.Network.DailyGatheringLimit?.Count);
        if (expected.HasValue) Assert.Equal(At.AddMilliseconds(20), fixture.Network.DailyGatheringLimit!.At);
    }

    [Fact]
    public void GapDuringPreparationCannotResurrectAnUnprefixedStand()
    {
        using var fixture = new Fixture();
        fixture.Send(Count(0xFFA7, 77), milliseconds: 20);
        var interrupted = false;
        fixture.Network.WorldContext.Changed += () =>
        {
            if (interrupted) return;
            interrupted = true;
            fixture.Network.ProcessProgressGap(Game);
        };

        fixture.Prepare();
        fixture.FullOwn();

        Assert.True(interrupted);
        Assert.Null(fixture.Network.DailyGatheringLimit);
    }

    [Fact]
    public void ForeignStaleAndInvalidUpdatesKeepTheirOwnBoundaries()
    {
        using var fixture = new Fixture();
        fixture.Send(Count(0xFFA8, 76), milliseconds: 20);
        fixture.Bind();
        Assert.Null(fixture.Network.DailyGatheringLimit);
        fixture.Send(Count(0xFFA8, 77), milliseconds: 200);
        fixture.Send(Count(0xFFA8, 88), milliseconds: 210, stream: "other");
        fixture.Send(Count(0xFFA8, 76), milliseconds: 190);
        Assert.Equal(77, fixture.Network.DailyGatheringLimit!.Count);

        fixture.Send(Count(0xFFA8, 1_001), milliseconds: 220);
        Assert.Null(fixture.Network.DailyGatheringLimit!.Count);
        fixture.Send(Count(0xFFA8, 77), milliseconds: 215);
        Assert.Null(fixture.Network.DailyGatheringLimit!.Count);
        fixture.Send(Count(0xFFA8, 78), milliseconds: 230);
        Assert.Equal(78, fixture.Network.DailyGatheringLimit!.Count);
        fixture.Bind(Identity with { CharacterId = 8 }, milliseconds: 250);
        Assert.Null(fixture.Network.DailyGatheringLimit);
    }

    private static byte[] Count(ushort opcode, long count)
    {
        var bytes = new byte[18];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, opcode);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(2), count);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(10), 123456789L);
        return bytes;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "soulcrest-gathering-limit-" + Guid.NewGuid().ToString("N"));
        internal NetworkLootService Network { get; }

        internal Fixture()
        {
            Directory.CreateDirectory(_root);
            Network = new(new ProgressService(Path.Combine(_root, "progress.json")));
            typeof(NetworkLootService).GetProperty(nameof(NetworkLootService.Running))!.SetValue(Network, true);
        }

        internal void Begin()
        {
            var bytes = new byte[17];
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, 0x3620);
            bytes[4] = (byte)Identity.EntityKey;
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(9), Identity.CharacterId);
            Send(bytes);
        }

        internal void Prepare(int milliseconds = 40, string stream = Game)
        {
            var bytes = new byte[48];
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, 0x3621);
            Send(bytes, milliseconds, stream);
        }

        internal void FullOwn(int milliseconds = 100, string stream = Game, OwnCharacterIdentity? identity = null)
        {
            identity ??= Identity;
            using var bytes = new MemoryStream();
            using var writer = new BinaryWriter(bytes);
            writer.Write((ushort)0x3633);
            writer.Write((byte)identity.EntityKey);
            writer.Write(0); // Own flags
            writer.Write((byte)1);
            var name = Encoding.UTF8.GetBytes(identity.Name);
            writer.Write((byte)name.Length);
            writer.Write(name);
            writer.Write((ushort)identity.ServerId);
            writer.Write(0);
            writer.Write(new byte[36]);
            writer.Write(new byte[2 + 16 + 1]); // Detail lists and race
            writer.Write((ushort)0);
            writer.Write(new byte[18]);
            writer.Write((byte)1);
            writer.Write(new byte[2 + 52]);
            writer.Write(new byte[3 + 12]); // Stats, abnormals, equipment and map
            writer.Write(identity.CharacterId);
            writer.Write((byte)0);
            writer.Write((byte)1);
            writer.Write(new byte[32 + 2]);
            Send(bytes.ToArray(), milliseconds, stream);
        }

        internal void Bind(OwnCharacterIdentity? identity = null, int milliseconds = 100) =>
            Network.ObserveIdentity(Game, identity ?? Identity, At.AddMilliseconds(milliseconds));

        internal void Send(byte[] bytes, int milliseconds = 0, string stream = Game) =>
            Network.ProcessProgressMessage(stream, bytes, At.AddMilliseconds(milliseconds));

        public void Dispose()
        {
            Network.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }
}
