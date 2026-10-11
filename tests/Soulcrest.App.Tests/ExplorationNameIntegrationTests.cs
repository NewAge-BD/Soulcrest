using System.Drawing;
using Soulcrest.App.Services;
using Soulcrest.Core.Text;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class ExplorationNameIntegrationTests
{
    private static readonly ExplorationLayout Layout = ExplorationLayout.ForWindow(new Size(2559, 1439));
    private static readonly ExplorationPlace Prison = new("synthetic-prison", "altgard", "dungeon", 1,
        "Abandoned Archon Jail Entrance", "Eingang zum Verlassenen Gefängnis der Archonen");
    private static readonly ExplorationPlace OtherPrison = new("synthetic-other-prison", "altgard", "dungeon", 2,
        "Abandoned Synthetic Jail Entrance", "Eingang zum Verlassenen Gefängnis der Synthetischen Sucher");
    private static readonly ExplorationPlace Chamber = new("synthetic-chamber", "altgard", "dungeon", 3,
        "Synthetic Chamber of Mirrors Entrance", "Synthetische Spiegelkammer");
    private static readonly ExplorationPlace[] Places = [Prison, Chamber];

    private static OcrLine Name(string text, double y = 340, double height = 24) => new(text, 46, y, 438, height);
    private static OcrLine[] Lines(params OcrLine[] rows) =>
        [new("Map: Altgard", 85, 30, 270, 40), new("Sealed Dungeon", 120, 220, 310, 30),
            new("31 %", 365, 270, 65, 25), .. rows];
    private static double Center(OcrLine row) => row.Y + row.Height / 2;

    private static Bitmap Capture(params double[] checks)
    {
        var image = new Bitmap(Layout.CaptureBounds.Width, Layout.CaptureBounds.Height);
        using var graphics = Graphics.FromImage(image);
        graphics.Clear(Color.FromArgb(65, 65, 65));
        using var resource = typeof(ExplorationScanService).Assembly.GetManifestResourceStream("Soulcrest.ExplorationComplete.png")
            ?? throw new InvalidOperationException("Missing exploration completion symbol fixture.");
        using var symbol = new Bitmap(resource);
        foreach (var center in checks) graphics.DrawImageUnscaled(symbol, 375, (int)(center - symbol.Height / 2d));
        return image;
    }

    private static ExplorationScanService.Page Read(OcrLine[] original, OcrLine[] contrast, Bitmap capture,
        IReadOnlyList<ExplorationPlace>? places = null) =>
        ExplorationOcrSelection.Read(original, contrast, places ?? Places, capture, Layout, "altgard");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReportedMergedMapLabelsResolveThroughTheFullCatalogButStillNeedAnOriginalCheck(bool checkedRow)
    {
        var original = Name("Verlassenes Gefängnis der...Wlu s Brücke");
        var contrast = Name("Verlassenes Gefängnis der...Mu@s Brücke", 338, 30);
        using var capture = checkedRow ? Capture(Center(original)) : Capture();
        var page = Read(Lines(original), Lines(contrast), capture);
        var row = Assert.Single(page.Rows!);
        Assert.Equal(Prison.Id, row.Id);
        Assert.Same(original, row.Line);
        Assert.Equal(checkedRow, row.Complete);
        Assert.False(page.Full);
        Assert.Empty(page.Unmatched);
        var confirmation = new ExplorationScanService.Confirmation();
        Assert.Empty(confirmation.Observe(page));
        var confirmed = confirmation.Observe(page);
        if (checkedRow) Assert.Equal([Prison.Id], confirmed);
        else Assert.Empty(confirmed);
    }

    [Fact]
    public void ARecoveredMergedLineKeepsItsOriginalWordsAndGeometry()
    {
        var original = Name("Verlassenes Gefängnis der...Wlu s Brücke") with
        {
            Words = [new("Verlassenes", 46, 340, 125, 24), new("Gefängnis", 177, 340, 110, 24),
                new("der...Wlu", 293, 340, 100, 24), new("s", 399, 340, 12, 24), new("Brücke", 417, 340, 67, 24)]
        };
        using var capture = Capture();
        var row = Assert.Single(Read(Lines(original), Lines(Name("Verlassenes Gefängnis der...Mu@s Brücke", 338, 30)), capture).Rows!);
        Assert.Same(original, row.Line);
        Assert.Equal(original.Words, row.Line.Words);
        Assert.Equal(Prison.Id, row.Id);
        Assert.False(row.Complete);
    }

    [Fact]
    public void GermanAndEnglishPrefixesOfTheSamePlaceDeduplicateAtOnePhysicalRow()
    {
        var original = Name("Verlassenes Gefängnis der Arch...Fremdes Label");
        var contrast = Name("Abandoned Archon J...Another label", 338, 30);
        using var capture = Capture(Center(original));
        var page = Read(Lines(original), Lines(contrast), capture);
        Assert.Equal(Prison.Id, Assert.Single(page.Rows!).Id);
        Assert.Equal([Prison.Id], page.Ids);
        Assert.Empty(page.Unmatched);
    }

    [Fact]
    public void DistinctRecognizedPrefixesAcrossPassesCannotShareACompletionCheck()
    {
        var original = Name("Verlassenes Gefängnis der...Map label");
        using var capture = Capture(Center(original));
        var page = Read(Lines(original), Lines(Name("Synthetic Chamber of...Other label", 338, 30)), capture);
        Assert.Empty(page.Ids);
        var row = Assert.Single(page.Rows!);
        Assert.Null(row.Id);
        Assert.False(row.Complete);
        Assert.NotEmpty(page.Unmatched);
    }

    [Fact]
    public void AnAmbiguousPrefixCannotBeOverruledByAnUnrelatedUniqueContrastName()
    {
        var original = Name("Verlassenes Gefängnis der...Map label");
        using var capture = Capture(Center(original));
        var page = Read(Lines(original), Lines(Name(Chamber.En, 338, 30)), capture, [Prison, OtherPrison, Chamber]);
        Assert.Empty(page.Ids);
        var row = Assert.Single(page.Rows!);
        Assert.Null(row.Id);
        Assert.False(row.Complete);
        Assert.NotEmpty(page.Unmatched);
    }

    [Fact]
    public void AUniqueContrastNameInsideTheOriginalCandidateGroupCanResolveThatPhysicalRow()
    {
        var original = Name("Verlassenes Gefängnis der...Map label");
        using var capture = Capture(Center(original));
        var page = Read(Lines(original), Lines(Name(Prison.En, 338, 30)), capture, [Prison, OtherPrison, Chamber]);
        Assert.Equal(Prison.Id, Assert.Single(page.Rows!).Id);
        Assert.Equal([Prison.Id], page.Ids);
        Assert.Empty(page.Unmatched);
    }

    [Fact]
    public void TextAfterTheEllipsisCannotDisambiguateTwoCatalogPlaces()
    {
        var original = Name("Verlassenes Gefängnis der..." + Prison.De);
        using var capture = Capture(Center(original));
        var page = Read(Lines(original), Lines(original), capture, [Prison, OtherPrison]);
        Assert.Null(Assert.Single(page.Rows!).Id);
        Assert.Empty(page.Ids);
        Assert.NotEmpty(page.Unmatched);
    }

    [Fact]
    public void UnknownOriginalTextCanStillBeRecoveredByAUniqueContrastReference()
    {
        var original = Name("Unlesbarer synthetischer Kartenname");
        using var capture = Capture(Center(original));
        var page = Read(Lines(original), Lines(Name("Verlassenes Gefängnis der...Foreign label", 338, 30)), capture);
        Assert.Equal(Prison.Id, Assert.Single(page.Rows!).Id);
        Assert.Equal([Prison.Id], page.Ids);
        Assert.Empty(page.Unmatched);
    }

    [Fact]
    public void CatalogReferencesStayInsideTheValidatedMapAndCategory()
    {
        var row = Name("Verlassenes Gefängnis der...Map label");
        using var capture = Capture(Center(row));
        ExplorationPlace[] catalog = [.. Places,
            Prison with { Id = "synthetic-other-map", Map = "ishalgen" },
            Prison with { Id = "synthetic-other-category", Kind = "stronghold" }];
        var page = Read(Lines(row), Lines(row), capture, catalog);
        Assert.Equal([Prison.Id], page.Ids);
        Assert.Equal(Prison.Id, Assert.Single(page.Rows!).Id);

        var wrongMap = Lines(row).Select(line => line.Text == "Map: Altgard" ? line with { Text = "Map: Ishalgen" } : line).ToArray();
        var wrongKind = Lines(row).Select(line => line.Text == "Sealed Dungeon" ? line with { Text = "Stronghold" } : line).ToArray();
        Assert.Empty(Read(wrongMap, wrongMap, capture).Ids);
        Assert.Empty(Read(wrongKind, wrongKind, capture).Ids);
    }

    [Fact]
    public void SeparateMapLabelsStartingToTheRightOfTheNameColumnCannotBecomeUnmatchedListRows()
    {
        var original = Name("Verlassenes Gefängnis der...") with { Width = 280 };
        var contrast = Name("Verlassenes Gefängnis der...", 338, 30) with { Width = 280 };
        using var capture = Capture(Center(original));
        var page = Read(Lines(original, new("Wlu s Brücke", 345, 340, 139, 14)),
            Lines(contrast, new("Mu@s Brücke", 345, 340, 139, 14)), capture);
        Assert.Equal(Prison.Id, Assert.Single(page.Rows!).Id);
        Assert.Equal([Prison.Id], page.Ids);
        Assert.Empty(page.Unmatched);
    }

    [Fact]
    public void ARecoveredPrefixCannotBorrowTheNextRowsOriginalCheck()
    {
        var first = Name("Verlassenes Gefängnis der...Map label");
        var second = Name("Synthetic Chamber of...Another label", 416);
        using var capture = Capture(Center(second));
        var page = Read(Lines(first, second), Lines(first, second), capture);
        Assert.Equal(2, page.Rows!.Count);
        Assert.False(page.Rows!.Single(row => row.Id == Prison.Id).Complete);
        Assert.True(page.Rows!.Single(row => row.Id == Chamber.Id).Complete);
        Assert.Equal([Chamber.Id], page.Ids);
    }

    [Fact]
    public void OneCheckBetweenTwoRecoveredNamesCannotCompleteEitherRow()
    {
        var first = Name("Verlassenes Gefängnis der...Map label", 340, 30);
        var second = Name("Synthetic Chamber of...Another label", 360, 30);
        using var capture = Capture((Center(first) + Center(second)) / 2);
        var page = Read(Lines(first, second), Lines(first, second), capture);
        Assert.Equal(2, page.Rows!.Count);
        Assert.All(page.Rows!, row => Assert.False(row.Complete));
        Assert.Empty(page.Ids);
    }

    private static readonly ExplorationPlace[] Kibelisks =
    [new("synthetic-ridge", "altgard", "kibelisk", 1, "Synthetic Northern Ridge Beacon", "Synthetischer Nordgrat-Wegpunkt"),
        new("synthetic-lake", "altgard", "kibelisk", 2, "Synthetic Northern Lake Beacon", "Synthetischer Nordsee-Wegpunkt")];

    [Fact]
    public void KibeliskCandidatesUseTheSameBilingualCatalogReferenceIndex()
    {
        var names = new ExplorationNameIndex(Kibelisks);
        foreach (var text in new[] { "Synthetischer Nordgrat...Fremdes Label", "Synthetic Northern Ridge...Foreign label" })
        {
            var row = new KibeliskRow(1, text, false, true);
            Assert.Equal(["synthetic-ridge"], KibeliskList.Candidates(row, Kibelisks).Select(place => place.Id));
            Assert.Equal(KibeliskList.Candidates(row, names), KibeliskList.Candidates(row, Kibelisks));
        }
    }

    [Theory]
    [InlineData("Bindung abgeschlossen", true)]
    [InlineData("Bindung nicht abgeschlossen", false)]
    [InlineData("Bind Complete", true)]
    [InlineData("Binding Incomplete", false)]
    public void AParsedKibeliskWithAnAttachedMapLabelRetainsItsExplicitBindingStatus(string status, bool bound)
    {
        var page = KibeliskList.Read([new("1. Synthetic Northern Ridge...Foreign label", 40, 340, 438, 24),
            new(status, 40, 370, 300, 20)], 1200);
        var row = Assert.Single(page.Rows);
        Assert.Equal(bound, row.Bound);
        Assert.Equal(["synthetic-ridge"], KibeliskList.Candidates(row, Kibelisks).Select(place => place.Id));
        var session = new KibeliskSession(Kibelisks);
        session.Observe(page);
        session.Observe(page);
        Assert.Equal(["synthetic-ridge"], bound ? session.Result().Bound : session.Result().Unbound);
        Assert.Empty(bound ? session.Result().Unbound : session.Result().Bound);
        Assert.Empty(session.Result().Unknown);
    }

    [Theory]
    [InlineData("Synthetic Northern Ridge", false)]
    [InlineData("Synthetic Northern Ridge", true)]
    [InlineData("Synthelic Northern Ridge Beacon", false)]
    public void KibeliskIntegrationRetainsImplicitTruncationAndTheExistingOneLetterTolerance(string text, bool truncated)
    {
        Assert.Equal(["synthetic-ridge"], KibeliskList.Candidates(new(1, text, truncated, true), Kibelisks).Select(place => place.Id));
    }

    [Fact]
    public void ASecondCanonicalKibeliskNameBehindTheEllipsisIsAHardConflict()
    {
        var row = new KibeliskRow(1, "Synthetic Northern Ridge..." + Kibelisks[1].En, false, true);
        Assert.Empty(KibeliskList.Candidates(row, Kibelisks));
        var session = new KibeliskSession(Kibelisks);
        var page = new KibeliskPage([row], false);
        session.Observe(page);
        session.Observe(page);
        Assert.Single(session.Result().Unknown);
        Assert.Empty(session.Result().Bound);
        Assert.Empty(session.Result().Unbound);
        Assert.False(session.Complete);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void KibeliskAmbiguousNameGroupsRequireDistinctNumbersAndAnAgreedStatus(bool agreedStatus)
    {
        var session = new KibeliskSession(Kibelisks);
        var first = new KibeliskRow(1, "Synthetic Northern...Foreign label", false, true);
        var single = new KibeliskPage([first], false);
        session.Observe(single);
        session.Observe(single);
        Assert.Single(session.Result().Open);
        Assert.Empty(session.Result().Bound);
        Assert.Empty(session.Result().Unbound);

        var both = new KibeliskPage([first, first with { Number = 2, Bound = agreedStatus }], false);
        session.Observe(both);
        session.Observe(both);
        Assert.Empty(session.Result().Unknown);
        if (agreedStatus)
        {
            Assert.Equal(Kibelisks.Select(place => place.Id).Order(), session.Result().Bound.Order());
            Assert.Empty(session.Result().Open);
            Assert.True(session.Complete);
        }
        else
        {
            Assert.NotEmpty(session.Result().Open);
            Assert.Empty(session.Result().Bound);
            Assert.Empty(session.Result().Unbound);
            Assert.False(session.Complete);
        }
    }

    [Fact]
    public void DuplicateOcrRowsWithOneKibeliskNumberCannotSettleAGroupOfTwoPlaces()
    {
        var session = new KibeliskSession(Kibelisks);
        var row = new KibeliskRow(1, "Synthetic Northern...Foreign label", false, true);
        var page = new KibeliskPage([row, row], false);
        session.Observe(page);
        session.Observe(page);
        Assert.Single(session.Confirmed);
        Assert.Single(session.Result().Open);
        Assert.Empty(session.Result().Bound);
        Assert.Empty(session.Result().Unbound);
        Assert.False(session.Complete);
    }

    [Fact]
    public void EqualKibeliskNamesAtDifferentPlacesKeepBothIdsInTheirCandidateGroup()
    {
        var second = Kibelisks[0] with { Id = "synthetic-ridge-second-place" };
        ExplorationPlace[] places = [Kibelisks[0], second];
        var row = new KibeliskRow(1, "Synthetischer Nordgrat...Fremdes Label", false, true);
        Assert.Equal(places.Select(place => place.Id).Order(), KibeliskList.Candidates(row, places).Select(place => place.Id).Order());
        var session = new KibeliskSession(places);
        var page = new KibeliskPage([row, row with { Number = 2 }], false);
        session.Observe(page);
        session.Observe(page);
        Assert.Equal(places.Select(place => place.Id).Order(), session.Result().Bound.Order());
        Assert.Empty(session.Result().Open);
    }
}
