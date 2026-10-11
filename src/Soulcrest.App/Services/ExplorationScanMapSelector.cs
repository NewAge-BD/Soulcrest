using Soulcrest.Core.Text;

namespace Soulcrest.App.Services;

/// <summary>Identifies one scannable map from passive OCR, then waits for two stable observations.</summary>
internal sealed class ExplorationScanMapSelector
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _mapTitles;
    private string? _pending;
    private int _observations;

    internal ExplorationScanMapSelector(ExplorationService exploration) : this(exploration.Places
        .Select(place => place.Map).Distinct(StringComparer.Ordinal)
        .ToDictionary(map => map, map => exploration.MapTitles(map), StringComparer.Ordinal)) { }

    // A detached title table also permits isolated alias/ambiguity tests without user profiles.
    internal ExplorationScanMapSelector(IReadOnlyDictionary<string, IReadOnlyList<string>> mapTitles) =>
        _mapTitles = mapTitles.Where(map => !string.IsNullOrWhiteSpace(map.Key))
            .ToDictionary(map => map.Key,
                map => (IReadOnlyList<string>)map.Value.Where(title => !string.IsNullOrWhiteSpace(title))
                    .Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);

    /// <summary>Reads only exact known titles in the existing map-title band; does not alter observation state.</summary>
    internal string? Read(ExplorationLayout layout, IReadOnlyList<OcrLine> original, IReadOnlyList<OcrLine> contrast)
    {
        var first = ReadPass(layout, original);
        var second = ReadPass(layout, contrast);
        if (first.Ambiguous || second.Ambiguous || first.Map is not null && second.Map is not null && first.Map != second.Map)
            return null;
        return first.Map ?? second.Map;
    }

    /// <summary>
    /// Returns a known map after two consecutive equal candidates. Unknown/conflicting readings reset
    /// the sequence; the caller must also call Reset whenever the captured list moved.
    /// </summary>
    internal string? Observe(string? candidate)
    {
        if (candidate is null || !_mapTitles.ContainsKey(candidate))
        {
            Reset();
            return null;
        }
        if (candidate == _pending) _observations = Math.Min(2, _observations + 1);
        else { _pending = candidate; _observations = 1; }
        return _observations == 2 ? candidate : null;
    }

    internal void Reset() { _pending = null; _observations = 0; }

    private (string? Map, bool Ambiguous) ReadPass(ExplorationLayout layout, IReadOnlyList<OcrLine> lines)
    {
        string? found = null;
        foreach (var line in lines)
        {
            var matches = _mapTitles.Where(map => layout.ShowsMap([line], map.Value))
                .Select(map => map.Key).ToArray();
            if (matches.Length == 0) continue;
            // Two header lines or one label shared by different maps cannot identify this frame.
            if (matches.Length != 1 || found is not null) return (null, true);
            found = matches[0];
        }
        return (found, false);
    }
}
