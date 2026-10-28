using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Soulcrest.App.Services;

public enum LootTrackerSessionKind { Grind, Dungeon, Lifeskill, Abyss, Mixed = 4 }
public enum LootTrackerActivityOrigin { Pve, Lifeskill, Abyss }
public enum LootTrackerViewMode { Mixed, Pve, Lifeskill, Abyss }
public enum LootTrackerSessionStatus { Live, Completed, Interrupted }
public enum LootTrackerSessionMode { Normal, LimitedKina }
public enum LootTrackerLootKind { Item, Soul }
public enum LootTrackerCombatKind { Kill = 0, Death = 1 }

/// <summary>
/// Read-only presentation contract. It neither starts a capture nor creates sessions from pet totals.
/// Empty means that no loot-tracker session has been supplied, not a confirmed zero-income session.
/// </summary>
public sealed record LootTrackerSnapshot(ImmutableArray<LootTrackerSession> Sessions, DateTimeOffset? At)
{
    public static LootTrackerSnapshot Empty { get; } = new([], null);
}

/// <summary>
/// Frozen character values at session start. ProfileId is the existing local exploration profile
/// GUID, never a network character/server/entity identifier. Missing values stay unknown.
/// </summary>
public sealed record LootTrackerCharacterSnapshot(string? ProfileId, string? Name,
    long? CombatScore = null, int? GearScore = null);

