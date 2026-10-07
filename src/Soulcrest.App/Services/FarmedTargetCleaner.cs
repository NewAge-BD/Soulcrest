namespace Soulcrest.App.Services;

/// <summary>
/// Removes the mark of a pet target once the pet was farmed into its next level (user request
/// 2026-10-06: a pet marked by hand stays until then, arriving at it does not remove it). Stops of a
/// saved route go on arrival instead and only vanish here when the map hides the new level (user report
/// 2026-10-04). Only a level reached by farming counts: switching a filter on keeps the marks.
/// </summary>
public sealed class FarmedTargetCleaner : IDisposable
{
    private readonly MapTargetsService _targets;
    private readonly ProgressService _progress;
    private readonly SettingsService _settings;
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _levels = new(StringComparer.Ordinal);

    public FarmedTargetCleaner(MapTargetsService targets, ProgressService progress, SettingsService settings)
    {
        _targets = targets;
        _progress = progress;
        _settings = settings;
        Remember();
        _progress.Changed += Check;
        _targets.Changed += Remember;
    }

    /// <summary>The pet a target belongs to: saved with the mark, or (older marks) found by its name.</summary>
    private string? PetOf(MapTarget target) =>
        target.PetId ?? (target.Kind.StartsWith("Pets", StringComparison.Ordinal) ? _progress.FindByName(target.Name)?.Id : null);

    private int LevelOf(string petId) => _progress.Thresholds.Level(_progress.Souls(petId));

    /// <summary>Levels of new marks, so the next pickup can be compared with them.</summary>
    private void Remember()
    {
        lock (_gate)
        {
            foreach (var target in _targets.Targets)
            {
                if (!_levels.ContainsKey(target.Id) && PetOf(target) is { } pet)
                    _levels[target.Id] = LevelOf(pet);
            }
        }
    }

    private void Check()
    {
        var finished = new List<string>();
        lock (_gate)
        {
            foreach (var target in _targets.Targets)
            {
                if (PetOf(target) is not { } pet)
                    continue;
                var level = LevelOf(pet);
                if (_levels.TryGetValue(target.Id, out var before) && level > before
                    && (ExplorationArrivalService.StaysUntilNextLevel(target) || _settings.Current.HidesLevel(level)))
                    finished.Add(target.Id);
                _levels[target.Id] = level;
            }
        }
        foreach (var id in finished)
            _targets.Remove(id);
    }

    public void Dispose()
    {
        _progress.Changed -= Check;
        _targets.Changed -= Remember;
    }
}
