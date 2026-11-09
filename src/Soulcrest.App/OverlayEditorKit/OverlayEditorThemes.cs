using System.Drawing;
using System.Globalization;
using Soulcrest.App.Overlay;

namespace Soulcrest.App.OverlayEditorKit;

public sealed record OverlayEditorTheme(string Id, string Label, string CssVariables);

/// <summary>The original six native palettes, shared by canvas content and its native-painted frame.</summary>
public static class OverlayEditorThemes
{
    public const string DefaultId = OverlayThemeDefinition.DefaultId;
    public static IReadOnlyList<OverlayEditorTheme> All { get; } = Array.AsReadOnly(
        OverlayThemeDefinition.All.Select(theme => new OverlayEditorTheme(theme.Id, theme.Label, Variables(theme))).ToArray());

    public static string ResolveThemeId(string? id) => OverlayThemeDefinition.NormalizeId(id);
    public static OverlayEditorTheme Resolve(string? id) => All.First(theme => theme.Id == ResolveThemeId(id));
    public static string CssVariables(string? id) => Resolve(id).CssVariables;

    private static string Variables(OverlayThemeDefinition theme)
    {
        static string ColorCss(Color color) => $"rgba({color.R},{color.G},{color.B},{(color.A / 255d).ToString("0.######", CultureInfo.InvariantCulture)})";
        var body = theme.IsLegacy ? "\"Segoe UI\",sans-serif" : "\"Segoe UI Semibold\",\"Segoe UI\",sans-serif";
        return $"--overlay-surface-rgb:{theme.Surface.R},{theme.Surface.G},{theme.Surface.B};"
            + $"--overlay-surface-alpha:{(theme.Surface.A / 255d).ToString("0.######", CultureInfo.InvariantCulture)};"
            + $"--overlay-primary:{ColorCss(theme.Primary)};--overlay-secondary:{ColorCss(theme.Secondary)};"
            + $"--overlay-gold:{ColorCss(theme.Gold)};--overlay-mint:{ColorCss(theme.Mint)};--overlay-border:{ColorCss(theme.Border)};"
            + $"--overlay-heading-font:\"{theme.HeadingFamily}\";--overlay-heading-weight:{(theme.IsLegacy ? 400 : 700)};--overlay-body-font:{body};"
            + $"--text:{ColorCss(theme.Primary)};--muted:{ColorCss(theme.Secondary)};--gold:{ColorCss(theme.Gold)};"
            + $"--teal:{ColorCss(theme.Mint)};--line:{ColorCss(theme.Border)};";
    }
}
