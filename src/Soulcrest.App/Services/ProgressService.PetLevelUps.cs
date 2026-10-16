namespace Soulcrest.App.Services;

/// <summary>A confirmed live result for the pet overlay; not a pickup or a persisted progress event.</summary>
internal sealed record PetLevelUp(string PetId, int Level, bool IsMax);

public sealed partial class ProgressService
{
    // User request 2026-10-16: animate confirmed live pet upgrades only. A monotone floor
    // survives source resets in this process so acquisition/result confirmations cannot replay it.
    private readonly Dictionary<int, int> _petLevelUpFloors = [];

    internal event Action<PetLevelUp>? PetLeveled;

    private void InitializePetLevelUpFloors()
    {
        foreach (var (number, raw) in _profile.NetworkPets)
            if (raw.LevelObserved) RememberPetLevelFloor(number, raw.Level);
    }

    // A baseline is silent even if it corrects a saved value. Previously observed higher
    // levels remain covered; a source/character change is never a reason to celebrate again.
    private void RememberPetLevelFloor(int number, int level) =>
        _petLevelUpFloors[number] = Math.Max(_petLevelUpFloors.GetValueOrDefault(number),
            Math.Clamp(level, 0, Thresholds.MaxLevel));

    /// <summary>Called under the progress lock after an accepted explicit acquisition/upgrade.</summary>
    private PetLevelUp? CapturePetLevelUp(int number, int level, int? previousObservedLevel)
    {
        var floor = Math.Max(_petLevelUpFloors.GetValueOrDefault(number), previousObservedLevel ?? 0);
        RememberPetLevelFloor(number, Math.Max(floor, level));
        if (level <= floor || level <= 0 || ResolveNetworkPet(number) is not { } id ||
            EntrySnapshot(id).ManualOverride) return null;
        return new(CanonicalId(id), level, level >= Thresholds.MaxLevel);
    }

    private void PublishPetLevelUp(PetLevelUp? levelUp)
    {
        // Publish outside the lock, before Changed can advance focus or remove a farmed target.
        if (levelUp is not null) SafeEvent.Raise(PetLeveled, levelUp, "Pet-Aufstieg");
    }
}
