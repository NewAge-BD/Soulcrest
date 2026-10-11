using System.Text;
using System.Text.RegularExpressions;

namespace Soulcrest.Core.Text;

/// <summary>Known world-map category headings, shared by list validation and OCR language detection.</summary>
public static partial class ExplorationHeading
{
    public static (string Kind, string Language)? Read(string text)
    {
        var key = Key(text);
        var end = text.TrimEnd();
        var truncated = end.EndsWith("..", StringComparison.Ordinal) || end.EndsWith('…') || HasReportedOcrEnding(end);
        return key switch
        {
            "sealeddungeon" or "sealeddungeons" => ("dungeon", "en"),
            "versiegelterdungeon" or "versiegeltedungeons" => ("dungeon", "de"),
            // User report 2026-10-11: the German tab visibly reads "Versiegelter Dunge...".
            // Windows OCR of that exact text returned ".«" (de-DE) and ".„" (en-US/auto).
            // Require this exact prefix and one evidenced ending; arbitrary shorter headings stay unknown.
            "versiegelterdunge" when truncated => ("dungeon", "de"),
            "stronghold" or "strongholds" => ("stronghold", "en"),
            "garnison" or "garnisonen" => ("stronghold", "de"),
            _ => null,
        };
    }

    /// <summary>
    /// A language hint may include the observed circular icon and a category percentage.
    /// This does not validate a list, a map, or completion; those still need spatial evidence.
    /// </summary>
    public static string? ReadLanguageHint(string text)
    {
        var heading = text.Trim();
        // This exact OCR icon is evidenced by the exploration word/coordinate fixture.
        if (heading.StartsWith("(O)", StringComparison.Ordinal) && heading.Length > 3 && char.IsWhiteSpace(heading[3]))
            heading = heading[3..].TrimStart();
        if (heading.Contains('%'))
        {
            var percentage = HeadingPercentage().Match(heading);
            if (!percentage.Success) return null;
            heading = percentage.Groups["heading"].Value.TrimEnd();
            if (heading.Contains('%')) return null;
        }
        if (!HeadingText().IsMatch(heading) || HasReportedOcrEnding(heading) && Key(heading) != "versiegelterdunge") return null;
        return Read(heading)?.Language;
    }

    private static string Key(string text) => new(text.ToLowerInvariant().Replace("ß", "ss", StringComparison.Ordinal)
        .Normalize(NormalizationForm.FormD).Where(char.IsLetterOrDigit).ToArray());

    private static bool HasReportedOcrEnding(string text) =>
        text.EndsWith(".«", StringComparison.Ordinal) || text.EndsWith(".„", StringComparison.Ordinal);

    [GeneratedRegex(@"^[\p{L}\p{M}]+(?:[\s\p{Pd}]+[\p{L}\p{M}]+)*(?:\.{2,}|…|\.[«„])?$", RegexOptions.CultureInvariant)]
    private static partial Regex HeadingText();

    // Match the existing list's integer 0–100 token, including its narrowly scoped IOO OCR repair.
    // Require a separate trailing field; arbitrary prefixes, suffixes and extra categories stay unknown.
    [GeneratedRegex(@"^(?<heading>.+?)\s+(?:[1Il][0Oo]{2}|[0-9]{1,2})\s*%$", RegexOptions.CultureInvariant)]
    private static partial Regex HeadingPercentage();
}
