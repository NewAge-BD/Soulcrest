using Soulcrest.Core.Pets;
using Soulcrest.Core.PetWindow;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class PanelPetNameTests
{
    private static readonly PetDefinition[] Pets =
    [
        new() { Id = "dracuni-herbalist", En = "Dracuni Herbalist", Genus = "cogni" },
        new() { Id = "predator-saraswati", En = "Predator Saraswati", Genus = "cogni" },
        new() { Id = "kerubar", En = "Kerubar", Genus = "cogni" },
        new() { Id = "kerubian", En = "Kerubian", Genus = "cogni" },
        new() { Id = "kerubiel", En = "Kerubiel", Genus = "cogni" },
        new() { Id = "enhanced-krall-commander", En = "Enhanced Krall Commander", Genus = "cogni" },
        new() { Id = "swarm", En = "Swarm", Genus = "fera" },
    ];

    [Theory] // misreads from the user's learned pets, 2026-10-03
    [InlineData("Dracunj Herbalist", "dracuni-herbalist")]
    [InlineData("Predator Saraswatj", "predator-saraswati")]
    [InlineData("Kerubjan", "kerubian")]
    [InlineData("Enhanced Krall Com...", "enhanced-krall-commander")]
    [InlineData("Swarm", "swarm")]
    public void ResolvesMisreadNames(string read, string id) => Assert.Equal(id, PanelPetName.Resolve(read, Pets)?.Id);

    [Theory]
    [InlineData("Kerubxx")] // as close to Kerubar as to others: ambiguous
    [InlineData("Red Spark Ignus")]
    [InlineData("Pagati")]
    public void LeavesUnknownOrAmbiguousNamesUnresolved(string read) => Assert.Null(PanelPetName.Resolve(read, Pets));

    [Theory]
    [InlineData("Red Spark Jgnus", "Red Spark Ignus")]
    [InlineData("Dracunj Herbalist", "Dracuni Herbalist")]
    [InlineData("Kerubjan", "Kerubjan")] // j before a vowel may be real
    [InlineData("Sentinel K'nash", "Sentinel K'nash")]
    public void CleansKnownMisreads(string read, string clean) => Assert.Equal(clean, PanelPetName.Clean(read));
}

public sealed class PetNameOverridesTests
{
    [Fact]
    public void ShippedOverridesNameCrestlich() =>
        Assert.Equal("Crestlich", PetNameOverrides.Shipped["crestlich-01"]);

    [Fact]
    public void IconNamedPetsArePlaceholders()
    {
        Assert.True(PetNameOverrides.IsPlaceholder(new PetDefinition { Id = "crestlich-02", En = "Crestlich 02", IconName = "Crestlich 02", Genus = "fera" }));
        Assert.False(PetNameOverrides.IsPlaceholder(new PetDefinition { Id = "brax", En = "Brax", IconName = "Brax 01", Genus = "fera" }));
    }
}
