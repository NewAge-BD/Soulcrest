using System.Collections.Immutable;
using System.Text.Json;

namespace Soulcrest.App.Services;

public sealed record FarmLocationMatch(string Name, bool Approximate);

/// <summary>
/// User decision 2026-10-26: use the nearest named map-region label as an explicitly approximate
/// farming location. Label centres do not establish territory boundaries. Loaded once from the
/// existing offline map package, without executing its JavaScript or reading native game files.
/// </summary>
public sealed class FarmLocationCatalog
{
    private const int MaximumManifestBytes = 1024 * 1024;
    private const int MaximumMapBytes = 16 * 1024 * 1024;
    private sealed record Label(double X, double Y, string En, string? De);
    private sealed record MapLabels(int Size, ImmutableArray<Label> Labels);
    private readonly IReadOnlyDictionary<string, MapLabels> _maps;

    private FarmLocationCatalog(IReadOnlyDictionary<string, MapLabels> maps) => _maps = maps;

    public static FarmLocationCatalog Load(string? mapDataDirectory)
    {
        var maps = new Dictionary<string, MapLabels>(StringComparer.Ordinal);
        if (!LocalDirectory(mapDataDirectory, out var directory)) return new(maps);
        try
        {
            if (!ReadLocalFile(Path.Combine(directory, "manifest.json"), MaximumManifestBytes, out var manifest)) return new(maps);
            using var document = JsonDocument.Parse(manifest);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("maps", out var definitions) || definitions.ValueKind != JsonValueKind.Array
                || definitions.GetArrayLength() > 128) return new(maps);
            var duplicateIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var definition in definitions.EnumerateArray())
            {
                if (definition.ValueKind != JsonValueKind.Object || Text(definition, "id") is not { } id || !SafeMapId(id)
                    || !definition.TryGetProperty("size", out var sizeValue) || sizeValue.ValueKind != JsonValueKind.Number || !sizeValue.TryGetInt32(out var size)
                    || size is <= 0 or > 1048576) continue;
                if (maps.ContainsKey(id)) { duplicateIds.Add(id); continue; }
                var path = Path.Combine(directory, id, "data.js");
                if (ReadLocalFile(path, MaximumMapBytes, out var text) && ParseMap(text, id, size) is { } map)
                    maps.Add(id, map);
            }
            foreach (var id in duplicateIds) maps.Remove(id);
        }
        catch (Exception exception) when (ReadError(exception)) { maps.Clear(); }
        return new(maps);
    }

    /// <summary>Requires the recorder's qualified visual snapshot. No position, map or label is inferred.</summary>
    public FarmLocationMatch? Resolve(RecorderPositionSnapshot snapshot, string language)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var position = snapshot.Position;
        if (position is null || string.IsNullOrWhiteSpace(position.MapId) || !_maps.TryGetValue(position.MapId, out var map)
            || !InMap(position.X, position.Y, map.Size)) return null;
        Label? best = null;
        var bestDistance = double.PositiveInfinity;
        var ambiguous = false;
        foreach (var label in map.Labels)
        {
            var dx = label.X - position.X; var dy = label.Y - position.Y;
            var distance = dx * dx + dy * dy;
            if (distance < bestDistance) { best = label; bestDistance = distance; ambiguous = false; }
            else if (distance == bestDistance && best is not null
                && (!string.Equals(label.En, best.En, StringComparison.Ordinal) || !string.Equals(label.De, best.De, StringComparison.Ordinal)))
                ambiguous = true;
        }
        return best is null || ambiguous ? null : new(language == "de" && best.De is { Length: > 0 } german ? german : best.En, true);
    }

    private static MapLabels? ParseMap(string text, string id, int size)
    {
        try
        {
            // Accept only the builder's data assignment. The remaining payload must be JSON,
            // followed by one semicolon; additional script content is rejected, never evaluated.
            const string initializer = "window.SoulcrestMaps = window.SoulcrestMaps || {};";
            if (!text.StartsWith(initializer, StringComparison.Ordinal)) return null;
            var body = text.AsSpan(initializer.Length).Trim();
            var assignment = "window.SoulcrestMaps[" + JsonSerializer.Serialize(id) + "] = ";
            if (!body.StartsWith(assignment, StringComparison.Ordinal) || !body.EndsWith(";", StringComparison.Ordinal)) return null;
            using var document = JsonDocument.Parse(body[assignment.Length..^1].ToString());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Text(root, "id") != id
                || !root.TryGetProperty("size", out var sizeValue) || sizeValue.ValueKind != JsonValueKind.Number || !sizeValue.TryGetInt32(out var payloadSize) || payloadSize != size
                || !root.TryGetProperty("categories", out var categories) || categories.ValueKind != JsonValueKind.Array || categories.GetArrayLength() > 4096
                || !root.TryGetProperty("markers", out var markers) || markers.ValueKind != JsonValueKind.Array) return null;
            var eligible = categories.EnumerateArray().Select(category => category.ValueKind == JsonValueKind.Object
                && Text(category, "group") == "Regions" && Text(category, "name") is "Region" or "Village" or "Battlefield" or "Hunting Ground").ToArray();
            var labels = ImmutableArray.CreateBuilder<Label>();
            foreach (var marker in markers.EnumerateArray())
            {
                if (marker.ValueKind != JsonValueKind.Array || marker.GetArrayLength() < 4
                    || marker[0].ValueKind != JsonValueKind.Number || !marker[0].TryGetInt32(out var category) || category < 0 || category >= eligible.Length || !eligible[category]
                    || marker[1].ValueKind != JsonValueKind.Number || marker[2].ValueKind != JsonValueKind.Number
                    || !marker[1].TryGetDouble(out var x) || !marker[2].TryGetDouble(out var y) || !InMap(x, y, size)
                    || Name(marker[3]) is not { } en) continue;
                var de = marker.GetArrayLength() > 4 ? Name(marker[4]) : null;
                labels.Add(new(x, y, en, de));
            }
            return labels.Count > 0 ? new(size, labels.ToImmutable()) : null;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException) { return null; }
    }

    private static bool InMap(double x, double y, int size) => double.IsFinite(x) && double.IsFinite(y)
        && x >= 0 && y >= 0 && x <= size && y <= size;
    private static string? Name(JsonElement value) => value.ValueKind == JsonValueKind.String && value.GetString()?.Trim() is { Length: > 0 and <= 256 } name
        && !name.Any(char.IsControl) ? name : null;
    private static string? Text(JsonElement value, string key) => value.TryGetProperty(key, out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
    private static bool SafeMapId(string id) => id.Length is > 0 and <= 128
        && id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static bool LocalDirectory(string? path, out string directory)
    {
        directory = "";
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
            var full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\", StringComparison.Ordinal) || new DriveInfo(Path.GetPathRoot(full)!).DriveType == DriveType.Network
                || !Directory.Exists(full) || HasReparseAncestor(new DirectoryInfo(full))) return false;
            directory = full;
            return true;
        }
        catch (Exception exception) when (ReadError(exception)) { return false; }
    }

    private static bool ReadLocalFile(string path, int maximumBytes, out string text)
    {
        text = "";
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > maximumBytes || (file.Attributes & FileAttributes.ReparsePoint) != 0
                || file.Directory is null || HasReparseAncestor(file.Directory)) return false;
            // The size check also runs against the opened stream, so a replaced/growing cache
            // cannot turn this startup projection into an unbounded file read.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > maximumBytes) return false;
            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd();
            return text.Length <= maximumBytes;
        }
        catch (Exception exception) when (ReadError(exception)) { return false; }
    }

    private static bool HasReparseAncestor(DirectoryInfo? directory)
    {
        for (var current = directory; current is not null; current = current.Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) return true;
        return false;
    }

    private static bool ReadError(Exception exception) => exception is IOException or UnauthorizedAccessException
        or ArgumentException or NotSupportedException or JsonException or InvalidOperationException;
}