/// <summary>
/// Observed session values supplied by a future tracking source. Unknown balances and combat score
/// stay null; route segments must be separated at position gaps and map boundaries by that source.
/// </summary>
public sealed record LootTrackerSession
{
    public string Id { get; init; } = "";
    public LootTrackerSessionKind Kind { get; init; }
    /// <summary>One records a single shared session with explicit fact origins; zero preserves legacy sessions.</summary>
    public int UnifiedVersion { get; init; }
    [JsonIgnore] public LootTrackerViewMode? ProjectionViewMode { get; init; }
    [JsonIgnore] public bool ProjectionLimited { get; init; }
    public LootTrackerSessionStatus Status { get; init; }
    /// <summary>Frozen at start; old journals without the field retain Normal.</summary>
    public LootTrackerSessionMode Mode { get; init; }
    /// <summary>The 07:00 UTC farm day; legacy sessions derive this from StartedAt.</summary>
    public DateOnly? FarmDay { get; init; }
    public string Location { get; init; } = "";
    /// <summary>Nearest named map label rather than proven area containment.</summary>
    public bool LocationApproximate { get; init; }
    public string? DungeonName { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public DateTimeOffset? LastLootAt { get; init; }
    /// <summary>Abyss residence heartbeat; recovery must not discard a quiet observed stay.</summary>
    public DateTimeOffset? LastPresenceAt { get; init; }
    public TimeSpan Elapsed { get; init; }
    public LootTrackerCharacterSnapshot? Character { get; init; }
    /// <summary>Legacy presentation value, used only when no character start snapshot exists.</summary>
    public long? CombatScore { get; init; }
    public long? KinaBound { get; init; }
    public long? KinaUnbound { get; init; }
    public long? MonsterCount { get; init; }
    public long? ItemCount { get; init; }
    public long? HarvestCount { get; init; }
    public long? FailedHarvestCount { get; init; }
    public long? SoulCount { get; init; }
    /// <summary>Canonical earned AP, separate from the spendable wallet and cumulative cap usage.</summary>
    public long? AbyssPoints { get; init; }
    public long? RankPointsGained { get; init; }
    public long? RankPointsLost { get; init; }
    public bool CoverageLimited { get; init; }
    public ImmutableArray<LootTrackerLoot> Loot { get; init; } = [];
    public ImmutableArray<LootTrackerMob> Mobs { get; init; } = [];
    public ImmutableArray<LootTrackerRouteSegment> RouteSegments { get; init; } = [];
    /// <summary>Explicit own combat results only. Missing historical observations are never reconstructed.</summary>
    public ImmutableArray<LootTrackerCombatEvent> CombatEvents { get; init; } = [];
    public ImmutableArray<LootTrackerResourceVisit> ResourceNodes { get; init; } = [];
    /// <summary>Version 1 is a growing gathering-point route; zero retains the legacy travelled path.</summary>
    public int FarmRouteVersion { get; init; }
    public ImmutableArray<LootTrackerGatherVisit> GatherVisits { get; init; } = [];
    public ImmutableArray<LootTrackerFarmRoutePoint> FarmRoutePoints { get; init; } = [];
    public ImmutableArray<LootTrackerIncomeBin> MinuteIncome { get; init; } = [];
    /// <summary>One records exact local timeline facts; zero preserves the aggregate legacy contract.</summary>
    public int TimelineVersion { get; init; }
    public ImmutableArray<LootTrackerTimelineEvent> TimelineEvents { get; init; } = [];
}

/// <summary>Actual observed session loot. A null MobId explicitly leaves the drop source unassigned.</summary>
public sealed record LootTrackerLoot(string Id, string Name, LootTrackerLootKind Kind, long Quantity,
    string? MobId = null)
{
}

/// <summary>Only source-confirmed own monster counts may populate this row.</summary>
public sealed record LootTrackerMob(string Id, string Name, long Count);

/// <summary>An empty point list preserves a known map without inventing a travelled path.</summary>
public sealed record LootTrackerRouteSegment(string MapId, string MapName,
    ImmutableArray<LootTrackerRoutePoint> Points);

public sealed record LootTrackerRoutePoint(double X, double Y, DateTimeOffset? At = null, TimeSpan? Elapsed = null);

/// <summary>A fresh visual position on a canonical local map, never a native entity/object identity.</summary>
public sealed record LootTrackerCombatPosition(string MapId, string MapName, double X, double Y,
    DateTimeOffset ObservedAt);

/// <summary>A persistent own result without opponent names or native keys. An absent position stays unplaced.</summary>
public sealed record LootTrackerCombatEvent(string Id, LootTrackerCombatKind Kind, DateTimeOffset At,
    TimeSpan Elapsed, LootTrackerCombatPosition? Position = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LootTrackerActivityOrigin? Origin { get; init; }
    /// <summary>Anonymous, validated class frozen at the outcome; never an opponent identity.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OpponentClassId { get; init; }
    /// <summary>Explicit actual opponent grade, never a stage inferred from own rank points.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? OpponentAbyssRankGrade { get; init; }
}

/// <summary>
/// A source-confirmed resource-node visit in encounter order, bound to an existing route point.
/// X/Y are catalogue-node coordinates; the referenced player position may differ.
/// The source must qualify a fresh position and an unambiguous catalogue node; this read model
/// does not infer visits, harvesting success, or resources from proximity. Repeated visits are valid.
/// </summary>
public sealed record LootTrackerResourceVisit(string NodeId, string Name, string MapId, string MapName,
    double X, double Y, int SegmentIndex, int PointIndex, DateTimeOffset? At = null);

/// <summary>An observed elapsed-minute bin; missing minutes and null values are not zero income.</summary>
public sealed record LootTrackerIncomeBin(int Minute, long? KinaBound, long? KinaUnbound)
{
    public long? AbyssPoints { get; init; }
    public long? RankPointsGained { get; init; }
    public long? RankPointsLost { get; init; }
}

/// <summary>Pure UI projection; no source, session engine, persistence, or timer is activated here.</summary>
public static class LootTrackerPresentation
{
    public const int DefaultIdleTimeoutMinutes = 2;
    public const int MinIdleTimeoutMinutes = 1;
    public const int MaxIdleTimeoutMinutes = 60;

