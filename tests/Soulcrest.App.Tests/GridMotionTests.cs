using System.Drawing;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>
/// User report 2026-10-03: the overlay showed "Bild bewegt sich" all the time when the pet in the middle
/// has a large idle animation. Only the card grid may decide whether the list stands still.
/// </summary>
public sealed class GridMotionTests
{
    private static readonly string Fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "pet-window", "en-2026-10-03-nocounter-live.png");

    // Card grid of the fixture (5 rows × 3 columns, 145×198 px).
    private static readonly List<Rectangle> Cards =
        [.. from y in new[] { 184, 402, 619, 837, 1055 } from x in new[] { 120, 279, 437 } select new Rectangle(x, y, 145, 198)];

    [Fact]
    public void AnimationOutsideTheGridIsNotMovement()
    {
        using var first = new Bitmap(Fixture);
        using var second = new Bitmap(Fixture);
        using (var graphics = Graphics.FromImage(second))
            graphics.FillRectangle(Brushes.OrangeRed, 900, 150, 800, 1100); // the pet model changes completely
        var motion = new GridMotion();
        Assert.False(motion.IsStill(first, Cards)); // no previous frame yet
        Assert.True(motion.IsStill(second, Cards));
    }

    [Fact]
    public void ScrollingTheListIsMovement()
    {
        using var first = new Bitmap(Fixture);
        using var scrolled = new Bitmap(first.Width, first.Height);
        using (var graphics = Graphics.FromImage(scrolled))
            graphics.DrawImage(first, 0, -12); // list content moved up by 12 px, grid found at the same place
        var motion = new GridMotion();
        motion.IsStill(first, Cards);
        Assert.False(motion.IsStill(scrolled, Cards));
    }
}
