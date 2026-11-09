using Soulcrest.Core.Network;
using Xunit;
using Packet = Soulcrest.Core.Tests.NetworkProgressMessageTests.Packet;

namespace Soulcrest.Core.Tests;

public sealed class OwnSkillMessageTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(3)] [InlineData(5)] [InlineData(9)] [InlineData(15)]
    public void FullListKeepsExplicitTotalOriginalAndOptionalComponentsAndRequiresEof(byte flags)
    {
        using var p = new Packet(0x5100);
        p.V(1); p.B(flags); p.I(17060000); p.B(15); p.B((flags & 1) == 0 ? (byte)13 : (byte)10);
        if ((flags & 1) != 0) { p.B(2); p.B(0); p.B(1); p.B(1); p.B(1); }
        if ((flags & 2) != 0) p.V(300);
        p.I(123);
        if ((flags & 4) != 0) p.B(2);
        if ((flags & 8) != 0) p.V(300);
        var snapshot = Assert.IsType<OwnSkillList>(OwnSkillMessage.TryParseList(p.Bytes));
        var skill = Assert.Single(snapshot.Skills).Value;
        Assert.Equal(17060000, skill.SkillId);
        Assert.Equal(15, skill.TotalLevel);
        if ((flags & 1) != 0)
        {
            Assert.Equal(10, skill.OriginalLevel);
            Assert.Equal(new(2, 0, 1, 1, 1), skill.AdditionalLevels);
        }
        else Assert.Null(skill.AdditionalLevels);
        NetworkProgressMessageTests.EveryCutRejected(p.Bytes, data => OwnSkillMessage.TryParseList(data));
        Assert.Null(OwnSkillMessage.TryParseList([.. p.Bytes, 0]));
    }

    [Theory]
    [InlineData(0x380F)] [InlineData(0x3813)]
    public void ChangesUseExplicitSkillIdRatherThanMapStorageKeyAndKeepComponentUnknown(ushort opcode)
    {
        using var p = new Packet(opcode);
        if (opcode == 0x3813) { p.V(1); p.I(42); }
        p.B(0); p.I(17060000); p.B(12); p.B(10); p.B(255);
        var value = Assert.Single(Assert.IsType<OwnSkillChanges>(OwnSkillMessage.TryParseChanges(p.Bytes)).Skills).Value;
        Assert.Equal(17060000, value.SkillId);
        Assert.Equal(12, value.TotalLevel);
        Assert.Null(value.AdditionalLevels);
        NetworkProgressMessageTests.EveryCutRejected(p.Bytes, data => OwnSkillMessage.TryParseChanges(data));
        Assert.Null(OwnSkillMessage.TryParseChanges([.. p.Bytes, 0]));
    }

    [Theory]
    [InlineData(0, 4, 4, 4, false)]
    [InlineData(1, 4, 3, 2, false)]
    [InlineData(1, 4, 5, 0, false)]
    [InlineData(16, 4, 4, 0, false)]
    [InlineData(1, 4, 2, 2, true)]
    public void UnknownFlagsInvalidIdsAndInconsistentComponentTotalsFailClosed(
        byte flags, byte total, byte original, byte board, bool valid)
    {
        using var p = new Packet(0x5100);
        p.V(1); p.B(flags); p.I(flags == 0 ? 0 : 17060000); p.B(total); p.B(original);
        if ((flags & 1) != 0) { p.B(board); p.Z(4); }
        p.I(0);
        Assert.Equal(valid, OwnSkillMessage.TryParseList(p.Bytes) is not null);
    }

    [Fact]
    public void DuplicateExplicitIdsCannotSilentlyOverwriteAndEmptyListDoesNotInventZeroLevels()
    {
        using var p = new Packet(0x5100);
        p.V(2);
        for (var i = 0; i < 2; i++) { p.B(0); p.I(17060000); p.B(10); p.B(10); p.I(0); }
        Assert.Null(OwnSkillMessage.TryParseList(p.Bytes));
        using var empty = new Packet(0x5100); empty.V(0);
        Assert.Empty(Assert.IsType<OwnSkillList>(OwnSkillMessage.TryParseList(empty.Bytes)).Skills);
        Assert.Null(OwnSkillMessage.TryParseList([0, 0, 0]));
    }
}
