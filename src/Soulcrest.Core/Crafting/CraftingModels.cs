using System.Collections.Immutable;

namespace Soulcrest.Core.Crafting;

public sealed record CraftingItem
{
    public int Id { get; init; }
    public string NameEn { get; init; } = "";
    public string NameDe { get; init; } = "";
    public string? Icon { get; init; }
    public string? Rarity { get; init; }
    public bool? IsTradable { get; init; }
    public bool? IsEquipment { get; init; }
}

public sealed record CraftingIngredient
{
    public int ItemId { get; init; }
    public int Quantity { get; init; }
    public ImmutableArray<int> ReplacementItemIds { get; init; } = [];
}

public sealed record CraftingRecipe
{
    public int Id { get; init; }
    public string Profession { get; init; } = "";
    public string Subcategory { get; init; } = "";
    public string? Faction { get; init; }
    public int? RequiredProficiency { get; init; }
    public int OutputItemId { get; init; }
    public int OutputQuantity { get; init; }
    public ImmutableArray<CraftingIngredient> Ingredients { get; init; } = [];
    public int? ComboRecipeId { get; init; }
    public decimal? ComboActivationChance { get; init; }
    public ImmutableArray<decimal>? SuccessRates { get; init; }
    public ImmutableArray<int> TransferItemIds { get; init; } = [];
    public decimal? CraftingFee { get; init; }
    public bool MechanicsQualified { get; init; }
    public bool? IsTransferCrafting { get; init; }
}

public sealed record CraftingNpcOffer
{
    public int ItemId { get; init; }
    public decimal UnitPrice { get; init; }
    public string? Faction { get; init; }
    public int BundleQuantity { get; init; } = 1;
    public bool ConditionsQualified { get; init; }
}

public enum CraftingAcquisition { Buy, Craft }
public enum CraftingPriceSource { Exchange, Npc, Manual, ExchangeAverage }
public sealed record CraftingPrice(int ItemId, decimal UnitPrice, CraftingPriceSource Source,
    DateTimeOffset? ObservedAt = null, int? ServerId = null, int BundleQuantity = 1)
{
    public bool IsMarket => Source is CraftingPriceSource.Exchange or CraftingPriceSource.ExchangeAverage;
    public DateTimeOffset? AverageRangeStart { get; init; }
    public int? AverageSamples { get; init; }
}

/// <summary>A replacement selection is scoped to one ingredient of one recipe.</summary>
public readonly record struct CraftingIngredientKey(int RecipeId, int IngredientItemId);

public sealed record CraftingPlanRequest
{
    public int RecipeId { get; init; }
    public int? TargetItemId { get; init; }
    /// <summary>Exact newly crafted COMBO target quantity. Null preserves the normal scenario.</summary>
    public int? TargetOutputQuantity { get; init; }
    /// <summary>Fixed number of attempts of the starting recipe, including failures and automatic COMBO rolls.</summary>
    public int TargetAttempts { get; init; } = 1;
    public string? Faction { get; init; }
    public ImmutableDictionary<string, int> ProficiencyLevels { get; init; } = ImmutableDictionary<string, int>.Empty;
    public ImmutableDictionary<int, CraftingAcquisition> Acquisition { get; init; } = ImmutableDictionary<int, CraftingAcquisition>.Empty;
    public ImmutableDictionary<int, int> RecipeChoices { get; init; } = ImmutableDictionary<int, int>.Empty;
    public ImmutableDictionary<CraftingIngredientKey, int> ReplacementChoices { get; init; } = ImmutableDictionary<CraftingIngredientKey, int>.Empty;
    public ImmutableDictionary<int, long> OwnedItems { get; init; } = ImmutableDictionary<int, long>.Empty;
    public ImmutableDictionary<int, CraftingPrice> BuyPrices { get; init; } = ImmutableDictionary<int, CraftingPrice>.Empty;
    public ImmutableDictionary<int, CraftingPrice> SellPrices { get; init; } = ImmutableDictionary<int, CraftingPrice>.Empty;
    public decimal SalesTaxPercent { get; init; } = 10m;
    public decimal RegistrationFeePercent { get; init; } = 2m;
    public int Seed { get; init; } = 20261110;
    public int Samples { get; init; } = 100_000;
    public int MaximumAttemptsPerSample { get; init; } = 100_000;
    public long MaximumTotalAttempts { get; init; } = 50_000_000;
}

public enum CraftingPlanStatus { Complete, Unknown, Invalid, LimitExceeded }
public sealed record CraftingPlanIssue(string Code, int? ItemId = null, int? RecipeId = null);
public sealed record CraftingShoppingRow(int ItemId, decimal ExpectedQuantity, decimal UnitPrice,
    decimal ExpectedCost, CraftingPriceSource Source);
public sealed record CraftingStepResult(int RecipeId, decimal ExpectedAttempts, decimal ExpectedSuccesses,
    decimal ExpectedFailures, decimal SuccessChance, decimal? ComboActivationChance);
public sealed record CraftingOutputResult(int ItemId, decimal ExpectedQuantity, decimal? NetValue);

public sealed record CraftingPlanResult
{
    public CraftingPlanStatus Status { get; init; }
    public ImmutableArray<CraftingPlanIssue> Issues { get; init; } = [];
    public int Samples { get; init; }
    public int Seed { get; init; }
    public decimal? ExpectedCost { get; init; }
    public decimal? P90Cost { get; init; }
    public decimal? ExpectedNetRevenue { get; init; }
    public CraftingOutputResult? TargetOutput { get; init; }
    public decimal? ExpectedProfit { get; init; }
    public decimal? ExpectedFees { get; init; }
    public ImmutableArray<CraftingShoppingRow> Shopping { get; init; } = [];
    public ImmutableArray<CraftingStepResult> Steps { get; init; } = [];
    public ImmutableArray<CraftingOutputResult> Outputs { get; init; } = [];
    public ImmutableArray<CraftingOutputResult> Byproducts { get; init; } = [];
}
