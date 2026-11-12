using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Soulcrest.Core.Crafting;

namespace Soulcrest.App.Services;

public sealed record CraftingScenarioStorageState(bool Available, bool IsReadOnly, bool IsDirty,
    DateTimeOffset? SavedAt, string? Error)
{
    public static CraftingScenarioStorageState Unavailable { get; } = new(false, false, false, null, null);
}

public sealed record CraftingScenarioReplacement(int RecipeId, int IngredientId, int SelectedId);

/// <summary>Manual preferences only. Quantity deliberately does not depend on the calculation request's API name.</summary>
public sealed record CraftingScenarioDraft
{
    public string Profession { get; init; } = "Blacksmithing";
    public string Subcategory { get; init; } = "Greatsword";
    public string Query { get; init; } = "";
    public int? SelectedRecipeId { get; init; }
    public int? TargetItemId { get; init; }
    public int Quantity { get; init; } = 1;
    public int SimulationSamples { get; init; } = 1000;
    public ImmutableDictionary<string, int> SimulatedLevels { get; init; } = ImmutableDictionary<string, int>.Empty;
    public ImmutableDictionary<int, CraftingAcquisition> Acquisition { get; init; } = ImmutableDictionary<int, CraftingAcquisition>.Empty;
    public ImmutableDictionary<int, int> RecipeChoices { get; init; } = ImmutableDictionary<int, int>.Empty;
    public ImmutableArray<CraftingScenarioReplacement> ReplacementChoices { get; init; } = [];
    public ImmutableDictionary<int, long> OwnedItems { get; init; } = ImmutableDictionary<int, long>.Empty;

    public static CraftingScenarioDraft Capture(CraftingCalculatorSnapshot state, int quantity) => new()
    {
        Profession = state.Profession, Subcategory = state.Subcategory, Query = state.Query,
        SelectedRecipeId = state.SelectedRecipeId, TargetItemId = state.TargetItemId, Quantity = quantity,
        SimulationSamples = state.SimulationSamples,
        SimulatedLevels = state.SimulatedLevels, Acquisition = state.Acquisition, RecipeChoices = state.RecipeChoices,
        ReplacementChoices = state.ReplacementChoices.OrderBy(row => row.Key.RecipeId).ThenBy(row => row.Key.IngredientItemId)
            .Select(row => new CraftingScenarioReplacement(row.Key.RecipeId, row.Key.IngredientItemId, row.Value)).ToImmutableArray(),
        OwnedItems = state.OwnedItems
    };

