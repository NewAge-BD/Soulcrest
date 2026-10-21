namespace Soulcrest.App.Services;

/// <summary>An interactive map center in native map pixels, with Leaflet's zoom value.</summary>
public sealed record MapViewport(double X, double Y, double Zoom)
{
    public bool IsValid() => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Zoom);

    internal static Dictionary<string, MapViewport> Normalize(Dictionary<string, MapViewport>? viewports)
    {
        var result = new Dictionary<string, MapViewport>(StringComparer.Ordinal);
        if (viewports is null) return result;
        foreach (var (mapId, viewport) in viewports)
            if (!string.IsNullOrWhiteSpace(mapId) && viewport is not null && viewport.IsValid())
                result.Add(mapId, viewport);
        return result;
    }
}
