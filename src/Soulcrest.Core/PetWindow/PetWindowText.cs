using System.Text.RegularExpressions;

namespace Soulcrest.Core.PetWindow;

/// <summary>Progress shown at the bottom of a pet card: "MAX" or "42/75".</summary>
public sealed record PetCardProgress(int Level, int SoulsInLevel, int Needed, bool IsMax);

/// <summary>
/// Parses texts of the in-game pet window (docs/PET_WINDOW.md). The card shows no name, only
/// the level badge and "MAX" or "souls/needed". The needed amount identifies the level being
/// worked on: /5 -> level 0 (not unlocked), /25 -> level 1, /75 -> level 2; MAX -> level 3.
/// </summary>
public static partial class PetWindowText
{
    private static readonly IReadOnlyDictionary<int, int> LevelByNeeded = new Dictionary<int, int> { [5] = 0, [25] = 1, [75] = 2 };

    // OCR variants of the cyan "MAX": "MAX", "MAX_", "MÄx", "MAx'".
    [GeneratedRegex(@"(?<![A-Za-z])M\s*[AÄÅ4]\s*[X×x](?![A-Za-z])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Max();

    // OCR variants: "42/75", "42 / 75", "42I75", "42l75", "4217S"
    [GeneratedRegex(@"(\d{1,2})\s*[/lI|\\]\s*(7\s*[5S]|2\s*[5S]|[5S])\b", RegexOptions.CultureInvariant)]
    private static partial Regex Fraction();

    [GeneratedRegex(@"Lv\.?\s*([0-9Il|])\b", RegexOptions.CultureInvariant)]
    private static partial Regex PanelLevel();

    [GeneratedRegex(@"(\d{1,3})\s*/\s*(\d{2,3})", RegexOptions.CultureInvariant)]
    private static partial Regex Collection();

    public static PetCardProgress? ParseCard(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        if (Max().IsMatch(text))
            return new PetCardProgress(3, 0, 0, true);
        var match = Fraction().Match(text);
        if (match.Success)
        {
            var souls = int.Parse(match.Groups[1].Value);
            var needed = int.Parse(match.Groups[2].Value.Replace(" ", "").Replace('S', '5'));
            if (LevelByNeeded.TryGetValue(needed, out var level) && souls < needed)
                return new PetCardProgress(level, souls, needed, false);
        }
        // OCR often reads the slash as "1": "42175" = 42/75, "17125" = 17/25.
        var slashAsOne = SlashReadAsOne().Match(text);
        if (slashAsOne.Success)
        {
            var souls = int.Parse(slashAsOne.Groups[1].Value);
            var needed = int.Parse(slashAsOne.Groups[2].Value);
            if (LevelByNeeded.TryGetValue(needed, out var level) && souls < needed)
                return new PetCardProgress(level, souls, needed, false);
        }
        // Locked pets: "415" = 4/5 (needs a 3-character token, "15" stays unparsed).
        var shortSlashAsOne = ShortSlashReadAsOne().Match(text);
        if (shortSlashAsOne.Success)
            return new PetCardProgress(0, int.Parse(shortSlashAsOne.Groups[1].Value), 5, false);
        return null;
    }

    [GeneratedRegex(@"(?<!\d)(\d{1,2})1(75|25)(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex SlashReadAsOne();

    [GeneratedRegex(@"(?<!\d)(\d{1,2})\s*/\s*([27])\S?", RegexOptions.CultureInvariant)]
    private static partial Regex TruncatedNeeded();

    /// <summary>
    /// Like <see cref="ParseCard"/>, plus readings whose denominator is garbled after its first digit
    /// ("6/2Ä" -> 6/25, "5/7?" -> 5/75): only 5, 25 and 75 exist. Use only together with a second check
    /// (the progress bar), never alone.
    /// </summary>
    public static PetCardProgress? ParseCardLenient(string? text)
    {
        if (ParseCard(text) is { } exact)
            return exact;
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var match = TruncatedNeeded().Match(text);
        if (!match.Success)
            return null;
        var souls = int.Parse(match.Groups[1].Value);
        var needed = match.Groups[2].Value == "2" ? 25 : 75;
        return souls < needed ? new PetCardProgress(needed == 25 ? 1 : 2, souls, needed, false) : null;
    }

    [GeneratedRegex(@"(?<!\d)([0-4])1[5S](?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex ShortSlashReadAsOne();

    /// <summary>
    /// Souls from the progress bar of a locked pet (x/5): the bar fill is exact enough for fifths.
    /// Returns null for other levels, where the bar is too coarse (±1–2 souls).
    /// </summary>
    public static int? SoulsFromBar(double fillFraction, int needed) => needed switch
    {
        5 => Math.Clamp((int)Math.Round(fillFraction * 5), 0, 4),
        // 25ths: fixtures measured within ±0.01 (half a soul); only used when the level badge shows "1".
        25 => Math.Clamp((int)Math.Round(fillFraction * 25), 0, 24),
        _ => null,
    };

    /// <summary>
    /// Whether an OCR text shows "souls/needed" in one of its known misread forms ("14/25", "14125",
    /// "14 25", "14I25"): a bar value is only taken over when the text backs it up.
    /// </summary>
    public static bool TextShowsSouls(string? text, int souls, int needed)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        foreach (var separator in new[] { "/", "1", " ", "I", "l", "|" })
        {
            var token = $"{souls}{separator}{needed}";
            var index = text.IndexOf(token, StringComparison.Ordinal);
            // Not part of a longer number ("114/25" does not show 14).
            if (index >= 0 && (index == 0 || !char.IsDigit(text[index - 1])))
                return true;
        }
        return false;
    }

    /// <summary>"Lv. 3 (MAX)" / "Lv. 2" next to "Pet Insight" in the right panel.</summary>
    public static int? ParsePanelLevel(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var match = PanelLevel().Match(text);
        if (!match.Success)
            return null;
        var digit = match.Groups[1].Value;
        return digit is "I" or "l" or "|" ? 1 : int.Parse(digit);
    }

    [GeneratedRegex(@"\(\s*(\d{1,2})\s*[/lI|]\s*(\d{1,2})\s*\)", RegexOptions.CultureInvariant)]
    private static partial Regex PanelFraction();

    /// <summary>Exact progress on the "Pet Insight" row: "Lv. 1 (5/25)" -> level 1, 5/25; "(MAX)" -> max.</summary>
    public static PetCardProgress? ParsePanelProgress(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        if (text.Contains("(MAX)", StringComparison.OrdinalIgnoreCase) || Max().IsMatch(text))
            return new PetCardProgress(3, 0, 0, true);
        var match = PanelFraction().Match(text);
        if (!match.Success)
            return null;
        var souls = int.Parse(match.Groups[1].Value);
        var needed = int.Parse(match.Groups[2].Value);
        return LevelByNeeded.TryGetValue(needed, out var level) && souls < needed ? new PetCardProgress(level, souls, needed, false) : null;
    }

    /// <summary>"Collection Status 94/200" -> (94, 200).</summary>
    public static (int Owned, int Total)? ParseCollection(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.Contains("Collection", StringComparison.OrdinalIgnoreCase))
            return null;
        var match = Collection().Match(text);
        return match.Success ? (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value)) : null;
    }
}
