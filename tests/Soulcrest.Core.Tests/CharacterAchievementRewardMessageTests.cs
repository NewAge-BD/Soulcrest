using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class CharacterAchievementRewardMessageTests
{
    [Theory]
    [InlineData(320101000)]
    [InlineData(320101100)]
    [InlineData(420101000)]
    [InlineData(420101100)]
    public void ExactSuccessVariantPreservesTheRawIdAndRewardedStepOnly(int id)
    {
        var parsed = Assert.IsType<CharacterAchievementRewardUpdate>(CharacterCombatMessage.TryParseAchievementReward(Message(id, 1)));
        Assert.Equal(id, parsed.AchievementId); Assert.Equal(1, parsed.RewardedStep);
        Assert.Equal(new CharacterAchievementRewardUpdate(id, byte.MaxValue),
            CharacterCombatMessage.TryParseAchievementReward(Message(id, byte.MaxValue)));
    }

    [Fact]
    public void EveryTruncationTrailerAndAnUnsupportedOpcodeOrResultIsRejected()
    {
        var message = Message(123, 1);
        for (var length = 0; length < message.Length; length++)
            Assert.Null(CharacterCombatMessage.TryParseAchievementReward(message[..length]));
        Assert.Null(CharacterCombatMessage.TryParseAchievementReward([.. message, 0]));
        Assert.Null(CharacterCombatMessage.TryParseAchievementReward([.. message, 0, 0, 0]));
        var wrongOpcode = (byte[])message.Clone(); wrongOpcode[0] = 0x84;
        Assert.Null(CharacterCombatMessage.TryParseAchievementReward(wrongOpcode));
        foreach (var result in new ushort[] { 1, 256, ushort.MaxValue })
            Assert.Null(CharacterCombatMessage.TryParseAchievementReward(Message(123, 1, result)));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(int.MinValue, 1)]
    [InlineData(123, 0)]
    public void NonpositiveIdentifiersOrZeroStepsCannotProduceAcknowledgementAuthority(int id, byte step) =>
        Assert.Null(CharacterCombatMessage.TryParseAchievementReward(Message(id, step)));

    private static byte[] Message(int id, byte step, ushort result = 0)
    {
        using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes);
        writer.Write((ushort)0x5686); writer.Write(result); writer.Write(id); writer.Write(step);
        return bytes.ToArray();
    }
}
