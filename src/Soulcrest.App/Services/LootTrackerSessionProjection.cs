using System.Collections.Immutable;

namespace Soulcrest.App.Services;

/// <summary>Four read-only views of one recorded session. A view never changes its clock, identity or walked path.</summary>
public static class LootTrackerSessionProjection
{
    public static LootTrackerViewMode View(LootTrackerSession session) => session.ProjectionViewMode
        ?? (session.Kind == LootTrackerSessionKind.Lifeskill ? LootTrackerViewMode.Lifeskill
            : session.Kind == LootTrackerSessionKind.Abyss ? LootTrackerViewMode.Abyss : LootTrackerViewMode.Pve);
    public static bool ShowsHarvest(LootTrackerSession session) => View(session) == LootTrackerViewMode.Lifeskill
        || (View(session) == LootTrackerViewMode.Mixed || View(session) == LootTrackerViewMode.Abyss && session.Kind == LootTrackerSessionKind.Abyss)
            && (session.UnifiedVersion > 0 || session.Kind == LootTrackerSessionKind.Lifeskill
            || !session.TimelineEvents.IsDefaultOrEmpty && session.TimelineEvents.Any(row => row.Kind is LootTrackerTimelineEventKind.HarvestSuccess or LootTrackerTimelineEventKind.HarvestFailure));
    public static bool ShowsAbyss(LootTrackerSession session) => session.Kind == LootTrackerSessionKind.Abyss
        && View(session) is LootTrackerViewMode.Mixed or LootTrackerViewMode.Abyss;
    // Currency income is independent of residence. Absolute character caches never select a points block.
    public static bool ShowsPoints(LootTrackerSession session) => ShowsAbyss(session)
        || session.Kind is not (LootTrackerSessionKind.Abyss or LootTrackerSessionKind.Lifeskill)
            && View(session) is (LootTrackerViewMode.Mixed or LootTrackerViewMode.Pve)
            && (session.AbyssPoints is not null || session.RankPointsGained is not null || session.RankPointsLost is not null
                || !session.TimelineEvents.IsDefaultOrEmpty && session.TimelineEvents.Any(row =>
                    row.Kind is (LootTrackerTimelineEventKind.AbyssPoints or LootTrackerTimelineEventKind.RankPoints)
                    && row.Amount is not null));
    public static bool ShowsPve(LootTrackerSession session) => View(session) == LootTrackerViewMode.Pve
        || View(session) == LootTrackerViewMode.Mixed && session.Kind != LootTrackerSessionKind.Lifeskill;

