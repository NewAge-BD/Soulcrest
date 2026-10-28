using System.Collections.Immutable;
using Soulcrest.Core.Network;

namespace Soulcrest.App.Services;

public sealed partial class UnifiedLootTrackerService
{
    private (string Map, long MapGeneration, long? WorldGeneration, long? BindingGeneration)? _routeContext;
    private DateTimeOffset? _positionEvidenceAt, _routeRecordedAt;
    private long? _positionStamp;
    private int _gatherSection;
    private bool _sectionVisited, _locationResolved;

    private void BreakRoute()
    {
        if (_sectionVisited) { _gatherSection = checked(_gatherSection + 1); _sectionVisited = false; }
        _routeContext = null; _positionEvidenceAt = _routeRecordedAt = null; _positionStamp = null;
    }

    private static PlayerPosition? QualifiedPosition(RecorderPositionSnapshot? snapshot, OwnCharacterContext own,
        WorldContextSnapshot world, DateTimeOffset at)
    {
        if (snapshot is not { Position: { } position } captured || !QualifiedWorld(world, own, at)
            || string.IsNullOrWhiteSpace(position.MapId) || position.ContextGeneration != captured.MapGeneration
            || captured.BindingGeneration != own.Generation || captured.WorldGeneration != world.Generation
            || world.SoulcrestMapId is { Length: > 0 } map && map != position.MapId
            || position.At == default || position.At > at || at - position.At > TimeSpan.FromSeconds(2)
            || captured.DetectedAt == default || captured.DetectedAt > at || at - captured.DetectedAt > TimeSpan.FromSeconds(5)
            || !double.IsFinite(position.X) || !double.IsFinite(position.Y) || position.X < 0 || position.Y < 0) return null;
        return position;
    }

    private void RecordPosition(RecorderPositionSnapshot? snapshot, WorldContextSnapshot world, OwnCharacterContext own,
        DateTimeOffset at, long stamp, bool activityPosition)
    {
        if (_active is not { } active || !ReferenceEquals(CurrentOwn, own)
            || QualifiedPosition(snapshot, own, world, at) is not { } position)
        { BreakRoute(); return; }
        var mapSize = _resolver.MapSize(position.MapId) ?? 8192;
        if (position.X > mapSize || position.Y > mapSize) { BreakRoute(); return; }
        var captured = snapshot!;
        // User decision 2026-10-26: an open-world session is named after the nearest area label of its first
        // qualified loot position, frozen and marked as approximate. Abyss keeps its world name.
        if (activityPosition && !_locationResolved && active.Kind == LootTrackerSessionKind.Mixed
            && _locationName?.Invoke(captured) is { Name: { } location } match
            && !string.IsNullOrWhiteSpace(location) && location.Length <= 1024 && !location.Any(char.IsControl))
        {
            active = active with { Location = location.Trim(), LocationApproximate = match.Approximate };
            _active = active; _locationResolved = true;
        }
        var context = (position.MapId, captured.MapGeneration, captured.WorldGeneration, captured.BindingGeneration);
        if (_routeContext != context || _routeRecordedAt is { } previousAt && at - previousAt > TimeSpan.FromSeconds(2)) BreakRoute();
        if (_positionEvidenceAt is { } evidence && position.At <= evidence) return;
        if (!activityPosition && _positionStamp is { } previous && _time.GetElapsedTime(previous, stamp) < TimeSpan.FromSeconds(1)) return;
        var segments = active.RouteSegments.ToBuilder();
        var point = new LootTrackerRoutePoint(position.X, position.Y, at, Elapsed(stamp));
        if (_routeContext is null || segments.Count == 0) segments.Add(new(position.MapId, _mapName(position.MapId), [point]));
        else segments[^1] = segments[^1] with { Points = segments[^1].Points.Add(point) };
        _active = active with { RouteSegments = segments.ToImmutable() };
        _routeContext = context; _positionEvidenceAt = position.At; _routeRecordedAt = at; _positionStamp = stamp;
    }

    private LootTrackerGatherVisit? GatherVisit(Activity activity, bool succeeded)
    {
        if (QualifiedPosition(activity.Position, activity.Own, activity.World, activity.At) is not { } position) return null;
        var visit = _resolver.Resolve(Guid.NewGuid().ToString("N"), _gatherSection, position, activity.GatherableId,
            activity.At, succeeded, _mapName(position.MapId), UiText.T("Sammelstelle"));
        if (visit.OriginalX > visit.MapSize || visit.OriginalY > visit.MapSize || visit.X > visit.MapSize || visit.Y > visit.MapSize)
        { BreakRoute(); return null; }
        _sectionVisited = true;
        return visit;
    }

    private static Func<RecorderPositionSnapshot, FarmLocationMatch?> CreateLocationResolver(SettingsService settings, ProgressService progress)
    {
        var catalog = FarmLocationCatalog.Load(progress.MapDataDirectory);
        return position => catalog.Resolve(position, settings.Current.LootNameLanguage);
    }

    private static LifeskillGatherPointResolver CreateResolver(ProgressService progress)
    {
        var maps = LootTrackerRouteMapPreview.Load(progress.MapDataDirectory);
        return new(ResourceNodeCatalog.Load(progress.MapDataDirectory),
            id => progress.Maps.FirstOrDefault(map => map.Id == id)?.MetersPerPixel,
            id => maps.TryGetValue(id, out var map) ? map.Size : null);
    }
}
