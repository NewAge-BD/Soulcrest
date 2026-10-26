using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class OwnCharacterScoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompleteOwnReadsCurrentScoresAndNeverSubstitutesTheHigherHistoricalScores(bool optional)
    {
        var message = OwnIdentityMessageTests.OwnMessage(optional, gearScore: 325, highestGearScore: 650,
            combatScore: 9_007_199_254_741_003, highestCombatScore: 9_007_199_254_741_099);
        var world = Assert.IsType<OwnWorldObservation>(OwnIdentityMessage.TryParseWorld(message));
        Assert.Equal(325, world.GearScore);
        Assert.Equal(9_007_199_254_741_003, world.CombatScore);
        NetworkProgressMessageTests.EveryCutRejected(message, data => OwnIdentityMessage.TryParseWorld(data));
        Assert.Null(OwnIdentityMessage.TryParseWorld([.. message, 0]));
    }

    [Theory]
    [InlineData(0, 0, 0L, 0)]
    [InlineData(-1, 25, null, 25)]
    [InlineData(100, -1, 100L, null)]
    [InlineData(long.MinValue, int.MinValue, null, null)]
    public void UnavailableScoreDoesNotInvalidateTheOtherScoreOrTheWholeIdentity(long combat, int gear,
        long? expectedCombat, int? expectedGear)
    {
        var world = Assert.IsType<OwnWorldObservation>(OwnIdentityMessage.TryParseWorld(
            OwnIdentityMessageTests.OwnMessage(false, gearScore: gear, combatScore: combat)));
        Assert.Equal(expectedCombat, world.CombatScore);
        Assert.Equal(expectedGear, world.GearScore);
        Assert.Equal(123456789, world.Identity.CharacterId);
    }

    [Fact]
    public void ScoresAreMetadataAndCannotChangeIdentityEquality()
    {
        var first = Assert.IsType<OwnWorldObservation>(OwnIdentityMessage.TryParseWorld(
            OwnIdentityMessageTests.OwnMessage(false, gearScore: 100, combatScore: 500)));
        var next = Assert.IsType<OwnWorldObservation>(OwnIdentityMessage.TryParseWorld(
            OwnIdentityMessageTests.OwnMessage(false, gearScore: 200, combatScore: 800)));
        Assert.Equal(first.Identity, next.Identity);
        Assert.Equal(100, first.GearScore);
        Assert.Equal(500, first.CombatScore);
        Assert.Equal(200, next.GearScore);
        Assert.Equal(800, next.CombatScore);
    }
}
