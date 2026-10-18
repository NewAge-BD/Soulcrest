using System.Globalization;

namespace Soulcrest.App.Services;

/// <summary>Pure presentation helpers for the embedded patchnotes; parsing and release contents stay unchanged.</summary>
internal static class PatchNotesReader
{
    internal sealed record Group(string Style, string Label, IReadOnlyList<PatchNotes.Entry> Entries);

    internal static IReadOnlyList<PatchNotes.Release> Shown(IEnumerable<PatchNotes.Release> releases) =>
        Array.AsReadOnly(releases.Where(release => Version.TryParse(release.Version, out var version)
            && version >= PatchNotes.FirstShown).ToArray());

    internal static IReadOnlyList<PatchNotes.Release> Filter(IReadOnlyList<PatchNotes.Release> releases, string? query)
    {
        var tokens = (query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return releases;
        return Array.AsReadOnly(releases.Where(release =>
        {
            var fields = new[] { release.Version, release.Date }.Concat(release.Entries.SelectMany(entry =>
                new[] { entry.Kind, string.Concat(PatchNotes.Spans(entry.Text).Select(span => span.Text)) }));
            var text = string.Join(" ", fields);
            return tokens.All(token => text.Contains(token, StringComparison.OrdinalIgnoreCase));
        }).ToArray());
    }

    internal static PatchNotes.Release? Select(IReadOnlyList<PatchNotes.Release> releases, string? version) =>
        releases.FirstOrDefault(release => string.Equals(release.Version, version, StringComparison.Ordinal))
        ?? releases.FirstOrDefault();

    internal static IReadOnlyList<Group> Groups(IReadOnlyList<PatchNotes.Entry> entries) =>
        Array.AsReadOnly(entries.Select(entry => (Entry: entry, Kind: Kind(entry.Kind)))
            .GroupBy(item => item.Kind.Key)
            .OrderBy(group => group.First().Kind.Order)
            .Select(group => new Group(group.First().Kind.Style, group.First().Kind.Label,
                Array.AsReadOnly(group.Select(item => item.Entry).ToArray()))).ToArray());

    private static (string Key, string Style, string Label, int Order) Kind(string kind)
    {
        var normalized = kind.Trim();
        if (normalized.Equals("Neu", StringComparison.OrdinalIgnoreCase) || normalized.Equals("New", StringComparison.OrdinalIgnoreCase))
            return ("known:new", "new", "Neu", 0);
        if (normalized.Equals("Verbessert", StringComparison.OrdinalIgnoreCase) || normalized.Equals("Improved", StringComparison.OrdinalIgnoreCase))
            return ("known:improved", "improved", "Verbessert", 1);
        if (normalized.Equals("Behoben", StringComparison.OrdinalIgnoreCase) || normalized.Equals("Fixed", StringComparison.OrdinalIgnoreCase))
            return ("known:fixed", "fixed", "Behoben", 2);
        if (normalized.Equals("Geändert", StringComparison.OrdinalIgnoreCase) || normalized.Equals("Changed", StringComparison.OrdinalIgnoreCase))
            return ("known:changed", "changed", "Geändert", 3);
        if (normalized.Equals("Entfernt", StringComparison.OrdinalIgnoreCase) || normalized.Equals("Removed", StringComparison.OrdinalIgnoreCase))
            return ("known:removed", "removed", "Entfernt", 4);
        return normalized.Length == 0 ? ("empty", "other", "Weitere Änderungen", 5)
            : ("other:" + kind, "other", kind, 5);
    }

    internal static string FormatDate(string date, string language, bool compact = false)
    {
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return date;
        var german = language == "de";
        var format = german ? compact ? "dd.MM.yyyy" : "d. MMMM yyyy"
            : compact ? "MMM d, yyyy" : "MMMM d, yyyy";
        return parsed.ToString(format, CultureInfo.GetCultureInfo(german ? "de-DE" : "en-US"));
    }
}
