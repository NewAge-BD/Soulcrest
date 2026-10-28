using System.Collections.Immutable;
using System.Globalization;
using Soulcrest.App.Overlay;
using Soulcrest.Core.Daevanion;

namespace Soulcrest.App.Services;

public enum AbyssCombatGrouping { Class, Rank }
public enum AbyssCombatSort { KillsDescending, KillsAscending, DeathsDescending, DeathsAscending, NameAscending, NameDescending }
public enum LootResourceSort
{
    SuccessesDescending, SuccessesAscending, NameAscending, NameDescending, AttemptsDescending, AttemptsAscending,
    FailuresDescending, FailuresAscending, QuantityDescending, QuantityAscending
}
public enum LootQuantitySort { QuantityDescending, QuantityAscending, NameAscending, NameDescending }
public enum LootTimelineValueMode { Cumulative, PerMinute }

/// <summary>Whole-session projections. Replay position and overlay filters never limit these totals.</summary>
public static class LootTrackerStatistics
{
    public sealed record CombatRow(string Key, string Name, string? IconAsset, bool Unknown,
        long Kills, long Deaths, double? KillPercent, double? DeathPercent, int? Grade = null);
    public sealed record ResourceRow(string Key, string Name, bool Unknown, long? Attempts,
        long Successes, long? Failures, long? Quantity);
    public sealed record QuantityRow(string Key, string Name, long Quantity, double? Percent, string? IconAsset = null);

    public static ImmutableArray<CombatRow> Combat(LootTrackerSession session, AbyssCombatGrouping grouping,
        AbyssCombatSort sort = AbyssCombatSort.KillsDescending, string language = "en")
    {
        if (session.Kind != LootTrackerSessionKind.Abyss || session.CombatEvents.IsDefaultOrEmpty) return [];
        var events = session.CombatEvents.Where(row => row.Kind is LootTrackerCombatKind.Kill or LootTrackerCombatKind.Death).ToArray();
        var kills = events.LongCount(row => row.Kind == LootTrackerCombatKind.Kill);
        var deaths = events.LongCount(row => row.Kind == LootTrackerCombatKind.Death);
        var rows = events.GroupBy(row => grouping == AbyssCombatGrouping.Rank
            ? row.OpponentAbyssRankGrade is >= 1 and <= 19 ? $"rank-{row.OpponentAbyssRankGrade:00}" : "unknown"
            : AbyssOpponentIconAssets.ClassAsset(row.OpponentClassId) is not null ? row.OpponentClassId! : "unknown")
            .Select(group =>
            {
                var unknown = group.Key == "unknown";
                var stage = grouping == AbyssCombatGrouping.Rank && !unknown
                    ? AbyssRankCatalog.Default.Stages.FirstOrDefault(stage => stage.Id == group.Key) : null;
                var name = unknown ? Unknown(language) : grouping == AbyssCombatGrouping.Rank ? stage!.Name(language)
                    : DaevanionCatalog.Default.Classes.First(row => row.Id == group.Key).Name.For(language);
                var countKills = group.LongCount(row => row.Kind == LootTrackerCombatKind.Kill);
                var countDeaths = group.LongCount(row => row.Kind == LootTrackerCombatKind.Death);
                return new CombatRow(group.Key, name, unknown ? null : stage?.IconAsset ?? AbyssOpponentIconAssets.ClassAsset(group.Key),
                    unknown, countKills, countDeaths, Percent(countKills, kills), Percent(countDeaths, deaths),
                    stage is not null ? int.Parse(group.Key.AsSpan(5), CultureInfo.InvariantCulture) : null);
            }).ToArray();
        var ordered = rows.OrderBy(row => row.Unknown);
        IOrderedEnumerable<CombatRow> result = sort switch
        {
            AbyssCombatSort.KillsAscending => ordered.ThenBy(row => row.Kills),
            AbyssCombatSort.DeathsDescending => ordered.ThenByDescending(row => row.Deaths),
            AbyssCombatSort.DeathsAscending => ordered.ThenBy(row => row.Deaths),
            AbyssCombatSort.NameAscending => grouping == AbyssCombatGrouping.Rank
                ? ordered.ThenBy(row => row.Grade) : ordered.ThenBy(row => row.Name, Comparer(language)),
            AbyssCombatSort.NameDescending => grouping == AbyssCombatGrouping.Rank
                ? ordered.ThenByDescending(row => row.Grade) : ordered.ThenByDescending(row => row.Name, Comparer(language)),
            _ => ordered.ThenByDescending(row => row.Kills)
        };
        return (grouping == AbyssCombatGrouping.Rank ? result.ThenBy(row => row.Grade) : result.ThenBy(row => row.Name, Comparer(language)))
            .ThenBy(row => row.Key, StringComparer.Ordinal).ToImmutableArray();
    }

