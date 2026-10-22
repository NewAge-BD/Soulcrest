using Soulcrest.Core.Daevanion;
using Soulcrest.Ocr.Daevanion;

namespace Soulcrest.App.Overlay;

public enum DaevanionStatRailItemKind { Stat, Clear, Previous, Next }

public sealed record DaevanionStatRailItem(string Id, DaevanionStatRailItemKind Kind, Rectangle Bounds,
    string Label, string Tooltip, bool Selected = false, bool Enabled = true);

/// <summary>Complete-board-relative control geometry, shared by the live rail and production previews.</summary>
public sealed record DaevanionStatRailLayout(Rectangle Bounds, IReadOnlyList<DaevanionStatRailItem> Items,
    int Page, int PageCount)
{
    public const int LogicalWidth = 176;
    public const int LogicalRowHeight = 28;
    public const int LogicalRowGap = 4;
    public const string ClearId = "@clear";
    public const string PreviousId = "@previous";
    public const string NextId = "@next";
    public bool HasSpace => !Bounds.IsEmpty;

    public static IReadOnlyList<DaevanionStat> StatsFor(DaevanionBoard board)
    {
        var ids = board.Nodes.Where(node => node.Kind == DaevanionNodeKind.Stat).SelectMany(node => node.Effects)
            .Where(effect => effect.Kind == DaevanionEffectKind.StatBonus).Select(effect => effect.StatId).ToHashSet(StringComparer.Ordinal);
        return Array.AsReadOnly(DaevanionCatalog.Default.Stats.Where(stat => ids.Contains(stat.Id)).ToArray());
    }

    public static DaevanionStatRailLayout Build(DaevanionBoard board, DaevanionBoardDetection detection,
        Size clientSize, IReadOnlySet<string> selected, string language, Rectangle toggleClientBounds = default,
        int dpi = 96, int page = 0)
    {
        var empty = new DaevanionStatRailLayout(Rectangle.Empty, [], 0, 0);
        if (dpi is <= 0 or > 960 || clientSize.Width <= 0 || clientSize.Height <= 0 || board.Nodes.Count == 0
            || !double.IsFinite(detection.Origin.X) || !double.IsFinite(detection.Origin.Y)
            || !double.IsFinite(detection.Spacing) || detection.Spacing <= 0
            || !double.IsFinite(detection.NodeSize) || detection.NodeSize <= 0) return empty;
        RectangleF? complete = null;
        foreach (var node in board.Nodes)
        {
            if (node.Row is < 1 or > 15 || node.Column is < 1 or > 15) return empty;
            var x = detection.Origin.X + (node.Column - 1) * detection.Spacing - detection.NodeSize / 2;
            var y = detection.Origin.Y + (node.Row - 1) * detection.Spacing - detection.NodeSize / 2;
            if (Math.Abs(x) > int.MaxValue / 4d || Math.Abs(y) > int.MaxValue / 4d || detection.NodeSize > int.MaxValue / 4d) return empty;
            var frame = new RectangleF((float)x, (float)y, (float)detection.NodeSize, (float)detection.NodeSize);
            var fitted = node.Kind == DaevanionNodeKind.Stat && DaevanionNodeFitting.CanExtend(frame, detection.Spacing)
                ? RectangleF.Union(frame, DaevanionNodeFitting.Bounds(frame, detection.Spacing)) : frame;
            complete = complete is { } previous ? RectangleF.Union(previous, fitted) : fitted;
        }
        var stats = StatsFor(board);
        if (stats.Count == 0) return empty;
        int Scale(int value) => Math.Max(1, (int)Math.Round(value * dpi / 96d));
        var width = Scale(LogicalWidth); var row = Scale(LogicalRowHeight); var gap = Scale(LogicalRowGap);
        var margin = Scale(8); var xOrigin = (int)Math.Ceiling(complete!.Value.Right) + Scale(12);
        if (xOrigin < margin || xOrigin + width > clientSize.Width - margin) return empty;
        var minimumTop = margin;
        // The content switch may itself use the right-side fallback. Keep its original location.
        if (!toggleClientBounds.IsEmpty && toggleClientBounds.Right > xOrigin && toggleClientBounds.Left < xOrigin + width)
            minimumTop = Math.Max(minimumTop, toggleClientBounds.Bottom + gap);
        var available = clientSize.Height - margin - minimumTop;
        var allRows = stats.Count + 1; // A global clear button remains reachable on every page.
        var allHeight = allRows * (row + gap) - gap;
        var paged = allHeight > available;
        var pageSize = paged ? (available + gap) / (row + gap) - 2 : stats.Count;
        if (pageSize < 1) return empty;
        var pageCount = (stats.Count + pageSize - 1) / pageSize;
        page = Math.Clamp(page, 0, pageCount - 1);
        var visibleStats = Math.Min(pageSize, stats.Count - page * pageSize);
        // Reserve the final page's unused slots so navigation never moves underneath the pointer.
        var displayRows = paged ? pageSize : stats.Count;
        var height = (displayRows + 1 + (paged ? 1 : 0)) * (row + gap) - gap;
        var yOrigin = Math.Clamp((int)Math.Floor(complete.Value.Top), minimumTop, clientSize.Height - margin - height);
        var german = language.StartsWith("de", StringComparison.OrdinalIgnoreCase);
        var items = new List<DaevanionStatRailItem>();
        for (var index = 0; index < visibleStats; index++)
        {
            var stat = stats[page * pageSize + index];
            items.Add(new(stat.Id, DaevanionStatRailItemKind.Stat, new(0, index * (row + gap), width, row),
                stat.ShortLabel.For(language), stat.Name.For(language), selected.Contains(stat.Id)));
        }
        var clearY = displayRows * (row + gap);
        items.Add(new(ClearId, DaevanionStatRailItemKind.Clear, new(0, clearY, width, row),
            german ? "Alle Highlights aus" : "Clear all highlights",
            german ? "Alle gespeicherten Stat-Hervorhebungen ausschalten" : "Clear all saved stat highlights",
            Enabled: selected.Count > 0));
        if (paged)
        {
            var half = (width - gap) / 2;
            var navY = clearY + row + gap;
            items.Add(new(PreviousId, DaevanionStatRailItemKind.Previous, new(0, navY, half, row),
                "‹", german ? "Vorherige Stats" : "Previous stats", Enabled: page > 0));
            items.Add(new(NextId, DaevanionStatRailItemKind.Next, new(half + gap, navY, width - half - gap, row),
                "›", german ? "Weitere Stats" : "More stats", Enabled: page + 1 < pageCount));
        }
        return new(new(xOrigin, yOrigin, width, height), Array.AsReadOnly(items.ToArray()), page, pageCount);
    }
}
