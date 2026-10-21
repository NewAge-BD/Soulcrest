using System.Drawing;
using Soulcrest.App.Services;

namespace Soulcrest.App.Overlay;

public sealed partial class RouteOverlayForm
{
    private readonly PreparationCache<PreparationKey, PreparedOverlay> _preparation = new();
    private readonly TargetDistanceCache _distances = new();

    private readonly record struct PreparationKey(string Map, MapSurface Surface, string Language, string? MapData, string ThemeId);

    /// <summary>Map-level choices and labels, independent of the current screen transform and player position.</summary>
    internal sealed record PreparedOverlay(
        string MapId,
        IReadOnlyList<(MapTarget Target, int Index)> Targets,
        IReadOnlySet<string> TargetIds,
        IReadOnlyList<PreparedCandidate<PetSymbol>> Pets,
        IReadOnlyList<PreparedCandidate<ResourceSymbol>> Resources,
        IReadOnlyList<SoulMonster> Souls,
        GuardZones? Guards,
        double? MetersPerPixel,
        IReadOnlyDictionary<string, Bitmap?> TargetIcons,
        IReadOnlyList<TargetSymbol> TargetSymbols,
        IReadOnlyList<HiddenCubeGroup> HiddenCubeGroups, string ThemeId = "soulcrest-legacy");

    internal sealed record TargetSymbol(MapTarget Target, ResourceSymbol? Symbol);

    /// <summary>
    /// Keep offscreen icons unloaded, as before caching: a small minimap must not read an entire
    /// map's portraits on its first frame. Only the compositor realizes candidates, after culling.
    /// </summary>
    internal sealed class PreparedCandidate<T>(double x, double y, Func<T> create)
    {
        private bool _created;
        private T? _symbol;
        internal double X { get; } = x;
        internal double Y { get; } = y;

        internal T Symbol
        {
            get
            {
                if (!_created)
                {
                    // Mark realized only after success: a transient file/GDI failure retries on
                    // the next frame, just as the original per-frame icon lookup did.
                    _symbol = create();
                    _created = true;
                }
                return _symbol!;
            }
        }
    }

    // Events invalidate without taking the compositor lock; they can come from the tracking thread.
    private void InvalidatePreparation() => _preparation.Invalidate();

    private PreparedOverlay PreparationFor(MapPlacement placement)
    {
        var key = new PreparationKey(placement.MapId, SurfaceOf(placement), UiText.Language, _progress.MapDataDirectory,
            OverlayThemeDefinition.Resolve(_settings.Current.OverlayTheme).Id);
        return _preparation.Get(key, () => PrepareOverlay(key.Map, key.Surface, key.Language,
            _settings.Current, _progress, _targets, IconFor, ResourceIcon, key.ThemeId));
    }

