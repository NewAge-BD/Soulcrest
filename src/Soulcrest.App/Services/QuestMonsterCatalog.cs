namespace Soulcrest.App.Services;

/// <summary>Verified quest-step NPC spawns. Derived display markers never enter route/progress persistence.</summary>
public sealed class QuestMonsterCatalog
{
    public sealed record Spawn(string MapId, double X, double Y, string NpcId, string Name, string? NameDe, int Count, string Icon);
    public sealed record Station(string RouteId, int StopIndex, string MapId, double X, double Y, string QuestId, int Step, IReadOnlyList<Spawn> Spawns, int? AcceptIndex = null, int? TurnInIndex = null);
    private readonly Dictionary<(string Route, int Index), Station> _stations;

    public QuestMonsterCatalog() : this(AppPaths.FindMapData() is { } data
        ? JsonFile.Load<List<Station>>(Path.Combine(data, "leveling-objectives.json")) : []) { }

    internal QuestMonsterCatalog(IEnumerable<Station> stations) => _stations = stations
        .GroupBy(s => (s.RouteId, s.StopIndex)).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());

    private IEnumerable<(Station Station, MapTarget Kill, MapTarget Accept, MapTarget Return)> Active(IReadOnlyList<MapTarget> targets)
    {
        foreach (var station in _stations.Values)
        {
            var kill = targets.FirstOrDefault(t => t.Leveling && t.RouteId == station.RouteId && t.StopIndex == station.StopIndex);
            var accept = targets.FirstOrDefault(t => t.Leveling && t.RouteId == station.RouteId && t.StopIndex == station.AcceptIndex);
            var turnIn = targets.FirstOrDefault(t => t.Leveling && t.RouteId == station.RouteId && t.StopIndex == station.TurnInIndex);
            if (kill is null || accept is not { Completed: true } || turnIn is not { Completed: false }
                || kill.MapId != station.MapId || Math.Abs(kill.X-station.X) > .01 || Math.Abs(kill.Y-station.Y) > .01) continue;
            yield return (station, kill, accept, turnIn);
        }
    }

    public IReadOnlyList<MapTarget> ResolveRoute(IReadOnlyList<MapTarget> targets, string language)
    {
        var result = new List<MapTarget>();
        foreach (var (station, kill, accept, _) in Active(targets))
        {
            var spawns = Resolve(kill with { Completed = false }, language);
            result.AddRange(spawns.Select(s => s with { Id = s.Id + ":" + station.StopIndex }));
            if (spawns.Count > 0)
            {
                var destination = spawns.MinBy(s => (s.X-kill.X)*(s.X-kill.X)+(s.Y-kill.Y)*(s.Y-kill.Y))!;
                result.Add(kill with { X = destination.X, Y = destination.Y, Id = "quest-branch:" + kill.Id, QuestMonster = true, MonsterBranch = true,
                    Leveling = false, Completed = false, Color = "#ffffff", Icon = spawns[0].Icon,
                    After = accept.Id, BranchX = accept.X, BranchY = accept.Y });
            }
        }
        return result;
    }

    public IReadOnlyList<MapTarget> ArrivalTargets(IReadOnlyList<MapTarget> targets, PlayerPosition position)
    {
        var active = Active(targets).ToDictionary(s => s.Kill.Id, s => s.Station);
        return targets.Where(t => !t.Completed).Select(t =>
        {
            if (!active.TryGetValue(t.Id, out var station) || position.MapId != t.MapId) return t;
            var spawn = station.Spawns.Where(s => s.MapId == t.MapId)
                .MinBy(s => (s.X-position.X)*(s.X-position.X)+(s.Y-position.Y)*(s.Y-position.Y));
            return spawn is null ? t : t with { X = spawn.X, Y = spawn.Y };
        }).ToArray();
    }

    public IReadOnlyList<MapTarget> MainRouteTargets(IReadOnlyList<MapTarget> all, IReadOnlyList<MapTarget> visible)
    {
        bool Branch(MapTarget t) => t.Leveling && t.RouteId is { } r && t.StopIndex is { } i
            && _stations.TryGetValue((r, i), out var s) && s.MapId == t.MapId
            && Math.Abs(s.X-t.X) <= .01 && Math.Abs(s.Y-t.Y) <= .01;
        return visible.Where(t => !Branch(t)).Select(t =>
        {
            var original = all.FirstOrDefault(p => p.Id == t.Id);
            var previous = all.FirstOrDefault(p => p.Id == original?.After);
            if (previous is null || !Branch(previous) || t.Completed && t.After is null) return t;
            var source = all.LastOrDefault(p => p.Leveling && p.RouteId == t.RouteId && p.StopIndex < t.StopIndex && !Branch(p));
            return source is null ? t with { After = null } : t with { After = source.Id, BranchX = source.X, BranchY = source.Y };
        }).ToArray();
    }

    public IReadOnlyList<MapTarget> Resolve(MapTarget? current, string language)
    {
        if (current is not { Leveling: true, Completed: false, RouteId: { } route, StopIndex: { } index }
            || !_stations.TryGetValue((route, index), out var station)
            || station.MapId != current.MapId || Math.Abs(station.X-current.X) > .01 || Math.Abs(station.Y-current.Y) > .01)
            return [];
        return station.Spawns.Where(s => s.MapId == current.MapId && s.Count > 0 && double.IsFinite(s.X) && double.IsFinite(s.Y))
            .Select((s, i) => new MapTarget($"quest-monster:{station.QuestId}:{station.Step}:{s.NpcId}:{i}",
                s.MapId, s.X, s.Y, $"{(language == "de" && !string.IsNullOrWhiteSpace(s.NameDe) ? s.NameDe : s.Name)} · ×{s.Count}",
                "Monsters · Quest Objective", s.Icon, Color: "#ffffff", QuestMonster: true)).ToArray();
    }
}
