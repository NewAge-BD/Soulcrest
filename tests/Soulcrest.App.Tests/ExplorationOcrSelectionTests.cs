using System.Drawing;
using Soulcrest.App.Services;
using Soulcrest.Core.Text;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class ExplorationOcrSelectionTests
{
    private static readonly ExplorationLayout Layout = ExplorationLayout.ForWindow(new Size(2559, 1439));
    private static readonly ExplorationPlace[] Places =
    [new("library", "altgard", "dungeon", 0, "Library of Foresight", null),
        new("vein", "altgard", "dungeon", 0, "Hidden Vein Cave", null),
        new("den", "altgard", "stronghold", 0, "Graverobber Den", null)];
    private static OcrLine[] Lines(string heading = "Sealed Dungeon", string percentage = "31 %", string map = "Map: Altgard") =>
        [new(map, 85, 30, 270, 40), new(heading, 120, 220, 310, 30), new(percentage, 365, 270, 65, 25)];
    private static OcrLine Name(string text, double y = 340) => new(text, 45, y, 270, 25);
    private static Bitmap Capture(bool checks = true)
    {
        var bitmap = new Bitmap(563, 1410);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.FromArgb(45, 45, 45));
        if (checks)
        {
            using var symbol = new Bitmap(Path.Combine(AppContext.BaseDirectory, "fixtures", "exploration-list", "complete-symbol.png"));
            g.DrawImageUnscaled(symbol, 375, 333);
            g.DrawImageUnscaled(symbol, 375, 409);
        }
        return bitmap;
    }
    private static ExplorationScanService.Page Read(IEnumerable<OcrLine> first, IEnumerable<OcrLine> second, Bitmap capture) =>
        ExplorationOcrSelection.Read(first.ToArray(), second.ToArray(), Places, capture, Layout, "altgard");

    [Fact]
    public void EnhancedPassAddsMissingGrayRowsAndDeduplicatesAnExistingRow()
    {
        using var capture = Capture();
        var first = Lines().Append(Name("Library of Foresight"));
        var second = first.Append(Name("Hidden Vein Cave", 416));
        var page = Read(first, second, capture);
        Assert.Equal(new[] { "library", "vein" }, page.Ids.Order());
        Assert.Equal(2, page.Rows!.Count);
        Assert.Empty(page.Unmatched);
        Assert.False(page.Full);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AValidNameWinsOverTheOtherPassesUnknownName(bool validFirst)
    {
        using var capture = Capture();
        var valid = Lines().Append(Name("Library of Foresight"));
        var unknown = Lines().Append(Name("Unreadable list name"));
        var page = validFirst ? Read(valid, unknown, capture) : Read(unknown, valid, capture);
        Assert.Equal(["library"], page.Ids);
        Assert.Equal("library", Assert.Single(page.Rows!).Id);
        Assert.Empty(page.Unmatched);
    }

    [Fact]
    public void TwoValidButConflictingNamesAtOnePhysicalRowStayUnmatched()
    {
        using var capture = Capture();
        var page = Read(Lines().Append(Name("Library of Foresight")), Lines().Append(Name("Hidden Vein Cave")), capture);
        Assert.Empty(page.Ids);
        var row = Assert.Single(page.Rows!);
        Assert.Null(row.Id);
        Assert.False(row.Complete);
        Assert.Contains("Library of Foresight", page.Unmatched);
        Assert.Contains("Hidden Vein Cave", page.Unmatched);
    }

    [Fact]
    public void APhysicalRowWithTwoEquidistantEnhancedAlternativesCannotBorrowTheCheck()
    {
        using var capture = Capture();
        var page = Read(Lines().Append(Name("Library of Foresight")),
            Lines().Concat([Name("Library of Foresight", 338), Name("Hidden Vein Cave", 342)]), capture);
        Assert.Empty(page.Ids);
        Assert.NotEmpty(page.Unmatched);
        Assert.All(page.Rows!, row => { Assert.Null(row.Id); Assert.False(row.Complete); });
    }

    [Fact]
    public void CategoryConflictPreservesOriginalReading()
    {
        using var capture = Capture();
        var page = Read(Lines().Append(Name("Library of Foresight")),
            Lines("Stronghold").Append(Name("Graverobber Den")), capture);
        Assert.Equal("dungeon", page.Kind);
        Assert.Equal(["library"], page.Ids);
        Assert.Single(page.Rows!);
    }

    [Fact]
    public void EnhancedHundredPercentAloneCannotCompleteTheCategory()
    {
        using var capture = Capture(false);
        var page = Read(Lines(), Lines(percentage: "100 %"), capture);
        Assert.Equal("dungeon", page.Kind);
        Assert.False(page.Full);
        Assert.Empty(page.Ids);
    }

    [Fact]
    public void OriginalHundredPercentTakesPriorityEvenWhenEnhancedCategoryConflicts()
    {
        using var capture = Capture(false);
        var page = Read(Lines(percentage: "100 %"), Lines("Stronghold", "100 %"), capture);
        Assert.True(page.Full);
        Assert.Equal("dungeon", page.Kind);
        Assert.Equal(new[] { "library", "vein" }, page.Ids.Order());
        Assert.DoesNotContain("den", page.Ids);
    }

    [Fact]
    public void HeadersAndMapTitlesCannotBeBorrowedBetweenPasses()
    {
        using var capture = Capture();
        var first = Lines().Where(line => line.Text != "Sealed Dungeon").Append(Name("Library of Foresight"));
        var second = Lines(map: "Map: Verteron").Append(Name("Library of Foresight"));
        var page = Read(first, second, capture);
        Assert.Null(page.Kind);
        Assert.False(page.Full);
        Assert.Empty(page.Ids);
    }

    [Fact]
    public void MissingOriginalHeadingAllowsEnhancedNamesButNotItsHundredPercent()
    {
        using var capture = Capture();
        var page = Read(Lines().Where(line => line.Text != "Sealed Dungeon"),
            Lines(percentage: "100 %").Append(Name("Library of Foresight")), capture);
        Assert.Equal("dungeon", page.Kind);
        Assert.False(page.Full);
        Assert.Equal(["library"], page.Ids);
        Assert.DoesNotContain("vein", page.Ids);
    }

    [Fact]
    public void BothValidCategoriesMustAgreeOnListEnd()
    {
        using var capture = Capture();
        var first = Lines().Append(Name("Library of Foresight"));
        var second = first.Append(Name("Undiscovered", 500));
        Assert.False(Read(first, second, capture).End);
        Assert.False(Read(second, first, capture).End);
        Assert.True(Read(second, second, capture).End);
    }

    [Fact]
    public void EnhancedEndCanBeUsedWhenOriginalCategoryIsMissing()
    {
        using var capture = Capture();
        var page = Read(Lines().Where(line => line.Text != "Sealed Dungeon"),
            Lines().Concat([Name("Library of Foresight"), Name("Undiscovered", 500)]), capture);
        Assert.Equal("dungeon", page.Kind);
        Assert.True(page.End);
        Assert.Equal(["library"], page.Ids);
    }

    [Fact]
    public void NeitherReadingCanCompleteNamesWithoutOriginalCheckPixels()
    {
        using var capture = Capture(false);
        var lines = Lines().Append(Name("Library of Foresight"));
        var page = Read(lines, lines, capture);
        Assert.Empty(page.Ids);
        Assert.False(Assert.Single(page.Rows!).Complete);
    }

    [Fact]
    public void CategoryWordsCanBeSeparatedFromTheirCircularIconWithoutBorrowingOriginalHeader()
    {
        using var capture = Capture();
        var iconHeading = new OcrLine("(O) Sealed Dungeon", 57, 210, 274, 39)
        {
            Words = [new("(O)", 57, 210, 40, 39), new("Sealed", 124, 220, 85, 26), new("Dungeon", 219, 220, 112, 26)]
        };
        var first = Lines().Where(line => line.Text != "Sealed Dungeon");
        var second = Lines().Where(line => line.Text != "Sealed Dungeon").Append(iconHeading).Append(Name("Library of Foresight"));
        var page = Read(first, second, capture);
        Assert.Equal("dungeon", page.Kind);
        Assert.Equal(["library"], page.Ids);
        Assert.False(page.Full);
    }

    [Fact]
    public void OrdinaryPrefixWordsCannotBeRemovedToManufactureACategoryHeading()
    {
        using var capture = Capture();
        var heading = new OcrLine("Other Sealed Dungeon", 57, 210, 274, 39)
        {
            Words = [new("Other", 57, 210, 40, 39), new("Sealed", 124, 220, 85, 26), new("Dungeon", 219, 220, 112, 26)]
        };
        var first = Lines().Where(line => line.Text != "Sealed Dungeon");
        var second = first.Append(heading).Append(Name("Library of Foresight"));
        var page = Read(first, second, capture);
        Assert.Null(page.Kind);
        Assert.Empty(page.Ids);
    }

    [Fact]
    public void HeadingIconCannotBeRemovedWithoutItsIndividualWordCoordinates()
    {
        using var capture = Capture();
        var first = Lines().Where(line => line.Text != "Sealed Dungeon");
        var second = first.Append(new("(O) Sealed Dungeon", 57, 210, 274, 39)).Append(Name("Library of Foresight"));
        var page = Read(first, second, capture);
        Assert.Null(page.Kind);
        Assert.Empty(page.Ids);
    }

    [Fact]
    public void CategoryWordsInTheMapTitleAreaCannotBecomeAListHeading()
    {
        using var capture = Capture();
        var heading = new OcrLine("(O) Sealed Dungeon", 57, 60, 274, 39)
        {
            Words = [new("(O)", 57, 60, 40, 39), new("Sealed", 124, 70, 85, 26), new("Dungeon", 219, 70, 112, 26)]
        };
        var first = Lines().Where(line => line.Text != "Sealed Dungeon");
        var second = first.Append(heading).Append(Name("Library of Foresight"));
        var page = Read(first, second, capture);
        Assert.Null(page.Kind);
        Assert.Empty(page.Ids);
    }

    [Fact]
    public void TextOutsideTheListCannotBeSilentlyDroppedFromAHeading()
    {
        using var capture = Capture();
        var heading = new OcrLine("(O) Sealed Dungeon Other", 57, 210, 600, 39)
        {
            Words = [new("(O)", 57, 210, 40, 39), new("Sealed", 124, 220, 85, 26), new("Dungeon", 219, 220, 112, 26),
                new("Other", 590, 220, 65, 26)]
        };
        var first = Lines().Where(line => line.Text != "Sealed Dungeon");
        var second = first.Append(heading).Append(Name("Library of Foresight"));
        var page = Read(first, second, capture);
        Assert.Null(page.Kind);
        Assert.Empty(page.Ids);
    }
}
