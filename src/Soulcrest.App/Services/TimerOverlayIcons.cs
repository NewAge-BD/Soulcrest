namespace Soulcrest.App.Services;

/// <summary>Timer artwork from shipped assets or the existing offline map package; never a remote URL.</summary>
public static class TimerOverlayIcons
{
    private const string MapPrefix = "map:";

    public static string ForBoss(string? iconPath) => RelativePath(iconPath) is { } relative ? MapPrefix + relative : "boss";

    public static string BundledFile(string? iconId) => iconId switch
    {
        "shugo" or "event:catalog:shugo" => "shugo-festival.png",
        "rift" or "event:catalog:rift" => "spacetime-rift.png",
        "invasion" or "event:catalog:invasion" => "dimensional-invasion.png",
        "abyss" or "event:catalog:abyss" => "artifact-siege.png",
        "daily-reset" or "event:catalog:daily-reset" => "daily-reset.png",
        "weekly-reset" or "event:catalog:weekly-reset" => "weekly-reset.png",
        "boss" => "boss-timer.png",
        _ => iconId?.StartsWith(MapPrefix, StringComparison.Ordinal) == true
            || iconId?.StartsWith("boss:", StringComparison.Ordinal) == true ? "boss-timer.png" : "custom-timer.png"
    };

    public static string BrowserSource(string? iconId) => MapPath(iconId) is { } relative
        ? "https://" + MainForm.MapDataHost + "/" + string.Join("/", relative.Split('/').Select(Uri.EscapeDataString))
        : "icons/events/" + BundledFile(iconId);

    /// <summary>Resolves paths only. The caller loads/caches images and handles unavailable files.</summary>
    public static string? LocalPath(string? iconId, string? mapDataDirectory)
    {
        if (MapPath(iconId) is not { } relative)
            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "wwwroot", "icons", "events", BundledFile(iconId)));
        if (string.IsNullOrWhiteSpace(mapDataDirectory)) return null;
        try
        {
            var root = Path.GetFullPath(mapDataDirectory);
            var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { return null; }
    }

    private static string? MapPath(string? iconId) => iconId?.StartsWith(MapPrefix, StringComparison.Ordinal) == true
        ? RelativePath(iconId[MapPrefix.Length..]) : null;

    private static string? RelativePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Replace('\\', '/');
        if (normalized.StartsWith('/') || Path.IsPathRooted(normalized) || normalized.Contains(':') || normalized.Contains('%')) return null;
        var invalid = Path.GetInvalidFileNameChars();
        foreach (var segment in normalized.Split('/'))
            if (string.IsNullOrWhiteSpace(segment) || segment is "." or ".." || segment.EndsWith(' ') || segment.EndsWith('.')
                || segment.IndexOfAny(invalid) >= 0 || segment.Any(char.IsControl)) return null;
        return normalized;
    }
}
