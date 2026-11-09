using Soulcrest.App.Components;
using Soulcrest.App.Services;
using Soulcrest.Core.Combat;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class CombatPetsPresentationTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private static PetCollectionEntry Pet(string id, string genus, int level, bool known = true, bool current = true, bool manual = false) =>
        new(id, genus, level, known, current, manual, At);
    private static CombatPetsSnapshot State(params PetCollectionEntry[] pets) => CombatPetsPresentation.Create(new(Array.AsReadOnly(pets)));
    private static CombatPetEffectTotal Effect(IReadOnlyList<CombatPetEffectTotal> effects, string id) => Assert.Single(effects, row => row.Key.StatId == id);

    [Fact]
    public void EveryMaxPetSuppliesAllThreeBonusesAndNoRemainingEffects()
    {
        var state = State(Pet("a", "cogni", 3), Pet("b", "fera", 3), Pet("c", "natura", 3), Pet("d", "varian", 3));
        Assert.Equal(CombatKnowledgeStatus.Complete, state.Status);
        var expected = new Dictionary<string, decimal> { ["HPMax"] = 30, ["Critical"] = 2, ["STR"] = 1,
            ["GroundVehicleSpeed"] = 3, ["Accuracy"] = 2, ["DEX"] = 1, ["SPMax"] = 9, ["CriticalResist"] = 2, ["AGI"] = 1,
            ["VehicleSprintCost"] = .3m, ["Evasion"] = 2, ["CON"] = 1 };
        Assert.Equal(expected.Count, state.OwnedEffects.Count);
        foreach (var (id, value) in expected) Assert.Equal(value, Effect(state.OwnedEffects, id).Value);
        Assert.Equal("Percent", Effect(state.OwnedEffects, "VehicleSprintCost").Key.Unit);
        Assert.All(state.MissingEffects, row => Assert.Equal(0m, row.Value));
        Assert.All(state.Categories, category => { Assert.Equal(3, category.CompletedStages); Assert.Equal(3, category.MaximumStages); Assert.Equal(100m, category.CompletionPercent); });
    }

    [Fact]
    public void CompletionCountsAcquiredStagesAndUsesTheWholeCurrentLevelVector()
    {
        var state = State(Pet("one", "cogni", 1), Pet("two", "cogni", 2));
        var category = Assert.Single(state.Categories, row => row.Genus == "cogni");
        Assert.Equal(3, category.CompletedStages); Assert.Equal(6, category.MaximumStages); Assert.Equal(50m, category.CompletionPercent);
        Assert.Equal(30m, Effect(state.OwnedEffects, "HPMax").Value);
        Assert.Equal(1m, Effect(state.OwnedEffects, "Critical").Value);
        Assert.Equal(0m, Effect(state.OwnedEffects, "STR").Value);
        Assert.Equal(30m, Effect(state.MissingEffects, "HPMax").Value);
        Assert.Equal(3m, Effect(state.MissingEffects, "Critical").Value);
        Assert.Equal(2m, Effect(state.MissingEffects, "STR").Value);
    }

    [Fact]
    public void OwnedPlusMissingAlwaysEqualsTheRosterMaximumIncludingDecimalRates()
    {
        var pets = CombatPetsPresentation.Genera.SelectMany(genus => Enumerable.Range(0, 4).Select(level => Pet(genus + level, genus, level))).ToArray();
        var state = State(pets);
        foreach (var genus in CombatPetsPresentation.Genera)
            foreach (var maximum in CombatPetOwnedEffectCatalog.Effects(genus, 3)!)
                Assert.Equal(4 * maximum.Value, Effect(state.OwnedEffects, maximum.StatId).Value + Effect(state.MissingEffects, maximum.StatId).Value);
        Assert.Equal(.6m, Effect(state.OwnedEffects, "VehicleSprintCost").Value);
        Assert.Equal(.6m, Effect(state.MissingEffects, "VehicleSprintCost").Value);
        Assert.All(state.Categories, row => Assert.Equal(50m, row.CompletionPercent));
    }

    [Fact]
    public void ConfirmedLevelZeroHasKnownZeroOwnedValuesAndTheFullMissingPotential()
    {
        var state = State(Pet("one", "varian", 0));
        Assert.All(state.OwnedEffects, row => Assert.Equal(0m, row.Value));
        Assert.Equal(.3m, Effect(state.MissingEffects, "VehicleSprintCost").Value);
        Assert.Equal(0m, state.Categories.Single(row => row.Genus == "varian").CompletionPercent);
    }

    [Fact]
    public void UnknownLevelNeverBecomesAConfirmedMissingPetOrInventsACompletionRate()
    {
        var state = State(Pet("one", "cogni", 1), Pet("unknown", "cogni", 0, known: false, current: false));
        Assert.All(state.OwnedEffects, row => Assert.Null(row.Value));
        Assert.All(state.MissingEffects, row => Assert.Null(row.Value));
        Assert.Equal(10m, Effect(state.OwnedEffects, "HPMax").KnownSubtotal);
        Assert.Equal(20m, Effect(state.MissingEffects, "HPMax").KnownSubtotal);
        var category = state.Categories.Single(row => row.Genus == "cogni");
        Assert.Null(category.CompletedStages); Assert.Null(category.CompletionPercent);
        Assert.Equal(6, category.MaximumStages); Assert.Equal(CombatKnowledgeStatus.Partial, category.Status);
    }

    [Fact]
    public void SavedAndManualLevelsRemainUsableWithExplicitSourceFlags()
    {
        var state = State(Pet("stored", "cogni", 3, current: false), Pet("manual", "fera", 2, current: false, manual: true));
        Assert.True(state.IsHistorical); Assert.True(state.IncludesManualValues); Assert.Equal(At, state.ObservedAt);
        Assert.Equal(30m, Effect(state.OwnedEffects, "HPMax").Value);
        Assert.Equal(2m, Effect(state.OwnedEffects, "GroundVehicleSpeed").Value);
    }

    [Fact]
    public void EmptyRosterAndUnsupportedLevelsNeverClaimZeroOrFullCompletion()
    {
        var empty = CombatPetsSnapshot.Empty;
        Assert.Equal(CombatKnowledgeStatus.Unknown, empty.Status); Assert.Empty(empty.OwnedEffects); Assert.Empty(empty.MissingEffects);
        Assert.Equal(new[] { "cogni", "fera", "natura", "varian" }, empty.Categories.Select(row => row.Genus));
        Assert.All(empty.Categories, row => { Assert.Null(row.CompletionPercent); Assert.Null(row.CompletedStages); });
        var unsupported = State(Pet("future", "fera", 4));
        Assert.All(unsupported.OwnedEffects, row => Assert.Null(row.Value));
        Assert.Null(unsupported.Categories.Single(row => row.Genus == "fera").CompletionPercent);
    }

    [Fact]
    public void SpecialAndUnknownGeneraDoNotChangeTheFourRequestedCategoriesOrTheirBonuses()
    {
        var first = State(Pet("a", "cogni", 3));
        var second = State(Pet("a", "cogni", 3), Pet("b", "special", 3), Pet("c", "future", 3));
        Assert.Equal(first.OwnedEffects, second.OwnedEffects); Assert.Equal(first.MissingEffects, second.MissingEffects);
        Assert.Equal(first.Categories, second.Categories);
        Assert.Throws<NotSupportedException>(() => ((IList<CombatPetEffectTotal>)second.OwnedEffects).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<CombatPetCategorySummary>)second.Categories).Clear());
    }

    [Theory]
    [InlineData("STR", "Points", "cogni")]
    [InlineData("Accuracy", "Points", "fera")]
    [InlineData("AGI", "Points", "natura")]
    [InlineData("VehicleSprintCost", "Percent", "varian")]
    [InlineData("VehicleSprintCost", "Points", null)]
    [InlineData("unknown", "Points", null)]
    public void MissingEffectNavigationUsesExactStatAndUnit(string stat, string unit, string? genus) =>
        Assert.Equal(genus, CombatPetsPresentation.GenusForStat(new(stat, unit)));
}