    public static LootTrackerSession Project(LootTrackerSession parent, LootTrackerViewMode mode)
    {
        ArgumentNullException.ThrowIfNull(parent);
        if (!Enum.IsDefined(mode)) mode = LootTrackerViewMode.Mixed;
        var exact = parent.TimelineVersion > 0;
        // Pure legacy sessions retain their existing aggregates. No historical sessions are joined,
        // and an old mixed Abyss item total is never divided by item identity or temporal proximity.
        if (parent.UnifiedVersion == 0 && (mode == LootTrackerViewMode.Mixed
            || mode == LootTrackerViewMode.Abyss && parent.Kind == LootTrackerSessionKind.Abyss
            || mode == LootTrackerViewMode.Pve && parent.Kind is LootTrackerSessionKind.Grind or LootTrackerSessionKind.Dungeon
            || mode == LootTrackerViewMode.Lifeskill && parent.Kind == LootTrackerSessionKind.Lifeskill))
            return parent with { ProjectionViewMode = mode };

        var facts = (parent.TimelineEvents.IsDefault ? [] : parent.TimelineEvents)
            .Where(row => Includes(parent, row, mode)).ToImmutableArray();
        var rewards = facts.Where(row => row.Kind is LootTrackerTimelineEventKind.Drop or LootTrackerTimelineEventKind.HarvestSuccess)
            .SelectMany(row => row.Loot.IsDefault ? [] : row.Loot).ToArray();
        var limited = parent.UnifiedVersion == 0 || !exact || !parent.TimelineEvents.IsDefaultOrEmpty && parent.TimelineEvents.Any(row => row.Origin is null);
        var loot = ImmutableArray<LootTrackerLoot>.Empty;
        var lootKnown = true;
        try
        {
            loot = rewards.GroupBy(row => (Id: CanonicalKey(row.Id), row.Kind)).Select(group => group.First() with
            {
                Id = group.Key.Id, Quantity = group.Aggregate(0L, (sum, row) => checked(sum + row.Quantity)), MobId = null,
            }).OrderBy(row => row.Id, StringComparer.Ordinal).ToImmutableArray();
        }
        catch (OverflowException) { limited = true; lootKnown = false; }
        var harvests = facts.Where(row => row.Kind is LootTrackerTimelineEventKind.HarvestSuccess or LootTrackerTimelineEventKind.HarvestFailure).ToArray();
        var withinContext = mode != LootTrackerViewMode.Abyss || parent.Kind == LootTrackerSessionKind.Abyss;
        var harvestKnown = withinContext && exact && (parent.HarvestCount is not null || harvests.Length > 0);
        var failuresKnown = withinContext && exact && parent.FailedHarvestCount is not null;
        var selectedVisits = facts.Where(row => row.GatherVisit is not null).Select(row => row.GatherVisit!.Id).ToHashSet(StringComparer.Ordinal);
        var visits = (parent.GatherVisits.IsDefault ? [] : parent.GatherVisits)
            .Where(row => mode != LootTrackerViewMode.Pve && (selectedVisits.Contains(row.Id)
                || mode == LootTrackerViewMode.Mixed || mode == LootTrackerViewMode.Abyss && parent.Kind == LootTrackerSessionKind.Abyss)).ToImmutableArray();
        var numeric = mode == LootTrackerViewMode.Mixed || mode == LootTrackerViewMode.Abyss && parent.Kind == LootTrackerSessionKind.Abyss
            || mode == LootTrackerViewMode.Pve && parent.Kind is LootTrackerSessionKind.Mixed or LootTrackerSessionKind.Grind or LootTrackerSessionKind.Dungeon;
        var points = numeric && parent.Kind != LootTrackerSessionKind.Lifeskill;
        var itemsKnown = lootKnown && exact && (mode == LootTrackerViewMode.Lifeskill ? harvestKnown : numeric && parent.ItemCount is not null);
        long? Sum(IEnumerable<long> amounts, bool known)
        {
            if (!known) return null;
            try { return amounts.Aggregate(0L, (sum, value) => checked(sum + value)); }
            catch (OverflowException) { limited = true; return null; }
        }
        long? Metric(LootTrackerTimelineEventKind kind, long? availability, bool include) => Sum(
            facts.Where(row => row.Kind == kind && row.Amount is not null).Select(row => row.Amount!.Value),
            include && exact && (availability is not null || facts.Any(row => row.Kind == kind)));
        var rank = facts.Where(row => row.Kind == LootTrackerTimelineEventKind.RankPoints && row.Amount is not null).Select(row => row.Amount!.Value).ToArray();
        var itemCount = Sum(loot.Where(row => row.Kind == LootTrackerLootKind.Item).Select(row => row.Quantity), itemsKnown);
        var soulCount = Sum(loot.Where(row => row.Kind == LootTrackerLootKind.Soul).Select(row => row.Quantity), exact && numeric && parent.SoulCount is not null);
        var kinaBound = Metric(LootTrackerTimelineEventKind.KinaBound, parent.KinaBound, numeric);
        var kinaUnbound = Metric(LootTrackerTimelineEventKind.KinaUnbound, parent.KinaUnbound, numeric);
        var ap = Metric(LootTrackerTimelineEventKind.AbyssPoints, parent.AbyssPoints, points);
        var gained = Sum(rank.Where(value => value >= 0), points && exact && (parent.RankPointsGained is not null || rank.Any(value => value > 0)));
        var lost = Sum(rank.Where(value => value < 0 && value != long.MinValue).Select(value => -value), points && exact && (parent.RankPointsLost is not null || rank.Any(value => value < 0)));
        var combat = (parent.CombatEvents.IsDefault ? [] : parent.CombatEvents)
            .Where(row => mode == LootTrackerViewMode.Mixed || mode == LootTrackerViewMode.Abyss && parent.Kind == LootTrackerSessionKind.Abyss
                || mode == LootTrackerViewMode.Pve && parent.Kind != LootTrackerSessionKind.Abyss && row.Origin == LootTrackerActivityOrigin.Pve).ToImmutableArray();
        var projected = parent with
        {
            ProjectionViewMode = mode, TimelineEvents = facts, Loot = loot,
            KinaBound = kinaBound, KinaUnbound = kinaUnbound, AbyssPoints = ap, RankPointsGained = gained, RankPointsLost = lost,
            HarvestCount = harvestKnown ? harvests.LongCount(row => row.Kind == LootTrackerTimelineEventKind.HarvestSuccess) : null,
            FailedHarvestCount = failuresKnown ? harvests.LongCount(row => row.Kind == LootTrackerTimelineEventKind.HarvestFailure) : null,
            ItemCount = itemCount, SoulCount = soulCount,
            MonsterCount = numeric ? parent.MonsterCount : null, Mobs = numeric ? parent.Mobs : [],
            GatherVisits = visits, FarmRoutePoints = LifeskillFarmRouteBuilder.Build(visits), CombatEvents = combat,
            ResourceNodes = mode == LootTrackerViewMode.Pve ? [] : parent.ResourceNodes,
            LastLootAt = mode == LootTrackerViewMode.Lifeskill
                ? facts.Where(row => row.Kind == LootTrackerTimelineEventKind.HarvestSuccess).Select(row => (DateTimeOffset?)row.At).Max()
                : parent.LastLootAt,
            MinuteIncome = [], ProjectionLimited = limited
        };
        return projected;
    }

