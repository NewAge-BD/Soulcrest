using System.Text.RegularExpressions;
using Soulcrest.Core.Text;

namespace Soulcrest.App.Services;

/// <summary>One entry of the in-game Kibelisk list: "51. Steel Hammer Temporar..." over "Bind Complete".</summary>
public sealed record KibeliskRow(int Number, string Name, bool Truncated, bool Bound);

/// <summary>The rows of one picture; End: empty space below the last row, the list ends here.</summary>
public sealed record KibeliskPage(IReadOnlyList<KibeliskRow> Rows, bool End);

/// <summary>
/// The Kibelisk tab of the world map (user screenshots 2026-10-06, English client): numbered names, long
/// ones cut with "..", and below each a status line "Bind Complete" or "Binding Incomplete". German
/// statuses were confirmed by the user on 2026-10-08. The list holds only the
/// Kibelisks the character has discovered (second character 2026-10-06: 24 of 61, then empty space).
/// </summary>
public static partial class KibeliskList
{
    [GeneratedRegex(@"^\s*(\d{1,3})\s*[.,]\s*(.+?)\s*$")]
    private static partial Regex NumberedName();

    [GeneratedRegex(@"(\.{2,}|…)\s*$")]
    private static partial Regex Ellipsis();

    private static bool? Status(string text) => ExplorationService.Normalize(text) switch
    {
        "bindcomplete" or "bindungabgeschlossen" => true,
        "bindingincomplete" or "bindungnichtabgeschlossen" => false,
        _ => null,
    };

    /// <summary>
    /// Rows of one picture: a numbered name and the status line right below it. The end of the list
    /// shows as empty space of at least two rows below the last one, inside <paramref name="listBottom"/>.
    /// </summary>
    public static KibeliskPage Read(IReadOnlyList<OcrLine> lines, double listBottom)
    {
        var ordered = lines.OrderBy(l => l.Y).ToArray();
        var rows = new List<KibeliskRow>();
        var positions = new List<double>();
        for (var i = 0; i < ordered.Length - 1; i++)
        {
            if (NumberedName().Match(ordered[i].Text) is not { Success: true } match || !int.TryParse(match.Groups[1].Value, out var number))
                continue;
            var below = ordered[i + 1];
            if (below.Y - ordered[i].Y > ordered[i].Height * 3 || Status(below.Text) is not { } bound)
                continue;
            var name = match.Groups[2].Value;
            var truncated = Ellipsis().IsMatch(name);
            rows.Add(new KibeliskRow(number, Ellipsis().Replace(name, "").Trim(), truncated, bound));
            positions.Add(ordered[i].Y);
        }
        var end = false;
        if (positions.Count >= 2)
        {
            var spacing = (positions[^1] - positions[0]) / (positions.Count - 1);
            end = positions[^1] + spacing * 2.5 < listBottom
                && !ordered.Any(l => l.Y > positions[^1] + spacing * 0.6 && NumberedName().IsMatch(l.Text));
        }
        return new KibeliskPage(rows, end);
    }

    /// <summary>
    /// The Kibelisks a row can be: the exact name, else every place whose name starts with the cut name.
    /// Equally named places (Altgard "Steel Hammer Temporary Trading Post" #1/#2) all count.
    /// </summary>
    public static IReadOnlyList<ExplorationPlace> Candidates(KibeliskRow row, IReadOnlyList<ExplorationPlace> places)
    {
        var name = ExplorationService.Normalize(row.Name);
        if (name.Length < 4)
            return [];
        var exact = places.Where(p => ExplorationService.Normalize(p.En) == name || p.De is not null && ExplorationService.Normalize(p.De) == name).ToArray();
        if (exact.Length > 0)
            return exact;
        if (!row.Truncated && ExplorationService.Match(row.Name, places) is { } similar)
            return places.Where(p => ExplorationService.Normalize(p.En) == ExplorationService.Normalize(similar.En)
                || similar.De is not null && p.De is not null && ExplorationService.Normalize(p.De) == ExplorationService.Normalize(similar.De)).ToArray();
        // One letter misread or lost ("'dun's Lake" for "Idun's Lake", full Altgard list 2026-10-06), when
        // that fits exactly one name.
        if (!row.Truncated && name.Length >= 8)
        {
            var near = places.SelectMany(p => new[] { p.En, p.De }.Where(n => !string.IsNullOrWhiteSpace(n))
                    .Select(n => (Place: p, Name: ExplorationService.Normalize(n!))))
                .Where(p => ExplorationService.Distance(name, p.Name) <= 1).ToArray();
            if (near.Select(p => p.Name).Distinct().Count() == 1)
                return near.Select(p => p.Place).DistinctBy(p => p.Id).ToArray();
        }
        // Cut names, also when OCR dropped the dots; long enough to mean something.
        return name.Length < 8 ? [] : places.Where(p => ExplorationService.Normalize(p.En).StartsWith(name, StringComparison.Ordinal)
            || p.De is not null && ExplorationService.Normalize(p.De).StartsWith(name, StringComparison.Ordinal)).ToArray();
    }
}

