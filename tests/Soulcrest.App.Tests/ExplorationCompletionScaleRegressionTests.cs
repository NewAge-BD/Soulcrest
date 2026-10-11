using System.Drawing;
using System.Drawing.Drawing2D;
using Soulcrest.App.Services;
using Soulcrest.Core.Text;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class ExplorationCompletionScaleRegressionTests
{
    private static readonly ExplorationLayout Layout = ExplorationLayout.ForWindow(new Size(3440, 1440));
    private static readonly ExplorationPlace[] Places =
    [
        new("checked", "altgard", "stronghold", 0, "Synthetic checked stronghold", "Grabräuberversteck"),
        new("open", "altgard", "stronghold", 1, "Synthetic open stronghold", "Provisorium der Klingenlegion"),
    ];

    // Synthetic OCR rows use positions measured in the user's very-small/very-large UI captures.
    // Neither capture nor any character information is part of this fixture.
    private static OcrLine[] Lines(bool large, bool contrast = false) => large
        ? (contrast
            ? [new("Karte: Altgard", 108, 47, 297, 46), new("Garnison", 156, 273, 141, 26),
                new("7%", 461, 328, 46, 21), new("Grabräuberversteck", 60, 426, 270, 22),
                new("Provisorium der Klingenl... 785 m", 60, 521, 439, 28), new("Unentdeckt", 362, 617, 139, 20)]
            : [new("• Altgard", 225, 47, 179, 45), new("Karte.", 108, 49, 125, 33), new("Garnison", 156, 273, 140, 26),
                new("7%", 461, 328, 46, 20), new("Grabräuberversteck", 60, 426, 269, 21),
                new("Provisorium der Klingenl... 785 m", 60, 521, 439, 28), new("Unentdeckt", 362, 617, 138, 20)])
        : (contrast
            ? [new("Karte: Altgard", 81, 34, 221, 35), new("Garnison", 117, 205, 106, 19),
                new("7%", 347, 246, 34, 16), new("Grabräuberversteck", 45, 319, 201, 16),
                new("Provisorium der Klingenl... 785 m", 45, 391, 330, 21), new("Unentdeckt", 264, 461, 106, 17)]
            : [new("Karte: Altgard", 67, 33, 221, 36), new("Garnison", 106, 207, 106, 20),
                new("7%", 337, 244, 35, 16), new("Grabräuberversteck", 37, 320, 200, 19),
                new("Provisorium der Klingenl... 785 m", 39, 390, 329, 22), new("Unentdeckt", 264, 461, 106, 17)]);

    private static Point Center(bool large) => large ? new(484, 438) : new(364, 328);
    private static Bitmap Capture()
    {
        var bitmap = new Bitmap(Layout.CaptureBounds.Width, Layout.CaptureBounds.Height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(65, 65, 65));
        return bitmap;
    }

    private static Rectangle DrawOriginalSymbol(Bitmap bitmap, bool large, Point? center = null)
    {
        using var stream = typeof(ExplorationScanService).Assembly.GetManifestResourceStream("Soulcrest.ExplorationComplete.png")
            ?? throw new InvalidOperationException("Missing exploration completion symbol fixture.");
        using var symbol = new Bitmap(stream);
        var scale = large ? 1.25 : .85;
        var width = (int)(symbol.Width * scale);
        var height = (int)(symbol.Height * scale);
        var at = center ?? Center(large);
        var bounds = new Rectangle(at.X - width / 2, at.Y - height / 2, width, height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.DrawImage(symbol, bounds);
        return bounds;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MeasuredScaleExtremesIdentifyTheMapAndCompleteOnlyTheRowWithTheOriginalCheck(bool large)
    {
        using var bitmap = Capture();
        var bounds = DrawOriginalSymbol(bitmap, large);
        if (large) Assert.True(bounds.Right > Layout.Bounds.Width * .85);
        var symbols = ExplorationCompletionSymbol.Find(bitmap, Layout);
        var hit = Assert.Single(symbols);
        Assert.InRange(Math.Abs(ExplorationCompletionSymbol.CenterY(hit) - Center(large).Y), 0, 5);

        var selector = new ExplorationScanMapSelector(new Dictionary<string, IReadOnlyList<string>> { ["altgard"] = ["Altgard"] });
        var original = Lines(large);
        var contrast = Lines(large, contrast: true);
        var map = selector.Read(Layout, original, contrast);
        Assert.Equal("altgard", map);
        Assert.Null(selector.Observe(map));
        Assert.Equal("altgard", selector.Observe(map));

        var page = ExplorationOcrSelection.Read(original, contrast, Places, bitmap, Layout, "altgard");
        Assert.Equal("stronghold", page.Kind);
        Assert.False(page.Full);
        Assert.True(page.End);
        var rows = page.Rows!;
        Assert.Equal(2, rows.Count);
        Assert.True(rows.Single(row => row.Id == "checked").Complete);
        Assert.False(rows.Single(row => row.Id == "open").Complete);
        Assert.Equal(["checked"], page.Ids);
        Assert.Empty(page.Unmatched);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void APlainGoldRingInTheExpandedColumnCannotCompleteARow(bool large)
    {
        using var bitmap = Capture();
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var gold = new Pen(Color.FromArgb(230, 208, 125), 2);
            var diameter = large ? 38 : 29;
            var at = Center(large);
            graphics.DrawEllipse(gold, at.X - diameter / 2, at.Y - diameter / 2, diameter, diameter);
        }
        Assert.Empty(ExplorationCompletionSymbol.Find(bitmap, Layout));
        var page = ExplorationOcrSelection.Read(Lines(large), Lines(large, contrast: true), Places, bitmap, Layout, "altgard");
        Assert.Empty(page.Ids);
        Assert.All(page.Rows!, row => Assert.False(row.Complete));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EvenAGenuineSymbolOnTheMapBelowUndiscoveredCannotGrantCompletion(bool large)
    {
        using var bitmap = Capture();
        var center = new Point(Center(large).X, large ? 710 : 550);
        DrawOriginalSymbol(bitmap, large, center);
        Assert.Single(ExplorationCompletionSymbol.Find(bitmap, Layout));
        var markerText = new OcrLine("Grabräuberversteck", large ? 60 : 37, center.Y - 10, 269, 21);
        var page = ExplorationOcrSelection.Read(Lines(large).Append(markerText).ToArray(),
            Lines(large, contrast: true).Append(markerText).ToArray(), Places, bitmap, Layout, "altgard");
        Assert.True(page.End);
        var rows = page.Rows!;
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.False(row.Complete));
        Assert.Empty(page.Ids);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NamesWithoutChecksStayOpenAtBothMeasuredScales(bool large)
    {
        using var bitmap = Capture();
        var page = ExplorationOcrSelection.Read(Lines(large), Lines(large, contrast: true), Places, bitmap, Layout, "altgard");
        var rows = page.Rows!;
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.NotNull(row.Id));
        Assert.All(rows, row => Assert.False(row.Complete));
        Assert.Empty(page.Ids);
        Assert.False(page.Full);
    }
}
