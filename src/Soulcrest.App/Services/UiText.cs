using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;

namespace Soulcrest.App.Services;

/// <summary>Application text only; game names and OCR source text are never translated here.</summary>
public static class UiText
{
    private static string _language = "en";
    private static readonly IReadOnlyDictionary<string, string> English = Load();
    private static readonly (Regex Pattern, string Translation)[] Templates = English
        .Where(pair => Regex.IsMatch(pair.Key, @"\{\d+\}"))
        .OrderByDescending(pair => pair.Key.Length)
        .Select(pair => (new Regex("^" + Regex.Replace(Regex.Escape(pair.Key), @"\\\{(\d+)\}",
            match => "(?<p" + match.Groups[1].Value + ">.*?)") + "$",
            RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
            TimeSpan.FromMilliseconds(100)), pair.Value)).ToArray();
    public static string Language
    {
        get => Volatile.Read(ref _language);
        set => Volatile.Write(ref _language, value == "de" ? "de" : "en");
    }
    public static string T(string? text)
    {
        if (text is null) return "";
        if (Language != "en") return text;
        if (English.TryGetValue(text, out var translated)) return translated;
        foreach (var (pattern, translation) in Templates)
        {
            var match = pattern.Match(text);
            if (match.Success)
                return Regex.Replace(translation, @"\{(\d+)\}", m => match.Groups["p" + m.Groups[1].Value].Value);
        }
        return text;
    }
    public static string F(string format, params object?[] arguments) =>
        string.Format(CultureInfo.GetCultureInfo(Language == "de" ? "de-DE" : "en-US"), T(format), arguments);
    private static IReadOnlyDictionary<string, string> Load()
    {
        using var stream = typeof(UiText).Assembly.GetManifestResourceStream("Soulcrest.App.Localization.en.json")
            ?? throw new InvalidOperationException("Missing UI translations.");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }
}
