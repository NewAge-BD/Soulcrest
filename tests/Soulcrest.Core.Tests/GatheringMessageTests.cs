using System.Text;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class GatheringMessageTests
{
    [Theory]
    [InlineData(0, 58)]
    [InlineData(1, 58)]
    [InlineData(2, 58)]
    [InlineData(255, 255)]
    public void OwnershipWitnessReadsBothPositiveKeysAndPreservesOpaqueBytes(byte opaqueA, byte opaqueB)
    {
        var bytes = Witness(300, 400, opaqueA, opaqueB);
        var witness = Assert.IsType<GatheringOwnershipWitness>(GatheringMessage.TryParseOwnershipWitness(bytes));
        Assert.Equal(new GatheringOwnershipWitness(300, opaqueA, opaqueB, 400), witness);
        EveryCutRejected(bytes, value => GatheringMessage.TryParseOwnershipWitness(value));
        Assert.Null(GatheringMessage.TryParseOwnershipWitness([.. bytes, 0]));
        Assert.Null(GatheringMessage.TryParseOwnershipWitness([0x33, 0x8D, .. bytes[2..]]));
        Assert.Null(GatheringMessage.TryParseCompletion(bytes));
    }

    [Fact]
    public void OwnershipWitnessAcceptsTheSignedPositiveKeyBoundaryButRejectsZeroOrNegativeKeys()
    {
        Assert.Equal(new GatheringOwnershipWitness(int.MaxValue, 0, 58, int.MaxValue),
            GatheringMessage.TryParseOwnershipWitness(Witness(int.MaxValue, int.MaxValue)));
        Assert.Null(GatheringMessage.TryParseOwnershipWitness(Witness(0, 400)));
        Assert.Null(GatheringMessage.TryParseOwnershipWitness(Witness(300, 0)));
        Assert.Null(GatheringMessage.TryParseOwnershipWitness(Witness(uint.MaxValue, 400)));
        Assert.Null(GatheringMessage.TryParseOwnershipWitness(Witness(300, uint.MaxValue)));
    }

    [Fact]
    public void OwnershipWitnessRejectsNoncanonicalOverflowingAndUnterminatedVarintsInEitherKey()
    {
        byte[][] invalid = [[0x80, 0], [0xFF, 0xFF, 0xFF, 0xFF, 0x1F], [0x80, 0x80, 0x80, 0x80, 0x80]];
        foreach (var key in invalid)
        {
            Assert.Null(GatheringMessage.TryParseOwnershipWitness(
                Packet(0x8D34, writer => { writer.Write(key); writer.Write((byte)0); writer.Write((byte)58); V(writer, 400); })));
            Assert.Null(GatheringMessage.TryParseOwnershipWitness(
                Packet(0x8D34, writer => { V(writer, 300); writer.Write((byte)0); writer.Write((byte)58); writer.Write(key); })));
        }
    }

    [Theory]
    [InlineData(1, 0, 1)]
    [InlineData(1, 1, 2)]
    [InlineData(0, 1, 1)]
    [InlineData(int.MaxValue, 0, int.MaxValue)]
    public void PrimaryRewardChecksTheSumWithoutChangingTheObservedCounts(int basic, int bonus, int expected)
    {
        var observation = new GatheringCompletionObservation(0, 58, 400, 610660025, basic, bonus, 0, 0);
        var result = GatheringMessage.QualifyPrimaryReward(observation);
        Assert.Equal(GatheringRewardQualificationStatus.Qualified, result.Status);
        Assert.Equal(610660025, result.ItemId);
        Assert.Equal(expected, result.Quantity);
        Assert.Equal(basic, observation.BasicCount);
        Assert.Equal(bonus, observation.BonusCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(255)]
    public void PrimaryRewardQualificationDoesNotInterpretTheOpaqueOutcomeByte(byte result)
    {
        var observation = new GatheringCompletionObservation(result, 58, 400, 610660025, 1, 0, 0, 0);
        Assert.Equal(new GatheringRewardQualification(GatheringRewardQualificationStatus.Qualified, 610660025, 1),
            GatheringMessage.QualifyPrimaryReward(observation));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(610660025)]
    public void ZeroRewardFieldsIncludingTheObservedFailedAttemptNeverCreateAGain(int item)
    {
        var observation = new GatheringCompletionObservation(2, 58, 400, item, 0, 0, 0, 0);
        AssertNoGain(GatheringRewardQualificationStatus.NoReward, observation);
    }

    [Theory]
    [InlineData(-1, 0, 610660025)]
    [InlineData(1, -1, 610660025)]
    [InlineData(int.MaxValue, 1, 610660025)]
    [InlineData(int.MaxValue, int.MaxValue, 610660025)]
    [InlineData(1, 0, 0)]
    [InlineData(1, 0, -1)]
    [InlineData(0, 0, -1)]
    public void NegativeOverflowingOrUnidentifiedPrimaryRewardsAreRejected(int basic, int bonus, int item)
    {
        var observation = new GatheringCompletionObservation(0, 58, 400, item, basic, bonus, 0, 0);
        AssertNoGain(GatheringRewardQualificationStatus.Invalid, observation);
    }

    [Theory]
    [InlineData(510116011, 1)]
    [InlineData(510116011, 0)]
    [InlineData(0, 1)]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    public void AnyAdditionalRewardFieldIsExplicitlyUnsupportedInsteadOfSilentlyDroppingExtras(int additional, int count)
    {
        var observation = new GatheringCompletionObservation(0, 58, 400, 610660025, 1, 1, additional, count);
        AssertNoGain(GatheringRewardQualificationStatus.UnsupportedAdditional, observation);
        AssertNoGain(GatheringRewardQualificationStatus.UnsupportedAdditional, observation with { BasicCount = 0, BonusCount = 0 });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(255)]
    public void CompletionReadsOpaqueOutcomeBytesWithoutClaimingSuccessOrAddingCandidateAmounts(byte result)
    {
        var bytes = Completion(result, 58, 610660025, 1, 1, 610610014, 2);
        var observation = Assert.IsType<GatheringCompletionObservation>(GatheringMessage.TryParseCompletion(bytes));
        Assert.Equal(result, observation.ResultCandidate);
        Assert.Equal(58, observation.Unknown);
        Assert.Equal(300, observation.TargetKey);
        Assert.Equal(610660025, observation.RewardItemId);
        Assert.Equal(1, observation.BasicCount);
        Assert.Equal(1, observation.BonusCount);
        Assert.Equal(610610014, observation.AdditionalItemId);
        Assert.Equal(2, observation.AdditionalCount);
        EveryCutRejected(bytes, value => GatheringMessage.TryParseCompletion(value));
        Assert.Null(GatheringMessage.TryParseCompletion([.. bytes, 0]));
    }

    [Fact]
    public void NegativeAndLargeRawAmountsRemainObservationsAndCannotBecomeAnImplicitReward()
    {
        var observation = Assert.IsType<GatheringCompletionObservation>(GatheringMessage.TryParseCompletion(
            Completion(1, 58, 610660025, int.MaxValue, int.MaxValue, 610610014, -1)));
        Assert.Equal(int.MaxValue, observation.BasicCount);
        Assert.Equal(int.MaxValue, observation.BonusCount);
        Assert.Equal(-1, observation.AdditionalCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ObjectReaderConsumesOptionalFieldsButReturnsNoPositionOrStrings(bool optional)
    {
        var bytes = Object(optional);
        var observation = Assert.IsType<GatheringObjectObservation>(GatheringMessage.TryParseObject(bytes));
        Assert.Equal(300, observation.EntityKey);
        Assert.Equal(1550020, observation.DataId);
        Assert.Equal(0, observation.OriginSpawnKey);
        int? expectedUnique = optional ? -2135533395 : null;
        Assert.Equal(expectedUnique, observation.SpawnUniqueKey);
        EveryCutRejected(bytes, value => GatheringMessage.TryParseObject(value));
        Assert.Null(GatheringMessage.TryParseObject([.. bytes, 0]));
    }

    [Fact]
    public void UnknownObjectOrDescriptorFlagsFailClosed()
    {
        var objectFlags = Object(false);
        objectFlags[4] = 16;
        Assert.Null(GatheringMessage.TryParseObject(objectFlags));
        var descriptorFlags = Object(false);
        descriptorFlags[5] = 32;
        Assert.Null(GatheringMessage.TryParseObject(descriptorFlags));
    }

    [Fact]
    public void RemovalReadsTheEntireMessageAndIsNotAGatheringSuccess()
    {
        var bytes = Packet(0x3635, writer => { V(writer, 300); writer.Write((byte)4); writer.Write((byte)3); V(writer, 0); });
        Assert.Equal(new GatheringObjectRemoval(300, 4, 3, 0), GatheringMessage.TryParseRemoval(bytes));
        EveryCutRejected(bytes, value => GatheringMessage.TryParseRemoval(value));
        Assert.Null(GatheringMessage.TryParseRemoval([.. bytes, 0]));
        Assert.Null(GatheringMessage.TryParseCompletion(bytes));
    }

    [Fact]
    public void NoncanonicalOrOverflowingTargetVarintsAreRejected()
    {
        var regular = Completion(0, 58, 1, 1, 0, 0, 0);
        var tail = regular[6..]; // skip opcode, opaque bytes and canonical two-byte target
        Assert.Null(GatheringMessage.TryParseCompletion([0x33, 0x8D, 0, 58, 0x80, 0, .. tail]));
        Assert.Null(GatheringMessage.TryParseCompletion([0x33, 0x8D, 0, 58, 0xFF, 0xFF, 0xFF, 0xFF, 0x1F, .. tail]));
    }

    private static byte[] Completion(byte result, byte unknown, int item, int basic, int bonus, int additional, int additionalCount) =>
        Packet(0x8D33, writer =>
        {
            writer.Write(result); writer.Write(unknown); V(writer, 300);
            writer.Write(item); writer.Write(basic); writer.Write(bonus); writer.Write(additional); writer.Write(additionalCount);
        });

    private static byte[] Witness(uint actor, uint target, byte opaqueA = 0, byte opaqueB = 58) =>
        Packet(0x8D34, writer => { V(writer, actor); writer.Write(opaqueA); writer.Write(opaqueB); V(writer, target); });

    private static void AssertNoGain(GatheringRewardQualificationStatus status, GatheringCompletionObservation observation)
    {
        var result = GatheringMessage.QualifyPrimaryReward(observation);
        Assert.Equal(status, result.Status);
        Assert.Null(result.ItemId);
        Assert.Null(result.Quantity);
    }

    private static byte[] Object(bool optional) => Packet(0x3634, writer =>
    {
        V(writer, 300); writer.Write((byte)(optional ? 15 : 0)); writer.Write((byte)(optional ? 31 : 0));
        if (optional)
        {
            var name = Encoding.UTF8.GetBytes("Synthetic object"); V(writer, (uint)name.Length); writer.Write(name);
        }
        writer.Write(1550020);
        if (optional) writer.Write(new byte[8]);
        writer.Write(new byte[12]); writer.Write(0); writer.Write(0f); writer.Write((byte)0); writer.Write((byte)1);
        writer.Write((byte)0); writer.Write(0L); writer.Write(0);
        if (optional) writer.Write(123L);
        writer.Write(0);
        if (optional) { writer.Write(456L); writer.Write(-2135533395); }
        writer.Write((byte)0); writer.Write((byte)0);
        if (optional) writer.Write(789);
        V(writer, 0);
    });

    private static byte[] Packet(ushort opcode, Action<BinaryWriter> write)
    {
        using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes);
        writer.Write(opcode); write(writer); return bytes.ToArray();
    }

    private static void V(BinaryWriter writer, uint value)
    {
        while (value >= 128) { writer.Write((byte)(value | 128)); value >>= 7; }
        writer.Write((byte)value);
    }

    private static void EveryCutRejected<T>(byte[] bytes, Func<byte[], T?> parse) where T : class
    {
        for (var length = 0; length < bytes.Length; length++) Assert.Null(parse(bytes[..length]));
    }
}
