using System.Text;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.Core.Tests;

/// <summary>Synthetic protocol fixtures only: no captured payloads or player names.</summary>
public sealed class NetworkProgressMessageTests
{
    [Fact]
    public void SnapshotUsesInnerPetIdsAndUnspentBalancesAndConsumesBoardBooleans()
    {
        var message = PetSnapshot();
        var snapshot = Assert.IsType<PetProgressSnapshot>(PetProgressMessage.TryParseSnapshot(message));
        Assert.Equal(2, snapshot.Levels[1001]);
        Assert.Equal(7, snapshot.AvailableSouls[1001]);
        Assert.Equal(5, snapshot.AvailableSouls[1002]);
        Assert.False(snapshot.Levels.ContainsKey(1002));
        Assert.False(snapshot.Levels.ContainsKey(70001)); // acquired-map storage key
        EveryCutRejected(message, d => PetProgressMessage.TryParseSnapshot(d));
    }

    [Fact]
    public void EmptySnapshotIsAValidBaselineButMissingTailIsNot()
    {
        using var w = new Packet(0x9000);
        w.I(0); w.V(0); w.V(0); w.V(0); w.V(0); w.V(0); w.B(0);
        var snapshot = Assert.IsType<PetProgressSnapshot>(PetProgressMessage.TryParseSnapshot(w.Bytes));
        Assert.Empty(snapshot.Levels);
        Assert.Empty(snapshot.AvailableSouls);
        Assert.Null(PetProgressMessage.TryParseSnapshot(w.Bytes[..^1]));
    }

    [Fact]
    public void SnapshotRejectsDuplicateInnerIdsNegativeBalanceAndInvalidLevels()
    {
        using var w = new Packet(0x9000);
        w.I(0); w.V(2);
        w.I(1); w.I(1001); w.I(1);
        w.I(2); w.I(1001); w.I(2);
        w.V(0); w.V(0); w.V(0); w.V(0); w.B(0);
        Assert.Null(PetProgressMessage.TryParseSnapshot(w.Bytes));
        foreach (var (level, balance) in new[] { (4, 0), (-1, 0), (1, -1) })
        {
            using var invalid = new Packet(0x9000);
            invalid.I(0); invalid.V(1); invalid.I(1); invalid.I(1001); invalid.I(level);
            invalid.V(1); invalid.I(1001); invalid.I(balance);
            invalid.V(0); invalid.V(0); invalid.V(0); invalid.B(0);
            Assert.Null(PetProgressMessage.TryParseSnapshot(invalid.Bytes));
        }
    }

    [Fact]
    public void AcquisitionIncludesEntityAndRequiresTheCompleteBoardTail()
    {
        using var w = new Packet(0x9009);
        w.V(300); w.I(1001); w.I(1); w.B(2); w.I(4); w.L(500);
        Assert.Equal(new PetAcquisition(300, 1001, 1), PetProgressMessage.TryParseAcquired(w.Bytes));
        EveryCutRejected(w.Bytes, d => PetProgressMessage.TryParseAcquired(d));
        Assert.Null(PetProgressMessage.TryParseSnapshot(w.Bytes));
    }

    [Fact]
    public void LootAndSpendingReadMultibyteCountsWithoutDiscardingRepeatedListEntries()
    {
        using var loot = new Packet(0x900D);
        loot.V(128);
        for (var i = 0; i < 128; i++) { loot.I(1001); loot.I(200); }
        var gains = Assert.IsAssignableFrom<IReadOnlyList<SoulGain>>(LootMessage.TryParse(loot.Bytes));
        Assert.Equal(128, gains.Count);
        Assert.Equal(25600, gains.Sum(g => g.Souls));
        EveryCutRejected(loot.Bytes, d => LootMessage.TryParse(d));
        using var spent = new Packet(0x900E);
        spent.V(1); spent.I(1001); spent.I(5);
        Assert.Equal([new SoulGain(1001, 5)], PetProgressMessage.TryParseSpent(spent.Bytes));
        Assert.Null(LootMessage.TryParse(spent.Bytes));
        EveryCutRejected(spent.Bytes, d => PetProgressMessage.TryParseSpent(d));
    }