    private static bool Includes(LootTrackerSession parent, LootTrackerTimelineEvent row, LootTrackerViewMode mode)
    {
        if (mode == LootTrackerViewMode.Mixed) return true;
        if (mode == LootTrackerViewMode.Abyss) return parent.Kind == LootTrackerSessionKind.Abyss;
        var origin = row.Origin ?? (parent.UnifiedVersion > 0 ? null : row.Kind is LootTrackerTimelineEventKind.HarvestSuccess or LootTrackerTimelineEventKind.HarvestFailure
            ? LootTrackerActivityOrigin.Lifeskill : parent.Kind is LootTrackerSessionKind.Grind or LootTrackerSessionKind.Dungeon ? LootTrackerActivityOrigin.Pve
                : parent.Kind == LootTrackerSessionKind.Abyss ? LootTrackerActivityOrigin.Abyss : (LootTrackerActivityOrigin?)null);
        return mode == LootTrackerViewMode.Pve ? parent.Kind != LootTrackerSessionKind.Abyss && origin == LootTrackerActivityOrigin.Pve
            : origin == LootTrackerActivityOrigin.Lifeskill && row.Kind is LootTrackerTimelineEventKind.HarvestSuccess or LootTrackerTimelineEventKind.HarvestFailure;
    }

    private static string CanonicalKey(string key) => MonsterLootItemCatalog.TryItemId(key, out var id)
        ? "Item:" + id.ToString(System.Globalization.CultureInfo.InvariantCulture) : key;

    /// <summary>Full confirmed gathering stops only; never a replay prefix, travelled path or combat waypoint.</summary>
    public static LootTrackerSession ResourceRoute(LootTrackerSession parent) => parent with
    {
        Kind = LootTrackerSessionKind.Lifeskill, ProjectionViewMode = LootTrackerViewMode.Lifeskill,
        FarmRouteVersion = 1, FarmRoutePoints = parent.GatherVisits.IsDefaultOrEmpty && parent.UnifiedVersion == 0
            && parent.Kind == LootTrackerSessionKind.Lifeskill && parent.FarmRouteVersion == 1 ? parent.FarmRoutePoints
                : LifeskillFarmRouteBuilder.Build(parent.GatherVisits.IsDefault ? [] : parent.GatherVisits),
        RouteSegments = [], CombatEvents = [], ResourceNodes = []
    };
}
