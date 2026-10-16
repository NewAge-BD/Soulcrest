using System.Text.Json;

namespace Soulcrest.App.Services;

/// <summary>An explicitly documented cube group. Connections are orientation aids, never spawn order.</summary>
public sealed record HiddenCubeGroup(string Id, int Number, string Color, IReadOnlyList<int> Markers,
    IReadOnlyList<HiddenCubeConnection> Connections);

/// <summary>One undirected connection between existing map markers, in map coordinates.</summary>
public sealed record HiddenCubeConnection(int FromMarker, int ToMarker, double FromX, double FromY, double ToX, double ToY);

public static partial class MapPetMarkers
{
    private static IReadOnlyList<HiddenCubeGroup> ReadHiddenCubeGroups(JsonElement root, IReadOnlyList<string> categories)
    {
        if (!root.TryGetProperty("hiddenCubeGroups", out var rawGroups) || rawGroups.ValueKind != JsonValueKind.Array)
            return [];
        var markers = root.GetProperty("markers");
        // Examine all raw claims first: a malformed competing definition must not silently win
        // or leave a seemingly unambiguous group behind (same contract as the interactive map).
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var numbers = new Dictionary<int, int>();
        var claims = new Dictionary<int, int>();
        var positions = new Dictionary<(double X, double Y), int>();
        foreach (var raw in rawGroups.EnumerateArray())
        {
            if (raw.ValueKind != JsonValueKind.Object) continue;
            if (raw.TryGetProperty("id", out var rawId) && rawId.ValueKind == JsonValueKind.String && rawId.GetString() is { } id)
                CountCubeClaim(ids, id);
            if (raw.TryGetProperty("number", out var rawNumber) && CubeInt(rawNumber, out var number)) CountCubeClaim(numbers, number);
            if (!raw.TryGetProperty("markers", out var rawMembers) || rawMembers.ValueKind != JsonValueKind.Array) continue;
            var members = new HashSet<int>();
            foreach (var member in rawMembers.EnumerateArray())
                if (CubeInt(member, out var index) && index >= 0 && index < markers.GetArrayLength() && members.Add(index))
                {
                    CountCubeClaim(claims, index);
                    var marker = markers[index];
                    if (marker.ValueKind == JsonValueKind.Array && marker.GetArrayLength() >= 3
                        && CubeDouble(marker[1], out var x) && double.IsFinite(x) && CubeDouble(marker[2], out var y) && double.IsFinite(y))
                        CountCubeClaim(positions, (x, y));
                }
        }
        var groups = new List<HiddenCubeGroup>();
        foreach (var raw in rawGroups.EnumerateArray())
        {
            if (raw.ValueKind != JsonValueKind.Object
                || !raw.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.String
                || idValue.GetString() is not { Length: > 0 } id || string.IsNullOrWhiteSpace(id)
                || !raw.TryGetProperty("number", out var numberValue) || !CubeInt(numberValue, out var number) || number < 1
                || !raw.TryGetProperty("color", out var colorValue) || colorValue.ValueKind != JsonValueKind.String
                || colorValue.GetString() is not { } color || !ValidCubeColor(color)
                || !raw.TryGetProperty("markers", out var membersValue) || membersValue.ValueKind != JsonValueKind.Array
                || !raw.TryGetProperty("connections", out var linksValue) || linksValue.ValueKind != JsonValueKind.Array)
                continue;
            var members = new HashSet<int>();
            var valid = true;
            foreach (var member in membersValue.EnumerateArray())
            {
                if (!CubeInt(member, out var index) || index < 0 || index >= markers.GetArrayLength() || !members.Add(index)
                    || !CubeMarker(markers[index], categories, id))
                {
                    valid = false;
                    break;
                }
            }
            if (!valid || members.Count == 0 || ids[id] != 1 || numbers[number] != 1
                || members.Any(i => claims[i] != 1 || positions[(markers[i][1].GetDouble(), markers[i][2].GetDouble())] != 1)
                || members.Select(i => markers[i][0].GetInt32()).Distinct().Skip(1).Any()) continue;
            var links = new List<HiddenCubeConnection>();
            var pairs = new HashSet<(int, int)>();
            foreach (var link in linksValue.EnumerateArray())
            {
                if (link.ValueKind != JsonValueKind.Array || link.GetArrayLength() != 2
                    || !CubeInt(link[0], out var from) || !CubeInt(link[1], out var to)
                    || from == to || !members.Contains(from) || !members.Contains(to)
                    || !pairs.Add((Math.Min(from, to), Math.Max(from, to))))
                {
                    valid = false;
                    break;
                }
                links.Add(new(from, to, markers[from][1].GetDouble(), markers[from][2].GetDouble(),
                    markers[to][1].GetDouble(), markers[to][2].GetDouble()));
            }
            if (!valid || !ConnectedCubeGroup(members, links)) continue;
            groups.Add(new(id, number, color, members.Order().ToArray(), links));
        }
        return groups;
    }

    private static void CountCubeClaim<TKey>(Dictionary<TKey, int> claims, TKey key) where TKey : notnull
    {
        claims[key] = claims.GetValueOrDefault(key) + 1;
    }

    private static bool CubeMarker(JsonElement marker, IReadOnlyList<string> categories, string group)
    {
        if (marker.ValueKind != JsonValueKind.Array || marker.GetArrayLength() < 3
            || !CubeInt(marker[0], out var category) || category < 0 || category >= categories.Count
            || categories[category] != "Collectibles/Hidden Cube"
            || !CubeDouble(marker[1], out var x) || !double.IsFinite(x)
            || !CubeDouble(marker[2], out var y) || !double.IsFinite(y)) return false;
        return marker.GetArrayLength() <= 7 || marker[7].ValueKind != JsonValueKind.Object
            || !marker[7].TryGetProperty("hiddenCubeGroup", out var assigned)
            || assigned.ValueKind == JsonValueKind.String && assigned.GetString() == group;
    }

    private static bool CubeInt(JsonElement value, out int result)
    {
        result = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out result);
    }

    private static bool CubeDouble(JsonElement value, out double result)
    {
        result = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out result);
    }

    private static bool ValidCubeColor(string color) => color.Length == 7 && color[0] == '#'
        && color.AsSpan(1).ContainsAnyExcept("0123456789abcdefABCDEF") == false;

    private static bool ConnectedCubeGroup(IReadOnlySet<int> members, IReadOnlyList<HiddenCubeConnection> links)
    {
        var reached = new HashSet<int> { members.First() };
        var pending = new Queue<int>(reached);
        while (pending.TryDequeue(out var current))
            foreach (var link in links)
            {
                var next = link.FromMarker == current ? link.ToMarker : link.ToMarker == current ? link.FromMarker : -1;
                if (next >= 0 && reached.Add(next)) pending.Enqueue(next);
            }
        return reached.Count == members.Count;
    }
}
