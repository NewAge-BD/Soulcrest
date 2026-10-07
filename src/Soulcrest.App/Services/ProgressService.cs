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
        _mapPets = MapDataDirectory is not null && File.Exists(Path.Combine(MapDataDirectory, "pets.json"))
            ? PetCatalog.Load(Path.Combine(MapDataDirectory, "pets.json")).Pets.ToList()
            : [];
        _learned = JsonFile.Load<List<PetDefinition>>(AppPaths.LearnedPetsFile);
        _learnedNames = JsonFile.Load<Dictionary<string, string>>(AppPaths.LearnedNamesFile);
        _profile = JsonFile.Load<ProgressProfile>(AppPaths.ProgressFile);
        RepairLearnedNames();
        Catalog = BuildCatalog();
        Maps = LoadMaps(MapDataDirectory);
    }

    private readonly List<PetDefinition> _mapPets;
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
        return new PetCatalog(mapPets.Concat(_learned.Where(l => mapPets.All(m => m.Id != l.Id))));
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
            if (_profile.Pets.Remove(learnedId, out var moved)
                && (!_profile.Pets.TryGetValue(placeholderId, out var existing) || existing.Souls < moved.Souls))
                _profile.Pets[placeholderId] = moved;
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

    public bool IsLearned(string petId)
    {
        lock (_gate)
            return _learned.Any(p => p.Id == petId);
    }

    /// <summary>
    /// Adds a pet known only from the in-game pet window (name read from its info panel).
    /// Genus and spawns are unknown; the portrait is stored for recognition.
    /// </summary>
    public PetDefinition LearnPet(string name, byte[] portraitPng)
    {
        PetDefinition pet;
        lock (_gate)
        {
            name = PanelPetName.Clean(name);
            var id = PetCatalog.Slug(name);
            pet = Catalog.Find(id) ?? new PetDefinition { Id = id, En = name, Genus = "unknown" };
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
            if (_profile.Pets.Remove(learned.Id, out var moved))
            {
                if (!_profile.Pets.TryGetValue(target.Id, out var existing) || existing.Souls < moved.Souls)
                    _profile.Pets[target.Id] = moved;
                else
                    existing.Focus |= moved.Focus;
            }
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
            return _profile.Pets.TryGetValue(petId, out var entry) ? entry.Souls : 0;
    }

    public bool IsFocus(string petId)
    {
        lock (_gate)
            return _profile.Pets.TryGetValue(petId, out var entry) && entry.Focus;
    }

    public IReadOnlyList<PetDefinition> FocusPets()
    {
        lock (_gate)
            return Catalog.Pets.Where(p => _profile.Pets.TryGetValue(p.Id, out var e) && e.Focus).ToList();
    }

    public IReadOnlyList<LoggedSoul> Recent()
    {
        lock (_gate)
            return _recent.ToList();
    }

    public void AddSouls(string petId, int quantity, string source, string rawText)
    {
        var logged = new LoggedSoul(DateTimeOffset.Now, petId, quantity, source, rawText);
        lock (_gate)
        {
            var entry = _profile.Get(petId);
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
            var entry = _profile.Get(petId);
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
                var entry = _profile.Get(petId);
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
            if (Catalog.Find(id) is not null) return id;
            _learned.Add(new PetDefinition { Id = id, En = source.En, Genus = source.Genus.ToLowerInvariant() });
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
            var entry = _profile.Get(petId);
            entry.Focus = !entry.Focus;
            Persist();
        }
        Changed?.Invoke();
    }

    private void Persist() => JsonFile.Save(AppPaths.ProgressFile, _profile);
}
