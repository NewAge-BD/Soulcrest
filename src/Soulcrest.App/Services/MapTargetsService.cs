using System.Text.Json.Serialization;

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
    [property: JsonPropertyName("color")] string? Color = null);

/// <summary>One stop of a saved route.</summary>
public sealed record RouteStop(
    [property: JsonPropertyName("map")] string MapId,
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("icon")] string? Icon,
    [property: JsonPropertyName("petId")] string? PetId = null);

/// <summary>
/// A Shift + right click sequence saved under a name (routes.json, user request 2026-10-05). Started
/// again it becomes the marked sequence; with <see cref="Repeat"/> it starts over after its last stop.
/// All its stops and lines share one <see cref="Color"/> from <see cref="MapTargetsService.Colors"/>
/// (user request 2026-10-07); routes saved before have none and get one when loaded.
/// </summary>
public sealed record SavedRoute(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("stops")] IReadOnlyList<RouteStop> Stops,
    [property: JsonPropertyName("repeat")] bool Repeat = false,
    [property: JsonPropertyName("color")] string? Color = null);

/// <summary>Marked targets, persistent (targets.json). Thread-safe; UI and overlay listen to Changed.</summary>
public sealed class MapTargetsService
{
    // Distinct colours, shared by the interactive map and the in-game overlay (index = order of marking).
    // Also the palette a saved route picks its one colour from.
    public static readonly string[] Colors = ["#facc15", "#38bdf8", "#f472b6", "#4ade80", "#fb923c", "#a78bfa", "#f87171", "#2dd4bf"];

    private readonly object _gate = new();
    private readonly List<MapTarget> _targets = JsonFile.Load<List<MapTarget>>(AppPaths.TargetsFile);
    private readonly List<SavedRoute> _routes = JsonFile.Load<List<SavedRoute>>(AppPaths.RoutesFile);

    public MapTargetsService()
    {
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
                _targets[i] = _targets[i] with { Color = route.Color };
        }
    }

    public event Action? Changed;

    /// <summary>Progression mode: the spawn the player is led to right now (not saved; white line).</summary>
    public MapTarget? Progression { get; private set; }

    public void SetProgression(MapTarget? target)
    {
        if (target == Progression)
            return;
        Progression = target;
        Changed?.Invoke();
    }

    public IReadOnlyList<MapTarget> Targets
    {
        get { lock (_gate) return _targets.ToList(); }
    }

    public IReadOnlyList<SavedRoute> Routes
    {
        get { lock (_gate) return _routes.ToList(); }
    }

    /// <summary>The routes whose stops are marked right now.</summary>
    public IReadOnlySet<string> ActiveRoutes
    {
        get { lock (_gate) return _targets.Where(t => t.RouteId is not null).Select(t => t.RouteId!).ToHashSet(); }
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
            var existing = _targets.FirstOrDefault(t => t.MapId == mapId && Math.Abs(t.X - x) < 0.5 && Math.Abs(t.Y - y) < 0.5);
            if (existing is not null)
            {
                RemoveFromChain(existing);
                added = false;
            }
            else
            {
                var after = chain ? _targets.LastOrDefault(t => t.MapId == mapId)?.Id : null;
                _targets.Add(new MapTarget(Guid.NewGuid().ToString("N"), mapId, x, y, name, kind, icon, after, petId));
                added = true;
            }
            JsonFile.Save(AppPaths.TargetsFile, _targets);
        }
        Changed?.Invoke();
        return added;
    }

    public void Remove(string id)
    {
        lock (_gate)
        {
            if (_targets.FirstOrDefault(t => t.Id == id) is { } target)
                RemoveFromChain(target);
            JsonFile.Save(AppPaths.TargetsFile, _targets);
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
            if (_targets.FirstOrDefault(t => t.Id == id) is not { } target)
                return;
            RemoveFromChain(target);
            if (target.RouteId is { } routeId && !_targets.Any(t => t.RouteId == routeId)
                && _routes.FirstOrDefault(r => r.Id == routeId) is { Repeat: true } route)
                AddStops(route);
            JsonFile.Save(AppPaths.TargetsFile, _targets);
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
            for (var target = _targets.LastOrDefault(t => t.MapId == mapId); target is not null && !chain.Contains(target);
                 target = target.After is { } after ? _targets.FirstOrDefault(t => t.Id == after) : null)
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
            JsonFile.Save(AppPaths.RoutesFile, _routes);
            var ids = chain.Select(t => t.Id).ToHashSet();
            for (var i = 0; i < _targets.Count; i++)
            {
                if (ids.Contains(_targets[i].Id))
                    _targets[i] = _targets[i] with { RouteId = route.Id, Color = route.Color };
            }
            JsonFile.Save(AppPaths.TargetsFile, _targets);
        }
        Changed?.Invoke();
        return route;
    }

    /// <summary>Replaces all marks with the route's stops, chained from the player to the last stop.</summary>
    public void StartRoute(string routeId)
    {
        lock (_gate)
        {
            if (_routes.FirstOrDefault(r => r.Id == routeId) is not { } route)
                return;
            StopRepeating(except: routeId);
            _targets.Clear();
            AddStops(route);
            JsonFile.Save(AppPaths.TargetsFile, _targets);
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
            if (index < 0)
                return;
            _routes[index] = _routes[index] with { Color = color };
            JsonFile.Save(AppPaths.RoutesFile, _routes);
            for (var i = 0; i < _targets.Count; i++)
            {
                if (_targets[i].RouteId == routeId)
                    _targets[i] = _targets[i] with { Color = color };
            }
            JsonFile.Save(AppPaths.TargetsFile, _targets);
        }
        Changed?.Invoke();
    }

    /// <summary>Deletes the saved route; its marks stay on the map as an ordinary sequence.</summary>
    public void DeleteRoute(string routeId)
    {
        lock (_gate)
        {
            _routes.RemoveAll(r => r.Id == routeId);
            JsonFile.Save(AppPaths.RoutesFile, _routes);
            for (var i = 0; i < _targets.Count; i++)
            {
                if (_targets[i].RouteId == routeId)
                    _targets[i] = _targets[i] with { RouteId = null, Color = null };
            }
            JsonFile.Save(AppPaths.TargetsFile, _targets);
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
            JsonFile.Save(AppPaths.RoutesFile, _routes);
        }
        Changed?.Invoke();
    }

    private void AddStops(SavedRoute route)
    {
        string? after = null;
        foreach (var stop in route.Stops)
        {
            var id = Guid.NewGuid().ToString("N");
            _targets.Add(new MapTarget(id, stop.MapId, stop.X, stop.Y, stop.Name, stop.Kind, stop.Icon, after, stop.PetId, RouteId: route.Id, Color: route.Color));
            after = id;
        }
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
            JsonFile.Save(AppPaths.TargetsFile, _targets);
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// A running route that is ended ("Alle entfernen", or replaced by another route) loses its repeat
    /// switch; it was still shown as on (user report 2026-10-07). Called under the lock, before the marks go.
    /// </summary>
    private void StopRepeating(string? except = null)
    {
        var running = _targets.Select(t => t.RouteId).OfType<string>().Where(id => id != except).ToHashSet(StringComparer.Ordinal);
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
            JsonFile.Save(AppPaths.RoutesFile, _routes);
    }
}
