using System.Drawing;
using Soulcrest.Core.Text;

namespace Soulcrest.App.Services;

/// <summary>Fixed list mask from the maximum-scale screenshot: left 22% of the game window.</summary>
public sealed record ExplorationLayout(Rectangle Bounds)
{
    // Include the map title in the same capture, so completion cannot spill into another map.
    public Rectangle CaptureBounds => Rectangle.FromLTRB(Bounds.Left, 0, Bounds.Right, Bounds.Bottom);
    public static ExplorationLayout ForWindow(Size window) => new(Rectangle.FromLTRB(
        0, (int)(window.Height * .12), (int)Math.Ceiling(window.Width * .22), (int)(window.Height * .98)));

    public IReadOnlyList<OcrLine> Rows(IReadOnlyList<OcrLine> lines) => lines.Where(l =>
        l.Y >= Bounds.Top && l.Y + l.Height <= Bounds.Bottom && l.X >= Bounds.Left && l.X + l.Width <= Bounds.Right &&
        !IsHeading(l.Text)).ToArray();

    internal static string? HeadingKind(string text) => ExplorationService.Normalize(text) switch
    {
        "sealeddungeon" or "sealeddungeons" or "versiegelterdungeon" or "versiegeltedungeons" => "dungeon",
        "stronghold" or "strongholds" or "garnison" or "garnisonen" => "stronghold",
        _ => null
    };
    private static bool IsHeading(string text) => HeadingKind(text) is not null;

    internal bool ShowsMap(IReadOnlyList<OcrLine> lines, string map) => ShowsMap(lines, [map]);

    /// <summary>The title "Map: …" / "Karte: …" names one of the map's names (German or English client).</summary>
    internal bool ShowsMap(IReadOnlyList<OcrLine> lines, IReadOnlyCollection<string> names)
    {
        var wanted = names.Select(ExplorationService.Normalize).SelectMany(n => new[] { "map" + n, "karte" + n }).ToHashSet(StringComparer.Ordinal);
        return lines.Any(l => l.Y >= 0 && l.Y + l.Height < Bounds.Top * .65 && l.X >= Bounds.Left && l.X + l.Width <= Bounds.Right
            && wanted.Contains(ExplorationService.Normalize(l.Text)));
    }
}

public sealed record ExplorationRowMark(Rectangle Bounds, bool Recognized);
public sealed record ExplorationScanFeedback(Rectangle Window, Rectangle? List, string Message, int Read, int Total,
    IReadOnlyList<ExplorationRowMark> Marks, bool Ready);
