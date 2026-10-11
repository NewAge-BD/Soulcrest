using System.Drawing;
using Soulcrest.Core.Text;

namespace Soulcrest.App.Services;

/// <summary>Combines two readings of one capture, keeping completion evidence in its original pixels.</summary>
internal static class ExplorationOcrSelection
{
    internal static ExplorationScanService.Page Read(IReadOnlyList<OcrLine> originalLines,
        IReadOnlyList<OcrLine> enhancedLines, IReadOnlyList<ExplorationPlace> candidates, Bitmap original,
        ExplorationLayout layout, string map, IReadOnlyCollection<string>? mapTitles = null)
    {
        var first = ExplorationList.Read(IsolateHeadings(originalLines, layout), candidates, original, layout, map, mapTitles);
        // The enhanced pass may restore names; it must never manufacture category-wide completion.
        if (first.Full) return first;
        var second = ExplorationList.Read(IsolateHeadings(enhancedLines, layout), candidates, original, layout, map, mapTitles, allowFullCompletion: false);
        if (second.Kind is null) return first;
        if (first.Kind is null) return second;
        if (first.Kind != second.Kind) return first;

        var firstRows = first.Rows ?? [];
        var secondRows = second.Rows ?? [];
        var forward = firstRows.Select(row => Nearest(row, secondRows)).ToArray();
        var backward = secondRows.Select(row => Nearest(row, firstRows)).ToArray();
        var consumed = new HashSet<int>();
        var rows = new List<ExplorationList.Row>();
        var additionalUnknown = new List<string>();
        for (var index = 0; index < firstRows.Count; index++)
        {
            var row = firstRows[index];
            if (forward[index] is { } partner && backward[partner] == index)
            {
                consumed.Add(partner);
                var other = secondRows[partner];
                if (row.NameConflict || other.NameConflict || Rejects(row, other.Id) || Rejects(other, row.Id)
                    || row.Id is not null && other.Id is not null && row.Id != other.Id)
                {
                    rows.Add(row with { Id = null, Complete = false });
                    additionalUnknown.Add(other.Line.Text);
                }
                else if (row.Id is null && other.Id is not null) rows.Add(other);
                else if (row.Id is not null && other.Id == row.Id)
                    rows.Add(row with { Complete = row.Complete || other.Complete });
                else
                {
                    rows.Add(row);
                    if (row.Id is null) additionalUnknown.Add(other.Line.Text);
                }
            }
            else rows.Add(secondRows.Any(other => SameRow(row, other))
                ? row with { Id = null, Complete = false } : row);
        }
        for (var index = 0; index < secondRows.Count; index++)
        {
            if (consumed.Contains(index)) continue;
            var row = secondRows[index];
            rows.Add(firstRows.Any(other => SameRow(row, other))
                ? row with { Id = null, Complete = false } : row);
        }
        var ordered = rows.OrderBy(row => row.Line.Y).ToArray();
        return new(ordered.Where(row => row.Complete && row.Id is not null).Select(row => row.Id!).ToHashSet(),
            ordered.Where(row => row.Id is null).Select(row => row.Line.Text).Concat(additionalUnknown).Distinct().ToArray(),
            first.End && second.End, first.Kind, false, ordered);
    }

    // An ambiguous reading is evidence too: a second pass cannot replace it with an unrelated ID.
    private static bool Rejects(ExplorationList.Row row, string? id) =>
        id is not null && row.CandidateIds is { Count: > 0 } candidates && !candidates.Contains(id);

    private static bool SameRow(ExplorationList.Row first, ExplorationList.Row second) =>
        Math.Abs(Center(first.Line) - Center(second.Line)) <= Math.Max(first.Line.Height, second.Line.Height) * .75;

    private static int? Nearest(ExplorationList.Row row, IReadOnlyList<ExplorationList.Row> others)
    {
        var neighbors = others.Select((other, index) => (Row: other, Index: index))
            .Where(other => SameRow(row, other.Row))
            .Select(other => (other.Index, Distance: Math.Abs(Center(row.Line) - Center(other.Row.Line))))
            .OrderBy(other => other.Distance).Take(2).ToArray();
        // A split/ambiguous OCR row cannot grant two different places the same check.
        return neighbors.Length == 0 || neighbors.Length > 1 && neighbors[1].Distance - neighbors[0].Distance <= .5
            ? null : neighbors[0].Index;
    }

    private static double Center(OcrLine line) => line.Y + line.Height / 2;

    private static IReadOnlyList<OcrLine> IsolateHeadings(IReadOnlyList<OcrLine> lines, ExplorationLayout layout) => lines.Select(line =>
    {
        if (line.Y < layout.Bounds.Top || line.Y >= layout.Bounds.Top + layout.Bounds.Height * .13 || line.Words.Count < 2)
            return line;
        var textLeft = layout.Bounds.Left + layout.Bounds.Width * .18;
        var iconWords = line.Words.Where(word => word.X < textLeft).ToArray();
        var words = line.Words.Where(word => word.X >= textLeft && word.X + word.Width <= layout.Bounds.Right).ToArray();
        // At 2x OCR the circular category icon can become a leading "(O)" word. Recover
        // the exact category words only, independently in each pass, with no fuzzy header match.
        if (iconWords.Length != 1 || ExplorationService.Normalize(iconWords[0].Text) is not ("" or "o" or "0") || words.Length == 0
            || iconWords.Length + words.Length != line.Words.Count
            || iconWords[0].X < layout.Bounds.Left + layout.Bounds.Width * .045 || iconWords[0].Right > textLeft)
            return line;
        var text = string.Join(" ", words.Select(word => word.Text));
        if (ExplorationLayout.HeadingKind(text) is null) return line;
        var left = words.Min(word => word.X);
        var top = words.Min(word => word.Y);
        var bottom = words.Max(word => word.Bottom);
        if (top < layout.Bounds.Top || bottom > layout.Bounds.Top + layout.Bounds.Height * .13
            || Math.Abs(iconWords[0].Y + iconWords[0].Height / 2 - (top + bottom) / 2) > Math.Max(iconWords[0].Height, bottom - top) * .75)
            return line;
        return line with { Text = text, X = left, Y = top, Width = words.Max(word => word.X + word.Width) - left,
            Height = bottom - top, Words = words };
    }).ToArray();
}
