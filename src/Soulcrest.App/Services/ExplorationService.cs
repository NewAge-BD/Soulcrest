using System.Text.Json;
using System.Text.RegularExpressions;
using Soulcrest.Core.Network;
using Soulcrest.Core.Progress;

namespace Soulcrest.App.Services;

/// <summary>A sealed dungeon, stronghold or Kibelisk (Kind "dungeon", "stronghold", "kibelisk") at map position X/Y.</summary>
public sealed record ExplorationPlace(string Id, string Map, string Kind, int Marker, string En, string? De, double X = 0, double Y = 0,
    int? QuestId = null, int? TeleportId = null)
{
    public string Name(string language) => language == "de" && !string.IsNullOrWhiteSpace(De) ? De : En;
}
public enum ExplorationProgressState { Undiscovered, DiscoveredIncomplete, DiscoveredCompleted }

/// <summary>Three game states; null means no confirmed interpretation, independent of observation freshness.</summary>
public sealed record ExplorationPlaceProgress(ExplorationProgressState? State, ProgressFreshness Freshness,
    string Source, DateTimeOffset? ObservedAt, byte? QuestStatus = null, byte? QuestStage = null,
    bool? TeleportPresent = null, string? RawSource = null, DateTimeOffset? RawObservedAt = null,
    ProgressFreshness RawFreshness = ProgressFreshness.Unknown, bool ManualOverride = false,
    IReadOnlyList<int>? QuestCounters = null, long? QuestOrdinal = null)
{
    public static ExplorationPlaceProgress Unknown { get; } = new(null, ProgressFreshness.Unknown, "unknown", null);
}

public sealed class CharacterExploration
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Character 1";
    public HashSet<string> Done { get; set; } = [];
    public int? ServerId { get; set; }
    public long? NetworkCharacterId { get; set; }
    public Dictionary<string, ExplorationPlaceProgress> Progress { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<int, QuestProgressRecord> RawQuests { get; set; } = [];
    public HashSet<int> RawTeleportIds { get; set; } = [];
}
public sealed class ExplorationProfiles
{
    public int SchemaVersion { get; set; }
    public string Active { get; set; } = "";
    public List<CharacterExploration> Characters { get; set; } = [];
}

public sealed record ExplorationCharacterSnapshot(string CharacterId, IReadOnlySet<string> Completed)
{
    public IReadOnlyDictionary<string, ExplorationPlaceProgress> Progress { get; init; } = new Dictionary<string, ExplorationPlaceProgress>();
    public int? ServerId { get; init; }
    public long? NetworkCharacterId { get; init; }
}

public enum CharacterChoice { Unchanged, Selected, Adopted, Created }

