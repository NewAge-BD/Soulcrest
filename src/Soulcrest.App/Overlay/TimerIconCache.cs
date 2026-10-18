using System.Drawing.Text;
using System.Runtime.InteropServices;
using Soulcrest.App.Services;

namespace Soulcrest.App.Overlay;

/// <summary>Owned, detached local images; missing files may recover on a later refresh.</summary>
internal sealed class TimerIconCache(string? mapDataDirectory = null) : IDisposable
{
    private readonly Dictionary<string, Image> _images = new(StringComparer.OrdinalIgnoreCase);
    internal Image? Load(string iconId)
    {
        var path = TimerOverlayIcons.LocalPath(iconId, mapDataDirectory);
        var image = path is null ? null : Read(path);
        if (image is not null) return image;
        var fallback = iconId.StartsWith("map:", StringComparison.Ordinal) ? "boss" : "custom";
        var replacement = TimerOverlayIcons.LocalPath(fallback, null);
        return replacement is null || replacement == path ? null : Read(replacement);
    }

    private Image? Read(string path)
    {
        if (_images.TryGetValue(path, out var cached)) return cached;
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            using var source = Image.FromStream(stream);
            return _images[path] = new Bitmap(source);
        }
        catch (Exception error) when (error is IOException or ArgumentException or ExternalException or OutOfMemoryException)
        { return null; }
    }

    public void Dispose()
    {
        foreach (var image in _images.Values) image.Dispose();
        _images.Clear();
    }
}

/// <summary>A private bundled font keeps native and browser text independent of installed fonts.</summary>
internal static class TimerStripTypeface
{
    private static readonly PrivateFontCollection Fonts = new();
    internal static readonly FontFamily Family = Load();
    internal const float Size = 18;
    internal const FontStyle Style = FontStyle.Bold;
    private static FontFamily Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "wwwroot", "fonts", "timer", "NotoSans-Bold.ttf");
        if (File.Exists(path))
        {
            Fonts.AddFontFile(path);
            if (Fonts.Families.FirstOrDefault() is { } family) return family;
        }
        return new FontFamily("Segoe UI");
    }
}
