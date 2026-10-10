using System.Text.Json.Serialization;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Soulcrest.App.Services;

/// <summary>
/// A map symbol marked with a right click: lines with direction arrows lead to it - from the player, or
/// from <see cref="After"/> (the target it was chained to with Shift + right click: player → A → B).
/// <see cref="Color"/> is the colour of the route the target belongs to; without one the marking order picks it.
/// </summary>
public sealed record MapTarget(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("map")] string MapId,
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("icon")] string? Icon,
    [property: JsonPropertyName("after")] string? After = null,
    [property: JsonPropertyName("petId")] string? PetId = null,
    string? ExplorationId = null, string? CharacterId = null,
    [property: JsonPropertyName("routeId")] string? RouteId = null,
    [property: JsonPropertyName("color")] string? Color = null,
    [property: JsonPropertyName("leveling")] bool Leveling = false,
    [property: JsonPropertyName("completed")] bool Completed = false,
    [property: JsonPropertyName("stopIndex")] int? StopIndex = null,
    [property: JsonPropertyName("questMonster")] bool QuestMonster = false,
    [property: JsonPropertyName("monsterBranch")] bool MonsterBranch = false,
    [property: JsonPropertyName("branchX")] double? BranchX = null,
    [property: JsonPropertyName("branchY")] double? BranchY = null);

/// <summary>Position in a running leveling route, retained after its last stop for backward navigation.</summary>
public sealed record RouteProgress(int Completed, int Total)
{
    public bool CanGoBack => Completed > 0;
    public bool CanGoNext => Completed < Total;
}

/// <summary>One stop of a saved route.</summary>
public sealed record RouteStop(
    [property: JsonPropertyName("map")] string MapId,
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("icon")] string? Icon,
    [property: JsonPropertyName("petId")] string? PetId = null,
    [property: JsonPropertyName("color")] string? Color = null);

/// <summary>
/// A Shift + right click sequence saved under a name (routes.soulroute, user request 2026-10-05). Started
/// again it becomes the marked sequence; with <see cref="Repeat"/> it starts over after its last stop.
/// Ordinary routes share one adjustable colour; Leveling Routes use objective colours and a moving
/// window of three completed and three upcoming stops (user request 2026-10-10).
/// </summary>
public sealed record SavedRoute(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("stops")] IReadOnlyList<RouteStop> Stops,
    [property: JsonPropertyName("repeat")] bool Repeat = false,
    [property: JsonPropertyName("color")] string? Color = null,
    [property: JsonPropertyName("category")] string? Category = null)
{
    [JsonIgnore] public bool IsLeveling => Category == LevelingRouteStyle.Category;
}

/// <summary>Objective colours of leveling routes; ordinary routes keep their adjustable route colour.</summary>
public static class LevelingRouteStyle
{
    public const string Category = "Leveling Routes";
    public const string MainQuest = "#facc15", Teleport = "#a78bfa", Exploration = "#ffffff", RegionalQuest = "#4ade80";
    private static readonly Regex EpisodeName = new(@"\A(Asmodian|Elyos) · Episode ([1-9][0-9]*)(?: · Level [1-9][0-9]*–[1-9][0-9]*)?\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    /// <summary>Only the imported episode naming format participates in automatic episode continuation.</summary>
    public static bool TryGetEpisode(SavedRoute route, out string faction, out int episode)
    {
        faction = "";
        episode = 0;
        if (!route.IsLeveling || string.IsNullOrWhiteSpace(route.Name)) return false;
        var match = EpisodeName.Match(route.Name);
        if (!match.Success || !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out episode))
            return false;
        faction = match.Groups[1].Value;
        return true;
    }

    public static bool IsTrace(RouteStop stop) =>
        stop.Kind.Contains("Empyrean Trace", StringComparison.OrdinalIgnoreCase)
        || stop.Name.Contains("Empyrean Trace", StringComparison.OrdinalIgnoreCase)
        || stop.Name.Contains("Emyrean Trace", StringComparison.OrdinalIgnoreCase);

