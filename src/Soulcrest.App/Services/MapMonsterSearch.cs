using Soulcrest.Core.Pets;

namespace Soulcrest.App.Services;

/// <summary>A named monster placement, including monsters without a proven soul drop.</summary>
public sealed record MapMonsterSpawn(string En, string? De, string? Icon, string? PetId);

public sealed record MonsterSearchMatch(string Key, string En, string? De, string? Icon,
    IReadOnlyList<string> PetIds, IReadOnlyList<string> Maps, int SpawnCount)
{
    public IReadOnlyList<string> Names { get; init; } = [Key];
    public string DisplayName(string language) => language == "de" && !string.IsNullOrWhiteSpace(De) ? De : En;
}

/// <summary>Offline named-monster index. Only explicit marker pet IDs establish a soul relation.</summary>
public sealed class MapMonsterSearch
{
    private readonly IReadOnlyList<MapInfo> _maps;
    private readonly PetCatalog _catalog;
    private readonly Lazy<IReadOnlyList<MonsterSearchMatch>> _entries;

    public MapMonsterSearch(string directory, IReadOnlyList<MapInfo> maps, PetCatalog catalog)
    {
        _maps = maps;
        _catalog = catalog;
        _entries = new(() => maps.SelectMany(map => MapPetMarkers.MonstersFor(directory, map.Id)
                .Select(spawn => (Map: map.Id, Spawn: spawn)))
            .GroupBy(row => row.Spawn.En, StringComparer.Ordinal)
            .Select(group => new MonsterSearchMatch(group.Key, group.Key,
                group.Select(row => row.Spawn.De).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)),
                group.Select(row => row.Spawn.Icon).FirstOrDefault(icon => icon is not null),
                group.Select(row => row.Spawn.PetId is { } id ? catalog.Find(id)?.Id : null)
                    .OfType<string>().Distinct(StringComparer.Ordinal).ToArray(),
                group.Select(row => row.Map).Distinct(StringComparer.Ordinal).ToArray(), group.Count()))
            .ToArray());
    }

    // User request 2026-10-14: at most one visible name, including duplicate spawns across maps.
    public IReadOnlyList<MonsterSearchMatch> Search(string query, string language)
    {
        var term = query.Trim();
        if (term.Length < 2) return [];
        return _entries.Value.Where(entry => entry.En.Contains(term, StringComparison.OrdinalIgnoreCase)
                || entry.De?.Contains(term, StringComparison.OrdinalIgnoreCase) == true)
            .GroupBy(entry => entry.DisplayName(language), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First() with
            {
                Names = group.SelectMany(entry => entry.Names).Distinct(StringComparer.Ordinal).ToArray(),
                PetIds = group.SelectMany(entry => entry.PetIds).Distinct(StringComparer.Ordinal).ToArray(),
                Maps = group.SelectMany(entry => entry.Maps).Distinct(StringComparer.Ordinal).ToArray(),
                SpawnCount = group.Sum(entry => entry.SpawnCount)
            })
            .OrderByDescending(entry => entry.DisplayName(language).Equals(term, StringComparison.OrdinalIgnoreCase))
            .ThenBy(entry => entry.DisplayName(language), StringComparer.OrdinalIgnoreCase)
            .Take(40).ToArray();
    }

    /// <summary>The concrete monster's maps, plus sourced farm maps of its associated pets.</summary>
    public IReadOnlyList<MapInfo> FarmMaps(MonsterSearchMatch match) => _maps.Where(map =>
        match.Maps.Contains(map.Id, StringComparer.Ordinal)
        || match.PetIds.Any(id => _catalog.Find(id)?.Maps.Contains(map.Id, StringComparer.Ordinal) == true)).ToArray();

    /// <summary>Prefer a verified player's map; a world-map preview or stale position is not player evidence.</summary>
    public static string PreferredMap(MonsterSearchMatch match, string shownMap,
        ExplorationTrackingSnapshot tracking, DateTimeOffset now)
    {
        var player = ExplorationOverview.FreshPosition(tracking, now);
        if (player is not null && match.Maps.Contains(player.MapId, StringComparer.Ordinal)) return player.MapId;
        return match.Maps.Contains(shownMap, StringComparer.Ordinal) ? shownMap : match.Maps.First();
    }
}
