using OpenCvSharp;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>
/// The world-map check runs only when the header symbol shows top left (user idea 2026-10-05). It must
/// fire on the open world map, at every UI scale, and stay quiet in normal play.
/// </summary>
public sealed class MapWindowEmblemTests
{
    private static string Fixture(string folder, string name) =>
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tests", "fixtures", folder, name);

    [Theory]
    [InlineData("map-tracking", "live-2026-10-03-worldmap-drift-screen.png", true)]
    [InlineData("map-tracking", "live-2026-10-03-lost-screen.png", true)]
    [InlineData("map-tracking", "live-2026-10-03-screen.png", false)]
    [InlineData("pet-window", "en-2026-10-03-uiscale-large.png", true)]
    [InlineData("pet-window", "en-2026-10-03-uiscale-medium.png", true)]
    [InlineData("pet-window", "en-2026-10-03-uiscale-small.png", true)]
    public void HeaderSymbolOfFullScreenWindows(string folder, string name, bool expected)
    {
        using var screen = Cv2.ImRead(Fixture(folder, name));
        Assert.False(screen.Empty(), name);
        Assert.Equal(expected, MapWindowEmblem.Shows(screen));
    }
}