    public static string ColorOf(RouteStop stop)
    {
        // Imported objectives keep their classification after their instruction is replaced with a
        // verified quest title (or removed). A snapped NPC can offer both main and regional quests.
        if (stop.Color is MainQuest or Teleport or Exploration or RegionalQuest) return stop.Color;
        var type = stop.Kind + " " + stop.Name;
        if (type.Contains("Kibelisk", StringComparison.OrdinalIgnoreCase) || type.Contains("telepor", StringComparison.OrdinalIgnoreCase)) return Teleport;
        if (type.Contains("Sealed Dungeon", StringComparison.OrdinalIgnoreCase) || type.Contains("Stronghold", StringComparison.OrdinalIgnoreCase)) return Exploration;
        if (stop.Name.StartsWith("Main quest", StringComparison.OrdinalIgnoreCase)) return MainQuest;
        if (stop.Name.StartsWith("Side quest", StringComparison.OrdinalIgnoreCase) || type.Contains("Regional Quest", StringComparison.OrdinalIgnoreCase)) return RegionalQuest;
        if (type.Contains("Hero Quest", StringComparison.OrdinalIgnoreCase)) return MainQuest;
        return stop.Color is MainQuest or Teleport or Exploration or RegionalQuest ? stop.Color : Exploration;
    }
}

/// <summary>Marked targets, persistent (targets.json). Thread-safe; UI and overlay listen to Changed.</summary>
public sealed class MapTargetsService : IDisposable
{
    // Distinct colours, shared by the interactive map and the in-game overlay (index = order of marking).
    // Also the palette a saved route picks its one colour from.
    public static readonly string[] Colors = ["#facc15", "#38bdf8", "#f472b6", "#4ade80", "#fb923c", "#a78bfa", "#f87171", "#2dd4bf"];

    private readonly object _gate = new();
    private readonly List<MapTarget> _targets = JsonFile.Load<List<MapTarget>>(AppPaths.TargetsFile);
    private readonly List<SavedRoute> _routes = RouteFile.LoadLibrary(AppPaths.RoutesFile, AppPaths.LegacyRoutesFile);
    private readonly ExplorationService? _exploration;
    private readonly LevelingProgressProfiles _levelingProfiles;
    private readonly string _levelingProgressPath = Path.Combine(AppPaths.DataDirectory, "leveling-progress.json");
    private string? _characterId;
    private bool _disposed;

    public MapTargetsService(ExplorationService? exploration = null)
    {
        _exploration = exploration;
        _characterId = exploration?.ActiveId;
        _levelingProfiles = exploration is null ? new() : JsonFile.Load<LevelingProgressProfiles>(_levelingProgressPath);
        // Routes and running marks from before route colours: a colour per route in list order (not written
        // back until the next change), and the marks of a route take its colour.
        for (var i = 0; i < _routes.Count; i++)
        {
            if (_routes[i].Color is not { } color || !Colors.Contains(color))
                _routes[i] = _routes[i] with { Color = ColorOf(i) };
        }
        for (var i = 0; i < _targets.Count; i++)
        {
            if (_targets[i].RouteId is { } routeId && _routes.FirstOrDefault(r => r.Id == routeId) is { } route)
            {
                var target = _targets[i];
                var color = route.IsLeveling
                    ? LevelingRouteStyle.ColorOf(new RouteStop(target.MapId, target.X, target.Y, target.Name, target.Kind, target.Icon, Color: target.Color))
                    : route.Color;
                _targets[i] = target with { Leveling = route.IsLeveling, Color = color };
            }
        }
        foreach (var route in _routes.Where(r => r.IsLeveling)) RestoreLevelingRoute(route);
        if (_exploration is not null)
        {
            if (!_levelingProfiles.LegacyAdopted)
            {
                // A brand-new exploration profile initially exists only in memory. Persist its id
                // before assigning legacy progress, so restarting cannot create a different owner.
                _exploration.Select(_characterId!);
                CaptureCharacterProgress();
                _levelingProfiles.LegacyAdopted = true;
                // Record ownership before replacing targets: legacy global progress is adopted once,
                // by the selected character, including when its route has already been completed.
                JsonFile.Save(_levelingProgressPath, _levelingProfiles);
            }
            LoadCharacterRoute();
            SaveTargets();
            _exploration.Changed += CharacterChanged;
        }
    }

    public event Action? Changed;

    /// <summary>Progression mode: the spawn the player is led to right now (not saved; white line).</summary>
    public MapTarget? Progression { get; private set; }

    /// <summary>Transient red boss target. Never saved, chained, or completed on arrival.</summary>
    public MapTarget? BossRush { get; private set; }
    public MapTarget? BossPreview { get; private set; }

