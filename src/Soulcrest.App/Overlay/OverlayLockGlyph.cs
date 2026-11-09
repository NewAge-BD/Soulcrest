using System.Collections.Concurrent;
using System.Drawing.Imaging;

namespace Soulcrest.App.Overlay;

/// <summary>The native lock painter for editor previews; cached only for the fixed logical control size.</summary>
public static class OverlayLockGlyph
{
    private static readonly ConcurrentDictionary<(string Theme, bool Locked), string> Cache = new();
    public static string GetDataUri(string? themeId, bool locked = true, int size = 28)
    {
        if (!OverlayLockButton.IsRenderableGlyphSize(new(size, size))) throw new ArgumentOutOfRangeException(nameof(size));
        var theme = OverlayThemeDefinition.Resolve(themeId);
        return size == 28 ? Cache.GetOrAdd((theme.Id, locked), key => Paint(OverlayThemeDefinition.Resolve(key.Theme), key.Locked, 28))
            : Paint(theme, locked, size);
    }

    private static string Paint(OverlayThemeDefinition theme, bool locked, int size)
    {
        using var image = OverlayLockButton.RenderGlyph(new(size, size), theme, locked);
        using var stream = new MemoryStream();
        image.Save(stream, ImageFormat.Png);
        return "data:image/png;base64," + Convert.ToBase64String(stream.GetBuffer(), 0, (int)stream.Length);
    }
}
