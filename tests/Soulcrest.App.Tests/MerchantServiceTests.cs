using System.Globalization;
using System.Text.Json;
using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Soulcrest.Core.Progress;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class MerchantServiceTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 13, 18, 0, 0, TimeSpan.Zero);
    private static readonly PetIdEntry FirstPet = PetIdTable.Find(1002)!;
    private static readonly PetIdEntry SecondPet = PetIdTable.Find(1004)!;
    private static string FirstId => FirstPet.CatalogId!;
    private static string SecondId => SecondPet.CatalogId!;

    [Fact]
    public void MixedTenSlotListSortsAllGoodsBeforeFilteringPetsAndNeverWritesProgress()
    {
        using var f = new Fixture();
        f.Baseline();
        var progressBytes = File.ReadAllBytes(f.ProgressPath);
        var goods = new[] { 90, 10, 80, 20, 70, 30, 60, 40, 50, 100 };
        var offers = Offers().Select((offer, i) => offer with { GoodsId = goods[i] }).ToArray();
        var message = List(offers);
        f.Accept(message);

        var snapshot = Assert.IsType<MerchantSnapshot>(f.Merchant.Snapshot);
        Assert.Equal((byte)77, snapshot.GroupKey);
        Assert.Equal(1, snapshot.SourceGeneration);
        Assert.Equal("synthetic-stream", snapshot.Stream);
        Assert.Equal(501, snapshot.ServerId);
        Assert.Equal(At.AddMilliseconds(100), snapshot.At);
        Assert.Equal(offers, snapshot.Offers);
        Assert.Collection(f.Merchant.Rows,
            row => { Assert.Equal(0, row.SlotIndex); Assert.Equal(SecondId, row.PetId); Assert.Equal("4/75", row.Counter); },
            row => { Assert.Equal(8, row.SlotIndex); Assert.Equal(FirstId, row.PetId); Assert.Equal("3/25", row.Counter); });
        Assert.All(f.Merchant.Rows, row => { Assert.True(row.IsFresh); Assert.False(row.IsMax); });
        Assert.Equal(progressBytes, File.ReadAllBytes(f.ProgressPath));
        Assert.Empty(f.Progress.Recent());
        Assert.False(File.Exists(f.EventsPath));

        Array.Fill(message, (byte)0);
        Assert.Equal(offers, snapshot.Offers);
        var mutable = Assert.IsAssignableFrom<IList<MerchantOffer>>(snapshot.Offers);
        Assert.Throws<NotSupportedException>(() => mutable[0] = offers[0] with { Quantity = 999 });
        var input = Offers();
        var separate = new MerchantSnapshot(77, 1, "synthetic-stream", 501, At, input);
        input[0] = input[0] with { Quantity = 999 };
        Assert.Equal(1, separate.Offers[0].Quantity);
    }

    [Fact]
    public void UnknownItemAndUnknownPetNumberKeepTheirSlotsWithoutCreatingCounterRows()
    {
        using var f = new Fixture(item => item == 20_000 ? FirstPet.Id : item == 20_001 ? int.MaxValue : null);
        f.Baseline();
        var offers = Offers();
        offers[0] = offers[0] with { GoodsId = 999 };
        f.Accept(List(offers));
        Assert.Equal(10, f.Merchant.Snapshot!.Offers.Count);
        var row = Assert.Single(f.Merchant.Rows);
        Assert.Equal(9, row.SlotIndex);
        Assert.Equal(FirstId, row.PetId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(502)]
    public void UnknownOrDifferentMerchantServerUsesTheSameGlobalPetCounters(int? sourceServer)
    {
        using var f = new Fixture();
        f.Baseline();
        f.Progress.SetSouls(FirstId, 105);
        f.Accept(List(Offers()), server: sourceServer);
        Assert.Equal(10, f.Merchant.Snapshot!.Offers.Count);
        Assert.Collection(f.Merchant.Rows,
            row => Assert.Equal("max", row.Counter),
            row => Assert.Equal("4/75", row.Counter));
    }

    [Fact]
    public void TwoUnknownServersNeverAuthorizeLegacyCounters()
    {
        using var f = new Fixture(legacyTotal: 105, legacyServer: null);
        f.Accept(List(Offers()), server: null);
        Assert.Null(f.Progress.ActiveServerId);
        Assert.All(f.Merchant.Rows, row => { Assert.Equal("?", row.Counter); Assert.False(row.IsMax); });
    }

    [Fact]
    public void CountersReactToSnapshotGainSpendAndLevelWithoutAnotherShopList()
    {
        using var f = new Fixture();
        f.Baseline();
        f.Accept(List(Offers()));
        var snapshot = f.Merchant.Snapshot;
        var changed = 0;
        f.Merchant.Changed += () => changed++;
        Assert.True(f.Progress.ApplyNetworkGain([new(FirstPet.Id, 2)], Context(3)));
        Assert.Equal("5/25", f.FirstRow.Counter);
        Assert.True(f.Progress.ApplyNetworkSpent([new(FirstPet.Id, 5)], Context(4)));
        Assert.Equal("0/25", f.FirstRow.Counter);
        Assert.True(f.Progress.ApplyNetworkSnapshot(Pets(2, 6), Context(5)));
        Assert.Equal("6/75", f.FirstRow.Counter);
        Assert.True(f.Progress.ApplyNetworkSnapshot(Pets(3, 0), Context(6)));
        Assert.Equal("max", f.FirstRow.Counter);
        Assert.True(f.FirstRow.IsMax);
        Assert.True(f.FirstRow.IsFresh);
        Assert.Same(snapshot, f.Merchant.Snapshot);
        Assert.Equal(4, changed);
        Assert.Single(f.Progress.Recent());
        Assert.Single(File.ReadAllLines(f.EventsPath));
    }

    [Fact]
    public void LegacyFarmTotalAndSeededDeltaCannotInventObservedBalanceOrMax()
    {
        using var f = new Fixture(legacyTotal: 105);
        Assert.True(f.Progress.BindNetworkContext(Context(1)));
        f.Accept(List(Offers()));
        Assert.Equal("?", f.FirstRow.Counter);
        Assert.False(f.FirstRow.IsMax);
        Assert.True(f.Progress.ApplyNetworkGain([new(FirstPet.Id, 2)], Context(2)));
        Assert.Equal(107, f.Progress.Souls(FirstId));
        Assert.Equal("?", f.FirstRow.Counter);
        Assert.False(f.FirstRow.IsMax);
    }

    [Fact]
    public void ObservedLevelControlsMaxEvenWhenTheSeparateBalanceIsUnknown()
    {
        using var f = new Fixture();
        Assert.True(f.Progress.BindNetworkContext(Context(1)));
        Assert.True(f.Progress.ApplyNetworkAcquisition(new(1001, FirstPet.Id, 3), Context(2)));
        f.Accept(List(Offers()));
        Assert.Equal("max", f.FirstRow.Counter);
        Assert.True(f.FirstRow.IsMax);
        Assert.False(f.FirstRow.IsFresh);
        f.Progress.MarkNetworkPetSourceStale();
        Assert.Equal("?", f.FirstRow.Counter);
        Assert.False(f.FirstRow.IsMax);
    }

    [Fact]
    public void LargeEarnedTotalDoesNotOverrideTheObservedLevelAndManualCounterStaysExplicit()
    {
        using var f = new Fixture();
        Assert.True(f.Progress.BindNetworkContext(Context(1)));
        Assert.True(f.Progress.ApplyNetworkSnapshot(Pets(1, 100), Context(2)));
        f.Accept(List(Offers()));
        Assert.Equal("100/25", f.FirstRow.Counter);
        Assert.False(f.FirstRow.IsMax);
        f.Progress.SetSouls(FirstId, 8);
        Assert.Equal("3/25", f.FirstRow.Counter);
        Assert.False(f.FirstRow.IsFresh);
        Assert.False(f.FirstRow.IsMax);
    }

    [Fact]
    public void ChangingTheCharacterServerKeepsTheGlobalMerchantCounter()
    {
        using var f = new Fixture();
        f.Baseline();
        f.Accept(List(Offers()));
        Assert.True(f.Progress.BindNetworkContext(Context(3, server: 502)));
        Assert.True(f.Progress.ApplyNetworkSnapshot(Pets(2, 17), Context(4, server: 502)));
        Assert.Equal("17/75", f.FirstRow.Counter);
    }

    [Theory]
    [InlineData(-1L, false)]
    [InlineData(0L, true)]
    [InlineData(20_000_000L, true)]
    [InlineData(20_000_001L, false)]
    public void HandshakeRequiresAForwardTimestampWithinTwoSeconds(long ticks, bool accepted)
    {
        using var f = new Fixture();
        f.Source.Send(Status(), at: At);
        f.Source.Send(List(Offers()), at: At.AddTicks(ticks));
        Assert.Equal(accepted, f.Merchant.Snapshot is not null);
    }

    [Fact]
    public void AFullListAloneCannotAppearAndAStatusImmediatelyClearsThePreviousList()
    {
        using var f = new Fixture();
        f.Source.Send(List(Offers()));
        Assert.Null(f.Merchant.Snapshot);
        f.Accept(List(Offers()));
        Assert.NotNull(f.Merchant.Snapshot);
        f.Source.Send(Status(88), at: At.AddSeconds(1));
        Assert.Null(f.Merchant.Snapshot);
        Assert.Empty(f.Merchant.Rows);
        f.Source.Send(List(Offers(), 88), at: At.AddSeconds(1.1));
        Assert.Equal((byte)88, f.Merchant.Snapshot!.GroupKey);
    }

    [Theory]
    [InlineData("group")]
    [InlineData("stream")]
    [InlineData("generation")]
    [InlineData("server")]
    [InlineData("null-server")]
    public void AListMustMatchEveryPendingStatusContextField(string mismatch)
    {
        using var f = new Fixture();
        f.Source.Send(Status());
        f.Source.Send(List(Offers(), mismatch == "group" ? (byte)88 : (byte)77),
            stream: mismatch == "stream" ? "another-synthetic-stream" : "synthetic-stream",
            generation: mismatch == "generation" ? 2 : 1,
            server: mismatch == "server" ? 502 : mismatch == "null-server" ? null : 501,
            at: At.AddMilliseconds(100));
        Assert.Null(f.Merchant.Snapshot);
        f.Source.Send(List(Offers()), at: At.AddMilliseconds(200));
        Assert.NotNull(f.Merchant.Snapshot); // A mismatched list must not consume the valid pending status.
    }

    [Fact]
    public void AnOlderGroupCannotConsumeTheNewerPendingStatus()
    {
        using var f = new Fixture();
        f.Source.Send(Status(77));
        f.Source.Send(Status(88), at: At.AddMilliseconds(100));
        f.Source.Send(List(Offers(), 77), at: At.AddMilliseconds(200));
        Assert.Null(f.Merchant.Snapshot);
        f.Source.Send(List(Offers(), 88), at: At.AddMilliseconds(300));
        Assert.Equal((byte)88, f.Merchant.Snapshot!.GroupKey);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnknownAndKnownServerMarkersCannotBeJoinedInEitherDirection(bool statusUnbound)
    {
        using var f = new Fixture();
        f.Source.Send(Status(), server: statusUnbound ? null : 501);
        f.Source.Send(List(Offers()), server: statusUnbound ? 501 : null, at: At.AddMilliseconds(100));
        Assert.Null(f.Merchant.Snapshot);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MalformedShopMessagesClearCurrentButPreserveAnIndependentPendingStatus(bool badStatus)
    {
        using var f = new Fixture();
        f.Accept(List(Offers()));
        f.Source.Send(badStatus ? Status(result: 1) : List(Offers())[..^1]);
        Assert.Null(f.Merchant.Snapshot);
        f.Source.Send(Status(88), at: At.AddMilliseconds(200));
        f.Source.Send(badStatus ? Status(result: 1) : List(Offers())[..^1], at: At.AddMilliseconds(300));
        f.Source.Send(List(Offers(), 88), at: At.AddMilliseconds(400));
        Assert.Equal((byte)88, f.Merchant.Snapshot!.GroupKey);
    }

    [Fact]
    public void SessionResetClearsPendingAndRejectsMessagesFromThePreviousGeneration()
    {
        using var f = new Fixture();
        f.Source.Send(Status());
        f.Source.Reset();
        f.Source.Send(List(Offers()));
        f.Source.Send(Status());
        f.Source.Send(List(Offers()));
        Assert.Null(f.Merchant.Snapshot);
        f.Accept(List(Offers()), generation: 2);
        Assert.Equal(2, f.Merchant.Snapshot!.SourceGeneration);
        f.Source.Reset();
        Assert.Null(f.Merchant.Snapshot);
        Assert.Empty(f.Merchant.Rows);
    }

    [Fact]
    public void ResetBeforeTheFirstMarkerRejectsTheOldInitialGeneration()
    {
        using var f = new Fixture();
        f.Source.Reset();
        f.Accept(List(Offers()), generation: 0);
        Assert.Null(f.Merchant.Snapshot);
        f.Accept(List(Offers()), generation: 1);
        Assert.Equal(1, f.Merchant.Snapshot!.SourceGeneration);
    }

    [Fact]
    public void EveryConsecutiveResetAdvancesTheFenceEvenWithoutAnInterveningMarker()
    {
        using var f = new Fixture();
        f.Accept(List(Offers()), generation: 1);
        f.Source.Reset();
        f.Source.Reset();
        f.Accept(List(Offers()), generation: 2);
        Assert.Null(f.Merchant.Snapshot);
        f.Accept(List(Offers()), generation: 3);
        Assert.Equal(3, f.Merchant.Snapshot!.SourceGeneration);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASubscriberCanResetOrDisposeDuringPublicationWithoutRevivingTheSnapshot(bool dispose)
    {
        using var f = new Fixture();
        var acted = false;
        f.Merchant.Changed += () =>
        {
            if (acted || f.Merchant.Snapshot is null) return;
            acted = true;
            if (dispose) f.Merchant.Dispose(); else f.Source.Reset();
        };
        f.Accept(List(Offers()));
        Assert.True(acted);
        Assert.Null(f.Merchant.Snapshot);
        Assert.Empty(f.Merchant.Rows);
    }

    [Fact]
    public void AResetDuringRowProjectionCannotReturnRowsFromTheDetachedSnapshot()
    {
        Source? source = null;
        var reset = false;
        using var f = new Fixture(item =>
        {
            if (!reset) { reset = true; source!.Reset(); }
            return item == 20_000 ? FirstPet.Id : null;
        });
        source = f.Source;
        f.Baseline();
        f.Accept(List(Offers()));
        Assert.Empty(f.Merchant.Rows);
        Assert.True(reset);
        Assert.Null(f.Merchant.Snapshot);
    }

    [Fact]
    public void DisposeUnsubscribesBothSourceEventsAndNoLongerRaisesProgressChanges()
    {
        using var f = new Fixture();
        f.Baseline();
        f.Accept(List(Offers()));
        var changes = 0;
        f.Merchant.Changed += () => changes++;
        Assert.Equal(1, f.Source.MessageSubscribers);
        Assert.Equal(1, f.Source.ResetSubscribers);
        f.Merchant.Dispose();
        f.Merchant.Dispose();
        Assert.Equal(0, f.Source.MessageSubscribers);
        Assert.Equal(0, f.Source.ResetSubscribers);
        f.Progress.SetSouls(FirstId, 8);
        f.Source.Reset();
        f.Accept(List(Offers()), generation: 2);
        Assert.Equal(0, changes);
        Assert.Null(f.Merchant.Snapshot);
        Assert.Empty(f.Merchant.Rows);
    }

    private static MerchantOffer[] Offers() => Enumerable.Range(0, 10)
        .Select(i => new MerchantOffer(100 + i, 20_000 + i, 1, 100_000, 70_000)).ToArray();

    private static byte[] List(IReadOnlyList<MerchantOffer> offers, byte group = 77)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0x5691); writer.Write(group); writer.Write(checked((byte)offers.Count));
        for (var i = 0; i < offers.Count; i++)
        {
            var offer = offers[i];
            writer.Write(offer.GoodsId); writer.Write(offer.ItemId); writer.Write(offer.Quantity);
            writer.Write((ushort)0xA17F); writer.Write(offer.OriginalPrice); writer.Write(offer.CurrentPrice);
            if (i % 4 == 0) writer.Write((byte)0xFF); // Opaque packed booleans do not suppress a row.
        }
        writer.Write(new byte[12]);
        return stream.ToArray();
    }

    private static byte[] Status(byte group = 77, ushort result = 0) =>
        [(byte)0x93, (byte)0x56, (byte)result, (byte)(result >> 8), group];

    private static NetworkProgressContext Context(long sequence, int server = 501) =>
        new(new(101, server, 1001, "Synthetic Merchant Profile"), 1, sequence, At.AddSeconds(sequence));

    private static PetProgressSnapshot Pets(int firstLevel, int firstBalance) => new(
        new Dictionary<int, int> { [FirstPet.Id] = firstLevel, [SecondPet.Id] = 2 },
        new Dictionary<int, int> { [FirstPet.Id] = firstBalance, [SecondPet.Id] = 4 });

    private sealed class Source : INetworkMerchantSource
    {
        public event Action<MerchantNetworkMessage>? MerchantMessageReceived;
        public event Action? MerchantSessionReset;
        internal int MessageSubscribers => MerchantMessageReceived?.GetInvocationList().Length ?? 0;
        internal int ResetSubscribers => MerchantSessionReset?.GetInvocationList().Length ?? 0;
        internal void Send(byte[] message, string stream = "synthetic-stream", long generation = 1,
            int? server = 501, DateTimeOffset? at = null) =>
            MerchantMessageReceived?.Invoke(new(stream, generation, server, at ?? At, message));
        internal void Reset() => MerchantSessionReset?.Invoke();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "soulcrest-merchant-" + Guid.NewGuid().ToString("N"));
        internal Source Source { get; } = new();
        internal ProgressService Progress { get; }
        internal MerchantService Merchant { get; }
        internal string ProgressPath => Path.Combine(_folder, "progress.json");
        internal string EventsPath => Path.Combine(_folder, "events.jsonl");
        internal MerchantCounterRow FirstRow => Assert.Single(Merchant.Rows, row => row.PetId == FirstId);

        internal Fixture(Func<int, int?>? lookup = null, int? legacyTotal = null, int? legacyServer = 501)
        {
            Directory.CreateDirectory(_folder);
            if (legacyTotal is { } total)
            {
                var profile = new ProgressProfile { SchemaVersion = 2, ServerId = legacyServer, ActiveServerId = legacyServer };
                profile.Pets[FirstId] = new() { Souls = total };
                File.WriteAllBytes(ProgressPath, JsonSerializer.SerializeToUtf8Bytes(profile));
            }
            var catalog = new PetCatalog(new[] { FirstPet, SecondPet }.Select(pet => new PetDefinition
            {
                Id = pet.CatalogId!, En = pet.En, Genus = pet.Genus.ToLowerInvariant(),
                NumericId = pet.Id.ToString(CultureInfo.InvariantCulture)
            }));
            Progress = new(ProgressPath, catalog);
            Merchant = new(Source, Progress, lookup ?? (item => item == 20_000 ? FirstPet.Id : item == 20_001 ? SecondPet.Id : null));
        }

        internal void Baseline()
        {
            Assert.True(Progress.BindNetworkContext(Context(1)));
            Assert.True(Progress.ApplyNetworkSnapshot(Pets(1, 3), Context(2)));
        }

        internal void Accept(byte[] message, int? server = 501, long generation = 1)
        {
            Source.Send(Status(), generation: generation, server: server, at: At);
            Source.Send(message, generation: generation, server: server, at: At.AddMilliseconds(100));
        }

        public void Dispose()
        {
            Merchant.Dispose();
            Directory.Delete(_folder, recursive: true);
        }
    }
}
