using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class MerchantPetItemsTests
{
    [Theory]
    [InlineData(534760011, 1012)]
    [InlineData(534760061, 1062)]
    [InlineData(534760096, 1097)]
    [InlineData(534760008, 1009)]
    [InlineData(534760012, 1013)]
    [InlineData(534760044, 1045)]
    [InlineData(534760051, 1052)]
    [InlineData(534760056, 1057)]
    [InlineData(534760093, 1094)]
    [InlineData(534760118, 1146)]
    [InlineData(534760191, 1156)]
    [InlineData(534760131, 1191)]
    [InlineData(534760139, 1194)]
    public void ObservedMerchantItemsResolveThroughIndependentPublicPetDetails(int itemId, int petNumber) =>
        Assert.Equal(petNumber, MerchantPetItems.Find(itemId));

    [Fact]
    public void EveryEmbeddedItemResolvesToAKnownPetAndUnknownItemsRemainUnknown()
    {
        Assert.NotEmpty(MerchantPetItems.All);
        foreach (var (item, pet) in MerchantPetItems.All)
        {
            Assert.True(item > 0);
            Assert.NotNull(PetIdTable.Find(pet));
        }
        Assert.Null(MerchantPetItems.Find(-1));
        Assert.Null(MerchantPetItems.Find(1));
    }
}
