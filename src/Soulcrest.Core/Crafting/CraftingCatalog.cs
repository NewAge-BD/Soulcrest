using System.Collections.Immutable;
using System.Text.Json;

namespace Soulcrest.Core.Crafting;

/// <summary>Own offline metadata. A readable recipe is not automatically a qualified economic model.</summary>
public sealed class CraftingCatalog
{
    private readonly ImmutableDictionary<int, CraftingItem> _items;
    private readonly ImmutableDictionary<int, CraftingRecipe> _recipes;
    private readonly ImmutableDictionary<int, CraftingRecipe> _comboParents;
    private static readonly Lazy<CraftingCatalog> Bundled = new(() =>
    {
        using var stream = typeof(CraftingCatalog).Assembly.GetManifestResourceStream("Soulcrest.CraftingCatalog.json")
            ?? throw new InvalidDataException("Crafting catalog resource is missing.");
        using var reader = new StreamReader(stream);
        return FromJson(reader.ReadToEnd());
    });

    public static CraftingCatalog Default => Bundled.Value;
    public string MetadataBuild { get; }
    public ImmutableArray<CraftingItem> Items { get; }
    public ImmutableArray<CraftingRecipe> Recipes { get; }
    public ImmutableArray<CraftingNpcOffer> NpcOffers { get; }
    public static ImmutableArray<string> Professions { get; } =
        ["Blacksmithing", "Armorsmithing", "Handicrafting", "Alchemy", "Cooking"];

    public CraftingCatalog(string metadataBuild, IEnumerable<CraftingItem> items,
        IEnumerable<CraftingRecipe> recipes, IEnumerable<CraftingNpcOffer>? npcOffers = null)
    {
        MetadataBuild = metadataBuild;
        Items = items.ToImmutableArray(); Recipes = recipes.ToImmutableArray();
        NpcOffers = npcOffers?.ToImmutableArray() ?? [];
        if (string.IsNullOrWhiteSpace(metadataBuild) || metadataBuild.Length > 128 || Items.Length > 50_000 || Recipes.Length > 20_000
            || Items.Any(item => item is null || item.Id <= 0 || string.IsNullOrWhiteSpace(item.NameEn) || string.IsNullOrWhiteSpace(item.NameDe)
                || item.Icon is { } icon && (icon.Contains(':') || icon.StartsWith('/') || icon.StartsWith('\\')
                    || icon.Split('/', '\\').Any(part => part == "..")))
            || Items.Select(item => item.Id).Distinct().Count() != Items.Length
            || Recipes.Any(recipe => recipe is null || recipe.Id <= 0) || Recipes.Select(recipe => recipe.Id).Distinct().Count() != Recipes.Length)
            throw new InvalidDataException("Invalid crafting catalog identity.");
        _items = Items.ToImmutableDictionary(item => item.Id);
        _recipes = Recipes.ToImmutableDictionary(recipe => recipe.Id);
        foreach (var recipe in Recipes)
        {
            if (!Professions.Contains(recipe.Profession) || string.IsNullOrWhiteSpace(recipe.Subcategory)
                || !_items.ContainsKey(recipe.OutputItemId) || recipe.OutputQuantity is <= 0 or > 1_000_000
                || recipe.RequiredProficiency is < 0 or > 100 || recipe.Ingredients.IsDefault
                || recipe.Ingredients.Any(row => row is null)
                || recipe.Ingredients.Select(row => row.ItemId).Distinct().Count() != recipe.Ingredients.Length
                || recipe.Ingredients.Any(row => row.Quantity is <= 0 or > 1_000_000 || !_items.ContainsKey(row.ItemId)
                    || row.ReplacementItemIds.IsDefault || row.ReplacementItemIds.Any(id => !_items.ContainsKey(id) || id == row.ItemId)
                    || row.ReplacementItemIds.Distinct().Count() != row.ReplacementItemIds.Length)
                || recipe.ComboRecipeId is { } combo && (!_recipes.ContainsKey(combo) || combo == recipe.Id)
                || recipe.ComboActivationChance is < 0 or > 1 || recipe.CraftingFee < 0
                || recipe.SuccessRates is { } rates && (rates.IsDefaultOrEmpty || rates.Any(rate => rate is < 0 or > 1))
                || recipe.TransferItemIds.IsDefault || recipe.TransferItemIds.Any(id => !recipe.Ingredients.Any(row => row.ItemId == id || row.ReplacementItemIds.Contains(id))
                    || _items[id].IsEquipment == false)
                || recipe.IsTransferCrafting == true && _items[recipe.OutputItemId].IsEquipment == false)
                throw new InvalidDataException("Invalid crafting recipe.");
            if (recipe.MechanicsQualified && (recipe.SuccessRates is null || recipe.CraftingFee is null
                || recipe.ComboRecipeId is not null && recipe.ComboActivationChance is null))
                throw new InvalidDataException("Qualified crafting recipe lacks required mechanics.");
        }
        var parents = Recipes.Where(recipe => recipe.ComboRecipeId is not null).ToArray();
        if (parents.Select(recipe => recipe.ComboRecipeId).Distinct().Count() != parents.Length)
            throw new InvalidDataException("Ambiguous crafting combo parent.");
        _comboParents = parents.ToImmutableDictionary(recipe => recipe.ComboRecipeId!.Value);
        if (Recipes.Any(recipe => recipe.MechanicsQualified && _comboParents.ContainsKey(recipe.Id)
            && (recipe.Ingredients.Length != 0 || recipe.CraftingFee != 0)))
            throw new InvalidDataException("Qualified combo recipe must reuse the base attempt without additional ingredients or fees.");
        foreach (var recipe in Recipes)
        {
            var visited = new HashSet<int>(); var current = recipe;
            while (current.ComboRecipeId is { } next)
            {
                if (!visited.Add(current.Id)) throw new InvalidDataException("Cyclic combo recipes.");
                current = _recipes[next];
            }
        }
        if (NpcOffers.Any(offer => offer is null || !_items.ContainsKey(offer.ItemId) || offer.UnitPrice < 0 || offer.BundleQuantity <= 0))
            throw new InvalidDataException("Invalid crafting NPC offer.");
    }

