using System.Text.Json;
using Soulcrest.Core.Pets;
using Soulcrest.Core.Network;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class PetFarmRegionsTests
{
    [Theory]
    [InlineData(1012, "Verteron")]
    [InlineData(1062, "Abyss / Verteron")]
    [InlineData(1097, "Verteron")]
    [InlineData(1009, "Special")]
    [InlineData(1013, "Abyss / Altgard")]
    [InlineData(1045, "Altgard / Verteron")]
    [InlineData(1052, "Altgard / Verteron")]
    [InlineData(1057, "Special")]
    [InlineData(1094, "Altgard / Verteron")]
    [InlineData(1146, "Special")]
    [InlineData(1156, "Verteron")]
    [InlineData(1191, "Special")]
    [InlineData(1194, "Verteron")]
    public void ObservedMerchantPetsUseBothPublishedMapMarkersAndNpcLocations(int petNumber, string expected)
    {
        Assert.Equal(expected, string.Join(" / ", PetFarmRegions.Find(petNumber)!));
    }

    [Fact]
    public void MissingPetIsUnknownAndRegionListsCannotBeMutated()
    {
        Assert.Null(PetFarmRegions.Find(-1));
        Assert.Null(PetFarmRegions.Find(int.MaxValue));
        var regions = Assert.IsAssignableFrom<IList<string>>(PetFarmRegions.Find(1013));
        Assert.Throws<NotSupportedException>(() => regions[0] = "Special");
    }

    [Fact]
    public void EveryClassificationHasPublicSourcesAndNoMixedSpecialOrDuplicateAbyss()
    {
        using var stream = typeof(PetFarmRegions).Assembly.GetManifestResourceStream("Soulcrest.PetFarmRegions.json")!;
        using var document = JsonDocument.Parse(stream);
        foreach (var row in document.RootElement.GetProperty("pets").EnumerateArray())
        {
            Assert.NotNull(PetIdTable.Find(row.GetProperty("petNumber").GetInt32()));
            Assert.NotEmpty(row.GetProperty("sourceRefs").EnumerateArray());
            foreach (var source in row.GetProperty("sourceRefs").EnumerateArray())
                Assert.Contains("https://", source.GetString());
            var regions = row.GetProperty("regions").EnumerateArray().Select(r => r.GetString()).ToArray();
            Assert.Equal(regions.Length, regions.Distinct().Count());
            if (regions.Contains("Special")) Assert.Single(regions);
        }
    }
}
