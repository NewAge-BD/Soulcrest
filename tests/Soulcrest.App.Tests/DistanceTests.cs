using Soulcrest.App.Overlay;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>
/// Distance in game metres (user request 2026-10-06). Checked in game on Altgard against the quest
/// targets, the NPCs Ninir (3539.7, 4332.8) and Ulgorn (2390.1, 3134.7), marked by the user: the game
/// showed 258 m, 998 m and 906 m.
/// </summary>
public sealed class DistanceTests
{
    private const double AltgardMetersPerPixel = 0.996094; // manifest value: world units are centimetres

    [Theory]
    [InlineData(3363, 4524, 3539.697, 4332.795, 258)]
    [InlineData(3270, 3369, 3539.697, 4332.795, 998)]
    [InlineData(3270, 3369, 2390.106, 3134.685, 906)]
    public void MapDistanceMatchesTheGame(double x, double y, double targetX, double targetY, double game)
    {
        var pixels = Math.Sqrt((targetX - x) * (targetX - x) + (targetY - y) * (targetY - y));
        Assert.InRange(pixels * AltgardMetersPerPixel / game, 0.985, 1.015); // the game also counts height
    }

    [Theory]
    [InlineData(258.4, "en", "258 m")]
    [InlineData(999.4, "en", "999 m")]
    [InlineData(999.6, "en", "1.0 km")]
    [InlineData(1834, "de", "1,8 km")]
    public void DistanceReadsLikeTheGame(double meters, string language, string expected)
    {
        var before = Soulcrest.App.Services.UiText.Language;
        try
        {
            Soulcrest.App.Services.UiText.Language = language;
            Assert.Equal(expected, RouteOverlayForm.DistanceText(meters));
        }
        finally { Soulcrest.App.Services.UiText.Language = before; }
    }
}
