using System.Drawing;
using System.Text.RegularExpressions;
using Soulcrest.Core.Text;

namespace Soulcrest.App.Services;

/// <summary>A category heading anchors its percentage and rows. Only checked rows are completions.</summary>
internal static class ExplorationList
{
    internal sealed record Row(OcrLine Line, string? Id, bool Complete);

    internal static ExplorationScanService.Page Read(IReadOnlyList<OcrLine> lines, IReadOnlyList<ExplorationPlace> candidates,
        Bitmap capture, ExplorationLayout layout, string map, IReadOnlyCollection<string>? mapTitles = null)
    {
        if (!layout.ShowsMap(lines, mapTitles ?? [map])) return new([], [], false);
        var headings = lines.Where(l => l.Y >= layout.Bounds.Top && l.Y < layout.Bounds.Top + layout.Bounds.Height * .13 &&
            l.X >= layout.Bounds.Left && l.X + l.Width <= layout.Bounds.Right && ExplorationLayout.HeadingKind(l.Text) is not null).ToArray();
        if (headings.Length != 1) return new([], [], false);
        var heading = headings[0];
        var kind = ExplorationLayout.HeadingKind(heading.Text)!;
        var places = candidates.Where(p => p.Map == map && p.Kind == kind).DistinctBy(p => p.Id).ToArray();
        if (places.Length == 0) return new([], [], false);
        var percentages = lines.Where(l => l.Y >= heading.Y + heading.Height * .7 &&
            l.Y + l.Height <= heading.Y + heading.Height * 5 && l.X >= layout.Bounds.Width * .55 &&
            l.X + l.Width <= layout.Bounds.Right && Regex.IsMatch(l.Text, @"^\s*(?:[1Il][0Oo]{2}|[0-9]{1,2})\s*%\s*$")).ToArray();
        // Windows OCR can read the three digits as "IOO". Correct only this exact percentage
        // token at the heading's percentage position; never normalise names or arbitrary text.
        if (percentages.Length == 1 && Regex.IsMatch(percentages[0].Text, @"^\s*[1Il][0Oo]{2}\s*%\s*$"))
            return new(places.Select(p => p.Id).ToHashSet(), [], false, kind, true, []);

        var rowTop = percentages.Length == 1 ? percentages[0].Y + percentages[0].Height * 2 : heading.Y + heading.Height * 3;
        var region = layout.Rows(lines).Where(l => l.Y >= rowTop).ToArray();
        var stop = region.Where(l => ExplorationService.Normalize(l.Text) is "undiscovered" or "unentdeckt")
            .Select(l => l.Y).DefaultIfEmpty(double.PositiveInfinity).Min();
        var names = region.Where(l => l.Y < stop && l.X >= layout.Bounds.Width * .025 && l.X < layout.Bounds.Width * .65 &&
            ExplorationService.Normalize(l.Text).Length >= 5).OrderBy(l => l.Y).ToArray();
        var symbols = ExplorationCompletionSymbol.Find(capture, layout);
        // A symbol can belong to exactly one nearest row; never borrow a check from the next line.
        var checkedRows = new HashSet<OcrLine>();
        foreach (var symbol in symbols)
        {
            var nearby = names.Select(l => (Line: l, Distance: Math.Abs(l.Y + l.Height / 2 - ExplorationCompletionSymbol.CenterY(symbol))))
                .Where(r => symbol.Left > r.Line.X + r.Line.Height * 2 &&
                    r.Distance <= Math.Max(r.Line.Height * .75, symbol.Height * .55)).OrderBy(r => r.Distance).ToArray();
            if (nearby.Length == 1) checkedRows.Add(nearby[0].Line);
        }
        var rows = names.Select(l => new Row(l, ExplorationService.Match(l.Text, places)?.Id, checkedRows.Contains(l))).ToArray();
        return new(rows.Where(r => r.Complete && r.Id is not null).Select(r => r.Id!).ToHashSet(),
            rows.Where(r => r.Id is null).Select(r => r.Line.Text).Distinct().ToArray(), double.IsFinite(stop), kind, false, rows);
    }
}
