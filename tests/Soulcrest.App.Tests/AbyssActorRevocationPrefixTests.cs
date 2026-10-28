using Soulcrest.App.Network;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class AbyssActorRevocationPrefixTests
{
    [Theory]
    [InlineData("413601", 1)] [InlineData("42369003", 400)]
    [InlineData("4136FFFFFFFF07", int.MaxValue)] [InlineData("42369003FFFF", 400)]
    public void OnlyTheCanonicalPositivePrefixIsReadAndTheTailCannotCreateEvidence(string hex, int expected) =>
        Assert.Equal(expected, AbyssActorRevocationPrefix.TryRead(Convert.FromHexString(hex)));

    [Theory]
    [InlineData("4736010100", 1)] [InlineData("473690030100", 400)]
    [InlineData("4736FF7F0100", 16383)]
    [InlineData("473690030A4C", 400)] [InlineData("473690030A00", 400)]
    [InlineData("47369003024F", 400)] [InlineData("473690030A46", 400)]
    [InlineData("47369003044F", 400)] [InlineData("47369003021E", 400)]
    public void QualifiedCharDisappearSelectsOnlyItsExplicitActorAndDoesNotInterpretChildBytes(string hex, int actor) =>
        Assert.Equal(actor, AbyssActorRevocationPrefix.TryRead(Convert.FromHexString(hex)));

    [Theory]
    [InlineData("473601")] [InlineData("47360101")] [InlineData("4736900301")]
    [InlineData("473601010000")] [InlineData("47369003010000")]
    [InlineData("4736000100")] [InlineData("473680000100")] [InlineData("473681000100")]
    [InlineData("47368080010100")] [InlineData("4736FFFFFFFF070100")]
    [InlineData("473601FFFF")] [InlineData("4736010000")] [InlineData("4736010A4C")]
    [InlineData("47369003014C")] [InlineData("473690030A4F")]
    public void UnknownCharDisappearFormsCannotNarrowTheGlobalFallback(string hex) =>
        Assert.Null(AbyssActorRevocationPrefix.TryRead(Convert.FromHexString(hex)));

    [Theory]
    [InlineData("")] [InlineData("41")] [InlineData("4136")] [InlineData("413600")]
    [InlineData("413680")] [InlineData("41368000")] [InlineData("41368100")]
    [InlineData("41368080808008")] [InlineData("4136FFFFFFFF0F")]
    [InlineData("41368080808010")] [InlineData("4136808080808001")]
    [InlineData("47369003")] [InlineData("46369003")] [InlineData("45369003")]
    public void UnknownOpcodesAndUnprovenOrMalformedPrefixesCannotSelectAnActor(string hex) =>
        Assert.Null(AbyssActorRevocationPrefix.TryRead(Convert.FromHexString(hex)));
}