    public CraftingScenarioDraft Sanitize(CraftingCatalog catalog, string? faction)
    {
        var knownBrowser = CraftingCalculatorPresentation.BrowserCategories.FirstOrDefault(row => row.Id == Profession);
        var browser = knownBrowser ?? CraftingCalculatorPresentation.BrowserCategories[0];
        var slot = browser.Subcategories.Contains(Subcategory) ? Subcategory : browser.Subcategories[0];
        var recipe = SelectedRecipeId is { } selected ? catalog.FindRecipe(selected) : null;
        if (recipe is not null && !CraftingCalculatorPresentation.MatchesFaction(recipe.Faction, faction))
        {
            var opposite = recipe;
            recipe = catalog.Recipes.Where(row => row.OutputItemId == opposite.OutputItemId
                && row.Profession == opposite.Profession && row.Subcategory == opposite.Subcategory
                && TransferCraftingRules.IsTransfer(catalog, row) == TransferCraftingRules.IsTransfer(catalog, opposite)
                && CraftingCalculatorPresentation.MatchesFaction(row.Faction, faction))
                .OrderBy(row => catalog.IsComboRecipe(row.Id) == catalog.IsComboRecipe(opposite.Id) ? 0 : 1)
                .ThenBy(row => row.RequiredProficiency).ThenBy(row => row.Id).FirstOrDefault();
        }
        if (recipe is null && SelectedRecipeId is not null)
            recipe = catalog.Recipes.Where(row => !catalog.IsComboRecipe(row.Id)
                && CraftingCalculatorPresentation.MatchesBrowser(catalog, row, browser.Id, slot)
                && CraftingCalculatorPresentation.MatchesFaction(row.Faction, faction))
                .OrderBy(row => row.RequiredProficiency).ThenBy(row => row.Id).FirstOrDefault();
        var routes = RecipeChoices.Where(row => catalog.FindItem(row.Key) is not null
            && catalog.FindRecipe(row.Value) is { } producer && producer.OutputItemId == row.Key
            && CraftingCalculatorPresentation.MatchesFaction(producer.Faction, faction)).ToImmutableDictionary();
        var acquisition = Acquisition.Where(row => catalog.FindItem(row.Key) is not null && Enum.IsDefined(row.Value)
            && (row.Value != CraftingAcquisition.Craft || routes.ContainsKey(row.Key))).ToImmutableDictionary();
        var replacements = ReplacementChoices.Where(row => catalog.FindRecipe(row.RecipeId) is { } owner
            && CraftingCalculatorPresentation.MatchesFaction(owner.Faction, faction)
            && owner.Ingredients.Any(ingredient => ingredient.ItemId == row.IngredientId
                && (row.SelectedId == ingredient.ItemId || ingredient.ReplacementItemIds.Contains(row.SelectedId))))
            .DistinctBy(row => (row.RecipeId, row.IngredientId)).ToImmutableArray();
        var retainSearchContext = !string.IsNullOrEmpty(Query) && knownBrowser is not null && browser.Subcategories.Contains(Subcategory);
        return this with
        {
            Profession = recipe is null || retainSearchContext
                ? browser.Id : TransferCraftingRules.IsTransfer(catalog, recipe) ? CraftingCalculatorPresentation.TransferCategory : recipe.Profession,
            Subcategory = recipe is null || retainSearchContext ? slot : recipe.Subcategory,
            Query = (Query ?? "")[..Math.Min(Query?.Length ?? 0, 160)],
            SelectedRecipeId = recipe?.Id, TargetItemId = recipe?.OutputItemId,
            Quantity = Quantity is >= 1 and <= 100_000 ? Quantity : 1,
            SimulationSamples = SimulationSamples is >= 1 and <= 100_000 ? SimulationSamples : 1000,
            SimulatedLevels = SimulatedLevels.Where(row => CraftingCatalog.Professions.Contains(row.Key)
                && row.Value is >= 0 and <= 100).ToImmutableDictionary(),
            Acquisition = acquisition, RecipeChoices = routes, ReplacementChoices = replacements,
            OwnedItems = OwnedItems.Where(row => catalog.FindItem(row.Key) is not null && row.Value is > 0 and <= 1_000_000_000)
                .ToImmutableDictionary(row => row.Key, row => recipe is not null && TransferCraftingRules.IsTransfer(catalog, recipe)
                    && TransferCraftingRules.IsEquipment(catalog, row.Key) ? Math.Min(1, row.Value) : row.Value)
        };
    }

    /// <summary>Leaves authority, source levels, prices, results and the quantity API entirely to the caller.</summary>
    public CraftingCalculatorSnapshot RestoreInto(CraftingCalculatorSnapshot state) => state with
    {
        Profession = Profession, Subcategory = Subcategory, Query = Query, SelectedRecipeId = SelectedRecipeId,
        TargetItemId = TargetItemId, SimulationSamples = SimulationSamples, SimulatedLevels = SimulatedLevels, Acquisition = Acquisition,
        RecipeChoices = RecipeChoices, ReplacementChoices = ReplacementChoices.ToImmutableDictionary(
            row => new CraftingIngredientKey(row.RecipeId, row.IngredientId), row => row.SelectedId), OwnedItems = OwnedItems
    };

