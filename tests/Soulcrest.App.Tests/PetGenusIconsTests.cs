using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class PetGenusIconsTests
{
    [Theory]
    [InlineData("cogni", "Cogni", "intellect")]
    [InlineData("natura", "Natura", "nature")]
    [InlineData("varian", "Varian", "trans")]
    [InlineData("fera", "Fera", "feral")]
    public void CanonicalAndCatalogLabelsUseTheExistingLocalPetTabIcons(string canonical, string catalogLabel, string slot)
    {
        var expected = $"https://{MainForm.MapDataHost}/icons/gamingtools/ui/resource/sprite/fwindow_vehicle/ut_fwindow_vehicle_creatureslot_{slot}_sprite.png";
        Assert.Equal(expected, PetGenusIcons.IconUrl(canonical));
        Assert.Equal(expected, PetGenusIcons.IconUrl(catalogLabel));
        Assert.Equal(expected, PetGenusIcons.IconUrl(catalogLabel.ToUpperInvariant()));
    }

    [Theory]
    [InlineData("special")]
    [InlineData("Special")]
    [InlineData("unknown")]
    [InlineData("")]
    [InlineData(null)]
    public void UnsupportedGeneraHaveNoGuessedSymbol(string? genus) => Assert.Null(PetGenusIcons.IconUrl(genus!));
}
