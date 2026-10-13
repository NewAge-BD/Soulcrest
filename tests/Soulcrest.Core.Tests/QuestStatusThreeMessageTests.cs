using Soulcrest.Core.Network;
using Xunit;
using Packet = Soulcrest.Core.Tests.NetworkProgressMessageTests.Packet;

namespace Soulcrest.Core.Tests;

/// <summary>Synthetic records only. Observed status 3 is retained as a raw byte without quest semantics.</summary>
public sealed class QuestStatusThreeMessageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedObservedStatusesPreserveThreeAndRequireTheEntireListIncludingRewards(bool rewards)
    {
        using var w = new Packet(0x8D09);
        w.V(3);
        Record(w, 700001, 2, rewards);
        Record(w, 700002, 3, rewards);
        Record(w, 700003, 4, rewards);
        var message = w.Bytes;
        var original = message.ToArray();

        var parsed = Assert.IsType<QuestProgressSnapshot>(ExplorationProgressMessage.TryParseQuests(message));

        Assert.Equal(new byte[] { 2, 3, 4 }, parsed.Quests.Select(q => q.Status));
        var rawThree = parsed.Quests[1];
        Assert.Equal((byte)3, rawThree.Status);
        Assert.Equal((byte)1, rawThree.Stage);
        Assert.Equal(new[] { 1, 0, 0, 0, 0, 0 }, rawThree.Counters);
        Assert.Equal(9, rawThree.Ordinal);
        Assert.Equal(original, message);
        NetworkProgressMessageTests.EveryCutRejected(message, b => ExplorationProgressMessage.TryParseQuests(b));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(127)]
    [InlineData(255)]
    public void AnUnobservedStatusStillRejectsTheWholeMixedList(int status)
    {
        using var w = new Packet(0x8D09);
        w.V(2);
        Record(w, 700001, 3);
        Record(w, 700002, (byte)status);
        Assert.Null(ExplorationProgressMessage.TryParseQuests(w.Bytes));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(128)]
    [InlineData(65536)]
    public void StatusThreeDoesNotRelaxTheUnrecoveredOptionalScalar(int optional)
    {
        using var w = new Packet(0x8D09);
        w.V(1);
        Record(w, 700001, 3, optional: (uint)optional);
        Assert.Null(ExplorationProgressMessage.TryParseQuests(w.Bytes));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(128)]
    [InlineData(255)]
    public void StatusThreeDoesNotRelaxTheUnrecoveredRewardByte(int rewardByte)
    {
        using var w = new Packet(0x8D09);
        w.V(1);
        Record(w, 700001, 3, rewards: true, rewardByte: (byte)rewardByte);
        Assert.Null(ExplorationProgressMessage.TryParseQuests(w.Bytes));
    }

    [Fact]
    public void StatusThreeStillRejectsMalformedStageCountersDuplicateKeysAndForeignOpcodes()
    {
        using var w = new Packet(0x8D09);
        w.V(1);
        Record(w, 700001, 3, rewards: true);
        var stage = w.Bytes;
        stage[8] = 128;
        Assert.Null(ExplorationProgressMessage.TryParseQuests(stage));
        var overlong = w.Bytes;
        overlong[9] = 0x80; overlong[10] = 0;
        Assert.Null(ExplorationProgressMessage.TryParseQuests(overlong));
        var overflow = w.Bytes;
        overflow.AsSpan(9, 5).Fill(0xFF); overflow[13] = 0x10;
        Assert.Null(ExplorationProgressMessage.TryParseQuests(overflow));
        var foreign = w.Bytes;
        foreign[0] = 0x0B;
        Assert.Null(ExplorationProgressMessage.TryParseQuests(foreign));
        using var duplicate = new Packet(0x8D09);
        duplicate.V(2);
        Record(duplicate, 700001, 3);
        Record(duplicate, 700001, 4);
        Assert.Null(ExplorationProgressMessage.TryParseQuests(duplicate.Bytes));
    }

    private static void Record(Packet w, int id, byte status, bool rewards = false,
        uint optional = 0, byte rewardByte = 0)
    {
        w.I(id); w.B(status); w.B(1);
        w.V(1); w.Z(5);
        w.L(9); w.V(optional); w.V(rewards ? 1u : 0u);
        if (rewards) { w.I(700004); w.V(80000); w.B(rewardByte); w.L(50000000); }
    }
}
