using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class PatchNotesReaderTests
{
    private static PatchNotes.Release Release(string version, string date = "2026-10-17", params PatchNotes.Entry[] entries) =>
        new(version, date, entries.Length == 0 ? [new("New", "A release entry.")] : entries);

    [Fact]
    public void ShownUsesTheExistingNumericBoundaryAndPreservesFileOrderAndOriginalReferences()
    {
        var source = new[] { Release("0.10.0"), Release("0.1.27"), Release("0.1.28"), Release("bad"),
            Release("0.2.0"), Release("0.9.9"), Release("0.1.28.1"), Release("0.1.28-preview") };
        var shown = PatchNotesReader.Shown(source);
        Assert.Equal(new[] { "0.10.0", "0.1.28", "0.2.0", "0.9.9", "0.1.28.1" }, shown.Select(release => release.Version));
        foreach (var release in shown) Assert.Contains(source, original => ReferenceEquals(original, release));
        Assert.Equal(8, source.Length);
        Assert.Empty(PatchNotesReader.Shown([]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n ")]
    public void AnEmptyQueryKeepsEveryReleaseAndItsCompleteEntries(string? query)
    {
        var source = new[] { Release("0.10.0"), Release("0.9.9") };
        var filtered = PatchNotesReader.Filter(source, query);
        Assert.Equal(source, filtered);
        Assert.Same(source[0], filtered[0]);
        Assert.Same(source[1].Entries, filtered[1].Entries);
    }

    [Theory]
    [InlineData("0.10.0")]
    [InlineData("2026-10-17")]
    [InlineData("NEW")]
    [InlineData("PET LOOKUP")]
    [InlineData("soul: alpha")]
    [InlineData("  0.10.0\t2026-10\nnew  lookup\u00a0alpha ")]
    public void SearchUsesAllWhitespaceTokensAcrossVersionDateKindAndVisibleSpans(string query)
    {
        var first = Release("0.10.0", "2026-10-17", new("New", "Faster **pet lookup** with `Soul: Alpha`."),
            new("Fixed", "A second unchanged entry."));
        var other = Release("0.9.9", "2026-10-16", new PatchNotes.Entry("Fixed", "Other changes."));
        var result = PatchNotesReader.Filter([first, other], query);
        Assert.Same(first, Assert.Single(result));
        Assert.Equal(2, result[0].Entries.Count);
        Assert.Same(first.Entries, result[0].Entries);
        Assert.Equal("Faster **pet lookup** with `Soul: Alpha`.", result[0].Entries[0].Text);
    }

    [Fact]
    public void TokensCanMatchDifferentEntriesButAllMustOccurInTheSameRelease()
    {
        var first = Release("0.10.0", entries: [new("New", "Pet lookup."), new("Fixed", "Overlay alignment.")]);
        var second = Release("0.9.9", entries: [new("Fixed", "Overlay only.")]);
        var third = Release("0.9.8", entries: [new("New", "Pet only.")]);
        Assert.Same(first, Assert.Single(PatchNotesReader.Filter([first, second, third], "pet overlay")));
        Assert.Empty(PatchNotesReader.Filter([second, third], "pet overlay"));
        Assert.Empty(PatchNotesReader.Filter([first], "pet missing"));
    }

    [Fact]
    public void SearchIgnoresFormattingDelimitersAndDoesNotTreatTheQueryAsRegex()
    {
        var release = Release("0.10.0", entries: [new("New", "A **bold** word and `code.txt`.")]);
        Assert.Same(release, Assert.Single(PatchNotesReader.Filter([release], "bold code.txt")));
        Assert.Empty(PatchNotesReader.Filter([release], "**bold**"));
        Assert.Empty(PatchNotesReader.Filter([release], "`code.txt`"));
        Assert.Empty(PatchNotesReader.Filter([release], ".*"));
    }

    [Fact]
    public void FilteringKeepsTheOriginalReleaseOrderAndGermanCaseInsensitiveText()
    {
        var first = Release("0.2.0", entries: [new("Geändert", "Größere Übersicht.")]);
        var second = Release("0.10.0", entries: [new("Neu", "Größere Übersicht.")]);
        var result = PatchNotesReader.Filter([first, second], "ÜBERSICHT");
        Assert.Equal(new[] { first, second }, result);
        Assert.Same(first, result[0]);
        Assert.Same(second, result[1]);
    }

    [Theory]
    [InlineData("0.10.0", 1)]
    [InlineData("0.9.9", 0)]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("missing", 0)]
    public void SelectionUsesAnExactVersionAndOtherwiseFallsBackToTheFirstRelease(string? version, int expected)
    {
        var releases = new[] { Release("0.9.9"), Release("0.10.0") };
        Assert.Same(releases[expected], PatchNotesReader.Select(releases, version));
        Assert.Null(PatchNotesReader.Select([], version));
    }

    [Fact]
    public void ASelectionFilteredOutOfTheListFallsBackOnlyWithinTheRemainingResults()
    {
        var first = Release("0.10.0", entries: [new("New", "Pets.")]);
        var second = Release("0.9.9", entries: [new("Fixed", "Overlay.")]);
        var visible = PatchNotesReader.Filter([first, second], "overlay");
        Assert.Same(second, PatchNotesReader.Select(visible, first.Version));
        Assert.Null(PatchNotesReader.Select(PatchNotesReader.Filter([first, second], "missing"), second.Version));
        Assert.Same(first, PatchNotesReader.Select([first, second], first.Version));
    }

    [Fact]
    public void BothLanguagesNormalizeToFiveDistinctKindsInTheKnownOrder()
    {
        PatchNotes.Entry[] entries = [new("Removed", "Removed first."), new("Behoben", "Fixed first."),
            new("Changed", "Changed."), new("new", "New first."), new("Improved", "Improved first."),
            new("Entfernt", "Removed second."), new("Neu", "New second."), new("Verbessert", "Improved second."),
            new("Fixed", "Fixed second."), new("Geändert", "Changed second.")];
        var groups = PatchNotesReader.Groups(entries);
        Assert.Equal(new[] { "new", "improved", "fixed", "changed", "removed" }, groups.Select(group => group.Style));
        Assert.Equal(new[] { "Neu", "Verbessert", "Behoben", "Geändert", "Entfernt" }, groups.Select(group => group.Label));
        Assert.Equal(new[] { entries[3], entries[6] }, groups[0].Entries);
        Assert.Equal(new[] { entries[4], entries[7] }, groups[1].Entries);
        Assert.Equal(new[] { entries[1], entries[8] }, groups[2].Entries);
        Assert.Equal(new[] { entries[2], entries[9] }, groups[3].Entries);
        Assert.Equal(new[] { entries[0], entries[5] }, groups[4].Entries);
        Assert.All(groups, group => Assert.All(group.Entries,
            entry => Assert.Contains(entries, original => ReferenceEquals(original, entry))));
        Assert.Equal("new", entries[3].Kind);
        Assert.Equal("Removed", entries[0].Kind);
    }

    [Fact]
    public void UnknownKindsRetainTheirExactLabelsAndSourceOrderWhileEmptyKindsGetAnExplicitLabel()
    {
        PatchNotes.Entry[] entries = [new("Security", "One."), new("", "Uncategorized."), new("Fixed", "Known."),
            new("Security", "Two."), new("Research ", "Preserve the original label."), new(" ", "Also uncategorized."),
            new("security", "Keep a different original label.")];
        var groups = PatchNotesReader.Groups(entries);
        Assert.Equal(new[] { "Behoben", "Security", "Weitere Änderungen", "Research ", "security" }, groups.Select(group => group.Label));
        Assert.Equal("fixed", groups[0].Style);
        Assert.All(groups.Skip(1), group => Assert.Equal("other", group.Style));
        Assert.Equal(new[] { entries[0], entries[3] }, groups[1].Entries);
        Assert.Equal(new[] { entries[1], entries[5] }, groups[2].Entries);
        Assert.Same(entries[4], Assert.Single(groups[3].Entries));
        Assert.Equal("", entries[1].Kind);
        Assert.Equal(" ", entries[5].Kind);
        Assert.Empty(PatchNotesReader.Groups([]));
    }

    [Theory]
    [InlineData("de", false, "7. Januar 2026")]
    [InlineData("de", true, "07.01.2026")]
    [InlineData("en", false, "January 7, 2026")]
    [InlineData("en", true, "Jan 7, 2026")]
    public void ValidDatesUseTheRequestedLanguageAndCompactFormat(string language, bool compact, string expected) =>
        Assert.Equal(expected, PatchNotesReader.FormatDate("2026-01-07", language, compact));

    [Theory]
    [InlineData("")]
    [InlineData("not-a-date")]
    [InlineData("2026-02-29")]
    [InlineData("2026-2-01")]
    [InlineData("2026-01-1")]
    [InlineData("2026-01-07 ")]
    [InlineData(" 2026-01-07")]
    [InlineData("2026/01/07")]
    public void InvalidOrNonexactDatesRemainUntouched(string date)
    {
        Assert.Equal(date, PatchNotesReader.FormatDate(date, "de"));
        Assert.Equal(date, PatchNotesReader.FormatDate(date, "en", compact: true));
    }

    [Fact]
    public void AValidLeapDayFormatsWithoutDependingOnTheOperatingSystemLanguage()
    {
        Assert.Equal("29. Februar 2024", PatchNotesReader.FormatDate("2024-02-29", "de"));
        Assert.Equal("February 29, 2024", PatchNotesReader.FormatDate("2024-02-29", "en"));
    }
}
