using System.Text.Json;
using Soulcrest.Core.Pets;
using Soulcrest.Core.PetWindow;
using Soulcrest.Core.Progress;

namespace Soulcrest.App.Services;

public sealed record LoggedSoul(DateTimeOffset At, string PetId, int Quantity, string Source, string RawText);

/// <summary>
/// A map of the data package; MetersPerPixel is the game distance one map pixel covers (Altgard 0.996,
/// Ishalgen 1.99; null in older packages).
/// </summary>
public sealed record MapInfo(string Id, string Label, string Group, double? MetersPerPixel = null);

/// <summary>Pet catalog plus the per-server progress profile. Thread-safe; UI listens to Changed.</summary>
public sealed class ProgressService
{
    private readonly object _gate = new();
    private readonly ProgressProfile _profile;
    private readonly List<LoggedSoul> _recent = [];

    public ProgressService()
    {
        MapDataDirectory = AppPaths.FindMapData();
        var mapCatalog = MapDataDirectory is not null && File.Exists(Path.Combine(MapDataDirectory, "pets.json"))
            ? PetCatalog.Load(Path.Combine(MapDataDirectory, "pets.json")) : new PetCatalog([]);
        _mapPets = mapCatalog.Pets.ToList();
        _aliases = new Dictionary<string, string>(mapCatalog.Aliases, StringComparer.Ordinal);
        _learned = JsonFile.Load<List<PetDefinition>>(AppPaths.LearnedPetsFile);
        _learnedNames = JsonFile.Load<Dictionary<string, string>>(AppPaths.LearnedNamesFile);
        _profile = JsonFile.Load<ProgressProfile>(AppPaths.ProgressFile);
        RepairLearnedNames();
        Catalog = BuildCatalog();
        Maps = LoadMaps(MapDataDirectory);
    }

    private readonly List<PetDefinition> _mapPets;
    private readonly Dictionary<string, string> _aliases;
    private readonly List<PetDefinition> _learned;
    // Real names for placeholder pets ("Crestlich 01"), learned from the in-game pet window.
    private readonly Dictionary<string, string> _learnedNames;

    /// <summary>
    /// Map-data pets with their real names (shipped overrides, then names learned from the pet window),
    /// plus pets learned from the pet window that are not in the map data.
    /// </summary>
    private PetCatalog BuildCatalog()
    {
        var names = new Dictionary<string, string>(PetNameOverrides.Shipped, StringComparer.Ordinal);
        foreach (var (id, name) in _learnedNames)
            names[id] = name;
        var mapPets = PetNameOverrides.Apply(_mapPets, names).ToList();
        return new PetCatalog(mapPets.Concat(_learned.Where(l => PanelPetName.CanLearn(l.En) && mapPets.All(m => m.Id != l.Id))), _aliases);
    }

    /// <summary>Catalog pet still named after its icon (no override, nothing learned yet).</summary>
    public bool IsPlaceholder(string petId) => Catalog.Find(petId) is { } pet && PetNameOverrides.IsPlaceholder(pet);