    [Fact]
    public void PointsRejectOverflowOverlongCountsInvalidAmountsAndTrailingBytes()
    {
        foreach (var count in new[] { new byte[] { 0x80, 0 }, [0xff, 0xff, 0xff, 0xff, 0x10], [0x80, 0x80, 0x80, 0x80, 0x80] })
            Assert.Null(LootMessage.TryParse([0x0d, 0x90, .. count]));
        foreach (var amount in new[] { -1, 0 })
        {
            using var w = new Packet(0x900D);
            w.V(1); w.I(1001); w.I(amount);
            Assert.Null(LootMessage.TryParse(w.Bytes));
        }
    }

    [Fact]
    public void TeleportSnapshotIsStrictAndRejectsDuplicateIds()
    {
        using var w = new Packet(0x9232);
        w.V(128);
        for (var i = 0; i < 128; i++) w.I(1052000 + i);
        var snapshot = Assert.IsType<TeleportProgressSnapshot>(ExplorationProgressMessage.TryParseTeleports(w.Bytes));
        Assert.Equal(128, snapshot.TeleportIds.Count);
        EveryCutRejected(w.Bytes, d => ExplorationProgressMessage.TryParseTeleports(d));
        using var duplicate = new Packet(0x9232);
        duplicate.V(2); duplicate.I(1052001); duplicate.I(1052001);
        Assert.Null(ExplorationProgressMessage.TryParseTeleports(duplicate.Bytes));
        using var empty = new Packet(0x9232);
        empty.V(0);
        Assert.Empty(Assert.IsType<TeleportProgressSnapshot>(ExplorationProgressMessage.TryParseTeleports(empty.Bytes)).TeleportIds);
    }

    [Fact]
    public void QuestCandidatePreservesRawStatusesAndSixVariableCounters()
    {
        var message = Quests();
        var snapshot = Assert.IsType<QuestProgressSnapshot>(ExplorationProgressMessage.TryParseQuests(message));
        Assert.Equal(2, snapshot.Quests.Count);
        Assert.Equal((byte)2, snapshot.Quests[0].Status);
        Assert.Equal((byte)4, snapshot.Quests[1].Status);
        Assert.Equal([820, 580, 30, 1220, 300, 0], snapshot.Quests[0].Counters);
        Assert.Equal(9, snapshot.Quests[0].Ordinal);
        EveryCutRejected(message, d => ExplorationProgressMessage.TryParseQuests(d));
    }

    [Fact]
    public void QuestCandidateRejectsUnseenVariantsInsteadOfProducingPartialProgress()
    {
        var message = Quests();
        foreach (var (offset, value) in new[] { (7, (byte)6), (8, (byte)128) })
        {
            var changed = message.ToArray(); changed[offset] = value;
            Assert.Null(ExplorationProgressMessage.TryParseQuests(changed));
        }
        using var nonemptyUnknown = new Packet(0x8D09);
        nonemptyUnknown.V(1); nonemptyUnknown.I(1810003); nonemptyUnknown.B(4); nonemptyUnknown.B(1);
        for (var i = 0; i < 6; i++) nonemptyUnknown.V(0);
        nonemptyUnknown.L(1); nonemptyUnknown.V(1); nonemptyUnknown.V(0);
        Assert.Null(ExplorationProgressMessage.TryParseQuests(nonemptyUnknown.Bytes));
        using var duplicate = new Packet(0x8D09);
        duplicate.V(2);
        for (var i = 0; i < 2; i++) Quest(duplicate, 1810003, 4, [1, 0, 0, 0, 0, 0], false);
        Assert.Null(ExplorationProgressMessage.TryParseQuests(duplicate.Bytes));
    }

