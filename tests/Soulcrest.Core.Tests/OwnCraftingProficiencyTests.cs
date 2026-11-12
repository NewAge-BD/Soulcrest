using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class OwnCraftingProficiencyTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CompleteOwnPublishesNumericCraftRecordsOnlyAfterItsExactEnd(bool optional)
    {
        CharacterCraftingProficiency[] records = [new(1, 12, 1234, 2), new(2, 0, 0, 0), new(5, 100, 5000, 7)];
        var bytes = OwnIdentityMessageTests.OwnMessage(optional, crafting: records);
        var world = Assert.IsType<OwnWorldObservation>(OwnIdentityMessage.TryParseWorld(bytes));
        Assert.Equal(records, world.CraftingProficiencies!.OrderBy(pair => pair.Key).Select(pair => pair.Value));
        Assert.False(world.CraftingProficiencies!.ContainsKey(3));
        NetworkProgressMessageTests.EveryCutRejected(bytes, data => OwnIdentityMessage.TryParseWorld(data));
    }

    [Fact]
    public void UnknownCategoriesAreConsumedWithoutInventingAProfessionAndMissingKeysAreNotZero()
    {
        var world = OwnIdentityMessage.TryParseWorld(OwnIdentityMessageTests.OwnMessage(false,
            crafting: [new(1, 23, 8, 0), new(200, 65000, -1, 0)]))!;
        Assert.Single(world.CraftingProficiencies!);
        Assert.Equal((ushort)23, world.CraftingProficiencies![1].Level);
        Assert.False(world.CraftingProficiencies!.ContainsKey(2));
    }

    [Theory]
    [InlineData("duplicate")] [InlineData("negative-exp")] [InlineData("out-of-range-level")]
    public void InvalidCraftDomainPreservesTheIndependentlyValidatedOwnIdentity(string invalid)
    {
        CharacterCraftingProficiency[] rows = invalid switch
        {
            "duplicate" => [new(1, 10, 1, 0), new(1, 11, 2, 0)],
            "negative-exp" => [new(1, 10, -1, 0)],
            _ => [new(1, 101, 0, 0)]
        };
        var world = Assert.IsType<OwnWorldObservation>(OwnIdentityMessage.TryParseWorld(
            OwnIdentityMessageTests.OwnMessage(false, crafting: rows)));
        Assert.Null(world.CraftingProficiencies);
        Assert.Equal(123456789, world.Identity.CharacterId);
    }

    [Fact]
    public void OversizedCraftListFailsTheBoundedWholeOwnContract() => Assert.Null(OwnIdentityMessage.TryParseWorld(
        OwnIdentityMessageTests.OwnMessage(false, crafting: Enumerable.Repeat(new CharacterCraftingProficiency(1, 1, 0, 0), 257).ToArray())));
}
