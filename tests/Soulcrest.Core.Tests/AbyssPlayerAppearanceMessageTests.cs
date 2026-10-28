using System.Buffers.Binary;
using System.Text;
using Soulcrest.Core.Network;
using Xunit;
using Packet = Soulcrest.Core.Tests.NetworkProgressMessageTests.Packet;

namespace Soulcrest.Core.Tests;

public sealed class AbyssPlayerAppearanceMessageTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CompletePcFormReadsMetadataOnlyAfterEverySubformAndTailIsValid(bool optional)
    {
        var fixture = Message(optional);
        var player = Assert.IsType<AbyssPlayerAppearanceObservation>(AbyssPlayerAppearanceMessage.TryParse(fixture.Bytes));
        Assert.Equal(300, player.EntityKey);
        Assert.Equal((ushort)501, player.ServerId);
        Assert.Equal("SyntheticPlayer", player.Name);
        Assert.Equal("Cleric", player.ClassId);
        Assert.Equal(19, player.AbyssRankGrade);
        Assert.Equal(nameof(AbyssPlayerAppearanceObservation), player.ToString());
        NetworkProgressMessageTests.EveryCutRejected(fixture.Bytes, data => AbyssPlayerAppearanceMessage.TryParse(data));
        Assert.Null(AbyssPlayerAppearanceMessage.TryParse([.. fixture.Bytes, 0]));
    }

    [Theory]
    [InlineData(5, "Gladiator")] [InlineData(8, "Gladiator")]
    [InlineData(9, "Templar")] [InlineData(12, "Templar")]
    [InlineData(13, "Ranger")] [InlineData(16, "Ranger")]
    [InlineData(17, "Assassin")] [InlineData(20, "Assassin")]
    [InlineData(21, "Elementalist")] [InlineData(24, "Elementalist")]
    [InlineData(25, "Sorcerer")] [InlineData(28, "Sorcerer")]
    [InlineData(29, "Cleric")] [InlineData(32, "Cleric")]
    [InlineData(33, "Chanter")] [InlineData(36, "Chanter")]
    public void StaticTemplateUsesTheSameClassKeysAsTheFullOwnForm(int template, string expected)
    {
        var player = Assert.IsType<AbyssPlayerAppearanceObservation>(AbyssPlayerAppearanceMessage.TryParse(Message(true, template: template).Bytes));
        Assert.Equal(expected, player.ClassId);
    }

    [Theory]
    [InlineData(int.MinValue)] [InlineData(-1)] [InlineData(0)] [InlineData(1)]
    [InlineData(4)] [InlineData(37)] [InlineData(40)] [InlineData(44)] [InlineData(int.MaxValue)]
    public void UnknownClassCannotDiscardAnOtherwiseExplicitPlayerIdentity(int template)
    {
        var player = Assert.IsType<AbyssPlayerAppearanceObservation>(AbyssPlayerAppearanceMessage.TryParse(Message(true, template: template).Bytes));
        Assert.Null(player.ClassId);
        Assert.Equal((300, (ushort)501, "SyntheticPlayer"), (player.EntityKey, player.ServerId, player.Name));
        Assert.Equal(19, player.AbyssRankGrade);
    }

    [Theory]
    [InlineData(1)] [InlineData(10)] [InlineData(13)] [InlineData(17)] [InlineData(19)]
    public void GradeIsAnExplicitOrdinalAndDoesNotDependOnTheClass(int grade)
    {
        var player = Assert.IsType<AbyssPlayerAppearanceObservation>(AbyssPlayerAppearanceMessage.TryParse(Message(false, template: 0, grade: grade).Bytes));
        Assert.Equal(grade, player.AbyssRankGrade);
        Assert.Null(player.ClassId);
    }

    [Theory]
    [InlineData(int.MinValue)] [InlineData(-1)] [InlineData(0)] [InlineData(20)] [InlineData(int.MaxValue)]
    public void UnrecognizedGradeRemainsUnknownWithoutLosingPlayerIdentity(int grade)
    {
        var player = Assert.IsType<AbyssPlayerAppearanceObservation>(AbyssPlayerAppearanceMessage.TryParse(Message(true, grade: grade).Bytes));
        Assert.Null(player.AbyssRankGrade);
        Assert.Equal("Cleric", player.ClassId);
        Assert.Equal(300, player.EntityKey);
    }

    [Fact]
    public void MissingGradeIsNotInventedFromOtherScalars()
    {
        var player = Assert.IsType<AbyssPlayerAppearanceObservation>(AbyssPlayerAppearanceMessage.TryParse(Message(true, hasGrade: false).Bytes));
        Assert.Null(player.AbyssRankGrade);
        Assert.Equal("Cleric", player.ClassId);
    }

    [Theory]
    [InlineData(0U)] [InlineData(0x80000000U)] [InlineData(uint.MaxValue)]
    public void EntityKeyMustBeAPositiveSignedKey(uint entity) =>
        Assert.Null(AbyssPlayerAppearanceMessage.TryParse(Message(false, entity: entity).Bytes));

    [Fact]
    public void ExplicitServerAndPcDescriptorAreRequired()
    {
        var fixture = Message(false);
        var noServer = fixture.Bytes.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(noServer.AsSpan(fixture.Offsets["server"]), 0);
        Assert.Null(AbyssPlayerAppearanceMessage.TryParse(noServer));
        var noDescriptor = fixture.Bytes.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(noDescriptor.AsSpan(fixture.Offsets["flags"]), 0x1000000);
        Assert.Null(AbyssPlayerAppearanceMessage.TryParse(noDescriptor));
        var noNameFlag = fixture.Bytes.ToArray(); noNameFlag[fixture.Offsets["descriptor"]] = 0;
        Assert.Null(AbyssPlayerAppearanceMessage.TryParse(noNameFlag));
    }

    [Theory]
    [InlineData(0x08000000U)] [InlineData(0x10000000U)] [InlineData(0x20000000U)]
    [InlineData(0x40000000U)] [InlineData(0x80000000U)]
    public void UnqualifiedMainFlagsCannotFallBackToAnIdentityPrefix(uint bit)
    {
        var fixture = Message(true);
        BinaryPrimitives.WriteUInt32LittleEndian(fixture.Bytes.AsSpan(fixture.Offsets["flags"]), 0x07FFFFFF | bit);
        Assert.Null(AbyssPlayerAppearanceMessage.TryParse(fixture.Bytes));
    }

    [Theory]
    [InlineData(0x20)] [InlineData(0x40)] [InlineData(0x80)]
    public void UnknownDescriptorAndSharedAbnormalFormsFailClosed(int bit)
    {
        var descriptor = Message(true); descriptor.Bytes[descriptor.Offsets["descriptor"]] |= (byte)bit;
        Assert.Null(AbyssPlayerAppearanceMessage.TryParse(descriptor.Bytes));
        var abnormal = Message(true); abnormal.Bytes[abnormal.Offsets["abnormal"]] |= (byte)bit;
        Assert.Null(AbyssPlayerAppearanceMessage.TryParse(abnormal.Bytes));
    }

    [Theory]
    [InlineData(0x10)] [InlineData(0x20)] [InlineData(0x40)] [InlineData(0x80)]
    public void UnknownOpaqueStructureMaskDoesNotBecomeASecondLayoutProbe(int bit)
    {
        var fixture = Message(true); fixture.Bytes[fixture.Offsets["extension"]] |= (byte)bit;
        Assert.Null(AbyssPlayerAppearanceMessage.TryParse(fixture.Bytes));
    }

    [Theory]
    [InlineData("")] [InlineData(" ")] [InlineData("Synthetic\0Player")]
    [InlineData("Synthetic\nPlayer")] [InlineData("Synthetic\tPlayer")]
    public void PcNameMustBeExplicitNonblankTextWithoutControls(string name) =>
        Assert.Null(AbyssPlayerAppearanceMessage.TryParse(Message(false, name: Encoding.UTF8.GetBytes(name)).Bytes));

    [Theory]
    [InlineData("FF")] [InlineData("C080")] [InlineData("EDA080")]
    public void PcNameRequiresStrictUtf8(string hex) =>
        Assert.Null(AbyssPlayerAppearanceMessage.TryParse(Message(false, name: Convert.FromHexString(hex)).Bytes));

    [Fact]
    public void Utf8NamesAndCustomizationHaveIndependentByteBounds()
    {
        Assert.NotNull(AbyssPlayerAppearanceMessage.TryParse(Message(true,
            name: Encoding.UTF8.GetBytes(new string('ä', 64)), customizationBytes: 65536).Bytes));
        Assert.Null(AbyssPlayerAppearanceMessage.TryParse(Message(true,
            name: Encoding.UTF8.GetBytes(new string('x', 129))).Bytes));
        Assert.Null(AbyssPlayerAppearanceMessage.TryParse(Message(true, customizationBytes: 65537).Bytes));
        Assert.Null(AbyssPlayerAppearanceMessage.TryParse(new byte[(1 << 20) + 1]));
    }

    [Theory]
    [InlineData("8000")] [InlineData("FFFFFFFF10")] [InlineData("8080808080")]
    public void ActorNameAndListCountsRequireBoundedCanonicalVarints(string hex)
    {
        var fixture = Message(false);
        foreach (var (field, originalLength) in new[] { ("entity", 2), ("nameLength", 1), ("abnormals", 1),
                     ("equipment", 1), ("stats", 1), ("customization", 1) })
            Assert.Null(AbyssPlayerAppearanceMessage.TryParse(Replace(fixture, field, originalLength, Convert.FromHexString(hex))));
    }

    [Fact]
    public void ClaimedListsMustFitTheirRemainingMinimumShape()
    {
        var fixture = Message(false);
        foreach (var field in new[] { "abnormals", "equipment", "stats" })
            Assert.Null(AbyssPlayerAppearanceMessage.TryParse(Replace(fixture, field, 1, [0x91, 0x4E]))); // 10001
    }

    [Fact]
    public void OpaqueStateAndNativeDatabaseFieldsNeverClaimAliveOrLifeIdentity()
    {
        var fixture = Message(true);
        var first = AbyssPlayerAppearanceMessage.TryParse(fixture.Bytes);
        fixture.Bytes[fixture.Offsets["stateLive"]] = 255;
        BinaryPrimitives.WriteInt64LittleEndian(fixture.Bytes.AsSpan(fixture.Offsets["databaseId"]), long.MinValue);
        Assert.Equal(first, AbyssPlayerAppearanceMessage.TryParse(fixture.Bytes));
        BinaryPrimitives.WriteInt64LittleEndian(fixture.Bytes.AsSpan(fixture.Offsets["opaqueTotal"]), -1);
        Assert.Null(AbyssPlayerAppearanceMessage.TryParse(fixture.Bytes));
    }

    [Theory]
    [InlineData((ushort)0x3633)] [InlineData((ushort)0x3642)] [InlineData((ushort)0x8D04)]
    public void OtherActorAndDeathFormsDoNotBorrowPcAppearanceQualification(ushort opcode)
    {
        var fixture = Message(false); BinaryPrimitives.WriteUInt16LittleEndian(fixture.Bytes, opcode);
        Assert.Null(AbyssPlayerAppearanceMessage.TryParse(fixture.Bytes));
    }

    private sealed record Fixture(byte[] Bytes, Dictionary<string, int> Offsets);

    private static byte[] Replace(Fixture fixture, string field, int originalLength, byte[] replacement)
    {
        var offset = fixture.Offsets[field];
        return [.. fixture.Bytes[..offset], .. replacement, .. fixture.Bytes[(offset + originalLength)..]];
    }

    private static Fixture Message(bool optional, uint entity = 300, int template = 30, int grade = 19,
        bool hasGrade = true, byte[]? name = null, int customizationBytes = 3)
    {
        using var writer = new Packet(0x3645);
        var offsets = new Dictionary<string, int>();
        void Mark(string field) => offsets[field] = writer.Bytes.Length;
        var flags = optional ? 0x07FFFFFF : 1;
        flags = hasGrade ? flags | 0x1000000 : flags & ~0x1000000;
        Mark("entity"); writer.V(entity); Mark("flags"); writer.I(flags);
        Mark("descriptor"); writer.B(optional ? (byte)31 : (byte)1);
        name ??= Encoding.UTF8.GetBytes("SyntheticPlayer");
        Mark("nameLength"); writer.V((uint)name.Length); foreach (var value in name) writer.B(value);
        writer.I(template);
        if (optional) writer.Z(1 + 1 + 5 + 1);
        writer.U(optional ? ushort.MaxValue : (ushort)0); writer.Z(18);
        if (optional) writer.Z(12 + 4 + 2 + 12 + 12 + 4);
        Mark("stateLive"); writer.B(77); writer.V(128); writer.V(300); writer.Z(52);
        if (optional) { writer.Z(1 + 4 + 1 + 1); writer.V(300); writer.Z(4 + 4 + 1 + 4 + 4); }
        Mark("server"); writer.U(501);
        if (optional) { writer.L(1); writer.S("SkippedGuild"); writer.Z(2 + 1 + 4 + 8 + 2); writer.S(""); }
        Mark("abnormals"); writer.V(optional ? 1U : 0U);
        if (optional)
        {
            Mark("abnormal"); writer.B(31); writer.V(300); writer.Z(20); writer.V(300);
            writer.B(1); writer.I(0); writer.Bool(true); writer.V(300); writer.Z(4 + 12);
        }
        Mark("equipment"); writer.V(optional ? 2U : 0U);
        if (optional)
        {
            for (var i = 0; i < 2; i++)
            {
                writer.Z(14); writer.Bool(i == 0); writer.I(0); writer.V(1);
                writer.Z(4); writer.Bool(true); writer.Z(11); writer.Bool(false); writer.Z(9); writer.Bool(true); writer.Z(4);
                writer.V(1); writer.I(0); writer.Bool(true); writer.Bool(false); writer.Bool(true); writer.V(300);
            }
            writer.Z(1 + 1); writer.Bool(true); writer.Bool(false); writer.Z(1 + 12 + 8 + 2 + 2 + 8);
            writer.Bool(true); writer.B(0); writer.Bool(false);
        }
        Mark("databaseId"); writer.L(1234567890123);
        Mark("stats"); writer.V(optional ? 2U : 0U); if (optional) writer.Z(12);
        writer.Z(12);
        if (optional) writer.Z(4 + 4 + 4);
        Mark("customization"); writer.V((uint)customizationBytes); writer.Z(customizationBytes);
        if (optional)
        {
            writer.B(0); writer.I(0);
            Mark("extension"); writer.B(15); writer.Z(7); writer.Bool(true); writer.I(0); writer.Z(8 + 4 + 8 + 4);
            writer.Z(4 + 4 + 4);
        }
        if (hasGrade) writer.I(grade);
        if (optional) { writer.Bool(true); writer.I(0); }
        Mark("opaqueTotal"); writer.L(0); writer.B(0); writer.Bool(true);
        return new(writer.Bytes, offsets);
    }
}
