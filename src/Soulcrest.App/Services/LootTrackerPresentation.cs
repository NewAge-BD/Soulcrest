using System.Collections.Immutable;

namespace Soulcrest.App.Services;

public enum LootTrackerSessionKind { Grind, Dungeon, Lifeskill }
public enum LootTrackerSessionStatus { Live, Completed, Interrupted }
public enum LootTrackerSessionMode { Normal, LimitedKina }
public enum LootTrackerLootKind { Item, Soul }

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
    public bool CoverageLimited { get; init; }
    public ImmutableArray<LootTrackerLoot> Loot { get; init; } = [];
    public ImmutableArray<LootTrackerMob> Mobs { get; init; } = [];
    public ImmutableArray<LootTrackerRouteSegment> RouteSegments { get; init; } = [];
    public ImmutableArray<LootTrackerResourceVisit> ResourceNodes { get; init; } = [];
    /// <summary>Version 1 is a growing gathering-point route; zero retains the legacy travelled path.</summary>
    public int FarmRouteVersion { get; init; }
    public ImmutableArray<LootTrackerGatherVisit> GatherVisits { get; init; } = [];
    public ImmutableArray<LootTrackerFarmRoutePoint> FarmRoutePoints { get; init; } = [];
    public ImmutableArray<LootTrackerIncomeBin> MinuteIncome { get; init; } = [];
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

/// <summary>
/// A source-confirmed resource-node visit in encounter order, bound to an existing route point.
/// X/Y are catalogue-node coordinates; the referenced player position may differ.
/// The source must qualify a fresh position and an unambiguous catalogue node; this read model
/// does not infer visits, harvesting success, or resources from proximity. Repeated visits are valid.
/// </summary>
public sealed record LootTrackerResourceVisit(string NodeId, string Name, string MapId, string MapName,
    double X, double Y, int SegmentIndex, int PointIndex, DateTimeOffset? At = null);

/// <summary>An observed elapsed-minute bin; missing minutes and null values are not zero income.</summary>
public sealed record LootTrackerIncomeBin(int Minute, long? KinaBound, long? KinaUnbound);

/// <summary>Pure UI projection; no source, session engine, persistence, or timer is activated here.</summary>
public static class LootTrackerPresentation
{
    public const int DefaultIdleTimeoutMinutes = 2;
    public const int MinIdleTimeoutMinutes = 1;
    public const int MaxIdleTimeoutMinutes = 60;

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

    private static string? ProfileId(string? value) => Guid.TryParse(value, out var id) ? id.ToString("N") : null;

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