    /// <summary>Display order only; exact item identities, quantities and stored encounter order stay unchanged.</summary>
    public static ImmutableArray<LootTrackerLoot> OrderedLoot(LootTrackerSession session) => session.Loot.IsDefaultOrEmpty ? []
        : OrderDrops(session.Loot, row => MonsterLootItemCatalog.Default.DropRarity(row.Id),
            row => row.Quantity, row => row.Name, row => row.Id).ToImmutableArray();

    // The existing item-grade order applies to both detail rows and the overlay's grouped rows.
    internal static IOrderedEnumerable<T> OrderDrops<T>(IEnumerable<T> rows, Func<T, LootItemRarity> rarity,
        Func<T, long> quantity, Func<T, string> name, Func<T, string> id) => rows
        .OrderByDescending(row => { var grade = rarity(row); return Enum.IsDefined(grade) ? (int)grade : (int)LootItemRarity.Unknown; })
        .ThenByDescending(quantity)
        .ThenBy(name, StringComparer.Ordinal)
        .ThenBy(id, StringComparer.Ordinal);

    public static bool UsesFarmRoute(LootTrackerSession session) =>
        session.Kind == LootTrackerSessionKind.Lifeskill && session.FarmRouteVersion == 1;

    /// <summary>The same ordered coordinates feed both the preview and exported map routes.</summary>
    public static ImmutableArray<LootTrackerRouteSegment> DisplayedRouteSegments(LootTrackerSession session)
    {
        if (!UsesFarmRoute(session)) return session.RouteSegments.IsDefault ? [] : session.RouteSegments;
        return FarmRouteSections(session).Select(section => new LootTrackerRouteSegment(section[0].MapId,
            section[0].MapName, section.Select(point => new LootTrackerRoutePoint(point.X, point.Y)).ToImmutableArray())).ToImmutableArray();
    }

    /// <summary>Preserves every distinct stop, including different catalogue nodes sharing a position.</summary>
    public static ImmutableArray<ImmutableArray<LootTrackerFarmRoutePoint>> FarmRouteSections(LootTrackerSession session)
    {
        if (!UsesFarmRoute(session)) return [];
        if (session.FarmRoutePoints.IsDefaultOrEmpty) return [];
        var result = ImmutableArray.CreateBuilder<ImmutableArray<LootTrackerFarmRoutePoint>>();
        var points = ImmutableArray.CreateBuilder<LootTrackerFarmRoutePoint>();
        (int Section, string Map)? current = null;
        foreach (var point in session.FarmRoutePoints)
        {
            if (string.IsNullOrWhiteSpace(point.MapId) || point.Section < 0
                || !double.IsFinite(point.X) || !double.IsFinite(point.Y) || point.X < 0 || point.Y < 0)
            { Flush(); current = null; continue; }
            var key = (point.Section, point.MapId);
            if (current != key) { Flush(); current = key; }
            points.Add(point);
        }
        Flush();
        return result.ToImmutable();

        void Flush()
        {
            if (points.Count > 0) result.Add(points.ToImmutable());
            points.Clear();
        }
    }

    public static int NormalizeIdleTimeoutMinutes(int value) =>
        Math.Clamp(value, MinIdleTimeoutMinutes, MaxIdleTimeoutMinutes);

    /// <summary>
    /// Copies a local profile and separately observed own scores into immutable session metadata.
    /// Subsequent profile renames, character switches and equipment changes do not update this value.
    /// </summary>
    public static LootTrackerCharacterSnapshot CaptureCharacter(CharacterExploration character,
        long? combatScore = null, int? gearScore = null)
    {
        ArgumentNullException.ThrowIfNull(character);
        return new(ProfileId(character.Id), NonEmpty(character.Name),
            combatScore is >= 0 ? combatScore : null, gearScore is >= 0 ? gearScore : null);
    }

    public static string? CharacterName(LootTrackerSession session) => NonEmpty(session.Character?.Name);

    public static string? CharacterProfileId(LootTrackerSession session) => ProfileId(session.Character?.ProfileId);

