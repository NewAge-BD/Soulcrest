using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Soulcrest.Core.Pets;

public sealed record PetDungeonSpawn(string Id, int NpcId, int PetNumber, double X, double Y,
    IReadOnlyList<string> SourceRefs);

public sealed record PetDungeonMap(string Id, int MapId, string En, string? De,
    string? VariantEn, string? VariantDe, string Image, string ImageSha256, int Width, int Height,
    IReadOnlyList<PetDungeonSpawn> Spawns, IReadOnlyList<string> SourceRefs)
{
    public string DisplayName(string language) => language == "de" && De is not null ? De : En;
    public string? VariantDisplayName(string language) => language == "de" && VariantDe is not null ? VariantDe : VariantEn;
}

/// <summary>
/// Proven dungeon placements in original image pixels. This catalogue is independent of the
/// navigable world maps, player tracking and saved pet progress (user request 2026-11-12).
/// </summary>
public static class PetDungeonMapCatalog
{
    private static readonly Lazy<IReadOnlyList<PetDungeonMap>> Table = new(Load);
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static IReadOnlyList<PetDungeonMap> All => Table.Value;
    public static IEnumerable<PetDungeonMap> ForPet(int petNumber) =>
        All.Where(map => map.Spawns.Any(spawn => spawn.PetNumber == petNumber));

    private static IReadOnlyList<PetDungeonMap> Load()
    {
        using var stream = typeof(PetDungeonMapCatalog).Assembly.GetManifestResourceStream("Soulcrest.PetDungeonMaps.json")
            ?? throw Invalid();
        using var reader = new StreamReader(stream);
        return FromJson(reader.ReadToEnd());
    }

    internal static IReadOnlyList<PetDungeonMap> FromJson(string json)
    {
        try
        {
            var file = JsonSerializer.Deserialize<CatalogFile>(json, Options);
            if (file?.SchemaVersion != 1 || file.Maps is not { Length: > 0 }) throw Invalid();
            var maps = new List<PetDungeonMap>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var images = new Dictionary<string, (string Hash, int Width, int Height)>(StringComparer.Ordinal);
            foreach (var map in file.Maps)
            {
                if (map is null || !ValidId(map.Id) || !keys.Add(map.Id!) || map.MapId <= 0
                    || string.IsNullOrWhiteSpace(map.En) || BadOptional(map.De)
                    || BadOptional(map.VariantEn) || BadOptional(map.VariantDe)
                    || map.Width is < 1 or > 16384 || map.Height is < 1 or > 16384
                    || map.Image is null || !Regex.IsMatch(map.Image, @"\Adungeon-maps/[a-z0-9][a-z0-9-]*\.(png|webp)\z",
                        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))
                    || map.ImageSha256 is not { Length: 64 } || map.ImageSha256.Any(character => !char.IsAsciiHexDigitLower(character))
                    || map.Spawns is not { Length: > 0 }) throw Invalid();
                var image = (map.ImageSha256, map.Width, map.Height);
                if (images.TryGetValue(map.Image, out var previous) && previous != image) throw Invalid();
                images[map.Image] = image;
                var spawns = new List<PetDungeonSpawn>();
                var spawnIds = new HashSet<string>(StringComparer.Ordinal);
                var positions = new HashSet<(int Pet, int Npc, double X, double Y)>();
                foreach (var spawn in map.Spawns)
                {
                    if (spawn is null || !ValidId(spawn.Id) || !spawnIds.Add(spawn.Id!) || spawn.NpcId <= 0
                        || spawn.X is not { } x || spawn.Y is not { } y || !double.IsFinite(x) || !double.IsFinite(y)
                        || x < 0 || y < 0 || x > map.Width || y > map.Height
                        || !positions.Add((spawn.PetNumber, spawn.NpcId, x, y))) throw Invalid();
                    // Presence or a matching name alone cannot establish a Soul source.
                    var acquisition = PetAcquisitionCatalog.Find(spawn.PetNumber);
                    if (acquisition is null || !acquisition.Sources.Any(source => source.MapIds.Contains(map.MapId)
                        && source.Npcs.Any(npc => npc.Id == spawn.NpcId && npc.MapIds.Contains(map.MapId)))) throw Invalid();
                    spawns.Add(new(spawn.Id!, spawn.NpcId, spawn.PetNumber, x, y, References(spawn.SourceRefs)));
                }
                maps.Add(new(map.Id!, map.MapId, map.En, map.De, map.VariantEn, map.VariantDe,
                    map.Image, map.ImageSha256, map.Width, map.Height, spawns.AsReadOnly(), References(map.SourceRefs)));
            }
            return maps.AsReadOnly();
        }
        catch (JsonException error) { throw new InvalidDataException("Invalid dungeon pet maps.", error); }
    }

    private static bool ValidId(string? text) => text is { Length: > 0 and <= 128 }
        && text.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '-')
        && text[0] != '-' && text[^1] != '-' && !text.Contains("--", StringComparison.Ordinal);
    private static bool BadOptional(string? text) => text is not null && string.IsNullOrWhiteSpace(text);
    private static IReadOnlyList<string> References(string[]? values)
    {
        if (values is not { Length: > 0 } || values.Distinct(StringComparer.Ordinal).Count() != values.Length
            || values.Any(value => !Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))) throw Invalid();
        return new ReadOnlyCollection<string>(values.ToArray());
    }
    private static InvalidDataException Invalid() => new("Invalid dungeon pet maps.");
    private sealed record CatalogFile(int SchemaVersion, MapFile?[]? Maps);
    private sealed record MapFile(string? Id, int MapId, string? En, string? De, string? VariantEn, string? VariantDe,
        string? Image, string? ImageSha256, int Width, int Height, SpawnFile?[]? Spawns, string[]? SourceRefs);
    private sealed record SpawnFile(string? Id, int NpcId, int PetNumber, double? X, double? Y, string[]? SourceRefs);
}
