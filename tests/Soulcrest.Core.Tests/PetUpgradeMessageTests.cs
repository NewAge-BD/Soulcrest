using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class PetUpgradeMessageTests
{
    [Theory]
    [InlineData(1062, 2)]
    [InlineData(1029, 3)]
    public void ExplicitUpgradeConsumesItsWholeKnownEnvelopeAndLeavesTheUnusedTailOpaque(int pet, int level)
    {
        var message = Message(pet: pet, level: level);
        Assert.Equal(new PetUpgradeResult(0, pet, level), PetProgressMessage.TryParseUpgrade(message));
        NetworkProgressMessageTests.EveryCutRejected(message, bytes => PetProgressMessage.TryParseUpgrade(bytes));
        Assert.Null(PetProgressMessage.TryParseUpgrade([0x09, 0x90, .. message[2..]]));
        Assert.Null(PetProgressMessage.TryParseAcquired(message));
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(-1, 2)]
    [InlineData(1062, -1)]
    [InlineData(1062, 0)]
    [InlineData(1062, 4)]
    public void SuccessRequiresAPositivePetAndSupportedAbsoluteLevel(int pet, int level) =>
        Assert.Null(PetProgressMessage.TryParseUpgrade(Message(pet: pet, level: level)));

    [Fact]
    public void CompleteFailedResultCanContainZeroFieldsButDoesNotBecomeAnUpgrade()
    {
        var message = Message(result: 1, pet: 0, level: 0);
        Assert.Equal(new PetUpgradeResult(1, 0, 0), PetProgressMessage.TryParseUpgrade(message));
        NetworkProgressMessageTests.EveryCutRejected(message, bytes => PetProgressMessage.TryParseUpgrade(bytes));
    }

    private static byte[] Message(ushort result = 0, int pet = 1062, int level = 2)
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        writer.Write((ushort)0x9014);
        writer.Write(result);
        writer.Write(pet);
        writer.Write(level);
        writer.Write(Enumerable.Range(0, 13).Select(i => (byte)(255 - i)).ToArray());
        return bytes.ToArray();
    }
}
