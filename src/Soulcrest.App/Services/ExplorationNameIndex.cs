using System.Text.RegularExpressions;

namespace Soulcrest.App.Services;

/// <summary>A complete, sourced name; shortened OCR observations are never stored as references.</summary>
internal sealed record ExplorationNameReference(string PlaceId, string Map, string Kind, string Language,
    string Name, string NormalizedName);

/// <summary>Ambiguous identities remain candidates; contradictory names cannot grant completion.</summary>
internal sealed record ExplorationNameResolution(IReadOnlyList<ExplorationPlace> Candidates, bool Conflict = false);

/// <summary>
/// Immutable text references for one map/category candidate set. User request 2026-10-11:
/// match arbitrary visible truncations against complete DE/EN catalog names, without cut-name tables.
/// This class supplies identities only; map, row geometry and completion evidence stay with the scan.
/// </summary>
internal sealed class ExplorationNameIndex
{
    private readonly ExplorationPlace[] _places;
    private readonly Name[] _names;
    private readonly bool _kibelisks;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<ExplorationPlace>> _exact;
    internal IReadOnlyList<ExplorationNameReference> References { get; }

    private sealed record Name(ExplorationPlace Place, string Key);

    internal ExplorationNameIndex(IEnumerable<ExplorationPlace> places)
    {
        var source = places.ToArray();
        _places = source.DistinctBy(place => place.Id).ToArray();
        _kibelisks = _places.Length > 0 && _places.All(place => place.Kind == "kibelisk");
        var byId = _places.ToDictionary(place => place.Id, StringComparer.Ordinal);
        var references = source.SelectMany(place => new[]
            {
                Reference(place, "en", place.En),
                Reference(place, "de", place.De)
            }).OfType<ExplorationNameReference>()
            .DistinctBy(reference => (reference.PlaceId, reference.Language, reference.Name)).ToArray();
        References = Array.AsReadOnly(references);
        var names = references.Select(reference => new Name(byId[reference.PlaceId], reference.NormalizedName)).ToList();
        // Complete list-name aliases evidenced by the user's Altgard screenshots (2026-10-04).
        // They are scoped semantic aliases, not abbreviated OCR states or cross-map renamings.
        foreach (var place in _places.Where(place => place.Map == "altgard" && place.Kind == "dungeon"))
        {
            if (place.En == "Lost Ruin Entrance") names.Add(new(place, "lostruins"));
            if (place.En == "Fafnite Storage Entrance") names.Add(new(place, "fafnitestorageroom"));
        }
        _names = names.DistinctBy(name => (name.Place.Id, name.Key)).ToArray();
        _exact = _names.GroupBy(name => name.Key, StringComparer.Ordinal).ToDictionary(group => group.Key,
            group => Candidates(group.Select(name => name.Place)), StringComparer.Ordinal);
    }

    private static ExplorationNameReference? Reference(ExplorationPlace place, string language, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var key = ExplorationService.Normalize(name);
        return key.Length == 0 ? null : new(place.Id, place.Map, place.Kind, language, name, key);
    }

    internal IReadOnlyList<ExplorationPlace> Resolve(string text, bool truncated = false) =>
        ResolveObservation(text, truncated).Candidates;

    internal ExplorationPlace? Match(string text)
    {
        var result = ResolveObservation(text);
        return !result.Conflict && result.Candidates.Count == 1 ? result.Candidates[0] : null;
    }

    internal ExplorationNameResolution ResolveObservation(string text, bool truncated = false)
    {
        var ellipsis = Regex.Match(text, @"\.{2,}|…", RegexOptions.CultureInvariant);
        var initial = ellipsis.Success ? text[..ellipsis.Index] : text;
        var key = ExplorationService.Normalize(initial);
        var cut = truncated || ellipsis.Success;
        var result = cut ? new ExplorationNameResolution(Prefixes(key, allowEdit: !_kibelisks)) : CompleteOrImplicitPrefix(key);
        if (!ellipsis.Success) return result;
        var found = result.Candidates;

        // A distance or foreign map label after the visible ellipsis is not a name continuation.
        // A second complete scoped catalog name is contradictory evidence, even if another OCR
        // pass recognizes only one of the names. Keep that conflict separate from ambiguity.
        var suffix = ExplorationService.Normalize(text[(ellipsis.Index + ellipsis.Length)..]);
        var after = Exact(suffix);
        if (after.Any(place => !found.Any(candidate => candidate.Id == place.Id)))
            return new(Candidates(found.Concat(after)), Conflict: true);
        return result;
    }

