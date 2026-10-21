using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class OwnWorldMessageTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void MapDefinitionIsReturnedOnlyAfterCompleteOwnAndOpaqueKeyIsNeverExposed(bool optional)
    {
        var message = OwnIdentityMessageTests.OwnMessage(optional, mapKey: 918273645, mapId: 600001);
        var observation = Assert.IsType<OwnWorldObservation>(OwnIdentityMessage.TryParseWorld(message));
        Assert.Equal(600001, observation.MapDataId);
        Assert.Equal(OwnIdentityMessage.TryParse(message), observation.Identity);
        Assert.DoesNotContain("918273645", observation.ToString());
        NetworkProgressMessageTests.EveryCutRejected(message, d => OwnIdentityMessage.TryParseWorld(d));
        Assert.Null(OwnIdentityMessage.TryParseWorld([.. message, 0]));
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(int.MinValue)]
    public void UnsupportedMapMetadataDoesNotInvalidateTheOwnIdentity(int map)
    {
        var observation = Assert.IsType<OwnWorldObservation>(OwnIdentityMessage.TryParseWorld(OwnIdentityMessageTests.OwnMessage(false, mapId: map)));
        Assert.Null(observation.MapDataId);
        Assert.Equal("Testchar", observation.Identity.Name);
    }

    [Fact]
    public void PreparationMapIsStrictButProvisional()
    {
        using var packet = new NetworkProgressMessageTests.Packet(0x3621);
        packet.I(1); packet.I(600001); packet.L(123); packet.Z(12 + 4); packet.B(0); packet.B(0); packet.L(0);
        packet.V(0); packet.V(300); packet.S(""); packet.B(0);
        Assert.Equal(600001, OwnIdentityMessage.TryParsePreparation(packet.Bytes)?.MapDataId);
        NetworkProgressMessageTests.EveryCutRejected(packet.Bytes, d => OwnIdentityMessage.TryParsePreparation(d));
        Assert.Null(OwnIdentityMessage.TryParsePreparation([.. packet.Bytes, 0]));
    }
}