    public CraftingItem? FindItem(int id) => _items.GetValueOrDefault(id);
    public CraftingRecipe? FindRecipe(int id) => _recipes.GetValueOrDefault(id);
    public ImmutableArray<CraftingRecipe> RecipesForItem(int id) => Recipes.Where(recipe => recipe.OutputItemId == id).ToImmutableArray();
    public static bool IsFactionCompatible(string? recipeFaction, string? faction)
    {
        static string? Normalize(string? value) => value?.ToLowerInvariant() switch
        {
            null or "" or "none" or "both" => null,
            "light" or "elyos" => "elyos",
            "dark" or "asmodian" or "asmodians" => "asmodians",
            var other => other
        };
        return Normalize(recipeFaction) is not { } required || required == Normalize(faction);
    }
    public bool IsComboRecipe(int id) => _comboParents.ContainsKey(id);
    public CraftingRecipe? FindBaseRecipe(int id)
    {
        var recipe = FindRecipe(id);
        while (recipe is not null && _comboParents.TryGetValue(recipe.Id, out var parent)) recipe = parent;
        return recipe;
    }

    public bool TrySuccessChance(CraftingRecipe recipe, IReadOnlyDictionary<string, int> levels, out decimal chance)
    {
        chance = 0;
        if (!recipe.MechanicsQualified || recipe.SuccessRates is not { } rates || rates.IsDefaultOrEmpty) return false;
        var delta = 0;
        if (recipe.RequiredProficiency is { } required)
        {
            if (!levels.TryGetValue(recipe.Profession, out var level) || level < required || level is < 0 or > 100) return false;
            delta = level - required;
        }
        if (delta >= rates.Length) return false;
        chance = rates[delta]; return true;
    }

    public static CraftingCatalog FromJson(string json)
    {
        if (json.Length > 32 * 1024 * 1024) throw new InvalidDataException("Crafting catalog is too large.");
        try
        {
            using var document = JsonDocument.Parse(json, new() { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.GetProperty("schema").GetInt32() != 1) throw new InvalidDataException("Unknown crafting catalog schema.");
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var build = root.GetProperty("metadataBuild");
            return new(build.ValueKind == JsonValueKind.String ? build.GetString()! : build.GetRawText(),
                root.GetProperty("items").Deserialize<ImmutableArray<CraftingItem>>(options),
                root.GetProperty("recipes").Deserialize<ImmutableArray<CraftingRecipe>>(options),
                root.TryGetProperty("npcOffers", out var offers) ? offers.Deserialize<ImmutableArray<CraftingNpcOffer>>(options) : []);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or FormatException)
        { throw new InvalidDataException("Invalid crafting catalog JSON.", error); }
    }
}
