using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Soulcrest.App.Services;

/// <summary>
/// Versioned, portable route files. Character progress and running map targets remain local and
/// refer to the unchanged route ids and stop order; they are never part of an exported library.
/// </summary>
public static class RouteFile
{
    public const string Format = "soulcrest.routes";
    public const int Version = 1;
    public const string Coordinates = "map-pixels";
    private const string DefaultResource = "Soulcrest.LevelingRoutes.soulroute";
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static readonly Regex Levels = new(@" · Level ([1-9][0-9]*)–([1-9][0-9]*)\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static List<SavedRoute> Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    public static List<SavedRoute> Read(Stream stream)
    {
        try
        {
            var document = JsonSerializer.Deserialize<RouteDocument>(stream, Options);
            return FromDocument(document);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Die Soulcrest-Routendatei enthält ungültiges JSON.", error);
        }
    }

    public static void Save(string path, IReadOnlyList<SavedRoute> routes)
    {
        var document = ToDocument(routes);
        _ = FromDocument(document); // Validate before replacing an existing file.
        JsonFile.Save(path, document);
    }

    public static void Write(Stream stream, IReadOnlyList<SavedRoute> routes)
    {
        var document = ToDocument(routes);
        _ = FromDocument(document);
        JsonSerializer.Serialize(stream, document, Options);
    }

    /// <summary>
    /// A new file always wins, including when it cannot be read. Legacy JSON is deleted only after
    /// the atomic conversion has been read back and all domain values match.
    /// </summary>
    public static List<SavedRoute> LoadLibrary(string path, string legacyPath)
    {
        try
        {
            return LoadLibraryStrict(path, legacyPath);
        }
        catch (InvalidDataException error)
        {
            // Like JsonFile.Load: an unreadable library must never stop Soulcrest from starting. The file
            // is kept beside it for inspection, and the library starts again from the bundled routes.
            var broken = File.Exists(path) ? path : legacyPath;
            if (File.Exists(broken))
                File.Move(broken, broken + ".defekt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), overwrite: true);
            LogFile.Error("Routenbibliothek laden", error);
            return [];
        }
    }

    private static List<SavedRoute> LoadLibraryStrict(string path, string legacyPath)
    {
        if (File.Exists(path)) return Load(path);
        if (!File.Exists(legacyPath)) return [];

        List<SavedRoute> routes;
        try
        {
            using var stream = File.OpenRead(legacyPath);
            routes = JsonSerializer.Deserialize<List<SavedRoute>>(stream, Options)
                ?? throw new InvalidDataException("Die alte Routenbibliothek ist leer oder ungültig.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Die alte Routenbibliothek enthält ungültiges JSON.", error);
        }

        Save(path, routes);
        var confirmed = Load(path);
        if (!SameRoutes(routes, confirmed))
            throw new InvalidDataException("Die Routenmigration konnte nicht unverändert bestätigt werden.");
        File.Delete(legacyPath);
        return confirmed;
    }

    /// <summary>Seed the bundled leveling episodes only for a genuinely new route library.</summary>
    public static void EnsureDefaultLibrary(string path, string legacyPath)
    {
        var routes = LoadLibrary(path, legacyPath);
        using var stream = typeof(RouteFile).Assembly.GetManifestResourceStream(DefaultResource)
            ?? throw new InvalidDataException("Das mitgelieferte Soulcrest-Routenpaket fehlt.");
        var bundled = Read(stream);
        // Existing libraries get the bundled routes too (an existing routes.json used to skip them for
        // good), but each route only once: a bundled route the user deleted does not come back.
        var seededPath = SeededPath(path);
        var seeded = File.Exists(seededPath) ? JsonFile.Load<List<string>>(seededPath).ToHashSet(StringComparer.Ordinal) : [];
        var missing = bundled.Where(b => !seeded.Contains(b.Id) && routes.All(r => r.Id != b.Id)).ToList();
        if (missing.Count > 0 || !File.Exists(path))
            Save(path, [.. routes, .. missing]);
        if (!bundled.All(b => seeded.Contains(b.Id)))
            JsonFile.Save(seededPath, seeded.Union(bundled.Select(b => b.Id)).Order(StringComparer.Ordinal).ToList());
    }

    /// <summary>Ids of the bundled routes already added once (routes.soulroute → routes.seeded.json).</summary>
    internal static string SeededPath(string path) => Path.ChangeExtension(path, ".seeded.json");

    private static bool SameRoutes(IReadOnlyList<SavedRoute> before, IReadOnlyList<SavedRoute> after) =>
        before.Count == after.Count && before.Zip(after).All(pair =>
            pair.First.Id == pair.Second.Id && pair.First.Name == pair.Second.Name
            && pair.First.Category == pair.Second.Category && pair.First.Repeat == pair.Second.Repeat
            && pair.First.Color == pair.Second.Color && pair.First.Stops.SequenceEqual(pair.Second.Stops));

    private static RouteDocument ToDocument(IReadOnlyList<SavedRoute> routes)
    {
        if (routes is null) throw new InvalidDataException("Die Routenliste fehlt.");
        return new RouteDocument
        {
            Format = Format,
            Version = Version,
            Coordinates = Coordinates,
            Routes = routes.Select(route =>
            {
                if (route is null || string.IsNullOrWhiteSpace(route.Id) || string.IsNullOrWhiteSpace(route.Name) || route.Stops is null)
                    throw new InvalidDataException("Eine Route oder ihre Stationen fehlen.");
                return new RouteEntry
                {
                    Id = route.Id, Name = route.Name, Category = route.Category,
                    Repeat = route.Repeat, Color = route.Color, Episode = EpisodeOf(route),
                    Stops = route.Stops.Select((stop, index) =>
                    {
                        if (stop is null || string.IsNullOrWhiteSpace(stop.MapId) || stop.Name is null || string.IsNullOrWhiteSpace(stop.Kind))
                            throw new InvalidDataException("Eine Routenstation ist unvollständig.");
                        return new StopEntry
                        {
                            Id = route.Id + ":" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            Map = stop.MapId, X = stop.X, Y = stop.Y, Title = stop.Name,
                            Objective = ObjectiveOf(route, stop), Kind = stop.Kind, Icon = stop.Icon,
                            PetId = stop.PetId, Color = stop.Color
                        };
                    }).ToList()
                };
            }).ToList()
        };
    }

    private static string ObjectiveOf(SavedRoute route, RouteStop stop)
    {
        if (!route.IsLeveling) return "custom";
        return LevelingRouteStyle.ColorOf(stop) switch
        {
            LevelingRouteStyle.MainQuest => "main-quest",
            LevelingRouteStyle.RegionalQuest => "regional-quest",
            LevelingRouteStyle.Teleport => "teleport",
            _ => stop.Kind == "Waypoint" ? "waypoint" : "exploration"
        };
    }

    private static EpisodeEntry? EpisodeOf(SavedRoute route)
    {
        if (!LevelingRouteStyle.TryGetEpisode(route, out var faction, out var number)) return null;
        var match = Levels.Match(route.Name);
        return new EpisodeEntry
        {
            Faction = faction.ToLowerInvariant(), Number = number,
            MinLevel = match.Success && int.TryParse(match.Groups[1].Value, out var min) ? min : null,
            MaxLevel = match.Success && int.TryParse(match.Groups[2].Value, out var max) ? max : null
        };
    }

    private static List<SavedRoute> FromDocument(RouteDocument? document)
    {
        if (document is null || document.Format != Format)
            throw new InvalidDataException("Keine Soulcrest-Routendatei.");
        if (document.Version != Version)
            throw new InvalidDataException("Diese Version des Soulcrest-Routenformats wird nicht unterstützt.");
        if (document.Coordinates != Coordinates)
            throw new InvalidDataException("Das Koordinatenformat der Routendatei wird nicht unterstützt.");
        if (document.Routes is null)
            throw new InvalidDataException("Die Routenliste fehlt.");

        var routeIds = new HashSet<string>(StringComparer.Ordinal);
        var stopIds = new HashSet<string>(StringComparer.Ordinal);
        var routes = new List<SavedRoute>(document.Routes.Count);
        foreach (var route in document.Routes)
        {
            if (route is null || string.IsNullOrWhiteSpace(route.Id) || !routeIds.Add(route.Id)
                || string.IsNullOrWhiteSpace(route.Name) || route.Repeat is null || route.Stops is null)
                throw new InvalidDataException("Eine Route hat fehlende oder doppelte Kennungen, Namen oder Stationen.");
            if (route.Episode is { } episode && (episode.Faction is not ("asmodian" or "elyos")
                || episode.Number < 1 || episode.MinLevel is < 1 || episode.MaxLevel is < 1
                || (episode.MinLevel.HasValue != episode.MaxLevel.HasValue)
                || episode.MinLevel > episode.MaxLevel))
                throw new InvalidDataException("Ungültige Episodenmetadaten.");

            var stops = new List<RouteStop>(route.Stops.Count);
            var restoredRoute = new SavedRoute(route.Id, route.Name, stops, route.Repeat.Value, route.Color, route.Category);
            foreach (var stop in route.Stops)
            {
                if (stop is null || string.IsNullOrWhiteSpace(stop.Id) || !stopIds.Add(stop.Id)
                    || stop.Id != route.Id + ":" + stops.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    || string.IsNullOrWhiteSpace(stop.Map) || stop.Title is null || string.IsNullOrWhiteSpace(stop.Kind)
                    || stop.X is not { } x || stop.Y is not { } y || !double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0
                    || stop.Objective is not ("main-quest" or "regional-quest" or "teleport" or "exploration" or "waypoint" or "custom"))
                    throw new InvalidDataException("Eine Routenstation ist unvollständig, doppelt oder ungültig.");
                var restoredStop = new RouteStop(stop.Map, x, y, stop.Title, stop.Kind, stop.Icon, stop.PetId, stop.Color);
                if (stop.Objective != ObjectiveOf(restoredRoute, restoredStop))
                    throw new InvalidDataException("Die Objective-Metadaten widersprechen der Routenstation.");
                stops.Add(restoredStop);
            }
            if (route.Episode is { } suppliedEpisode)
            {
                var derivedEpisode = EpisodeOf(restoredRoute);
                if (derivedEpisode is null || suppliedEpisode.Faction != derivedEpisode.Faction
                    || suppliedEpisode.Number != derivedEpisode.Number || suppliedEpisode.MinLevel != derivedEpisode.MinLevel
                    || suppliedEpisode.MaxLevel != derivedEpisode.MaxLevel)
                    throw new InvalidDataException("Die Episodenmetadaten widersprechen dem Routennamen.");
            }
            routes.Add(restoredRoute);
        }
        return routes;
    }

    private sealed class RouteDocument
    {
        public RouteDocument() { }
        [JsonPropertyName("format")] public string? Format { get; set; }
        [JsonPropertyName("version")] public int Version { get; set; }
        [JsonPropertyName("coordinates")] public string? Coordinates { get; set; }
        [JsonPropertyName("routes")] public List<RouteEntry>? Routes { get; set; }
    }

    private sealed class RouteEntry
    {
        public RouteEntry() { }
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("category"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Category { get; set; }
        [JsonPropertyName("repeat")] public bool? Repeat { get; set; }
        [JsonPropertyName("color"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Color { get; set; }
        [JsonPropertyName("episode"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public EpisodeEntry? Episode { get; set; }
        [JsonPropertyName("stops")] public List<StopEntry>? Stops { get; set; }
    }

    private sealed class EpisodeEntry
    {
        public EpisodeEntry() { }
        [JsonPropertyName("faction")] public string? Faction { get; set; }
        [JsonPropertyName("number")] public int Number { get; set; }
        [JsonPropertyName("minLevel"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? MinLevel { get; set; }
        [JsonPropertyName("maxLevel"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? MaxLevel { get; set; }
    }

    private sealed class StopEntry
    {
        public StopEntry() { }
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("map")] public string? Map { get; set; }
        [JsonPropertyName("x")] public double? X { get; set; }
        [JsonPropertyName("y")] public double? Y { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("objective")] public string? Objective { get; set; }
        [JsonPropertyName("kind")] public string? Kind { get; set; }
        [JsonPropertyName("icon"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Icon { get; set; }
        [JsonPropertyName("petId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? PetId { get; set; }
        [JsonPropertyName("color"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Color { get; set; }
    }
}