    /// <summary>
    /// Only target, settings and progress events rebuild these candidates. Projection and viewport
    /// clipping deliberately happen afterwards, for every optical-flow placement.
    /// </summary>
    internal static PreparedOverlay PrepareOverlay(string map, MapSurface surface, string language,
        AppSettings settings, ProgressService progress, MapTargetsService targets,
        Func<PetSpawn, string, Bitmap?> petIcon, Func<string?, string, Bitmap?> resourceIcon, string? themeId = null)
    {
        var onMap = targets.VisibleTargets.Select((t, i) => (Target: t, Index: i)).Where(t => t.Target.MapId == map).ToList();
        if (targets.Progression is { } next && next.MapId == map) onMap.Add((next, -1));
        if (targets.BossRush is { } boss && boss.MapId == map) onMap.Add((boss, -2));
        if (targets.BossPreview is { } preview && preview.MapId == map) onMap.Add((preview, -3));
        if (!settings.ShowRoutesInGame) onMap.Clear();

        string? Pet(MapTarget target) => target.PetId ?? (target.Kind.StartsWith("Pets", StringComparison.Ordinal)
            ? progress.FindByName(target.Name)?.Id : null);
        var marked = onMap.Select(t => Pet(t.Target)).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var mapdata = progress.MapDataDirectory;
        for (var i = 0; i < onMap.Count; i++)
        {
            var target = onMap[i].Target;
            if (target.Completed || Pet(target) is not { } pet) continue;
            var name = mapdata is not null && MapPetMarkers.MonsterAt(mapdata, map, pet, target.X, target.Y) is { } monster
                ? monster.In(language) : target.Name;
            onMap[i] = (target with { Name = $"{name} {progress.SoulCount(pet)}" }, onMap[i].Index);
        }

        var pets = new List<PreparedCandidate<PetSymbol>>();
        var resources = new List<PreparedCandidate<ResourceSymbol>>();
        var souls = new List<SoulMonster>();
        var targetSymbols = new List<TargetSymbol>();
        var targetIcons = new Dictionary<string, Bitmap?>(StringComparer.Ordinal);
        GuardZones? guards = null;
        var metersPerPixel = progress.Maps.FirstOrDefault(m => m.Id == map)?.MetersPerPixel;
        if (mapdata is not null)
        {
            // Several spawns share a pet. Read its progress and create its label only once per rebuild.
            var petState = new Dictionary<string, (bool Visible, string Label, bool Done)>(StringComparer.Ordinal);
            foreach (var spawn in MapPetMarkers.For(mapdata, map))
            {
                var isMarked = marked.Contains(spawn.PetId);
                if (!PetVisible(settings, map, surface, spawn, isMarked)) continue;
                if (!petState.TryGetValue(spawn.PetId, out var state))
                {
                    var visible = isMarked || (!settings.HidesLevel(progress.Level(spawn.PetId))
                        && (!settings.ExclusivePetsOnly || progress.Catalog.Find(spawn.PetId) is not { } pet || pet.IsExclusiveTo(map)));
                    state = visible ? (true, progress.PetMapLabel(spawn.PetId), progress.IsMax(spawn.PetId)) : (false, "", false);
                    petState.Add(spawn.PetId, state);
                }
                if (state.Visible)
                    pets.Add(new PreparedCandidate<PetSymbol>(spawn.X, spawn.Y,
                        () => new PetSymbol(spawn.X, spawn.Y, petIcon(spawn, mapdata), state.Label, state.Done)));
            }
            var cubeGroupsVisible = HiddenCubeGroupsVisible(settings, map, surface);
            foreach (var symbol in MapPetMarkers.SymbolsFor(mapdata, map))
                if (SymbolVisible(settings, map, surface, symbol))
                    resources.Add(new PreparedCandidate<ResourceSymbol>(symbol.X, symbol.Y,
                        () => new ResourceSymbol(symbol.X, symbol.Y, resourceIcon(symbol.Icon, mapdata), OutlineColor: cubeGroupsVisible ? symbol.OutlineColor : null)));
            if (marked.Count > 0)
                foreach (var spawn in MapPetMarkers.SoulMonstersFor(mapdata, map))
                    if (marked.Contains(spawn.PetId)) souls.Add(new SoulMonster(spawn.X, spawn.Y));
            if (settings.GuardZoneMaps.Contains(map))
                guards = new GuardZones(MapPetMarkers.GuardsFor(mapdata, map), Math.Max(1, settings.GuardRadiusMeters) / (metersPerPixel ?? 1));

            foreach (var (target, _) in onMap)
            {
                if (ShowsIconOnly(target))
                {
                    targetSymbols.Add(new TargetSymbol(target, null));
                    if ((target.Icon ?? MapPetMarkers.CategoryIcon(mapdata, map, CategoryOf(target.Kind))) is { } path)
                        targetIcons[target.Id] = resourceIcon(path, mapdata);
                }
                else if (target.Leveling)
                    targetSymbols.Add(new TargetSymbol(target, new ResourceSymbol(target.X, target.Y,
                        target.Icon is { } path ? resourceIcon(path, mapdata) : null, target.Color, target.Completed ? 0.28f : 1f)));
                else if (!target.MonsterBranch && Pet(target) is null && target.Icon is { } icon)
                    targetSymbols.Add(new TargetSymbol(target, new ResourceSymbol(target.X, target.Y, resourceIcon(icon, mapdata))));
            }
        }
        return new PreparedOverlay(map, onMap, onMap.Select(t => t.Target.Id).ToHashSet(StringComparer.Ordinal),
            pets, resources, souls, guards, metersPerPixel, targetIcons, targetSymbols,
            mapdata is null ? [] : PrepareHiddenCubeGroups(settings, map, surface, mapdata),
            themeId ?? OverlayThemeDefinition.Resolve(settings.OverlayTheme).Id);
    }

    // Cube orientation stays independent of selected markers, route settings and player position.
    internal static IReadOnlyList<HiddenCubeGroup> PrepareHiddenCubeGroups(AppSettings settings, string map, MapSurface surface, string mapdata) =>
        HiddenCubeGroupsVisible(settings, map, surface) ? MapPetMarkers.HiddenCubeGroupsFor(mapdata, map) : [];

    private static bool HiddenCubeGroupsVisible(AppSettings settings, string map, MapSurface surface) =>
        MapLayerVisibility.CategoryVisible(settings, map, surface, "Collectibles", "Hidden Cube")
            && MapLayerVisibility.CategoryVisible(settings, map, surface, "Collectibles", "Cube Groups");

    private List<(MapTarget Target, int Index)> TargetsWithDistance(PreparedOverlay prepared) =>
        _distances.Get(prepared, _tracking.CanUsePlayerPosition ? _tracking.Position : null, UiText.Language);

    /// <summary>
    /// Optical flow updates the projection much more often than a verified player position. Reuse
    /// exactly the same strings until the full accepted position, prepared content or language changes.
    /// </summary>
    internal sealed class TargetDistanceCache
    {
        private PreparedOverlay? _prepared;
        private PlayerPosition? _player;
        private string? _language;
        private List<(MapTarget Target, int Index)>? _targets;

