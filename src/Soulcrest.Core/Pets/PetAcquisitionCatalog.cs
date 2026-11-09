using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Soulcrest.Core.Network;

namespace Soulcrest.Core.Pets;

public enum PetAcquisitionCategory { Expedition, Transcendence }
public enum PetGlobalAvailability { Available, Upcoming, Unconfirmed }

public sealed record PetAcquisitionMapAvailability
{
    internal PetAcquisitionMapAvailability(int mapId, PetGlobalAvailability status, string checkedAt,
        IReadOnlyList<string> sourceRefs) => (MapId, Status, CheckedAt, SourceRefs) =
        (mapId, status, checkedAt, sourceRefs);

    public int MapId { get; }
    public PetGlobalAvailability Status { get; }
    public string CheckedAt { get; }
    public IReadOnlyList<string> SourceRefs { get; }
}

public sealed record PetAcquisitionNpc
{
    internal PetAcquisitionNpc(int id, string? en, string? de, IReadOnlyList<int> mapIds,
        IReadOnlyList<string> sourceRefs) => (Id, En, De, MapIds, SourceRefs) = (id, en, de, mapIds, sourceRefs);

    public int Id { get; }
    public string? En { get; }
    public string? De { get; }
    public IReadOnlyList<int> MapIds { get; }
    public IReadOnlyList<string> SourceRefs { get; }
    public string DisplayName(string language) => language == "de" && De is not null ? De :
        En ?? De ?? "NPC " + Id.ToString(CultureInfo.InvariantCulture);
}

public sealed record PetAcquisitionSource
{
    internal PetAcquisitionSource(string id, string dungeonEn, string? dungeonDe, PetAcquisitionCategory category,
        IReadOnlyList<int> mapIds, IReadOnlyList<PetAcquisitionNpc> npcs,
        IReadOnlyList<PetAcquisitionMapAvailability> globalAvailability, IReadOnlyList<string> sourceRefs) =>
        (Id, DungeonEn, DungeonDe, Category, MapIds, Npcs, GlobalAvailability, SourceRefs) =
        (id, dungeonEn, dungeonDe, category, mapIds, npcs, globalAvailability, sourceRefs);

    public string Id { get; }
    public string DungeonEn { get; }
    public string? DungeonDe { get; }
    public PetAcquisitionCategory Category { get; }
    public IReadOnlyList<int> MapIds { get; }
    public IReadOnlyList<PetAcquisitionNpc> Npcs { get; }
    public IReadOnlyList<PetAcquisitionMapAvailability> GlobalAvailability { get; }
    public IReadOnlyList<string> SourceRefs { get; }
    // A source family does not establish an accessible difficulty or a drop probability.
    public string? Mode => null;
    public decimal? DropChance => null;
    public bool HasAvailableVariants => GlobalAvailability.Any(map => map.Status == PetGlobalAvailability.Available);
    public bool HasUpcomingVariants => GlobalAvailability.Any(map => map.Status == PetGlobalAvailability.Upcoming);
    public bool HasUnconfirmedVariants => GlobalAvailability.Any(map => map.Status == PetGlobalAvailability.Unconfirmed);

    // The same original NPC name may occur in both open and future variants. Keep those statuses separate.
    public IEnumerable<PetAcquisitionNpc> NpcsWithAvailability(PetGlobalAvailability status) =>
        Npcs.Where(npc => GlobalAvailability.Any(map => map.Status == status && npc.MapIds.Contains(map.MapId)));
    public string DungeonDisplayName(string language) => language == "de" && DungeonDe is not null ? DungeonDe : DungeonEn;
}

public sealed record PetAcquisitionEntry
{
    internal PetAcquisitionEntry(int petNumber, int soulItemId, string newSince,
        IReadOnlyList<PetAcquisitionSource> sources, IReadOnlyList<string> sourceRefs) =>
        (PetNumber, SoulItemId, NewSince, Sources, SourceRefs) = (petNumber, soulItemId, newSince, sources, sourceRefs);

    public int PetNumber { get; }
    public int SoulItemId { get; }
    public string NewSince { get; }
    public IReadOnlyList<PetAcquisitionSource> Sources { get; }
    public IReadOnlyList<string> SourceRefs { get; }
}

