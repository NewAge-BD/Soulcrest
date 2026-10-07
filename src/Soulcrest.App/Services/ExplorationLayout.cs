using System.Drawing;
using Soulcrest.Core.Text;

namespace Soulcrest.App.Services;

/// <summary>Fixed list mask from the maximum-scale screenshot: left 22% of the game window.</summary>
public sealed record ExplorationLayout(Rectangle Bounds)
{
    public static ExplorationLayout ForWindow(Size window) => new(Rectangle.FromLTRB(
        0, (int)(window.Height * .12), (int)Math.Ceiling(window.Width * .22), (int)(window.Height * .98)));

    public IReadOnlyList<OcrLine> Rows(IReadOnlyList<OcrLine> lines) => lines.Where(l =>
        l.Y >= Bounds.Top && l.Y + l.Height <= Bounds.Bottom && l.X >= Bounds.Left && l.X + l.Width <= Bounds.Right &&
        !IsHeading(l.Text)).ToArray();

    private static bool IsHeading(string text) => ExplorationService.Normalize(text) is
        "sealeddungeon" or "sealeddungeons" or "versiegelterdungeon" or "versiegeltedungeons" or
        "stronghold" or "strongholds" or "garnison" or "garnisonen";
}

public sealed record ExplorationRowMark(Rectangle Bounds, bool Recognized);
public sealed record ExplorationScanFeedback(Rectangle Window, Rectangle? List, string Message, int Read, int Total,
    IReadOnlyList<ExplorationRowMark> Marks, bool Ready);