        internal List<(MapTarget Target, int Index)> Get(PreparedOverlay prepared, PlayerPosition? player, string language)
        {
            if (ReferenceEquals(_prepared, prepared) && _player == player && _language == language && _targets is { } targets)
                return targets;
            var result = TargetsWithDistance(prepared, player);
            _prepared = prepared;
            _player = player;
            _language = language;
            return _targets = result;
        }
    }

    internal static List<(MapTarget Target, int Index)> TargetsWithDistance(PreparedOverlay prepared, PlayerPosition? player)
    {
        var result = prepared.Targets.ToList();
        if (player is null || player.MapId != prepared.MapId || prepared.MetersPerPixel is not { } metersPerPixel) return result;
        for (var i = 0; i < result.Count; i++)
        {
            var target = result[i].Target;
            if (target.Completed || target.Leveling && string.IsNullOrWhiteSpace(target.Name)
                || target.After is { } after && prepared.TargetIds.Contains(after)) continue;
            var pixels = Math.Sqrt((target.X - player.X) * (target.X - player.X) + (target.Y - player.Y) * (target.Y - player.Y));
            result[i] = (target with { Name = $"{target.Name} · {DistanceText(pixels * metersPerPixel)}" }, result[i].Index);
        }
        return result;
    }

    internal static List<PetSymbol> VisiblePets(MapPlacement placement, PreparedOverlay prepared)
    {
        var area = new RectangleF(placement.Region.X - 20, placement.Region.Y - 20, placement.Region.Width + 40, placement.Region.Height + 40);
        return prepared.Pets.Where(p => area.Contains(placement.WorldToScreen(p.X, p.Y))).Select(p => p.Symbol).ToList();
    }

    internal static List<SoulMonster> VisibleSoulMonsters(MapPlacement placement, PreparedOverlay prepared)
    {
        var area = new RectangleF(placement.Region.X - 8, placement.Region.Y - 8, placement.Region.Width + 16, placement.Region.Height + 16);
        return prepared.Souls.Where(p => area.Contains(placement.WorldToScreen(p.X, p.Y))).ToList();
    }

    internal static List<ResourceSymbol> VisibleSymbols(MapPlacement placement, PreparedOverlay prepared)
    {
        var area = new RectangleF(placement.Region.X - 12, placement.Region.Y - 12, placement.Region.Width + 24, placement.Region.Height + 24);
        return prepared.Resources.Where(p => area.Contains(placement.WorldToScreen(p.X, p.Y))).Select(p => p.Symbol).ToList();
    }

    internal static GuardZones? VisibleGuardZones(MapPlacement placement, PreparedOverlay prepared)
    {
        if (prepared.Guards is not { } zones) return null;
        var first = placement.WorldToScreen(0, 0);
        var second = placement.WorldToScreen(zones.RadiusWorld, 0);
        var margin = (float)Math.Sqrt((second.X - first.X) * (second.X - first.X) + (second.Y - first.Y) * (second.Y - first.Y)) + 4;
        var area = new RectangleF(placement.Region.X - margin, placement.Region.Y - margin, placement.Region.Width + 2 * margin, placement.Region.Height + 2 * margin);
        var guards = zones.Guards.Where(g => area.Contains(placement.WorldToScreen(g.X, g.Y))).ToList();
        return guards.Count == 0 ? null : new GuardZones(guards, zones.RadiusWorld);
    }

    internal static void ApplyTargetSymbols(List<ResourceSymbol> resources, PreparedOverlay prepared)
    {
        foreach (var item in prepared.TargetSymbols)
        {
            var target = item.Target;
            if (item.Symbol is null) RemoveSymbolInRing(resources, target);
            else if (target.Leveling)
            {
                resources.RemoveAll(r => r.X == target.X && r.Y == target.Y);
                resources.Add(item.Symbol);
            }
            else if (!resources.Any(r => r.X == target.X && r.Y == target.Y)) resources.Add(item.Symbol);
        }
    }
}

/// <summary>
/// One compositor consumes the cache. Invalidation is lock-free so progress/target events never wait
/// for composition. Each entry retains the generation captured before its build: an overlapping
/// invalidation cannot promote old work as belonging to the new generation.
/// </summary>
internal sealed class PreparationCache<TKey, TValue> where TKey : notnull
{
    private sealed record Entry(long Generation, TKey Key, TValue Value);
    private long _generation;
    private Entry? _entry;

    internal void Invalidate() => Interlocked.Increment(ref _generation);

    internal TValue Get(TKey key, Func<TValue> build)
    {
        var generation = Volatile.Read(ref _generation);
        if (_entry is { } cached && cached.Generation == generation && EqualityComparer<TKey>.Default.Equals(cached.Key, key))
            return cached.Value;
        var value = build();
        // A semantic change during this build is rejected by the frame dispatcher's epoch. Keeping
        // the old generation here forces the next frame to rebuild, without starving rendering
        // or disposal by retrying indefinitely while network progress keeps changing.
        _entry = new Entry(generation, key, value);
        return value;
    }
}