    private ExplorationNameResolution CompleteOrImplicitPrefix(string key)
    {
        if (key.Length < (_kibelisks ? 4 : 5)) return new([]);
        var exact = Exact(key);
        if (exact.Count > 0) return new(exact);
        var fuzzy = FullNameFuzzy(key);
        if (fuzzy.Candidates.Count > 0)
            return _kibelisks && fuzzy.Conflict ? PreferCoveringExactPrefixes(key, fuzzy) : fuzzy;
        if (_kibelisks && key.Length >= 8)
        {
            var close = _names.Where(name => ExplorationService.Distance(name.Key, key) <= 1).ToArray();
            if (close.Length > 0)
            {
                // Genuine duplicate names remain several identities for the Kibelisk group rule.
                // Different near names must not become a new Kibelisk group: only a single
                // normalized reference name passed the existing one-edit whole-name rule.
                var result = new ExplorationNameResolution(Candidates(close.Select(name => name.Place)),
                    Conflict: close.Select(name => name.Key).Distinct().Count() > 1);
                return result.Conflict ? PreferCoveringExactPrefixes(key, result) : result;
            }
        }
        // Missing OCR dots provide no permission to repair the remaining prefix fuzzily.
        return new(Prefixes(key, allowEdit: false));
    }

    private ExplorationNameResolution PreferCoveringExactPrefixes(string key, ExplorationNameResolution ambiguous)
    {
        var prefixes = Prefixes(key, allowEdit: false);
        // The old Kibelisk fallback still allowed an exact prefix group after an inconclusive
        // fuzzy name. It is safe only when no fuzzy contender contradicts that prefix group.
        return prefixes.Count > 0 && ambiguous.Candidates.All(candidate => prefixes.Any(prefix => prefix.Id == candidate.Id))
            ? new(prefixes) : ambiguous;
    }

    private IReadOnlyList<ExplorationPlace> Exact(string key) =>
        _exact.GetValueOrDefault(key) ?? [];

    private IReadOnlyList<ExplorationPlace> Prefixes(string key, bool allowEdit)
    {
        if (key.Length < (_kibelisks ? 8 : 12)) return [];
        return Candidates(_names.Where(name => name.Key.StartsWith(key, StringComparison.Ordinal)
            || allowEdit && Enumerable.Range(Math.Max(1, key.Length - 1), 3)
                .Where(length => length <= name.Key.Length)
                .Any(length => ExplorationService.Distance(key, name.Key[..length]) <= 1))
            .Select(name => name.Place));
    }

    private ExplorationNameResolution FullNameFuzzy(string key)
    {
        var ranked = _names.GroupBy(name => name.Place.Id).Select(group =>
            (Place: group.First().Place, Score: group.Max(name =>
                1 - (double)ExplorationService.Distance(key, name.Key) / Math.Max(key.Length, name.Key.Length))))
            .OrderByDescending(match => match.Score).ToArray();
        if (ranked.Length == 0 || ranked[0].Score < .90) return new([]);
        if (ranked.Length == 1 || ranked[0].Score - ranked[1].Score >= .08)
            return new(Candidates([ranked[0].Place]));
        // Preserve every contender that prevents the old eight-percentage-point separation.
        var candidates = Candidates(ranked.TakeWhile(match => ranked[0].Score - match.Score < .08).Select(match => match.Place));
        var competingNames = _kibelisks && _names.Where(name => ranked[0].Score
            - (1 - (double)ExplorationService.Distance(key, name.Key) / Math.Max(key.Length, name.Key.Length)) < .08)
            .Select(name => name.Key).Distinct().Take(2).Count() > 1;
        return new(candidates, Conflict: competingNames);
    }

    private static IReadOnlyList<ExplorationPlace> Candidates(IEnumerable<ExplorationPlace> places) =>
        Array.AsReadOnly(places.DistinctBy(place => place.Id).ToArray());
}
