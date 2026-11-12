using System.Collections.Immutable;
using System.Globalization;
using Soulcrest.Core.Crafting;

namespace Soulcrest.App.Services;

public sealed record CraftingProfession(string Id, byte Category, ImmutableArray<string> Subcategories);
public sealed record CraftingSourceSelection(int ItemId, int? RecipeId);
public sealed record CraftingReplacementSelection(int RecipeId, int IngredientId, int SelectedId);
public sealed record CraftingLevelSelection(string Profession, int? Level);
public sealed record CraftingOwnershipSelection(int ItemId, bool Owned);
public sealed record CraftingOwnedQuantitySelection(int ItemId, long Quantity);

/// <summary>Product taxonomy requested on 2026-11-10; independent of native enum spelling.</summary>
public static class CraftingCalculatorPresentation
{
    public static ImmutableArray<CraftingProfession> Professions { get; } =
    [
        new("Blacksmithing", 1, ["Greatsword", "Longsword", "Dagger", "Mace", "Guard", "Materials"]),
        new("Armorsmithing", 2, ["Helm", "Pauldrons", "Top", "Legs", "Gloves", "Shoes", "Cloak", "Materials"]),
        new("Handicrafting", 3, ["Bow", "Staff", "Necklace", "Earrings", "Ring", "Bracelet", "Materials"]),
        new("Alchemy", 4, ["Spellbook", "Orb", "Potion", "Scroll", "Manastone", "Materials"]),
        new("Cooking", 5, ["Food"])
    ];

    public const string TransferCategory = "Transfercrafting";
    public static ImmutableArray<CraftingProfession> BrowserCategories { get; } = Professions.Add(
        new(TransferCategory, 0, ["Greatsword", "Longsword", "Dagger", "Mace", "Guard", "Bow", "Staff", "Spellbook", "Orb",
            "Helm", "Pauldrons", "Top", "Legs", "Gloves", "Shoes", "Cloak", "Necklace", "Earrings", "Ring"]));
    public static bool MatchesBrowser(CraftingCatalog catalog, CraftingRecipe recipe, string category, string subcategory) =>
        category == TransferCategory
            ? TransferCraftingRules.IsTransfer(catalog, recipe) && recipe.Subcategory == subcategory
            : !TransferCraftingRules.IsTransfer(catalog, recipe) && recipe.Profession == category && recipe.Subcategory == subcategory;

    public static bool MatchesFaction(string? recipeFaction, string? settingsFaction) =>
        string.IsNullOrWhiteSpace(settingsFaction) || CraftingCatalog.IsFactionCompatible(recipeFaction, settingsFaction);

