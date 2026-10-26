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
        Func<string, bool> tileExists)
    {
        if (mapId is null || map is null || map.Id != mapId || !Valid(map))
            return new(Availability.MissingMap, new(0, 0, 440, 247.5), [], [], []);
        var lines = LootTrackerCharts.MapRoute(session, mapId, map.Size);
        var nodes = LootTrackerCharts.MapResourceNodes(session, mapId, map.Size);
        var route = LootTrackerPresentation.DisplayedRouteSegments(session);
        var points = route.IsDefaultOrEmpty ? [] : route
            .Where(segment => segment.MapId == mapId && !segment.Points.IsDefaultOrEmpty)
            .SelectMany(segment => segment.Points).Where(point => LootTrackerCharts.InMap(point.X, point.Y, map.Size)).ToList();
        if (points.Count == 0) return new(Availability.NoRoute, new(0, 0, map.Size, map.Size / Aspect), [], [], []);
        points.AddRange(nodes.Select(node => new LootTrackerRoutePoint(Parse(node.X), Parse(node.Y))));
        var view = View(points, map.Size);
        // Match Leaflet's tile geometry: logical side = tileSize * 2^(refZoom - nativeZoom).
        var preferred = Math.Clamp((int)Math.Ceiling(map.RefZoom + Math.Log2(640 / view.Width)), map.MinNativeZoom, map.MaxNativeZoom);
        for (var zoom = preferred; zoom >= map.MinNativeZoom; zoom--)
        {
            var tiles = VisibleTiles(map, view, zoom);
            if (tiles.Count > 0 && tiles.All(tile => tileExists(tile.RelativePath)))
                return new(Availability.Ready, view, tiles, lines, nodes);
        }
        return new(Availability.MissingTiles, view, [], lines, nodes);
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
