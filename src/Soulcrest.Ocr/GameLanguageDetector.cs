using System.Text.RegularExpressions;

namespace Soulcrest.Ocr;

/// <summary>Conservative DE/EN hints from ordinary words, not a list of assumed game translations.</summary>
public static partial class GameLanguageDetector
{
    private static readonly HashSet<string> English = new(StringComparer.OrdinalIgnoreCase)
        { "the", "and", "with", "your", "soul", "souls", "bound", "obtained", "acquired", "level", "collection", "locked", "summon", "insight" };
    private static readonly HashSet<string> German = new(StringComparer.OrdinalIgnoreCase)
        { "der", "die", "das", "und", "mit", "deine", "seele", "seelen", "gebunden", "erhalten", "stufe", "sammlung", "gesperrt", "beschwören", "einsicht",
            "bindung", "abgeschlossen", "nicht", "kenntnis", "petkenntnis", "sammlungsfortschritt" };

    [GeneratedRegex(@"\p{L}+", RegexOptions.CultureInvariant)]
    private static partial Regex Words();

    public static string? Detect(IEnumerable<string> lines)
    {
        var texts = lines.ToArray();
        var words = texts.SelectMany(line => Words().Matches(line).Select(m => m.Value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var en = words.Count(English.Contains);
        var de = words.Count(German.Contains);
        var captions = texts.Select(text => new string(text.Where(char.IsLetter).ToArray()).ToLowerInvariant()).ToArray();
        // These complete game captions are stronger evidence than one ordinary word (user-confirmed DE).
        if (captions.Any(t => t.Contains("petkenntnis", StringComparison.Ordinal) || t.Contains("sammlungsfortschritt", StringComparison.Ordinal))) de = Math.Max(de, 2);
        if (captions.Any(t => t.Contains("petinsight", StringComparison.Ordinal) || t.Contains("collectionstatus", StringComparison.Ordinal))) en = Math.Max(en, 2);
        if (en >= 2 && en >= de + 2) return "en";
        if (de >= 2 && de >= en + 2) return "de";
        return null; // names, digits, MAX, empty frames or mixed/uncertain text do not prove a language
    }
}
