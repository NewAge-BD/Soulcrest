using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Soulcrest.App.Services;

/// <summary>
/// Offline preview in the same logical map pixels as MapTracking and CRS.Simple. Only the viewport changes;
/// the route is never stretched onto an unrelated full-map image. No tile download or navigation state.
/// </summary>
public static class LootTrackerRouteMapPreview
{
    public sealed record MapDefinition(string Id, string Label, string Tiles, int Size, int RefZoom,
        int TileSize, int MinNativeZoom, int MaxNativeZoom);
    public sealed record Viewport(double X, double Y, double Width, double Height)
    {
        public string ViewBox => string.Join(" ", new[] { X, Y, Width, Height }.Select(LootTrackerCharts.MapNumber));
    }
    public sealed record Tile(string RelativePath, double X, double Y, double Side);
    public enum Availability { Ready, MissingMap, MissingTiles, NoRoute }
    public sealed record Preview(Availability Status, Viewport View, IReadOnlyList<Tile> Tiles,
        IReadOnlyList<LootTrackerCharts.RouteLine> Lines, IReadOnlyList<LootTrackerCharts.ResourceNode> Nodes)
    {
        public IReadOnlyList<LootTrackerCharts.SessionMarker> Markers { get; init; } = [];
        public double UnitsPerPixel => View.Width / 640;
    }

    private const double Aspect = 16d / 9;
    private const int MaxManifestBytes = 1024 * 1024;

