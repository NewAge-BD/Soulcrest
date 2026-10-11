using Soulcrest.App.Services;
using Soulcrest.Core.Pets;
using Soulcrest.Core.PetWindow;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class PetScanOverviewTests
{
    private static readonly PetCatalog Catalog = new(
    [
        new PetDefinition { Id = "fixture-sprout", En = "Synthetic Amber Sprout", De = "Synthetischer Bernsteinspross", Genus = "fixture" },
        new PetDefinition { Id = "fixture-fox", En = "Synthetic Frost Fox", De = "Synthetischer Frostfuchs", Genus = "fixture" },
        new PetDefinition { Id = "fixture-bloom", En = "Synthetic Moon Bloom", De = "Synthetische Mondblüte", Genus = "fixture" },
        new PetDefinition { Id = "fixture-ember", En = "Synthetic Ember", Genus = "fixture" },
    ]);

    private static ScanEntry Entry(string key, string? pet = "fixture-sprout", ScanConfidence confidence = ScanConfidence.Recognised,
        (int Level, int InLevel)? value = null, bool apply = true, string? suggested = null)
    {
        var entry = new ScanEntry { Key = key, PetId = pet, Confidence = confidence, Apply = apply,
            SuggestedName = suggested, ThumbnailDataUrl = "", PortraitPng = [], Seen = 2 };
        if (value is { } reading) entry.Votes.Add(reading, 2);
        return entry;
    }

    private static ScanEntry[] MixedEntries()
    {
        var conflict = Entry("conflict", "fixture-ember", value: (1, 4));
        conflict.Votes.Add((1, 5), 40); // repetition never makes a contradictory value safe
        return
        [
            Entry("ready", value: (1, 4)),
            Entry("confirmed", "fixture-fox", ScanConfidence.Confirmed, (3, 0), apply: false),
            Entry("uncertain", "fixture-bloom", ScanConfidence.Uncertain, (2, 8)),
            Entry("missing", "fixture-fox", ScanConfidence.Confirmed),
            conflict,
            Entry("unknown", null, ScanConfidence.Unknown, (0, 2), suggested: "Synthetic Amber Sprite"),
            Entry("unknown-empty", null, ScanConfidence.Unknown),
        ];
    }

    [Fact]
    public void EmptyScanHasNoReadyReviewSelectedOrUnlockedPets()
    {
        var snapshot = PetScanOverview.Build([], Catalog, "en");
        Assert.Equal((0, 0, 0, 0, 0), (snapshot.Total, snapshot.Ready, snapshot.Review, snapshot.Selected, snapshot.Unlocked));
        Assert.Empty(snapshot.Rows);
    }

    [Fact]
    public void SummarySeparatesReadyReviewAndEligibleSelectionWithoutChangingExistingApplyPolicy()
    {
        var snapshot = PetScanOverview.Build(MixedEntries(), Catalog, "en");
        Assert.Equal((7, 2, 5, 2, 3), (snapshot.Total, snapshot.Ready, snapshot.Review, snapshot.Selected, snapshot.Unlocked));
        var uncertain = snapshot.Rows.Single(row => row.Entry.Key == "uncertain");
        Assert.True(uncertain.Eligible);
        Assert.True(uncertain.NeedsReview);
        Assert.Equal(PetScanEntryStatus.UncertainPet, uncertain.Status);
        Assert.All(snapshot.Rows.Where(row => row.Entry.Key is "missing" or "conflict" or "unknown" or "unknown-empty"),
            row => Assert.False(row.Eligible));
        Assert.All(snapshot.Rows.Take(snapshot.Review), row => Assert.True(row.NeedsReview));
        Assert.All(snapshot.Rows.Skip(snapshot.Review), row => Assert.False(row.NeedsReview));
    }

    [Theory]
    [InlineData("all", 7)]
    [InlineData("review", 5)]
    [InlineData("ready", 2)]
    [InlineData("selected", 2)]
    [InlineData("invalid", 7)]
    public void FilteringNeverChangesGlobalCountsOrTheUnderlyingSelection(string filter, int count)
    {
        var entries = MixedEntries();
        var original = entries.Select(entry => (entry.Key, entry.Apply, entry.PetId, entry.Reading, Votes: entry.Votes.ToArray())).ToArray();
        var baseline = PetScanOverview.Build(entries, Catalog, "en");
        var filtered = PetScanOverview.Build(entries, Catalog, "en", status: filter);
        Assert.Equal(count, filtered.Rows.Count);
        Assert.Equal((baseline.Total, baseline.Ready, baseline.Review, baseline.Selected, baseline.Unlocked),
            (filtered.Total, filtered.Ready, filtered.Review, filtered.Selected, filtered.Unlocked));
        for (var i = 0; i < entries.Length; i++)
        {
            Assert.Equal((original[i].Key, original[i].Apply, original[i].PetId, original[i].Reading),
                (entries[i].Key, entries[i].Apply, entries[i].PetId, entries[i].Reading));
            Assert.Equal(original[i].Votes, entries[i].Votes.ToArray());
        }
        if (filter == "selected")
            Assert.Equal(new[] { "ready", "uncertain" }, filtered.Rows.Select(row => row.Entry.Key).Order());
        if (filter == "review") Assert.All(filtered.Rows, row => Assert.True(row.NeedsReview));
        if (filter == "ready") Assert.All(filtered.Rows, row => Assert.False(row.NeedsReview));
    }

    [Fact]
    public void HiddenSelectedPetsRemainInTheApplyCountWhenSearchShowsOnlyOneRowOrNoRows()
    {
        var entries = MixedEntries();
        var one = PetScanOverview.Build(entries, Catalog, "en", search: "MOON bloom", status: "selected");
        Assert.Equal("uncertain", Assert.Single(one.Rows).Entry.Key);
        Assert.Equal(2, one.Selected);
        var none = PetScanOverview.Build(entries, Catalog, "en", search: "unmatched synthetic query", status: "selected");
        Assert.Empty(none.Rows);
        Assert.Equal(2, none.Selected);
        Assert.True(entries.Single(entry => entry.Key == "ready").Apply);
    }

    [Theory]
    [InlineData("BERNSTEINSPROSS", "ready")]
    [InlineData(" amber   sprout ", "ready")]
    [InlineData("Amber Bernsteinspross", "ready")]
    [InlineData("fixture-bloom", "uncertain")]
    [InlineData("amber sprite", "unknown")]
    public void SearchMatchesAllTermsAcrossLocalizedNamesSuggestionsAndPetIds(string search, string key)
    {
        var snapshot = PetScanOverview.Build(MixedEntries(), Catalog, "en", search: search);
        Assert.Equal(key, Assert.Single(snapshot.Rows).Entry.Key);
    }

    [Fact]
    public void DisplayUsesRequestedLanguageWithEnglishFallbackAndLeavesUnnamedPetsToTheUi()
    {
        var rows = PetScanOverview.Build(MixedEntries(), Catalog, "de").Rows;
        Assert.Equal("Synthetischer Bernsteinspross", rows.Single(row => row.Entry.Key == "ready").Name);
        Assert.Equal("Synthetic Ember", rows.Single(row => row.Entry.Key == "conflict").Name);
        Assert.Equal("Synthetic Amber Sprite", rows.Single(row => row.Entry.Key == "unknown").Name);
        Assert.Equal("", rows.Single(row => row.Entry.Key == "unknown-empty").Name);
    }

    [Fact]
    public void ReviewReasonPrioritizesConflictThenMissingIdentityThenMissingValueThenUncertainty()
    {
        var both = Entry("both", null, ScanConfidence.Unknown, (1, 2));
        both.Votes.Add((1, 3), 2);
        var entries = new[] { both, Entry("no-identity", null, ScanConfidence.Unknown),
            Entry("no-value", confidence: ScanConfidence.Uncertain), Entry("uncertain", confidence: ScanConfidence.Uncertain, value: (1, 2)),
            Entry("ready", confidence: ScanConfidence.Confirmed, value: (1, 2)) };
        var rows = PetScanOverview.Build(entries, Catalog, "en").Rows.ToDictionary(row => row.Entry.Key);
        Assert.Equal(PetScanEntryStatus.ConflictingValue, rows["both"].Status);
        Assert.Equal(PetScanEntryStatus.UnknownPet, rows["no-identity"].Status);
        Assert.Equal(PetScanEntryStatus.UnreadableValue, rows["no-value"].Status);
        Assert.Equal(PetScanEntryStatus.UncertainPet, rows["uncertain"].Status);
        Assert.Equal(PetScanEntryStatus.Ready, rows["ready"].Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PanelOrManualConfirmationResolvesConflictsWithoutLosingTheSelectedPet(bool manual)
    {
        var entry = Entry("conflict", value: (2, 8));
        entry.Votes.Add((2, 9), 20);
        var before = PetScanOverview.Build([entry], Catalog, "en");
        Assert.Equal(1, before.Review);
        Assert.Equal(0, before.Selected);
        var value = new PetCardProgress(2, 8, 75, false);
        if (manual) entry.CorrectValue(value);
        else entry.ConfirmValue(value);
        var after = PetScanOverview.Build([entry], Catalog, "en");
        Assert.Equal(PetScanEntryStatus.Ready, Assert.Single(after.Rows).Status);
        Assert.Equal(1, after.Selected);
        Assert.True(entry.Apply);
    }

    [Fact]
    public void LockedZeroAndMaxValuesRemainReadableButOnlyUnlockedLevelsCountAsUnlocked()
    {
        var zero = Entry("locked-zero", value: (0, 0));
        var max = Entry("max", "fixture-fox", ScanConfidence.Confirmed, (3, 0));
        var snapshot = PetScanOverview.Build([zero, max], Catalog, "en");
        Assert.Equal(2, snapshot.Ready);
        Assert.Equal(2, snapshot.Selected);
        Assert.Equal(1, snapshot.Unlocked);
        Assert.All(snapshot.Rows, row => Assert.True(row.Eligible));
    }
}
