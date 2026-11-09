using System.Collections.Immutable;
using Soulcrest.App.Services;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class LootTrackerStatisticsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AssistsHaveIndependentCountsEvidenceAndPercentagesWithoutIncreasingKillsOrDeaths()
    {
        var session = new LootTrackerSession
        {
            Kind = LootTrackerSessionKind.Abyss,
            CombatEvents = [Combat("kill", LootTrackerCombatKind.Kill, "Cleric", 19),
                Combat("death", LootTrackerCombatKind.Death, "Templar", 1),
                Combat("assist1", LootTrackerCombatKind.Assist, "Cleric", 19) with { IsEstimated = true },
                Combat("assist2", LootTrackerCombatKind.Assist, "Cleric", 19) with { IsEstimated = true },
                Combat("assist3", LootTrackerCombatKind.Assist, null, null)]
        };
        var rows = LootTrackerStatistics.Combat(session, AbyssCombatGrouping.Class, AbyssCombatSort.AssistsDescending);
        Assert.Equal(1, rows.Sum(row => row.Kills)); Assert.Equal(1, rows.Sum(row => row.Deaths));
        Assert.Equal(3, rows.Sum(row => row.Assists)); Assert.Equal(2, rows.Sum(row => row.EstimatedAssists));
        Assert.Equal("Cleric", rows[0].Key); Assert.Equal(2, rows[0].Assists);
        Assert.Equal(2 / 3d * 100, rows[0].AssistPercent); Assert.Equal(100, rows[0].KillPercent);
        Assert.Equal(100, rows[1].DeathPercent); Assert.Equal("unknown", rows[^1].Key);
        Assert.Null(Assert.Single(LootTrackerStatistics.Combat(session with
        { CombatEvents = [Combat("kill-only", LootTrackerCombatKind.Kill, "Cleric", 19)] }, AbyssCombatGrouping.Class)).AssistPercent);
    }

    [Fact]
    public void CombatUsesAllSessionResultsWithIndependentUnknownBucketsAndWholeSessionDenominators()
    {
        var session = new LootTrackerSession
        {
            Kind = LootTrackerSessionKind.Abyss,
            CombatEvents = Enumerable.Range(0, 12).Select(index => Combat(index.ToString(), LootTrackerCombatKind.Kill, "Cleric", 19))
                .Append(Combat("unknown", LootTrackerCombatKind.Kill, null, 19))
                .Append(Combat("death", LootTrackerCombatKind.Death, "Templar", null)).ToImmutableArray()
        };
        var classes = LootTrackerStatistics.Combat(session, AbyssCombatGrouping.Class);
        Assert.Equal(new[] { "Cleric", "Templar", "unknown" }, classes.Select(row => row.Key));
        Assert.Equal(12, classes[0].Kills); Assert.Equal(12 / 13d * 100, classes[0].KillPercent);
        Assert.Equal(0, classes[0].DeathPercent); Assert.Equal(100, classes[1].DeathPercent);
        Assert.Null(classes[^1].IconAsset); Assert.True(classes[^1].Unknown);
        var ranks = LootTrackerStatistics.Combat(session, AbyssCombatGrouping.Rank, AbyssCombatSort.DeathsDescending);
        Assert.Equal(13, ranks[0].Kills); Assert.Equal("rank-19", ranks[0].Key);
        Assert.Equal(100, ranks[^1].DeathPercent); // unknown always remains last, even with the most deaths
        Assert.Equal(14, session.CombatEvents.Length);
    }

    [Fact]
    public void TiedRankCountsUseNativeGradeAndEmptyDenominatorsStayUnknown()
    {
        var session = new LootTrackerSession
        {
            Kind = LootTrackerSessionKind.Abyss,
            CombatEvents = [Combat("low", LootTrackerCombatKind.Kill, "Cleric", 19),
                Combat("high", LootTrackerCombatKind.Kill, "Cleric", 1), Combat("missing", LootTrackerCombatKind.Kill, "Unknown class", 100)]
        };
        var ranks = LootTrackerStatistics.Combat(session, AbyssCombatGrouping.Rank);
        Assert.Equal(new[] { "rank-01", "rank-19", "unknown" }, ranks.Select(row => row.Key));
        Assert.Equal("abyss-rank-icons/rank-01.png", ranks[0].IconAsset);
        Assert.All(ranks, row => Assert.Null(row.DeathPercent));
        Assert.Null(LootTrackerStatistics.Percent(0, 0));
        Assert.Empty(LootTrackerStatistics.Combat(new(), AbyssCombatGrouping.Class));
    }

    [Fact]
    public void ResourceAttemptsIncludeUnplacedExactIdentitiesAndRewardsWithoutDoubleCountingPositionedVisits()
    {
        var visit = new LootTrackerGatherVisit("placed", 0, "sample", "Sample", 10, 10, 10, 10, At, true, "Sample ore", GatherableId: 42);
        var session = new LootTrackerSession
        {
            Kind = LootTrackerSessionKind.Lifeskill, TimelineVersion = 1, GatherVisits = [visit], FailedHarvestCount = 2,
            TimelineEvents =
            [
                Harvest("first", true, 42, 2) with { GatherVisit = visit }, Harvest("no-position", true, 42, 3),
                Harvest("failure", false, 42, 0), Harvest("unknown", false, null, 0)
            ]
        };
        var resources = LootTrackerStatistics.Resources(session);
        Assert.Equal(2, resources.Length);
        Assert.Equal("Sample ore", resources[0].Name); Assert.Equal(3, resources[0].Attempts);
        Assert.Equal(2, resources[0].Successes); Assert.Equal(1, resources[0].Failures); Assert.Equal(5, resources[0].Quantity);
        Assert.True(resources[^1].Unknown); Assert.Equal(1, resources[^1].Failures);
        var reversed = LootTrackerStatistics.Resources(session, LootResourceSort.AttemptsAscending);
        Assert.Equal("unknown", reversed[^1].Key);
    }

    [Fact]
    public void HistoricalVisitsDoNotInventItemQuantitiesAndUnplacedIdentityDoesNotInventAResourceName()
    {
        var historical = new LootTrackerSession
        {
            Kind = LootTrackerSessionKind.Lifeskill,
            GatherVisits = [new("old", 0, "sample", "Sample", 1, 2, 1, 2, At, false, "Sample herb", GatherableId: 99)]
        };
        Assert.Null(Assert.Single(LootTrackerStatistics.Resources(historical)).Quantity);
        var current = historical with { TimelineVersion = 1, TimelineEvents = [Harvest("unplaced", false, 99, 0)] };
        Assert.Equal("Resource #99", Assert.Single(LootTrackerStatistics.Resources(current)).Name);
        Assert.Equal("Ressource #99", Assert.Single(LootTrackerStatistics.Resources(current, language: "de")).Name);
    }

    [Fact]
    public void SuccessOnlyLegacyJournalDoesNotClaimKnownAttemptsOrZeroFailures()
    {
        var session = new LootTrackerSession
        {
            Kind = LootTrackerSessionKind.Lifeskill, TimelineVersion = 1, FailedHarvestCount = null,
            TimelineEvents = [Harvest("old-success", true, 42, 3), Harvest("old-second-success", true, 42, 2)]
        };
        var row = Assert.Single(LootTrackerStatistics.Resources(session));
        Assert.Null(row.Attempts); Assert.Null(row.Failures);
        Assert.Equal(2, row.Successes); Assert.Equal(5, row.Quantity);
        var known = Assert.Single(LootTrackerStatistics.Resources(session with { FailedHarvestCount = 0 }));
        Assert.Equal(2, known.Attempts); Assert.Equal(0, known.Failures);
        Assert.Equal(row.Successes, known.Successes); Assert.Equal(row.Quantity, known.Quantity);
    }

    [Fact]
    public void LootAndMobSortingKeepSourcesSeparateAndUseDeterministicNameTies()
    {
        var session = new LootTrackerSession
        {
            Loot = [new("b", "Beta", LootTrackerLootKind.Item, 4), new("a", "Alpha", LootTrackerLootKind.Soul, 4),
                new("c", "Gamma", LootTrackerLootKind.Item, 1)],
            Mobs = [new("mob", "Confirmed monster", 2)]
        };
        Assert.Equal(new[] { "a", "b", "c" }, LootTrackerStatistics.Loot(session).Select(row => row.Key));
        Assert.Equal(new[] { "c", "a", "b" }, LootTrackerStatistics.Loot(session, LootQuantitySort.QuantityAscending).Select(row => row.Key));
        Assert.Equal(2, Assert.Single(LootTrackerStatistics.Mobs(session)).Quantity);
        Assert.Empty(LootTrackerStatistics.Mobs(session with { Mobs = [] }));
        Assert.Empty(LootTrackerStatistics.Loot(session with { Loot = default }));
    }

    private static LootTrackerCombatEvent Combat(string id, LootTrackerCombatKind kind, string? classId, int? grade) =>
        new(id, kind, At, TimeSpan.Zero) { OpponentClassId = classId, OpponentAbyssRankGrade = grade };
    private static LootTrackerTimelineEvent Harvest(string id, bool success, int? resource, long quantity) =>
        new(id, success ? LootTrackerTimelineEventKind.HarvestSuccess : LootTrackerTimelineEventKind.HarvestFailure, At, TimeSpan.Zero)
        {
            GatherableId = resource,
            Loot = quantity > 0 ? [new("sample-item", "Sample reward", LootTrackerLootKind.Item, quantity)] : []
        };
}
