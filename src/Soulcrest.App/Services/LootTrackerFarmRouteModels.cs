namespace Soulcrest.App.Services;

/// <summary>
/// One confirmed own gathering attempt with a fresh visual position. A nearby matching catalogue
/// node may replace that position (user request 2026-10-25). Native object keys are never stored.
/// The original and chosen coordinates are frozen together for deterministic recovery.
/// </summary>
public sealed record LootTrackerGatherVisit(string Id, int Section, string MapId, string MapName,
    double OriginalX, double OriginalY, double X, double Y, DateTimeOffset At, bool Succeeded,
    string Name, int MapSize = 8192, int? GatherableId = null, string? CatalogNodeId = null);

/// <summary>One distinct stop in the growing route; repeated successes and failures retain visits.</summary>
public sealed record LootTrackerFarmRoutePoint(string Id, int Section, string MapId, string MapName,
    double X, double Y, string Name, int VisitCount, int SuccessCount, int FailureCount,
    string? CatalogNodeId = null, int? GatherableId = null);