    public static string Name(CraftingItem? item) => item is null ? "—"
        : UiText.Language == "de" && !string.IsNullOrWhiteSpace(item.NameDe) ? item.NameDe : item.NameEn;
    public static string Number(decimal? value, string format = "N0") => value is { } number
        ? number.ToString(format, CultureInfo.GetCultureInfo(UiText.Language == "de" ? "de-DE" : "en-US")) : "—";
    public static string Time(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"))
        .ToString("g", CultureInfo.GetCultureInfo(UiText.Language == "de" ? "de-DE" : "en-US"));
    public static string Chance(decimal? value) => value is { } chance ? Number(chance * 100, "0.000000") + " %" : "—";
    public static string ActivationChance(decimal? value) => value is { } chance ? Number(chance * 100, "0.##") + " %" : "—";
    public static bool IsComboEquipmentTarget(CraftingCatalog catalog, int? id) => id is { } recipeId
        && catalog.IsComboRecipe(recipeId) && catalog.FindRecipe(recipeId) is { } recipe
        && TransferCraftingRules.IsEquipment(catalog, recipe.OutputItemId);
    public static decimal? ComboActivationProbability(CraftingCatalog catalog, int? id)
    {
        if (id is not { } recipeId || !catalog.IsComboRecipe(recipeId)) return null;
        var current = catalog.FindBaseRecipe(recipeId); var chance = 1m;
        while (current is not null && current.Id != recipeId)
        {
            if (current.ComboActivationChance is not { } activation || current.ComboRecipeId is not { } next) return null;
            chance *= activation; current = catalog.FindRecipe(next);
        }
        return current is null ? null : chance;
    }
    public static string? ProficiencyHint(CraftingRecipe recipe, CraftingCalculatorSnapshot state)
    {
        var observed = state.ObservedLevels.TryGetValue(recipe.Profession, out var known);
        var level = observed ? known : state.Level(recipe.Profession);
        return level is { } value && recipe.RequiredProficiency is { } required && value < required
            ? UiText.F(observed ? "{0}: Charakterlevel {1}, benötigt {2}." : "{0}: Rechenlevel {1}, benötigt {2}.",
                recipe.Profession, value, required) : null;
    }
    public static ImmutableArray<string> ProficiencyWarnings(CraftingCalculatorSnapshot state) =>
        state.RequiredProficiencyLevels.OrderBy(row => row.Key).Select(row =>
        {
            var observed = state.ObservedLevels.TryGetValue(row.Key, out var known);
            var level = observed ? known : state.Level(row.Key);
            return level is { } value && value < row.Value
                ? UiText.F(observed ? "{0}: Charakterlevel {1}, benötigt {2}." : "{0}: Rechenlevel {1}, benötigt {2}.",
                    row.Key, value, row.Value) : null;
        }).OfType<string>().ToImmutableArray();
    public static string? ReferenceSimulationHint(CraftingCalculatorSnapshot state) => state.ReferenceLevels.IsEmpty ? null
        : UiText.F("Referenzsimulation mit Mindestlevels der gewählten Herstellungskette: {0}. Deine Charakterlevel bleiben unverändert.",
            string.Join(" · ", state.ReferenceLevels.OrderBy(row => row.Key).Select(row =>
                $"{row.Key} {state.Level(row.Key)} → {row.Value}")));
    public static decimal? SuccessChance(CraftingRecipe recipe, CraftingCalculatorSnapshot state)
    {
        if (recipe.SuccessRates is not { } rates || state.CalculationLevel(recipe.Profession) is not { } level
            || recipe.RequiredProficiency is not { } required || level < required || level - required >= rates.Length) return null;
        return rates[level - required];
    }
    public static string Source(CraftingPriceSource source) => source switch
    {
        CraftingPriceSource.Npc => UiText.T("NPC-Händler"),
        CraftingPriceSource.Exchange => "aion2.exchange",
        CraftingPriceSource.ExchangeAverage => UiText.T("aion2.exchange · 24h-Durchschnitt (Ersatzpreis)"),
        _ => UiText.T("Manueller Preis")
    };
    public static string Faction(string? faction) => faction?.ToLowerInvariant() switch
    {
        "light" or "elyos" => "Elyos",
        "dark" or "asmodian" or "asmodians" => "Asmodians",
        _ => UiText.T("Beide Fraktionen")
    };
    public static bool SameFaction(string? left, string? right) => left is null || right is null
        || Faction(left).Equals(Faction(right), StringComparison.OrdinalIgnoreCase);

    public static string Issue(CraftingPlanIssue issue) => UiText.T(issue.Code switch
    {
        "InputPriceMissing" => "Für eine Zutat fehlt ein Kaufpreis.",
        "TargetPriceMissing" => "Für das Zielprodukt fehlt ein Verkaufspreis.",
        "ProficiencyUnknown" or "ProficiencyMissing" => "Das Proficiency Level ist noch unbekannt. Trage ein Rechenlevel ein oder bestätige den eigenen Welteintritt.",
        "ProficiencyTooLow" => "Das Rechenlevel liegt unter der Proficiency-Anforderung dieses Rezepts.",
        "SuccessRateUnknown" or "SuccessRateUnavailable" or "ProbabilityUnqualified" => "Die Erfolgswahrscheinlichkeit ist für diesen Proficiency-Abstand noch nicht belegt.",
        "MechanicsUnqualified" => "Verbrauch oder Ergebnis dieses Rezepts ist noch nicht vollständig belegt.",
        "RecipeCycle" => "Die ausgewählte Herstellungskette enthält einen Kreis.",
        "TargetUnreachable" => "Das gewählte Zielprodukt ist mit diesem Plan nicht erreichbar.",
        "FactionUnavailable" or "MarketMismatch" => "Rezept und gewählte Marktfraktion passen nicht zusammen.",
        "SimulationLimit" => "Die Berechnung hat ihre Grenze erreicht. Reduziere die Zielmenge oder kaufe ein Vorprodukt.",
        "TradeabilityUnqualified" => "Die Handelbarkeit dieses Items ist noch nicht belegt.",
        "TargetTradeabilityUnknown" => "Die Handelbarkeit dieses Items ist noch nicht belegt.",
        "TargetNotTradable" => "Dieses Zielprodukt ist nicht am Markt handelbar. Ein Marktprofit ist deshalb nicht berechenbar.",
        "NpcConditionsUnqualified" => "Die Einkaufsbedingungen des NPC-Angebots sind nicht vollständig belegt.",
        "ComboChanceMissing" => "Die COMBO-Aktivierungschance ist für dieses Rezept noch unbekannt.",
        "NumericLimit" => "Die Mengen oder Kosten überschreiten den unterstützten Zahlenbereich.",
        "TransferGearSelectionMissing" => "Wähle das Ausgangsteil für diesen Transferschritt aus.",
        "CraftRouteAmbiguous" => "Wähle den genauen Herstellungsweg für dieses Vorprodukt aus.",
        "CraftRouteMissing" => "Für dieses Ausrüstungsteil ist kein Herstellungsweg belegt. Es muss bereits vorhanden sein.",
        "TransferRecipeRequired" => "Wähle ein Transferrezept für die Kostenrechnung aus.",
        _ => "Die Rechnung ist noch nicht vollständig. Prüfe Level, Zutatenquellen und Preise."
    });
}

public sealed record CraftingCalculatorSnapshot
{
    public string Profession { get; init; } = "Blacksmithing";
    public string Subcategory { get; init; } = "Greatsword";
    public string Query { get; init; } = "";
    public int SimulationSamples { get; init; } = 1000;
    public int? SelectedRecipeId { get; init; }
    public int? TargetItemId { get; init; }
    public int TargetQuantity { get; init; } = 1;
    public string? ProfileId { get; init; }
    public bool ProficiencyFresh { get; init; }
    public bool UsingSavedProficiency { get; init; }
    public DateTimeOffset? ProficiencyObservedAt { get; init; }
    public ImmutableDictionary<string, int> ObservedLevels { get; init; } = ImmutableDictionary<string, int>.Empty;
    public ImmutableDictionary<string, int> SimulatedLevels { get; init; } = ImmutableDictionary<string, int>.Empty;
    public ImmutableDictionary<string, int> ReferenceLevels { get; init; } = ImmutableDictionary<string, int>.Empty;
    public ImmutableDictionary<string, int> RequiredProficiencyLevels { get; init; } = ImmutableDictionary<string, int>.Empty;
    public CraftingScenarioStorageState ScenarioStorage { get; init; } = CraftingScenarioStorageState.Unavailable;
    public ImmutableDictionary<int, CraftingAcquisition> Acquisition { get; init; } = ImmutableDictionary<int, CraftingAcquisition>.Empty;
    public ImmutableDictionary<int, int> RecipeChoices { get; init; } = ImmutableDictionary<int, int>.Empty;
    public ImmutableDictionary<CraftingIngredientKey, int> ReplacementChoices { get; init; } = ImmutableDictionary<CraftingIngredientKey, int>.Empty;
    public ImmutableDictionary<int, long> OwnedItems { get; init; } = ImmutableDictionary<int, long>.Empty;
    public ImmutableDictionary<int, CraftingPrice> BuyPrices { get; init; } = ImmutableDictionary<int, CraftingPrice>.Empty;
    public ImmutableDictionary<int, CraftingPrice> SellPrices { get; init; } = ImmutableDictionary<int, CraftingPrice>.Empty;
    public ImmutableArray<ExchangeMarketServer> Servers { get; init; } = [];
    public string? ServerName { get; init; }
    public string? Faction { get; init; }
    public decimal SalesTaxPercent { get; init; } = 10m;
    public decimal RegistrationFeePercent { get; init; } = 2m;
    public bool Busy { get; init; }
    public bool IsError { get; init; }
    public CraftingCalculationFailure? PriceRefreshFailure { get; init; }
    public CraftingCalculationFailure? CalculationFailure { get; init; }
    public string Status { get; init; } = "Wähle ein Rezept für die Kalkulation.";
    public CraftingPlanResult? Result { get; init; }
    public DateTimeOffset? CalculatedAt { get; init; }
    public int RequiredPriceCount { get; init; }
    public long Revision { get; init; }
    public int? CalculationLevel(string profession) => ReferenceLevels.TryGetValue(profession, out var reference) ? reference : Level(profession);
    public int? Level(string profession) => SimulatedLevels.TryGetValue(profession, out var simulated) ? simulated
        : ObservedLevels.TryGetValue(profession, out var observed) ? observed : null;
}
