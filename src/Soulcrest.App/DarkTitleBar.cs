using Soulcrest.App.Overlay;

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

    public static void Apply(nint window)
    {
        var on = 1;
        if (NativeMethods.DwmSetWindowAttribute(window, ImmersiveDarkMode, ref on, sizeof(int)) != 0)
            _ = NativeMethods.DwmSetWindowAttribute(window, ImmersiveDarkModeBefore20H1, ref on, sizeof(int));
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            return;
        Set(window, CaptionColor, 0x16, 0x1b, 0x24); // --panel #161b24
        Set(window, TextColor, 0xe6, 0xeb, 0xf2);    // --text #e6ebf2
        Set(window, BorderColor, 0x2a, 0x33, 0x42);  // --line #2a3342
    }

    private static void Set(nint window, int attribute, int r, int g, int b)
    {
        var colorRef = r | (g << 8) | (b << 16); // COLORREF 0x00BBGGRR
        _ = NativeMethods.DwmSetWindowAttribute(window, attribute, ref colorRef, sizeof(int));
    }
}