    [Fact]
    public void VarintsEnforceWidthOverflowAndTwosComplementSignedValues()
    {
        using var w = new Packet(0);
        w.V(0); w.V(127); w.V(128); w.V(uint.MaxValue);
        var r = new ProgressPacketReader(w.Bytes.AsSpan(2));
        Assert.Equal(0u, r.V32()); Assert.Equal(127u, r.V32()); Assert.Equal(128u, r.V32()); Assert.Equal(-1, r.Vi32());
        r.Exact();
        Assert.True(VarintRejected([0xff, 0xff, 0xff, 0xff, 0x10]));
        Assert.True(VarintRejected([0x80, 0]));
        Assert.True(VarintRejected([0x80]));
    }

    private static bool VarintRejected(byte[] bytes)
    {
        try { var r = new ProgressPacketReader(bytes); r.V32(); return false; }
        catch (InvalidDataException) { return true; }
    }

    internal static byte[] PetSnapshot()
    {
        using var w = new Packet(0x9000);
        w.I(1001); w.V(1); w.I(70001); w.I(1001); w.I(2);
        w.V(2); w.I(1001); w.I(7); w.I(1002); w.I(5);
        w.V(1); w.B(2); w.I(3);
        w.V(1); w.I(40);
        w.V(2);
        for (var board = 0; board < 2; board++)
        {
            w.B((byte)board); w.B(2); w.I(3); w.L(750);
            w.V(1); w.B(1); w.V(5);
            for (var slot = 0; slot < 5; slot++)
            {
                w.B((byte)slot); w.B(1); w.U(100); w.L(7);
                w.Bool(slot % 2 == 0); w.Bool(slot % 3 == 0);
            }
        }
        w.B(1);
        return w.Bytes;
    }

    private static byte[] Quests()
    {
        using var w = new Packet(0x8D09);
        w.V(2);
        Quest(w, 2702010, 2, [820, 580, 30, 1220, 300, 0], false);
        Quest(w, 1810003, 4, [1, 0, 0, 0, 0, 0], true);
        return w.Bytes;
    }

    private static void Quest(Packet w, int id, byte status, int[] counters, bool reward)
    {
        w.I(id); w.B(status); w.B(1);
        foreach (var n in counters) w.V((uint)n);
        w.L(9); w.V(0); w.V(reward ? 1u : 0u);
        if (reward) { w.I(210140097); w.V(80000); w.B(0); w.L(50000000); }
    }

    internal static void EveryCutRejected<T>(byte[] message, Func<byte[], T?> parse) where T : class
    {
        for (var length = 0; length < message.Length; length++) Assert.Null(parse(message[..length]));
        Assert.Null(parse([.. message, 0]));
        Assert.Null(parse([.. message, 1, 2, 3]));
    }

    internal sealed class Packet : IDisposable
    {
        private readonly MemoryStream _stream = new();
        private readonly BinaryWriter _writer;
        private int _boolBit = 8;
        private long _boolAt;
        public Packet(ushort opcode) { _writer = new(_stream, Encoding.UTF8, leaveOpen: true); U(opcode); }
        public byte[] Bytes => _stream.ToArray();
        public void B(byte n) => _writer.Write(n);
        public void U(ushort n) => _writer.Write(n);
        public void I(int n) => _writer.Write(n);
        public void L(long n) => _writer.Write(n);
        public void V(uint n)
        {
            do { var b = (byte)(n & 127); n >>= 7; B(n == 0 ? b : (byte)(b | 128)); } while (n > 0);
        }
        public void Z(int count) { for (var i = 0; i < count; i++) B(0); }
        public void S(string value) { var b = Encoding.UTF8.GetBytes(value); V((uint)b.Length); _writer.Write(b); }
        public void Bool(bool value)
        {
            if (_boolBit == 8) { _boolAt = _stream.Position; B(0); _boolBit = 0; }
            if (value) _stream.GetBuffer()[_boolAt] |= (byte)(1 << _boolBit);
            _boolBit++;
        }
        public void Dispose() { _writer.Dispose(); _stream.Dispose(); }
    }
}
