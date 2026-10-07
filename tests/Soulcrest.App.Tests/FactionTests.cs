using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>The guards kill the other faction: Elyos need the zones on Altgard, Asmodians on Verteron (setup 2026-10-07).</summary>
public sealed class FactionTests
{
    [Theory]
    [InlineData(Factions.Elyos, "altgard")]
    [InlineData(Factions.Asmodian, "verteron")]
    public void FactionSwitchesTheZonesOfTheOtherFactionsMap(string faction, string map) =>
        Assert.Equal([map], Factions.GuardZoneMapsFor(faction));

    [Fact]
    public void NoFactionNoZones() => Assert.Empty(Factions.GuardZoneMapsFor(null));
}