    public void SetBossPreview(MapTarget? target)
    {
        if (target == BossPreview) return;
        BossPreview = target;
        Changed?.Invoke();
    }

    public void SetBossRush(MapTarget? target)
    {
        if (target == BossRush) return;
        BossRush = target;
        Changed?.Invoke();
    }

    public void SetProgression(MapTarget? target)
    {
        if (target == Progression)
            return;
        Progression = target;
        Changed?.Invoke();
    }

    public IReadOnlyList<MapTarget> Targets
    {
        get { lock (_gate) return _targets.Where(t => !t.Completed).ToList(); }
    }

    /// <summary>Leveling routes show the last three completed stops and the next three pending stops.</summary>
    public IReadOnlyList<MapTarget> VisibleTargets
    {
        get
        {
            lock (_gate)
            {
                var visible = _targets.Where(t => !t.Leveling && !t.Completed).ToList();
                foreach (var group in _targets.Where(t => t.Leveling).GroupBy(t => t.RouteId))
                {
                    var history = group.Where(t => t.Completed).TakeLast(3).ToList();
                    if (history.Count > 0) history[0] = history[0] with { After = null };
                    var upcoming = group.Where(t => !t.Completed).Take(3).ToList();
                    if (upcoming.Count > 0) upcoming[0] = upcoming[0] with { After = null };
                    visible.AddRange(history);
                    visible.AddRange(upcoming);
                }
                return visible;
            }
        }
    }

    public IReadOnlyList<SavedRoute> Routes
    {
        get { lock (_gate) return _routes.ToList(); }
    }

    /// <summary>The routes whose stops are marked right now.</summary>
    public IReadOnlySet<string> ActiveRoutes
    {
        get { lock (_gate) return _targets.Where(t => !t.Completed && t.RouteId is not null).Select(t => t.RouteId!).ToHashSet(); }
    }

    /// <summary>Null until the route is started, or after its marks are cleared/replaced.</summary>
    public RouteProgress? GetRouteProgress(string routeId)
    {
        lock (_gate)
        {
            if (_routes.FirstOrDefault(r => r.Id == routeId) is not { IsLeveling: true } route
                || !_targets.Any(t => t.RouteId == routeId)) return null;
            return new RouteProgress(_targets.Count(t => t.RouteId == routeId && t.Completed),
                route.Stops.Count(s => !LevelingRouteStyle.IsTrace(s)));
        }
    }

    /// <summary>The selected character's saved position, even while another route is displayed.</summary>
    public RouteProgress? GetSavedRouteProgress(string routeId)
    {
        lock (_gate)
        {
            if (_exploration is null) return GetRouteProgress(routeId);
            if (_routes.FirstOrDefault(r => r.Id == routeId) is not { IsLeveling: true } route
                || _characterId is null || !_levelingProfiles.Characters.TryGetValue(_characterId, out var character)
                || !character.Routes.TryGetValue(routeId, out var completed)) return null;
            var total = route.Stops.Count(s => !LevelingRouteStyle.IsTrace(s));
            return new RouteProgress(Math.Clamp(completed, 0, total), total);
        }
    }

    public string? ActiveLevelingRouteId
    {
        get { lock (_gate) return _targets.FirstOrDefault(t => t.Leveling)?.RouteId; }
    }

    public static string ColorOf(int index) => Colors[index % Colors.Length];

    /// <summary>The colour of a marked target: its route's colour, else the colour of its place in the marking order.</summary>
    public static string ColorOf(MapTarget target, int index) => target.Color ?? ColorOf(index);

    /// <summary>The colour a newly saved route gets: the first palette colour no saved route uses yet.</summary>
    public string NextRouteColor
    {
        get
        {
            lock (_gate)
            {
                var used = _routes.Select(r => r.Color).ToHashSet();
                return Colors.FirstOrDefault(c => !used.Contains(c)) ?? ColorOf(_routes.Count);
            }
        }
    }

