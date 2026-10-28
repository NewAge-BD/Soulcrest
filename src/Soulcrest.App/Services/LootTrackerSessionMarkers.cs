using System.Collections.Immutable;
using System.Globalization;

namespace Soulcrest.App.Services;

public enum LootTrackerSessionMarkerKind { Kill, Death, Odyle, Gathering, Drop }

/// <summary>Exact bundled presentation for a confirmed marker reward. Null quantity preserves an unrepresentable sum.</summary>
public sealed record LootTrackerMarkerItem(string Id, string Name, long? Quantity, string? Asset, LootItemRarity Rarity);

/// <summary>Local presentation data only. Markers never contribute waypoints or route distance.</summary>
public sealed record LootTrackerSessionMarker(string Id, LootTrackerSessionMarkerKind Kind,
    string MapId, string MapName, double X, double Y, string Name, ImmutableArray<DateTimeOffset> Times)
{
    public ImmutableArray<LootTrackerMarkerItem> Items { get; init; } = [];
    public string? OpponentClassId { get; init; }
    public int? OpponentAbyssRankGrade { get; init; }
    public string KindKey => Kind.ToString().ToLowerInvariant();
    public string Tooltip
    {
        get
        {
            var label = Kind is LootTrackerSessionMarkerKind.Odyle or LootTrackerSessionMarkerKind.Gathering
                ? $"{UiText.T(Name)} · {UiText.F("{0} Ernten", Times.Length)}"
                : UiText.T(Kind == LootTrackerSessionMarkerKind.Kill ? "Kill" : Kind == LootTrackerSessionMarkerKind.Death ? "Tod" : "Loot");
            var rewards = Items.IsDefaultOrEmpty ? "" : " · " + string.Join(", ", Items.Select(item =>
                item.Name + " ×" + (item.Quantity?.ToString("N0", CultureInfo.GetCultureInfo(UiText.Language == "de" ? "de-DE" : "en-US")) ?? "—")));
            return $"{label}{rewards} · {string.Join(", ", Times.Select(Time))}";
        }
    }
    private static string Time(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm:ss");
}

public static class LootTrackerSessionMarkers
{
    public static ImmutableArray<LootTrackerSessionMarker> All(LootTrackerSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var result = ImmutableArray.CreateBuilder<LootTrackerSessionMarker>();
        if (!session.CombatEvents.IsDefaultOrEmpty)
            foreach (var row in session.CombatEvents)
                if (row.Position is { } position && Valid(position.MapId, position.X, position.Y)
                    && row.Kind is LootTrackerCombatKind.Kill or LootTrackerCombatKind.Death)
                    result.Add(new(row.Id, row.Kind == LootTrackerCombatKind.Kill ? LootTrackerSessionMarkerKind.Kill
                        : LootTrackerSessionMarkerKind.Death, position.MapId, position.MapName,
                        position.X, position.Y, "", [row.At])
                    {
                        OpponentClassId = row.Kind == LootTrackerCombatKind.Kill ? LootTrackerTimelineFacts.ClassId(row.OpponentClassId) : null,
                        OpponentAbyssRankGrade = row.Kind == LootTrackerCombatKind.Kill && row.OpponentAbyssRankGrade is >= 1 and <= 19
                            ? row.OpponentAbyssRankGrade : null
                    });

        // The caller supplies the chronological replay prefix. Never match rewards to a nearby event,
        // route point, combat row or to the whole-session loot aggregate.
        var facts = (session.TimelineEvents.IsDefault ? [] : session.TimelineEvents)
            .Where(row => row.Kind is LootTrackerTimelineEventKind.HarvestSuccess or LootTrackerTimelineEventKind.Drop)
            .DistinctBy(row => row.Id, StringComparer.Ordinal).ToArray();
        var harvests = facts.Where(row => row.Kind == LootTrackerTimelineEventKind.HarvestSuccess
            && row.GatherVisit is { Succeeded: true } visit && Valid(visit.MapId, visit.X, visit.Y)).ToArray();
        var harvestsByVisit = harvests.ToLookup(row => row.GatherVisit!.Id, StringComparer.Ordinal);
        var visits = (session.GatherVisits.IsDefault ? [] : session.GatherVisits)
            .Concat(harvests.Select(row => row.GatherVisit!))
            .Where(row => row.Succeeded && Valid(row.MapId, row.X, row.Y))
            .DistinctBy(row => row.Id, StringComparer.Ordinal);
        // Only an explicitly resolved catalogue identity joins visits. Unresolved visits stay separate,
        // even when their resource type or coordinates happen to be identical.
        foreach (var group in visits.GroupBy(row => (row.MapId,
            Key: !string.IsNullOrWhiteSpace(row.CatalogNodeId) ? "catalog:" + row.CatalogNodeId : "visit:" + row.Id)))
        {
            var rows = group.OrderBy(row => row.At).ThenBy(row => row.Id, StringComparer.Ordinal).ToArray();
            var first = rows[0];
            var rewards = rows.SelectMany(row => harvestsByVisit[row.Id])
                .SelectMany(row => row.Loot.IsDefault ? [] : row.Loot).ToArray();
            var odyle = session.Kind == LootTrackerSessionKind.Abyss && (session.UnifiedVersion == 0
                || rewards.Any(loot => MonsterLootItemCatalog.TryItemId(loot.Id, out var itemId)
                    && itemId is 610610013 or 610610014 or 610610015 or 610610016));
            result.Add(new(first.Id, odyle ? LootTrackerSessionMarkerKind.Odyle : LootTrackerSessionMarkerKind.Gathering,
                first.MapId, first.MapName, first.X, first.Y, first.Name, rows.Select(row => row.At).ToImmutableArray())
                { Items = Items(rewards) });
        }

        var drops = facts.Where(row => row.Kind == LootTrackerTimelineEventKind.Drop)
            .Select(row => (Fact: row, Position: DropPosition(row)))
            .Where(row => row.Position is { } position && Valid(position.MapId, position.X, position.Y)
                && !row.Fact.Loot.IsDefaultOrEmpty && row.Fact.Loot.Any(loot => loot.Quantity > 0));
        // Equal recorded coordinates can share a display pin; their original event times and exact items remain distinct facts.
        foreach (var group in drops.GroupBy(row => (row.Position!.MapId, row.Position.X, row.Position.Y)))
        {
            var rows = group.OrderBy(row => row.Fact.At).ThenBy(row => row.Fact.Id, StringComparer.Ordinal).ToArray();
            var first = rows[0]; var position = first.Position!;
            result.Add(new(first.Fact.Id, LootTrackerSessionMarkerKind.Drop, position.MapId, position.MapName,
                position.X, position.Y, "Loot", rows.Select(row => row.Fact.At).ToImmutableArray())
                { Items = Items(rows.SelectMany(row => row.Fact.Loot)) });
        }
        return result.ToImmutable();
    }

    public static LootTrackerRouteSegment[] Maps(LootTrackerSession session) =>
        LootTrackerPresentation.DisplayedRouteSegments(session)
            .Concat(All(session).Select(row => new LootTrackerRouteSegment(row.MapId, row.MapName, [])))
            .Where(row => !string.IsNullOrWhiteSpace(row.MapId)).DistinctBy(row => row.MapId).ToArray();

    private static LootTrackerCombatPosition? DropPosition(LootTrackerTimelineEvent row) => row.Position
        ?? (row.GatherVisit is { Succeeded: true } visit && Valid(visit.MapId, visit.X, visit.Y)
            ? new(visit.MapId, visit.MapName, visit.X, visit.Y, visit.At) : null);

    private static ImmutableArray<LootTrackerMarkerItem> Items(IEnumerable<LootTrackerLoot> rewards) => rewards
        .Where(loot => loot.Quantity > 0 && !string.IsNullOrWhiteSpace(loot.Id))
        .GroupBy(loot => MonsterLootItemCatalog.TryItemId(loot.Id, out var id)
            ? "Item:" + id.ToString(CultureInfo.InvariantCulture) : loot.Id, StringComparer.Ordinal)
        .Select(group =>
        {
            long? quantity;
            try { quantity = group.Aggregate(0L, (sum, loot) => checked(sum + loot.Quantity)); }
            catch (OverflowException) { quantity = null; }
            return new LootTrackerMarkerItem(group.Key, group.Select(loot => loot.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
                ?? group.Key, quantity, MonsterLootIconCatalog.Default.DropAsset(group.Key), MonsterLootItemCatalog.Default.DropRarity(group.Key));
        }).OrderByDescending(item => item.Rarity).ThenBy(item => item.Quantity is null)
        .ThenByDescending(item => item.Quantity).ThenBy(item => item.Id, StringComparer.Ordinal).ToImmutableArray();

    private static bool Valid(string map, double x, double y) => !string.IsNullOrWhiteSpace(map)
        && double.IsFinite(x) && double.IsFinite(y) && x >= 0 && y >= 0;
}
