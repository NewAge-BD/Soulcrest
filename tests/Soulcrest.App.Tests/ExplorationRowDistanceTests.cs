using System.Drawing;
using Soulcrest.App.Services;
using Soulcrest.Core.Text;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class ExplorationRowDistanceTests
{
    private static readonly ExplorationLayout Layout = ExplorationLayout.ForWindow(new Size(2559, 1439));
    // The complete German label is present in the imported Altgard catalog.
    private static readonly ExplorationPlace Place = new("stronghold-test", "altgard", "stronghold", 0,
        "Synthetic stronghold fixture", "Provisorium der Klingenlegion");
    private static OcrLine Row(string text) => new(text, 48, 357, 351, 21);
    private static OcrLine[] Lines(OcrLine row, string map = "Karte: Altgard", string heading = "Garnison") =>
        [new(map, 86, 36, 235, 37), new(heading, 124, 219, 115, 21), new("7%", 370, 263, 36, 15), row,
            new("Unentdeckt", 291, 434, 109, 16)];
    private static Bitmap Capture(bool complete = false)
    {
        var bitmap = new Bitmap(Layout.CaptureBounds.Width, Layout.CaptureBounds.Height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(65, 65, 65));
        if (complete)
        {
            using var stream = typeof(ExplorationScanService).Assembly.GetManifestResourceStream("Soulcrest.ExplorationComplete.png")
                ?? throw new InvalidOperationException("Missing exploration completion symbol fixture.");
            using var symbol = new Bitmap(stream);
            graphics.DrawImageUnscaled(symbol, 360, 357 + (21 - symbol.Height) / 2);
        }
        return bitmap;
    }

    [Theory]
    [InlineData("Provisorium der Klingenle... 785 m")]
    [InlineData("Provisorium der Klingenl... 785 m")]
    [InlineData("Provisorium der Klingenle.. 0 m")]
    [InlineData("Provisorium der Klingenle… 12345 m")]
    public void TruncatedNamesWithAnIntegerMetreFieldUseTheCatalogReferenceIndex(string text)
    {
        using var capture = Capture();
        Assert.Equal(Place.Id, ExplorationService.Match(text, [Place])?.Id);
        var page = ExplorationList.Read(Lines(Row(text)), [Place], capture, Layout, "altgard");
        var row = Assert.Single(page.Rows!);
        Assert.Equal(Place.Id, row.Id);
        Assert.Equal(text, row.Line.Text);
        Assert.False(row.Complete);
        Assert.False(page.Full);
        Assert.Empty(page.Ids);
        Assert.Empty(page.Unmatched);
        Assert.True(page.End);
    }

    [Fact]
    public void TheOriginalLineWordsAndBoundsArePreserved()
    {
        using var capture = Capture();
        var original = Row("Provisorium der Klingenle... 785 m") with
        {
            Words = [new("Provisorium", 48, 357, 112, 21), new("der", 166, 357, 30, 21),
                new("Klingenle...", 202, 357, 125, 21), new("785", 340, 357, 31, 21), new("m", 379, 357, 20, 21)]
        };
        var row = Assert.Single(ExplorationList.Read(Lines(original), [Place], capture, Layout, "altgard").Rows!);
        Assert.Same(original, row.Line);
        Assert.Equal(Place.Id, row.Id);
        Assert.Equal(5, row.Line.Words.Count);
        Assert.False(row.Complete);
    }

    [Fact]
    public void ARecoveredNameStillNeedsItsOriginalCompletionSymbolAndTwoFrames()
    {
        using var capture = Capture(complete: true);
        var page = ExplorationList.Read(Lines(Row("Provisorium der Klingenle... 785 m")), [Place], capture, Layout, "altgard");
        Assert.True(Assert.Single(page.Rows!).Complete);
        Assert.Equal([Place.Id], page.Ids);
        var confirmation = new ExplorationScanService.Confirmation();
        Assert.Empty(confirmation.Observe(page));
        Assert.Equal([Place.Id], confirmation.Observe(page));
        Assert.False(confirmation.FullConfirmed);
    }

    [Theory]
    [InlineData("Provisorium der Klingenle... 785m")]
    [InlineData("Provisorium der Klingenle... -785 m")]
    [InlineData("Provisorium der Klingenle... +785 m")]
    [InlineData("Provisorium der Klingenle... 785.5 m")]
    [InlineData("Provisorium der Klingenle... 785,5 m")]
    [InlineData("Provisorium der Klingenle... 785 km")]
    [InlineData("Provisorium der Klingenle... 785 M")]
    [InlineData("Provisorium der Klingenle... 785 ft")]
    [InlineData("Provisorium der Klingenle... Complete 785 m")]
    [InlineData("Provisorium der Klingenle... 785 m Complete")]
    [InlineData("Provisorium der Klingenle...Fremde Kartenbeschriftung")]
    public void TextAfterAnExplicitEllipsisIsNotPartOfTheCatalogNameAndCannotCreateACheck(string text)
    {
        using var capture = Capture();
        var page = ExplorationList.Read(Lines(Row(text)), [Place], capture, Layout, "altgard");
        var row = Assert.Single(page.Rows!);
        Assert.Equal(Place.Id, row.Id);
        Assert.Equal(text, row.Line.Text);
        Assert.False(row.Complete);
        Assert.Empty(page.Unmatched);
        Assert.Empty(page.Ids);
        Assert.False(page.Full);
    }

    [Theory]
    [InlineData("Provisorium der Klingenle 785 m")]
    [InlineData("Provisorium der Klingenle. 785 m")]
    public void TextWithoutAnExplicitEllipsisCannotDiscardAnArbitrarySuffix(string text)
    {
        using var capture = Capture();
        var page = ExplorationList.Read(Lines(Row(text)), [Place], capture, Layout, "altgard");
        Assert.Null(Assert.Single(page.Rows!).Id);
        Assert.Equal([text], page.Unmatched);
        Assert.Empty(page.Ids);
    }

    [Fact]
    public void AmbiguousAndShortPrefixesStayUnmatchedWithAnEllipsisSuffix()
    {
        using var capture = Capture();
        var other = Place with { Id = "synthetic-second", De = "Provisorium der Klingenlegion – synthetic second candidate" };
        var ambiguous = ExplorationList.Read(Lines(Row("Provisorium der Klingenle... 785 m")), [Place, other], capture, Layout, "altgard");
        Assert.Null(Assert.Single(ambiguous.Rows!).Id);
        Assert.Empty(ambiguous.Ids);
        var shortPrefix = ExplorationList.Read(Lines(Row("Provis... 785 m")), [Place], capture, Layout, "altgard");
        Assert.Null(Assert.Single(shortPrefix.Rows!).Id);
        Assert.Empty(shortPrefix.Ids);
    }

    [Fact]
    public void TruncatedReferenceMatchingCannotBypassMapOrCategoryValidation()
    {
        using var capture = Capture();
        var row = Row("Provisorium der Klingenle... 785 m");
        var wrongMap = ExplorationList.Read(Lines(row, map: "Karte: Ishalgen"), [Place], capture, Layout, "altgard");
        var wrongKind = ExplorationList.Read(Lines(row, heading: "Versiegelter Dungeon"), [Place], capture, Layout, "altgard");
        Assert.Null(wrongMap.Kind);
        Assert.Null(wrongKind.Kind);
        Assert.Empty(wrongMap.Ids);
        Assert.Empty(wrongKind.Ids);
    }
}
