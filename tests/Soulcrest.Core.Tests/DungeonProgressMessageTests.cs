using Soulcrest.Core.Network;
using Xunit;
using Packet = Soulcrest.Core.Tests.NetworkProgressMessageTests.Packet;

namespace Soulcrest.Core.Tests;

/// <summary>Synthetic packets only; the seven-reward fixture preserves an observed layout, not captured bytes.</summary>
public sealed class DungeonProgressMessageTests
{
    [Fact]
    public void StatePreservesAllFiveWireStatesAndTimesAndRejectsEveryTruncation()
    {
        foreach (var state in Enum.GetValues<DungeonRunState>())
        {
            var message = State(320027, (byte)state, 1000, 11000);
            Assert.Equal(23, message.Length); // opcode plus the exact 21-byte body
            Assert.Equal(new DungeonStateSnapshot(320027, state, 1000, 11000),
                DungeonProgressMessage.TryParseState(message));
            NetworkProgressMessageTests.EveryCutRejected(message, b => DungeonProgressMessage.TryParseState(b));
        }
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(-1, 0, 0, 0)]
    [InlineData(320027, 5, 0, 0)]
    [InlineData(320027, 255, 0, 0)]
    [InlineData(320027, 2, -1, 0)]
    [InlineData(320027, 2, 0, -1)]
    public void StateRejectsInvalidIdsUnknownStatesAndNegativeTimes(int dungeon, byte state, long start, long end) =>
        Assert.Null(DungeonProgressMessage.TryParseState(State(dungeon, state, start, end)));

    [Fact]
    public void StateAllowsAbsentTimeSentinelsWithoutInferringCompletion()
    {
        var parsed = Assert.IsType<DungeonStateSnapshot>(DungeonProgressMessage.TryParseState(State(320027, 2, 0, 0)));
        Assert.Equal(DungeonRunState.Process, parsed.State);
        Assert.Equal(0, parsed.StartTime);
        Assert.Equal(0, parsed.EndTime);
    }

