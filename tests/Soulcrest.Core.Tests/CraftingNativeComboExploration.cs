using System.Collections.Immutable;
using System.Diagnostics;
using Soulcrest.Core.Crafting;
using Xunit;
using Xunit.Abstractions;

namespace Soulcrest.Core.Tests;

/// <summary>Explicit offline native-chain proof. All prices and levels are anonymous manual calculation inputs.</summary>
public sealed class CraftingNativeComboExploration(ITestOutputHelper output)
{
    [DiagnosticFact]
    [Trait("Category", "Diagnostic")]
    public void CompleteOneHundredThousandNativeGearComboChainsWithinTheQualifiedRollBudget()
    {
        var catalog = CraftingCatalog.Default;
        var target = catalog.FindRecipe(141023001)!;
        var routes = new[] { 141024001, 141025001, 141026001 }
            .ToImmutableDictionary(id => catalog.FindRecipe(id)!.OutputItemId, id => id);
        var timer = Stopwatch.StartNew();
        var result = new CraftingOutputTargetCalculator(catalog).Calculate(new()
        {
            RecipeId = target.Id, TargetItemId = target.OutputItemId, TargetOutputQuantity = 1, Faction = "Asmodians",
            ProficiencyLevels = ImmutableDictionary<string, int>.Empty.Add("Blacksmithing", 55),
            Acquisition = routes.Keys.ToImmutableDictionary(id => id, _ => CraftingAcquisition.Craft), RecipeChoices = routes,
            BuyPrices = catalog.Items.Where(item => item.IsEquipment == false).ToImmutableDictionary(item => item.Id,
                item => new CraftingPrice(item.Id, 100m, CraftingPriceSource.Manual)),
            SellPrices = ImmutableDictionary<int, CraftingPrice>.Empty.Add(target.OutputItemId,
                new(target.OutputItemId, 50_000m, CraftingPriceSource.Manual)),
            Samples = 100_000, MaximumTotalAttempts = 200_000_000
        });
        timer.Stop();
        Assert.Equal(CraftingPlanStatus.Complete, result.Status);
        Assert.Equal(100_000, result.Samples); Assert.Equal(1m, result.TargetOutput!.ExpectedQuantity);
        Assert.InRange(result.ExpectedCost!.Value, 1_000_000m, 1_130_000m);
        Assert.Equal(44_000m, result.TargetOutput.NetValue);
        Assert.Equal(44_000m - result.ExpectedCost, result.ExpectedProfit);
        Assert.DoesNotContain(result.Shopping, row => catalog.FindItem(row.ItemId)!.IsEquipment == true);
        Assert.All(routes.Values, id => Assert.Contains(result.Steps, step => step.RecipeId == id));
        var totalRolls = result.Steps.Sum(step => step.ExpectedAttempts) * result.Samples;
        Assert.InRange(totalRolls, 50_000_001m, 200_000_000m);
        output.WriteLine("Native COMBO chain: samples={0}; cost={1}; profit={2}; rolls={3}; seconds={4:F1}",
            result.Samples, result.ExpectedCost, result.ExpectedProfit, totalRolls, timer.Elapsed.TotalSeconds);
    }
}
