using System.Drawing;

namespace Soulcrest.App.OverlayEditorKit;

/// <summary>Only the mandatory control's portable position; unlocked interaction remains transient.</summary>
public sealed record OverlayLockWidgetPosition
{
    public double? X { get; init; }
    public double? Y { get; init; }
}

/// <summary>One fixed-size control per window, outside the optional content-widget collection.</summary>
public static class OverlayLockWidget
{
    public const string Id = "overlay-lock";
    public const string Kind = "overlay-lock";
    public const int Size = 28;
    public const int Inset = 8;

    public static OverlayWidgetDefinition Definition { get; } = new(Kind, "Overlay-Sperre",
        "Pflicht-Widget zum Entsperren und Sperren des Overlayfensters", "lock", Size, Size);

    public static OverlayLockWidgetPosition Position(OverlaySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Normalize(settings.LockWidget, settings.Width, settings.Height);
    }

    public static RectangleF Bounds(OverlaySettings settings)
    {
        var position = Position(settings);
        return new((float)position.X!.Value, (float)position.Y!.Value, (float)Size, (float)Size);
    }

    public static OverlayLockWidgetPosition Normalize(OverlayLockWidgetPosition? position, double width, double height)
    {
        width = Dimension(width, 360, 160, 1600);
        height = Dimension(height, 260, 64, 1200);
        var maximumX = Math.Max(0, width - Size);
        var maximumY = Math.Max(0, height - Size);
        return new()
        {
            X = Coordinate(position?.X, Math.Max(0, maximumX - Inset), maximumX),
            Y = Coordinate(position?.Y, Math.Min(Inset, maximumY), maximumY),
        };
    }

    public static OverlaySettings WithPosition(OverlaySettings settings, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings with { LockWidget = Normalize(new() { X = x, Y = y }, settings.Width, settings.Height) };
    }

    public static OverlayWidget Layout(OverlaySettings settings)
    {
        var position = Position(settings);
        return new()
        {
            Id = Id, Kind = Kind, X = position.X!.Value, Y = position.Y!.Value,
            Width = Size, Height = Size, ContentWidth = Size, ContentHeight = Size,
            ShowLabel = false, ShowIcon = true,
        };
    }

    internal static bool Equivalent(OverlayLockWidgetPosition? left, OverlayLockWidgetPosition? right,
        double width, double height) => Normalize(left, width, height) == Normalize(right, width, height);

    private static double Dimension(double value, double fallback, double minimum, double maximum) =>
        Math.Clamp(double.IsFinite(value) ? value : fallback, minimum, maximum);
    private static double Coordinate(double? value, double fallback, double maximum) =>
        Math.Clamp(value is { } number && double.IsFinite(number) ? number : fallback, 0, maximum);
}