/// <summary>Character-only exploration state. Pet progress remains server-wide.</summary>
public sealed partial class ExplorationService
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly ExplorationProfiles _state;
    private bool _needsMigrationBackup;
    private NetworkProgressContext? _networkContext;
    private string? _networkCharacter;
    private long _invalidatedGeneration = -1;
    private long _questSequence = -1, _teleportSequence = -1;
    public IReadOnlyList<ExplorationPlace> Places { get; }
    public event Action? Changed;
    public ExplorationService(ProgressService progress) : this(Path.Combine(AppPaths.DataDirectory, "exploration.json"), LoadPlaces(progress)) { }
    internal ExplorationService(string path, IReadOnlyList<ExplorationPlace> places)
    {
        _path = path; Places = places; _state = JsonFile.Load<ExplorationProfiles>(path);
        _needsMigrationBackup = File.Exists(path) && _state.SchemaVersion < 2;
        if (_state.Characters.Count == 0) _state.Characters.Add(new());
        if (!_state.Characters.Any(c => c.Id == _state.Active)) _state.Active = _state.Characters[0].Id;
        foreach (var c in _state.Characters)
            foreach (var (old, now) in Renamed)
                if (Places.Any(p => p.Id == now))
                {
                    if (c.Done.Remove(old)) c.Done.Add(now);
                    if (c.Progress.Remove(old, out var moved)) c.Progress.TryAdd(now, moved);
                }
        foreach (var c in _state.Characters)
        {
            foreach (var id in c.Done)
                c.Progress.TryAdd(id, new(ExplorationProgressState.DiscoveredCompleted, ProgressFreshness.Unknown, "legacy", null));
            foreach (var (id, entry) in c.Progress.ToArray())
                c.Progress[id] = entry with { Freshness = entry.Freshness == ProgressFreshness.Fresh ? ProgressFreshness.Stale : entry.Freshness,
                    RawFreshness = entry.RawFreshness == ProgressFreshness.Fresh ? ProgressFreshness.Stale : entry.RawFreshness };
            RefreshDone(c);
        }
    }

    /// <summary>
    /// Ids from before the import named the Altgard Kibelisk at 5407/3467 "Shulak Street Stall" (user screenshots
    /// 2026-10-06): it was "#1" of the two "Steel Hammer Temporary Trading Post", the other one was "#2".
    /// </summary>
    private static readonly (string Old, string New)[] Renamed =
    [
        ("altgard|kibelisk|Steel Hammer Temporary Trading Post#1", "altgard|kibelisk|Shulak Street Stall"),
        ("altgard|kibelisk|Steel Hammer Temporary Trading Post#2", "altgard|kibelisk|Steel Hammer Temporary Trading Post"),
    ];
    public string ActiveId { get { lock (_gate) return _state.Active; } }
    public IReadOnlyList<(string Id, string Name)> Characters { get { lock (_gate) return _state.Characters.Select(c => (c.Id, c.Name)).ToArray(); } }
    public HashSet<string> Completed { get { lock (_gate) return new(_state.Characters.Single(c => c.Id == _state.Active).Done); } }
    /// <summary>Atomic character/progress pair for UI handlers (user request 2026-10-11).</summary>
    public ExplorationCharacterSnapshot Snapshot()
    {
        lock (_gate)
        {
            var character = _state.Characters.Single(c => c.Id == _state.Active);
            return new(_state.Active, new HashSet<string>(character.Done))
            {
                Progress = Places.Select(p => p.Id).Distinct().ToDictionary(id => id,
                    id => Detach(character.Progress.GetValueOrDefault(id) ?? ExplorationPlaceProgress.Unknown), StringComparer.Ordinal),
                ServerId = character.ServerId, NetworkCharacterId = character.NetworkCharacterId,
            };
        }
    }
    public void Select(string id)
    {
        lock (_gate) { if (!_state.Characters.Any(c => c.Id == id)) return; _state.Active = id; Persist(); }
        Changed?.Invoke();
    }
    public void Add(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        lock (_gate) { var c = new CharacterExploration { Name = name.Trim() }; _state.Characters.Add(c); _state.Active = c.Id; Persist(); }
        Changed?.Invoke();
    }
    /// <summary>
    /// The character detected after a loading screen (user request 2026-10-06): its profile is chosen.
    /// An unknown name takes over the oldest never-renamed default profile ("Character 1"), so progress
    /// checked off before the detection stays; otherwise a new profile is made.
    /// </summary>
    public CharacterChoice UseCharacter(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return CharacterChoice.Unchanged;
        CharacterChoice choice;
        lock (_gate)
        {
            var known = _state.Characters.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (known is not null)
            {
                if (known.Id == _state.Active) return CharacterChoice.Unchanged;
                _state.Active = known.Id;
                choice = CharacterChoice.Selected;
            }
            else if (_state.Characters.FirstOrDefault(c => DefaultName().IsMatch(c.Name)) is { } unnamed)
            {
                unnamed.Name = name;
                _state.Active = unnamed.Id;
                choice = CharacterChoice.Adopted;
            }
            else
            {
                var created = new CharacterExploration { Name = name };
                _state.Characters.Add(created);
                _state.Active = created.Id;
                choice = CharacterChoice.Created;
            }
            Persist();
        }
        Changed?.Invoke();
        return choice;
    }

    [GeneratedRegex(@"^Character \d+$")]
    private static partial Regex DefaultName();

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        lock (_gate) { _state.Characters.Single(c => c.Id == _state.Active).Name = name.Trim(); Persist(); }
        Changed?.Invoke();
    }
    /// <summary>Compatibility action: unchecking a discovered place does not make it undiscovered.</summary>
    public void SetDone(string character, IEnumerable<string> ids, bool done = true) =>
        SetState(character, ids, done ? ExplorationProgressState.DiscoveredCompleted : ExplorationProgressState.DiscoveredIncomplete);

    public void SetState(string character, IEnumerable<string> ids, ExplorationProgressState? state)
    {
        if (state is { } value && !Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(state));
        var changed = false;
        lock (_gate)
        {
            var c = _state.Characters.SingleOrDefault(c => c.Id == character);
            if (c is null) return;
            foreach (var id in ids.Distinct().Where(id => Places.Any(p => p.Id == id)))
            {
                var old = c.Progress.GetValueOrDefault(id) ?? ExplorationPlaceProgress.Unknown;
                if (old.State == state && old.ManualOverride && old.Source == "manual") continue;
                c.Progress[id] = old with { State = state, Freshness = ProgressFreshness.Unknown,
                    Source = "manual", ObservedAt = DateTimeOffset.UtcNow, ManualOverride = true };
                changed = true;
            }
            if (changed) { RefreshDone(c); Persist(); }
        }
        if (changed) SafeEvent.Raise(Changed, "Erkundungsfortschritt");
    }

    /// <summary>Reuse an unbound named legacy profile only when unambiguous; server and numeric id own all future updates.</summary>
    public CharacterChoice BindNetworkContext(NetworkProgressContext context)
    {
        if (context.CharacterId <= 0 || context.ServerId <= 0 || string.IsNullOrWhiteSpace(context.Name) ||
            context.Generation <= _invalidatedGeneration || context.Sequence < 0) return CharacterChoice.Unchanged;
        CharacterChoice choice;
        lock (_gate)
        {
            if (context.Generation <= _invalidatedGeneration) return CharacterChoice.Unchanged;
            if (_networkContext is { } prior && (context.Generation < prior.Generation ||
                context.Generation == prior.Generation && context.Sequence < prior.Sequence)) return CharacterChoice.Unchanged;
            if (_networkContext is { } same && same.Generation == context.Generation &&
                same.CharacterId == context.CharacterId && same.ServerId == context.ServerId) return CharacterChoice.Unchanged;
            StaleCurrent();
            var matches = _state.Characters.Where(c => c.NetworkCharacterId == context.CharacterId && c.ServerId == context.ServerId).ToArray();
            var named = _state.Characters.Where(c => c.NetworkCharacterId is null &&
                (c.ServerId is null || c.ServerId == context.ServerId) && string.Equals(c.Name, context.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
            var c = matches.FirstOrDefault(match => match.Id == _state.Active) ?? matches.FirstOrDefault();
            if (c is not null) choice = c.Id == _state.Active ? CharacterChoice.Unchanged : CharacterChoice.Selected;
            else if (named.Length == 1) { c = named[0]; choice = CharacterChoice.Adopted; }
            else if (named.Length == 0 && _state.Characters.FirstOrDefault(p => p.NetworkCharacterId is null &&
                p.ServerId is null && DefaultName().IsMatch(p.Name)) is { } unnamed)
            { c = unnamed; c.Name = context.Name.Trim(); choice = CharacterChoice.Adopted; }
            else
            { c = new CharacterExploration { Name = context.Name.Trim() }; _state.Characters.Add(c); choice = CharacterChoice.Created; }
            c.NetworkCharacterId = context.CharacterId;
            c.ServerId = context.ServerId;
            _state.Active = c.Id;
            _networkCharacter = c.Id;
            _networkContext = context;
            _questSequence = _teleportSequence = -1;
            PersistNetwork();
        }
        SafeEvent.Raise(Changed, "Charakterprofil");
        return choice;
    }

    private CharacterExploration? NetworkCharacter(NetworkProgressContext context, long lastSequence) =>
        _networkContext is { } current && current.Generation == context.Generation && current.ServerId == context.ServerId &&
        current.CharacterId == context.CharacterId && context.Sequence >= current.Sequence && context.Sequence > lastSequence
            ? _state.Characters.SingleOrDefault(c => c.Id == _networkCharacter) : null;

    /// <summary>Raw quest state only: status 2/4 and absence are not yet proven discovery/completion semantics.</summary>
    public bool ApplyNetworkQuests(QuestProgressSnapshot snapshot, NetworkProgressContext context)
    {
        if (snapshot.Quests.Any(q => q.QuestId <= 0) || snapshot.Quests.Select(q => q.QuestId).Distinct().Count() != snapshot.Quests.Count) return false;
        lock (_gate)
        {
            if (NetworkCharacter(context, _questSequence) is not { } c) return false;
            var quests = snapshot.Quests.ToDictionary(q => q.QuestId);
            c.RawQuests = quests.ToDictionary(p => p.Key, p => p.Value with { Counters = p.Value.Counters.ToArray() });
            foreach (var place in Places.Where(p => p.QuestId is not null).DistinctBy(p => p.Id))
            {
                var old = c.Progress.GetValueOrDefault(place.Id) ?? ExplorationPlaceProgress.Unknown;
                if (quests.TryGetValue(place.QuestId!.Value, out var quest))
                    c.Progress[place.Id] = old with { QuestStatus = quest.Status, QuestStage = quest.Stage,
                        QuestCounters = quest.Counters.ToArray(), QuestOrdinal = quest.Ordinal,
                        RawSource = "network-quest-unconfirmed", RawObservedAt = context.At, RawFreshness = ProgressFreshness.Fresh };
                else c.Progress[place.Id] = old with { QuestStatus = null, QuestStage = null, QuestCounters = null, QuestOrdinal = null,
                    RawSource = "network-quest-absent-unconfirmed", RawObservedAt = context.At, RawFreshness = ProgressFreshness.Fresh };
            }
            _questSequence = context.Sequence;
            PersistNetwork();
        }
        SafeEvent.Raise(Changed, "Erkundungs-Netzwerkstand");
        return true;
    }

    /// <summary>Presence is preserved separately until engraved/bound semantics are verified; absence clears no legacy/manual state.</summary>
    public bool ApplyNetworkTeleports(TeleportProgressSnapshot snapshot, NetworkProgressContext context)
    {
        if (snapshot.TeleportIds.Any(id => id <= 0)) return false;
        lock (_gate)
        {
            if (NetworkCharacter(context, _teleportSequence) is not { } c) return false;
            c.RawTeleportIds = snapshot.TeleportIds.ToHashSet();
            foreach (var place in Places.Where(p => p.Kind == "kibelisk" && p.TeleportId is not null).DistinctBy(p => p.Id))
            {
                var old = c.Progress.GetValueOrDefault(place.Id) ?? ExplorationPlaceProgress.Unknown;
                c.Progress[place.Id] = old with { TeleportPresent = snapshot.TeleportIds.Contains(place.TeleportId!.Value),
                    RawSource = "network-teleport-unconfirmed", RawObservedAt = context.At, RawFreshness = ProgressFreshness.Fresh };
            }
            _teleportSequence = context.Sequence;
            PersistNetwork();
        }
        SafeEvent.Raise(Changed, "Erkundungs-Netzwerkstand");
        return true;
    }

    public void MarkNetworkStale()
    {
        lock (_gate)
        {
            StaleCurrent();
            if (_networkContext is { } current) _invalidatedGeneration = Math.Max(_invalidatedGeneration, current.Generation);
            _networkContext = null;
            _networkCharacter = null;
        }
        SafeEvent.Raise(Changed, "Erkundungs-Netzwerkstand");
    }

    private void StaleCurrent()
    {
        if (_state.Characters.FirstOrDefault(c => c.Id == _networkCharacter) is not { } c) return;
        foreach (var (id, entry) in c.Progress.ToArray())
            c.Progress[id] = entry with { Freshness = entry.Freshness == ProgressFreshness.Fresh ? ProgressFreshness.Stale : entry.Freshness,
                RawFreshness = entry.RawFreshness == ProgressFreshness.Fresh ? ProgressFreshness.Stale : entry.RawFreshness };
    }

    private static ExplorationPlaceProgress Detach(ExplorationPlaceProgress entry) => entry with { QuestCounters = entry.QuestCounters?.ToArray() };
    private static void RefreshDone(CharacterExploration c) => c.Done = c.Progress
        .Where(p => p.Value.State == ExplorationProgressState.DiscoveredCompleted).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);

    /// <summary>Keep the accepted in-memory state/sequence when storage is temporarily unavailable.</summary>
    private void PersistNetwork()
    {
        try { Persist(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { LogFile.Error("Erkundungs-Netzwerkstand nicht gespeichert", error); }
    }

    private void Persist()
    {
        if (_needsMigrationBackup) { ProgressMigrationBackup.Create(_path); _needsMigrationBackup = false; }
        _state.SchemaVersion = 2;
        JsonFile.Save(_path, _state);
    }

    private static IReadOnlyList<ExplorationPlace> LoadPlaces(ProgressService progress)
    {
        var result = new List<ExplorationPlace>();
        if (progress.MapDataDirectory is null) return result;
        foreach (var map in progress.Maps)
        {
            var path = Path.Combine(progress.MapDataDirectory, map.Id, "data.js");
            if (!File.Exists(path)) continue;
            var text = File.ReadAllText(path); var start = text.IndexOf("] = {", StringComparison.Ordinal); var end = text.LastIndexOf('}');
            if (start < 0) continue;
            using var doc = JsonDocument.Parse(text.AsMemory(start + 4, end - start - 3));
            var cats = doc.RootElement.GetProperty("categories").EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToArray();
            var index = 0;
            foreach (var m in doc.RootElement.GetProperty("markers").EnumerateArray())
            {
                // User request 2026-10-12: game progress comes from own network observations, not proximity.
                var kind = cats[m[0].GetInt32()] switch { "Sealed Dungeon" => "dungeon", "Stronghold" => "stronghold", "Kibelisk" => "kibelisk", _ => null };
                if (kind is not null && m[3].GetString() is { Length: > 0 } en)
                    result.Add(new($"{map.Id}|{kind}|{en}", map.Id, kind, index, en, m[4].GetString(), m[1].GetDouble(), m[2].GetDouble(), MarkerId(m, "questId"), MarkerId(m, "teleportId")));
                index++;
            }
        }
        return UniqueKibelisks(result);
    }

    private static int? MarkerId(JsonElement marker, string name) => marker.GetArrayLength() > 7 &&
        marker[7].ValueKind == JsonValueKind.Object && marker[7].TryGetProperty(name, out var id) && id.TryGetInt32(out var value) && value > 0 ? value : null;

    /// <summary>
    /// Some Kibelisks share a name at places far apart (Verteron "Cantas Valley Mushroom Tree Peak",
    /// 170 px apart). They keep "#1", "#2" by their x position so existing character-progress ids stay distinct.
    /// </summary>
    internal static List<ExplorationPlace> UniqueKibelisks(List<ExplorationPlace> places)
    {
        foreach (var group in places.Where(p => p.Kind == "kibelisk").GroupBy(p => p.Id).Where(g => g.Count() > 1).ToList())
        {
            var number = 0;
            foreach (var place in group.OrderBy(p => p.X).ThenBy(p => p.Y))
                places[places.IndexOf(place)] = place with { Id = $"{place.Id}#{++number}" };
        }
        return places;
    }
}