    public static IReadOnlyDictionary<string, MapDefinition> Load(string? directory)
    {
        if (!LocalDirectory(directory, out var root)) return new Dictionary<string, MapDefinition>();
        try
        {
            var path = Path.Combine(root, "manifest.json");
            if (!File.Exists(path) || new FileInfo(path).Length > MaxManifestBytes) return new Dictionary<string, MapDefinition>();
            return ParseManifest(File.ReadAllText(path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return new Dictionary<string, MapDefinition>(); }
    }

    public static IReadOnlyDictionary<string, MapDefinition> ParseManifest(string json)
    {
        var result = new Dictionary<string, MapDefinition>(StringComparer.Ordinal);
        if (json.Length > MaxManifestBytes) return result;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("maps", out var maps) || maps.ValueKind != JsonValueKind.Array) return result;
            var duplicateIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in maps.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) continue;
                var id = String(row, "id"); var tiles = String(row, "tiles");
                var size = Integer(row, "size"); var reference = Integer(row, "refZoom");
                var tileSize = OptionalInteger(row, "tileSize", 256);
                var min = OptionalInteger(row, "minNativeZoom", 0); var max = Integer(row, "maxNativeZoom");
                if (id is null || tiles is null || size is null || reference is null || max is null || tileSize is null || min is null) continue;
                var definition = new MapDefinition(id, String(row, "label") ?? id, tiles, size.Value,
                    reference.Value, tileSize.Value, min.Value, max.Value);
                if (!Valid(definition)) continue;
                if (!result.TryAdd(id, definition)) duplicateIds.Add(id);
            }
            foreach (var id in duplicateIds) result.Remove(id);
        }
        catch (JsonException) { result.Clear(); }
        return result;
    }

    public static Preview Build(LootTrackerSession session, string? mapId, MapDefinition? map,
        Func<string, bool> tileExists, LootTrackerSessionReplay? replay = null)
    {
        session = replay?.Session ?? session;
        var routeSession = replay is null ? session : session with { FarmRouteVersion = 0 };
        if (mapId is null || map is null || map.Id != mapId || !Valid(map))
        {
            if (replay is null || mapId is null)
                return new(Availability.MissingMap, new(0, 0, 440, 247.5), [], [], []);
            var schematicLines = replay.IsPrepared ? replay.PreparedParts.Where(part => part.MapId == mapId).Select(part => part.Line).ToArray()
                : LootTrackerCharts.MapRoute(routeSession, mapId, double.MaxValue);
            var schematicNodes = LootTrackerCharts.MapResourceNodes(session, mapId, double.MaxValue);
            var schematicMarkers = LootTrackerCharts.MapMarkers(session, mapId, double.MaxValue);
            var center = replay.LastPosition?.MapId == mapId ? replay.LastPosition : null;
            var last = routeSession.RouteSegments.LastOrDefault(row => row.MapId == mapId && !row.Points.IsDefaultOrEmpty)?.Points.LastOrDefault();
            var size = session.GatherVisits.FirstOrDefault(row => row.MapId == mapId)?.MapSize ?? 8192;
            return new(Availability.MissingMap, FollowView(center?.X ?? last?.X ?? size / 2d,
                center?.Y ?? last?.Y ?? size / 2d, size, false), [], schematicLines, schematicNodes) { Markers = schematicMarkers };
        }
        var lines = replay?.IsPrepared == true ? replay.PreparedParts.Where(part => part.MapId == mapId).Select(part => part.Line).ToArray()
            : LootTrackerCharts.MapRoute(routeSession, mapId, map.Size);
        var nodes = LootTrackerCharts.MapResourceNodes(session, mapId, map.Size);
        var route = replay?.IsPrepared == true ? System.Collections.Immutable.ImmutableArray<LootTrackerRouteSegment>.Empty : LootTrackerPresentation.DisplayedRouteSegments(routeSession);
        var points = route.IsDefaultOrEmpty ? [] : route
            .Where(segment => segment.MapId == mapId && !segment.Points.IsDefaultOrEmpty)
            .SelectMany(segment => segment.Points).Where(point => LootTrackerCharts.InMap(point.X, point.Y, map.Size)).ToList();
        var markers = LootTrackerCharts.MapMarkers(session, mapId, map.Size);
        points.AddRange(markers.Select(row => new LootTrackerRoutePoint(Parse(row.X), Parse(row.Y))));
        points.AddRange(nodes.Select(node => new LootTrackerRoutePoint(Parse(node.X), Parse(node.Y))));
        if (replay?.LastPosition is { } observed && observed.MapId == mapId && LootTrackerCharts.InMap(observed.X, observed.Y, map.Size))
            points.Add(new(observed.X, observed.Y));
        if (points.Count == 0 && replay?.IsPrepared == true)
        {
            var visible = replay.PreparedParts.LastOrDefault(part => part.MapId == mapId && !part.Points.IsDefaultOrEmpty);
            if (visible is not null && LootTrackerCharts.InMap(visible.Points[^1].X, visible.Points[^1].Y, map.Size))
                points.Add(new(visible.Points[^1].X, visible.Points[^1].Y));
        }
        if (points.Count == 0) return new(Availability.NoRoute, new(0, 0, map.Size, map.Size / Aspect), [], [], []);
        var follow = replay?.LastPosition;
        var view = replay is null ? View(points, map.Size) : FollowView(
            follow?.MapId == mapId ? follow.X : points[^1].X,
            follow?.MapId == mapId ? follow.Y : points[^1].Y, map.Size);
        // Match Leaflet's tile geometry: logical side = tileSize * 2^(refZoom - nativeZoom).
        var preferred = Math.Clamp((int)Math.Ceiling(map.RefZoom + Math.Log2(640 / view.Width)), map.MinNativeZoom, map.MaxNativeZoom);
        for (var zoom = preferred; zoom >= map.MinNativeZoom; zoom--)
        {
            var tiles = VisibleTiles(map, view, zoom);
            if (tiles.Count > 0 && tiles.All(tile => tileExists(tile.RelativePath)))
                return new(Availability.Ready, view, tiles, lines, nodes) { Markers = markers };
        }
        return new(Availability.MissingTiles, view, [], lines, nodes) { Markers = markers };
    }

    /// <summary>Resolves only the validated relative paths emitted by the manifest, below the product mapdata folder.</summary>
    public static bool TileExists(string directory, string relativePath)
    {
        if (!SafeRelativePath(relativePath) || !LocalDirectory(directory, out var localRoot)) return false;
        try
        {
            var root = localRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return false; }
    }

    /// <summary>Replay follows the last confirmed position at a fixed, map-relative scale.</summary>
    private static Viewport FollowView(double x, double y, int mapSize, bool clamp = true)
    {
        var width = mapSize / 16d; var height = width / Aspect;
        return new(clamp ? Math.Clamp(x - width / 2, 0, mapSize - width) : x - width / 2,
            clamp ? Math.Clamp(y - height / 2, 0, mapSize - height) : y - height / 2, width, height);
    }

    private static Viewport View(List<LootTrackerRoutePoint> points, int mapSize)
    {
        var minX = points.Min(point => point.X); var maxX = points.Max(point => point.X);
        var minY = points.Min(point => point.Y); var maxY = points.Max(point => point.Y);
        var width = Math.Max(mapSize / 16d, Math.Max((maxX - minX) * 1.3, (maxY - minY) * Aspect * 1.3));
        var height = width / Aspect;
        return new(Origin((minX + maxX) / 2, width), Origin((minY + maxY) / 2, height), width, height);

        double Origin(double center, double extent) => extent >= mapSize ? (mapSize - extent) / 2
            : Math.Clamp(center - extent / 2, 0, mapSize - extent);
    }

    private static IReadOnlyList<Tile> VisibleTiles(MapDefinition map, Viewport view, int zoom)
    {
        var side = map.TileSize * Math.Pow(2, map.RefZoom - zoom);
        var firstX = (int)Math.Floor(Math.Max(0, view.X) / side);
        var firstY = (int)Math.Floor(Math.Max(0, view.Y) / side);
        var lastX = (int)Math.Ceiling(Math.Min(map.Size, view.X + view.Width) / side) - 1;
        var lastY = (int)Math.Ceiling(Math.Min(map.Size, view.Y + view.Height) / side) - 1;
        if ((long)(lastX - firstX + 1) * (lastY - firstY + 1) > 256) return [];
        var tiles = new List<Tile>();
        for (var y = firstY; y <= lastY; y++)
        for (var x = firstX; x <= lastX; x++)
        {
            var path = map.Tiles.Replace("{z}", zoom.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{x}", x.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{y}", y.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
            tiles.Add(new(path, x * side, y * side, side));
        }
        return tiles;
    }

    private static bool Valid(MapDefinition map) => !string.IsNullOrWhiteSpace(map.Id) && map.Id.Length <= 128
        && map.Size is > 0 and <= 1048576 && map.RefZoom is >= 0 and <= 20
        && map.MinNativeZoom >= 0 && map.MaxNativeZoom >= map.MinNativeZoom && map.MaxNativeZoom <= 20
        && map.TileSize is 256 or 512
        && map.Size / (map.TileSize * Math.Pow(2, map.RefZoom - map.MaxNativeZoom)) <= 65536
        && SafeTemplate(map.Tiles);

    private static bool SafeTemplate(string path) => path.Count(character => character == '{') == 3
        && path.Count(character => character == '}') == 3 && path.Contains("{z}", StringComparison.Ordinal)
        && path.Contains("{x}", StringComparison.Ordinal) && path.Contains("{y}", StringComparison.Ordinal)
        && SafeRelativePath(path.Replace("{z}", "0", StringComparison.Ordinal).Replace("{x}", "0", StringComparison.Ordinal).Replace("{y}", "0", StringComparison.Ordinal));

    private static bool SafeRelativePath(string path) => path.Length <= 240 && !path.Contains("..", StringComparison.Ordinal)
        && Regex.IsMatch(path, @"\A[A-Za-z0-9_-]+(?:/[A-Za-z0-9_-]+)*\.(?:webp|png|jpg|jpeg)\z", RegexOptions.CultureInvariant);
    private static string? String(JsonElement row, string key) => row.TryGetProperty(key, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static int? Integer(JsonElement row, string key) => row.TryGetProperty(key, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
    private static int? OptionalInteger(JsonElement row, string key, int fallback) => row.TryGetProperty(key, out _)
        ? Integer(row, key) : fallback;
    private static bool LocalDirectory(string? directory, out string fullPath)
    {
        fullPath = "";
        if (string.IsNullOrWhiteSpace(directory)) return false;
        try
        {
            var path = Path.GetFullPath(directory);
            if (path.StartsWith(@"\\", StringComparison.Ordinal) || new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Network) return false;
            fullPath = path;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return false; }
    }
    private static double Parse(string value) => double.Parse(value, CultureInfo.InvariantCulture);
}
