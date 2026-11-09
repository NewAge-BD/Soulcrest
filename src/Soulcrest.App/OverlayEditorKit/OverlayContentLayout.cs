namespace Soulcrest.App.OverlayEditorKit;

// Adapted from overlay-editor-kit 1.15.0: the general metric branch of OverlayContentLayout.cs.
public sealed record OverlayContentLayout(OverlayWidget LayoutWidget, double Scale,
    double ReferenceWidth, double ReferenceHeight, double MinimumWidth, double MinimumHeight)
{
    public static OverlayContentLayout Create(OverlayWidget widget, OverlaySnapshot snapshot)
    {
        var width = Valid(widget.Width, 168);
        var height = Valid(widget.Height, 72);
        if (Reflows(widget))
        {
            var fit = Math.Min(1, Math.Min(width / 80, height / 48));
            return new(widget with { X = 0, Y = 0, Width = width / fit, Height = height / fit },
                fit, width, height, 80, 48);
        }
        var referenceWidth = Valid(widget.ContentWidth ?? width, width);
        var referenceHeight = Valid(widget.ContentHeight ?? height, height);
        var fontScale = double.IsFinite(widget.FontScale) ? Math.Clamp(widget.FontScale, .7, 2) : 1;
        var metric = snapshot.Metrics.GetValueOrDefault(widget.Kind);
        var minimumHeight = widget.Kind switch
        {
            "genus-insight" => 20 + (widget.ShowLabel ? 18 * fontScale : 0) + 343 * fontScale,
            "abyss-killfeed" => 16 + (widget.ShowLabel ? 18 * fontScale : 0) +
                Math.Max(1, Math.Min(widget.KillFeedRows, Soulcrest.App.Overlay.AbyssRankOverlayRenderer.KillFeedEntries(snapshot.Abyss).Count)) * 27 * fontScale,
            "abyss-rank-progress" => 19 + (widget.ShowLabel ? 18 * fontScale : 0) + (widget.ShowLabel ? 66 : 48) * fontScale +
                (widget.ShowLabel && !snapshot.Abyss.Confirmed && snapshot.Abyss.RankPoints is >= 0 ? 11 * fontScale + 3 : 0),
            "daily-kina" => 16 + 24 * fontScale + (widget.ShowLabel ? 36 * fontScale : 0),
            "clock" => 16 + (widget.ShowLabel ? 18 * fontScale : 0) + 26 * fontScale,
            "rare-notice" => 48 + (widget.ShowLabel ? 18 * fontScale : 0) + 24 * fontScale,
            _ when OverlayCatalog.IsLootWidget(widget.Kind) => 48,
            _ => 16 + (widget.ShowLabel ? 18 * fontScale : 0) + 26 * fontScale +
                (metric?.HistoricalAge is not null ? 12 * fontScale + 3
                    : widget.ShowLabel && !string.IsNullOrEmpty(metric?.Detail) ? 12 * fontScale : 0),
        };
        var minimumWidth = widget.Kind switch
        {
            "genus-insight" => 320,
            "abyss-rank-progress" => Math.Max(240, 80 + (widget.ShowIcon ? 100 * fontScale : 0)),
            "abyss-killfeed" => Math.Max(240, 20 + (156 + (widget.ShowIcon ? 44 : 0)) * fontScale),
            _ => 80d,
        };
        var scale = Math.Min(Math.Min(width / referenceWidth, height / referenceHeight),
            Math.Min(width / minimumWidth, height / minimumHeight));
        return new(widget with { X = 0, Y = 0, Width = width / scale, Height = height / scale, FontScale = fontScale },
            scale, referenceWidth, referenceHeight, minimumWidth, minimumHeight);
    }

    public static bool Reflows(OverlayWidget widget) => widget.Kind == "drop-grid" && widget.ItemView == "grid";
    private static double Valid(double value, double fallback) => double.IsFinite(value) && value > 0 ? Math.Clamp(value, 1, 1600) : fallback;
}

public readonly record struct OverlayWindowChrome(double Left, double Top, double Right, double Bottom)
{
    public double Horizontal => Left + Right;
    public double Vertical => Top + Bottom;
    public bool HasTitleBar => Top > 0;
    public double OuterWidth(double contentWidth) => contentWidth + Horizontal;
    public double OuterHeight(double contentHeight) => contentHeight + Vertical;
    public static OverlayWindowChrome For(string? themeId, bool showBorder) => default;
}