/// <summary>
/// Rows collected while the user scrolls: a row counts once it was read the same way in two pictures in
/// a row. A row with one candidate sets it; a cut name shared by several Kibelisks ("Eastern Gribade
/// Highla..." = Tail Island and Cliff) is settled once as many such rows were seen as there are
/// candidates and all have the same status. The list is the truth: "Binding Incomplete" unchecks.
/// </summary>
public sealed class KibeliskSession(IReadOnlyList<ExplorationPlace> places)
{
    private Dictionary<int, KibeliskRow> _previous = [];
    private readonly Dictionary<int, KibeliskRow> _confirmed = [];
    private bool _endBefore;

    /// <summary>The end of the list was seen twice in a row and every number up to it was read.</summary>
    public bool Ended { get; private set; }

    public IReadOnlyDictionary<int, KibeliskRow> Confirmed => _confirmed;

    /// <summary>Adds a picture's rows; returns the rows confirmed by it.</summary>
    public IReadOnlyList<KibeliskRow> Observe(KibeliskPage page)
    {
        var rows = page.Rows;
        var confirmed = rows.Where(r => _previous.TryGetValue(r.Number, out var before) && before == r).ToList();
        foreach (var row in confirmed)
            _confirmed[row.Number] = row;
        _previous = rows.GroupBy(r => r.Number).ToDictionary(g => g.Key, g => g.First());
        if (rows.Count > 0)
        {
            // End: empty space below the last row twice in a row, or the last number of the map's
            // Kibelisks (full list, its last row sits at the bottom edge).
            var last = rows.Max(r => r.Number);
            if (page.End && _endBefore || last >= places.Select(p => p.Id).Distinct().Count())
                Ended |= Enumerable.Range(1, last).All(_confirmed.ContainsKey);
        }
        _endBefore = page.End;
        return confirmed;
    }

    /// <summary>Settled Kibelisks so far, rows without a candidate, and rows still waiting for their group.</summary>
    public (IReadOnlyList<string> Bound, IReadOnlyList<string> Unbound, IReadOnlyList<string> Unknown, IReadOnlyList<string> Open) Result()
    {
        var bound = new List<string>();
        var unbound = new List<string>();
        var unknown = new List<string>();
        var open = new List<string>();
        foreach (var group in _confirmed.Values.Select(r => (Row: r, Places: KibeliskList.Candidates(r, places)))
                     .GroupBy(e => string.Join("|", e.Places.Select(p => p.Id).Order())))
        {
            var candidates = group.First().Places;
            var rows = group.Select(e => e.Row).ToList();
            if (candidates.Count == 0)
            {
                unknown.AddRange(rows.Select(r => $"{r.Number}. {r.Name}"));
                continue;
            }
            if (rows.Count != candidates.Count || rows.Any(r => r.Bound != rows[0].Bound))
            {
                open.AddRange(rows.Select(r => $"{r.Number}. {r.Name}"));
                continue;
            }
            (rows[0].Bound ? bound : unbound).AddRange(candidates.Select(p => p.Id));
        }
        if (Ended)
        {
            // Not in the complete list: not discovered yet, so not bound either.
            var listed = _confirmed.Values.SelectMany(r => KibeliskList.Candidates(r, places)).Select(p => p.Id).ToHashSet();
            unbound.AddRange(places.Select(p => p.Id).Distinct().Where(id => !listed.Contains(id)));
        }
        return (bound, unbound, unknown, open);
    }

    /// <summary>The scan is done: the list end was read, or every Kibelisk of the map is settled.</summary>
    public bool Complete
    {
        get
        {
            if (Ended)
                return true;
            var (bound, unbound, _, _) = Result();
            return places.Select(p => p.Id).Distinct().All(id => bound.Contains(id) || unbound.Contains(id));
        }
    }
}