    internal bool SameAs(CraftingScenarioDraft other) => Profession == other.Profession && Subcategory == other.Subcategory
        && Query == other.Query && SelectedRecipeId == other.SelectedRecipeId && TargetItemId == other.TargetItemId && Quantity == other.Quantity
        && SimulationSamples == other.SimulationSamples
        && Same(SimulatedLevels, other.SimulatedLevels) && Same(Acquisition, other.Acquisition) && Same(RecipeChoices, other.RecipeChoices)
        && ReplacementChoices.SequenceEqual(other.ReplacementChoices) && Same(OwnedItems, other.OwnedItems);
    private static bool Same<TKey, TValue>(ImmutableDictionary<TKey, TValue> left, ImmutableDictionary<TKey, TValue> right) where TKey : notnull =>
        left.Count == right.Count && left.All(row => right.TryGetValue(row.Key, out var value) && EqualityComparer<TValue>.Default.Equals(row.Value, value));
}

/// <summary>One serial, debounced, atomic manual-scenario store. Deleted profiles cannot be recreated by delayed writes.</summary>
public sealed class CraftingScenarioStore : IDisposable
{
    public const string LoadError = "Die Crafting-Auswahl konnte nicht geladen werden. Die vorhandene Datei bleibt unverändert.";
    public const string SaveError = "Die Crafting-Auswahl konnte nicht gespeichert werden.";
    private const int MaximumBytes = 8 * 1024 * 1024, MaximumProfiles = 512, MaximumEntries = 1024;
    private static readonly JsonSerializerOptions Options = new() { MaxDepth = 20, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly Action<string, object> _save;
    private readonly ITimer _debounce;
    private readonly HashSet<string> _deleted = new(StringComparer.Ordinal);
    private ImmutableDictionary<string, CraftingScenarioDraft> _profiles = ImmutableDictionary<string, CraftingScenarioDraft>.Empty;
    private long _revision, _savedRevision;
    private bool _readOnly, _disposed, _capacityError;
    private DateTimeOffset? _savedAt;
    private string? _error;
    public event Action? Changed;
    public CraftingScenarioStorageState State { get { lock (_gate) return StateLocked(); } }
    public CraftingScenarioStore() : this(Path.Combine(AppPaths.DataDirectory, "crafting-scenarios.json")) { }
    public CraftingScenarioStore(string path, TimeProvider? time = null) : this(path, time, static (file, value) => JsonFile.Save(file, value)) { }
    internal CraftingScenarioStore(string path, TimeProvider? time, Action<string, object> save)
    {
        _path = Path.GetFullPath(path); _time = time ?? TimeProvider.System; _save = save;
        _debounce = _time.CreateTimer(_ => { _ = FlushAsync(); }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        if (!File.Exists(_path)) return;
        try
        {
            using var stream = File.OpenRead(_path);
            if (stream.Length > MaximumBytes) throw new InvalidDataException();
            using var document = JsonDocument.Parse(stream, new() { MaxDepth = 20 });
            RejectDuplicates(document.RootElement);
            var envelope = document.Deserialize<Envelope>(Options);
            if (envelope is not { SchemaVersion: 1, Profiles: not null } || envelope.Profiles.Length > MaximumProfiles)
                throw new InvalidDataException();
            var profiles = _profiles.ToBuilder();
            foreach (var row in envelope.Profiles)
            {
                if (row is null || Normalize(row.ProfileId) is not { } id || profiles.ContainsKey(id) || !Valid(row.Draft))
                    throw new InvalidDataException();
                profiles.Add(id, row.Draft);
            }
            _profiles = profiles.ToImmutable(); _savedAt = envelope.SavedAt;
        }
        catch (Exception error) when (StorageError(error)) { _readOnly = true; _error = LoadError; }
    }

    public CraftingScenarioDraft? Read(string? profileId)
    {
        var id = Normalize(profileId);
        lock (_gate) return id is not null && !_deleted.Contains(id) ? _profiles.GetValueOrDefault(id) : null;
    }
    public void Stage(string? profileId, CraftingScenarioDraft draft)
    {
        if (Normalize(profileId) is not { } id || !Valid(draft)) return;
        lock (_gate)
        {
            if (_disposed || _deleted.Contains(id) || _profiles.TryGetValue(id, out var old) && old.SameAs(draft)) return;
            if (!_profiles.ContainsKey(id) && _profiles.Count >= MaximumProfiles) { _capacityError = true; }
            else
            {
                _profiles = _profiles.SetItem(id, draft); _revision++;
                if (!_readOnly) _debounce.Change(TimeSpan.FromMilliseconds(500), Timeout.InfiniteTimeSpan);
            }
        }
        Raise();
    }
    public void DeleteProfile(string profileId)
    {
        if (Normalize(profileId) is not { } id) return;
        lock (_gate)
        {
            if (_disposed) return;
            _deleted.Add(id);
            if (_profiles.ContainsKey(id)) { _profiles = _profiles.Remove(id); _revision++; }
            if (_profiles.Count < MaximumProfiles) _capacityError = false;
            if (!_readOnly) _debounce.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
        Raise();
    }
    public void Flush() => FlushAsync().GetAwaiter().GetResult();
    public async Task FlushAsync()
    {
        await _writer.WaitAsync().ConfigureAwait(false);
        try
        {
            while (true)
            {
                Envelope envelope; long revision;
                lock (_gate)
                {
                    if (_readOnly || _savedRevision == _revision) return;
                    revision = _revision;
                    envelope = new(1, _time.GetUtcNow(), _profiles.OrderBy(row => row.Key, StringComparer.Ordinal)
                        .Select(row => new SavedProfile(row.Key, row.Value)).ToArray());
                }
                try
                {
                    if (JsonSerializer.SerializeToUtf8Bytes(envelope).Length > MaximumBytes) throw new InvalidDataException();
                    // File IO never runs inside the service/network or the store's state lock.
                    await Task.Run(() => _save(_path, envelope)).ConfigureAwait(false);
                    lock (_gate) { _savedRevision = revision; _savedAt = envelope.SavedAt; _error = null; }
                }
                catch (Exception error) when (StorageError(error))
                { lock (_gate) _error = SaveError; Raise(); return; }
                Raise();
            }
        }
        finally { _writer.Release(); }
    }
    private CraftingScenarioStorageState StateLocked() => new(true, _readOnly, _revision != _savedRevision, _savedAt,
        _error ?? (_capacityError ? SaveError : null));
    private void Raise() => SafeEvent.Raise(Changed, "Crafting-Auswahl");
    private static string? Normalize(string? id) => Guid.TryParse(id, out var value) && value != Guid.Empty ? value.ToString("N") : null;
    private static bool Valid(CraftingScenarioDraft? draft) => draft is not null && draft.Profession is { Length: <= 80 }
        && draft.Subcategory is { Length: <= 80 } && draft.Query is { Length: <= 160 }
        && draft.SimulatedLevels is { Count: <= MaximumEntries } && draft.Acquisition is { Count: <= MaximumEntries }
        && draft.RecipeChoices is { Count: <= MaximumEntries } && draft.OwnedItems is { Count: <= MaximumEntries }
        && !draft.ReplacementChoices.IsDefault && draft.ReplacementChoices.Length <= MaximumEntries
        && draft.ReplacementChoices.All(row => row is not null)
        && draft.ReplacementChoices.Select(row => (row.RecipeId, row.IngredientId)).Distinct().Count() == draft.ReplacementChoices.Length;
    private static bool StorageError(Exception error) => error is IOException or UnauthorizedAccessException or JsonException
        or InvalidDataException or ArgumentException or NotSupportedException;
    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            { if (!keys.Add(property.Name)) throw new InvalidDataException(); RejectDuplicates(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var row in element.EnumerateArray()) RejectDuplicates(row);
    }
    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; _debounce.Dispose(); }
        Flush();
    }
    private sealed record SavedProfile(string ProfileId, CraftingScenarioDraft Draft);
    private sealed record Envelope(int SchemaVersion, DateTimeOffset SavedAt, SavedProfile[] Profiles);
}