    /// <summary>
    /// Gives a placeholder pet ("KrallWar 01 V01") the name the game shows for it (pet window panel).
    /// Ids stay the same, so progress is kept.
    /// </summary>
    public void NamePlaceholder(string petId, string name)
    {
        lock (_gate)
        {
            _learnedNames[petId] = PanelPetName.Clean(name);
            JsonFile.Save(AppPaths.LearnedNamesFile, _learnedNames);
            Catalog = BuildCatalog();
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// A pet learned from the pet window turned out to be a placeholder pet of the map data (same
    /// portrait): the placeholder gets the learned name, progress moves over (higher value wins), the
    /// learned duplicate is dropped.
    /// </summary>
    public void MergeLearnedIntoPlaceholder(string learnedId, string placeholderId)
    {
        lock (_gate)
        {
            if (_learned.FirstOrDefault(l => l.Id == learnedId) is not { } learned || _mapPets.All(m => m.Id != placeholderId))
                return;
            _learned.Remove(learned);
            _learnedNames[placeholderId] = learned.En;
            MergeProfile(learnedId, placeholderId);
            var oldPortrait = Path.Combine(AppPaths.LearnedPortraitsDirectory, $"{learnedId}.png");
            var newPortrait = Path.Combine(AppPaths.LearnedPortraitsDirectory, $"{placeholderId}.png");
            if (File.Exists(oldPortrait))
            {
                if (File.Exists(newPortrait))
                    File.Delete(oldPortrait);
                else
                    File.Move(oldPortrait, newPortrait);
            }
            JsonFile.Save(AppPaths.LearnedPetsFile, _learned);
            JsonFile.Save(AppPaths.LearnedNamesFile, _learnedNames);
            Persist();
            Catalog = BuildCatalog();
        }
        Changed?.Invoke();
    }

    public string? MapDataDirectory { get; }

    /// <summary>Map-data pets plus pets learned from the in-game pet window. Replaced when a pet is learned.</summary>
    public PetCatalog Catalog { get; private set; }

    public IReadOnlyList<PetDefinition> VerifiedPets => PetSoulIdentity.Verified(Catalog);

    public int? SoulId(string petId) => Catalog.Find(petId) is { } pet ? PetSoulIdentity.Resolve(pet, Catalog) : null;

    public bool IsLearned(string petId)
    {
        lock (_gate)
            return _learned.Any(p => p.Id == petId);
    }

    /// <summary>
    /// Adds a pet known only from the in-game pet window (name read from its info panel).
    /// Genus and spawns are unknown; the portrait is stored for recognition.
    /// </summary>
    public PetDefinition LearnPet(string name, byte[] portraitPng, string language = "en")
    {
        PetDefinition pet;
        lock (_gate)
        {
            name = PanelPetName.Clean(name);
            var id = PetCatalog.Slug(name);
            if (FindByName(name) is null && Catalog.Find(id) is null && !PanelPetName.CanLearn(name))
                throw new ArgumentException("Pet name is incomplete or identifies a panel label.", nameof(name));
            pet = FindByName(name) ?? Catalog.Find(id) ?? new PetDefinition { Id = id, En = name, De = language == "de" ? name : null, Genus = "unknown" };
            id = pet.Id;
            if (_mapPets.All(m => m.Id != id) && _learned.All(l => l.Id != id))
            {
                _learned.Add(pet);
                JsonFile.Save(AppPaths.LearnedPetsFile, _learned);
                Catalog = BuildCatalog();
            }
            SaveLearnedPortrait(id, portraitPng);
        }
        Changed?.Invoke();
        return pet;
    }

    /// <summary>A German name read twice for a confidently recognised learned pet keeps its existing ID.</summary>
    public void NameLearnedGerman(string petId, string name)
    {
        lock (_gate)
        {
            var index = _learned.FindIndex(p => p.Id == petId);
            if (index < 0 || !string.IsNullOrWhiteSpace(_learned[index].De) || PanelPetName.IsTruncated(name)) return;
            _learned[index] = _learned[index] with { De = PanelPetName.Clean(name) };
            JsonFile.Save(AppPaths.LearnedPetsFile, _learned);
            Catalog = BuildCatalog();
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Remembers a confirmed card portrait so the pet is recognised directly next time. Returns the path
    /// when it was written now (an existing portrait is kept).
    /// </summary>
    public static string? SaveLearnedPortrait(string petId, byte[] portraitPng)
    {
        Directory.CreateDirectory(AppPaths.LearnedPortraitsDirectory);
        var path = Path.Combine(AppPaths.LearnedPortraitsDirectory, $"{petId}.png");
        if (File.Exists(path))
            return null;
        File.WriteAllBytes(path, portraitPng);
        return path;
    }

    public static IEnumerable<(string PetId, string Path)> LearnedPortraits() =>
        Directory.Exists(AppPaths.LearnedPortraitsDirectory)
            ? Directory.GetFiles(AppPaths.LearnedPortraitsDirectory, "*.png").Select(p => (Path.GetFileNameWithoutExtension(p), p))
            : [];

    /// <summary>
    /// One portrait identity per catalog pet, including a legacy-file fallback. Loading a scan
    /// never needs to rename or discard stored alias portraits just to remove duplicate references.
    /// </summary>
    public IReadOnlyList<(string PetId, string Path)> RecognitionPortraits() => LearnedPortraits()
        .Where(p => Catalog.Find(p.PetId) is not null)
        .Select(p => (PetId: CanonicalId(p.PetId), p.Path, OriginalId: p.PetId))
        .OrderBy(p => p.PetId != p.OriginalId)
        .ThenBy(p => p.OriginalId, StringComparer.Ordinal)
        .DistinctBy(p => p.PetId)
        .Select(p => (p.PetId, p.Path)).ToArray();

    /// <summary>Finds a pet by its displayed name (English or German), tolerant to case and punctuation.</summary>
    public PetDefinition? FindByName(string name)
    {
        return PanelPetName.Resolve(name, Catalog.Pets);
    }

    /// <summary>
    /// Learned pets whose name was misread ("Dracunj Herbalist" = catalog "Dracuni Herbalist", "Red Spark
    /// Jgnus" = "Red Spark Ignus"): progress and portrait move to the right pet, the duplicate is dropped.
    /// Progress is merged by keeping the higher value; the tester backs up the data folder before each start.
    /// </summary>
    private void RepairLearnedNames()
    {
        var changed = false;
        foreach (var learned in _learned.ToList())
        {
            var target = PanelPetName.Resolve(learned.En, _mapPets);
            if (target is null)
            {
                var cleaned = PanelPetName.Clean(learned.En);
                var cleanedId = PetCatalog.Slug(cleaned);
                if (cleanedId == learned.Id)
                    continue;
                target = _learned.FirstOrDefault(l => l.Id == cleanedId) ?? new PetDefinition { Id = cleanedId, En = cleaned, Genus = learned.Genus };
                if (!_learned.Contains(target))
                    _learned.Add(target);
            }
            if (target.Id == learned.Id)
                continue;
            _learned.Remove(learned);
            MergeProfile(learned.Id, target.Id);
            var oldPortrait = Path.Combine(AppPaths.LearnedPortraitsDirectory, $"{learned.Id}.png");
            var newPortrait = Path.Combine(AppPaths.LearnedPortraitsDirectory, $"{target.Id}.png");
            if (File.Exists(oldPortrait))
            {
                if (File.Exists(newPortrait))
                    File.Delete(oldPortrait);
                else
                    File.Move(oldPortrait, newPortrait);
            }
            changed = true;
        }
        if (!changed)
            return;
        JsonFile.Save(AppPaths.LearnedPetsFile, _learned);
        Persist();
    }

    private bool MergeProfile(string alias, string target)
    {
        if (alias == target || !_profile.Pets.Remove(alias, out var moved)) return false;
        var existing = _profile.Get(target);
        // Alias snapshots describe the same collection: keep the greatest value, never add them.
        existing.Souls = Math.Max(existing.Souls, moved.Souls);
        existing.Focus |= moved.Focus;
        if (moved.UpdatedAt > existing.UpdatedAt) existing.UpdatedAt = moved.UpdatedAt;
        return true;
    }

    private string CanonicalId(string petId) => Catalog.Find(petId)?.Id ?? petId;

    private IEnumerable<string> ProfileIds(string canonicalId) =>
        _aliases.Where(pair => pair.Value == canonicalId).Select(pair => pair.Key).Prepend(canonicalId);

    /// <summary>Normalises this pet only when an explicit progress write is about to occur.</summary>
    private PetProgressEntry EntryForWrite(string petId)
    {
        var canonicalId = CanonicalId(petId);
        foreach (var alias in ProfileIds(canonicalId).Where(id => id != canonicalId))
            MergeProfile(alias, canonicalId);
        return _profile.Get(canonicalId);
    }

    /// <summary>Maps of the data package in manifest order (grouped Elyos, Asmodier, Abyss).</summary>
    public IReadOnlyList<MapInfo> Maps { get; }

    public string MapLabel(string id) => UiText.T(Maps.FirstOrDefault(m => m.Id == id)?.Label ?? id);

    private static List<MapInfo> LoadMaps(string? directory)
    {
        if (directory is null || !File.Exists(Path.Combine(directory, "manifest.json")))
            return [];
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "manifest.json")));
        return document.RootElement.GetProperty("maps").EnumerateArray()
            .Select(m => new MapInfo(
                m.GetProperty("id").GetString()!,
                m.GetProperty("label").GetString()!,
                m.TryGetProperty("group", out var group) ? group.GetString() ?? "" : "",
                m.TryGetProperty("metersPerPixel", out var meters) && meters.ValueKind == JsonValueKind.Number && meters.GetDouble() > 0 ? meters.GetDouble() : null))
            .ToList();
    }

    public SoulThresholds Thresholds => SoulThresholds.Default;

    public event Action? Changed;

    public int Souls(string petId)
    {
        lock (_gate)
            return ProfileIds(CanonicalId(petId)).Max(id => _profile.Pets.TryGetValue(id, out var entry) ? entry.Souls : 0);
    }

    public bool IsFocus(string petId)
    {
        lock (_gate)
            return ProfileIds(CanonicalId(petId)).Any(id => _profile.Pets.TryGetValue(id, out var entry) && entry.Focus);
    }

    public IReadOnlyList<PetDefinition> FocusPets()
    {
        lock (_gate)
            return Catalog.Pets.Where(p => IsFocus(p.Id)).ToList();
    }

    public IReadOnlyList<LoggedSoul> Recent()
    {
        lock (_gate)
            return _recent.ToList();
    }

    public void AddSouls(string petId, int quantity, string source, string rawText)
    {
        lock (_gate)
        {
            petId = CanonicalId(petId);
            var logged = new LoggedSoul(DateTimeOffset.Now, petId, quantity, source, rawText);
            var entry = EntryForWrite(petId);
            entry.Souls += quantity;
            entry.UpdatedAt = logged.At;
            _recent.Insert(0, logged);
            if (_recent.Count > 200)
                _recent.RemoveAt(_recent.Count - 1);
            // Runs on the network capture thread: a save that fails even after the retries is logged and
            // the soul stays counted in memory, so the next save writes it (review 2026-10-07).
            var line = JsonSerializer.Serialize(logged);
            try
            {
                Persist();
                Directory.CreateDirectory(AppPaths.DataDirectory);
                JsonFile.Retry(() => File.AppendAllText(AppPaths.EventsFile, line + Environment.NewLine));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                LogFile.Error($"Soul nicht gespeichert {line}", e);
            }
        }
        SafeEvent.Raise(Changed, "Soul-Fortschritt");
    }

    public void SetSouls(string petId, int souls)
    {
        lock (_gate)
        {
            var entry = EntryForWrite(petId);
            entry.Souls = Math.Max(0, souls);
            entry.UpdatedAt = DateTimeOffset.Now;
            Persist();
        }
        Changed?.Invoke();
    }

    /// <summary>One profile write and notification for a reviewed scan batch.</summary>
    public void SetLevels(IReadOnlyList<(string PetId, int Level, int InLevel)> values)
    {
        if (values.Count == 0) return;
        lock (_gate)
        {
            foreach (var (petId, level, inLevel) in values)
            {
                var entry = EntryForWrite(petId);
                entry.Souls = Thresholds.Total(level, inLevel);
                entry.UpdatedAt = DateTimeOffset.Now;
            }
            Persist();
        }
        Changed?.Invoke();
    }

    /// <summary>Register a pet from the bundled, sourced network ID table when first encountered.</summary>
    public string EnsureNetworkPet(Soulcrest.Core.Network.PetIdEntry source)
    {
        lock (_gate)
        {
            var id = source.CatalogId ?? FindByName(source.En)?.Id ?? PetCatalog.Slug(source.En);
            if (Catalog.Find(id) is { } known) return known.Id;
            _learned.Add(new PetDefinition { Id = id, En = source.En, Genus = source.Genus.ToLowerInvariant(),
                NumericId = source.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            JsonFile.Save(AppPaths.LearnedPetsFile, _learned);
            Catalog = BuildCatalog();
            return id;
        }
    }

    /// <summary>Game-style entry: reached level plus souls towards the next level.</summary>
    public void SetLevel(string petId, int level, int soulsInLevel) =>
        SetSouls(petId, Thresholds.Total(level, soulsInLevel));

    /// <summary>"St. 1 · 6/25" (level reached, souls towards the next level) or "St. 3 · max": beside the pet
    /// symbols on the interactive map and on the in-game map.</summary>
    /// <summary>Souls of the current level, "24/25", or "max": shown beside marked pet targets in game.</summary>
    public string SoulCount(string petId)
    {
        var total = Souls(petId);
        return Thresholds.IsMax(total) ? "max" : $"{Thresholds.SoulsInLevel(total)}/{Thresholds.NeededForNextLevel(total)}";
    }

    public string PetMapLabel(string petId)
    {
        var total = Souls(petId);
        return Thresholds.IsMax(total)
            ? UiText.F("St. {0} · max", Thresholds.MaxLevel)
            : UiText.F("St. {0} · {1}/{2}", Thresholds.Level(total), Thresholds.SoulsInLevel(total), Thresholds.NeededForNextLevel(total));
    }

    /// <summary>"Stufe 2 · 12/75" or "max" – shared by list, overlay and toasts.</summary>
    public string LevelText(string petId)
    {
        var total = Souls(petId);
        var level = Thresholds.Level(total);
        if (Thresholds.IsMax(total))
            return "max";
        return level == 0
            ? $"{Thresholds.SoulsInLevel(total)}/{Thresholds.NeededForNextLevel(total)}"
            : UiText.F("St. {0} · {1}/{2}", level, Thresholds.SoulsInLevel(total), Thresholds.NeededForNextLevel(total));
    }

    public void ToggleFocus(string petId)
    {
        lock (_gate)
        {
            var entry = EntryForWrite(petId);
            entry.Focus = !entry.Focus;
            Persist();
        }
        Changed?.Invoke();
    }

    private void Persist() => JsonFile.Save(AppPaths.ProgressFile, _profile);
}
