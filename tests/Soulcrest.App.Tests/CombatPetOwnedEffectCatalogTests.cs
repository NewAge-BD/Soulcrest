using System.Globalization;
using Soulcrest.App.Components;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class CombatPetOwnedEffectCatalogTests
{
    [Theory]
    [InlineData("cogni", 1, "HPMax:10")]
    [InlineData("cogni", 2, "HPMax:20;Critical:1")]
    [InlineData("cogni", 3, "HPMax:30;Critical:2;STR:1")]
    [InlineData("fera", 1, "GroundVehicleSpeed:1")]
    [InlineData("fera", 2, "GroundVehicleSpeed:2;Accuracy:1")]
    [InlineData("fera", 3, "GroundVehicleSpeed:3;Accuracy:2;DEX:1")]
    [InlineData("natura", 1, "SPMax:3")]
    [InlineData("natura", 2, "SPMax:6;CriticalResist:1")]
    [InlineData("natura", 3, "SPMax:9;CriticalResist:2;AGI:1")]
    [InlineData("varian", 1, "VehicleSprintCost:0.1")]
    [InlineData("varian", 2, "VehicleSprintCost:0.2;Evasion:1")]
    [InlineData("varian", 3, "VehicleSprintCost:0.3;Evasion:2;CON:1")]
    public void EachAcquiredLevelUsesTheFullExactEffectVector(string genus, int level, string expected)
    {
        var effects = Assert.IsAssignableFrom<IReadOnlyList<CombatTitleDisplayEffect>>(CombatPetOwnedEffectCatalog.Effects(genus, level));
        Assert.Equal(expected, string.Join(";", effects.Select(effect => effect.StatId + ":"
            + effect.Value!.Value.ToString(CultureInfo.InvariantCulture))));
        Assert.All(effects, effect => Assert.Equal(effect.StatId == "VehicleSprintCost" ? "Percent" : "Points", effect.Unit));
    }

    [Theory]
    [InlineData("cogni")]
    [InlineData("fera")]
    [InlineData("natura")]
    [InlineData("varian")]
    [InlineData("special")]
    public void KnownLevelZeroIsExplicitlyEmptyAndCannotGrantStats(string genus)
    {
        Assert.Empty(CombatPetOwnedEffectCatalog.Effects(genus, 0)!);
    }

    [Fact]
    public void SpecialAndUnknownMetadataHaveDifferentSemantics()
    {
        Assert.Equal(3, CombatPetOwnedEffectCatalog.MaximumLevel);
        for (var level = 0; level <= CombatPetOwnedEffectCatalog.MaximumLevel; level++)
            Assert.Empty(CombatPetOwnedEffectCatalog.Effects("special", level)!);
        Assert.Null(CombatPetOwnedEffectCatalog.Effects("unknown", 0));
        Assert.Null(CombatPetOwnedEffectCatalog.Effects("", 3));
        Assert.Null(CombatPetOwnedEffectCatalog.Effects(null!, 1));
        Assert.Null(CombatPetOwnedEffectCatalog.Effects("cogni", -1));
        Assert.Null(CombatPetOwnedEffectCatalog.Effects("cogni", 4));
        Assert.Null(CombatPetOwnedEffectCatalog.Effects("special", 4));
    }

    [Fact]
    public void EffectsKeepLocalizedNamesAndReadOnlyCollections()
    {
        var effects = new[] { "cogni", "fera", "natura", "varian" }
            .SelectMany(genus => CombatPetOwnedEffectCatalog.Effects(genus, 3)!).ToArray();
        Assert.Equal(("Macht", "Might"), Labels("STR"));
        Assert.Equal(("Agilität", "Dexterity"), Labels("DEX"));
        Assert.Equal(("Präzision", "Precision"), Labels("AGI"));
        Assert.Equal(("Vitalität", "Constitution"), Labels("CON"));
        Assert.Equal(("SP", "Stamina"), Labels("SPMax"));
        Assert.Equal(("SP-Kosten (Reittier): Sprint", "Mount Sprint Stamina Cost Reduction Rate"), Labels("VehicleSprintCost"));
        foreach (var genus in new[] { "cogni", "fera", "natura", "varian", "special" })
        {
            var view = CombatPetOwnedEffectCatalog.Effects(genus, 3)!;
            Assert.Throws<NotSupportedException>(() => Assert.IsAssignableFrom<IList<CombatTitleDisplayEffect>>(view).Clear());
        }

        (string De, string En) Labels(string id)
        {
            var effect = Assert.Single(effects, effect => effect.StatId == id);
            return (effect.LabelDe, effect.LabelEn);
        }
    }
}
