using System.Reflection;

namespace Soulcrest.App.Services;

public static class AppPaths
{
    /// <summary>%LOCALAPPDATA%\Soulcrest; tests redirect it with SOULCREST_DATA so real user data stays untouched.</summary>
    public static string DataDirectory { get; } =
        Environment.GetEnvironmentVariable("SOULCREST_DATA") is { Length: > 0 } overridden
            ? overridden
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Soulcrest");

    public static string WebViewDirectory => Path.Combine(DataDirectory, "webview2");
    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public static string ProgressFile => Path.Combine(DataDirectory, "progress.json");
    public static string EventsFile => Path.Combine(DataDirectory, "events.jsonl");
    public static string PinsFile => Path.Combine(DataDirectory, "pins.json");
    public static string TargetsFile => Path.Combine(DataDirectory, "targets.json");
    public static string RoutesFile => Path.Combine(DataDirectory, "routes.json");
    public static string LogsDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>Pets and portraits learned from the in-game pet window (pets not in the map data).</summary>
    /// <summary>Cached SIFT references of the maps for player tracking (rebuilt when the map data changes).</summary>
    public static string MapReferenceDirectory => Path.Combine(DataDirectory, "cache", "map-references");
    public static string LearnedDirectory => Path.Combine(DataDirectory, "learned");
    public static string LearnedPetsFile => Path.Combine(LearnedDirectory, "learned-pets.json");
    public static string LearnedPortraitsDirectory => Path.Combine(LearnedDirectory, "portraits");
    public static string LearnedNamesFile => Path.Combine(LearnedDirectory, "learned-names.json");
    /// <summary>Learned portraits that turned out to show another pet (kept for inspection, not used).</summary>
    public static string RejectedPortraitsDirectory => Path.Combine(LearnedDirectory, "rejected");

    /// <summary>Repository root, set by the desktop tester launcher (SOULCREST_SOURCE).</summary>
    public static string? SourceRoot
    {
        get
        {
            var value = Environment.GetEnvironmentVariable("SOULCREST_SOURCE");
            return !string.IsNullOrWhiteSpace(value) && Directory.Exists(value) ? value : null;
        }
    }

    /// <summary>"0.1.0+&lt;revision&gt;" from the build (the tester launcher passes the commit as SourceRevisionId).</summary>
    public static string Version { get; } =
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";

    /// <summary>
    /// Map data package (scripts/build_tester_data.py). Order: SOULCREST_MAPDATA (tester launcher),
    /// "mapdata" beside the exe, imports/generated/mapdata above the exe (development build).
    /// </summary>
    public static string? FindMapData()
    {
        var candidates = new List<string>();
        var fromEnvironment = Environment.GetEnvironmentVariable("SOULCREST_MAPDATA");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
            candidates.Add(fromEnvironment);
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "mapdata"));
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            candidates.Add(Path.Combine(dir.FullName, "imports", "generated", "mapdata"));
        return candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, "manifest.json")));
    }
}
