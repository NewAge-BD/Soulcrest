using System.Text.RegularExpressions;
using Soulcrest.Core.Text;
using Soulcrest.Core.Pets;

namespace Soulcrest.Core.PetWindow;

/// <summary>
/// Pet name from the info panel of the pet window. Windows OCR reads the panel font's "i"/"I" as "j"/"J"
/// ("Red Spark Jgnus", "Dracunj Herbalist", "Predator Saraswatj", user data 2026-10-03), and long names
/// are cut with "..." ("Enhanced Krall Com..."). Without this, the catalog pet was learned a second time.
/// </summary>
public static partial class PanelPetName
{
    // "J" before a consonant at a word start, "j" at a word end or between consonants: no English name
    // has these, so they are misread "I"/"i".
    [GeneratedRegex(@"\bJ(?=[b-df-hj-np-tv-z])")]
    private static partial Regex CapitalJBeforeConsonant();

    [GeneratedRegex(@"(?<=[a-z])j\b")]
    private static partial Regex SmallJAtWordEnd();

    [GeneratedRegex(@"(?<=[b-df-hj-np-tv-z])j(?=[b-df-hj-np-tv-z])")]
    private static partial Regex SmallJBetweenConsonants();

    public static bool IsTruncated(string name) =>
        name.TrimEnd().EndsWith("...", StringComparison.Ordinal) || name.TrimEnd().EndsWith('…');

    /// <summary>Repairs the known misreads; keeps the rest as read.</summary>
    public static string Clean(string raw)
    {
        var name = raw.Trim();
        name = CapitalJBeforeConsonant().Replace(name, "I");
        name = SmallJAtWordEnd().Replace(name, "i");
        name = SmallJBetweenConsonants().Replace(name, "i");
        return name;
    }

    /// <summary>
    /// The catalog pet meant by a panel name: exact (after normalising), with "j" read as "i", as the only
    /// pet starting with a cut name, or as the only pet within one OCR error (two for long names).
    /// </summary>
    public static PetDefinition? Resolve(string raw, IEnumerable<PetDefinition> pets)
    {
        var candidates = pets
            .SelectMany(p => new[] { p.En, p.De }.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => (Pet: p, Name: Key(n!))))
            .Where(c => c.Name.Length > 0)
            .ToList();
        var truncated = IsTruncated(raw);
        var wanted = Key(Clean(raw.Trim().TrimEnd('.', '…')));
        if (wanted.Length < 3)
            return null;

        var exact = candidates.Where(c => c.Name == wanted).Select(c => c.Pet).Distinct().ToList();
        if (exact.Count == 1)
            return exact[0];
        if (truncated && wanted.Length >= 6)
        {
            var prefixed = candidates.Where(c => c.Name.StartsWith(wanted, StringComparison.Ordinal)).Select(c => c.Pet).Distinct().ToList();
            return prefixed.Count == 1 ? prefixed[0] : null;
        }
        if (wanted.Length < 6)
            return null;
        var allowed = wanted.Length >= 12 ? 2 : 1;
        var close = candidates
            .Select(c => (c.Pet, Distance: NameText.Levenshtein(wanted, c.Name)))
            .Where(c => c.Distance <= allowed)
            .GroupBy(c => c.Pet.Id)
            .Select(g => g.MinBy(c => c.Distance))
            .OrderBy(c => c.Distance)
            .ToList();
        if (close.Count == 0)
            return null;
        // Unique best (a second pet at the same distance is ambiguous: "Kerubar"/"Kerubiel").
        return close.Count == 1 || close[1].Distance > close[0].Distance ? close[0].Pet : null;
    }

    // Normalised, with "j" counted as "i" (the misread is that common).
    private static string Key(string name) => NameText.Normalize(name).Replace('j', 'i');
}