    public static ImmutableArray<ResourceRow> Resources(LootTrackerSession session,
        LootResourceSort sort = LootResourceSort.SuccessesDescending, string language = "en")
    {
        if (!LootTrackerSessionProjection.ShowsHarvest(session)) return [];
        // Exact attempt events include rewards without a position. Old route visits are a labelled,
        // incomplete fallback: proximity and an item identity never establish a resource identity.
        var attempts = session.TimelineVersion >= 1
            ? (session.TimelineEvents.IsDefaultOrEmpty ? [] : session.TimelineEvents).Where(row => row.Kind is LootTrackerTimelineEventKind.HarvestSuccess or LootTrackerTimelineEventKind.HarvestFailure)
                .Select(row => new Attempt(row.GatherableId ?? row.GatherVisit?.GatherableId,
                    row.GatherVisit?.Name,
                    row.Kind == LootTrackerTimelineEventKind.HarvestSuccess,
                    row.Loot.IsDefault ? 0 : row.Loot.Where(loot => loot.Kind == LootTrackerLootKind.Item && loot.Quantity > 0).Sum(loot => loot.Quantity), true))
            : (session.GatherVisits.IsDefaultOrEmpty ? [] : session.GatherVisits)
                .Select(row => new Attempt(row.GatherableId, row.Name, row.Succeeded, 0, false));
        var rows = attempts.GroupBy(row => row.ResourceId is > 0 ? "resource-" + row.ResourceId : "unknown")
            .Select(group => new ResourceRow(group.Key, group.Key == "unknown" ? Unknown(language)
                    : group.Select(row => row.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
                        ?? (language == "de" ? "Ressource #" : "Resource #") + group.First().ResourceId,
                group.Key == "unknown", session.FailedHarvestCount is null ? null : group.LongCount(),
                group.LongCount(row => row.Success), session.FailedHarvestCount is null ? null : group.LongCount(row => !row.Success),
                group.All(row => row.QuantityKnown) ? group.Sum(row => row.Quantity) : null)).ToArray();
        var ordered = rows.OrderBy(row => row.Unknown);
        var result = sort switch
        {
            LootResourceSort.SuccessesAscending => ordered.ThenBy(row => row.Successes),
            LootResourceSort.NameAscending => ordered.ThenBy(row => row.Name, Comparer(language)),
            LootResourceSort.NameDescending => ordered.ThenByDescending(row => row.Name, Comparer(language)),
            LootResourceSort.AttemptsDescending => ordered.ThenByDescending(row => row.Attempts),
            LootResourceSort.AttemptsAscending => ordered.ThenBy(row => row.Attempts),
            LootResourceSort.FailuresDescending => ordered.ThenByDescending(row => row.Failures),
            LootResourceSort.FailuresAscending => ordered.ThenBy(row => row.Failures),
            LootResourceSort.QuantityDescending => ordered.ThenBy(row => row.Quantity is null).ThenByDescending(row => row.Quantity),
            LootResourceSort.QuantityAscending => ordered.ThenBy(row => row.Quantity is null).ThenBy(row => row.Quantity),
            _ => ordered.ThenByDescending(row => row.Successes)
        };
        return result.ThenBy(row => row.Name, Comparer(language)).ThenBy(row => row.Key, StringComparer.Ordinal).ToImmutableArray();
    }

    public static ImmutableArray<QuantityRow> Loot(LootTrackerSession session,
        LootQuantitySort sort = LootQuantitySort.QuantityDescending, string language = "en")
    {
        if (session.Loot.IsDefaultOrEmpty) return [];
        var total = session.Loot.Where(row => row.Quantity >= 0).Sum(row => row.Quantity);
        return OrderQuantity(session.Loot.Where(row => row.Quantity >= 0).Select(row =>
            new QuantityRow(row.Id, row.Name, row.Quantity, Percent(row.Quantity, total), MonsterLootIconCatalog.Default.DropAsset(row.Id))), sort, language);
    }

    public static ImmutableArray<QuantityRow> Mobs(LootTrackerSession session,
        LootQuantitySort sort = LootQuantitySort.QuantityDescending, string language = "en")
    {
        if (session.Mobs.IsDefaultOrEmpty) return [];
        var total = session.Mobs.Where(row => row.Count >= 0).Sum(row => row.Count);
        return OrderQuantity(session.Mobs.Where(row => row.Count >= 0).Select(row =>
            new QuantityRow(row.Id, row.Name, row.Count, Percent(row.Count, total))), sort, language);
    }

    public static double? Percent(long amount, long total) => total > 0 ? amount / (double)total * 100 : null;
    private static string Unknown(string language) => language == "de" ? "Unbekannt" : "Unknown";
    private static StringComparer Comparer(string language) => StringComparer.Create(CultureInfo.GetCultureInfo(language == "de" ? "de-DE" : "en-US"), false);
    private static ImmutableArray<QuantityRow> OrderQuantity(IEnumerable<QuantityRow> rows, LootQuantitySort sort, string language)
    {
        var ordered = sort switch
        {
            LootQuantitySort.QuantityAscending => rows.OrderBy(row => row.Quantity),
            LootQuantitySort.NameAscending => rows.OrderBy(row => row.Name, Comparer(language)),
            LootQuantitySort.NameDescending => rows.OrderByDescending(row => row.Name, Comparer(language)),
            _ => rows.OrderByDescending(row => row.Quantity)
        };
        return ordered.ThenBy(row => row.Name, Comparer(language)).ThenBy(row => row.Key, StringComparer.Ordinal).ToImmutableArray();
    }
    private sealed record Attempt(int? ResourceId, string? Name, bool Success, long Quantity, bool QuantityKnown);
}