    [Fact]
    public void SevenRewardCompletionStrictlyConsumesTheAnonymousObserved91ByteBodyShape()
    {
        var rewards = Enumerable.Range(1, 7).Select(i => (1000 + i, (long)i)).ToArray();
        var message = Completion(rewards: rewards);
        Assert.Equal(93, message.Length);
        var parsed = Assert.IsType<DungeonCompletion>(DungeonProgressMessage.TryParseCompletion(message));
        Assert.Equal(320027, parsed.DungeonId);
        Assert.True(parsed.Succeeded);
        Assert.Null(parsed.ContentsId);
        Assert.Null(parsed.ClearLevel);
        Assert.Equal(7, parsed.Rewards.Count);
        Assert.Equal(28, parsed.Rewards.Values.Sum());
        NetworkProgressMessageTests.EveryCutRejected(message, b => DungeonProgressMessage.TryParseCompletion(b));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void CompletionReadsOnlyFlaggedOptionalFieldsAfterThePackedBoolean(byte flags)
    {
        var message = Completion(flags: flags, contents: 300, level: 128, rewards: [(1001, 15L)]);
        var parsed = Assert.IsType<DungeonCompletion>(DungeonProgressMessage.TryParseCompletion(message));
        Assert.Equal((flags & 1) != 0 ? 300 : (int?)null, parsed.ContentsId);
        Assert.Equal((flags & 2) != 0 ? 128 : (int?)null, parsed.ClearLevel);
        Assert.Equal(15, parsed.Rewards[1001]);
        NetworkProgressMessageTests.EveryCutRejected(message, b => DungeonProgressMessage.TryParseCompletion(b));
    }

    [Theory]
    [InlineData(0x00, false)]
    [InlineData(0x01, true)]
    [InlineData(0x02, false)]
    [InlineData(0x03, true)]
    [InlineData(0xfe, false)]
    [InlineData(0xff, true)]
    public void SucceededUsesBitZeroOfItsOwnByteAndDoesNotConsumeTheFlags(byte booleanByte, bool expected)
    {
        var parsed = Assert.IsType<DungeonCompletion>(DungeonProgressMessage.TryParseCompletion(
            Completion(flags: 3, booleanByte: booleanByte, contents: 129, level: 4)));
        Assert.Equal(expected, parsed.Succeeded);
        Assert.Equal(129, parsed.ContentsId);
        Assert.Equal(4, parsed.ClearLevel);
    }

    [Fact]
    public void UnsuccessfulEmptyResultIsValidAndDoesNotBecomeACompletion()
    {
        var message = Completion(booleanByte: 0);
        var parsed = Assert.IsType<DungeonCompletion>(DungeonProgressMessage.TryParseCompletion(message));
        Assert.False(parsed.Succeeded);
        Assert.Empty(parsed.Rewards);
        NetworkProgressMessageTests.EveryCutRejected(message, b => DungeonProgressMessage.TryParseCompletion(b));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(128)]
    [InlineData(255)]
    public void CompletionRejectsUnknownOptionalFlags(byte flags)
    {
        var message = Completion();
        message[2] = flags;
        Assert.Null(DungeonProgressMessage.TryParseCompletion(message));
    }

    [Theory]
    [InlineData(0, 1001, 1)]
    [InlineData(-1, 1001, 1)]
    [InlineData(320027, 0, 1)]
    [InlineData(320027, -1, 1)]
    [InlineData(320027, 1001, -1)]
    public void CompletionRejectsInvalidDungeonOrRewardIdsAndNegativeAmounts(int dungeon, int item, long amount) =>
        Assert.Null(DungeonProgressMessage.TryParseCompletion(Completion(dungeon: dungeon, rewards: [(item, amount)])));

    [Theory]
    [InlineData(1, -1, 0)]
    [InlineData(2, 0, -1)]
    [InlineData(3, -1, -1)]
    public void CompletionRejectsNegativeOptionalValues(byte flags, int contents, int level) =>
        Assert.Null(DungeonProgressMessage.TryParseCompletion(Completion(flags: flags, contents: contents, level: level)));

    [Fact]
    public void OptionalZeroSentinelsAndZeroOrLarge64BitRewardAmountsAreRetained()
    {
        var parsed = Assert.IsType<DungeonCompletion>(DungeonProgressMessage.TryParseCompletion(
            Completion(flags: 3, rewards: [(1001, 0L), (1002, long.MaxValue)])));
        Assert.Equal(0, parsed.ContentsId);
        Assert.Equal(0, parsed.ClearLevel);
        Assert.Equal(0, parsed.Rewards[1001]);
        Assert.Equal(long.MaxValue, parsed.Rewards[1002]);
    }

    [Fact]
    public void DuplicateRewardKeysRejectThePacketInsteadOfSilentlyLosingAnAmount() =>
        Assert.Null(DungeonProgressMessage.TryParseCompletion(Completion(rewards: [(1001, 1L), (1001, 2L)])));

    [Fact]
    public void RewardMapIsDetachedAndReadOnly()
    {
        var message = Completion(rewards: [(1001, 3L)]);
        var parsed = Assert.IsType<DungeonCompletion>(DungeonProgressMessage.TryParseCompletion(message));
        Array.Clear(message);
        Assert.Equal(3, parsed.Rewards[1001]);
        var dictionary = Assert.IsAssignableFrom<IDictionary<int, long>>(parsed.Rewards);
        Assert.Throws<NotSupportedException>(() => dictionary[1001] = 12);
    }

    [Fact]
    public void MultibyteRewardCountDoesNotShiftTheFirstEntry()
    {
        var rewards = Enumerable.Range(1, 128).Select(i => (1000 + i, (long)i)).ToArray();
        var message = Completion(rewards: rewards);
        var parsed = Assert.IsType<DungeonCompletion>(DungeonProgressMessage.TryParseCompletion(message));
        Assert.Equal(128, parsed.Rewards.Count);
        Assert.Equal(128, parsed.Rewards[1128]);
        NetworkProgressMessageTests.EveryCutRejected(message, b => DungeonProgressMessage.TryParseCompletion(b));
    }

    [Fact]
    public void RewardCountsAreCanonicalBoundedAndCannotExceedRemainingBytes()
    {
        foreach (byte[] count in new byte[][] { [1], [0x80, 0], [0xff, 0xff, 0xff, 0xff, 0x10], [0x80, 0x80, 0x80, 0x80, 0x80] })
        {
            var empty = Completion();
            Assert.Null(DungeonProgressMessage.TryParseCompletion([.. empty[..^1], .. count]));
        }
        using var large = new Packet(0x6101);
        large.B(0); large.I(320027); large.Bool(true); large.V(10001);
        for (var i = 1; i <= 10001; i++) { large.I(i); large.L(1); }
        Assert.Null(DungeonProgressMessage.TryParseCompletion(large.Bytes));
    }

    [Fact]
    public void EachDecoderRequiresItsOwnOpcode()
    {
        Assert.Null(DungeonProgressMessage.TryParseCompletion(State(320027, 3, 0, 0)));
        Assert.Null(DungeonProgressMessage.TryParseState(Completion()));
        var wrong = Completion(); wrong[0] = 2;
        Assert.Null(DungeonProgressMessage.TryParseCompletion(wrong));
    }

    private static byte[] State(int dungeon, byte state, long start, long end)
    {
        using var w = new Packet(0x6100);
        w.I(dungeon); w.B(state); w.L(start); w.L(end);
        return w.Bytes;
    }

    private static byte[] Completion(int dungeon = 320027, byte flags = 0, byte booleanByte = 1,
        int contents = 0, int level = 0, (int Item, long Amount)[]? rewards = null)
    {
        using var w = new Packet(0x6101);
        w.B(flags); w.I(dungeon); w.B(booleanByte);
        if ((flags & 1) != 0) w.V(unchecked((uint)contents));
        rewards ??= [];
        w.V((uint)rewards.Length);
        foreach (var (item, amount) in rewards) { w.I(item); w.L(amount); }
        if ((flags & 2) != 0) w.V(unchecked((uint)level));
        return w.Bytes;
    }
}
