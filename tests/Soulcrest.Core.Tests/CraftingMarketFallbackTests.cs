using System.Collections.Immutable;
using Soulcrest.Core.Crafting;
using Xunit;

namespace Soulcrest.Core.Tests;

public sealed class CraftingMarketFallbackTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 10, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void VerifiedAverageUsesTheSameGrossPurchaseAndNetSaleFeeRules()
    {
        var result = Calculate(Average(1, 100), Average(2, 10));
        Assert.Equal(CraftingPlanStatus.Complete, result.Status);
        Assert.Equal(10m, result.ExpectedCost); Assert.Equal(88m, result.ExpectedProfit + result.ExpectedCost);
        Assert.Equal(12m, result.ExpectedFees); Assert.Equal(78m, result.ExpectedProfit);
        Assert.Equal(CraftingPriceSource.ExchangeAverage, Assert.Single(result.Shopping).Source);
    }

    [Theory]
    [InlineData("server")] [InlineData("window")] [InlineData("samples")] [InlineData("timestamp")] [InlineData("zero")]
    public void UnqualifiedOrMismatchedAverageCannotProduceProfit(string variant)
    {
        var price = Average(2, 10);
        price = variant switch
        {
            "server" => price with { ServerId = 11302 },
            "window" => price with { AverageRangeStart = At.AddHours(-23) },
            "samples" => price with { AverageSamples = 0 },
            "timestamp" => price with { ObservedAt = null },
            _ => price with { UnitPrice = 0 }
        };
        var result = Calculate(Average(1, 100), price);
        Assert.Equal(CraftingPlanStatus.Unknown, result.Status); Assert.Null(result.ExpectedProfit);
    }

    private static CraftingPrice Average(int item, decimal price) => new(item, price, CraftingPriceSource.ExchangeAverage, At, 11301)
    { AverageRangeStart = At.AddHours(-24), AverageSamples = 24 };
    private static CraftingPlanResult Calculate(CraftingPrice sale, CraftingPrice buy)
    {
        var catalog = new CraftingCatalog("synthetic", [
            new() { Id = 1, NameEn = "Synthetic product", NameDe = "Synthetisches Produkt", IsTradable = true },
            new() { Id = 2, NameEn = "Synthetic input", NameDe = "Synthetische Zutat", IsTradable = true }], [
            new() { Id = 1, Profession = "Blacksmithing", Subcategory = "Greatsword", RequiredProficiency = 1,
                OutputItemId = 1, OutputQuantity = 1, Ingredients = [new() { ItemId = 2, Quantity = 1 }],
                SuccessRates = [1m], CraftingFee = 0, MechanicsQualified = true }]);
        return new CraftingCalculator(catalog).Calculate(new()
        {
            RecipeId = 1, Samples = 1,
            ProficiencyLevels = ImmutableDictionary<string, int>.Empty.Add("Blacksmithing", 1),
            BuyPrices = ImmutableDictionary<int, CraftingPrice>.Empty.Add(2, buy),
            SellPrices = ImmutableDictionary<int, CraftingPrice>.Empty.Add(1, sale)
        });
    }
}
