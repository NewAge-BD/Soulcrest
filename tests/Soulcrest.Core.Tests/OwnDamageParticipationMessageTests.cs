using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class OwnDamageParticipationMessageTests
{
    [Theory]
    [InlineData(0x0006, 2, 1_701_000_011U)]
    [InlineData(0x0006, 3, 1_701_000_011U)]
    [InlineData(0x0026, 2, 1_773_000_111U)]
    [InlineData(0x0026, 3, 1_773_000_111U)]
    [InlineData(0x0004, 2, 1_704_000_011U)]
    [InlineData(0x0024, 3, 1_735_345_011U)]
    [InlineData(0x0046, 2, 1_705_034_011U)]
    [InlineData(0x0066, 3, 1_703_004_011U)]
    [InlineData(0x1044, 2, 1_719_004_011U)]
    [InlineData(0x1046, 3, 300_001_911U)]
    [InlineData(0x1066, 2, 1_708_023_011U)]
    public void CompleteComparedOwnPositiveFormsDescribeParticipationOnly(int fields, int kind, uint effect)
    {
        var message = Message(fields: (ushort)fields, kind: (byte)kind, effect: effect);
        var result = Assert.IsType<OwnDamageParticipationObservation>(OwnDamageParticipationMessage.TryParse(message, 100));
        Assert.Equal(100, result.CasterEntityKey);
        Assert.Equal(200, result.VictimEntityKey);
        Assert.True(result.HasPositiveDamage);
        Assert.Equal(nameof(OwnDamageParticipationObservation), result.ToString());
        NetworkProgressMessageTests.EveryCutRejected(message, data => OwnDamageParticipationMessage.TryParse(data, 100));
        Assert.Null(OwnDamageParticipationMessage.TryParse([.. message, 0], 100));
    }

    [Theory]
    [InlineData(300_001_911U)] [InlineData(1_701_000_011U)] [InlineData(1_702_000_011U)]
    [InlineData(1_703_004_011U)] [InlineData(1_704_000_011U)] [InlineData(1_705_034_011U)]
    [InlineData(1_705_034_012U)] [InlineData(1_706_000_012U)] [InlineData(1_706_001_311U)]
    [InlineData(1_706_001_312U)] [InlineData(1_707_034_011U)] [InlineData(1_707_034_012U)]
    [InlineData(1_708_023_011U)] [InlineData(1_708_023_012U)] [InlineData(1_719_004_011U)]
    [InlineData(1_730_004_011U)] [InlineData(1_730_004_013U)] [InlineData(1_730_004_014U)]
    [InlineData(1_735_345_011U)] [InlineData(1_735_345_012U)] [InlineData(1_735_345_013U)]
    [InlineData(1_773_000_111U)]
    public void IndividuallyPositiveComparedEffectsRemainExplicitLiterals(uint effect) =>
        Assert.NotNull(OwnDamageParticipationMessage.TryParse(Message(effect: effect), 100));

    [Theory]
    [InlineData(0, 100U, 200U)] [InlineData(-1, 100U, 200U)] [InlineData(101, 100U, 200U)]
    [InlineData(100, 101U, 200U)] [InlineData(100, 0U, 200U)] [InlineData(100, uint.MaxValue, 200U)]
    [InlineData(100, 100U, 0U)] [InlineData(100, 100U, uint.MaxValue)] [InlineData(100, 100U, 100U)]
    public void ForeignMissingOutOfRangeAndSelfTargetsCannotBecomeOwnParticipation(int own, uint caster, uint victim) =>
        Assert.Null(OwnDamageParticipationMessage.TryParse(Message(caster: caster, victim: victim), own));

    [Theory]
    [InlineData(0U)] [InlineData(1U)] [InlineData(1_780_000_111U)] [InlineData(uint.MaxValue)]
    [InlineData(1_710_004_011U)] [InlineData(1_710_004_012U)] [InlineData(1_712_004_011U)]
    [InlineData(1_716_002_711U)] [InlineData(1_716_003_711U)] [InlineData(1_716_004_611U)]
    [InlineData(1_701_000_010U)] [InlineData(1_701_000_012U)] [InlineData(1_735_345_014U)]
    public void PositiveHealingAndUnknownEffectsAreNeverPromotedToDamage(uint effect) =>
        Assert.Null(OwnDamageParticipationMessage.TryParse(Message(effect: effect), 100));

    [Theory]
    [InlineData(0UL)] [InlineData(9_223_372_036_854_775_808UL)] [InlineData(ulong.MaxValue)]
    public void ZeroAndValuesOutsideTheSignedWireContractAreNotPositiveDamage(ulong amount) =>
        Assert.Null(OwnDamageParticipationMessage.TryParse(Message(amount: amount), 100));

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(4)] [InlineData(255)]
    public void UncomparedResultKindsRemainUnsupported(int kind) =>
        Assert.Null(OwnDamageParticipationMessage.TryParse(Message(kind: (byte)kind), 100));

    [Theory]
    [InlineData(0)] [InlineData(2)] [InlineData(65535)]
    public void UncomparedLoopValuesRemainUnsupported(int loop) =>
        Assert.Null(OwnDamageParticipationMessage.TryParse(Message(loop: (ushort)loop), 100));

    [Theory]
    [InlineData(0x0005)] [InlineData(0x0007)] [InlineData(0x002E)] [InlineData(0x0044)]
    [InlineData(0x1006)] [InlineData(0x1064)] [InlineData(0x4006)] [InlineData(0x8006)]
    public void OtherOptionalFieldContractsAreNotReadAsTheComparedForm(int fields) =>
        Assert.Null(OwnDamageParticipationMessage.TryParse(Message(fields: (ushort)fields), 100));

    [Fact]
    public void UnknownPackedFlagAndPeriodicOpcodeDoNotBorrowTheDamageContract()
    {
        Assert.Null(OwnDamageParticipationMessage.TryParse(Message(packed: 0x80), 100));
        Assert.Null(OwnDamageParticipationMessage.TryParse(Message(opcode: 0x3805), 100));
        Assert.Null(OwnDamageParticipationMessage.TryParse(Message(opcode: 0x8D04), 100));
    }

    [Theory]
    [InlineData("8000")] [InlineData("8080808080808080808000")] [InlineData("FFFFFFFFFFFFFFFFFF02")]
    public void PrimaryAmountsRejectNoncanonicalUnterminatedAndOverflowVarints(string encoded) =>
        Assert.Null(OwnDamageParticipationMessage.TryParse(Message(primaryEncoding: Convert.FromHexString(encoded)), 100));

    [Theory]
    [InlineData("8000")] [InlineData("FFFFFFFF10")] [InlineData("8080808080")]
    public void ActorAndListCountVarintsMustBeCanonicalAndBounded(string encoded)
    {
        var bytes = Convert.FromHexString(encoded);
        Assert.Null(OwnDamageParticipationMessage.TryParse(Message(casterEncoding: bytes), 100));
        Assert.Null(OwnDamageParticipationMessage.TryParse(Message(victimEncoding: bytes), 100));
        Assert.Null(OwnDamageParticipationMessage.TryParse(Message(fields: 0x0026, countEncoding: bytes), 100));
    }

    [Fact]
    public void AuxiliaryListIsBoundedConsumedAndNeverSubstitutesForThePrimaryAmount()
    {
        Assert.NotNull(OwnDamageParticipationMessage.TryParse(Message(fields: 0x0026, auxiliaryCount: 64), 100));
        Assert.Null(OwnDamageParticipationMessage.TryParse(Message(fields: 0x0026, auxiliaryCount: 65), 100));
        Assert.Null(OwnDamageParticipationMessage.TryParse(Message(fields: 0x0026, auxiliaryCount: 64, amount: 0), 100));
        Assert.Null(OwnDamageParticipationMessage.TryParse(Message(fields: 0x0026, auxiliaryAmount: ulong.MaxValue), 100));
    }

    [Fact]
    public void AdditionalScalarListHasItsOwnCountBoundsAndCanonicalVarints()
    {
        Assert.NotNull(OwnDamageParticipationMessage.TryParse(Message(fields: 0x0046, scalarCount: 64), 100));
        Assert.Null(OwnDamageParticipationMessage.TryParse(Message(fields: 0x0046, scalarCount: 65), 100));
        Assert.Null(OwnDamageParticipationMessage.TryParse(Message(fields: 0x0046, scalarCountEncoding: [0x80, 0]), 100));
        Assert.Null(OwnDamageParticipationMessage.TryParse(Message(fields: 0x0046, scalarCountEncoding: [0xFF, 0xFF, 0xFF, 0xFF, 0x10]), 100));
    }

    [Fact]
    public void EighthBooleanRetainsTheOriginalPackedByteAcrossBothListsAndScalars()
    {
        var allBits = Message(fields: 0x1066, packed: 0xFF);
        Assert.NotNull(OwnDamageParticipationMessage.TryParse(allBits, 100));
        NetworkProgressMessageTests.EveryCutRejected(allBits, data => OwnDamageParticipationMessage.TryParse(data, 100));
        Assert.Null(OwnDamageParticipationMessage.TryParse([.. allBits, 0], 100));
        Assert.Null(OwnDamageParticipationMessage.TryParse(Message(fields: 0x0066, packed: 0x80), 100));
    }

    [Theory]
    [InlineData((byte)0, true)] [InlineData((byte)1, true)]
    [InlineData((byte)2, false)] [InlineData((byte)0x80, false)] [InlineData((byte)0xFF, false)]
    public void StandaloneOptionalBooleanDoesNotReadReservedPaddingAsMoreFields(byte value, bool accepted)
    {
        var message = Message(fields: 0x1044, standaloneBoolean: value);
        Assert.Equal(accepted, OwnDamageParticipationMessage.TryParse(message, 100) is not null);
    }

    private static byte[] Message(ushort fields = 0x0006, byte kind = 2, uint effect = 1_701_000_011,
        uint caster = 100, uint victim = 200, ulong amount = 17, ushort loop = 1, byte packed = 0,
        ushort opcode = 0x3804, int auxiliaryCount = 2, ulong auxiliaryAmount = 1,
        byte[]? primaryEncoding = null, byte[]? casterEncoding = null, byte[]? victimEncoding = null,
        byte[]? countEncoding = null, int scalarCount = 1, byte[]? scalarCountEncoding = null,
        byte standaloneBoolean = 0)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(opcode); Var(writer, victim, victimEncoding); writer.Write(fields); Var(writer, caster, casterEncoding);
        writer.Write(7U); writer.Write((byte)0); writer.Write(kind);
        if ((fields & 0x0002) != 0)
        {
            writer.Write(packed); Var(writer, 0); writer.Write((byte)0);
        }
        writer.Write(effect); writer.Write(0U); Var(writer, 0);
        Var(writer, amount, primaryEncoding);
        if ((fields & 0x0020) != 0)
        {
            Var(writer, (uint)auxiliaryCount, countEncoding);
            for (var index = 0; index < auxiliaryCount; index++) Var(writer, auxiliaryAmount);
        }
        if ((fields & 0x0040) != 0)
        {
            Var(writer, (uint)scalarCount, scalarCountEncoding);
            for (var index = 0; index < scalarCount; index++) writer.Write(17U);
        }
        writer.Write(loop);
        if ((fields & 0x1002) == 0x1000) writer.Write(standaloneBoolean);
        return stream.ToArray();
    }

    private static void Var(BinaryWriter writer, ulong value, byte[]? encoded = null)
    {
        if (encoded is not null) { writer.Write(encoded); return; }
        while (value >= 128) { writer.Write((byte)(value | 128)); value >>= 7; }
        writer.Write((byte)value);
    }
}
