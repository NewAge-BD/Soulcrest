using System.Collections.Frozen;
using System.Text.Json;

namespace Soulcrest.Core.Instances;

public enum InstanceMapKind { Unknown, OpenWorld, Instance }
public sealed record InstanceNames(string? De, string? En);
public sealed record InstanceMap(int Id, InstanceNames Names, InstanceMapKind Kind, string? Category,
    IReadOnlyList<string> SourceRefs, string? SoulcrestMapId = null, InstanceNames? CategoryNames = null);
public sealed record InstanceDungeon(int Id, IReadOnlyList<int> MapIds, InstanceNames Names,
    string? Category, string? Mode, IReadOnlyList<string> SourceRefs, bool? InstanceLayer = null, bool? Active = null, InstanceNames? CategoryNames = null, InstanceNames? ModeNames = null);

/// <summary>Offline facts only. A definition ID is never a unique visit or server instance ID.</summary>
public sealed class InstanceCatalog
{
    private readonly FrozenDictionary<int, InstanceMap> _maps;
    private readonly FrozenDictionary<int, InstanceDungeon> _dungeons;
    private readonly FrozenDictionary<int, IReadOnlyList<InstanceDungeon>> _byMap;
    public static InstanceCatalog Empty { get; } = new([], []);
    private static readonly Lazy<InstanceCatalog> Embedded = new(LoadEmbedded);
    public static InstanceCatalog Default => Embedded.Value;
    public IReadOnlyDictionary<int, InstanceMap> Maps => _maps;
    public IReadOnlyDictionary<int, InstanceDungeon> Dungeons => _dungeons;
    public bool HasData => _maps.Count != 0;

    private InstanceCatalog(IEnumerable<InstanceMap> maps, IEnumerable<InstanceDungeon> dungeons)
    {
        _maps = maps.ToFrozenDictionary(x => x.Id);
        _dungeons = dungeons.ToFrozenDictionary(x => x.Id);
        _byMap = _dungeons.Values.SelectMany(d => d.MapIds.Select(id => (Id: id, Dungeon: d)))
            .GroupBy(x => x.Id).ToFrozenDictionary(g => g.Key,
                g => (IReadOnlyList<InstanceDungeon>)Array.AsReadOnly(g.Select(x => x.Dungeon).OrderBy(x => x.Id).ToArray()));
    }

    public InstanceMap? FindMap(int id) => _maps.GetValueOrDefault(id);
    public InstanceDungeon? FindDungeon(int id) => _dungeons.GetValueOrDefault(id);
    public IReadOnlyList<InstanceDungeon> DungeonsForMap(int id) => _byMap.GetValueOrDefault(id) ?? [];

    public static InstanceCatalog FromJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != 1) throw new InvalidDataException("Unknown instance catalogue schema.");
            var maps = new List<InstanceMap>(); var dungeons = new List<InstanceDungeon>();
            var mapIds = new HashSet<int>(); var dungeonIds = new HashSet<int>();
            foreach (var row in Rows(root, "maps"))
            {
                var id = Id(row); if (!mapIds.Add(id)) throw new InvalidDataException("Duplicate map definition.");
                var kind = Text(row, "kind") switch
                {
                    "openWorld" => InstanceMapKind.OpenWorld,
                    "instance" => InstanceMapKind.Instance,
                    "unknown" => InstanceMapKind.Unknown,
                    _ => throw new InvalidDataException("Unknown map kind.")
                };
                maps.Add(new(id, Names(row), kind, Text(row, "category"), Sources(row), Text(row, "soulcrestMapId"), Names(row, "categoryNames")));
            }
            foreach (var row in Rows(root, "dungeons"))
            {
                var id = Id(row); if (!dungeonIds.Add(id)) throw new InvalidDataException("Duplicate dungeon definition.");
                var ids = new List<int>(); var seen = new HashSet<int>();
                foreach (var value in Rows(row, "mapIds"))
                {
                    var map = value.GetInt32();
                    if (map <= 0 || !seen.Add(map) || !mapIds.Contains(map)) throw new InvalidDataException("Unresolved or duplicate dungeon map reference.");
                    ids.Add(map);
                }
                dungeons.Add(new(id, ids.AsReadOnly(), Names(row), Text(row, "category"), Text(row, "mode"), Sources(row), Flag(row, "instanceLayer"), Flag(row, "active"), Names(row, "categoryNames"), Names(row, "modeNames")));
            }
            return new(maps, dungeons);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { throw new InvalidDataException("Invalid instance catalogue.", error); }
    }

    private static IEnumerable<JsonElement> Rows(JsonElement element, string name)
    {
        var array = element.GetProperty(name);
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 100000) throw new InvalidDataException("Invalid catalogue array.");
        return array.EnumerateArray();
    }
    private static int Id(JsonElement row)
    {
        var id = row.GetProperty("id").GetInt32();
        return id > 0 ? id : throw new InvalidDataException("Nonpositive catalogue definition.");
    }
    private static string? Text(JsonElement row, string name)
    {
        if (!row.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        var text = value.GetString();
        if (text?.Length > 4096) throw new InvalidDataException("Oversized catalogue label.");
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
    private static bool? Flag(JsonElement row, string name) => !row.TryGetProperty(name, out var value)
        || value.ValueKind == JsonValueKind.Null ? null : value.GetBoolean();
    private static InstanceNames Names(JsonElement row, string property = "names") => row.TryGetProperty(property, out var names)
        && names.ValueKind != JsonValueKind.Null ? new(Text(names, "de"), Text(names, "en")) : new(null, null);
    private static IReadOnlyList<string> Sources(JsonElement row)
    {
        var sources = new List<string>();
        foreach (var value in Rows(row, "sourceRefs"))
        {
            var text = value.GetString();
            if (string.IsNullOrWhiteSpace(text) || text.Length > 4096) throw new InvalidDataException("Invalid catalogue evidence reference.");
            sources.Add(text);
        }
        return sources.AsReadOnly();
    }
    private static InstanceCatalog LoadEmbedded()
    {
        using var stream = typeof(InstanceCatalog).Assembly.GetManifestResourceStream("Soulcrest.InstanceCatalog.json");
        if (stream is null) return Empty;
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        if (string.IsNullOrWhiteSpace(json)) return Empty;
        try { return FromJson(json); }
        catch (InvalidDataException) { return Empty; } // Unknown facts cannot grant context authority.
    }
}