/// <summary>Additional sourced acquisition locations, independent of navigable map markers and player progress.</summary>
public static class PetAcquisitionCatalog
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly Lazy<IReadOnlyDictionary<int, PetAcquisitionEntry>> Table = new(Load);

    public static IReadOnlyDictionary<int, PetAcquisitionEntry> All => Table.Value;
    public static PetAcquisitionEntry? Find(int petNumber) => Table.Value.GetValueOrDefault(petNumber);
    public static PetAcquisitionEntry? Find(int? petNumber) => petNumber is { } number ? Find(number) : null;

    private static IReadOnlyDictionary<int, PetAcquisitionEntry> Load()
    {
        using var stream = typeof(PetAcquisitionCatalog).Assembly.GetManifestResourceStream("Soulcrest.PetAcquisitionSources.json")
            ?? throw new InvalidDataException("Missing embedded pet acquisition sources.");
        using var reader = new StreamReader(stream);
        return FromJson(reader.ReadToEnd());
    }

    internal static IReadOnlyDictionary<int, PetAcquisitionEntry> FromJson(string json)
    {
        try
        {
            var file = JsonSerializer.Deserialize<CatalogFile>(json, Options);
            if (file?.SchemaVersion != 1 || file.Pets is null)
                throw Invalid();
            var result = new Dictionary<int, PetAcquisitionEntry>();
            var sourceIds = new HashSet<string>(StringComparer.Ordinal);
            var itemIds = new HashSet<int>();
            foreach (var pet in file.Pets)
            {
                if (pet is null || PetIdTable.Find(pet.PetNumber) is null || pet.SoulItemId <= 0
                    || !itemIds.Add(pet.SoulItemId) || pet.Sources is not { Length: > 0 }
                    || pet.NewSince is null || !DateOnly.TryParseExact(pet.NewSince, "yyyy-MM-dd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    throw Invalid();
                var sources = new List<PetAcquisitionSource>();
                foreach (var source in pet.Sources)
                {
                    if (source is null || !ValidId(source.Id) || !sourceIds.Add(source.Id!)
                        || string.IsNullOrWhiteSpace(source.DungeonEn) || InvalidOptionalLabel(source.DungeonDe)
                        || source.Mode is not null || source.DropChance is not null || source.Npcs is not { Length: > 0 })
                        throw Invalid();
                    var category = source.Category switch
                    {
                        "Expedition" => PetAcquisitionCategory.Expedition,
                        "Transcendence" => PetAcquisitionCategory.Transcendence,
                        _ => throw Invalid()
                    };
                    var maps = PositiveIds(source.MapIds);
                    if (source.GlobalAvailability is null || source.GlobalAvailability.Length != maps.Count)
                        throw Invalid();
                    var availability = new List<PetAcquisitionMapAvailability>();
                    var availabilityMapIds = new HashSet<int>();
                    foreach (var map in source.GlobalAvailability)
                    {
                        if (map is null || !maps.Contains(map.MapId) || !availabilityMapIds.Add(map.MapId)
                            || map.CheckedAt is null || !ValidDate(map.CheckedAt)) throw Invalid();
                        var status = map.Status switch
                        {
                            "available" => PetGlobalAvailability.Available,
                            "upcoming" => PetGlobalAvailability.Upcoming,
                            "unconfirmed" => PetGlobalAvailability.Unconfirmed,
                            _ => throw Invalid()
                        };
                        availability.Add(new(map.MapId, status, map.CheckedAt, Sources(map.SourceRefs)));
                    }
                    var npcs = new List<PetAcquisitionNpc>();
                    var npcIds = new HashSet<int>();
                    foreach (var npc in source.Npcs)
                    {
                        if (npc is null || npc.Id <= 0 || !npcIds.Add(npc.Id)
                            || InvalidOptionalLabel(npc.En) || InvalidOptionalLabel(npc.De)
                            || npc.En is null && npc.De is null)
                            throw Invalid();
                        var npcMaps = PositiveIds(npc.MapIds);
                        if (npcMaps.Any(map => !maps.Contains(map))) throw Invalid();
                        npcs.Add(new(npc.Id, npc.En, npc.De, npcMaps, Sources(npc.SourceRefs)));
                    }
                    if (maps.Any(map => !npcs.Any(npc => npc.MapIds.Contains(map)))) throw Invalid();
                    sources.Add(new(source.Id!, source.DungeonEn, source.DungeonDe, category,
                        maps, npcs.AsReadOnly(), availability.AsReadOnly(), Sources(source.SourceRefs)));
                }
                if (!result.TryAdd(pet.PetNumber, new(pet.PetNumber, pet.SoulItemId, pet.NewSince,
                    sources.AsReadOnly(), Sources(pet.SourceRefs)))) throw Invalid();
            }
            return new ReadOnlyDictionary<int, PetAcquisitionEntry>(result);
        }
        catch (JsonException error) { throw new InvalidDataException("Invalid pet acquisition sources.", error); }
    }

    private static IReadOnlyList<int> PositiveIds(int[]? values)
    {
        if (values is not { Length: > 0 } || values.Any(value => value <= 0)
            || values.Distinct().Count() != values.Length) throw Invalid();
        return Array.AsReadOnly(values.ToArray());
    }

    private static IReadOnlyList<string> Sources(string[]? values)
    {
        if (values is not { Length: > 0 } || values.Distinct(StringComparer.Ordinal).Count() != values.Length
            || values.Any(value => !Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))) throw Invalid();
        return Array.AsReadOnly(values.ToArray());
    }

    private static bool InvalidOptionalLabel(string? text) => text is not null && string.IsNullOrWhiteSpace(text);
    private static bool ValidDate(string value) => DateOnly.TryParseExact(value, "yyyy-MM-dd",
        CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    private static bool ValidId(string? id) => !string.IsNullOrWhiteSpace(id) && id[0] is >= 'a' and <= 'z'
        && id.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')
        && !id.EndsWith('-') && !id.Contains("--", StringComparison.Ordinal);
    private static InvalidDataException Invalid() => new("Invalid pet acquisition sources.");

    private sealed record CatalogFile(int SchemaVersion, EntryFile?[]? Pets);
    private sealed record EntryFile(int PetNumber, int SoulItemId, string? NewSince,
        SourceFile?[]? Sources, string[]? SourceRefs);
    private sealed record SourceFile(string? Id, string? DungeonEn, string? DungeonDe, string? Category,
        int[]? MapIds, NpcFile?[]? Npcs, AvailabilityFile?[]? GlobalAvailability,
        string? Mode, decimal? DropChance, string[]? SourceRefs);
    private sealed record AvailabilityFile(int MapId, string? Status, string? CheckedAt, string[]? SourceRefs);
    private sealed record NpcFile(int Id, string? En, string? De, int[]? MapIds, string[]? SourceRefs);
}
