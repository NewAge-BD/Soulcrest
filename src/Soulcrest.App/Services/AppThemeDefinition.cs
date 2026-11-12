using System.Drawing;

namespace Soulcrest.App.Services;

/// <summary>User request 2026-11-11: one palette for the main WebView and its native window chrome.</summary>
public sealed record AppThemeDefinition(
    string Id, string Label, string Description,
    Color Background, Color Panel, Color Panel2, Color Line,
    Color Text, Color Muted, Color Accent, Color AccentInk)
{
    public const string DefaultId = "soulcrest-classic";
    public const string DarkId = "graphite-dark";

    public static IReadOnlyList<AppThemeDefinition> All { get; } = Array.AsReadOnly(new[]
    {
        new AppThemeDefinition(DefaultId, "Soulcrest Classic", "Das bisherige Soulcrest-Design in Dunkelblau und Türkis.",
            Hex("#0e1117"), Hex("#161b24"), Hex("#1d2430"), Hex("#2a3342"),
            Hex("#e6ebf2"), Hex("#8b96a8"), Hex("#5eead4"), Hex("#062b27")),
        new AppThemeDefinition(DarkId, "Dark", "Neutrale Graphitflächen, helle Texte und dezente Konturen.",
            Hex("#212121"), Hex("#171717"), Hex("#2f2f2f"), Hex("#424242"),
            Hex("#ececec"), Hex("#b4b4b4"), Hex("#f4f4f4"), Hex("#171717"))
    });

    public static string NormalizeId(string? id) => id == DarkId ? DarkId : DefaultId;
    public static AppThemeDefinition ForId(string? id) => All[NormalizeId(id) == DarkId ? 1 : 0];

    /// <summary>Only catalogue-owned colour values are emitted, never persisted user input.</summary>
    public string CssVariables =>
        $"--bg:{Css(Background)};--panel:{Css(Panel)};--panel-2:{Css(Panel2)};--line:{Css(Line)};" +
        $"--text:{Css(Text)};--muted:{Css(Muted)};--accent:{Css(Accent)};--accent-ink:{Css(AccentInk)};";

    private static Color Hex(string value) => ColorTranslator.FromHtml(value);
    private static string Css(Color value) => $"#{value.R:x2}{value.G:x2}{value.B:x2}";
}