    /// <summary>
    /// Marks the symbol, or unmarks it when it is already marked (same map, same place). With
    /// <paramref name="chain"/> the new target follows the last marked target on that map.
    /// </summary>
    public bool Toggle(string mapId, double x, double y, string name, string kind, string? icon, bool chain = false, string? petId = null)
    {
        bool added;
        lock (_gate)
        {
            var existing = _targets.FirstOrDefault(t => !t.Completed && t.MapId == mapId && Math.Abs(t.X - x) < 0.5 && Math.Abs(t.Y - y) < 0.5);
            if (existing is not null)
            {
                if (existing.Leveling) CompleteLevelingStop(existing);
                else RemoveFromChain(existing);
                added = false;
            }
            else
            {
                var after = chain ? _targets.LastOrDefault(t => !t.Completed && t.MapId == mapId)?.Id : null;
                _targets.Add(new MapTarget(Guid.NewGuid().ToString("N"), mapId, x, y, name, kind, icon, after, petId));
                added = true;
            }
            SaveTargets();
        }
        Changed?.Invoke();
        return added;
    }

    public void Remove(string id)
    {
        lock (_gate)
        {
            if (_targets.FirstOrDefault(t => t.Id == id) is { } target)
            {
                if (target.Leveling && !target.Completed) CompleteLevelingStop(target);
                else if (!target.Leveling) RemoveFromChain(target);
            }
            SaveTargets();
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// The player reached the target: it disappears like <see cref="Remove"/>. When it was the last
    /// marked stop of a route set to repeat, the route starts over.
    /// </summary>
    public void Arrive(string id)
    {
        lock (_gate)
        {
            if (_targets.FirstOrDefault(t => t.Id == id && !t.Completed) is not { } target)
                return;
            if (target.Leveling)
            {
                // Later visits may share a coordinate; only the current stop advances this route.
                if (_targets.FirstOrDefault(t => t.RouteId == target.RouteId && !t.Completed)?.Id != id) return;
                CompleteLevelingStop(target);
            }
            else RemoveFromChain(target);
            if (target.RouteId is { } routeId && !_targets.Any(t => t.RouteId == routeId && !t.Completed)
                && _routes.FirstOrDefault(r => r.Id == routeId) is { Repeat: true } route
                && !LevelingRouteStyle.TryGetEpisode(route, out _, out _))
            {
                _targets.RemoveAll(t => t.RouteId == routeId);
                AddStops(route);
            }
            SaveTargets();
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// The sequence on <paramref name="mapId"/> that ends with the last marked target there, from its
    /// first stop (player → A → B → C). Empty without marks.
    /// </summary>
    public IReadOnlyList<MapTarget> ChainOn(string mapId)
    {
        lock (_gate)
        {
            var chain = new List<MapTarget>();
            for (var target = _targets.LastOrDefault(t => !t.Completed && t.MapId == mapId); target is not null && !chain.Contains(target);
                 target = target.After is { } after ? _targets.FirstOrDefault(t => t.Id == after && !t.Completed) : null)
                chain.Insert(0, target);
            return chain;
        }
    }

    /// <summary>
    /// Saves the sequence on <paramref name="mapId"/> as a route (null below two stops). Its marks then
    /// belong to the route, so repeating already works on this first run.
    /// </summary>
    public SavedRoute? SaveRoute(string mapId, string? name)
    {
        SavedRoute route;
        lock (_gate)
        {
            var chain = ChainOn(mapId);
            if (chain.Count < 2)
                return null;
            route = new SavedRoute(Guid.NewGuid().ToString("N"),
                string.IsNullOrWhiteSpace(name) ? $"{chain[0].Name} → {chain[^1].Name}" : name.Trim(),
                chain.Select(t => new RouteStop(t.MapId, t.X, t.Y, t.Name, t.Kind, t.Icon, t.PetId)).ToList(),
                Color: NextRouteColor);
            _routes.Add(route);
            RouteFile.Save(AppPaths.RoutesFile, _routes);
            var ids = chain.Select(t => t.Id).ToHashSet();
            for (var i = 0; i < _targets.Count; i++)
            {
                if (ids.Contains(_targets[i].Id))
                    _targets[i] = _targets[i] with { RouteId = route.Id, Color = route.Color };
            }
            SaveTargets();
        }
        Changed?.Invoke();
        return route;
    }

    /// <summary>Starts a route, resuming the selected character's saved leveling position.</summary>
    public void StartRoute(string routeId) => ActivateRoute(routeId, restart: false);

    /// <summary>Starts this leveling route at its first stop for the selected character only.</summary>
    public void RestartRoute(string routeId) => ActivateRoute(routeId, restart: true);

    /// <summary>Stops this route while retaining the selected character's saved leveling position.</summary>
    public void StopRoute(string routeId)
    {
        lock (_gate)
        {
            var marks = _targets.Where(t => t.RouteId == routeId).ToArray();
            if (marks.Length == 0) return;
            CaptureCharacterProgress();
            var index = _routes.FindIndex(r => r.Id == routeId && r.Repeat);
            if (index >= 0)
            {
                _routes[index] = _routes[index] with { Repeat = false };
                RouteFile.Save(AppPaths.RoutesFile, _routes);
            }
            // Ordinary targets may have been chained to this route. Keep them and reconnect their
            // lines instead of leaving a reference to a removed stop.
            foreach (var mark in marks)
                if (_targets.FirstOrDefault(t => t.Id == mark.Id) is { } current) RemoveFromChain(current);
            SaveTargets();
        }
        Changed?.Invoke();
    }

    private void ActivateRoute(string routeId, bool restart)
    {
        lock (_gate)
        {
            if (_routes.FirstOrDefault(r => r.Id == routeId) is not { } route)
                return;
            CaptureCharacterProgress();
            StopRepeating(except: routeId, levelingOnly: _exploration is not null && route.IsLeveling);
            if (_exploration is not null && route.IsLeveling)
            {
                _targets.RemoveAll(t => t.Leveling);
                var completed = !restart ? GetSavedRouteProgress(routeId)?.Completed ?? 0 : 0;
                AddStops(route, completed);
            }
            else
            {
                _targets.Clear();
                AddStops(route);
            }
            SaveTargets();
        }
        Changed?.Invoke();
    }

    public void SetRouteRepeat(string routeId, bool repeat) => UpdateRoute(routeId, r => r with { Repeat = repeat });

    /// <summary>Gives the route one of the palette <see cref="Colors"/>; its marked stops change along.</summary>
    public void SetRouteColor(string routeId, string color)
    {
        if (!Colors.Contains(color))
            return;
        lock (_gate)
        {
            var index = _routes.FindIndex(r => r.Id == routeId);
            if (index < 0 || _routes[index].IsLeveling)
                return;
            _routes[index] = _routes[index] with { Color = color };
            RouteFile.Save(AppPaths.RoutesFile, _routes);
            for (var i = 0; i < _targets.Count; i++)
            {
                if (_targets[i].RouteId == routeId)
                    _targets[i] = _targets[i] with { Color = color };
            }
            SaveTargets();
        }
        Changed?.Invoke();
    }

    /// <summary>Deletes the saved route; its marks stay on the map as an ordinary sequence.</summary>
    public void DeleteRoute(string routeId)
    {
        lock (_gate)
        {
            _routes.RemoveAll(r => r.Id == routeId);
            RouteFile.Save(AppPaths.RoutesFile, _routes);
            foreach (var character in _levelingProfiles.Characters.Values)
            {
                character.Routes.Remove(routeId);
                if (character.ActiveRouteId == routeId) character.ActiveRouteId = null;
            }
            _targets.RemoveAll(t => t.RouteId == routeId && t.Completed);
            for (var i = 0; i < _targets.Count; i++)
            {
                if (_targets[i].RouteId == routeId)
                    _targets[i] = _targets[i] with { RouteId = null, Color = null, Leveling = false, Completed = false, StopIndex = null };
            }
            SaveTargets();
        }
        Changed?.Invoke();
    }

    private void UpdateRoute(string routeId, Func<SavedRoute, SavedRoute> change)
    {
        lock (_gate)
        {
            var index = _routes.FindIndex(r => r.Id == routeId);
            if (index < 0)
                return;
            _routes[index] = change(_routes[index]);
            RouteFile.Save(AppPaths.RoutesFile, _routes);
        }
        Changed?.Invoke();
    }

    private void AddStops(SavedRoute route, int completed = 0)
    {
        string? after = null;
        var stopIndex = 0;
        foreach (var stop in route.Stops.Where(s => !route.IsLeveling || !LevelingRouteStyle.IsTrace(s)))
        {
            var id = Guid.NewGuid().ToString("N");
            _targets.Add(new MapTarget(id, stop.MapId, stop.X, stop.Y, stop.Name, stop.Kind, stop.Icon, after, stop.PetId,
                RouteId: route.Id, Color: route.IsLeveling ? LevelingRouteStyle.ColorOf(stop) : route.Color,
                Leveling: route.IsLeveling, StopIndex: route.IsLeveling ? stopIndex : null,
                Completed: route.IsLeveling && stopIndex < completed, CharacterId: route.IsLeveling ? _characterId : null));
            after = id;
            stopIndex++;
        }
    }

    private void CompleteLevelingStop(MapTarget target)
    {
        // Removing a later visible stop skips through it. Keep one contiguous position, so back/next
        // also work when coordinates repeat and when a route was manually skipped ahead.
        for (var i = 0; i < _targets.Count; i++)
            if (_targets[i].RouteId == target.RouteId && _targets[i].StopIndex <= target.StopIndex)
                _targets[i] = _targets[i] with { Completed = true };
        if (!_targets.Any(t => t.RouteId == target.RouteId && !t.Completed)
            && _routes.FirstOrDefault(r => r.Id == target.RouteId) is { } route)
            ContinueEpisode(route);
    }

    /// <summary>Called under the target lock only after a stop completes, never during load or selection.</summary>
    private void ContinueEpisode(SavedRoute completedRoute)
    {
        if (!LevelingRouteStyle.TryGetEpisode(completedRoute, out var faction, out var episode)) return;
        CaptureCharacterProgress();
        while (episode < int.MaxValue)
        {
            episode++;
            var candidates = _routes.Where(r => LevelingRouteStyle.TryGetEpisode(r, out var nextFaction, out var nextEpisode)
                && nextFaction == faction && nextEpisode == episode).ToArray();
            // A missing or ambiguous episode ends this chain; never guess a different continuation.
            if (candidates.Length != 1) return;
            var next = candidates[0];
            var total = next.Stops.Count(s => !LevelingRouteStyle.IsTrace(s));
            var completed = GetSavedRouteProgress(next.Id)?.Completed ?? 0;
            if (completed >= total) continue;
            StopRepeating(except: next.Id, levelingOnly: true);
            foreach (var mark in _targets.Where(t => t.Leveling).ToArray())
                if (_targets.FirstOrDefault(t => t.Id == mark.Id) is { } current) RemoveFromChain(current);
            AddStops(next, completed);
            return;
        }
    }

    public void AdvanceRoute(string routeId)
    {
        var next = Targets.FirstOrDefault(t => t.RouteId == routeId);
        if (next is not null) Arrive(next.Id);
    }

    /// <summary>Moves the current objective one stop back, including from the completed route.</summary>
    public void RewindRoute(string routeId)
    {
        lock (_gate)
        {
            if (_routes.FirstOrDefault(r => r.Id == routeId) is not { IsLeveling: true }
                || _targets.LastOrDefault(t => t.RouteId == routeId && t.Completed) is not { } previous) return;
            _targets[_targets.IndexOf(previous)] = previous with { Completed = false };
            SaveTargets();
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Older versions retained only three completed marks. Recover their missing prefix from the
    /// saved route, preserving the position and target ids. New marks persist the stop index, making
    /// later quest renaming and coordinate corrections independent of their former display text.
    /// </summary>
    private void RestoreLevelingRoute(SavedRoute route)
    {
        var marks = _targets.Where(t => t.RouteId == route.Id).ToList();
        if (marks.Count == 0) return;
        var stops = route.Stops.Where(s => !LevelingRouteStyle.IsTrace(s)).ToList();
        var indexed = new Dictionary<int, MapTarget>();
        var searchBefore = stops.Count;
        for (var i = marks.Count - 1; i >= 0; i--)
        {
            var mark = marks[i];
            var index = mark.StopIndex is { } saved && saved >= 0 && saved < stops.Count ? saved : -1;
            if (index < 0)
            {
                // Align from the end: an NPC can appear several times along the same route.
                index = searchBefore > 0 ? stops.FindLastIndex(searchBefore - 1, searchBefore, s => SameStop(s, mark)) : -1;
                if (index < 0) index = searchBefore - 1;
            }
            if (index < 0 || indexed.ContainsKey(index)) continue;
            indexed.Add(index, mark);
            searchBefore = index;
        }
        var position = indexed.Where(p => !p.Value.Completed).Select(p => p.Key).DefaultIfEmpty(stops.Count).Min();
        var insertAt = _targets.FindIndex(t => t.RouteId == route.Id);
        _targets.RemoveAll(t => t.RouteId == route.Id);
        var restored = new List<MapTarget>();
        string? after = null;
        for (var i = 0; i < stops.Count; i++)
        {
            var stop = stops[i];
            var id = indexed.TryGetValue(i, out var mark) ? mark.Id : Guid.NewGuid().ToString("N");
            restored.Add(new MapTarget(id, stop.MapId, stop.X, stop.Y, stop.Name, stop.Kind, stop.Icon, after, stop.PetId,
                RouteId: route.Id, Color: LevelingRouteStyle.ColorOf(stop), Leveling: true, Completed: i < position, StopIndex: i));
            after = id;
        }
        _targets.InsertRange(insertAt, restored);
    }

    private static bool SameStop(RouteStop stop, MapTarget mark) =>
        stop.MapId == mark.MapId && Math.Abs(stop.X - mark.X) < 0.5 && Math.Abs(stop.Y - mark.Y) < 0.5
        && stop.Name == mark.Name;

    private void SaveTargets()
    {
        if (_exploration is not null)
        {
            CaptureCharacterProgress();
            JsonFile.Save(_levelingProgressPath, _levelingProfiles);
        }
        JsonFile.Save(AppPaths.TargetsFile, _targets);
    }

    private void CaptureCharacterProgress()
    {
        if (_characterId is null) return;
        if (!_levelingProfiles.Characters.TryGetValue(_characterId, out var character))
            _levelingProfiles.Characters.Add(_characterId, character = new());
        character.ActiveRouteId = null;
        foreach (var group in _targets.Where(t => t.Leveling && t.RouteId is not null).GroupBy(t => t.RouteId!))
        {
            if (!_routes.Any(r => r.Id == group.Key && r.IsLeveling)) continue;
            character.Routes[group.Key] = group.Count(t => t.Completed);
            character.ActiveRouteId = group.Key;
        }
    }

    private void LoadCharacterRoute()
    {
        _targets.RemoveAll(t => t.Leveling);
        if (_characterId is null || !_levelingProfiles.Characters.TryGetValue(_characterId, out var character)
            || character.ActiveRouteId is not { } routeId
            || _routes.FirstOrDefault(r => r.Id == routeId && r.IsLeveling) is not { } route) return;
        var total = route.Stops.Count(s => !LevelingRouteStyle.IsTrace(s));
        AddStops(route, Math.Clamp(character.Routes.GetValueOrDefault(routeId), 0, total));
    }

    private void CharacterChanged()
    {
        if (_exploration is null) return;
        lock (_gate)
        {
            if (_disposed || _exploration.ActiveId == _characterId) return;
            CaptureCharacterProgress();
            _characterId = _exploration.ActiveId;
            LoadCharacterRoute();
            SaveTargets();
        }
        Changed?.Invoke();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_exploration is not null) _exploration.Changed -= CharacterChanged;
        }
    }

    private sealed class LevelingProgressProfiles
    {
        public LevelingProgressProfiles() { }
        public bool LegacyAdopted { get; set; }
        public Dictionary<string, CharacterLevelingProgress> Characters { get; set; } = [];
    }

    private sealed class CharacterLevelingProgress
    {
        public CharacterLevelingProgress() { }
        public string? ActiveRouteId { get; set; }
        public Dictionary<string, int> Routes { get; set; } = [];
    }

    /// <summary>Removes a target; targets chained to it continue from its predecessor instead.</summary>
    private void RemoveFromChain(MapTarget target)
    {
        _targets.Remove(target);
        for (var i = 0; i < _targets.Count; i++)
        {
            if (_targets[i].After == target.Id)
                _targets[i] = _targets[i] with { After = target.After };
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            StopRepeating();
            _targets.Clear();
            SaveTargets();
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// A running route that is ended ("Alle entfernen", or replaced by another route) loses its repeat
    /// switch; it was still shown as on (user report 2026-10-07). Called under the lock, before the marks go.
    /// </summary>
    private void StopRepeating(string? except = null, bool levelingOnly = false)
    {
        var running = _targets.Where(t => !levelingOnly || t.Leveling).Select(t => t.RouteId).OfType<string>()
            .Where(id => id != except).ToHashSet(StringComparer.Ordinal);
        var changed = false;
        for (var i = 0; i < _routes.Count; i++)
        {
            if (_routes[i].Repeat && running.Contains(_routes[i].Id))
            {
                _routes[i] = _routes[i] with { Repeat = false };
                changed = true;
            }
        }
        if (changed)
            RouteFile.Save(AppPaths.RoutesFile, _routes);
    }
}
