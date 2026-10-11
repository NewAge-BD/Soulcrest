using System.Drawing;
using System.Drawing.Imaging;
using Soulcrest.App.Services;
using Soulcrest.Core.PetWindow;
using Soulcrest.Ocr.PetWindow;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class PetScanSelectionTests
{
    [Fact]
    public void BatchSelectsOnlyRequestedEligibleRowsAndNotifiesOnce()
    {
        using var scan = new PetScanService(new SettingsService(), new ProgressService());
        var first = scan.Merge(Card("first", 5));
        var second = scan.Merge(Card("second", 6));
        var other = scan.Merge(Card("other", 7));
        scan.SetApply(first.Key, false);
        scan.SetApply(second.Key, false);
        scan.SetApply(other.Key, false);
        var rendered = scan.Entries;
        var notifications = 0;
        scan.Changed += () => notifications++;

        scan.SetApply(new[] { rendered[0].Key, rendered[1].Key, rendered[0].Key, "missing" }, true);

        Assert.Equal(1, notifications);
        Assert.All(scan.Entries.Take(2), entry => Assert.True(entry.Apply));
        Assert.False(Assert.Single(scan.Entries, entry => entry.Key == other.Key).Apply);
        Assert.All(rendered, entry => Assert.False(entry.Apply)); // UI snapshots remain detached.
    }

    [Fact]
    public void BatchSelectionRejectsUnknownUnreadableAndConflictingRows()
    {
        using var scan = new PetScanService(new SettingsService(), new ProgressService());
        var valid = scan.Merge(Card("valid", 5));
        var unreadable = scan.Merge(Card("unreadable", null));
        var conflict = scan.Merge(Card("conflict", 5));
        scan.Merge(Card("conflict", 6, evidence: "second image"));
        var unknown = scan.Merge(Card(null, 7, ulong.MaxValue));
        foreach (var entry in scan.Entries) scan.SetApply(entry.Key, false);
        var notifications = 0;
        scan.Changed += () => notifications++;

        scan.SetApply(scan.Entries.Select(entry => entry.Key).ToArray(), true);

        Assert.Equal(1, notifications);
        Assert.True(Assert.Single(scan.Entries, entry => entry.Key == valid.Key).Apply);
        foreach (var key in new[] { unreadable.Key, conflict.Key, unknown.Key })
            Assert.False(Assert.Single(scan.Entries, entry => entry.Key == key).Apply);
        Assert.Null(conflict.Reading);
    }

    [Fact]
    public void BatchCanAlwaysDeselectRowsEvenAfterTheirReadingBecomesInvalid()
    {
        using var scan = new PetScanService(new SettingsService(), new ProgressService());
        var conflict = scan.Merge(Card("conflict", 5));
        scan.Merge(Card("conflict", 6, evidence: "second image"));
        var unknown = scan.Merge(Card(null, 7, ulong.MaxValue));
        scan.SetApply(unknown.Key, true);
        var notifications = 0;
        scan.Changed += () => notifications++;

        scan.SetApply(new[] { conflict.Key, unknown.Key }, false);

        Assert.Equal(1, notifications);
        Assert.All(scan.Entries, entry => Assert.False(entry.Apply));
    }

    [Fact]
    public void EmptyMissingUnchangedAndIneligibleBatchesDoNotNotify()
    {
        using var scan = new PetScanService(new SettingsService(), new ProgressService());
        var valid = scan.Merge(Card("valid", 5));
        var unknown = scan.Merge(Card(null, 7, ulong.MaxValue));
        var notifications = 0;
        scan.Changed += () => notifications++;

        scan.SetApply(Array.Empty<string>(), true);
        scan.SetApply(new[] { "missing" }, false);
        scan.SetApply(new[] { valid.Key, valid.Key }, true);
        scan.SetApply(new[] { unknown.Key }, true);
        scan.SetApply(new[] { unknown.Key }, false);

        Assert.Equal(0, notifications);
    }

    [Fact]
    public void BatchChecksCurrentEvidenceInsteadOfTheRenderedSnapshot()
    {
        using var scan = new PetScanService(new SettingsService(), new ProgressService());
        var entry = scan.Merge(Card("pet", 5));
        scan.SetApply(entry.Key, false);
        var rendered = Assert.Single(scan.Entries);
        scan.Merge(Card("pet", 6, evidence: "new conflicting image"));
        var notifications = 0;
        scan.Changed += () => notifications++;

        scan.SetApply(new[] { rendered.Key }, true);

        Assert.NotNull(rendered.Reading);
        Assert.Null(Assert.Single(scan.Entries).Reading);
        Assert.False(Assert.Single(scan.Entries).Apply);
        Assert.Equal(0, notifications);
    }

    [Fact]
    public void ASelectionFromBeforeClearCannotTargetALaterCard()
    {
        using var scan = new PetScanService(new SettingsService(), new ProgressService());
        var previous = scan.Merge(Card("pet", 5));
        scan.Clear();
        var current = scan.Merge(Card("pet", 5));
        var notifications = 0;
        scan.Changed += () => notifications++;

        scan.SetApply(new[] { previous.Key }, false);

        Assert.NotEqual(previous.Key, current.Key);
        Assert.True(Assert.Single(scan.Entries).Apply);
        Assert.Equal(0, notifications);
        scan.Assign(previous, "unrelated");
        scan.SetApply(previous.Key, false);
        Assert.Equal("pet", Assert.Single(scan.Entries).PetId);
        Assert.True(Assert.Single(scan.Entries).Apply);
    }

    [Fact]
    public void BatchSelectionFollowsAPanelMergeAndDeduplicatesItsAliases()
    {
        using var picture = new Bitmap(40, 50);
        using var stream = new MemoryStream();
        picture.Save(stream, ImageFormat.Png);
        var progress = new ProgressService();
        var pet = progress.LearnPet($"Selection Merge {Guid.NewGuid():N}", stream.ToArray());
        using var scan = new PetScanService(new SettingsService(), progress);
        var card = Card(pet.Id, 5) with { PortraitPng = stream.ToArray() };
        var survivor = scan.Merge(card);
        var selected = card with { Selected = true, PortraitHash = ulong.MaxValue,
            Match = new PortraitMatch(null, 0, null, 0) };
        var retired = scan.Merge(selected);
        List<(PetCardScan Card, ScanEntry Entry)> frame = [(selected, retired)];
        scan.LearnFromPanel(new PetWindowScan([selected],
            new PetPanelScan(pet.En, 1, "", new PetCardProgress(1, 5, 25, false)), null, null), frame, false);
        Assert.Equal(survivor.Key, Assert.Single(scan.Entries).Key);
        var notifications = 0;
        scan.Changed += () => notifications++;

        scan.SetApply(new[] { retired.Key }, false);
        Assert.False(Assert.Single(scan.Entries).Apply);
        Assert.Equal(1, notifications);
        scan.SetApply(new[] { retired.Key, survivor.Key, retired.Key }, true);

        Assert.True(Assert.Single(scan.Entries).Apply);
        Assert.Equal(2, notifications);
        scan.SetApply(retired.Key, false);
        scan.Assign(retired, pet.Id);
        Assert.Equal(pet.Id, Assert.Single(scan.Entries).PetId);
        Assert.False(Assert.Single(scan.Entries).Apply);
    }

    [Fact]
    public void SelectedResultsCanStillBeAppliedWhileTheLiveScanIsRunning()
    {
        var progress = new ProgressService();
        using var scan = new PetScanService(new SettingsService(), progress);
        var included = scan.Merge(Card($"selection-apply-{Guid.NewGuid():N}", 5));
        var excluded = scan.Merge(Card($"selection-exclude-{Guid.NewGuid():N}", 9));
        scan.SetApply(new[] { included.Key, excluded.Key }, false);
        scan.SetApply(new[] { included.Key }, true);
        // Set the state without starting capture or interacting with any running game.
        typeof(PetScanService).GetProperty(nameof(PetScanService.Running))!.SetValue(scan, true);

        var applied = scan.ApplySelected();

        Assert.Equal(1, applied);
        Assert.True(scan.Running);
        Assert.Equal(progress.Thresholds.Total(1, 5), progress.Souls(included.PetId!));
        Assert.Equal(0, progress.Souls(excluded.PetId!));
    }

    [Fact]
    public void RepeatedPanelReadsAndANewRunKeepTheUsersSelectionUntilTheyChangeIt()
    {
        var progress = new ProgressService();
        var png = Portrait();
        var pet = progress.LearnPet($"Selection Panel {Guid.NewGuid():N}", png);
        using var scan = new PetScanService(new SettingsService(), progress);
        var card = Card(pet.Id, 5) with { PortraitPng = png, Selected = true };
        var entry = scan.Merge(card);
        List<(PetCardScan Card, ScanEntry Entry)> frame = [(card, entry)];
        var panel = new PetWindowScan([card],
            new PetPanelScan(pet.En, 1, "", new PetCardProgress(1, 5, 25, false)), null, null);
        scan.LearnFromPanel(panel, frame, false);

        scan.SetApply(new[] { entry.Key }, false);
        scan.LearnFromPanel(panel, frame, false);
        scan.BeginNewRun();
        scan.LearnFromPanel(panel, frame, false);
        Assert.False(Assert.Single(scan.Entries).Apply);

        scan.SetApply(new[] { entry.Key }, true);
        scan.LearnFromPanel(panel, frame, false);
        Assert.True(Assert.Single(scan.Entries).Apply);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void APanelMergePreservesTheLatestExplicitDecisionFromEitherEntry(bool retiredIsNewest)
    {
        var progress = new ProgressService();
        var png = Portrait();
        var pet = progress.LearnPet($"Selection Order {Guid.NewGuid():N}", png);
        using var scan = new PetScanService(new SettingsService(), progress);
        var card = Card(pet.Id, 5) with { PortraitPng = png };
        var survivor = scan.Merge(card);
        var selected = card with { Selected = true, PortraitHash = ulong.MaxValue,
            Match = new PortraitMatch(null, 0, null, 0) };
        var retired = scan.Merge(selected);
        if (retiredIsNewest)
        {
            scan.SetApply(survivor.Key, false);
            scan.SetApply(retired.Key, true);
        }
        else
        {
            scan.SetApply(retired.Key, true);
            scan.SetApply(survivor.Key, false);
        }
        List<(PetCardScan Card, ScanEntry Entry)> frame = [(selected, retired)];

        scan.LearnFromPanel(new PetWindowScan([selected],
            new PetPanelScan(pet.En, 1, "", new PetCardProgress(1, 5, 25, false)), null, null), frame, false);

        Assert.Equal(survivor.Key, Assert.Single(scan.Entries).Key);
        Assert.Equal(retiredIsNewest, Assert.Single(scan.Entries).Apply);
    }

    [Fact]
    public void DeselectingAnAlreadyUncheckedUnknownRowSurvivesAssignmentWithoutANotification()
    {
        var progress = new ProgressService();
        var png = Portrait();
        var pet = progress.LearnPet($"Selection Assign {Guid.NewGuid():N}", png);
        using var scan = new PetScanService(new SettingsService(), progress);
        var entry = scan.Merge(Card(null, 5) with { PortraitPng = png });
        Assert.False(entry.Apply);
        var notifications = 0;
        scan.Changed += () => notifications++;

        scan.SetApply(new[] { entry.Key }, false);
        Assert.Equal(0, notifications);
        scan.Assign(Assert.Single(scan.Entries), pet.Id);

        Assert.Equal(pet.Id, Assert.Single(scan.Entries).PetId);
        Assert.False(Assert.Single(scan.Entries).Apply);
    }

    [Fact]
    public void ASelectionOverrideSurvivesAnEntrySnapshotAndMerge()
    {
        using var scan = new PetScanService(new SettingsService(), new ProgressService());
        var entry = scan.Merge(Card("pet", 5));
        scan.SetApply(new[] { entry.Key }, false);
        var snapshot = Assert.Single(scan.Entries);
        var survivor = new ScanEntry { Key = "survivor", PetId = "pet", PortraitPng = [], ThumbnailDataUrl = "" };

        survivor.MergeValues(snapshot);

        Assert.False(survivor.Apply);
        Assert.False(survivor.DefaultApply);
    }

    private static byte[] Portrait()
    {
        using var picture = new Bitmap(40, 50);
        using var stream = new MemoryStream();
        picture.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static PetCardScan Card(string? petId, int? souls, ulong hash = 0, string evidence = "image") => new(
        0, new Rectangle(10, 10, 150, 180),
        souls is { } value ? new PetCardProgress(1, value, 25, false) : null,
        souls is { } text ? $"{text}/25" : "?", new PortraitMatch(petId, petId is null ? 0 : 30, null, 0),
        false, [], hash, Evidence: evidence);
}
