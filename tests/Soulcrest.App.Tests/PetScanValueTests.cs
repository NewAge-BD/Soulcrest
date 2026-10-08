using System.Drawing;
using System.Drawing.Imaging;
using Soulcrest.App.Overlay;
using Soulcrest.App.Services;
using Soulcrest.Core.PetWindow;
using Soulcrest.Ocr.PetWindow;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class PetScanValueTests
{
    [Fact]
    public void KuruWorkerLaterMatchPromotesTheUnknownRowAndPreservesItsManualValue()
    {
        using var scan = new PetScanService(new SettingsService(), new ProgressService());
        var unknown = Card(5) with { PortraitHash = 0x4d65c3f2ee6c1800,
            Match = new PortraitMatch(null, 0, null, 0) };
        var first = scan.Merge(unknown);
        Assert.True(scan.CorrectValue(first.Key, "6/25"));
        var known = unknown with { Column = 2, Bounds = new Rectangle(400, 180, 150, 180),
            PortraitHash = 0x4d65c3f6ee6c1800, Match = new PortraitMatch("kuru-worker", 199, null, 0) };
        var later = scan.Merge(known);
        Assert.Same(first, later);
        Assert.Equal(first.Key, Assert.Single(scan.Entries).Key);
        Assert.Equal("kuru-worker", later.PetId);
        Assert.Equal((1, 6), later.Reading);
        Assert.True(scan.CorrectValue(first.Key, "7/25"));
        Assert.Equal((1, 7), later.Reading);
    }

    [Fact]
    public void LaterMatchNeverPromotesAnUnknownCardAlreadyVisibleElsewhere()
    {
        using var scan = new PetScanService(new SettingsService(), new ProgressService());
        var unknown = Card(5) with { Match = new PortraitMatch(null, 0, null, 0), PortraitHash = 16 };
        var first = scan.Merge(unknown);
        var known = unknown with { Match = new PortraitMatch("kuru-worker", 199, null, 0), PortraitHash = 17 };
        Assert.NotSame(first, scan.Merge(known, [first]));
        Assert.Equal(2, scan.Entries.Count);
        Assert.Null(first.PetId);
    }

    [Fact]
    public void LaterMatchDoesNotChooseBetweenTwoNearIdenticalUnknownPets()
    {
        using var scan = new PetScanService(new SettingsService(), new ProgressService());
        var unknown = Card(5) with { Match = new PortraitMatch(null, 0, null, 0), PortraitHash = 16 };
        var first = scan.Merge(unknown);
        var second = scan.Merge(unknown with { PortraitHash = 17 }, [first]);
        var known = unknown with { Match = new PortraitMatch("kuru-worker", 199, null, 0), PortraitHash = 18 };
        var third = scan.Merge(known);
        Assert.NotSame(first, third);
        Assert.NotSame(second, third);
        Assert.Equal(3, scan.Entries.Count);
    }

    [Theory]
    [InlineData(4, 1, 16UL)]
    [InlineData(4, 0, 16UL)]
    [InlineData(5, 1, 31UL)]
    public void DifferentValuesOwnershipOrPortraitsCannotPromoteAnUnknownPet(int souls, int level, ulong hash)
    {
        using var scan = new PetScanService(new SettingsService(), new ProgressService());
        var unknown = Card(5) with { Match = new PortraitMatch(null, 0, null, 0), PortraitHash = 16 };
        var first = scan.Merge(unknown);
        var known = unknown with { PortraitHash = hash, Match = new PortraitMatch("kuru-worker", 199, null, 0),
            Progress = new PetCardProgress(level, souls, level == 0 ? 5 : 25, false) };
        Assert.NotSame(first, scan.Merge(known));
        Assert.Null(first.PetId);
    }

    [Fact]
    public void ANewRunKeepsPagesNotScannedAgainAndRefreshesSeenOnes()
    {
        // Review 2026-10-08: restarting the scan reset every entry, so "Übernehmen" skipped all pets
        // of pages not scrolled through again.
        using var scan = new PetScanService(new SettingsService(), new ProgressService());
        var other = Card(9) with { Match = new PortraitMatch("other", 30, null, 0), PortraitHash = 99 };
        var notAgain = scan.Merge(other);
        var seenAgain = scan.Merge(Card(5));

        scan.BeginNewRun();
        scan.Merge(Card(6, "fresh image"));

        Assert.Equal((1, 9), notAgain.Reading);  // kept: its page was not scanned again
        Assert.Equal((1, 6), seenAgain.Reading); // fresh evidence replaced the earlier run's value
    }

    private static ScanEntry Entry() => new()
    {
        Key = "test", PetId = "test", ThumbnailDataUrl = "", PortraitPng = [],
    };

    private static PetCardScan Card(int? souls, string evidence = "image", int level = 1) => new(
        0, new Rectangle(10, 10, 150, 180),
        souls is { } n ? new PetCardProgress(level, n, level == 1 ? 25 : 75, false) : null,
        souls is { } value ? $"{value}/25" : "?", new PortraitMatch("test", 30, null, 0), false, [], 0,
        Evidence: evidence);

    [Fact]
    public void RepeatedImageIsNotIndependentEvidence()
    {
        var entry = Entry();
        for (var i = 0; i < 100; i++) entry.Observe(Card(3));
        Assert.Equal(2, entry.Votes[(1, 3)]);
        entry.Observe(Card(3, "different image"));
        Assert.Equal(4, entry.Votes[(1, 3)]);
    }

    [Fact]
    public void ConflictingNumbersBlockReadinessInsteadOfWinningByRepetition()
    {
        var entry = Entry();
        entry.Observe(Card(5));
        for (var i = 0; i < 100; i++) entry.Observe(Card(3, $"wrong-{i}"));
        Assert.Null(entry.Reading);
        using var scan = new PetScanService(new SettingsService(), new ProgressService());
        var page = scan.DescribePage([(Card(3), entry)], stable: true);
        Assert.False(page.Ready);
        Assert.Equal(0, page.Read);
        var mark = Assert.Single(page.Marks);
        Assert.True(mark.Conflict);
        Assert.Equal("3/25", mark.Value);
        Assert.Equal("5/25", mark.CurrentValue);
    }

    [Fact]
    public void PanelConfirmationRemainsAuthoritativeAndSurvivesSnapshots()
    {
        var entry = Entry();
        entry.Observe(Card(3));
        entry.ConfirmValue(new PetCardProgress(1, 5, 25, false));
        for (var i = 0; i < 100; i++) entry.Observe(Card(3, $"wrong-{i}"));
        Assert.Equal((1, 5), entry.Reading);
        Assert.Equal((1, 5), entry.Snapshot().Reading);
        Assert.False(entry.HasValueConflict);
        var mark = PetScanService.DescribeCard(Card(3), entry);
        Assert.Equal("Panel", mark.Source);
        Assert.Equal("5/25", mark.Value);
        Assert.Equal("3/25", mark.CurrentValue);
        Assert.False(mark.ValueMissing);
        Assert.False(mark.Conflict);
    }

    [Fact]
    public void StoredNumberIsLabelledAndDoesNotCompleteAnUnreadableCard()
    {
        var entry = Entry();
        entry.Observe(Card(7, level: 2));
        using var scan = new PetScanService(new SettingsService(), new ProgressService());
        var page = scan.DescribePage([(Card(null), entry)], stable: true);
        Assert.False(page.Ready);
        var mark = Assert.Single(page.Marks);
        Assert.True(mark.ValueMissing);
        Assert.Equal("Bisher", mark.Source);
        Assert.Equal("7/75", mark.Value);
    }

    [Fact]
    public void SuccessfulAndMaxCardsAlsoGetLabels()
    {
        var entry = Entry();
        entry.Observe(Card(5));
        var max = Entry();
        max.ConfirmValue(new PetCardProgress(3, 0, 0, true));
        using var scan = new PetScanService(new SettingsService(), new ProgressService());
        var page = scan.DescribePage([(Card(5), entry), (Card(null), max)], stable: true);
        Assert.True(page.Ready);
        Assert.Equal(new[] { "5/25", "MAX" }, page.Marks.Select(m => m.Value));
    }

    [Fact]
    public void ResetRequiresFreshEvidenceAndPanelConfirmation()
    {
        var entry = Entry();
        entry.Observe(Card(3));
        entry.ConfirmValue(new PetCardProgress(1, 5, 25, false));
        entry.ResetValues();
        Assert.Null(entry.Reading);
        Assert.Null(entry.PanelReading);
        Assert.Empty(entry.Votes);
        entry.Observe(Card(3));
        Assert.Equal((1, 3), entry.Reading);
    }

    [Fact]
    public void ManualCorrectionOutranksOcrAndPanelAndSurvivesResetSnapshotAndMerge()
    {
        var entry = Entry();
        entry.CorrectValue(new PetCardProgress(1, 5, 25, false));
        entry.Observe(Card(3));
        entry.ConfirmValue(new PetCardProgress(3, 0, 0, true));
        var mark = PetScanService.DescribeCard(Card(3), entry);
        Assert.Equal("5/25", mark.Value);
        Assert.Equal("Manuell", mark.Source);
        Assert.Equal("test", mark.EntryKey);
        Assert.False(mark.Conflict);
        Assert.False(mark.ValueMissing);
        entry.ResetValues();
        Assert.Equal((1, 5), entry.Snapshot().Reading);
        var survivor = Entry();
        survivor.MergeValues(entry);
        survivor.ConfirmValue(new PetCardProgress(2, 7, 75, false));
        Assert.Equal((1, 5), survivor.Reading);
        survivor.CorrectValue(new PetCardProgress(1, 4, 25, false));
        entry.CorrectValue(new PetCardProgress(1, 6, 25, false));
        survivor.MergeValues(entry.Snapshot());
        Assert.Equal((1, 6), survivor.Reading); // newest user correction survives merging two edited entries
    }

    [Theory]
    [InlineData("5/25", true)]
    [InlineData(" 7 / 75 ", true)]
    [InlineData("max", true)]
    [InlineData("4/5", true)]
    [InlineData("25/25", false)]
    [InlineData("75/75", false)]
    [InlineData("5/5", false)]
    [InlineData("331/75", false)]
    [InlineData("-1/25", false)]
    [InlineData("", false)]
    [InlineData("1/7", false)]
    public void CorrectionRequiresACompleteValidValue(string text, bool valid) =>
        Assert.Equal(valid, PetScanService.ParseCorrection(text) is not null);

    [Fact]
    public void OnlyControlOnTheDisplayedValueOfAStableCardIsEditable()
    {
        var card = Card(5) with { ProgressBounds = new Rectangle(90, 170, 60, 12) };
        var mark = PetScanService.DescribeCard(card, Entry());
        var tag = ScanMarkerOverlayForm.ValueTag(mark);
        Assert.Equal(167, tag.Bottom);
        Assert.False(tag.IntersectsWith(card.ProgressBounds.Value));
        var point = new Point(tag.Left + 10, tag.Top + 5);
        Assert.Same(mark, ScanMarkerOverlayForm.EditableAt([mark], point, true, true));
        Assert.Null(ScanMarkerOverlayForm.EditableAt([mark], point, false, true));
        Assert.Null(ScanMarkerOverlayForm.EditableAt([mark], point, true, false));
        Assert.Null(ScanMarkerOverlayForm.EditableAt([mark], new Point(30, 20), true, true));
        Assert.Null(ScanMarkerOverlayForm.EditableAt([mark], new Point(100, 175), true, true));
    }

    [Fact]
    public void NewNamesNeedConsecutiveConfirmationsAndTabHeadingsAreNeverLearned()
    {
        using var picture = new Bitmap(40, 50);
        using var stream = new MemoryStream();
        picture.Save(stream, ImageFormat.Png);
        var card = Card(5) with { Match = new PortraitMatch(null, 0, null, 0), Selected = true, PortraitPng = stream.ToArray() };
        var progress = new ProgressService();
        using var scan = new PetScanService(new SettingsService(), progress);
        var entry = scan.Merge(card);
        List<(PetCardScan Card, ScanEntry Entry)> frame = [(card, entry)];
        var name = $"Retest {Guid.NewGuid():N}";
        var panel = new PetPanelScan(name, 1, name, new PetCardProgress(1, 5, 25, false));
        var read = new PetWindowScan([card], panel, null, null);
        scan.LearnFromPanel(read, frame, singlePicture: false);
        Assert.Null(entry.PetId);
        Assert.Null(progress.FindByName(name));
        // An intervening invalid panel resets the confirmation.
        scan.LearnFromPanel(read with { Panel = panel with { Name = "All Owned Effe..." } }, frame, false);
        scan.LearnFromPanel(read, frame, false);
        Assert.Null(entry.PetId);
        scan.LearnFromPanel(read, frame, false);
        Assert.Equal(progress.FindByName(name)!.Id, entry.PetId);
        Assert.Equal((1, 5), entry.PanelReading);
    }

    [Fact]
    public void AnEditorTargetCannotBeReusedAfterClear()
    {
        using var scan = new PetScanService(new SettingsService(), new ProgressService());
        var card = Card(5);
        var before = scan.Merge(card);
        Assert.True(scan.CorrectValue(before.Key, "7/75"));
        scan.Clear();
        var after = scan.Merge(card);
        Assert.NotEqual(before.Key, after.Key);
        Assert.False(scan.CorrectValue(before.Key, "3/25"));
        Assert.Equal((1, 5), after.Reading);
    }

    [Fact]
    public void AnOpenEditorFollowsItsEntryWhenPanelRecognitionMergesIt()
    {
        using var picture = new Bitmap(40, 50);
        using var stream = new MemoryStream();
        picture.Save(stream, ImageFormat.Png);
        var progress = new ProgressService();
        var pet = progress.LearnPet($"Merge Retest {Guid.NewGuid():N}", stream.ToArray());
        using var scan = new PetScanService(new SettingsService(), progress);
        var card = Card(5) with { PortraitPng = stream.ToArray(), Match = new PortraitMatch(pet.Id, 30, null, 0) };
        var survivor = scan.Merge(card);
        var selected = card with { Selected = true, PortraitHash = ulong.MaxValue, Match = new PortraitMatch(null, 0, null, 0) };
        var original = scan.Merge(selected);
        Assert.True(scan.CorrectValue(original.Key, "6/25"));
        List<(PetCardScan Card, ScanEntry Entry)> frame = [(selected, original)];
        scan.LearnFromPanel(new PetWindowScan([selected], new PetPanelScan(pet.En, 1, "", new PetCardProgress(1, 5, 25, false)), null, null), frame, false);
        Assert.Equal(survivor.Key, Assert.Single(scan.Entries).Key);
        Assert.Equal((1, 6), survivor.Reading);
        Assert.True(scan.CorrectValue(original.Key, "7/25"));
        Assert.Equal((1, 7), survivor.Reading);
    }

    [Fact]
    public void SuccessfulLabelLeavesTheOriginalCounterUncovered()
    {
        ScanMark[] marks =
        [
            new(new Rectangle(10, 10, 150, 180), false, "7/75", "Scan", false, false, "7/75"),
            new(new Rectangle(180, 10, 150, 180), true, "6/75", "Bisher", false, false, "?"),
            new(new Rectangle(350, 10, 150, 180), true, "3/25", "Scan", true, false, "5/25"),
            new(new Rectangle(10, 220, 150, 180), false, "4/75", "Scan", false, true, "4/75"),
            new(new Rectangle(180, 220, 150, 180), false, "7/75", "Panel", false, false, "MAX"),
            new(new Rectangle(350, 220, 150, 180), false, "MAX", "Scan", false, false, "MAX"),
        ];
        using var image = ScanMarkerOverlayForm.RenderMarks(marks, new Rectangle(0, 0, 510, 410));
        Assert.True(image.GetPixel(20, 15).A > 0);
        var tag = ScanMarkerOverlayForm.ValueTag(marks[0]);
        Assert.True(image.GetPixel(tag.Left + 3, tag.Top + 3).A > 0);
        for (var y = 165; y < 185; y++)
            for (var x = 20; x < 150; x++)
                Assert.Equal(0, image.GetPixel(x, y).A);
        // Optional offline visual review, without starting an overlay or touching the game.
        if (Environment.GetEnvironmentVariable("SOULCREST_SCANMARKERS_PNG") is { Length: > 0 } file)
            image.Save(file, ImageFormat.Png);
    }
}
