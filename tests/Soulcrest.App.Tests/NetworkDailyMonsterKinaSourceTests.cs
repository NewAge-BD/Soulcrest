using Soulcrest.App.Network;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>Only synthetic daily totals, inventory records and entry prefixes.</summary>
public sealed class NetworkDailyMonsterKinaSourceTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch;
    private static readonly OwnCharacterIdentity Identity = new(7, 9, 300, "SyntheticDaily");
    private const string Game = "synthetic-daily-own", Foreign = "synthetic-daily-foreign";

    [Fact]
    public void InitialSnapshotIsUnknownRatherThanAnInventedZero()
    {
        using var f = new Fixture();
        var snapshot = f.Network.DailyMonsterKinaSnapshot;
        Assert.False(snapshot.IsReady); Assert.Null(snapshot.Character);
        Assert.Null(snapshot.Total); Assert.Null(snapshot.At); Assert.Equal(0, snapshot.Sequence);
        Assert.Empty(f.Gains);
    }

    [Fact]
    public void InitialAbsoluteTotalWaitsForMatchingEntryPreparationAndOwn()
    {
        using var f = new Fixture(); f.Begin();
        f.Send(Initial(100), milliseconds: 20);
        Assert.False(f.Network.DailyMonsterKinaSnapshot.IsReady);
        f.Prepare(); Assert.False(f.Network.DailyMonsterKinaSnapshot.IsReady);
        f.Bind(milliseconds: 100);
        var snapshot = f.Network.DailyMonsterKinaSnapshot;
        Assert.True(snapshot.IsReady); Assert.Equal(100L, snapshot.Total);
        Assert.Equal(At.AddMilliseconds(20), snapshot.At);
        Assert.Same(f.Network.OwnCharacter, snapshot.Character);
        Assert.True(snapshot.Sequence > 0); Assert.Empty(f.Gains);
    }

    [Fact]
    public void FirstUpdateWithoutAnInitialBaselineOnlyInitializesTheDisplay()
    {
        using var f = new Fixture(); f.Bind();
        f.Send(Update(100), milliseconds: 100);
        Assert.True(f.Network.DailyMonsterKinaSnapshot.IsReady);
        Assert.Equal(100L, f.Network.DailyMonsterKinaSnapshot.Total);
        Assert.Empty(f.Gains);
        f.Send(Update(140), milliseconds: 200);
        Assert.Equal(40L, Assert.Single(f.Gains).UnboundKina);
    }

    [Fact]
    public void PositiveDifferencePublishesExactlyOneCanonicalGain()
    {
        using var f = new Fixture(); f.Login();
        var character = f.Network.OwnCharacter;
        f.Send(Update(140), milliseconds: 200);
        var gain = Assert.Single(f.Gains);
        Assert.Equal(40L, gain.UnboundKina); Assert.Same(character, gain.Character);
        Assert.Equal(At.AddMilliseconds(200), gain.At);
        Assert.Equal(gain.Sequence, f.Network.DailyMonsterKinaSnapshot.Sequence);
        Assert.Equal(140L, f.Network.DailyMonsterKinaSnapshot.Total);
    }

    [Fact]
    public void SequenceRemainsMonotoneAfterMalformedUpdateAndBaselineReinitialization()
    {
        using var f = new Fixture(); f.Login();
        f.Send(Update(140), milliseconds: 200);
        var priorSequence = Assert.Single(f.Gains).Sequence;
        f.Send([0xA6, 0xFF, 1], milliseconds: 250);
        Assert.False(f.Network.DailyMonsterKinaSnapshot.IsReady);
        f.Send(Update(200), milliseconds: 300);
        var baselineSequence = f.Network.DailyMonsterKinaSnapshot.Sequence;
        Assert.True(baselineSequence > priorSequence);
        Assert.Single(f.Gains);
        f.Send(Update(230), milliseconds: 400);
        Assert.Equal(2, f.Gains.Count);
        Assert.Equal(30, f.Gains[1].UnboundKina);
        Assert.True(f.Gains[1].Sequence > baselineSequence);
    }
    [Fact]
    public void RepeatedAbsoluteUpdatesDoNotCountTheSameIncomeAgain()
    {
        using var f = new Fixture(); f.Login();
        f.Send(Update(140), milliseconds: 200);
        f.Send(Update(140), milliseconds: 250);
        f.Send(Update(140), milliseconds: 300);
        Assert.Equal(40L, Assert.Single(f.Gains).UnboundKina);
        Assert.Equal(140L, f.Network.DailyMonsterKinaSnapshot.Total);
        Assert.Equal(At.AddMilliseconds(300), f.Network.DailyMonsterKinaSnapshot.At);
    }

    [Fact]
    public void LowerServerTotalIsAuthoritativeWithoutNegativeIncomeOrGuessedDayReset()
    {
        using var f = new Fixture(); f.Login(total: 900000);
        f.Send(Update(25), milliseconds: 200);
        Assert.Equal(25L, f.Network.DailyMonsterKinaSnapshot.Total); Assert.Empty(f.Gains);
        f.Send(Update(35), milliseconds: 300);
        Assert.Equal(10L, Assert.Single(f.Gains).UnboundKina);
    }

    [Fact]
    public void ZeroServerTotalIsKnownAndTheNextPositiveDifferenceStillCounts()
    {
        using var f = new Fixture(); f.Login();
        f.Send(Update(0), milliseconds: 200);
        Assert.True(f.Network.DailyMonsterKinaSnapshot.IsReady);
        Assert.Equal(0L, f.Network.DailyMonsterKinaSnapshot.Total); Assert.Empty(f.Gains);
        f.Send(Update(5), milliseconds: 300);
        Assert.Equal(5L, Assert.Single(f.Gains).UnboundKina);
    }

    [Fact]
    public void Int64TotalsPreserveSmallDifferencesWithoutFloatingPointLoss()
    {
        using var f = new Fixture(); f.Login(total: long.MaxValue - 7);
        f.Send(Update(long.MaxValue), milliseconds: 200);
        Assert.Equal(7L, Assert.Single(f.Gains).UnboundKina);
        Assert.Equal(long.MaxValue, f.Network.DailyMonsterKinaSnapshot.Total);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DuplicateInitialAndPreOwnUpdateDiscardTheTentativeTotal(bool update)
    {
        using var f = new Fixture(); f.Begin();
        f.Send(Initial(100), milliseconds: 20);
        f.Send(update ? Update(150) : Initial(100), milliseconds: 30);
        f.Prepare(); f.Bind(milliseconds: 100);
        Assert.False(f.Network.DailyMonsterKinaSnapshot.IsReady); Assert.Empty(f.Gains);
        f.Send(Update(200), milliseconds: 200);
        Assert.Equal(200L, f.Network.DailyMonsterKinaSnapshot.Total); Assert.Empty(f.Gains);
        f.Send(Update(210), milliseconds: 300);
        Assert.Equal(10L, Assert.Single(f.Gains).UnboundKina);
    }

    [Fact]
    public void InitialTotalAfterPreparationIsOutsideTheQualifiedVariant()
    {
        using var f = new Fixture(); f.Begin(); f.Prepare();
        f.Send(Initial(100), milliseconds: 60); f.Bind(milliseconds: 100);
        Assert.False(f.Network.DailyMonsterKinaSnapshot.IsReady); Assert.Empty(f.Gains);
    }

    [Fact]
    public void InitialTotalWithoutEntryCannotBeAdoptedByALaterOwn()
    {
        using var f = new Fixture();
        f.Send(Initial(100), milliseconds: 20); f.Bind(milliseconds: 100);
        Assert.False(f.Network.DailyMonsterKinaSnapshot.IsReady); Assert.Empty(f.Gains);
    }

    [Fact]
    public void EntryWithoutPreparationCannotAuthorizeItsInitialTotal()
    {
        using var f = new Fixture(); f.Begin();
        f.Send(Initial(100), milliseconds: 20); f.Bind(milliseconds: 100);
        Assert.False(f.Network.DailyMonsterKinaSnapshot.IsReady); Assert.Empty(f.Gains);
    }

    [Theory]
    [InlineData(8, 300)]
    [InlineData(7, 301)]
    public void EntryCharacterAndEntityMustBothMatchTheFullOwn(long character, int entity)
    {
        using var f = new Fixture(); f.Begin();
        f.Send(Initial(100), milliseconds: 20); f.Prepare();
        f.Bind(new(character, 9, entity, "SyntheticMismatch"), milliseconds: 100);
        Assert.False(f.Network.DailyMonsterKinaSnapshot.IsReady); Assert.Empty(f.Gains);
    }

    [Fact]
    public void ForeignMessagesAndForeignGapDoNotChangeTheOwnDailyTotal()
    {
        using var f = new Fixture(); f.Login();
        f.Send(Initial(5000), Foreign, 150); f.Send(Update(6000), Foreign, 200);
        f.Network.ProcessProgressGap(Foreign);
        Assert.Equal(100L, f.Network.DailyMonsterKinaSnapshot.Total); Assert.Empty(f.Gains);
        f.Send(Update(140), milliseconds: 250);
        Assert.Equal(40L, Assert.Single(f.Gains).UnboundKina);
    }

    [Theory]
    [InlineData("own-gap")]
    [InlineData("global-gap")]
    [InlineData("reset")]
    [InlineData("own-refresh")]
    [InlineData("character-change")]
    public void OwnBoundaryPreventsAnyCrossGapDifference(string boundary)
    {
        using var f = new Fixture(); f.Login();
        switch (boundary)
        {
            case "own-gap": f.Network.ProcessProgressGap(Game); break;
            case "global-gap": f.Network.ProcessProgressGap("*"); break;
            case "reset": f.Network.ResetProgressContext(); break;
            case "own-refresh": f.Bind(milliseconds: 150); break;
            case "character-change": f.Bind(new(8, 9, 301, "SyntheticOther"), milliseconds: 150); break;
        }
        var snapshot = f.Network.DailyMonsterKinaSnapshot;
        Assert.False(snapshot.IsReady); Assert.Null(snapshot.Total); Assert.Null(snapshot.Character); Assert.Null(snapshot.At);
        if (f.Network.OwnCharacter is null) f.Bind(milliseconds: 180);
        f.Send(Update(10000), milliseconds: 200);
        Assert.Equal(10000L, f.Network.DailyMonsterKinaSnapshot.Total); Assert.Empty(f.Gains);
        f.Send(Update(10005), milliseconds: 300);
        Assert.Equal(5L, Assert.Single(f.Gains).UnboundKina);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MalformedDailyUpdateCannotDestroyIndependentOwnOrPetTracking(bool trailingByte)
    {
        using var f = new Fixture(); f.Login();
        var own = f.Network.OwnCharacter;
        var souls = new List<NetworkSoul>(); f.Network.SoulGained += souls.Add;
        f.Send(trailingByte ? [.. Update(140), 0] : [0xA6, 0xFF, 0], milliseconds: 200);
        Assert.False(f.Network.DailyMonsterKinaSnapshot.IsReady); Assert.Same(own, f.Network.OwnCharacter);
        f.Send(Packet(0x900D, w => { w.Write((byte)1); w.Write(1004); w.Write(2); }), milliseconds: 250);
        Assert.Equal(2, Assert.Single(souls).Souls); Assert.Empty(f.Gains);
    }

    [Fact]
    public void NegativeTotalRejectsTheDailySourceWithoutEmittingALoss()
    {
        using var f = new Fixture(); f.Login();
        f.Send(Update(-1), milliseconds: 200);
        Assert.False(f.Network.DailyMonsterKinaSnapshot.IsReady); Assert.Empty(f.Gains);
    }

    [Fact]
    public void SnapshotObjectsRemainImmutableAfterANewerObservation()
    {
        using var f = new Fixture(); f.Login();
        var before = f.Network.DailyMonsterKinaSnapshot;
        f.Send(Update(140), milliseconds: 200);
        Assert.Equal(100L, before.Total); Assert.Equal(140L, f.Network.DailyMonsterKinaSnapshot.Total);
        Assert.NotSame(before, f.Network.DailyMonsterKinaSnapshot);
    }

    [Fact]
    public void DisabledSourceCannotExposeALiveTotalOrPublishIncome()
    {
        using var f = new Fixture(running: false); f.Login();
        f.Send(Update(140), milliseconds: 200);
        Assert.False(f.Network.DailyMonsterKinaSnapshot.IsReady); Assert.Empty(f.Gains);
    }

    [Theory]
    [InlineData("preparation")]
    [InlineData("own")]
    public void DailyObservationDuringResetCannotReplayAnOlderPendingInitialTotal(string boundary)
    {
        using var f = new Fixture(); f.Begin(); f.Send(Initial(100), milliseconds: 20);
        if (boundary == "own") f.Prepare();
        var injected = false;
        f.Network.WorldContext.Changed += () =>
        {
            if (injected) return;
            injected = true; f.Send(Update(150), milliseconds: 80);
        };
        if (boundary == "preparation") f.Prepare();
        f.Bind(milliseconds: 100);
        Assert.True(injected); Assert.False(f.Network.DailyMonsterKinaSnapshot.IsReady);
        Assert.Empty(f.Gains);
    }

    [Fact]
    public void ReentrantGainsAreDeliveredInOrderToEverySubscriber()
    {
        using var f = new Fixture(); f.Login();
        var calls = new List<string>();
        f.Network.MonsterKinaGained += gain =>
        {
            calls.Add("A:" + gain.UnboundKina);
            if (gain.UnboundKina == 40) f.Send(Update(150), milliseconds: 250);
        };
        f.Network.MonsterKinaGained += gain => calls.Add("B:" + gain.UnboundKina);
        f.Send(Update(140), milliseconds: 200);
        Assert.Equal(new[] { "A:40", "B:40", "A:10", "B:10" }, calls);
        Assert.Equal(new long[] { 40, 10 }, f.Gains.Select(g => g.UnboundKina));
        Assert.True(f.Gains[1].Sequence > f.Gains[0].Sequence);
    }

    [Fact]
    public void ReentrantOwnChangeDiscardsQueuedDailyGainsFromThePreviousCharacter()
    {
        using var f = new Fixture(); f.Login();
        var changed = false;
        f.Network.MonsterKinaGained += _ =>
        {
            if (changed) return;
            changed = true; f.Send(Update(150), milliseconds: 250);
            f.Bind(new(8, 9, 301, "SyntheticOther"), milliseconds: 300);
        };
        f.Send(Update(140), milliseconds: 200);
        Assert.Equal(40L, Assert.Single(f.Gains).UnboundKina);
        Assert.False(f.Network.DailyMonsterKinaSnapshot.IsReady);
    }

    [Fact]
    public void WalletAndDailyChannelsPublishTheirOwnEvidenceWithoutCallingEachOther()
    {
        using var f = new Fixture(); var wallets = new List<NetworkKinaGain>(); f.Network.KinaGained += wallets.Add;
        f.Begin(); f.Send(Initial(1000), milliseconds: 10); f.Send(WalletList(100), milliseconds: 20);
        f.Prepare(); f.Bind(milliseconds: 100);
        f.Send(WalletChange(140), milliseconds: 200);
        Assert.Equal(40L, Assert.Single(wallets).UnboundKina); Assert.Empty(f.Gains);
        f.Send(Update(1040), milliseconds: 201);
        Assert.Equal(40L, Assert.Single(f.Gains).UnboundKina); Assert.Single(wallets);
        Assert.Same(wallets[0].Character, f.Gains[0].Character);
    }

    [Fact]
    public void MalformedWalletDoesNotInvalidateTheIndependentDailySource()
    {
        using var f = new Fixture(); f.Login();
        f.Send([0x1B, 0x56, 1], milliseconds: 150);
        Assert.True(f.Network.KinaSnapshot.FormatRejected);
        Assert.True(f.Network.DailyMonsterKinaSnapshot.IsReady);
        f.Send(Update(140), milliseconds: 200);
        Assert.Equal(40L, Assert.Single(f.Gains).UnboundKina);
    }

    [Theory]
    [InlineData(120000, true)]
    [InlineData(120001, false)]
    public void ExpiredEntryCannotBindAnOldInitialTotal(int ownMilliseconds, bool accepted)
    {
        using var f = new Fixture(); f.Begin(); f.Send(Initial(100), milliseconds: 20); f.Prepare();
        f.Bind(milliseconds: ownMilliseconds);
        Assert.Equal(accepted, f.Network.DailyMonsterKinaSnapshot.IsReady);
        Assert.Empty(f.Gains);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "soulcrest-daily-source-" + Guid.NewGuid().ToString("N"));
        internal NetworkLootService Network { get; }
        internal List<NetworkKinaGain> Gains { get; } = [];
        internal Fixture(bool running = true)
        {
            Directory.CreateDirectory(_root);
            var progress = new ProgressService(Path.Combine(_root, "progress.json"), new PetCatalog([
                new PetDefinition { Id = PetIdTable.Find(1004)!.CatalogId ?? PetCatalog.Slug(PetIdTable.Find(1004)!.En), En = PetIdTable.Find(1004)!.En, Genus = PetIdTable.Find(1004)!.Genus, NumericId = "1004" }]));
            Network = new(progress);
            typeof(NetworkLootService).GetProperty(nameof(NetworkLootService.Running))!.SetValue(Network, running);
            Network.MonsterKinaGained += Gains.Add;
        }
        internal void Begin() => Send(Entry(Identity));
        internal void Prepare() => Send(Preparation(), milliseconds: 40);
        internal void Bind(OwnCharacterIdentity? identity = null, long milliseconds = 0) =>
            Network.ObserveIdentity(Game, identity ?? Identity, At.AddMilliseconds(milliseconds));
        internal void Send(byte[] message, string stream = Game, long milliseconds = 0) =>
            Network.ProcessProgressMessage(stream, message, At.AddMilliseconds(milliseconds));
        internal void Login(long total = 100) { Begin(); Send(Initial(total), milliseconds: 20); Prepare(); Bind(milliseconds: 100); }
        public void Dispose() { Network.Dispose(); Directory.Delete(_root, recursive: true); }
    }

    private static byte[] Initial(long total) => Daily(0xFFA5, total);
    private static byte[] Update(long total) => Daily(0xFFA6, total);
    private static byte[] Daily(ushort opcode, long total) => Packet(opcode, w => { w.Write(total); w.Write(0L); });
    private static byte[] Entry(OwnCharacterIdentity identity) => Packet(0x3620, w =>
    { w.Write((ushort)0); V(w, identity.EntityKey); w.Write(new byte[4]); w.Write(identity.CharacterId); });
    private static byte[] Preparation() => Packet(0x3621, w => w.Write(new byte[46]));
    private static byte[] WalletList(long balance) => Packet(0x5611, w =>
    { w.Write((ushort)528); V(w, 1); WalletItem(w, balance); w.Write(new byte[6]); });
    private static byte[] WalletChange(long balance) => Packet(0x561B, w =>
    { V(w, 1); w.Write((byte)1); WalletItem(w, balance); w.Write(new byte[] { 11, 0, 0 }); });
    private static void WalletItem(BinaryWriter w, long balance)
    {
        w.Write((byte)0); w.Write(11L); w.Write(930100031); w.Write(balance); w.Write((byte)1); w.Write(0);
        w.Write((byte)0); V(w, 0); w.Write(0L); // Both false bools share this one cached zero byte.
    }
    private static void V(BinaryWriter w, int value)
    {
        var remaining = unchecked((uint)value);
        while (remaining >= 128) { w.Write((byte)(remaining | 128)); remaining >>= 7; }
        w.Write((byte)remaining);
    }
    private static byte[] Packet(ushort opcode, Action<BinaryWriter> write)
    {
        using var memory = new MemoryStream(); using var writer = new BinaryWriter(memory);
        writer.Write(opcode); write(writer); return memory.ToArray();
    }
}