    /// <summary>A present character snapshot owns its unknown values; legacy scores cannot fill them.</summary>
    public static long? CombatScore(LootTrackerSession session)
    {
        var score = session.Character is { } character ? character.CombatScore : session.CombatScore;
        return score is >= 0 ? score : null;
    }

    public static int? GearScore(LootTrackerSession session) => session.Character?.GearScore is >= 0 and var score
        ? score : null;

    public static string? SessionName(LootTrackerSession session)
    {
        if (session.Kind == LootTrackerSessionKind.Dungeon)
            return NonEmpty(session.DungeonName) ?? NonEmpty(session.Location);
        if (session.Kind == LootTrackerSessionKind.Lifeskill)
            return NonEmpty(session.Location);

        var location = NonEmpty(session.Location);
        var mob = session.Mobs.IsDefaultOrEmpty ? null : session.Mobs
            .Where(row => row.Count > 0 && !string.IsNullOrWhiteSpace(row.Name))
            .OrderByDescending(row => row.Count)
            .ThenBy(row => row.Name, StringComparer.Ordinal)
            .Select(row => row.Name.Trim())
            .FirstOrDefault();
        return (location, mob) switch
        {
            (not null, not null) => location,
            (not null, null) => location,
            (null, not null) => mob,
            _ => null,
        };
    }

    /// <summary>
    /// Keeps source-supplied visit order and rejects dangling references, map mismatches and
    /// invalid node/player coordinates. This structural check cannot establish source confirmation
    /// or node association itself and applies no distance heuristic.
    /// </summary>
    public static IReadOnlyList<LootTrackerResourceVisit> GetValidatedResourceVisits(LootTrackerSession session)
    {
        if (session.ResourceNodes.IsDefaultOrEmpty || session.RouteSegments.IsDefaultOrEmpty) return [];
        var visits = ImmutableArray.CreateBuilder<LootTrackerResourceVisit>();
        foreach (var visit in session.ResourceNodes)
        {
            if (string.IsNullOrWhiteSpace(visit.NodeId) || string.IsNullOrWhiteSpace(visit.MapId)
                || !double.IsFinite(visit.X) || !double.IsFinite(visit.Y) || visit.X < 0 || visit.Y < 0
                || visit.SegmentIndex < 0 || visit.SegmentIndex >= session.RouteSegments.Length) continue;
            var segment = session.RouteSegments[visit.SegmentIndex];
            if (!string.Equals(segment.MapId, visit.MapId, StringComparison.Ordinal)
                || segment.Points.IsDefaultOrEmpty || visit.PointIndex < 0 || visit.PointIndex >= segment.Points.Length) continue;
            var point = segment.Points[visit.PointIndex];
            if (double.IsFinite(point.X) && double.IsFinite(point.Y) && point.X >= 0 && point.Y >= 0)
                visits.Add(visit);
        }
        return visits.ToImmutable();
    }

    /// <summary>Both components must be known. Overflow and invalid negative income stay unknown.</summary>
    public static long? TotalKina(LootTrackerSession session)
    {
        if (session.KinaBound is not { } bound || session.KinaUnbound is not { } unbound
            || bound < 0 || unbound < 0 || bound > long.MaxValue - unbound)
            return null;
        return bound + unbound;
    }

    public static double? PerHour(long? amount, TimeSpan elapsed) =>
        amount is >= 0 && elapsed > TimeSpan.Zero ? amount.Value / elapsed.TotalHours : null;

    public static double? PerMinute(long? amount, TimeSpan elapsed) =>
        amount is >= 0 && elapsed > TimeSpan.Zero ? amount.Value / elapsed.TotalMinutes : null;

    public static long? NetRankPoints(LootTrackerSession session) =>
        session.RankPointsGained is >= 0 and var gained && session.RankPointsLost is >= 0 and var lost
            ? gained - lost : null;

    private static string? ProfileId(string? value) => Guid.TryParse(value, out var id) ? id.ToString("N") : null;

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
