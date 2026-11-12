using System.Security.Cryptography;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;

namespace Soulcrest.App.Services;

public sealed record DungeonPetMapPin(string Id, int PetNumber, string PetName, string NpcName,
    double X, double Y, bool Selected);
public sealed record DungeonPetMapScene(string Key, string Title, string? Variant, int MapId,
    PetGlobalAvailability Availability, string CheckedAt, string ImageUrl, int Width, int Height,
    IReadOnlyList<DungeonPetMapPin> Pins);
public sealed record DungeonPetMapSelection(string PetId, int PetNumber, string PetName, string? IconUrl,
    IReadOnlyList<DungeonPetMapScene> Scenes, string? UnavailableReason);
public enum DungeonPetMapDisplayState { NotOpened, Loading, Ready, Error, Closed }
public sealed record PetDungeonMapDiagnostics(int Maps, int PetSpecies, int Placements, int MissingImages,
    DungeonPetMapDisplayState DisplayState = DungeonPetMapDisplayState.NotOpened, DateTimeOffset? DisplayObservedAt = null);

/// <summary>Read-only offline dungeon scenes. Never changes the world map, tracking, targets or progress.</summary>
public sealed class PetDungeonMapService
{
    private readonly ProgressService _progress;
    private readonly IReadOnlyList<PetDungeonMap> _maps;
    private readonly Func<string, bool> _imageExists;
    private readonly object _displayGate = new();
    private DungeonPetMapDisplayState _displayState;
    private DateTimeOffset? _displayObservedAt;

    public PetDungeonMapService(ProgressService progress)
        : this(progress, PetDungeonMapCatalog.All, ImageValidator(progress.MapDataDirectory, PetDungeonMapCatalog.All)) { }

    internal PetDungeonMapService(ProgressService progress, IReadOnlyList<PetDungeonMap> maps,
        Func<string, bool> imageExists) => (_progress, _maps, _imageExists) = (progress, maps, imageExists);

    public DungeonPetMapSelection For(PetDefinition pet, string language)
    {
        var number = _progress.SoulId(pet.Id);
        var acquisition = PetAcquisitionCatalog.Find(number);
        var scenes = new List<DungeonPetMapScene>();
        if (number is { } petNumber && acquisition is not null)
        {
            var catalog = _progress.Catalog;
            foreach (var map in _maps.Where(map => map.Spawns.Any(spawn => spawn.PetNumber == petNumber)))
            {
                if (!_imageExists(map.Image)) continue;
                var statuses = acquisition.Sources.SelectMany(source => source.GlobalAvailability)
                    .Where(variant => variant.MapId == map.MapId).ToArray();
                if (statuses.Length == 0) continue;
                var availability = statuses.Select(status => status.Status).Distinct().ToArray();
                var pins = map.Spawns.Select(spawn =>
                {
                    var identity = PetIdTable.Find(spawn.PetNumber)!;
                    var definition = identity.CatalogId is { } id ? catalog.Find(id) : null;
                    var sourceNpc = PetAcquisitionCatalog.Find(spawn.PetNumber)!.Sources
                        .SelectMany(source => source.Npcs).First(npc => npc.Id == spawn.NpcId && npc.MapIds.Contains(map.MapId));
                    return new DungeonPetMapPin(spawn.Id, spawn.PetNumber, definition?.DisplayName(language) ?? identity.En,
                        sourceNpc.DisplayName(language), spawn.X, spawn.Y, spawn.PetNumber == petNumber);
                }).ToArray();
                scenes.Add(new(map.Id, map.DisplayName(language), map.VariantDisplayName(language), map.MapId,
                    availability.Length == 1 ? availability[0] : PetGlobalAvailability.Unconfirmed,
                    string.Join(" / ", statuses.Select(status => status.CheckedAt).Distinct(StringComparer.Ordinal)),
                    ImageUrl(map.Image), map.Width, map.Height, Array.AsReadOnly(pins)));
            }
        }
        // A released variant is preferred; a future map remains a clearly labelled planning view.
        var ordered = scenes.OrderBy(scene => scene.Availability).ThenBy(scene => scene.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(scene => scene.Variant, StringComparer.CurrentCultureIgnoreCase).ThenBy(scene => scene.Key, StringComparer.Ordinal).ToArray();
        var originalLabels = ordered.ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var scene = originalLabels[index];
            if (scene.Variant is null && originalLabels.Count(other => other.Title == scene.Title) > 1)
                ordered[index] = scene with { Variant = UiText.F("Variante {0}",
                    originalLabels.Take(index + 1).Count(other => other.Title == scene.Title)) };
            else if (scene.Variant is not null && originalLabels.Count(other => other.Title == scene.Title && other.Variant == scene.Variant) > 1)
                ordered[index] = scene with { Variant = scene.Variant + " · " + UiText.F("Variante {0}",
                    originalLabels.Take(index + 1).Count(other => other.Title == scene.Title && other.Variant == scene.Variant)) };
        }
        var missingImages = number is { } requested && _maps.Any(map => map.Spawns.Any(spawn => spawn.PetNumber == requested));
        return new(pet.Id, number ?? 0, pet.DisplayName(language), pet.Icon is { } icon ? ImageUrl(icon) : null,
            Array.AsReadOnly(ordered), ordered.Length > 0 ? null : UiText.T(missingImages
                ? "Die Dungeonkarten fehlen oder sind veraltet. Bitte das Kartenpaket aktualisieren."
                : "Für diese Dungeonquelle sind noch keine belegten Kartenpositionen verfügbar."));
    }

    public void ObserveDisplay(DungeonPetMapDisplayState state)
    {
        if (!Enum.IsDefined(state)) return;
        lock (_displayGate) { _displayState = state; _displayObservedAt = DateTimeOffset.UtcNow; }
    }

    public PetDungeonMapDiagnostics Diagnostics()
    {
        DungeonPetMapDisplayState state;
        DateTimeOffset? observed;
        lock (_displayGate) { state = _displayState; observed = _displayObservedAt; }
        return new(_maps.Count, _maps.SelectMany(map => map.Spawns).Select(spawn => spawn.PetNumber).Distinct().Count(),
            _maps.Sum(map => map.Spawns.Count), _maps.Select(map => map.Image).Distinct(StringComparer.Ordinal).Count(image => !_imageExists(image)),
            state, observed);
    }

    private static string ImageUrl(string relative) => "https://" + MainForm.MapDataHost + "/"
        + string.Join("/", relative.Split('/').Select(Uri.EscapeDataString));

    internal static Func<string, bool> ImageValidator(string? directory, IReadOnlyList<PetDungeonMap> maps)
    {
        var hashes = maps.GroupBy(map => map.Image, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(map => map.ImageSha256).Distinct(StringComparer.Ordinal).Single(), StringComparer.Ordinal);
        var cache = new Dictionary<string, (long Length, DateTime Modified, bool Valid)>(StringComparer.Ordinal);
        return relative =>
        {
            if (directory is null || !hashes.TryGetValue(relative, out var expected)
                || !LootTrackerRouteMapPreview.TileExists(directory, relative)) return false;
            try
            {
                var path = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
                var info = new FileInfo(path);
                lock (cache)
                {
                    if (cache.TryGetValue(relative, out var entry) && entry.Length == info.Length && entry.Modified == info.LastWriteTimeUtc)
                        return entry.Valid;
                    using var stream = File.OpenRead(path);
                    var valid = Convert.ToHexStringLower(SHA256.HashData(stream)) == expected;
                    cache[relative] = (info.Length, info.LastWriteTimeUtc, valid);
                    return valid;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
        };
    }
}
