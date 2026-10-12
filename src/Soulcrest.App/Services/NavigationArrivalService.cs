namespace Soulcrest.App.Services;

/// <summary>
/// Advances ordinary navigation and leveling route stops when the player reaches them.
/// </summary>
public sealed class NavigationArrivalService : IDisposable
{
    private readonly MapTrackingService _tracking;
    private readonly MapTargetsService _targets;
    private readonly SettingsService _settings;
    private DateTimeOffset _lastPosition;
    // Ordinary targets the player has been seen away from: only these disappear on arrival, so a target set
    // while standing next to it does not vanish at once.
    private readonly HashSet<string> _armed = [];
    public NavigationArrivalService(MapTrackingService tracking, MapTargetsService targets, SettingsService settings)
    {
        _tracking = tracking; _targets = targets; _settings = settings;
        tracking.Changed += Check;
    }
    private void Check()
    {
        var position = _tracking.Position;
        if (!_tracking.Running || !_tracking.Found || position is null || position.At <= _lastPosition) return;
        _lastPosition = position.At;
        foreach (var arrived in ArrivedTargets(_targets.Targets, position, _settings.Current.TargetArrivalRadius, _armed, DateTimeOffset.UtcNow))
            _targets.Arrive(arrived);
    }

    /// <summary>
    /// Marked targets on the player's map within the arrival radius that were armed before (the
    /// player had been outside the radius). Leveling routes complete only their current stop,
    /// without requiring a departure before arrival. Updates <paramref name="armed"/>; 0 disables the removal.
    /// Reaching a stop of a sequence also removes the stops before it that were skipped (user decision
    /// 2026-10-05), first stop first, so a repeating route restarts only after its last stop.
    /// Pets marked by hand stay until the pet reaches its next level (<see cref="FarmedTargetCleaner"/>,
    /// user request 2026-10-06); only route stops disappear on arrival.
    /// </summary>
    internal static IReadOnlyList<string> ArrivedTargets(IReadOnlyList<MapTarget> targets, PlayerPosition position, int arrivalRadius,
        HashSet<string> armed, DateTimeOffset now)
    {
        armed.IntersectWith(targets.Select(t => t.Id));
        var radius = Math.Clamp(arrivalRadius, 0, 500);
        if (radius == 0 || position.At > now || now - position.At > TimeSpan.FromSeconds(2) || !double.IsFinite(position.X) || !double.IsFinite(position.Y))
            return [];
        var arrived = new List<string>();
        var levelingHeads = targets.Where(t => t.Leveling && !t.Completed).GroupBy(t => t.RouteId)
            .Select(g => g.First().Id).ToHashSet();
        foreach (var target in targets)
        {
            if (target.Completed || StaysUntilNextLevel(target) || target.Leveling && !levelingHeads.Contains(target.Id))
                continue;
            var inside = target.MapId == position.MapId
                && (position.X - target.X) * (position.X - target.X) + (position.Y - target.Y) * (position.Y - target.Y) <= radius * radius;
            if (target.Leveling)
            {
                // One current stop per route and fresh position; close NPC visits need no exit/re-entry.
                armed.Remove(target.Id);
                if (inside) arrived.Add(target.Id);
                continue;
            }
            if (!inside)
                armed.Add(target.Id);
            else if (armed.Remove(target.Id))
            {
                var skipped = new List<string>();
                var seen = new HashSet<string> { target.Id };
                for (var before = Predecessor(targets, target); before is not null && seen.Add(before.Id); before = Predecessor(targets, before))
                {
                    if (!StaysUntilNextLevel(before))
                        skipped.Insert(0, before.Id);
                }
                foreach (var id in skipped.Append(target.Id))
                {
                    if (!arrived.Contains(id))
                        arrived.Add(id);
                }
            }
        }
        return arrived;
    }

    /// <summary>A pet marked with a right click, outside a saved route.</summary>
    internal static bool StaysUntilNextLevel(MapTarget target) =>
        target.RouteId is null && (target.PetId is not null || target.Kind.StartsWith("Pets", StringComparison.Ordinal));

    private static MapTarget? Predecessor(IReadOnlyList<MapTarget> targets, MapTarget target) =>
        target.After is { } after ? targets.FirstOrDefault(t => t.Id == after) : null;
    public void Dispose() => _tracking.Changed -= Check;
}
