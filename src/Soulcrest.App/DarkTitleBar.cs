using Soulcrest.App.Overlay;
using Soulcrest.App.Services;

namespace Soulcrest.App;

/// <summary>
/// The Windows title bar in Soulcrest's dark design instead of white (user request 2026-10-07): dark mode
/// from Windows 10 2004 on, and on Windows 11 the colours of the app's top bar (--panel, --text, --line in
/// app.css). Older systems ignore the attributes and keep their standard bar.
/// </summary>
internal static class DarkTitleBar
{
    private const int ImmersiveDarkMode = 20, ImmersiveDarkModeBefore20H1 = 19;
    private const int BorderColor = 34, CaptionColor = 35, TextColor = 36;

    public static bool Apply(nint window, AppThemeDefinition? theme = null)
    {
        theme ??= AppThemeDefinition.ForId(AppThemeDefinition.DefaultId);
        var on = 1;
        var darkApplied = NativeMethods.DwmSetWindowAttribute(window, ImmersiveDarkMode, ref on, sizeof(int)) == 0
            || NativeMethods.DwmSetWindowAttribute(window, ImmersiveDarkModeBefore20H1, ref on, sizeof(int)) == 0;
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            return darkApplied;
        var caption = Set(window, CaptionColor, theme.Panel.R, theme.Panel.G, theme.Panel.B);
        var text = Set(window, TextColor, theme.Text.R, theme.Text.G, theme.Text.B);
        var border = Set(window, BorderColor, theme.Line.R, theme.Line.G, theme.Line.B);
        return darkApplied && caption && text && border;
    }

    private static bool Set(nint window, int attribute, int r, int g, int b)
    {
        var colorRef = r | (g << 8) | (b << 16); // COLORREF 0x00BBGGRR
        return NativeMethods.DwmSetWindowAttribute(window, attribute, ref colorRef, sizeof(int)) == 0;
    }
}
