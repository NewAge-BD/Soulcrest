using Soulcrest.App.Services;
using Soulcrest.Core.Text;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class KibeliskSessionSafetyTests
{
    private static readonly ExplorationPlace[] Places =
    [
        new("synthetic-beacon", "synthetic-map", "kibelisk", 1, "Synthetic Beacon", "Synthetische Wegstation"),
        new("synthetic-ridge", "synthetic-map", "kibelisk", 2, "Synthetic Ridge", "Synthetischer Grat"),
        new("synthetic-lake", "synthetic-map", "kibelisk", 3, "Synthetic Lake", "Synthetischer See"),
        new("synthetic-unlisted", "synthetic-map", "kibelisk", 4, "Synthetic Unlisted Place", null),
    ];

    private static KibeliskPage Page(params (string Name, bool Bound)[] rows) => KibeliskList.Read(rows.SelectMany((row, index) =>
        new[] { new OcrLine($"{index + 1}. {row.Name}", 40, 40 + index * 86, 300, 19),
                new OcrLine(row.Bound ? "Bindung abgeschlossen" : "Bindung nicht abgeschlossen", 40, 70 + index * 86, 300, 19) }).ToArray(), 1200);

    private static KibeliskSession ReadTwice(KibeliskPage page, IReadOnlyList<ExplorationPlace>? places = null)
    {
        var session = new KibeliskSession(places ?? Places);
        session.Observe(page);
        session.Observe(page);
        Assert.True(session.Ended);
        return session;
    }

    [Fact]
    public void UnknownNamesAtTheConfirmedEndCannotUnbindAnyPreviouslyDiscoveredPlace()
    {
        var session = ReadTwice(Page(("Unreadable synthetic alpha", true), ("Unreadable synthetic beta", false)));
        var (bound, unbound, unknown, open) = session.Result();
        Assert.Empty(bound);
        Assert.Empty(unbound);
        Assert.Equal(2, unknown.Count);
        Assert.Empty(open);
        Assert.False(session.Complete);
    }

    [Fact]
    public void AnUnknownRowBlocksOnlyTheInferredNegativesAndKeepsClearBoundAndUnboundRows()
    {
        var session = ReadTwice(Page(("Synthetische Wegstation", true), ("Synthetischer Grat", false), ("Unreadable synthetic name", true)));
        var (bound, unbound, unknown, open) = session.Result();
        Assert.Equal(new[] { "synthetic-beacon" }, bound);
        Assert.Equal(new[] { "synthetic-ridge" }, unbound);
        Assert.Single(unknown);
        Assert.Empty(open);
        Assert.DoesNotContain("synthetic-lake", unbound);
        Assert.DoesNotContain("synthetic-unlisted", unbound);
        Assert.False(session.Complete);
    }

    [Fact]
    public void EvenWhenEveryPlaceHasAClearStatusAnAdditionalUnknownRowPreventsClaimingCompletion()
    {
        var page = Page(("Synthetische Wegstation", true), ("Synthetischer Grat", false),
            ("Synthetischer See", true), ("Synthetic Unlisted Place", true), ("Unreadable synthetic extra", false));
        var session = ReadTwice(page);
        Assert.Equal(3, session.Result().Bound.Count);
        Assert.Equal(new[] { "synthetic-ridge" }, session.Result().Unbound);
        Assert.Single(session.Result().Unknown);
        Assert.False(session.Complete);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnresolvedTruncatedGroupsNeverInferUnboundForAbsentPlaces(bool conflictingStatuses)
    {
        ExplorationPlace[] places = [.. Places,
            new("synthetic-east-a", "synthetic-map", "kibelisk", 5, "Synthetic Eastern Highland Tail", null),
            new("synthetic-east-b", "synthetic-map", "kibelisk", 6, "Synthetic Eastern Highland Cliff", null)];
        var rows = new List<(string Name, bool Bound)> { ("Synthetische Wegstation", true), ("Synthetic Eastern High...", true) };
        if (conflictingStatuses) rows.Add(("Synthetic Eastern High...", false));
        var session = ReadTwice(Page(rows.ToArray()), places);
        Assert.Equal(new[] { "synthetic-beacon" }, session.Result().Bound);
        Assert.Empty(session.Result().Unbound);
        Assert.Empty(session.Result().Unknown);
        Assert.NotEmpty(session.Result().Open);
        Assert.False(session.Complete);
    }

    [Fact]
    public void AResolvedPartialDiscoveredListStillUnbindsPlacesNotPresentAtItsConfirmedEnd()
    {
        var session = ReadTwice(Page(("Synthetische Wegstation", true), ("Synthetischer Grat", false)));
        var (bound, unbound, unknown, open) = session.Result();
        Assert.Equal(new[] { "synthetic-beacon" }, bound);
        Assert.Equal(new[] { "synthetic-lake", "synthetic-ridge", "synthetic-unlisted" }, unbound.Order());
        Assert.Empty(unknown);
        Assert.Empty(open);
        Assert.True(session.Complete);
    }

    [Fact]
    public void ResolvingAnUnknownRowLaterEnablesTheEndFallbackWithoutRestartingTheSession()
    {
        var session = ReadTwice(Page(("Synthetische Wegstation", true), ("Unreadable synthetic name", false)));
        Assert.False(session.Complete);
        Assert.Empty(session.Result().Unbound);
        var fixedPage = Page(("Synthetische Wegstation", true), ("Synthetischer Grat", false));
        session.Observe(fixedPage);
        Assert.False(session.Complete); // one corrected frame cannot replace the old unknown evidence
        Assert.Empty(session.Result().Unbound);
        session.Observe(fixedPage);
        Assert.Empty(session.Result().Unknown);
        Assert.Empty(session.Result().Open);
        Assert.Equal(new[] { "synthetic-lake", "synthetic-ridge", "synthetic-unlisted" }, session.Result().Unbound.Order());
        Assert.True(session.Complete);
    }

    [Fact]
    public void AConfirmedNumberCanChangeFromBoundToUnboundWithoutIncreasingTheRowCount()
    {
        var session = new KibeliskSession(Places);
        var boundRow = new KibeliskRow(1, "Synthetische Wegstation", false, true);
        var boundPage = new KibeliskPage([boundRow], false);
        session.Observe(boundPage);
        Assert.Single(session.Observe(boundPage));
        var previousCount = session.Confirmed.Count;
        Assert.Equal(new[] { "synthetic-beacon" }, session.Result().Bound);
        Assert.False(session.Ended);

        var unboundRow = boundRow with { Bound = false };
        var unboundPage = new KibeliskPage([unboundRow], false);
        Assert.Empty(session.Observe(unboundPage));
        Assert.Equal(new[] { "synthetic-beacon" }, session.Result().Bound);
        Assert.Empty(session.Result().Unbound);
        Assert.Equal(unboundRow, Assert.Single(session.Observe(unboundPage)));
        Assert.Equal(previousCount, session.Confirmed.Count);
        Assert.Empty(session.Result().Bound);
        Assert.Equal(new[] { "synthetic-beacon" }, session.Result().Unbound);
        Assert.False(session.Ended);
    }

    [Fact]
    public void AnUnknownConfirmedNumberCanResolveWithoutIncreasingTheRowCount()
    {
        var session = new KibeliskSession(Places);
        var unknownRow = new KibeliskRow(1, "Unreadable synthetic name", false, true);
        var unknownPage = new KibeliskPage([unknownRow], false);
        session.Observe(unknownPage);
        Assert.Single(session.Observe(unknownPage));
        var previousCount = session.Confirmed.Count;
        Assert.Single(session.Result().Unknown);
        Assert.Empty(session.Result().Bound);
        Assert.False(session.Ended);

        var resolvedRow = unknownRow with { Name = "Synthetische Wegstation" };
        var resolvedPage = new KibeliskPage([resolvedRow], false);
        Assert.Empty(session.Observe(resolvedPage));
        Assert.Single(session.Result().Unknown);
        Assert.Empty(session.Result().Bound);
        Assert.Equal(resolvedRow, Assert.Single(session.Observe(resolvedPage)));
        Assert.Equal(previousCount, session.Confirmed.Count);
        Assert.Empty(session.Result().Unknown);
        Assert.Equal(new[] { "synthetic-beacon" }, session.Result().Bound);
        Assert.Empty(session.Result().Unbound);
        Assert.False(session.Ended);
    }

    [Fact]
    public void AnUnobservedListCannotGenerateAnyNegativeOrClaimCompletion()
    {
        var session = new KibeliskSession(Places);
        Assert.Empty(session.Result().Bound);
        Assert.Empty(session.Result().Unbound);
        Assert.False(session.Ended);
        Assert.False(session.Complete);
    }
}
