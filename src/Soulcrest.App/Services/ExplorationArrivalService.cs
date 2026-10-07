namespace Soulcrest.App.Services;

/// <summary>
/// On a fresh player position: completes the current exploration target, and removes marked targets
/// (right click / Shift + right click) the player has reached, like the progression target disappears
/// (user request 2026-10-05).
/// </summary>
public sealed class ExplorationArrivalService : IDisposable
{
    private readonly MapTrackingService _tracking;
    private readonly MapTargetsService _targets;
    private readonly ExplorationService _exploration;
    private readonly SettingsService _settings;
    private DateTimeOffset _lastPosition;
    // Targets the player has been seen away from: only these disappear on arrival, so a target set
    // while standing next to it does not vanish at once.
    private readonly HashSet<string> _armed = [];
    public ExplorationArrivalService(MapTrackingService tracking, MapTargetsService targets, ExplorationService exploration, SettingsService settings)
    {
        _tracking = tracking; _targets = targets; _exploration = exploration; _settings = settings;
        tracking.Changed += Check;
    }
    private void Check()
    {
        var position = _tracking.Position;
        if (!_tracking.Running || !_tracking.Found || position is null || position.At <= _lastPosition) return;
        _lastPosition = position.At;
        var character = _exploration.ActiveId;
        var id = Reached(_settings.Current, _targets.Progression, position, character, DateTimeOffset.UtcNow);
        if (id is not null) _exploration.SetDone(character, [id]);
        var kibelisks = ReachedKibelisks(_exploration.Places, position, _settings.Current.ExplorationCompletionRadius, DateTimeOffset.UtcNow);
        if (kibelisks.Count > 0) _exploration.SetDone(character, kibelisks); // already done ones change nothing
        foreach (var arrived in ArrivedTargets(_targets.Targets, position, _settings.Current.ExplorationCompletionRadius, _armed, DateTimeOffset.UtcNow))
            _targets.Arrive(arrived);
    }

    /// <summary>
    /// Marked targets on the player's map within the completion radius that were armed before (the
    /// player had been outside the radius). Updates <paramref name="armed"/>; 0 disables the removal.
    /// Reaching a stop of a sequence also removes the stops before it that were skipped (user decision
    /// 2026-10-05), first stop first, so a repeating route restarts only after its last stop.
    /// Pets marked by hand stay until the pet reaches its next level (<see cref="FarmedTargetCleaner"/>,
    /// user request 2026-10-06); only route stops disappear on arrival.
    /// </summary>
    internal static IReadOnlyList<string> ArrivedTargets(IReadOnlyList<MapTarget> targets, PlayerPosition position, int completionRadius,
        HashSet<string> armed, DateTimeOffset now)
    {
        armed.IntersectWith(targets.Select(t => t.Id));
        var radius = Math.Clamp(completionRadius, 0, 500);
        if (radius == 0 || position.At > now || now - position.At > TimeSpan.FromSeconds(2) || !double.IsFinite(position.X) || !double.IsFinite(position.Y))
            return [];
        var arrived = new List<string>();
        foreach (var target in targets)
        {
            if (StaysUntilNextLevel(target))
                continue;
            var inside = target.MapId == position.MapId
                && (position.X - target.X) * (position.X - target.X) + (position.Y - target.Y) * (position.Y - target.Y) <= radius * radius;
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

    /// <summary>
    /// Kibelisks within the completion radius of a fresh position: reaching one unlocks it for the active
    /// character (user request 2026-10-06). 0 turns this off like the other automatic completions.
    /// </summary>
    internal static IReadOnlyList<string> ReachedKibelisks(IReadOnlyList<ExplorationPlace> places, PlayerPosition position, int completionRadius, DateTimeOffset now)
    {
        var radius = Math.Clamp(completionRadius, 0, 500);
        if (radius == 0 || position.At > now || now - position.At > TimeSpan.FromSeconds(2) || !double.IsFinite(position.X) || !double.IsFinite(position.Y))
            return [];
        return places.Where(p => p.Kind == "kibelisk" && p.Map == position.MapId
                && (p.X - position.X) * (p.X - position.X) + (p.Y - position.Y) * (p.Y - position.Y) <= radius * radius)
            .Select(p => p.Id).Distinct().ToList();
    }

    /// <summary>A pet marked with a right click, outside a saved route.</summary>
    internal static bool StaysUntilNextLevel(MapTarget target) =>
        target.RouteId is null && (target.PetId is not null || target.Kind.StartsWith("Pets", StringComparison.Ordinal));

    private static MapTarget? Predecessor(IReadOnlyList<MapTarget> targets, MapTarget target) =>
        target.After is { } after ? targets.FirstOrDefault(t => t.Id == after) : null;
    internal static string? Reached(AppSettings settings, MapTarget? target, PlayerPosition position, string character, DateTimeOffset now)
    {
        if (!settings.ProgressionEnabled || settings.ProgressionMode is not ("dungeon" or "stronghold" or "exploration") ||
            settings.ExplorationCompletionRadius <= 0 || target?.ExplorationId is null || target.CharacterId != character ||
            target.MapId != position.MapId || position.At > now || now - position.At > TimeSpan.FromSeconds(2)) return null;
        if (settings.ProgressionMode == "dungeon" && target.Kind != "Sealed Dungeon" ||
            settings.ProgressionMode == "stronghold" && target.Kind != "Stronghold") return null;
        var radius = Math.Clamp(settings.ExplorationCompletionRadius, 0, 500);
        var dx = position.X - target.X; var dy = position.Y - target.Y;
        return double.IsFinite(dx) && double.IsFinite(dy) && dx * dx + dy * dy <= radius * radius ? target.ExplorationId : null;
    }
    public void Dispose() => _tracking.Changed -= Check;
}
