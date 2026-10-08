using System.Text.RegularExpressions;
using Soulcrest.Core.PetWindow;

namespace Soulcrest.Ocr.PetWindow;

/// <summary>Independent numerator evidence; the circle supplies the denominator, never the numerator.</summary>
internal static partial class CardReadConsensus
{
    [GeneratedRegex(@"^\s*(\d{1,2})\s*[/lI|\\ ]\s*([\dS]+)[^\d]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Fraction();

    internal static PetCardProgress? ParseAtLevel(string text, int level)
    {
        var needed = level switch { 1 => 25, 2 => 75, _ => 0 };
        if (needed == 0) return null;
        var match = Fraction().Match(text);
        if (match.Success)
        {
            var denominator = match.Groups[2].Value.Replace('S', '5');
            // Observed cropped/duplicated denominator glyphs: 6/7, 7/715. Never reinterpret /25 as /75.
            var allowed = needed == 75 ? new[] { "75", "7", "715" } : new[] { "25", "2", "215" };
            var numerator = int.Parse(match.Groups[1].Value);
            return allowed.Contains(denominator) && numerator < needed
                ? new PetCardProgress(level, numerator, needed, false) : null;
        }
        // A slash recognised as 1 must still form a complete, bounded token (331/75 is not 31/75).
        var compact = text.Trim();
        return compact.All(char.IsDigit) && PetWindowText.ParseCard(compact) is { } exact && exact.Level == level ? exact : null;
    }

    internal static PetCardProgress? Resolve(IEnumerable<(string Text, int Variant)> attempts, int? level, double barFill)
    {
        if (level is not (1 or 2)) return null;
        var reads = attempts.Select(a => (Value: ParseAtLevel(a.Text, level.Value), a.Variant))
            .Where(a => a.Value is not null).ToList();
        var best = reads.GroupBy(a => a.Value!).OrderByDescending(g => g.Count()).FirstOrDefault();
        if (best is null || PetWindowScanner.DropsLeadingDigit(best.Key, barFill)
            || PetWindowScanner.ContradictsVisibleBar(best.Key, barFill)) return null;
        var alternatives = reads.Count - best.Count();
        var independent = best.Select(a => a.Variant).Distinct().Count();
        if (independent >= 2 && (best.Count() >= 3 && best.Count() >= 2 * alternatives
            || best.Count() >= 2 && alternatives == 0))
        {
            // /25 has a visible, precise bar. Preserve the protection against 3 instead of 5.
            if (best.Key.Needed == 25 && barFill >= .03 && Math.Abs(barFill * 25 - best.Key.SoulsInLevel) > 1)
                return null;
            return best.Key;
        }
        // Two scales of one mask are useful only when an independently measured bar is precise.
        return best.Count() >= 2 && alternatives == 0 && barFill >= .03
            && Math.Abs(barFill * best.Key.Needed - best.Key.SoulsInLevel) <= .5 ? best.Key : null;
    }
}
