using System.Text.Json;
using Soulcrest.Core.Pets;
using Soulcrest.Core.PetWindow;
using Soulcrest.Core.Progress;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Services;

public sealed record LoggedSoul(DateTimeOffset At, string PetId, int Quantity, string Source, string RawText);

/// <summary>
/// A map of the data package; MetersPerPixel is the game distance one map pixel covers (Altgard 0.996,
/// Ishalgen 1.99; null in older packages).
/// </summary>
public sealed record MapInfo(string Id, string Label, string Group, double? MetersPerPixel = null);

/// <summary>Pet catalog plus the per-server progress profile. Thread-safe; UI listens to Changed.</summary>
public sealed partial class ProgressService
{
    private readonly object _gate = new();
    private ProgressProfile _profile;
    private readonly ProgressProfile _storageProfile;
    private readonly string _progressPath;
    private bool _needsMigrationBackup;
    private NetworkProgressContext? _networkContext;
    private long _networkSequence = -1;
    private long _invalidatedGeneration = -1;
    private readonly List<LoggedSoul> _recent = [];

    public ProgressService() : this(AppPaths.ProgressFile) { }

    internal ProgressService(string path, PetCatalog? catalog = null)
    {
        _progressPath = path;
        MapDataDirectory = AppPaths.FindMapData();
        var mapCatalog = catalog ?? (MapDataDirectory is not null && File.Exists(Path.Combine(MapDataDirectory, "pets.json"))
            ? PetCatalog.Load(Path.Combine(MapDataDirectory, "pets.json")) : new PetCatalog([]));
        _mapPets = mapCatalog.Pets.ToList();
        _aliases = new Dictionary<string, string>(mapCatalog.Aliases, StringComparer.Ordinal);
        _learned = JsonFile.Load<List<PetDefinition>>(AppPaths.LearnedPetsFile);
        _learnedNames = JsonFile.Load<Dictionary<string, string>>(AppPaths.LearnedNamesFile);
        _storageProfile = JsonFile.Load<ProgressProfile>(_progressPath);
        _needsMigrationBackup = File.Exists(_progressPath) && _storageProfile.SchemaVersion < 2;
        _profile = _storageProfile.ActiveServerId is { } active && active != _storageProfile.ServerId
            && _storageProfile.Servers.TryGetValue(active, out var stored) ? stored : _storageProfile;
        foreach (var profile in _storageProfile.Servers.Values.Prepend(_storageProfile))
            foreach (var entry in profile.Pets.Values)
                if (entry.Freshness == ProgressFreshness.Fresh) entry.Freshness = ProgressFreshness.Stale;
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
        if (moved.Souls > existing.Souls)
        {
            existing.Source = moved.Source;
            existing.Freshness = moved.Freshness;
        }
        existing.Souls = Math.Max(existing.Souls, moved.Souls);
        existing.ManualOverride |= moved.ManualOverride;
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
            entry.Source = source;
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
            entry.Source = "manual";
            entry.ManualOverride = true;
            entry.Freshness = ProgressFreshness.Unknown;
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
                entry.Source = "manual";
                entry.ManualOverride = true;
                entry.Freshness = ProgressFreshness.Unknown;
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
            try { JsonFile.Save(AppPaths.LearnedPetsFile, _learned); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { LogFile.Error("Netzwerk-Pet nicht gespeichert", error); }
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
    public string SoulCount(string petId) => IsMax(petId) ? "max" : $"{SoulsInLevel(petId)}/{NeededForNextLevel(petId)}";

    public string PetMapLabel(string petId) => IsMax(petId)
        ? UiText.F("St. {0} · max", Thresholds.MaxLevel)
        : UiText.F("St. {0} · {1}/{2}", Level(petId), SoulsInLevel(petId), NeededForNextLevel(petId));

    public string LevelText(string petId)
    {
        if (IsMax(petId)) return "max";
        var level = Level(petId);
        return level == 0 ? $"{SoulsInLevel(petId)}/{NeededForNextLevel(petId)}"
            : UiText.F("St. {0} · {1}/{2}", level, SoulsInLevel(petId), NeededForNextLevel(petId));
    }

    /// <summary>Acquired level and unspent balance stay independent; enough souls do not acquire a level.</summary>
    public int? ObservedLevel(string petId)
    {
        lock (_gate)
            return !EntrySnapshot(petId).ManualOverride && RawFor(petId) is { LevelObserved: true } raw ? raw.Level : null;
    }

    public int? AvailableSouls(string petId)
    {
        lock (_gate)
            return !EntrySnapshot(petId).ManualOverride && RawFor(petId) is { BalanceObserved: true } raw ? raw.AvailableSouls : null;
    }

    /// <summary>A partial network delta keeps the stored level seed until acquisition or a full snapshot arrives.</summary>
    public int Level(string petId)
    {
        lock (_gate)
            return !EntrySnapshot(petId).ManualOverride && RawFor(petId) is { } raw
                ? raw.Level : Thresholds.Level(Souls(petId));
    }
    public int SoulsInLevel(string petId)
    {
        lock (_gate)
        {
            // Confirmation and the stored balance are independent. A rejected packet must not
            // reconstruct spent souls from EarnedTotal, which intentionally never falls on spend.
            if (!EntrySnapshot(petId).ManualOverride && RawFor(petId) is { } raw) return raw.AvailableSouls;
            var level = Level(petId);
            // Legacy-only and manual values retain their existing cumulative-total display.
            return level >= Thresholds.MaxLevel ? 0 : Math.Max(0, Souls(petId) - Thresholds.TotalForLevel(level));
        }
    }
    public bool IsMax(string petId) => Level(petId) >= Thresholds.MaxLevel;
    public int NeededForNextLevel(string petId) => IsMax(petId) ? 0 : Thresholds.Steps[Level(petId)];

    public PetProgressEntry EntrySnapshot(string petId)
    {
        lock (_gate)
        {
            var entries = ProfileIds(CanonicalId(petId)).Select(id => _profile.Pets.GetValueOrDefault(id))
                .OfType<PetProgressEntry>().ToArray();
            var entry = entries.OrderByDescending(e => e.Souls).ThenByDescending(e => e.UpdatedAt).FirstOrDefault() ?? new();
            return new PetProgressEntry { Souls = entry.Souls, Focus = entries.Any(e => e.Focus), UpdatedAt = entry.UpdatedAt,
                Source = entry.Source, Freshness = entry.Freshness, ManualOverride = entries.Any(e => e.ManualOverride) };
        }
    }

    private NetworkPetProgress? RawFor(string petId) => SoulId(petId) is { } number ? _profile.NetworkPets.GetValueOrDefault(number) : null;

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

    public int? ActiveServerId { get { lock (_gate) return _profile.ServerId; } }
    public DateTimeOffset? NetworkBaselineAt { get { lock (_gate) return _profile.NetworkBaselineAt; } }

    /// <summary>Adopts the old server-wide collection once; character switches on that server share it.</summary>
    public bool BindNetworkContext(NetworkProgressContext context)
    {
        lock (_gate)
        {
            if (!ValidContext(context) || context.Generation <= _invalidatedGeneration || _networkContext is { } prior &&
                (context.Generation < prior.Generation || context.Generation == prior.Generation && context.Sequence < prior.Sequence)) return false;
            if (_networkContext is { } same && same.Generation == context.Generation &&
                same.CharacterId == context.CharacterId && same.ServerId == context.ServerId) return true;
            foreach (var entry in _profile.Pets.Values)
                if (entry.Freshness == ProgressFreshness.Fresh) entry.Freshness = ProgressFreshness.Stale;
            if (_storageProfile.ServerId is null) _storageProfile.ServerId = context.ServerId;
            _profile = _storageProfile.ServerId == context.ServerId ? _storageProfile :
                _storageProfile.Servers.TryGetValue(context.ServerId, out var profile) ? profile :
                _storageProfile.Servers[context.ServerId] = new ProgressProfile { ServerId = context.ServerId,
                    Server = context.ServerId.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            foreach (var raw in _profile.NetworkPets.Values) raw.HasBaseline = false;
            _storageProfile.ActiveServerId = context.ServerId;
            _networkContext = context;
            _networkSequence = -1;
            _recent.Clear();
            PersistNetwork();
        }
        SafeEvent.Raise(Changed, "Server-Fortschritt");
        return true;
    }

    private static bool ValidContext(NetworkProgressContext c) =>
        c.ServerId > 0 && c.CharacterId > 0 && c.Generation >= 0 && c.Sequence >= 0 && !string.IsNullOrWhiteSpace(c.Name);

    private bool AcceptContext(NetworkProgressContext c) => _networkContext is { } current &&
        current.Generation == c.Generation && current.ServerId == c.ServerId && current.CharacterId == c.CharacterId &&
        c.Sequence >= current.Sequence && c.Sequence > _networkSequence;

    public bool ApplyNetworkSnapshot(PetProgressSnapshot snapshot, NetworkProgressContext context)
    {
        if (snapshot.Levels.Any(p => p.Key <= 0 || p.Value is < 0 or > 3) ||
            snapshot.AvailableSouls.Any(p => p.Key <= 0 || p.Value < 0 || p.Value > int.MaxValue - Thresholds.Max)) return false;
        lock (_gate)
        {
            if (!AcceptContext(context)) return false;
            var numbers = snapshot.Levels.Keys.Union(snapshot.AvailableSouls.Keys)
                .Union(_profile.NetworkPets.Keys).Union(VerifiedPets.Select(p => SoulId(p.Id)).OfType<int>()).ToArray();
            foreach (var number in numbers)
            {
                var level = snapshot.Levels.GetValueOrDefault(number);
                var available = snapshot.AvailableSouls.GetValueOrDefault(number);
                var total = Thresholds.TotalForLevel(level) + available;
                _profile.NetworkPets[number] = new NetworkPetProgress { Level = level, AvailableSouls = available,
                    EarnedTotal = total, HasBaseline = true, LevelObserved = true, BalanceObserved = true };
                if (ResolveNetworkPet(number) is not { } id) continue;
                var entry = EntryForWrite(id);
                if (!entry.ManualOverride)
                {
                    entry.Souls = total;
                    entry.Source = "network-snapshot";
                    entry.UpdatedAt = context.At;
                    entry.Freshness = ProgressFreshness.Fresh;
                }
            }
            _profile.NetworkBaselineAt = context.At;
            _networkSequence = context.Sequence;
            PersistNetwork();
        }
        SafeEvent.Raise(Changed, "Pet-Netzwerkstand");
        return true;
    }

    /// <summary>Absolute acquisition updates raw level only: spending may arrive before or after it.</summary>
    public bool ApplyNetworkAcquisition(PetAcquisition acquisition, NetworkProgressContext context)
    {
        if (acquisition.EntityKey != context.Identity.EntityKey || acquisition.PetNumber <= 0 || acquisition.Level is < 0 or > 3) return false;
        lock (_gate)
        {
            if (!AcceptContext(context)) return false;
            var raw = NetworkEntry(acquisition.PetNumber);
            if (raw.LevelObserved && acquisition.Level < raw.Level) return false;
            raw.Level = acquisition.Level;
            raw.LevelObserved = true;
            raw.EarnedTotal = Math.Max(raw.EarnedTotal, Thresholds.TotalForLevel(raw.Level));
            WriteNetworkTotal(acquisition.PetNumber, raw, context);
            _networkSequence = context.Sequence;
            PersistNetwork();
        }
        SafeEvent.Raise(Changed, "Pet-Netzwerkstand");
        return true;
    }

    /// <summary>An own-bound successful result supplies the absolute level; its spending remains a separate message.</summary>
    public bool ApplyNetworkUpgrade(PetUpgradeResult upgrade, NetworkProgressContext context)
    {
        if (upgrade.Result != 0 || upgrade.PetNumber <= 0 || upgrade.Level is < 1 or > 3) return false;
        lock (_gate)
        {
            if (!AcceptContext(context)) return false;
            var raw = NetworkEntry(upgrade.PetNumber);
            if (raw.LevelObserved && upgrade.Level < raw.Level) return false;
            raw.Level = upgrade.Level;
            raw.LevelObserved = true;
            raw.EarnedTotal = Math.Max(raw.EarnedTotal, Thresholds.TotalForLevel(raw.Level));
            WriteNetworkTotal(upgrade.PetNumber, raw, context);
            _networkSequence = context.Sequence;
            PersistNetwork();
        }
        SafeEvent.Raise(Changed, "Pet-Netzwerkstand");
        return true;
    }

    public bool ApplyNetworkSpent(IReadOnlyList<SoulGain> spent, NetworkProgressContext context) =>
        ApplyNetworkPoints(spent, context, gain: false);

    /// <summary>Logs accepted pickups once; the capture dispatcher owns session events.</summary>
    public bool ApplyNetworkGain(IReadOnlyList<SoulGain> gains, NetworkProgressContext context) =>
        ApplyNetworkPoints(gains, context, gain: true);

    private bool ApplyNetworkPoints(IReadOnlyList<SoulGain> points, NetworkProgressContext context, bool gain)
    {
        if (points.Any(p => p.PetNumber <= 0 || p.Souls < 0)) return false;
        var totals = points.GroupBy(p => p.PetNumber).ToDictionary(g => g.Key, g => g.Sum(p => (long)p.Souls));
        lock (_gate)
        {
            if (!AcceptContext(context)) return false;
            if (totals.Any(p => p.Value > int.MaxValue || gain &&
                ((_profile.NetworkPets.GetValueOrDefault(p.Key)?.EarnedTotal ?? 0) > int.MaxValue - p.Value ||
                 (_profile.NetworkPets.GetValueOrDefault(p.Key)?.AvailableSouls ?? 0) > int.MaxValue - p.Value ||
                 ResolveNetworkPet(p.Key) is { } id && Souls(id) > int.MaxValue - p.Value))) return false;
            foreach (var (number, amount) in totals)
            {
                var raw = NetworkEntry(number);
                raw.AvailableSouls = gain ? checked(raw.AvailableSouls + (int)amount) : Math.Max(0, raw.AvailableSouls - (int)amount);
                if (gain)
                {
                    raw.EarnedTotal += (int)amount;
                    if (ResolveNetworkPet(number) is { } id && EntryForWrite(id) is { ManualOverride: true } manual)
                    {
                        manual.Souls += (int)amount;
                        manual.UpdatedAt = context.At;
                        manual.Source = "manual+network";
                    }
                    else WriteNetworkTotal(number, raw, context);
                }
            }
            _networkSequence = context.Sequence;
            if (gain)
            {
                var logged = points.Where(p => p.Souls > 0).Select(p => new LoggedSoul(context.At,
                    ResolveNetworkPet(p.PetNumber) ?? p.PetNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    p.Souls, "network", $"pet:{p.PetNumber};generation:{context.Generation};sequence:{context.Sequence}")).ToArray();
                foreach (var row in logged) _recent.Insert(0, row);
                if (_recent.Count > 200) _recent.RemoveRange(200, _recent.Count - 200);
                try
                {
                    Persist();
                    var eventPath = Path.Combine(Path.GetDirectoryName(_progressPath)!, "events.jsonl");
                    if (logged.Length > 0) JsonFile.Retry(() => File.AppendAllText(eventPath, string.Concat(logged.Select(row => JsonSerializer.Serialize(row) + Environment.NewLine))));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { LogFile.Error("Netzwerk-Souls nicht gespeichert", error); }
            }
            else PersistNetwork();
        }
        SafeEvent.Raise(Changed, "Pet-Netzwerkstand");
        return true;
    }

    private NetworkPetProgress NetworkEntry(int number)
    {
        if (_profile.NetworkPets.TryGetValue(number, out var raw)) return raw;
        var id = ResolveNetworkPet(number);
        var total = id is not null ? Souls(id) : 0;
        // Preserve the pre-delta legacy estimate separately from the growing earned total.
        // LevelObserved stays false until the game actually supplies an acquired level.
        raw = new NetworkPetProgress { Level = Thresholds.Level(total), EarnedTotal = total,
            AvailableSouls = Thresholds.SoulsInLevel(total) };
        _profile.NetworkPets[number] = raw;
        return raw;
    }

    private string? ResolveNetworkPet(int number)
    {
        var known = VerifiedPets.FirstOrDefault(p => SoulId(p.Id) == number);
        return known?.Id ?? (PetIdTable.Find(number) is { } source ? EnsureNetworkPet(source) : null);
    }

    private void WriteNetworkTotal(int number, NetworkPetProgress raw, NetworkProgressContext context)
    {
        if (ResolveNetworkPet(number) is not { } id) return;
        var entry = EntryForWrite(id);
        if (entry.ManualOverride) return;
        entry.Souls = raw.EarnedTotal;
        entry.UpdatedAt = context.At;
        entry.Source = "network";
        entry.Freshness = raw.HasBaseline ? ProgressFreshness.Fresh : ProgressFreshness.Unknown;
    }

    public void ClearManualOverride(string petId)
    {
        lock (_gate)
        {
            var entry = EntryForWrite(petId);
            entry.ManualOverride = false;
            if (SoulId(petId) is { } number && _profile.NetworkPets.TryGetValue(number, out var raw))
            {
                entry.Souls = raw.EarnedTotal;
                entry.Source = "network";
                entry.Freshness = ProgressFreshness.Stale;
            }
            Persist();
        }
        SafeEvent.Raise(Changed, "Pet-Netzwerkstand");
    }

    /// <summary>A rejected pet packet invalidates that source, not the own identity or later loot deltas.</summary>
    public void MarkNetworkPetSourceStale()
    {
        var changed = false;
        lock (_gate)
        {
            foreach (var entry in _profile.Pets.Values)
                if (!entry.ManualOverride && entry.Freshness == ProgressFreshness.Fresh)
                { entry.Freshness = ProgressFreshness.Stale; changed = true; }
            foreach (var raw in _profile.NetworkPets.Values)
            {
                changed |= raw.HasBaseline || raw.BalanceObserved || raw.LevelObserved;
                raw.HasBaseline = raw.BalanceObserved = raw.LevelObserved = false;
            }
            changed |= _profile.NetworkBaselineAt is not null;
            _profile.NetworkBaselineAt = null;
            if (changed) PersistNetwork();
        }
        if (changed) SafeEvent.Raise(Changed, "Pet-Netzwerkstand");
    }

    public void MarkNetworkStale()
    {
        lock (_gate)
        {
            foreach (var entry in _profile.Pets.Values)
                if (entry.Freshness == ProgressFreshness.Fresh) entry.Freshness = ProgressFreshness.Stale;
            foreach (var raw in _profile.NetworkPets.Values) raw.HasBaseline = false;
            if (_networkContext is { } current) _invalidatedGeneration = Math.Max(_invalidatedGeneration, current.Generation);
            _networkContext = null;
        }
        SafeEvent.Raise(Changed, "Pet-Netzwerkstand");
    }

    /// <summary>A locked profile must not abort the remaining messages of a captured packet.</summary>
    private void PersistNetwork()
    {
        try { Persist(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { LogFile.Error("Pet-Netzwerkstand nicht gespeichert", error); }
    }

    private void Persist()
    {
        if (_needsMigrationBackup)
        {
            ProgressMigrationBackup.Create(_progressPath);
            _needsMigrationBackup = false;
        }
        _storageProfile.SchemaVersion = 2;
        JsonFile.Save(_progressPath, _storageProfile);
    }
}


internal static class ProgressMigrationBackup
{
    /// <summary>Exact old bytes, unique path, no overwrite; only called immediately before a migration write.</summary>
    internal static void Create(string path)
    {
        if (File.Exists(path))
            JsonFile.Retry(() => File.Copy(path, path + ".pre-network-" + Guid.NewGuid().ToString("N") + ".bak", overwrite: false));
    }
}
